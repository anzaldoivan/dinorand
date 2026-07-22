using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Dc2PlateKeyPatch (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 4, K118) on synthetic room
/// packages: ST205's slot-5 routine[2] SAT-9 (op-0x39) use-plate records, laid at walker-discoverable
/// positions — no game files in the repo. Each record's byte shape (plate push / thread push /
/// routineIdx push + op-0x39) mirrors the decoded ST205 terminal registration: block+0x00 = routine
/// idx (last push), block+0x08 = required plate id (3rd-from-last push). The re-key lever permutes ONLY
/// the block+0x00 routine-idx literals of the blue terminal (Group B), leaving every block+0x08 plate id
/// untouched so each reject routine stays coupled to its own plate.
/// </summary>
public class Dc2PlateKeyPatchTests
{
    // ---- synthetic ST205 slot-5 blob: routine[2] carrying the 18 vanilla op-0x39 records ----

    private static byte[] Push(int value, int mode) => new byte[] { 0x05, (byte)mode, (byte)value, (byte)(value >> 8) };

    /// <summary>One op-0x39 record: plate (block+0x08) / thread (block+0x04) / routineIdx (block+0x00)
    /// pushes then the op — the routineIdx push is mode-4, the plate/thread pushes mode-0 (both are
    /// literal-passthrough in resolver 0x48BA50). Mirrors the ST205 record byte shape (K118).</summary>
    private static IEnumerable<byte> Record(int plate, int routine)
        => Push(plate, 0).Concat(Push(3, 0)).Concat(Push(routine, 4)).Concat(new byte[] { 0x39, 0x00 });

    /// <summary>Build a decompressed SCD blob whose sorted routine[2] holds the 18 vanilla records;
    /// routines 0/1 are bare RETURNs.</summary>
    internal static byte[] MakeBlob()
    {
        const int routineCount = 3;
        var bodies = new byte[routineCount][];
        bodies[0] = new byte[] { 0x04, 0x00 };
        bodies[1] = new byte[] { 0x04, 0x00 };
        var r2 = new List<byte>();
        foreach (var (plate, routine) in Dc2PlateKeyPatch.VanillaRecords)
            r2.AddRange(Record(plate, routine));
        r2.AddRange(new byte[] { 0x04, 0x00 }); // RETURN
        bodies[2] = r2.ToArray();

        const int sectionStart = 0x80;
        int opbase = sectionStart + 0x1c;
        var offsets = new int[routineCount];
        int cursor = routineCount * 4;
        for (int i = 0; i < routineCount; i++) { offsets[i] = cursor; cursor += bodies[i].Length; }

        var blob = new byte[opbase + cursor];
        WriteU32(blob, 0x14, Dc2DoorEditor.BlobBaseVa + sectionStart); // directory entry [5]
        for (int i = 0; i < routineCount; i++) WriteU32(blob, opbase + i * 4, (uint)offsets[i]);
        for (int i = 0; i < routineCount; i++) bodies[i].CopyTo(blob, opbase + offsets[i]);
        return blob;
    }

    /// <summary>64-entry BGR555 palette (128 B): a blue-violet ramp (blue-family) followed by a grey
    /// ramp, so the recolor's blue-family predicate has both hits and non-hits to discriminate.</summary>
    internal static byte[] MakePalette()
    {
        var pal = new byte[128];
        for (int i = 0; i < 64; i++)
        {
            int r, g, b;
            if (i < 18) { r = 5 + i % 4; g = 6 + i % 3; b = 10 + i % 6 + 8; } // blue-dominant (b>g, b>=r, b>=8)
            else { r = g = b = (i - 18) & 0x1f; }                             // grey ramp
            int v = (r & 0x1f) | ((g & 0x1f) << 5) | ((b & 0x1f) << 10);
            pal[i * 2] = (byte)v; pal[i * 2 + 1] = (byte)(v >> 8);
        }
        return pal;
    }

    /// <summary>Package with the script blob at entry 0, filler Data entries 1-12, and the panel
    /// PALETTE at the ST205 index (13).</summary>
    internal static byte[] MakePackage()
    {
        var entries = new List<(GianEntryType, byte[])> { (GianEntryType.Lzss0, Compression.Lzss.Compress(MakeBlob())) };
        for (int i = 1; i < Dc2PlateKeyPatch.PaletteEntryIndex; i++)
            entries.Add((GianEntryType.Data, new byte[] { 1, 2, 3, 4 }));
        entries.Add((GianEntryType.Palette, MakePalette()));
        return SyntheticRoom.Package(GianPackage.Dc2EntrySize, entries.ToArray());
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    private static byte[] Blob(byte[] package) => Dc2DoorEditor.DecompressScdBlob(package);

    private static int ReadI16(byte[] b, int off) => (short)(b[off] | (b[off + 1] << 8));

    // ---- T-A: SelectRequiredPlate ----

    [Fact]
    public void SelectRequiredPlate_IsDeterministic_Uniform_OverTheSixPlates()
    {
        // deterministic per RNG state
        Assert.Equal(Dc2PlateKeyPatch.SelectRequiredPlate(new Random(1234)),
                     Dc2PlateKeyPatch.SelectRequiredPlate(new Random(1234)));

        // every pick is a real plate id; all six are reachable across seeds (uniform)
        var seen = new HashSet<int>();
        for (int s = 0; s < 600; s++)
        {
            int p = Dc2PlateKeyPatch.SelectRequiredPlate(new Random(s));
            Assert.Contains(p, Dc2PlateKeyPatch.PlateIds);
            seen.Add(p);
        }
        Assert.Equal(Dc2PlateKeyPatch.PlateIds.OrderBy(x => x), seen.OrderBy(x => x));
    }

    // ---- T-B: routing permutation ----

    [Fact]
    public void ApplyRoom_TargetBlue_IsByteIdentical()
    {
        var package = MakePackage();
        var outp = Dc2PlateKeyPatch.ApplyRoom(package, Dc2PlateKeyPatch.VanillaCorrectPlate, out var result);
        Assert.Equal(package, outp);                                   // no-op: blue is already correct
        Assert.False(result.Changed);
    }

    [Theory]
    [InlineData(Dc2PlateKeyPatch.Green)]
    [InlineData(Dc2PlateKeyPatch.Red)]
    [InlineData(Dc2PlateKeyPatch.Yellow)]
    [InlineData(Dc2PlateKeyPatch.White)]
    [InlineData(Dc2PlateKeyPatch.Purple)]
    public void ApplyRoom_ReKeysTerminal_FlippingOnlyTwoRoutineLiterals(int target)
    {
        var package = MakePackage();
        var pristineBlob = Blob(package);

        var pristineRecs = Dc2PlateKeyPatch.LocateRecords(pristineBlob);
        var outp = Dc2PlateKeyPatch.ApplyRoom(package, target, out var result);
        var newBlob = Blob(outp);
        Assert.True(result.Changed);
        Assert.Equal(pristineBlob.Length, newBlob.Length);
        Assert.Equal(2, result.RoutingEdits.Length);

        // every differing blob byte lies inside one of the two routineIdx literals — nothing else moved
        var editWindows = result.RoutingEdits.SelectMany(e => new[] { e.Offset, e.Offset + 1 }).ToHashSet();
        for (int i = 0; i < pristineBlob.Length; i++)
            if (pristineBlob[i] != newBlob[i]) Assert.Contains(i, editWindows);
        foreach (var e in result.RoutingEdits)
        {
            Assert.Equal(e.OldValue, ReadI16(pristineBlob, e.Offset));
            Assert.Equal(e.NewValue, ReadI16(newBlob, e.Offset));
        }

        // the seed plate's record is promoted to ACCEPT (r20); blue is demoted to its own reject (r16)
        var promote = result.RoutingEdits.Single(e => e.NewValue == Dc2PlateKeyPatch.Terminal2AcceptRoutine);
        var demote = result.RoutingEdits.Single(e => e.NewValue == Dc2PlateKeyPatch.RejectRoutineOf(Dc2PlateKeyPatch.VanillaCorrectPlate));
        Assert.Equal(target, pristineRecs.Single(r => r.RoutineIdxOffset == promote.Offset).PlateId);
        Assert.Equal(Dc2PlateKeyPatch.VanillaCorrectPlate, pristineRecs.Single(r => r.RoutineIdxOffset == demote.Offset).PlateId);

        // block+0x08 plate ids are never touched — every reject routine stays coupled to its plate
        foreach (var rec in pristineRecs) Assert.Equal(rec.PlateId, ReadI16(newBlob, rec.PlateIdOffset));

        // the blue panel (entry 13) is recoloured in the output package — routing + recolor both wired
        Assert.NotEqual(PaletteOf(package), PaletteOf(outp));
    }

    private static byte[] PaletteOf(byte[] package)
    {
        var pkg = GianPackage.TryParse(package)!;
        var e = pkg.Entries[Dc2PlateKeyPatch.PaletteEntryIndex];
        return package.AsSpan(e.PayloadOffset, (int)e.DeclaredSize).ToArray();
    }

    [Fact]
    public void LocateRecords_TamperedSignature_Throws()
    {
        var blob = MakeBlob();
        // corrupt the accept record's routine idx so the vanilla 18-record signature no longer holds
        var rec = Dc2PlateKeyPatch.LocateRecords(blob).First(r => r.RoutineIndex == Dc2PlateKeyPatch.Terminal2AcceptRoutine);
        blob[rec.RoutineIdxOffset] = 99;
        var ex = Assert.Throws<InvalidOperationException>(() => Dc2PlateKeyPatch.LocateRecords(blob));
        Assert.Contains("refusing to re-key", ex.Message);
    }

    // ---- T-C: palette recolor ----

    [Fact]
    public void RecolorPanelPalette_TargetBlue_IsByteIdentical()
    {
        var pal = MakePalette();
        Assert.Equal(pal, Dc2PlateKeyPatch.RecolorPanelPalette(pal, Dc2PlateKeyPatch.VanillaCorrectPlate));
    }

    [Theory]
    [InlineData(Dc2PlateKeyPatch.Green)]
    [InlineData(Dc2PlateKeyPatch.Red)]
    [InlineData(Dc2PlateKeyPatch.Purple)]
    public void RecolorPanelPalette_ShiftsOnlyBlueFamilyEntries_AndPreservesLength(int target)
    {
        var pal = MakePalette();
        var outp = Dc2PlateKeyPatch.RecolorPanelPalette(pal, target);
        Assert.Equal(pal.Length, outp.Length);

        bool anyShifted = false;
        for (int i = 0; i < 64; i++)
        {
            int v = pal[i * 2] | (pal[i * 2 + 1] << 8);
            int r = v & 0x1f, g = (v >> 5) & 0x1f, b = (v >> 10) & 0x1f;
            bool blueFamily = b > g && b >= r && b >= 8;
            int nv = outp[i * 2] | (outp[i * 2 + 1] << 8);
            if (blueFamily) { if (nv != v) anyShifted = true; }
            else Assert.Equal(v, nv); // grey/other entries untouched
        }
        Assert.True(anyShifted, "at least one blue-family entry should have been recoloured");
    }

    /// <summary>Real-data pin, gated on <c>DINORAND_DC2_DIR</c>: the vanilla 18-record signature is
    /// found at walker-derived positions in the shipping ST205.DAT, and entry 13 is a 128-byte
    /// PALETTE (read-only; prefers the .bak if the install is already re-keyed).</summary>
    [Fact]
    public void RealRoom_VanillaSignatureAndPaletteHold()
    {
        var dataDir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir)) return;
        var src = Path.Combine(dataDir, "ST205.DAT");
        var bak = src + ".bak";
        var path = File.Exists(bak) ? bak : src;
        if (!File.Exists(path)) return;

        var package = File.ReadAllBytes(path);
        var records = Dc2PlateKeyPatch.LocateRecords(Dc2DoorEditor.DecompressScdBlob(package));
        Assert.Equal(Dc2PlateKeyPatch.VanillaRecords.Count, records.Length);
        for (int i = 0; i < records.Length; i++)
        {
            Assert.Equal(Dc2PlateKeyPatch.VanillaRecords[i].Plate, records[i].PlateId);
            Assert.Equal(Dc2PlateKeyPatch.VanillaRecords[i].Routine, records[i].RoutineIndex);
        }
        var pkg = GianPackage.TryParse(package)!;
        Assert.Equal(GianEntryType.Palette, pkg.Entries[Dc2PlateKeyPatch.PaletteEntryIndex].Type);
        Assert.Equal(128u, pkg.Entries[Dc2PlateKeyPatch.PaletteEntryIndex].DeclaredSize);
    }
}
