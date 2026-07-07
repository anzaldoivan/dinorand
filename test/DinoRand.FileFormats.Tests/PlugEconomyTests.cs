using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Tests for the Plug economy (ITEM-RANDO-PLAN.md §7.4): the reachable plug supply vs the plug demand of
/// reachable emergency boxes. Boxes are optional storage, so this is a visibility/guard layer, never a
/// beatability gate. Worlds are built in memory; demand only needs the reachable room set.
/// </summary>
public class PlugEconomyTests
{
    private static readonly DinoCrisis1 Game = new();
    private const int Plug = 0x2B;

    private static RoomFile RoomWith(int code, int plugs, int otherItems)
    {
        var room = new RoomFile((code >> 8) & 0xff, code & 0xff);
        int fo = 0;
        for (int i = 0; i < plugs; i++)
            room.Items.Add(new ItemRecord { ItemId = Plug, OriginalItemId = Plug, Amount = 1, FileOffset = fo++ });
        for (int i = 0; i < otherItems; i++)
            room.Items.Add(new ItemRecord { ItemId = 0x16, OriginalItemId = 0x16, Amount = 1, FileOffset = fo++ });
        return room;
    }

    [Fact]
    public void ReachablePlugSupply_CountsOnlyReachablePlugPickups()
    {
        // Two plugs in a reachable room, one plug in an unreachable room. Supply scoped to the reachable
        // set counts only the two; non-plug items are ignored.
        var inReach = RoomWith(0x0105, plugs: 2, otherItems: 5);
        var outReach = RoomWith(0x0503, plugs: 1, otherItems: 0);
        var graph = RoomGraph.Build(new[] { inReach, outReach });

        Assert.Equal(2, PlugEconomy.ReachablePlugSupply(graph, Game, new HashSet<int> { 0x0105 }));
        Assert.Equal(0, PlugEconomy.ReachablePlugSupply(graph, Game, new HashSet<int>()));
        Assert.Equal(3, PlugEconomy.ReachablePlugSupply(graph, Game, new HashSet<int> { 0x0105, 0x0503 }));
    }

    [Fact]
    public void ReachableBoxDemand_SumsReachableBoxPlugCosts()
    {
        // Control Room Hall (0x0105) is a single 1-plug box. Control Room B3 (0x0602) holds TWO boxes
        // (1 + 3 plugs), so a room with two boxes contributes both.
        Assert.Equal(1, PlugEconomy.ReachableBoxDemand(Game, new HashSet<int> { 0x0105 }));
        Assert.Equal(4, PlugEconomy.ReachableBoxDemand(Game, new HashSet<int> { 0x0602 }));
        Assert.Equal(0, PlugEconomy.ReachableBoxDemand(Game, new HashSet<int> { 0x0FFF }));

        // Every box room reachable ⇒ the full vanilla demand.
        var allBoxRooms = Game.EmergencyBoxes.Select(b => b.RoomCode).ToHashSet();
        Assert.Equal(26, PlugEconomy.ReachableBoxDemand(Game, allBoxRooms));
    }

    [Fact]
    public void Evaluate_ReportsBalance_AndMeetsDemandFlag()
    {
        var room = RoomWith(0x0105, plugs: 1, otherItems: 0);
        var graph = RoomGraph.Build(new[] { room });

        var enough = PlugEconomy.Evaluate(graph, Game, new HashSet<int> { 0x0105 });
        Assert.Equal(1, enough.Supply);   // one plug pickup present
        Assert.Equal(1, enough.Demand);   // the 1-plug Control Room Hall box
        Assert.True(enough.MeetsDemand);

        // Box reachable but no plug pickup in reach ⇒ demand outstrips supply.
        var short_ = PlugEconomy.Evaluate(graph, Game, new HashSet<int> { 0x0602 });
        Assert.Equal(0, short_.Supply);
        Assert.Equal(4, short_.Demand);
        Assert.False(short_.MeetsDemand);
    }
}
