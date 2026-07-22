using DinoRand.FileFormats.Stage;
using DinoRand.ApClient;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public class ApPlacementInstallerTests
{
    private sealed class TempLayout : IDisposable
    {
        public TempLayout()
        {
            Root = Path.Combine(Path.GetTempPath(), $"dinorand-ap-install-{Guid.NewGuid():N}");
            DataDir = Path.Combine(Root, "Data");
            OutDir = Path.Combine(Root, "out");
            Directory.CreateDirectory(DataDir);
        }

        public string Root { get; }
        public string DataDir { get; }
        public string OutDir { get; }

        public RoomFile AddRoom(string room, byte id, ushort amount)
        {
            int code = Convert.ToInt32(room, 16);
            int stage = code >> 8;
            int roomNo = code & 0xff;
            var bytes = SyntheticRoom.Dc1Room(
                new[] { new SyntheticRoom.Item(id, amount) },
                Array.Empty<SyntheticRoom.Door>(),
                Array.Empty<SyntheticRoom.Enemy>());
            File.WriteAllBytes(Path.Combine(DataDir, $"st{stage:x}{roomNo:x2}.dat"), bytes);
            return RoomFile.Read(stage, roomNo, bytes);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private static ApPlacementInstaller.RecordPatch Patch(
        string room, RoomFile source, byte placedId = 0x21, ushort take = 0x1234)
    {
        var item = Assert.Single(source.Items);
        return new ApPlacementInstaller.RecordPatch(
            room,
            item.FileOffset,
            Dc1ItemRecordClass.Pickup,
            item.OriginalItemId,
            item.OriginalAmount,
            item.OriginalTakeIndex,
            placedId,
            1,
            take,
            Visual: null);
    }

    [Fact]
    public void WriteRooms_StaleValidRecordAtExpectedOffset_IsRejected()
    {
        using var temp = new TempLayout();
        var source = temp.AddRoom("010d", 0x17, 6);
        var stalePlan = Patch("010d", source) with { ExpectedItemId = 0x16 };

        Assert.Throws<InvalidDataException>(
            () => ApPlacementInstaller.WriteRooms(temp.DataDir, temp.OutDir, new[] { stalePlan }));
        Assert.False(Directory.Exists(temp.OutDir));
    }

    [Fact]
    public void WriteRooms_DuplicateRecordPlans_AreRejected()
    {
        using var temp = new TempLayout();
        var source = temp.AddRoom("010d", 0x16, 6);
        var patch = Patch("010d", source);

        Assert.Throws<InvalidDataException>(
            () => ApPlacementInstaller.WriteRooms(temp.DataDir, temp.OutDir, new[] { patch, patch }));
        Assert.False(Directory.Exists(temp.OutDir));
    }

    [Fact]
    public void ExactWrittenFileList_PreventsStaleRoomInstallation()
    {
        using var temp = new TempLayout();
        var current = temp.AddRoom("010d", 0x16, 6);
        var untouched = temp.AddRoom("010e", 0x1d, 2);
        var untouchedBefore = untouched.OriginalBytes.ToArray();
        Directory.CreateDirectory(temp.OutDir);
        File.WriteAllBytes(Path.Combine(temp.OutDir, "st10e.dat"),
            SyntheticRoom.Dc1Room(
                new[] { new SyntheticRoom.Item(0x21, 99) },
                Array.Empty<SyntheticRoom.Door>(),
                Array.Empty<SyntheticRoom.Enemy>()));

        var result = ApPlacementInstaller.WriteRooms(
            temp.DataDir, temp.OutDir, new[] { Patch("010d", current) });
        GameInstaller.Install(temp.DataDir, temp.OutDir, "AP test", result.WrittenFiles);

        Assert.Equal(new[] { "st10d.dat" }, result.WrittenFiles);
        Assert.Equal(untouchedBefore, File.ReadAllBytes(Path.Combine(temp.DataDir, "st10e.dat")));
    }

    [Fact]
    public void WriteRooms_LaterInvalidRoom_PublishesNoCurrentArtifact()
    {
        using var temp = new TempLayout();
        var first = temp.AddRoom("010d", 0x16, 6);
        var second = temp.AddRoom("010e", 0x1d, 2);
        var invalid = Patch("010e", second) with { ExpectedAmount = 3 };

        Assert.Throws<InvalidDataException>(() => ApPlacementInstaller.WriteRooms(
            temp.DataDir, temp.OutDir, new[] { Patch("010d", first), invalid }));

        Assert.False(Directory.Exists(temp.OutDir));
    }

    [Fact]
    public void WriteRooms_ValidPlan_PreservesIdTakeBehaviorAndUsesOneUnit()
    {
        using var temp = new TempLayout();
        var source = temp.AddRoom("010d", 0x16, 6);

        var result = ApPlacementInstaller.WriteRooms(
            temp.DataDir, temp.OutDir, new[] { Patch("010d", source) });

        Assert.Equal(1, result.RoomsWritten);
        Assert.Equal(1, result.RecordsPatched);
        Assert.Equal(new[] { "st10d.dat" }, result.WrittenFiles);
        var written = RoomFile.ReadFromFile(1, 0x0d, Path.Combine(temp.OutDir, "st10d.dat"));
        var item = Assert.Single(written.Items);
        Assert.Equal((0x21, 1, 0x1234), (item.ItemId, item.Amount, item.TakeIndex));
    }

    [Fact]
    public void OneApConsumable_IsOneUnitForLocalAndRemoteDelivery()
    {
        using var temp = new TempLayout();
        var source = temp.AddRoom("010d", 0x16, 15);
        var patch = Patch("010d", source, placedId: 0x1d);
        ApPlacementInstaller.WriteRooms(temp.DataDir, temp.OutDir, new[] { patch });
        var local = Assert.Single(RoomFile.ReadFromFile(
            1, 0x0d, Path.Combine(temp.OutDir, "st10d.dat")).Items);

        var remote = Dc1GrantPlanner.Plan(
            new[] { new ReceivedGameItem(0, 0x1d, FromOwnSlot: false) }, -1,
            new Dc1InventorySnapshot(new byte[32], new byte[40], 10));
        var remoteWrite = Assert.Single(remote.Writes);

        Assert.Equal(1, local.Amount);
        Assert.Equal(1, remoteWrite.Bytes[1]);
    }
}
