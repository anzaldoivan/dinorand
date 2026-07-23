using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Maps;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class PickupLocationContractTests
{
    private static ItemRecord Item(int id, int offset, short x, short z)
    {
        var raw = new byte[ItemRecord.Length];
        raw[0] = ItemRecord.Opcode;
        raw[2] = 4;
        raw[3] = ItemRecord.Length;
        BitConverter.GetBytes(x).CopyTo(raw, 4);
        BitConverter.GetBytes(z).CopyTo(raw, 6);
        return new ItemRecord
        {
            ItemId = id,
            OriginalItemId = id,
            Amount = 1,
            OriginalAmount = 1,
            FileOffset = offset,
            Raw = raw,
        };
    }

    private static RoomGraph Build(string roomJson, params ItemRecord[] items)
    {
        var room = new RoomFile(1, 0);
        room.Items.AddRange(items);
        return RoomGraph.Build(new[] { room }, MapRequirements.Parse(
            $$"""{ "rooms": { "0100": {{roomJson}} } }"""));
    }

    [Fact]
    public void SameIdAtDifferentPositions_RemainsDistinctWithoutExplicitGroup()
    {
        var graph = Build("{}", Item(0x1d, 0x100, 10, 20), Item(0x1d, 0x200, 30, 40));
        var locations = graph.Nodes.Single().Items.Select(x => x.Location).ToArray();

        Assert.Equal(2, locations.Select(x => x.PhysicalId).Distinct().Count());
        Assert.Equal(2, locations.Select(x => x.LogicalId).Distinct().Count());
    }

    [Fact]
    public void PositionCollision_DoesNotMergeRecordPolicy()
    {
        const string json = """
        {
          "itemPriorities": [
            { "at": "10,20", "records": ["0x100"], "priority": "Fixed" }
          ]
        }
        """;
        var graph = Build(json, Item(0x1d, 0x100, 10, 20), Item(0x20, 0x200, 10, 20));
        var items = graph.Nodes.Single().Items.OrderBy(x => x.Record.FileOffset).ToArray();

        Assert.Equal(ItemPriority.Fixed, items[0].Location.Priority);
        Assert.Equal(ItemPriority.Normal, items[1].Location.Priority);
    }

    [Fact]
    public void ExplicitMultiRecordGroup_HasOneLogicalIdentity()
    {
        const string json = """
        {
          "itemGroups": [
            { "id": "0100:test", "records": ["0x100", "0x200"] }
          ]
        }
        """;
        var graph = Build(json, Item(0x1d, 0x100, 10, 20), Item(0x1d, 0x200, 10, 20));
        var locations = graph.Nodes.Single().Items.Select(x => x.Location).ToArray();

        Assert.Single(locations.Select(x => x.LogicalId).Distinct());
        Assert.Equal(2, locations.Select(x => x.PhysicalId).Distinct().Count());
    }

    [Fact]
    public void ExplicitItemLink_DoesNotCaptureAnUnlistedSameIdRecord()
    {
        const string json = """
        {
          "itemLinks": [
            { "id": "1d", "records": ["0x100", "0x200"] }
          ]
        }
        """;
        var graph = Build(json,
            Item(0x1d, 0x100, 10, 20),
            Item(0x1d, 0x200, 30, 40),
            Item(0x1d, 0x300, 50, 60));
        var items = graph.Nodes.Single().Items.OrderBy(x => x.Record.FileOffset).ToArray();

        Assert.Equal("1d", items[0].Link);
        Assert.Equal("1d", items[1].Link);
        Assert.Null(items[2].Link);
    }

    [Fact]
    public void DefaultMap_Preserves030cConservativeCollisionPin()
    {
        var room = new RoomFile(3, 0x0c);
        room.Items.Add(Item(0x13, 0x49ba0, -7700, 6900));
        room.Items.Add(Item(0x35, 0x4c044, -7700, 6900));
        var graph = RoomGraph.Build(new[] { room }, MapRequirements.LoadDefault());

        Assert.All(graph.Nodes.Single().Items, x => Assert.Equal(ItemPriority.Fixed, x.Location.Priority));
        Assert.Equal(2, graph.Nodes.Single().Items.Select(x => x.Location.PhysicalId).Distinct().Count());
    }

    [Fact]
    public void DefaultMap_DoesNotSpreadGeneratedPinAcross0308CoordinateCollision()
    {
        var room = new RoomFile(3, 8);
        room.Items.Add(Item(0x23, 0x42b20, -1185, 1451));
        room.Items.Add(Item(0x1d, 0x42b4c, -1185, 1451));
        room.Items.Add(Item(0x1f, 0x42b7c, -1185, 1451));
        room.Items.Add(Item(0x1d, 0x42ba8, -1185, 1451));
        var graph = RoomGraph.Build(new[] { room }, MapRequirements.LoadDefault());
        var items = graph.Nodes.Single().Items.ToDictionary(item => item.Record.FileOffset);

        Assert.Equal(ItemPriority.Normal, items[0x42b20].Location.Priority);
        Assert.Equal(ItemPriority.Fixed, items[0x42b4c].Location.Priority);
        Assert.Equal(ItemPriority.Normal, items[0x42b7c].Location.Priority);
        Assert.Equal(ItemPriority.Fixed, items[0x42ba8].Location.Priority);
    }
}
