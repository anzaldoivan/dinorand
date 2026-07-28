using System.Buffers.Binary;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Output;
using DinoRand.Randomizer.Graph;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc1NativeRecoveryAidSuppressionTests
{
    private const int RoomCode = 0x0303;
    private const int RecoveryAidOffset = 0x4FA68;
    private const int PanelKey2Offset = RecoveryAidOffset + ItemRecord.Length;

    [Fact]
    public void Apply_PatchesExactlyTheRecoveryAidRecordAndLeavesPanelKey2Untouched()
    {
        var source = SourceRoom();

        var patched = Dc1NativeRecoveryAidSuppression.Apply(RoomCode, source);
        var before = RoomFile.Read(3, 3, source).RdtBuffer;
        var after = RoomFile.Read(3, 3, patched).RdtBuffer;

        Assert.Equal(new byte[ItemRecord.Length], after[RecoveryAidOffset..(RecoveryAidOffset + ItemRecord.Length)]);
        Assert.Equal(before[..RecoveryAidOffset], after[..RecoveryAidOffset]);
        Assert.Equal(before[(RecoveryAidOffset + ItemRecord.Length)..],
                     after[(RecoveryAidOffset + ItemRecord.Length)..]);
        Assert.Equal(0x3e, RoomScript.Parse(after).Items.Single(x => x.FileOffset == PanelKey2Offset).ItemId);
    }

    [Fact]
    public void Apply_RejectsSignatureMismatchWithoutMutatingInput()
    {
        var source = SourceRoom();
        var mismatched = (byte[])source.Clone();
        var room = RoomFile.Read(3, 3, mismatched);
        room.RdtBuffer[RecoveryAidOffset + 4]++;
        mismatched = room.WriteWithRdt(room.RdtBuffer);
        var before = mismatched.ToArray();

        Assert.Throws<InvalidDataException>(() => Dc1NativeRecoveryAidSuppression.Apply(RoomCode, mismatched));
        Assert.Equal(before, mismatched);
    }

    [Fact]
    public void Apply_IsIdempotentAndReparsesAsNoOps()
    {
        var once = Dc1NativeRecoveryAidSuppression.Apply(RoomCode, SourceRoom());
        var twice = Dc1NativeRecoveryAidSuppression.Apply(RoomCode, once);

        Assert.Equal(once, twice);
        var room = RoomFile.Read(3, 3, twice);
        Assert.DoesNotContain(room.Items, x => x.FileOffset == RecoveryAidOffset);
        Assert.Equal(0x3e, room.Items.Single(x => x.FileOffset == PanelKey2Offset).ItemId);
    }

    [Fact]
    public void GeneratedOutput_RejectsRecordDifferingOnlyInOtherwiseUnusedByte()
    {
        var source = SourceRoom();
        var outputRoom = RoomFile.Read(3, 3, source);
        outputRoom.RdtBuffer[RecoveryAidOffset + 1] = 0x7f;
        var output = outputRoom.WriteWithRdt(outputRoom.RdtBuffer);

        Assert.Throws<InvalidDataException>(() =>
            Dc1NativeRecoveryAidSuppression.ApplyGenerated(RoomCode, source, output));
    }

    [Fact]
    public void GeneratedFixtureWithoutExactSourceRecord_PassesThrough()
    {
        var fixtureRoom = RoomFile.Read(3, 3, SourceRoom());
        fixtureRoom.RdtBuffer[RecoveryAidOffset + 1] = 0x7f;
        var fixture = fixtureRoom.WriteWithRdt(fixtureRoom.RdtBuffer);

        Assert.Equal(fixture,
            Dc1NativeRecoveryAidSuppression.ApplyGenerated(RoomCode, fixture, fixture));
    }

    [Fact]
    public void StandaloneGeneratedMismatch_IsRefusedBeforePublish()
    {
        using var temp = new TempDirectory();
        var source = SourceRoom();
        var room = RoomFile.Read(3, 3, source);
        var changed = RoomFile.Read(3, 3, source);
        changed.RdtBuffer[RecoveryAidOffset + 1] = 0x7f;
        var context = new RandomizationContext(new DinoCrisis1(), new[] { room },
            RoomGraph.Build(new[] { room }), new Seed(1), new RandomizerConfig(), _ => { });
        context.SetRoomOutput(room, changed.WriteWithRdt(changed.RdtBuffer));

        Assert.Throws<InvalidDataException>(() => Dc1RunArtifactWriter.Write(temp.Path,
            new[] { new RoomFileRef(3, 3, "st33.dat") }, new[] { room }, RoomGraph.Build(new[] { room }),
            context, new Seed(1), new RandomizerConfig(), Array.Empty<string>(), false, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(temp.Path, "st33.dat")));
    }

    [Fact]
    public void RealGogSource_CompleteRecoveryAidRecordIsSuppressed()
    {
        var install = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (install is null || new DinoCrisis1().GetDataDir(install) is not { } dataDir) return;

        var source = File.ReadAllBytes(Path.Combine(dataDir, "st303.dat"));
        var output = Dc1NativeRecoveryAidSuppression.ApplyGenerated(RoomCode, source, source);
        var room = RoomFile.Read(3, 3, output);

        Assert.Equal(new byte[ItemRecord.Length],
            room.RdtBuffer[RecoveryAidOffset..(RecoveryAidOffset + ItemRecord.Length)]);
    }

    [Fact]
    public void StandaloneGeneratedRoomOutput_SuppressesRecoveryAid()
    {
        using var temp = new TempDirectory();
        var bytes = SourceRoom();
        var room = RoomFile.Read(3, 3, bytes);
        var context = new RandomizationContext(new DinoCrisis1(), new[] { room },
            RoomGraph.Build(new[] { room }), new Seed(1), new RandomizerConfig(), _ => { });

        Dc1RunArtifactWriter.Write(temp.Path, new[] { new RoomFileRef(3, 3, "st33.dat") },
            new[] { room }, RoomGraph.Build(new[] { room }), context, new Seed(1), new RandomizerConfig(), Array.Empty<string>(),
            false, CancellationToken.None);

        var written = File.ReadAllBytes(Path.Combine(temp.Path, "st33.dat"));
        var parsed = RoomFile.Read(3, 3, written);
        Assert.Equal(new byte[ItemRecord.Length],
            parsed.RdtBuffer[RecoveryAidOffset..(RecoveryAidOffset + ItemRecord.Length)]);
    }

    [Fact]
    public void ApGeneratedRoomOutput_SuppressesRecoveryAidBeforePublish()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.DataPath);
        var source = SourceRoom();
        File.WriteAllBytes(Path.Combine(temp.DataPath, "st303.dat"), source);
        var sourceRoom = RoomFile.Read(3, 3, source);
        var panel = sourceRoom.Items.Single(x => x.FileOffset == PanelKey2Offset);
        var patch = new ApPlacementInstaller.RecordPatch(
            "0303", PanelKey2Offset, Dc1ItemRecordClass.Pickup,
            panel.OriginalItemId, panel.OriginalAmount, panel.OriginalTakeIndex,
            0x2e, 1, 0x1234, Visual: null);

        var result = ApPlacementInstaller.WriteRooms(temp.DataPath, temp.OutPath, new[] { patch });
        var written = RoomFile.ReadFromFile(3, 3, Path.Combine(temp.OutPath, result.WrittenFiles.Single()));

        Assert.Equal(new byte[ItemRecord.Length],
            written.RdtBuffer[RecoveryAidOffset..(RecoveryAidOffset + ItemRecord.Length)]);
        Assert.Equal((0x2e, (ushort)0x1234),
            (written.Items.Single(x => x.FileOffset == PanelKey2Offset).ItemId,
             written.Items.Single(x => x.FileOffset == PanelKey2Offset).TakeIndex));
    }

    private static byte[] SourceRoom()
    {
        var rdt = new byte[PanelKey2Offset + ItemRecord.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(rdt.AsSpan(0x14), RoomScript.PsxRdtBase + 0x24u);
        BinaryPrimitives.WriteUInt32LittleEndian(rdt.AsSpan(0x24), 4);
        RecoveryAidRecord().CopyTo(rdt, RecoveryAidOffset);
        PanelKey2Record().CopyTo(rdt, PanelKey2Offset);
        return SyntheticRoom.Package(GianPackage.Dc1EntrySize,
            (GianEntryType.Texture, new byte[] { 0xdc, 0x1d, 0, 0 }),
            (GianEntryType.Data, rdt));
    }

    private static byte[] RecoveryAidRecord() => Convert.FromHexString(
        "28020400000200F8000800F8000800F2000200F2040200010000000021000100B30015000000188000000000");

    private static byte[] PanelKey2Record() => ItemRecordBytes(0x3e, 1, 179, 0, 0, ItemRecord.NoDisplaySlot, 0);

    private static byte[] ItemRecordBytes(byte id, ushort count, ushort take, short x, short z,
                                          byte displaySlot, uint model)
    {
        var record = new byte[ItemRecord.Length];
        record[0] = DcOpcodes.Item;
        record[2] = DcOpcodes.ItemSubtype;
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(4), x);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(8), z);
        record[ItemRecord.IdOffset] = id;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(ItemRecord.CountOffset), count);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(ItemRecord.TakeIndexOffset), take);
        record[ItemRecord.DisplaySlotOffset] = displaySlot;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(ItemRecord.ModelPtrOffset), model);
        return record;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dinorand-native-aid-{Guid.NewGuid():N}");
            DataPath = System.IO.Path.Combine(Path, "Data");
            OutPath = System.IO.Path.Combine(Path, "out");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string DataPath { get; }
        public string OutPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
