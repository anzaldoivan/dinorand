using System.Buffers.Binary;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The CE-gate authoring labs (STATIC-SCD-RE cont.48/49/51/52): authored <c>0x20</c> record fields
/// (+3 AI param / +5 birth mode / +6 maxHP), the optional op-<c>0x22</c>+op-<c>0x3a</c> activation-pair
/// emission behind <see cref="EnemyAuthoring"/>, and the species palette copy
/// (<see cref="TextureImporter.CopySpeciesPalette"/>). Corpus-backed tests are gated on
/// <c>DINORAND_DC1_DIR</c> (no-op on CI); the palette tests run on synthetic packages.
/// </summary>
public class EnemyAuthoringTests
{
    private static string? DataDir()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        foreach (var c in new[] { root, Path.Combine(root, "Data") })
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "st*.dat").Any())
                return c;
        return Directory.EnumerateDirectories(root, "Data", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static RoomFile Load(string dir, int stage, int room, string file)
        => RoomFile.Read(stage, room, File.ReadAllBytes(Path.Combine(dir, file)));

    private static IEnumerable<int> Heads(RoomFile rf)
    {
        foreach (var e in rf.Enemies)
        {
            if (e.OriginalModelPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
            if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
        }
    }

    /// <summary>The st102 deploy shape (cont.52): reuse the room's own cat-2 model, splice at the
    /// hand-verified post-0x59 offset 0x3A164, author +6/+5/+3 — verify the bytes land in the record.</summary>
    [Fact]
    public void AddEnemyAt_AuthoredFields_LandInTheRecordBytes()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        var path = Path.Combine(dir, "st102.dat");
        if (!File.Exists(path)) return;

        var target = Load(dir, 1, 0x02, "st102.dat");
        // st102's only enemy is the 0x59 RaptorHeavy at slot 0 (cont.52) — donor from its own record.
        var native = target.Enemies.Single();
        Assert.Equal(DcOpcodes.Enemy2, native.Opcode);
        Assert.Equal(0, native.Slot);
        var donor = SpeciesImporter.ExtractDonor(target.RdtBuffer, native, Heads(target));

        const int off = 0x3A164; // post-0x59, pre-op-0x05 tail-call (cont.52 §4)
        var authoring = new EnemyAuthoring { MaxHp = 2222, AiParam = 0, BirthMode = 0 };
        var added = target.AddEnemyAt(donor, off, 4200, 0, 1200, 0, authoring: authoring);

        Assert.Equal(off, added.FileOffset);
        Assert.Equal(1, added.Slot);                       // slot 0 is the native 0x59 — auto-pick must skip it
        Assert.Equal(2222, added.MaxHp);
        // The raw record bytes carry the authored fields at +3/+5/+6.
        var raw = target.RdtBuffer.AsSpan(off, DcOpcodes.EnemyLength);
        Assert.Equal(DcOpcodes.Enemy, raw[0]);
        Assert.Equal(0, raw[EnemyRecord.AiParamOffset]);
        Assert.Equal(0, raw[EnemyRecord.BirthModeOffset]);
        Assert.Equal(2222, BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(EnemyRecord.MaxHpOffset, 2)));
        // In-room model reuse: the injected record points at the 0x59's own resources (renderable).
        Assert.Equal(native.OriginalModelPtr, added.ModelPtr);
        Assert.Equal(native.OriginalMotionPtr, added.MotionPtr);

        // Round-trips and still parses clean (the emitted record walks as a normal 0x20).
        var reread = RoomFile.Read(1, 0x02, target.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Contains(reread.Enemies, e => e.FileOffset == off && e.MaxHp == 2222 && e.Slot == 1);
    }

    /// <summary>Activation-pair emission (cont.49): the injected 0x20 is followed by
    /// <c>22 02 &lt;slot&gt; 00</c> + <c>3a 00 &lt;behavior&gt; 00 &lt;blob8&gt;</c>, the blob copied from the
    /// room's first native 0x3a, and the grown script still walks clean.</summary>
    [Fact]
    public void AddEnemyAt_EmitsActivationPair_BlobCopiedFromNative3a()
    {
        var dir = DataDir();
        if (dir is null) return;
        var path = Path.Combine(dir, "st102.dat");
        if (!File.Exists(path)) return;

        var target = Load(dir, 1, 0x02, "st102.dat");
        var native = target.Enemies.Single();
        var donor = SpeciesImporter.ExtractDonor(target.RdtBuffer, native, Heads(target));

        // st102's first native 0x3a (sub6 @0x3A22C, cont.52) carries an ALL-ZERO blob.
        const int off = 0x3A164;
        var added = target.AddEnemyAt(donor, off, 4200, 0, 1200, 0,
                                      authoring: new EnemyAuthoring { ActivateBehavior = 5 });

        var buf = target.RdtBuffer;
        int o = off + DcOpcodes.EnemyLength;
        Assert.Equal(0x22, buf[o]);
        Assert.Equal(0x02, buf[o + 1]);
        Assert.Equal(added.Slot, buf[o + 2]);              // binds the record's own slot
        Assert.Equal(0x00, buf[o + 3]);
        Assert.Equal(0x3a, buf[o + 4]);
        Assert.Equal(0x00, buf[o + 5]);
        Assert.Equal(5, buf[o + 6]);                       // behavior code -> +0x3C = (5<<8)|4
        Assert.Equal(0x00, buf[o + 7]);
        Assert.All(buf[(o + 8)..(o + 16)], b => Assert.Equal(0, b)); // st102's native blob is zeros

        var reread = RoomFile.Read(1, 0x02, target.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Contains(reread.Enemies, e => e.FileOffset == off);
    }

    /// <summary>The auto-copy prefers a native 0x3a whose behavior code MATCHES the requested one and
    /// carries its byte[3] along: st10e's first behavior-1 install is <c>3a 00 01 1f fcd20000 05dd0000</c>
    /// (sub12 @0x1CC24), while its behavior-6/5 installs differ — requesting behavior 1 must copy the
    /// 0x1F + that blob, not the first 0x3a in file order.</summary>
    [Fact]
    public void AddEnemyAt_ActivationCopy_PrefersMatchingBehavior_AndCarriesB3()
    {
        var dir = DataDir();
        if (dir is null) return;
        if (!File.Exists(Path.Combine(dir, "st10e.dat"))) return;

        var target = Load(dir, 1, 0x0e, "st10e.dat");
        var native = target.Enemies.First(e => e.Species == DinoSpecies.Velociraptor);
        var donor = SpeciesImporter.ExtractDonor(target.RdtBuffer, native, Heads(target));

        const int off = 0x1CB18; // sub0 tail: post both raptors, before the op-0x01 task-spawn (cont.53 prep)
        var added = target.AddEnemyAt(donor, off, -7718, 0, -9086, 0, slot: 2,
                                      authoring: new EnemyAuthoring { ActivateBehavior = 1 });

        var buf = target.RdtBuffer;
        int o = off + DcOpcodes.EnemyLength;
        Assert.Equal(new byte[] { 0x22, 0x02, 2, 0x00 }, buf[o..(o + 4)]);
        Assert.Equal(0x3a, buf[o + 4]);
        Assert.Equal(1, buf[o + 6]);                        // requested behavior
        Assert.Equal(0x1f, buf[o + 7]);                     // b3 copied from the matching native 0x3a
        Assert.Equal(new byte[] { 0xfc, 0xd2, 0, 0, 0x05, 0xdd, 0, 0 }, buf[(o + 8)..(o + 16)]);
        Assert.Equal(2, added.Slot);                        // explicit slot honoured

        var reread = RoomFile.Read(1, 0x0e, target.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Contains(reread.Enemies, e => e.FileOffset == off && e.Slot == 2);
    }

    /// <summary>An explicit 8-byte blob overrides the native copy; a wrong-size blob is refused.</summary>
    [Fact]
    public void AddEnemyAt_ExplicitBlob_UsedVerbatim_AndWrongSizeRefused()
    {
        var dir = DataDir();
        if (dir is null) return;
        var path = Path.Combine(dir, "st102.dat");
        if (!File.Exists(path)) return;

        var target = Load(dir, 1, 0x02, "st102.dat");
        var native = target.Enemies.Single();
        var donor = SpeciesImporter.ExtractDonor(target.RdtBuffer, native, Heads(target));
        var blob = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        const int off = 0x3A164;
        target.AddEnemyAt(donor, off, 0, 0, 0, 0,
                          authoring: new EnemyAuthoring { ActivateBehavior = 1, ActivateBlob = blob });
        Assert.Equal(blob, target.RdtBuffer[(off + 32)..(off + 40)]);

        var fresh = Load(dir, 1, 0x02, "st102.dat");
        var freshDonor = SpeciesImporter.ExtractDonor(fresh.RdtBuffer, fresh.Enemies.Single(), Heads(fresh));
        Assert.Throws<ArgumentException>(() => fresh.AddEnemyAt(freshDonor, off, 0, 0, 0, 0,
            authoring: new EnemyAuthoring { ActivateBehavior = 1, ActivateBlob = new byte[4] }));
    }

    // ---- palette copy (synthetic packages; no game files needed) -----------------------------------

    /// <summary>A minimal DC1 package: [Palette(rect), Data filler]. The palette payload is
    /// <paramref name="fill"/> repeated over 512 bytes.</summary>
    private static byte[] PackageWithPalette(int x, int y, byte fill, int palSize = 512)
    {
        const int header = 2048, sector = 2048;
        var buf = new byte[header + sector + sector];
        void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), v);
        void U16(int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), v);
        // entry 0: type-2 palette, RECT (x, y, 256, 1)
        U32(0, (uint)GianEntryType.Palette); U32(4, (uint)palSize);
        U16(8, (ushort)x); U16(10, (ushort)y); U16(12, 256); U16(14, 1);
        // entry 1: raw data filler (nonzero size terminates cleanly; offset 16.. nonzero defeats DC2 probe)
        U32(16, (uint)GianEntryType.Data); U32(20, 4);
        buf.AsSpan(header, palSize).Fill(fill);
        return buf;
    }

    [Fact]
    public void CopySpeciesPalette_CopiesTheClutEntry_AndNothingElse()
    {
        // CLUT code for VRAM (768,511) — the cat-2 raptor row (cont.51).
        ushort clut = TextureImporter.MakeClut(768, 511);
        var target = PackageWithPalette(768, 511, 0x11);
        var donor = PackageWithPalette(768, 511, 0x99);

        var patched = TextureImporter.CopySpeciesPalette(target, donor, clut);

        Assert.All(patched[2048..(2048 + 512)], b => Assert.Equal(0x99, b));  // palette replaced
        Assert.Equal(target[..2048], patched[..2048]);                        // header untouched
        Assert.Equal(target[(2048 + 512)..], patched[(2048 + 512)..]);        // everything else untouched
    }

    [Fact]
    public void CopySpeciesPalette_Refuses_MissingEntry_And_SizeMismatch()
    {
        ushort clut = TextureImporter.MakeClut(768, 511);
        var atClut = PackageWithPalette(768, 511, 0x11);
        var elsewhere = PackageWithPalette(768, 260, 0x99);
        Assert.Throws<InvalidOperationException>(() => TextureImporter.CopySpeciesPalette(atClut, elsewhere, clut));
        Assert.Throws<InvalidOperationException>(() => TextureImporter.CopySpeciesPalette(elsewhere, atClut, clut));

        var small = PackageWithPalette(768, 511, 0x99, palSize: 256);
        Assert.Throws<InvalidOperationException>(() => TextureImporter.CopySpeciesPalette(atClut, small, clut));
    }
}
