using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for the enemy pass: it permutes (model, motion) pointer pairs among a room's
/// same-category enemy records, never invents new resources, keeps swaps within a category, and
/// leaves scripted T-Rex rooms alone. No game files needed — rooms are built in memory.
/// </summary>
public class EnemyRandomizerTests
{
    // Bone count for each AI category (the validated 1:1 skeleton↔category map, cont.14): so a
    // fixture enemy decodes to a real DinoSpecies and passes EnemyRecord.IsRandomizableDino, exactly
    // as a record read from a real room would.
    private static int BonesFor(byte category) => category switch
    {
        1 => 15, 2 => 21, 3 => 20, 4 => 10, 5 => 7, 7 => 18, _ => 0,
    };

    private static EnemyRecord Enemy(byte category, uint model, uint motion) => new()
    {
        Category = category,
        ModelPtr = model,
        MotionPtr = motion,
        OriginalModelPtr = model,
        OriginalMotionPtr = motion,
        SpeciesBoneCount = BonesFor(category),
        FileOffset = 0,
    };

    private static void Run(RoomFile room, int seed = 1)
    {
        var rooms = new[] { room };
        var ctx = new RandomizationContext(
            new DinoCrisis1(), rooms, RoomGraph.Build(rooms), new Seed(seed),
            new RandomizerConfig(), _ => { });
        new EnemyRandomizer().Apply(ctx);
    }

    [Fact]
    public void Permute_WithinCategory_PreservesTheMultisetOfPairs()
    {
        var room = new RoomFile(5, 0x03); // st503: two distinct cat-1 raptors
        room.Enemies.Add(Enemy(0x01, 0x80124d28, 0x80146d10));
        room.Enemies.Add(Enemy(0x01, 0x80128868, 0x80148fb4));

        Run(room);

        var before = new[] { (0x80124d28u, 0x80146d10u), (0x80128868u, 0x80148fb4u) };
        var after = room.Enemies.Select(e => (e.ModelPtr, e.MotionPtr)).ToList();
        // Same set of pairs, just reassigned (no resource invented or lost).
        Assert.Equal(before.OrderBy(p => p.Item1), after.OrderBy(p => p.Item1));
        // Two distinct pairs over two slots → at least one record actually changed.
        Assert.Contains(room.Enemies, e => e.IsEdited);
    }

    [Fact]
    public void Permute_KeepsModelAndMotionPaired()
    {
        var room = new RoomFile(5, 0x03);
        room.Enemies.Add(Enemy(0x01, 0x80124d28, 0x80146d10));
        room.Enemies.Add(Enemy(0x01, 0x80128868, 0x80148fb4));

        Run(room);

        // Whatever model a slot ends up with, it carries that model's own motion.
        foreach (var e in room.Enemies)
        {
            uint expectedMotion = e.ModelPtr == 0x80124d28 ? 0x80146d10u : 0x80148fb4u;
            Assert.Equal(expectedMotion, e.MotionPtr);
        }
    }

    [Fact]
    public void HomogeneousCategory_IsLeftUnchanged()
    {
        var room = new RoomFile(4, 0x07); // st407: four identical Pteranodons (one shared model)
        for (int i = 0; i < 4; i++)
            room.Enemies.Add(Enemy(0x07, 0x80107f64, 0x8010abe4));

        Run(room);

        Assert.All(room.Enemies, e => Assert.False(e.IsEdited));
    }

    [Fact]
    public void DifferentCategories_AreNeverMixed()
    {
        var room = new RoomFile(2, 0x01); // st202-like layout but NOT a scripted room code
        room.Enemies.Add(Enemy(0x01, 0x80124d28, 0x80146d10)); // raptor A
        room.Enemies.Add(Enemy(0x01, 0x80128868, 0x80148fb4)); // raptor B
        var loner = Enemy(0x04, 0x8010f5d0, 0x8012cfc4);       // a T-Rex-class singleton
        room.Enemies.Add(loner);

        Run(room);

        // The cat-4 singleton has no same-category partner → untouched, and no cat-1 model
        // ever lands in it.
        Assert.False(loner.IsEdited);
        Assert.Equal(0x8010f5d0u, loner.ModelPtr);
        // The cat-1 pair only ever holds cat-1 models.
        var catOne = room.Enemies.Where(e => e.Category == 0x01).Select(e => e.ModelPtr).ToList();
        Assert.All(catOne, m => Assert.Contains(m, new[] { 0x80124d28u, 0x80128868u }));
    }

    [Fact]
    public void ScriptedTRexRoom_IsExcluded()
    {
        var room = new RoomFile(6, 0x10); // st610 = room code 0x610, final T-Rex battle
        room.Enemies.Add(Enemy(0x01, 0x80111244, 0x8011f780));
        room.Enemies.Add(Enemy(0x01, 0x8010d704, 0x8011d4dc));

        Run(room);

        Assert.All(room.Enemies, e => Assert.False(e.IsEdited));
    }

    [Fact]
    public void CutsceneRoom_IsExcluded()
    {
        // st10d = room code 0x10d, the Backyard opening scene. Its two distinct cat-1 entities are
        // raptors (cont.15), but they are choreographed by the intro cutscene, so the pass must leave
        // them — and the (model, motion) pair each holds — untouched.
        var room = new RoomFile(1, 0x0d);
        room.Enemies.Add(Enemy(0x01, 0x8011b43c, 0x801399b4)); // slot 8
        room.Enemies.Add(Enemy(0x01, 0x8011ef78, 0x8013bc58)); // slot 9

        Run(room);

        Assert.All(room.Enemies, e => Assert.False(e.IsEdited));
    }

    [Fact]
    public void NonDinosaurEntity_IsNeverPermuted()
    {
        // A 0x20 entity whose model does not decode as a dinosaur (here: no skeleton, so Species is
        // Unknown — standing in for the rig-sharing humanoid corpse / walk-desync garbage) shares a
        // room and AI category with two real raptors. It must never be drawn into the permutation,
        // and the raptors must still only ever hold raptor models.
        var room = new RoomFile(3, 0x05); // ordinary (non-scripted, non-cutscene) room code
        var nonDino = Enemy(0x01, 0x80abcdef, 0x80fedcba);
        nonDino.SpeciesBoneCount = 0; // does not decode → IsRandomizableDino == false
        room.Enemies.Add(nonDino);
        room.Enemies.Add(Enemy(0x01, 0x80124d28, 0x80146d10));
        room.Enemies.Add(Enemy(0x01, 0x80128868, 0x80148fb4));

        Run(room);

        Assert.False(nonDino.IsEdited);
        Assert.Equal(0x80abcdefu, nonDino.ModelPtr);
        var raptorModels = room.Enemies.Where(e => e != nonDino).Select(e => e.ModelPtr);
        Assert.All(raptorModels, m => Assert.Contains(m, new[] { 0x80124d28u, 0x80128868u }));
    }
}

/// <summary>
/// Integration tests over the real DC1 install (gated on <c>DINORAND_DC1_DIR</c>; no-op without it).
/// Run the enemy pass across the whole shipped game and assert the cont.15 guarantees: the Backyard
/// cutscene entities and the lone humanoid corpse are never permuted, and the pass only ever edits
/// records that decode as dinosaurs — while still doing real work somewhere.
/// </summary>
public class EnemyRandomizerRealInstallTests
{
    private static readonly DinoCrisis1 Game = new();

    private static List<RoomFile>? LoadInstall()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return null;
        return refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
    }

    private static List<RoomFile>? RunPass(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return null;
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(seed),
                                           new RandomizerConfig(), _ => { });
        new EnemyRandomizer().Apply(ctx);
        return rooms;
    }

    private static IEnumerable<EnemyRecord> RoomEnemies(IEnumerable<RoomFile> rooms, int code) =>
        rooms.Where(r => ((r.Stage & 0xff) << 8 | (r.Room & 0xff)) == code).SelectMany(r => r.Enemies);

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    public void RealInstall_BackyardCutsceneEntities_AreNeverPermuted(int seed)
    {
        var rooms = RunPass(seed);
        if (rooms is null) return; // no game files (CI) — skip

        // The Backyard (0x010d, the "Gail intro" scene) holds two distinct cat-1 raptor entities that
        // would otherwise be a valid permutation; the cutscene exclusion must keep every one untouched.
        var backyard = RoomEnemies(rooms, 0x010d).ToList();
        Assert.NotEmpty(backyard);
        Assert.All(backyard, e => Assert.False(e.IsEdited));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    public void RealInstall_OnlyDinosaurEntities_AreEverEdited(int seed)
    {
        var rooms = RunPass(seed);
        if (rooms is null) return;

        // Across the whole game, anything the pass edited must be a positively-decoded dinosaur — so
        // no rig-sharing humanoid (the st50c corpse) or walk-desync record is ever touched.
        var edited = rooms.SelectMany(r => r.Enemies).Where(e => e.IsEdited).ToList();
        Assert.NotEmpty(edited); // the pass still does real work somewhere
        Assert.All(edited, e =>
        {
            Assert.True(e.IsRandomizableDino);
            Assert.True(e.SpeciesBoneCount > 0);
        });
    }

    [Fact]
    public void RealInstall_HumanoidCorpse_IsNeverEdited()
    {
        var rooms = RunPass(1);
        if (rooms is null) return;

        // st50c (Power Freq. Room) holds the game's only non-dinosaur 0x20 entity — a "Researcher"
        // corpse that reuses the 15-bone raptor rig. As a singleton it must never be edited.
        var corpse = RoomEnemies(rooms, 0x050c).ToList();
        Assert.NotEmpty(corpse);
        Assert.All(corpse, e => Assert.False(e.IsEdited));
    }
}
