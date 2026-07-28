using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc1SuppressedPickupCorpusTests
{
    [Fact]
    public void RealInstall_PanelKey2AndRecoveryAidRemainDistinctWithSharedOriginalTakeIndex179()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrWhiteSpace(root)) return;

        var game = new DinoCrisis1();
        var panelRoom = game.EnumerateRooms(root).Single(reference => reference.Stage == 1 && reference.Room == 3);
        var aidRoom = game.EnumerateRooms(root).Single(reference => reference.Stage == 3 && reference.Room == 3);
        var panel = RoomFile.ReadFromFile(panelRoom.Stage, panelRoom.Room, panelRoom.Path).Items
            .Single(item => item.FileOffset == 0x1292c);
        var aid = RoomFile.ReadFromFile(aidRoom.Stage, aidRoom.Room, aidRoom.Path).Items
            .Single(item => item.FileOffset == 0x4fa68);

        Assert.Equal(0x3e, panel.OriginalItemId);
        Assert.Equal(0x21, aid.OriginalItemId);
        Assert.Equal((ushort)179, panel.OriginalTakeIndex);
        Assert.Equal((ushort)179, aid.OriginalTakeIndex);
        Assert.NotEqual(panel.FileOffset, aid.FileOffset);
    }
}
