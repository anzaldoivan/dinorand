using System;
using System.IO;
using System.Linq;
using System.Text;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for the per-room enemy-SE retarget (<see cref="ExePatcher.ExtractRoomDinoSubBlock"/> /
/// <see cref="ExePatcher.RetargetRoomDinoSe"/>) that fixes the st0102 "Theri plays raptor sounds" defect.
/// DC1 binds the loaded dino SE set to the room id via a per-room SE manifest in DINO.exe (room→block
/// directory at <c>0x63A470</c>, blocks of 16-byte <c>{id, flags, namePtr, _}</c> records loaded until
/// <c>namePtr==0</c>, playback id-keyed). The fix copies a native target-species room's dino sub-block over
/// the swapped room's and early-terminates. Decode + offsets: <c>docs/dc1/ENEMY-SOUND-SYSTEM.md</c>.
/// Synthetic-exe tests always run; the real-install test is gated on <c>DINORAND_DC1_DIR</c>.
/// </summary>
public class EnemySoundSeTests
{
    private const uint IB = ExePatcher.ImageBase;

    private static byte[] NewImage() => new byte[ExePatcher.FileBackedRvaHi];

    private static void WriteStr(byte[] exe, uint va, string s)
    {
        int off = (int)(va - IB);
        var b = Encoding.ASCII.GetBytes(s);
        b.CopyTo(exe, off);
        exe[off + b.Length] = 0;
    }

    private static void WriteRec(byte[] exe, uint va, uint id, uint nameVa, uint flags = 0)
    {
        ExePatcher.WriteUInt32AtVa(exe, va + (uint)ExePatcher.SeRecordIdOffset, id);
        ExePatcher.WriteUInt32AtVa(exe, va + 4, flags);
        ExePatcher.WriteUInt32AtVa(exe, va + (uint)ExePatcher.SeRecordNameOffset, nameVa);
        ExePatcher.WriteUInt32AtVa(exe, va + 0xC, 0);
    }

    private static void SetDir(byte[] exe, int stage, int room, uint blockVa)
        => ExePatcher.WriteUInt32AtVa(exe, ExePatcher.SeDirectoryBaseVa + (uint)(stage * 32 + room) * 4, blockVa);

    // String pool (inside the file-backed window, distinct VAs).
    private const uint S_JFOOT = 0x00660000, S_JKAMI = 0x00660020, S_JIKAKU = 0x00660040;
    private const uint S_RFOOT = 0x00660060, S_RKAMI = 0x00660080;
    private const uint S_COMMON = 0x006600A0, S_ROOM = 0x006600C0;

    // Blocks (inside the manifest table region [0x62F320, 0x63A470)).
    private const uint DONOR_BLOCK = 0x00630000;  // native Theri room
    private const uint TARGET_BLOCK = 0x00631000; // swapped (raptor) room

    /// <summary>Build a synthetic exe with a donor (Theri, ids 0x30-0x33) block and a target (raptor,
    /// ids 0x30-0x35) block, wired into the directory at (6,5) and (1,2).</summary>
    private static byte[] BuildImage(int targetDinoRecords = 6)
    {
        var exe = NewImage();
        WriteStr(exe, S_JFOOT, @"se\dino\j_foot");
        WriteStr(exe, S_JKAMI, @"se\dino\j_kami1");
        WriteStr(exe, S_JIKAKU, @"se\dino\j_ikaku");
        WriteStr(exe, S_RFOOT, @"se\dino\r_foot1");
        WriteStr(exe, S_RKAMI, @"se\dino\r_kami1");
        WriteStr(exe, S_COMMON, @"se\common\sw");
        WriteStr(exe, S_ROOM, @"se\room\test");

        // Donor Theri block: [common] + 4 dino (0x30 jFoot, 0x31 jFoot, 0x32 jKami, 0x33 jIkaku) + term.
        uint v = DONOR_BLOCK;
        WriteRec(exe, v, 0xd0, S_COMMON); v += 0x10;
        WriteRec(exe, v, 0x30, S_JFOOT); v += 0x10;
        WriteRec(exe, v, 0x31, S_JFOOT); v += 0x10;
        WriteRec(exe, v, 0x32, S_JKAMI); v += 0x10;
        WriteRec(exe, v, 0x33, S_JIKAKU); v += 0x10;
        WriteRec(exe, v, 0x00, 0); // terminator (namePtr=0)
        SetDir(exe, 6, 5, DONOR_BLOCK);

        // Target raptor block: [room]+[common] + N dino (alternating rFoot/rKami) + term.
        v = TARGET_BLOCK;
        WriteRec(exe, v, 0x90, S_ROOM); v += 0x10;
        WriteRec(exe, v, 0xd0, S_COMMON); v += 0x10;
        for (int i = 0; i < targetDinoRecords; i++)
        {
            WriteRec(exe, v, (uint)(0x30 + i), (i % 2 == 0) ? S_RFOOT : S_RKAMI); v += 0x10;
        }
        WriteRec(exe, v, 0x00, 0);
        SetDir(exe, 1, 2, TARGET_BLOCK);
        return exe;
    }

    /// <summary>Walk a block and return the dino records that the loader would install (id, name) — i.e.
    /// up to the first <c>namePtr==0</c> terminator.</summary>
    private static (uint Id, string Name)[] LoadedDino(byte[] exe, uint blockVa)
    {
        var list = new System.Collections.Generic.List<(uint, string)>();
        for (uint va = blockVa; ; va += (uint)ExePatcher.SeRecordStride)
        {
            uint np = ExePatcher.ReadUInt32AtVa(exe, va + (uint)ExePatcher.SeRecordNameOffset);
            if (np == 0) break;
            string? nm = ExePatcher.ReadCStringAtVa(exe, np);
            if (nm != null && nm.StartsWith(ExePatcher.SeDinoPrefix, StringComparison.OrdinalIgnoreCase))
                list.Add((ExePatcher.ReadUInt32AtVa(exe, va + (uint)ExePatcher.SeRecordIdOffset), nm));
        }
        return list.ToArray();
    }

    [Fact]
    public void SeBlockVa_ResolvesDirectoryEntry()
    {
        var exe = BuildImage();
        Assert.Equal(DONOR_BLOCK, ExePatcher.SeBlockVa(exe, 6, 5));
        Assert.Equal(TARGET_BLOCK, ExePatcher.SeBlockVa(exe, 1, 2));
    }

    [Fact]
    public void ExtractRoomDinoSubBlock_ReturnsContiguousDinoRecords()
    {
        var exe = BuildImage();
        byte[] sub = ExePatcher.ExtractRoomDinoSubBlock(exe, 6, 5);
        Assert.Equal(4 * ExePatcher.SeRecordStride, sub.Length); // the 4 j_ records, not the common record
    }

    [Fact]
    public void RetargetRoomDinoSe_ReplacesRaptorSetWithTheriSet()
    {
        var exe = BuildImage();
        // Pre: target loads raptor SE.
        Assert.All(LoadedDino(exe, TARGET_BLOCK), r => Assert.StartsWith(@"se\dino\r_", r.Name));

        byte[] sub = ExePatcher.ExtractRoomDinoSubBlock(exe, 6, 5);
        var res = ExePatcher.RetargetRoomDinoSe(exe, 1, 2, sub);

        Assert.Equal(4, res.RecordsWritten);
        Assert.Equal(6, res.Capacity);
        var loaded = LoadedDino(exe, TARGET_BLOCK);
        // Post: exactly the 4 Theri records, in order, with the donor's ids — no raptor sample reachable.
        Assert.Equal(4, loaded.Length);
        Assert.All(loaded, r => Assert.StartsWith(@"se\dino\j_", r.Name));
        Assert.Equal(new uint[] { 0x30, 0x31, 0x32, 0x33 }, loaded.Select(l => l.Id).ToArray());
        Assert.Equal(@"se\dino\j_ikaku", loaded[3].Name);
    }

    [Fact]
    public void RetargetRoomDinoSe_DoesNotTouchNonDinoOrOtherBlocks()
    {
        var exe = BuildImage();
        byte[] before = (byte[])exe.Clone();
        byte[] sub = ExePatcher.ExtractRoomDinoSubBlock(exe, 6, 5);
        ExePatcher.RetargetRoomDinoSe(exe, 1, 2, sub);

        // The donor block, the room/common prefix of the target, and all strings are untouched: the only
        // changed bytes lie within the target block's dino sub-block region.
        uint targetDinoStart = TARGET_BLOCK + 2 * (uint)ExePatcher.SeRecordStride;     // after room+common
        uint targetDinoEnd = targetDinoStart + 6 * (uint)ExePatcher.SeRecordStride;    // 6-record capacity
        for (int off = 0; off < before.Length; off++)
        {
            if (before[off] == exe[off]) continue;
            uint va = IB + (uint)off;
            Assert.InRange(va, targetDinoStart, targetDinoEnd + 0x10);
        }
    }

    [Fact]
    public void RetargetRoomDinoSe_IsIdempotent()
    {
        var exe = BuildImage();
        byte[] sub = ExePatcher.ExtractRoomDinoSubBlock(exe, 6, 5);
        ExePatcher.RetargetRoomDinoSe(exe, 1, 2, sub);
        byte[] afterFirst = (byte[])exe.Clone();

        // Re-extract (donor unchanged) and re-apply: capacity is now the donor count (prior terminator),
        // so the second pass writes the same bytes — byte-identical result.
        byte[] sub2 = ExePatcher.ExtractRoomDinoSubBlock(exe, 6, 5);
        var res = ExePatcher.RetargetRoomDinoSe(exe, 1, 2, sub2);
        Assert.Equal(4, res.RecordsWritten);
        Assert.Equal(4, res.Capacity);
        Assert.Equal(afterFirst, exe);
    }

    [Fact]
    public void RetargetRoomDinoSe_Throws_WhenDonorExceedsCapacity()
    {
        var exe = BuildImage(targetDinoRecords: 3); // capacity 3 < donor 4
        byte[] sub = ExePatcher.ExtractRoomDinoSubBlock(exe, 6, 5);
        Assert.Throws<InvalidOperationException>(() => ExePatcher.RetargetRoomDinoSe(exe, 1, 2, sub));
    }

    [Fact]
    public void RetargetRoomDinoSe_Throws_OnEmptyOrMisalignedDonor()
    {
        var exe = BuildImage();
        Assert.Throws<ArgumentException>(() => ExePatcher.RetargetRoomDinoSe(exe, 1, 2, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => ExePatcher.RetargetRoomDinoSe(exe, 1, 2, new byte[0x18]));
    }

    [Fact]
    public void ReadCStringAtVa_ReadsAndRejectsOutOfWindow()
    {
        var exe = BuildImage();
        Assert.Equal(@"se\dino\j_foot", ExePatcher.ReadCStringAtVa(exe, S_JFOOT));
        Assert.Null(ExePatcher.ReadCStringAtVa(exe, 0x006DE990u)); // BSS / not file-backed
    }

    // ---- gated real-data integration: the actual english DINO.exe ----

    private static string? InstallRoot()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        return root;
    }

    [Fact]
    public void RealExe_St102_LoadsRaptorSe_AndRetargetsToTheri()
    {
        var root = InstallRoot();
        if (root is null) return; // gated
        string exePath = Directory.EnumerateFiles(root, "DINO.exe", SearchOption.AllDirectories).FirstOrDefault()
                         ?? Path.Combine(root, "DINO.exe");
        if (!File.Exists(exePath)) return;

        byte[] exe = File.ReadAllBytes(exePath);
        // st0102 (stage 1, room 2) natively loads the Velociraptor (r_*) set; st605 (stage 6, room 5) the Theri (j_*) set.
        var st102Before = LoadedDino(exe, ExePatcher.SeBlockVa(exe, 1, 2));
        Assert.NotEmpty(st102Before);
        Assert.Contains(st102Before, r => r.Name.StartsWith(@"se\dino\r_", StringComparison.OrdinalIgnoreCase));

        byte[] sub = ExePatcher.ExtractRoomDinoSubBlock(exe, 6, 5);
        Assert.True(sub.Length >= 16 * 10); // the ~17 j_ records
        Assert.All(SubRecords(exe, sub), nm => Assert.StartsWith(@"se\dino\j_", nm));

        var res = ExePatcher.RetargetRoomDinoSe(exe, 1, 2, sub);
        var st102After = LoadedDino(exe, ExePatcher.SeBlockVa(exe, 1, 2));
        Assert.All(st102After, r => Assert.StartsWith(@"se\dino\j_", r.Name)); // no raptor sample reachable
        Assert.True(res.RecordsWritten >= 10 && res.RecordsWritten <= res.Capacity);
    }

    private static string[] SubRecords(byte[] exe, byte[] sub)
    {
        var names = new System.Collections.Generic.List<string>();
        for (int i = 0; i < sub.Length; i += ExePatcher.SeRecordStride)
        {
            uint np = BitConverter.ToUInt32(sub, i + ExePatcher.SeRecordNameOffset);
            names.Add(ExePatcher.ReadCStringAtVa(exe, np) ?? "?");
        }
        return names.ToArray();
    }
}
