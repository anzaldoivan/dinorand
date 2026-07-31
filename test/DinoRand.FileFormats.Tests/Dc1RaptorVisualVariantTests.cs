using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using System.Buffers.Binary;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc1RaptorVisualVariantTests
{
    public static IEnumerable<object[]> NonRaptorHeavySpecies() =>
        Enum.GetValues<DinoSpecies>()
            .Where(species => species != DinoSpecies.RaptorHeavy)
            .Select(species => new object[] { species });

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
    public void Authoring_Enemy2AiOnly_PatchesAiAndPreservesUnsupportedFields()
    {
        var enemy = new EnemyRecord
        {
            Opcode = DcOpcodes.Enemy2,
            Category = 2,
            SpeciesBoneCount = 21,
            MaxHp = 777,
            AiParam = 7,
            BirthMode = 2,
        };

        var result = Dc1RaptorVisualVariantEditor.TryAuthor(
            enemy, new Dc1RaptorVisualVariantAuthoring(AiParam: 6));

        Assert.Equal(Dc1RaptorVisualVariantOutcome.Applied, result.Outcome);
        Assert.Equal((ushort)777, enemy.MaxHp);
        Assert.Equal((byte)6, enemy.AiParam);
        Assert.Equal((byte)2, enemy.BirthMode);
    }

    [Fact]
    public void BlueProfile_IsDonorExactSt508_AndAuthoringIsAiOnly()
    {
        Assert.Equal((5, 0x08),
            (Dc1RaptorVisualVariantProfiles.Blue.DonorStage,
             Dc1RaptorVisualVariantProfiles.Blue.DonorRoom));
        Assert.Equal(TextureImporter.MakeClut(768, 511),
            Dc1RaptorVisualVariantProfiles.Blue.ClutCode);
        Assert.Equal(new Dc1RaptorVisualVariantAuthoring(AiParam: 6),
             Dc1RaptorVisualVariantProfiles.Blue.Authoring);
    }

    [Fact]
    public void BlueProfile_ComposesClutCopyAnd0x59AiWithoutChangingModelMotion()
    {
        var target = PackageWithPalette(0x11);
        var donor = PackageWithPalette(0x99);
        var enemy = new EnemyRecord
        {
            Opcode = DcOpcodes.Enemy2,
            Category = 2,
            SpeciesBoneCount = 21,
            ModelPtr = 0x80101234,
            MotionPtr = 0x80105678,
            InstanceIndex = 8,
            AiParam = 7,
            MaxHp = 850,
            BirthMode = 2,
        };

        var result = Dc1RaptorVisualVariantEditor.TryApplyProfile(
            target, donor, enemy, Dc1RaptorVisualVariantProfiles.Blue);

        Assert.Equal(Dc1RaptorVisualVariantOutcome.Applied, result.Outcome);
        Assert.Equal((byte)6, enemy.AiParam);
        Assert.Equal((uint)0x80101234, enemy.ModelPtr);
        Assert.Equal((uint)0x80105678, enemy.MotionPtr);
        Assert.Equal((byte)8, enemy.InstanceIndex);
        Assert.Equal((ushort)850, enemy.MaxHp);
        Assert.Equal((byte)2, enemy.BirthMode);
        Assert.All(result.PatchedBytes![2048..(2048 + 512)], b => Assert.Equal(0x99, b));
    }

    [Fact]
    public void RoomRoundTrip_0x59AiAuthoringChangesOnlyInlineAiByte()
    {
        var room = RoomFile.Read(1, 2,
            SyntheticRoom.Dc1Room(Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(),
                new[] { new SyntheticRoom.Enemy(DcOpcodes.Enemy2, 2, 21) }));
        var enemy = room.Enemies.Single();
        var before = enemy.Raw.ToArray();
        enemy.AiParam = 6;

        var reread = RoomFile.Read(1, 2, room.Write());

        Assert.Equal((byte)6, reread.Enemies.Single().AiParam);
        var after = reread.Enemies.Single().Raw;
        for (int i = 0; i < before.Length; i++)
            if (i != EnemyRecord.AiParamOffset)
                Assert.Equal(before[i], after[i]);
    }

    [Fact]
    public void Authoring_Pteranodon_IsUnsupportedAndNonMutating()
    {
        var enemy = new EnemyRecord
        {
            Opcode = DcOpcodes.Enemy,
            Category = 7,
            SpeciesBoneCount = 18,
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

    [Theory]
    [MemberData(nameof(NonRaptorHeavySpecies))]
    public void Authoring_EveryNonRaptorHeavySpecies_IsRejectedAndNonMutating(DinoSpecies species)
    {
        var enemy = EnemyFor(species);
        var before = Snapshot(enemy);
        var beforeRaw = enemy.Raw.ToArray();

        var result = Dc1RaptorVisualVariantEditor.TryAuthor(
            enemy, new Dc1RaptorVisualVariantAuthoring(2222, 0, 1));

        Assert.Equal(Dc1RaptorVisualVariantOutcome.Unsupported, result.Outcome);
        Assert.Equal(before, Snapshot(enemy));
        Assert.Equal(beforeRaw, enemy.Raw);
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

    [Theory]
    [MemberData(nameof(NonRaptorHeavySpecies))]
    public void PaletteApply_EveryNonRaptorHeavySpecies_IsRejectedAndNonMutating(DinoSpecies species)
    {
        var target = PackageWithPalette(0x11);
        var donor = PackageWithPalette(0x99);
        var beforeTarget = target.ToArray();
        var beforeDonor = donor.ToArray();
        var enemy = EnemyFor(species);
        var before = Snapshot(enemy);
        var beforeRaw = enemy.Raw.ToArray();

        var result = Dc1RaptorVisualVariantEditor.TryApplyPalette(
            target, donor, enemy, BlueEvidence());

        Assert.Equal(Dc1RaptorVisualVariantOutcome.Unsupported, result.Outcome);
        Assert.Null(result.PatchedBytes);
        Assert.Equal(beforeTarget, target);
        Assert.Equal(beforeDonor, donor);
        Assert.Equal(before, Snapshot(enemy));
        Assert.Equal(beforeRaw, enemy.Raw);
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

    private static EnemyRecord EnemyFor(DinoSpecies species)
    {
        var (category, bones) = species switch
        {
            DinoSpecies.Velociraptor => (1, 15),
            DinoSpecies.Tyrannosaurus => (3, 20),
            DinoSpecies.Swarm => (5, 7),
            DinoSpecies.Pteranodon => (7, 18),
            DinoSpecies.Therizinosaurus => (8, 22),
            DinoSpecies.Unknown => (0, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(species), species, null),
        };

        return new EnemyRecord
        {
            Opcode = DcOpcodes.Enemy,
            InstanceIndex = 0x12,
            Slot = 0x34,
            PosX = -101,
            PosY = 202,
            PosZ = -303,
            Rotation = 404,
            Category = (byte)category,
            KillFlag = 0x56,
            ModelPtr = 0x80101234,
            MotionPtr = 0x80105678,
            OriginalModelPtr = 0x80102222,
            OriginalMotionPtr = 0x80106666,
            MaxHp = 777,
            AiParam = 3,
            OriginalAiParam = 4,
            BirthMode = 2,
            OriginalBirthMode = 5,
            OriginalMaxHp = 888,
            SpeciesBoneCount = bones,
            IsNpcSceneActor = true,
            Raw = new byte[] { 1, 2, 3, 4 },
            FileOffset = 0x1234,
        };
    }

    private static EnemyState Snapshot(EnemyRecord enemy) => new(
        enemy.Opcode,
        enemy.InstanceIndex,
        enemy.Slot,
        enemy.PosX,
        enemy.PosY,
        enemy.PosZ,
        enemy.Rotation,
        enemy.Category,
        enemy.KillFlag,
        enemy.ModelPtr,
        enemy.MotionPtr,
        enemy.OriginalModelPtr,
        enemy.OriginalMotionPtr,
        enemy.MaxHp,
        enemy.AiParam,
        enemy.OriginalAiParam,
        enemy.BirthMode,
        enemy.OriginalBirthMode,
        enemy.OriginalMaxHp,
        enemy.SpeciesBoneCount,
        enemy.IsNpcSceneActor,
        enemy.FileOffset);

    private readonly record struct EnemyState(
        byte Opcode,
        byte InstanceIndex,
        byte Slot,
        short PosX,
        short PosY,
        short PosZ,
        short Rotation,
        byte Category,
        byte KillFlag,
        uint ModelPtr,
        uint MotionPtr,
        uint OriginalModelPtr,
        uint OriginalMotionPtr,
        ushort MaxHp,
        byte AiParam,
        byte OriginalAiParam,
        byte BirthMode,
        byte OriginalBirthMode,
        ushort OriginalMaxHp,
        int SpeciesBoneCount,
        bool IsNpcSceneActor,
        int FileOffset);

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
