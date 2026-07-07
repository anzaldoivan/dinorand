using System;
using System.Buffers.Binary;
using System.IO;
using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// THROWAWAY probe (unstaged): stage the mechanism-(d) char-swap proof — inject an SCD
/// <c>push -1; op 0x29</c> cave into ST001's init routine so the Regina&lt;-&gt;Dylan switch runs on
/// room load (docs/dc2/DC2-WEAPON-SYSTEM-DECODE.md §4b). User-authorized 2026-07-03.
///
/// Mechanics (all offsets verified against the live pristine blob before writing):
///   blob 9,800 B; slot-5 section 0x1204; slot5base(=section+0x1C, the VM's [scene+0xA54] after
///   0x48BCD0's +0x1C) = 0x1220; init table entry u32@0x1220 = 0x56 (init abs 0x1276).
///   Cave appended at blob end 0x2648 (4-aligned; idx from slot5base = 0x1428/4 = 0x50A):
///     +0  u32 0x56              scratch table entry = original init offset
///     +4  05 00 FF FF           push -1            (byte template = ST001's own trigger @0x2518)
///     +8  29 00                 equip/char-switch  (handler 0x4892F0; IP += 2 at 0x489543)
///     +A  00 00                 nop                (op 0x00 handler 0x489B00 = IP += 2)
///     +C  05 00 0A 05           push 0x050A        (cave idx for op 0x19)
///     +10 19 00                 thread-restart: IP = slot5base + [slot5base+0x50A*4] = orig init
///   Init entry u32@0x1220 repointed 0x56 -&gt; 0x142C (cave code at 0x264C).
///   Known quirk: every re-construction of the init entity (room re-entry) toggles again.
///
/// Backup: creates <c>ST001.DAT.dinorand-bak</c> if absent; NEVER overwrites an existing backup.
/// Idempotent: skips when the init entry already reads 0x142C. Repack mirrors the shipped
/// Dc2ScdBlob.RepackWithBlob logic on the public GianPackage/Lzss API.
/// </summary>
public class _ZZ_ApplyDc2St001CharSwap
{
    private const string RoomPath = @"C:\Games\dinorand\4249140_DinoCrisis2\rebirth\Data\ST001.DAT";
    private const string BakPath = RoomPath + ".dinorand-bak";

    private const int SectionOff = 0x1204;
    private const int OpBase = SectionOff + 0x1C;          // 0x1220, the VM slot5base
    private const uint OrigInitOff = 0x56;                 // -> init routine at 0x1276
    private const int PristineBlobLen = 0x2648;            // 9,800 B; cave lands here
    private const int CaveOff = PristineBlobLen;           // 0x2648, 4-aligned from OpBase
    private const uint CaveInitOff = CaveOff + 4 - OpBase; // 0x142C: init entry -> cave code

    [Fact]
    public void Inject_op29_char_swap_into_ST001_init()
    {
        if (!File.Exists(RoomPath)) return; // gated: no install at the Windows path on this machine
        var package = File.ReadAllBytes(RoomPath);
        var pkg = GianPackage.TryParse(package);
        Assert.NotNull(pkg);

        int blobIdx = -1;
        for (int i = 0; i < pkg!.Entries.Count; i++)
            if (pkg.Entries[i].Type == GianEntryType.Lzss0) blobIdx = i;
        Assert.True(blobIdx >= 0, "no LZSS0 SCD blob entry");
        var entry = pkg.Entries[blobIdx];
        var blob = Lzss.Decompress(package.AsSpan(entry.PayloadOffset, (int)entry.DeclaredSize));

        uint initEntry = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(OpBase, 4));
        if (initEntry == CaveInitOff)
        {
            Console.WriteLine("ALREADY INJECTED — nothing to do.");
            return;
        }

        // pristine fingerprint, byte-exact, before any write
        Assert.Equal(PristineBlobLen, blob.Length);
        Assert.Equal(OrigInitOff, initEntry);
        uint dir5 = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x14, 4));
        Assert.Equal(0x005E0000u + SectionOff, dir5);
        // the room's own story trigger at 0x2518 carries the exact byte template we clone
        Assert.Equal("0500ffff2900", Convert.ToHexString(blob.AsSpan(0x251A, 6).ToArray()).ToLowerInvariant());

        var newBlob = new byte[blob.Length + 0x14];
        blob.CopyTo(newBlob, 0);
        var cave = newBlob.AsSpan(CaveOff);
        BinaryPrimitives.WriteUInt32LittleEndian(cave, OrigInitOff);            // +0 scratch entry
        new byte[] { 0x05, 0x00, 0xFF, 0xFF }.CopyTo(cave.Slice(4));            // +4 push -1
        new byte[] { 0x29, 0x00 }.CopyTo(cave.Slice(8));                        // +8 char-switch
        new byte[] { 0x05, 0x00, 0x0A, 0x05 }.CopyTo(cave.Slice(0xC));          // +C push 0x050A
        new byte[] { 0x19, 0x00 }.CopyTo(cave.Slice(0x10));                     // +10 restart->init
        BinaryPrimitives.WriteUInt32LittleEndian(newBlob.AsSpan(OpBase, 4), CaveInitOff);

        // self-check the op-0x19 arithmetic against the VM's own formula
        int idx = (CaveOff - OpBase) / 4;
        Assert.Equal(0x50A, idx);
        Assert.Equal(CaveOff, OpBase + idx * 4);
        uint restartTarget = BinaryPrimitives.ReadUInt32LittleEndian(newBlob.AsSpan(OpBase + idx * 4, 4));
        Assert.Equal(OrigInitOff, restartTarget);

        // repack (mirror of Dc2ScdBlob.RepackWithBlob on the public API)
        byte[] newPayload = Lzss.Compress(newBlob);
        int total = GianPackage.HeaderSize;
        for (int i = 0; i < pkg.Entries.Count; i++)
            total += Align(i == blobIdx ? newPayload.Length : (int)pkg.Entries[i].DeclaredSize);
        var result = new byte[total];
        package.AsSpan(0, GianPackage.HeaderSize).CopyTo(result);
        int pos = GianPackage.HeaderSize;
        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            var e = pkg.Entries[i];
            ReadOnlySpan<byte> src = i == blobIdx
                ? newPayload
                : package.AsSpan(e.PayloadOffset, (int)e.DeclaredSize);
            src.CopyTo(result.AsSpan(pos));
            if (i == blobIdx)
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * pkg.EntrySize + 4, 4), (uint)src.Length);
            pos += Align(src.Length);
        }

        // offline round-trip verification before touching disk
        var verifyPkg = GianPackage.TryParse(result);
        Assert.NotNull(verifyPkg);
        var vEntry = verifyPkg!.Entries[blobIdx];
        var vBlob = Lzss.Decompress(result.AsSpan(vEntry.PayloadOffset, (int)vEntry.DeclaredSize));
        Assert.Equal(newBlob.Length, vBlob.Length);
        Assert.True(newBlob.AsSpan().SequenceEqual(vBlob), "blob round-trip mismatch");
        // everything except the init entry and the cave is byte-identical to pristine
        Assert.True(vBlob.AsSpan(0, OpBase).SequenceEqual(blob.AsSpan(0, OpBase)), "pre-entry region changed");
        Assert.True(vBlob.AsSpan(OpBase + 4, PristineBlobLen - OpBase - 4)
                        .SequenceEqual(blob.AsSpan(OpBase + 4)), "post-entry region changed");

        if (!File.Exists(BakPath))
            File.Copy(RoomPath, BakPath);
        File.WriteAllBytes(RoomPath, result);

        var reread = File.ReadAllBytes(RoomPath);
        Assert.True(reread.AsSpan().SequenceEqual(result), "post-write verification failed");
        Console.WriteLine($"APPLIED: package {package.Length} -> {result.Length} B; blob {blob.Length} -> {newBlob.Length} B; backup at {BakPath}");
    }

    private static int Align(int v) => (v + GianPackage.SectorSize - 1) & ~(GianPackage.SectorSize - 1);
}
