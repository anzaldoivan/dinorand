using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for the gated DC1 enemy-HP pass: it writes a seeded, difficulty-scaled maxHP into each
/// eligible <c>0x20</c> record's <c>+6</c> word, leaves <c>0x59</c> records and non-dinosaurs alone, is
/// deterministic per seed, and scales with <c>--difficulty</c>. No game files needed — rooms are in memory,
/// plus one end-to-end synthetic-file round-trip proving the HP lands at <c>+6</c> on disk.
/// </summary>
public class EnemyHpRandomizerTests
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

    private static void Run(RoomFile room, int seed = 1, double difficulty = 0.5)
    {
        var rooms = new[] { room };
        var ctx = new RandomizationContext(
            new DinoCrisis1(), rooms, RoomGraph.Build(rooms), new Seed(seed),
            new RandomizerConfig { RandomizeEnemyHp = true, EnemyDifficulty = difficulty }, _ => { });
        new EnemyHpRandomizer().Apply(ctx);
    }

    [Fact]
    public void Disabled_ByDefault()
    {
        Assert.False(new EnemyHpRandomizer().IsEnabled(new RandomizerConfig()));
        Assert.True(new EnemyHpRandomizer().IsEnabled(new RandomizerConfig { RandomizeEnemyHp = true }));
    }

    [Fact]
    public void SetsNonZeroMaxHp_OnEligibleDinos()
    {
        var room = new RoomFile(5, 0x03); // plain (non-scripted, non-cutscene) room
        room.Enemies.Add(Enemy(0x01, 0x80124d28, 0x80146d10));
        room.Enemies.Add(Enemy(0x02, 0x80128868, 0x80148fb4));

        Run(room, difficulty: 0.5); // band 680..2040

        Assert.All(room.Enemies, e =>
        {
            Assert.True(e.IsEdited);
            Assert.InRange((int)e.MaxHp, 680, 2040);
        });
    }

    [Fact]
    public void IsDeterministic_ForAFixedSeed()
    {
        var a = new RoomFile(5, 0x03);
        var b = new RoomFile(5, 0x03);
        for (int i = 0; i < 2; i++)
        {
            a.Enemies.Add(Enemy(0x01, 0x80124d28, 0x80146d10));
            b.Enemies.Add(Enemy(0x01, 0x80124d28, 0x80146d10));
        }

        Run(a, seed: 4242);
        Run(b, seed: 4242);

        Assert.Equal(a.Enemies.Select(e => e.MaxHp), b.Enemies.Select(e => e.MaxHp));
    }

    [Fact]
    public void Enemy2Records_AreNeverTouched()
    {
        // A 0x59 record's +6 is model-pointer bytes, so the HP pass must skip it entirely.
        var room = new RoomFile(5, 0x03);
        var theri = Enemy(0x08, 0x80130000, 0x80140000, DcOpcodes.Enemy2);
        room.Enemies.Add(theri);
        room.Enemies.Add(Enemy(0x01, 0x80124d28, 0x80146d10)); // an eligible 0x20 alongside it

        Run(room);

        Assert.False(theri.IsEdited);
        Assert.Equal(0, theri.MaxHp);
        Assert.Contains(room.Enemies, e => e.Opcode == DcOpcodes.Enemy && e.IsEdited);
    }

    [Fact]
    public void NonDinosaur_And_ScriptedAndCutsceneRooms_AreExcluded()
    {
        // Non-dino (undecodable) in a plain room: never edited.
        var plain = new RoomFile(3, 0x05);
        var nonDino = Enemy(0x01, 0x80abcdef, 0x80fedcba);
        nonDino.SpeciesBoneCount = 0; // IsRandomizableDino == false
        plain.Enemies.Add(nonDino);
        Run(plain);
        Assert.False(nonDino.IsEdited);
        Assert.Equal(0, nonDino.MaxHp);

        // Scripted T-Rex room (0x610) and cutscene room (0x10d) are skipped wholesale.
        var scripted = new RoomFile(6, 0x10);
        scripted.Enemies.Add(Enemy(0x01, 0x80111244, 0x8011f780));
        Run(scripted);
        Assert.All(scripted.Enemies, e => Assert.False(e.IsEdited));

        var cutscene = new RoomFile(1, 0x0d);
        cutscene.Enemies.Add(Enemy(0x01, 0x8011b43c, 0x801399b4));
        Run(cutscene);
        Assert.All(cutscene.Enemies, e => Assert.False(e.IsEdited));
    }

    [Fact]
    public void Difficulty_ScalesTheBand_Upward()
    {
        // diff 0 → band [340,680]; diff 1 → band [1020,3400] — disjoint, so every easy HP < every hard HP.
        var easy = new RoomFile(5, 0x03);
        var hard = new RoomFile(5, 0x03);
        for (int i = 0; i < 3; i++)
        {
            easy.Enemies.Add(Enemy(0x01, (uint)(0x80124d28 + i), 0x80146d10));
            hard.Enemies.Add(Enemy(0x01, (uint)(0x80124d28 + i), 0x80146d10));
        }

        Run(easy, difficulty: 0.0);
        Run(hard, difficulty: 1.0);

        int easyMax = easy.Enemies.Max(e => (int)e.MaxHp);
        int hardMin = hard.Enemies.Min(e => (int)e.MaxHp);
        Assert.True(easyMax < hardMin, $"easy max {easyMax} should be below hard min {hardMin}");
    }

    [Fact]
    public void MaxHp_SurvivesToTheFile_AtOffsetSix()
    {
        // End-to-end: run the pass on a synthetic room, write, re-read, and confirm the drawn HP is the
        // +6 word of each edited 0x20 record on disk.
        var enemies = new[]
        {
            new SyntheticRoom.Enemy(DcOpcodes.Enemy, 1, 15),
            new SyntheticRoom.Enemy(DcOpcodes.Enemy, 2, 21),
        };
        var raw = SyntheticRoom.Dc1Room(Array.Empty<SyntheticRoom.Item>(),
                                        Array.Empty<SyntheticRoom.Door>(), enemies);
        var room = RoomFile.Read(5, 0x03, raw);
        Assert.Equal(2, room.Enemies.Count);

        var rooms = new[] { room };
        var ctx = new RandomizationContext(
            new DinoCrisis1(), rooms, RoomGraph.Build(rooms), new Seed(7),
            new RandomizerConfig { RandomizeEnemyHp = true, EnemyDifficulty = 0.7 }, _ => { });
        new EnemyHpRandomizer().Apply(ctx);

        var expected = room.Enemies.Select(e => e.MaxHp).ToList();
        Assert.All(expected, hp => Assert.NotEqual(0, hp));

        var reread = RoomFile.Read(5, 0x03, room.Write());
        Assert.Equal(expected, reread.Enemies.Select(e => e.MaxHp));
        // And the raw +6 word matches, straight out of the re-read buffer.
        for (int i = 0; i < reread.Enemies.Count; i++)
        {
            int off = reread.Enemies[i].FileOffset + EnemyRecord.MaxHpOffset;
            ushort got = (ushort)(reread.RdtBuffer[off] | (reread.RdtBuffer[off + 1] << 8));
            Assert.Equal(expected[i], got);
        }
    }
}
