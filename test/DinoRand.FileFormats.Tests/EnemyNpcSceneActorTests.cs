using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Regression tests for the NPC-scene-actor exclusion (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.41).
/// A DC1 scene hub (e.g. 0106 "Control Room 1F", the Kirk/Gail rooms) places its NPC characters as
/// <c>0x20</c> records whose embedded skeleton is a vestigial <b>raptor</b> rig — so bone count / AI
/// category / bind-pose all mis-read them as a Velociraptor. The one file-static tell (PS1-confirmed)
/// is relational: within the room a single motion pointer is shared by ≥2 <i>distinct</i> model
/// pointers (two characters on one shared biped motion set). Neither the permute nor the HP pass may
/// touch such records.
/// </summary>
public class EnemyNpcSceneActorTests
{
    private static int BonesFor(byte category) => category switch
    {
        1 => 15, 2 => 21, 3 => 20, 4 => 10, 5 => 7, 7 => 18, 8 => 22, _ => 0,
    };

    private static EnemyRecord Enemy(byte category, uint model, uint motion, byte opcode = DcOpcodes.Enemy) => new()
    {
        Opcode = opcode,
        Category = category,
        ModelPtr = model,
        MotionPtr = motion,
        OriginalModelPtr = model,
        OriginalMotionPtr = motion,
        SpeciesBoneCount = BonesFor(category),
        FileOffset = 0,
    };

    // The 0106 shape: two cat-1 records sharing ONE motion with two DISTINCT models (byte-exact
    // from english/Data/st106.dat: models 0x8010ec6c / 0x8010b490, motion 0x8012b8bc).
    private static RoomFile ControlRoom1F()
    {
        var room = new RoomFile(1, 0x06);
        room.Enemies.Add(Enemy(0x01, 0x8010ec6c, 0x8012b8bc));
        room.Enemies.Add(Enemy(0x01, 0x8010b490, 0x8012b8bc));
        EnemyRecord.MarkNpcSceneActors(room.Enemies); // what RoomScript.Read does after parsing
        return room;
    }

    [Fact]
    public void MarkNpcSceneActors_FlagsSharedMotionDistinctModels()
    {
        var room = ControlRoom1F();
        Assert.All(room.Enemies, e => Assert.True(e.IsNpcSceneActor));
    }

    [Fact]
    public void NpcSceneActor_IsNotRandomizableDino()
    {
        var room = ControlRoom1F();
        // They still decode as a (bogus) Velociraptor — that is exactly the trap — but must be excluded.
        Assert.All(room.Enemies, e => Assert.Equal(DinoSpecies.Velociraptor, e.Species));
        Assert.All(room.Enemies, e => Assert.False(e.IsRandomizableDino));
    }

    [Fact]
    public void EnemyRandomizer_LeavesNpcSceneActorsUntouched()
    {
        var room = ControlRoom1F();
        var rooms = new[] { room };
        new EnemyRandomizer().Apply(new RandomizationContext(
            new DinoCrisis1(), rooms, RoomGraph.Build(rooms), new Seed(1),
            new RandomizerConfig { RandomizeEnemies = true }, _ => { }));

        Assert.All(room.Enemies, e => Assert.False(e.IsEdited));
        Assert.Equal(0x8010ec6cu, room.Enemies[0].ModelPtr);
        Assert.Equal(0x8010b490u, room.Enemies[1].ModelPtr);
    }

    [Fact]
    public void EnemyHpRandomizer_LeavesNpcSceneActorsUntouched()
    {
        var room = ControlRoom1F();
        var rooms = new[] { room };
        new EnemyHpRandomizer().Apply(new RandomizationContext(
            new DinoCrisis1(), rooms, RoomGraph.Build(rooms), new Seed(1),
            new RandomizerConfig { RandomizeEnemyHp = true, EnemyDifficulty = 0.5 }, _ => { }));

        Assert.All(room.Enemies, e => Assert.False(e.IsEdited));
        Assert.All(room.Enemies, e => Assert.Equal(0, e.MaxHp));
    }

    [Fact]
    public void DistinctMotions_StayRandomizable()
    {
        // Two genuine raptors, each with its OWN motion (the st503 shape) — NOT an NPC signature.
        var room = new RoomFile(5, 0x03);
        room.Enemies.Add(Enemy(0x01, 0x80124d28, 0x80146d10));
        room.Enemies.Add(Enemy(0x01, 0x80128868, 0x80148fb4));
        EnemyRecord.MarkNpcSceneActors(room.Enemies);

        Assert.All(room.Enemies, e => Assert.False(e.IsNpcSceneActor));
        Assert.All(room.Enemies, e => Assert.True(e.IsRandomizableDino));
    }

    [Fact]
    public void IdenticalRaptorsSharingBothPointers_StayRandomizable()
    {
        // Two identical raptors sharing BOTH model and motion (the st10e shape) — one distinct model,
        // so not the shared-motion/distinct-model signature.
        var room = new RoomFile(1, 0x0e);
        room.Enemies.Add(Enemy(0x01, 0x80110e70, 0x80119ca4));
        room.Enemies.Add(Enemy(0x01, 0x80110e70, 0x80119ca4));
        EnemyRecord.MarkNpcSceneActors(room.Enemies);

        Assert.All(room.Enemies, e => Assert.False(e.IsNpcSceneActor));
        Assert.All(room.Enemies, e => Assert.True(e.IsRandomizableDino));
    }
}

/// <summary>
/// Real-install gated (<c>DINORAND_DC1_DIR</c>) — drives the actual parse path (<c>RoomScript.Read</c>
/// auto-marks) across the shipped game. The six two-models-one-motion rooms (cont.41) each carry an NPC
/// duo (Rick/Gail/Kirk on a shared biped motion). Two are pure scene rooms (0106 "Control Room 1F" —
/// live-CE-confirmed Rick/Gail; 0612) with no raptors; the other four are <b>mixed</b> — the NPC pair
/// coexists with genuine raptors that keep their own motion and must stay randomizable. So the invariant
/// is per-record (NPC actors never edited), not per-room.
/// </summary>
public class EnemyNpcSceneActorRealInstallTests
{
    private static readonly DinoCrisis1 Game = new();
    // The shared-motion/distinct-model rooms and how many NPC-actor records each carries.
    private static readonly (int Code, int NpcActors)[] NpcSceneRooms =
        { (0x0106, 2), (0x030c, 2), (0x050e, 2), (0x050f, 2), (0x0609, 2), (0x0612, 2) };

    private static List<RoomFile>? LoadInstall()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return null;
        return refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
    }

    private static IEnumerable<EnemyRecord> RoomEnemies(IEnumerable<RoomFile> rooms, int code) =>
        rooms.Where(r => ((r.Stage & 0xff) << 8 | (r.Room & 0xff)) == code).SelectMany(r => r.Enemies);

    [Fact]
    public void RealInstall_NpcSceneRooms_HaveTheirNpcDuoFlagged()
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // CI / no game files — skip

        foreach (var (code, npcActors) in NpcSceneRooms)
        {
            var flagged = RoomEnemies(rooms, code).Where(e => e.IsNpcSceneActor).ToList();
            // The NPC duo (>= 2 distinct models on one motion) is present and never randomizable.
            Assert.True(flagged.Count >= npcActors, $"room {code:04X} should flag its NPC duo");
            Assert.All(flagged, e => Assert.False(e.IsRandomizableDino));
        }
        // 0106 is a PURE NPC room (Rick/Gail, no raptors): every record must be an NPC actor.
        var controlRoom = RoomEnemies(rooms, 0x0106).Where(e => e.Opcode == DcOpcodes.Enemy).ToList();
        Assert.Equal(2, controlRoom.Count);
        Assert.All(controlRoom, e => Assert.True(e.IsNpcSceneActor));

        // A mixed room (030C) must STILL keep its genuine raptors randomizable — the fix is surgical.
        Assert.Contains(RoomEnemies(rooms, 0x030c), e => e.IsRandomizableDino);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    public void RealInstall_NpcActors_AreNeverEditedByEitherPass(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(seed),
            new RandomizerConfig { RandomizeEnemies = true, RandomizeEnemyHp = true, EnemyDifficulty = 0.7 },
            _ => { });
        new EnemyRandomizer().Apply(ctx);
        new EnemyHpRandomizer().Apply(ctx);

        // No NPC-scene actor, anywhere in the game, is ever touched by either pass.
        Assert.All(rooms.SelectMany(r => r.Enemies).Where(e => e.IsNpcSceneActor),
                   e => Assert.False(e.IsEdited));
        // The passes must still do real work elsewhere (guard against a blanket no-op).
        Assert.Contains(rooms.SelectMany(r => r.Enemies), e => e.IsEdited);
    }
}
