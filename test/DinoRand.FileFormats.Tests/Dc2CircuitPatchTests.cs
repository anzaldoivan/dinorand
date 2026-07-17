using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Dc2CircuitPatch (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 2, K110) on synthetic room
/// packages: a slot-5 script with the vanilla blink runs laid out at walker-discoverable positions —
/// no game files in the repo. The blink-run byte shape (3 literal pushes group/id/value + op-0x1c)
/// mirrors the decoded ST607/ST402 routines.
/// </summary>
public class Dc2CircuitPatchTests
{
    /// <summary>Build a decompressed SCD blob whose sorted slot-5 routine table matches
    /// <paramref name="spec"/>: blink routines carry the vanilla SetFlag runs + terminator,
    /// every other routine is a bare RETURN.</summary>
    internal static byte[] MakeBlob(Dc2CircuitPatch.RoomSpec spec)
    {
        int routineCount = spec.Routines.Max(r => r.RoutineIndex) + 1;

        static byte[] Push(int value) => new byte[] { 0x05, 0x00, (byte)value, (byte)(value >> 8) };
        static IEnumerable<byte> SetFlag(int group, int id, int value)
            => Push(group).Concat(Push(id)).Concat(Push(value)).Concat(new byte[] { 0x1c, 0x00 });

        var bodies = new byte[routineCount][];
        for (int i = 0; i < routineCount; i++)
        {
            var blinkSpec = spec.Routines.FirstOrDefault(r => r.RoutineIndex == i);
            if (blinkSpec is null)
            {
                bodies[i] = new byte[] { 0x04, 0x00 }; // RETURN
                continue;
            }
            var body = new List<byte>();
            foreach (int id in blinkSpec.VanillaIds)
                body.AddRange(SetFlag(7, id, 1));
            body.AddRange(SetFlag(7, blinkSpec.TerminatorId, 1)); // terminator
            body.AddRange(new byte[] { 0x04, 0x00 });             // RETURN
            bodies[i] = body.ToArray();
        }

        // Section at blob+0x80; routine offset directory at opbase = section+0x1c, offsets relative
        // to opbase and ascending, bodies packed right after the directory (self-bounding contract).
        const int sectionStart = 0x80;
        int opbase = sectionStart + 0x1c;
        var offsets = new int[routineCount];
        int cursor = routineCount * 4;
        for (int i = 0; i < routineCount; i++)
        {
            offsets[i] = cursor;
            cursor += bodies[i].Length;
        }

        var blob = new byte[opbase + cursor];
        WriteU32(blob, 0x14, Dc2DoorEditor.BlobBaseVa + sectionStart); // directory entry [5]
        for (int i = 0; i < routineCount; i++)
            WriteU32(blob, opbase + i * 4, (uint)offsets[i]);
        for (int i = 0; i < routineCount; i++)
            bodies[i].CopyTo(blob, opbase + offsets[i]);
        return blob;
    }

    internal static byte[] MakePackage(Dc2CircuitPatch.RoomSpec spec)
        => SyntheticRoom.Package(GianPackage.Dc2EntrySize,
            (GianEntryType.Lzss0, Lzss.Compress(MakeBlob(spec))),
            (GianEntryType.Data, new byte[16]));

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    public static IEnumerable<object[]> AllRooms()
        => Dc2CircuitPatch.Rooms.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(AllRooms))]
    public void LocateBlinkIdOffsets_FindsTheVanillaRuns(Dc2CircuitPatch.RoomSpec spec)
    {
        var blob = MakeBlob(spec);
        foreach (var routine in spec.Routines)
        {
            var offs = Dc2CircuitPatch.LocateBlinkIdOffsets(blob, spec, routine);
            Assert.Equal(routine.VanillaIds.Count, offs.Length);
            for (int i = 0; i < offs.Length; i++)
                Assert.Equal(routine.VanillaIds[i], blob[offs[i]] | (blob[offs[i] + 1] << 8));
        }
    }

    [Theory]
    [MemberData(nameof(AllRooms))]
    public void ShuffleRoom_KeepsLengthsCoverageNoAdjacentRepeats_AndTouchesOnlyBlinkIds(Dc2CircuitPatch.RoomSpec spec)
    {
        var pristineBlob = MakeBlob(spec);
        var package = MakePackage(spec);

        var shuffled = Dc2CircuitPatch.ShuffleRoom(package, spec, new Random(1234), out var results);
        var newBlob = Dc2DoorEditor.DecompressScdBlob(shuffled);
        Assert.Equal(pristineBlob.Length, newBlob.Length);

        var editedOffsets = new HashSet<int>();
        Assert.Equal(spec.Routines.Count, results.Length);
        foreach (var routine in spec.Routines)
        {
            var offs = Dc2CircuitPatch.LocateBlinkIdOffsets(pristineBlob, spec, routine);
            var r = results.Single(x => x.RoutineIndex == routine.RoutineIndex);
            Assert.Equal(routine.VanillaIds, r.OldIds);
            Assert.Equal(offs.Length, r.NewIds.Length);                       // length preserved
            Assert.All(r.NewIds, id => Assert.Contains(id, spec.BoxIds));     // alphabet only
            Assert.All(spec.BoxIds, id => Assert.Contains(id, r.NewIds));     // every box at least once
            for (int i = 1; i < r.NewIds.Length; i++)
                Assert.NotEqual(r.NewIds[i - 1], r.NewIds[i]);                // no adjacent repeats
            for (int i = 0; i < offs.Length; i++)
            {
                Assert.Equal(r.NewIds[i], newBlob[offs[i]] | (newBlob[offs[i] + 1] << 8));
                editedOffsets.Add(offs[i]);
                editedOffsets.Add(offs[i] + 1);
            }
        }
        // every byte OUTSIDE the blink id literals is untouched — terminators, cadence, all of it
        for (int i = 0; i < pristineBlob.Length; i++)
            if (!editedOffsets.Contains(i))
                Assert.Equal(pristineBlob[i], newBlob[i]);
    }

    [Fact]
    public void ShuffleRoom_IsDeterministicPerRngSeed()
    {
        var spec = Dc2CircuitPatch.Rooms[0];
        var package = MakePackage(spec);
        var a = Dc2CircuitPatch.ShuffleRoom(package, spec, new Random(7), out _);
        var b = Dc2CircuitPatch.ShuffleRoom(package, spec, new Random(7), out _);
        var c = Dc2CircuitPatch.ShuffleRoom(package, spec, new Random(8), out _);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void LocateBlinkIdOffsets_TamperedSequence_Throws()
    {
        var spec = Dc2CircuitPatch.Rooms[0];
        var routine = spec.Routines[0];
        var blob = MakeBlob(spec);

        var offs = Dc2CircuitPatch.LocateBlinkIdOffsets(blob, spec, routine);
        blob[offs[3]] = 42; // not the vanilla id
        var ex = Assert.Throws<InvalidOperationException>(
            () => Dc2CircuitPatch.LocateBlinkIdOffsets(blob, spec, routine));
        Assert.Contains("refusing to shuffle circuits", ex.Message);
    }

    [Fact]
    public void LocateBlinkIdOffsets_MissingRoutine_Throws()
    {
        var spec = Dc2CircuitPatch.Rooms[0];
        // a blob with only routine 0 present cannot satisfy routine index 7
        var tiny = MakeBlob(new Dc2CircuitPatch.RoomSpec(spec.FileName, spec.Stage, spec.Room, spec.BoxIds,
            new[] { new Dc2CircuitPatch.RoutineSpec(0, spec.Routines[0].VanillaIds, spec.Routines[0].TerminatorId) }));
        Assert.Throws<InvalidOperationException>(
            () => Dc2CircuitPatch.LocateBlinkIdOffsets(tiny, spec, spec.Routines[0]));
    }

    [Fact]
    public void GenerateSequence_HonoursAllThreeConstraints()
    {
        var alphabet = new[] { 16, 17, 18, 19, 20 };
        for (int seed = 0; seed < 25; seed++)
        {
            var seq = Dc2CircuitPatch.GenerateSequence(alphabet, 14, new Random(seed));
            Assert.Equal(14, seq.Length);
            Assert.All(seq, id => Assert.Contains(id, alphabet));
            Assert.All(alphabet, id => Assert.Contains(id, seq));
            for (int i = 1; i < seq.Length; i++)
                Assert.NotEqual(seq[i - 1], seq[i]);
        }
    }

    /// <summary>Real-data pin, gated on <c>DINORAND_DC2_DIR</c>: the vanilla blink runs are found at
    /// walker-derived offsets in the shipping ST607.DAT/ST402.DAT (read-only; skips if a room is
    /// already shuffled, i.e. has a .bak).</summary>
    [Fact]
    public void RealRooms_VanillaPinsHold()
    {
        var dataDir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir)) return;

        foreach (var spec in Dc2CircuitPatch.Rooms)
        {
            var path = Path.Combine(dataDir, spec.FileName);
            if (!File.Exists(path)) return;
            if (File.Exists(path + ".bak")) return; // room already shuffled in place → skip
            var blob = Dc2DoorEditor.DecompressScdBlob(File.ReadAllBytes(path));
            foreach (var routine in spec.Routines)
            {
                var offs = Dc2CircuitPatch.LocateBlinkIdOffsets(blob, spec, routine);
                Assert.Equal(routine.VanillaIds.Count, offs.Length);
            }
        }
    }
}
