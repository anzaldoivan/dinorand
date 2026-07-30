using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using System.Buffers.Binary;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc1RaptorVisualVariantTests
{
    private static Dc1RaptorVisualVariantEvidence BlueEvidence() => new(
        "blue",
        "RaptorHeavy",
        DinoSpecies.RaptorHeavy,
        2,
        21,
        5,
        0x11,
        TextureImporter.MakeClut(768, 511),
        "ST511 type-2 palette census; model category 2 / 21 bones");

    [Fact]
    public void Registry_ManualBlueEvidence_IsRaptorHeavyOnly_AndAutomaticSelectionIsOff()
    {
        var registry = new Dc1RaptorVisualVariantRegistry();
        registry.Register(BlueEvidence());

        Assert.False(Dc1RaptorVisualVariantRegistry.AutomaticSelectionEnabled);
        Assert.Equal("RaptorHeavy", registry.Get("blue").PaletteFamily);
        Assert.Throws<ArgumentException>(() => registry.Register(BlueEvidence() with
        {
            VariantId = "blue",
            Species = DinoSpecies.Pteranodon,
            Category = 7,
            BoneCount = 18,
        }));
    }

    [Fact]
    public void Authoring_Enemy2ExplicitBirthOrHp_IsUnsupportedAndNonMutating()
    {
        var enemy = new EnemyRecord
        {
            Opcode = DcOpcodes.Enemy2,
            Category = 2,
            SpeciesBoneCount = 21,
            MaxHp = 777,
            AiParam = 3,
            BirthMode = 2,
        };
        var before = (enemy.MaxHp, enemy.AiParam, enemy.BirthMode);

        var result = Dc1RaptorVisualVariantEditor.TryAuthor(
            enemy, new Dc1RaptorVisualVariantAuthoring(2222, 0, 1));

        Assert.Equal(Dc1RaptorVisualVariantOutcome.Unsupported, result.Outcome);
        Assert.Equal(before, (enemy.MaxHp, enemy.AiParam, enemy.BirthMode));
    }

    [Fact]
    public void Authoring_Enemy20_ChangesOnlyProvenSafeFields()
    {
        var enemy = new EnemyRecord
        {
            Opcode = DcOpcodes.Enemy,
            Category = 2,
            SpeciesBoneCount = 21,
            MaxHp = 777,
            AiParam = 3,
            BirthMode = 2,
        };

        var result = Dc1RaptorVisualVariantEditor.TryAuthor(
            enemy, new Dc1RaptorVisualVariantAuthoring(2222, 0, 1));

        Assert.Equal(Dc1RaptorVisualVariantOutcome.Applied, result.Outcome);
        Assert.Equal(2222, enemy.MaxHp);
        Assert.Equal(0, enemy.AiParam);
        Assert.Equal(1, enemy.BirthMode);
    }

    [Fact]
    public void Authoring_Enemy20_RoundTripsTheThreeProvenFields()
    {
        var room = RoomFile.Read(1, 2,
            SyntheticRoom.Dc1Room(Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(),
                new[] { new SyntheticRoom.Enemy(DcOpcodes.Enemy, 2, 21) }));
        var result = Dc1RaptorVisualVariantEditor.TryAuthor(
            room.Enemies.Single(), new Dc1RaptorVisualVariantAuthoring(2222, 4, 1));

        Assert.Equal(Dc1RaptorVisualVariantOutcome.Applied, result.Outcome);
        var reread = RoomFile.Read(1, 2, room.Write());
        var enemy = reread.Enemies.Single();
        Assert.Equal(2222, enemy.MaxHp);
        Assert.Equal(4, enemy.AiParam);
        Assert.Equal(1, enemy.BirthMode);
    }

    [Fact]
    public void PaletteApply_IsRaptorHeavyOnly_AndLeavesTargetRecordFieldsUntouched()
    {
        var evidence = BlueEvidence();
        var target = PackageWithPalette(0x11);
        var donor = PackageWithPalette(0x99);
        var enemy = new EnemyRecord
        {
            Opcode = DcOpcodes.Enemy2,
            Category = 2,
            SpeciesBoneCount = 21,
            MaxHp = 777,
            AiParam = 3,
            BirthMode = 2,
        };
        var before = (enemy.MaxHp, enemy.AiParam, enemy.BirthMode);

        var result = Dc1RaptorVisualVariantEditor.TryApplyPalette(target, donor, enemy, evidence);

        Assert.Equal(Dc1RaptorVisualVariantOutcome.Applied, result.Outcome);
        Assert.Equal(before, (enemy.MaxHp, enemy.AiParam, enemy.BirthMode));
        Assert.All(result.PatchedBytes![2048..(2048 + 512)], b => Assert.Equal(0x99, b));
        Assert.All(target[2048..(2048 + 512)], b => Assert.Equal(0x11, b));
    }

    private static byte[] PackageWithPalette(byte fill)
    {
        var bytes = new byte[4096];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), (uint)GianEntryType.Palette);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 768);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), 511);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), 256);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), (uint)GianEntryType.Data);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), 4);
        bytes.AsSpan(2048, 512).Fill(fill);
        return bytes;
    }

    [Fact]
    public void RngStream_IsStableAndSeparateFromOtherEnemyStreams()
    {
        var seed = new Seed(12345);
        var visual = Dc1RaptorVisualVariantRandom.RngFor(seed);
        var enemies = seed.RngFor("enemies");
        var crossSpecies = seed.RngFor("cross-species");
        var hp = seed.RngFor("enemy-hp");

        Assert.Equal(seed.RngFor(Dc1RaptorVisualVariantRandom.StreamName).Next(), visual.Next());
        Assert.NotEqual(enemies.Next(), seed.RngFor(Dc1RaptorVisualVariantRandom.StreamName).Next());
        Assert.NotEqual(crossSpecies.Next(), seed.RngFor(Dc1RaptorVisualVariantRandom.StreamName).Next());
        Assert.NotEqual(hp.Next(), seed.RngFor(Dc1RaptorVisualVariantRandom.StreamName).Next());
    }
}
