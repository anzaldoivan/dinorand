using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public class Dc1ItemEditBatchTests
{
    private const int RoomCode = 0x010D;

    private static RoomFile Room(int roomCode = RoomCode, byte id = 0x16, ushort amount = 17)
    {
        var bytes = SyntheticRoom.Dc1Room(
            new[] { new SyntheticRoom.Item(id, amount) },
            Array.Empty<SyntheticRoom.Door>(),
            Array.Empty<SyntheticRoom.Enemy>());
        return RoomFile.Read(roomCode >> 8, roomCode & 0xff, bytes);
    }

    private static Dc1ItemEdit Edit(RoomFile room)
    {
        var item = Assert.Single(room.Items);
        return new Dc1ItemEdit(
            room.Stage << 8 | room.Room,
            item.FileOffset,
            Dc1ItemRecordClass.Pickup,
            item.OriginalItemId,
            item.OriginalAmount,
            item.OriginalTakeIndex,
            0x21,
            5,
            0x1234,
            new Dc1ItemVisualEdit(7, ItemRecord.GenericPanelModelPtr));
    }

    public static IEnumerable<object[]> InvalidExpectedValues()
    {
        var room = Room();
        var edit = Edit(room);
        yield return new object[] { room, edit with { ExpectedItemId = 0x17 } };
        yield return new object[] { room, edit with { ExpectedAmount = 18 } };
        yield return new object[] { room, edit with { ExpectedTakeIndex = 1 } };
        yield return new object[]
        {
            room,
            edit with { ExpectedClass = edit.ExpectedClass with { Opcode = 0x29 } },
        };
        yield return new object[] { room, edit with { RoomCode = 0x010E } };
    }

    [Theory]
    [MemberData(nameof(InvalidExpectedValues))]
    public void Prepare_WrongExpectedContract_Fails(RoomFile room, Dc1ItemEdit edit)
    {
        Assert.Throws<InvalidDataException>(() => Dc1ItemEditBatch.Prepare(new[] { room }, new[] { edit }));
    }

    [Fact]
    public void Prepare_DuplicatePhysicalTarget_Fails()
    {
        var room = Room();
        var edit = Edit(room);

        Assert.Throws<InvalidDataException>(
            () => Dc1ItemEditBatch.Prepare(new[] { room }, new[] { edit, edit }));
    }

    [Fact]
    public void Prepare_OverlappingPhysicalTargets_Fails()
    {
        var room = Room();
        var edit = Edit(room);
        var overlap = edit with { RecordOffset = edit.RecordOffset + 1 };

        Assert.Throws<InvalidDataException>(
            () => Dc1ItemEditBatch.Prepare(new[] { room }, new[] { edit, overlap }));
    }

    [Fact]
    public void Prepare_LaterInvalidRoom_ExposesNoEarlierOutputAndMutatesNoRoom()
    {
        var first = Room(0x010D);
        var second = Room(0x010E, id: 0x1D, amount: 2);
        var firstBefore = first.RdtBuffer.ToArray();
        var secondBefore = second.RdtBuffer.ToArray();
        var valid = Edit(first);
        var invalid = Edit(second) with { ExpectedItemId = 0x7F };

        Assert.Throws<InvalidDataException>(
            () => Dc1ItemEditBatch.Prepare(new[] { first, second }, new[] { valid, invalid }));

        Assert.Equal(firstBefore, first.RdtBuffer);
        Assert.Equal(secondBefore, second.RdtBuffer);
        Assert.Equal(first.OriginalBytes, first.Write());
        Assert.Equal(second.OriginalBytes, second.Write());
    }

    [Fact]
    public void Prepare_ValidEdit_WritesRequestedFieldsAndPreservesEveryOtherRdtByte()
    {
        var room = Room();
        var edit = Edit(room);
        var result = Dc1ItemEditBatch.Prepare(new[] { room }, new[] { edit });

        var bytes = Assert.Single(result.Rooms).Bytes;
        var reread = RoomFile.Read(room.Stage, room.Room, bytes);
        var actual = Assert.Single(reread.Items);
        Assert.Equal((0x21, 5, 0x1234), (actual.ItemId, actual.Amount, actual.TakeIndex));
        Assert.Equal(7, actual.DisplaySlot);

        var allowed = new HashSet<int>
        {
            edit.RecordOffset + ItemRecord.IdOffset,
            edit.RecordOffset + ItemRecord.CountOffset,
            edit.RecordOffset + ItemRecord.CountOffset + 1,
            edit.RecordOffset + ItemRecord.TakeIndexOffset,
            edit.RecordOffset + ItemRecord.TakeIndexOffset + 1,
            edit.RecordOffset + ItemRecord.DisplaySlotOffset,
            edit.RecordOffset + ItemRecord.ModelPtrOffset,
            edit.RecordOffset + ItemRecord.ModelPtrOffset + 1,
            edit.RecordOffset + ItemRecord.ModelPtrOffset + 2,
            edit.RecordOffset + ItemRecord.ModelPtrOffset + 3,
        };
        for (int i = 0; i < room.RdtBuffer.Length; i++)
        {
            if (allowed.Contains(i)) continue;
            Assert.Equal(room.RdtBuffer[i], reread.RdtBuffer[i]);
        }
    }
}
