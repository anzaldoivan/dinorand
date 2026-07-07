using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Integration test over the real DC1 install. Point <c>DINORAND_DC1_DIR</c> at a
/// directory containing the game's <c>Data</c> folder; the test then asserts that every
/// room file survives <c>Read</c> → <c>Write</c> byte-identically — the Phase 0 exit
/// gate (DESIGN.md §6). With no env var set (e.g. CI without game files) it no-ops.
///
/// Today this proves the non-destructive guarantee (Write preserves bytes). Once
/// <see cref="RoomFile"/> parses chunks and rebuilds them, the same assertion becomes
/// the real structural round-trip with no test changes.
/// </summary>
public class RoomFileRoundTripTests
{
    private static readonly System.Text.RegularExpressions.Regex RoomPattern =
        new(@"^st([0-9a-c])([0-9a-f]{2})\.dat$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static IEnumerable<object[]> RoomFiles()
    {
        // Explicit env var wins; otherwise fall back to the in-repo English data so the
        // tests run on a fresh clone with no setup. Either way a missing dir no-ops below.
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) root = FindRepoRoot();
        if (string.IsNullOrEmpty(root)) yield break;

        var dataDir = FindDataDir(root);
        if (dataDir is null) yield break;

        foreach (var path in Directory.EnumerateFiles(dataDir, "st*.dat"))
            if (RoomPattern.IsMatch(Path.GetFileName(path)))
                yield return new object[] { path };
    }

    [Theory]
    [MemberData(nameof(RoomFiles))]
    public void Room_ReadWrite_IsByteIdentical(string path)
    {
        var name = Path.GetFileName(path);
        var m = RoomPattern.Match(name);
        int stage = Convert.ToInt32(m.Groups[1].Value, 16);
        int room = Convert.ToInt32(m.Groups[2].Value, 16);

        var original = File.ReadAllBytes(path);
        var rewritten = RoomFile.Read(stage, room, original).Write();

        Assert.Equal(original, rewritten);
    }

    [Theory]
    [MemberData(nameof(RoomFiles))]
    public void Room_ItemIdEdit_SurvivesWriteReadCycle(string path)
    {
        var name = Path.GetFileName(path);
        var m = RoomPattern.Match(name);
        int stage = Convert.ToInt32(m.Groups[1].Value, 16);
        int room = Convert.ToInt32(m.Groups[2].Value, 16);

        var original = RoomFile.Read(stage, room, File.ReadAllBytes(path));

        // Pick a real (non-empty) item record to reroll; rooms without one are skipped.
        var target = original.Items.FirstOrDefault(i => !i.IsEmptySlot);
        if (target is null) return;

        int index = original.Items.IndexOf(target);
        int newId = target.ItemId == 0x16 ? 0x21 : 0x16; // any different supply id
        target.ItemId = newId;

        var rewritten = original.Write();
        Assert.NotEqual(original.OriginalBytes, rewritten);

        var reread = RoomFile.Read(stage, room, rewritten);
        Assert.Equal(newId, reread.Items[index].ItemId);
        // Every other item id is unchanged (only the one record was edited).
        for (int i = 0; i < reread.Items.Count; i++)
            if (i != index)
                Assert.Equal(original.Items[i].OriginalItemId, reread.Items[i].ItemId);
    }

    [Theory]
    [MemberData(nameof(RoomFiles))]
    public void Room_EnemyModelEdit_SurvivesWriteReadCycle(string path)
    {
        var name = Path.GetFileName(path);
        var m = RoomPattern.Match(name);
        int stage = Convert.ToInt32(m.Groups[1].Value, 16);
        int room = Convert.ToInt32(m.Groups[2].Value, 16);

        var original = RoomFile.Read(stage, room, File.ReadAllBytes(path));

        // Need two enemy records to swap; rooms with fewer are skipped.
        if (original.Enemies.Count < 2) return;

        var a = original.Enemies[0];
        var b = original.Enemies[1];
        uint origAModel = a.ModelPtr, origAMotion = a.MotionPtr;

        // Give enemy[0] enemy[1]'s model/motion pair (a real, already-loaded pointer).
        a.ModelPtr = b.OriginalModelPtr;
        a.MotionPtr = b.OriginalMotionPtr;

        var rewritten = original.Write();
        // If the two happened to share a pair, no bytes change — that's fine, just stop.
        if (origAModel == b.OriginalModelPtr && origAMotion == b.OriginalMotionPtr) return;
        Assert.NotEqual(original.OriginalBytes, rewritten);

        var reread = RoomFile.Read(stage, room, rewritten);
        Assert.Equal(b.OriginalModelPtr, reread.Enemies[0].ModelPtr);
        Assert.Equal(b.OriginalMotionPtr, reread.Enemies[0].MotionPtr);
        // Every other enemy record is unchanged.
        for (int i = 1; i < reread.Enemies.Count; i++)
        {
            Assert.Equal(original.Enemies[i].OriginalModelPtr, reread.Enemies[i].ModelPtr);
            Assert.Equal(original.Enemies[i].OriginalMotionPtr, reread.Enemies[i].MotionPtr);
        }
    }

    [Theory]
    [MemberData(nameof(RoomFiles))]
    public void Room_EnemySpecies_DecodesAndMatchesCategory(string path)
    {
        var name = Path.GetFileName(path);
        var m = RoomPattern.Match(name);
        int stage = Convert.ToInt32(m.Groups[1].Value, 16);
        int room = Convert.ToInt32(m.Groups[2].Value, 16);

        var roomFile = RoomFile.Read(stage, room, File.ReadAllBytes(path));

        // Every placed enemy's model skeleton must decode to a known species, and that
        // model-side species must agree with the script's AI category byte — the validated
        // 1:1 skeleton↔category mapping (docs/dc1/STATIC-SCD-RE.md cont.14).
        foreach (var e in roomFile.Enemies)
        {
            Assert.NotEqual(DinoSpecies.Unknown, e.Species);
            Assert.True(e.SpeciesMatchesCategory,
                $"{name}: category {e.Category} != species {e.Species} ({e.SpeciesBoneCount} bones)");
        }
    }

    [Fact]
    public void Corpus_SkeletonTopology_IsOneToOneWithCategory()
    {
        // Globally: each AI category maps to exactly one bone count and vice versa, across the
        // whole install. (No-ops without DINORAND_DC1_DIR.)
        var catToBones = new Dictionary<byte, int>();
        var bonesToCat = new Dictionary<int, byte>();
        int total = 0;

        foreach (var args in RoomFiles())
        {
            var path = (string)args[0];
            var m = RoomPattern.Match(Path.GetFileName(path));
            int stage = Convert.ToInt32(m.Groups[1].Value, 16);
            int room = Convert.ToInt32(m.Groups[2].Value, 16);
            var roomFile = RoomFile.Read(stage, room, File.ReadAllBytes(path));

            foreach (var e in roomFile.Enemies)
            {
                total++;
                Assert.True(e.SpeciesBoneCount > 0);
                if (catToBones.TryGetValue(e.Category, out int b))
                    Assert.Equal(b, e.SpeciesBoneCount);
                else
                    catToBones[e.Category] = e.SpeciesBoneCount;

                if (bonesToCat.TryGetValue(e.SpeciesBoneCount, out byte c))
                    Assert.Equal(c, e.Category);
                else
                    bonesToCat[e.SpeciesBoneCount] = e.Category;
            }
        }

        if (total == 0) return; // no game files present
        Assert.Equal(catToBones.Count, bonesToCat.Count);
    }

    [Theory]
    [MemberData(nameof(RoomFiles))]
    public void Room_DoorEdit_SurvivesWriteReadCycle(string path)
    {
        var name = Path.GetFileName(path);
        var m = RoomPattern.Match(name);
        int stage = Convert.ToInt32(m.Groups[1].Value, 16);
        int room = Convert.ToInt32(m.Groups[2].Value, 16);

        var original = RoomFile.Read(stage, room, File.ReadAllBytes(path));

        // Pick a door to repoint + relock; rooms without one are skipped.
        var target = original.Doors.FirstOrDefault();
        if (target is null) return;

        int index = original.Doors.IndexOf(target);
        int newStage = target.TargetStage == 1 ? 2 : 1;
        int newRoom = (target.TargetRoom + 1) & 0xff;
        int newLock = target.LockId == 0x12 ? 0x2e : 0x12;
        target.TargetStage = newStage;
        target.TargetRoom = newRoom;
        target.LockId = newLock;

        var rewritten = original.Write();
        Assert.NotEqual(original.OriginalBytes, rewritten);

        var reread = RoomFile.Read(stage, room, rewritten);
        Assert.Equal(newStage, reread.Doors[index].TargetStage);
        Assert.Equal(newRoom, reread.Doors[index].TargetRoom);
        Assert.Equal(newLock, reread.Doors[index].LockId);
        // Every other door is unchanged (only the one record was edited).
        for (int i = 0; i < reread.Doors.Count; i++)
            if (i != index)
            {
                Assert.Equal(original.Doors[i].OriginalTargetStage, reread.Doors[i].TargetStage);
                Assert.Equal(original.Doors[i].OriginalTargetRoom, reread.Doors[i].TargetRoom);
                Assert.Equal(original.Doors[i].OriginalLockId, reread.Doors[i].LockId);
            }
    }

    /// Walk up from the test assembly to the repo root (the dir holding <c>english/</c> or
    /// <c>DinoRand.sln</c>) so the in-repo English data is found on any clone — never a
    /// hardcoded per-machine path. Returns null if not found (caller then no-ops).
    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "english")) ||
                File.Exists(Path.Combine(dir.FullName, "DinoRand.sln")))
                return dir.FullName;
        return null;
    }

    private static string? FindDataDir(string root)
    {
        foreach (var c in new[] { Path.Combine(root, "Data"), Path.Combine(root, "english", "Data") })
            if (HasRooms(c)) return c;
        if (Directory.Exists(root))
            foreach (var sub in Directory.EnumerateDirectories(root))
                if (HasRooms(Path.Combine(sub, "Data"))) return Path.Combine(sub, "Data");
        return null;
    }

    private static bool HasRooms(string dir) =>
        Directory.Exists(dir) &&
        Directory.EnumerateFiles(dir, "st*.dat").Any(f => RoomPattern.IsMatch(Path.GetFileName(f)));
}
