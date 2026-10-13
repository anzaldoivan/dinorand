using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The room data is the package entry the engine loads at <c>0x80100000</c>, which is <b>not</b> always
/// the last entry: 41 real DC1 files (stages 7/8/9/B/C and <c>st40d</c>) carry sound entries after it.
/// These tests pin the selector, the tail-preserving rewrite, and the fail-closed refusal of the
/// texture-insert path on such packages.
/// </summary>
public class RoomDataSelectionTests
{
    private static readonly SyntheticRoom.Item[] OneItem = { new(0x10, 1) };
    private static readonly SyntheticRoom.Door[] NoDoors = Array.Empty<SyntheticRoom.Door>();
    private static readonly SyntheticRoom.Enemy[] NoEnemies = Array.Empty<SyntheticRoom.Enemy>();

    private static byte[] MidRoom(GianEntryType rdtType = GianEntryType.Unknown)
        => SyntheticRoom.Dc1RoomWithTrailingEntries(OneItem, NoDoors, NoEnemies, rdtType);

    private static byte[] Payload(byte[] file, GianEntry e)
        => file.AsSpan(e.PayloadOffset, (int)e.DeclaredSize).ToArray();

    [Fact]
    public void TryParse_Dc1_ExposesHeaderLoadAddress()
    {
        var pkg = GianPackage.TryParse(MidRoom())!;

        Assert.Null(pkg.Entries[0].LoadAddress);                       // texture: x/y is a VRAM rect
        Assert.Equal(GianPackage.RoomDataLoadAddress, pkg.Entries[1].LoadAddress);
        Assert.Equal(SyntheticRoom.SoundTableBase, pkg.Entries[4].LoadAddress);
    }

    [Fact]
    public void RoomDataEntry_IsTheEntryLoadedAtTheRdtBase_NotTheLastEntry()
    {
        var pkg = GianPackage.TryParse(MidRoom())!;

        Assert.Equal(5, pkg.Entries.Count);
        Assert.Equal(1, pkg.RoomDataIndex);
        Assert.Equal(pkg.Entries[1].PayloadOffset, pkg.RoomDataEntry!.Value.PayloadOffset);
    }

    [Fact]
    public void RoomDataEntry_FallsBackToLastEntry_WhenNoEntryCarriesTheRdtBase()
    {
        var pkg = GianPackage.TryParse(SyntheticRoom.Dc1Room(OneItem, NoDoors, NoEnemies))!;
        Assert.Equal(pkg.Entries.Count - 1, pkg.RoomDataIndex);
    }

    [Fact]
    public void RoomDataEntry_Dc2_StaysTheLastEntry()
    {
        var pkg = GianPackage.TryParse(SyntheticRoom.Dc2Room(3))!;
        Assert.True(pkg.IsDc2);
        Assert.Equal(pkg.Entries.Count - 1, pkg.RoomDataIndex);
        Assert.All(pkg.Entries, e => Assert.Null(e.LoadAddress));
    }

    [Theory]
    [InlineData(GianEntryType.Unknown)]
    [InlineData(GianEntryType.Data)]
    public void Read_MidPackageRoomData_SurfacesItsRecords_AndWritesByteIdentical(GianEntryType rdtType)
    {
        var bytes = MidRoom(rdtType);
        var room = RoomFile.Read(7, 1, bytes);

        Assert.True(room.ParsedCleanly);
        Assert.Single(room.Items);
        Assert.Equal(0x10, room.Items[0].ItemId);
        Assert.Equal(bytes, room.Write());
    }

    [Theory]
    [InlineData(GianEntryType.Unknown)]
    [InlineData(GianEntryType.Data)]
    public void Write_EditedMidPackageRoomData_KeepsEveryTrailingEntry(GianEntryType rdtType)
    {
        var bytes = MidRoom(rdtType);
        var before = GianPackage.TryParse(bytes)!;
        var room = RoomFile.Read(7, 1, bytes);
        room.Items[0].ItemId = 0x16;

        var rewritten = room.Write();

        var after = GianPackage.TryParse(rewritten)!;
        Assert.Equal(before.Entries.Count, after.Entries.Count);
        Assert.Equal(1, after.RoomDataIndex);
        for (int i = 0; i < before.Entries.Count; i++)
        {
            Assert.Equal(before.Entries[i].Type, after.Entries[i].Type);
            Assert.Equal(before.Entries[i].LoadAddress, after.Entries[i].LoadAddress);
            if (i != 1) Assert.Equal(Payload(bytes, before.Entries[i]), Payload(rewritten, after.Entries[i]));
        }
        Assert.Equal(0x16, RoomFile.Read(7, 1, rewritten).Items[0].ItemId);
    }

    [Fact]
    public void WriteWithRdt_GrowingMidPackageRoomData_ShiftsTheTrailingEntriesIntact()
    {
        var bytes = MidRoom(GianEntryType.Data);
        var before = GianPackage.TryParse(bytes)!;
        var room = RoomFile.Read(7, 1, bytes);

        // Grow the raw room data past its sector so every later payload has to move.
        var grown = new byte[room.RdtBuffer.Length + 3 * GianPackage.SectorSize];
        room.RdtBuffer.CopyTo(grown, 0);
        var rewritten = room.WriteWithRdt(grown);

        var after = GianPackage.TryParse(rewritten)!;
        Assert.Equal(before.Entries.Count, after.Entries.Count);
        Assert.Equal((uint)grown.Length, after.Entries[1].DeclaredSize);
        for (int i = 2; i < before.Entries.Count; i++)
        {
            Assert.True(after.Entries[i].PayloadOffset > before.Entries[i].PayloadOffset);
            Assert.Equal(Payload(bytes, before.Entries[i]), Payload(rewritten, after.Entries[i]));
        }
        Assert.Equal(rewritten.Length,
            after.Entries[^1].PayloadOffset + after.Entries[^1].AlignedSize);
    }

    [Fact]
    public void InsertEntriesBeforeRdt_RefusesAPackageWhoseRoomDataIsNotLast()
    {
        var insert = new PackageRepacker.NewEntry(GianEntryType.Texture, new VramRect(1, 2, 3, 4), new byte[8]);
        var ex = Assert.Throws<InvalidOperationException>(
            () => PackageRepacker.InsertEntriesBeforeRdt(MidRoom(), new[] { insert }));
        Assert.Contains("not the last entry", ex.Message);
    }

    /// <summary>Every real room walks cleanly once the right entry is read (107 before the selector,
    /// 148 after). Falls back to the synthetic corpus without an install.</summary>
    [Theory]
    [MemberData(nameof(RoomFileRoundTripTests.RoomFiles), MemberType = typeof(RoomFileRoundTripTests))]
    public void Room_ScriptWalksCleanly(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        int stage = Convert.ToInt32(name.Substring(2, 1), 16);
        int room = Convert.ToInt32(name.Substring(3, 2), 16);

        var file = RoomFile.Read(stage, room, File.ReadAllBytes(path));

        Assert.True(file.ParsedCleanly, $"{name}: room script did not walk cleanly");
    }
}
