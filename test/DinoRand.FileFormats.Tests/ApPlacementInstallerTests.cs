using DinoRand.FileFormats.Stage;
using DinoRand.ApClient;
using DinoRand.Randomizer.Ap;
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

    [Fact]
    public void Coordinator_FailedPreflight_PreservesExistingInstalledSeed()
    {
        using var temp = new TempLayout();
        var source = temp.AddRoom("010d", 0x16, 6);
        var old = ApPlacementInstaller.WriteRooms(temp.DataDir, temp.OutDir,
            new[] { Patch("010d", source, placedId: 0x22) });
        GameInstaller.Install(temp.DataDir, temp.OutDir, "standalone", old.WrittenFiles);
        var installedBefore = File.ReadAllBytes(Path.Combine(temp.DataDir, "st10d.dat"));

        var checks = ChecksFor(source, expectedItemId: 0x99);
        var connection = Connection(0x23);

        Assert.Throws<InvalidDataException>(() => Dc1ApInstallCoordinator.Install(
            temp.DataDir, Path.Combine(temp.Root, "failed"), connection, checks));

        Assert.Equal(installedBefore, File.ReadAllBytes(Path.Combine(temp.DataDir, "st10d.dat")));
        Assert.True(GameInstaller.IsInstalled(temp.DataDir));
    }

    [Fact]
    public void Coordinator_Success_PreflightsRestoresOverlaysExactFilesAndPatchesOnlyExistingExe()
    {
        using var temp = new TempLayout();
        var source = temp.AddRoom("010d", 0x16, 6);
        var untouched = temp.AddRoom("010e", 0x1d, 2);
        var untouchedBytes = untouched.OriginalBytes.ToArray();
        var old = ApPlacementInstaller.WriteRooms(temp.DataDir, temp.OutDir,
            new[] { Patch("010d", source, placedId: 0x22), Patch("010e", untouched, placedId: 0x24) });
        GameInstaller.Install(temp.DataDir, temp.OutDir, "standalone", old.WrittenFiles);
        File.WriteAllBytes(Path.Combine(temp.Root, "DINO.exe"), Array.Empty<byte>());
        var outDir = Path.Combine(temp.Root, "new");
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "st10e.dat"), new byte[] { 0x7f });
        var patchObservedInstalled = false;

        var result = Dc1ApInstallCoordinator.Install(
            temp.DataDir, outDir, Connection(0x23), ChecksFor(source),
            applyPickupCancelPatch: (dataDir, seed) =>
            {
                patchObservedInstalled = GameInstaller.IsInstalled(dataDir);
            });

        Assert.Equal(new[] { "st10d.dat" }, result.Placement.WrittenFiles);
        Assert.True(result.PickupCancelPatchApplied);
        Assert.True(patchObservedInstalled);
        Assert.Equal(untouchedBytes, File.ReadAllBytes(Path.Combine(temp.DataDir, "st10e.dat")));
        var installed = Assert.Single(RoomFile.ReadFromFile(1, 0x0d,
            Path.Combine(temp.DataDir, "st10d.dat")).Items);
        Assert.Equal(0x23, installed.ItemId);
    }

    private static ApConnection Connection(int placedId) => new(
        "TEST", 1, 4, "060d",
        new Dictionary<long, int> { [123] = placedId },
        new Dictionary<long, int>(),
        new HashSet<long>());

    private static Dc1ClientChecks ChecksFor(RoomFile source, int expectedItemId = 0x16)
    {
        var item = Assert.Single(source.Items);
        return new Dc1ClientChecks
        {
            Locations = new[]
            {
                new Dc1ClientChecks.Entry
                {
                    Name = "Synthetic pickup",
                    ApId = 123,
                    Key = "synthetic",
                    LogicalId = "synthetic",
                    Room = "010d",
                    Class = "normal",
                    Predicate = new Dc1ClientChecks.Predicate
                    {
                        Kind = "any",
                        Group = 7,
                        AnyOf = new[] { 1 },
                    },
                    Records = new[]
                    {
                        new Dc1ClientChecks.RecordPlan
                        {
                            Room = "010d",
                            Rec = $"0x{item.FileOffset:x}",
                            ExpectedOpcode = Dc1ItemRecordClass.Pickup.Opcode,
                            ExpectedSubtype = Dc1ItemRecordClass.Pickup.Subtype,
                            ExpectedLength = Dc1ItemRecordClass.Pickup.Length,
                            VanillaItemId = expectedItemId,
                            VanillaAmount = item.OriginalAmount,
                            VanillaTake = item.OriginalTakeIndex,
                            Geometry = Array.Empty<int>(),
                            Take = item.OriginalTakeIndex,
                        },
                    },
                },
            },
        };
    }
}
