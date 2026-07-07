using System.Text;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>The spawn-graph JSON loader: parses TYPE/SLOT operands per room and maps stage/room to the
/// <c>st_id</c> key.</summary>
public class Dc2SpawnGraphTests
{
    [Theory]
    [InlineData(2, 2, "202")]
    [InlineData(1, 2, "102")]
    [InlineData(4, 10, "40A")] // ST40A — hex room id
    [InlineData(0, 0, "000")]
    public void RoomKey_FormatsStIdAsHex(int stage, int room, string expected) =>
        Assert.Equal(expected, Dc2SpawnGraph.RoomKey(stage, room));

    [Fact]
    public void Parse_ReadsTypeModeOffsetAndSlot()
    {
        const string json = """
        { "rooms": {
            "202": { "spawns": [
              { "fields": { "TYPE": {"value": 2, "mode": 0, "value_off": 5692},
                            "SLOT": {"value": 5} } },
              { "fields": { "TYPE": {"value": 16, "mode": 0, "value_off": 6000},
                            "SLOT": {"value": 1} } } ] },
            "999": { "spawns": [] }
        } }
        """;
        var graph = Dc2SpawnGraph.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        var st202 = graph.ForRoom("202");
        Assert.NotNull(st202);
        Assert.Equal(2, st202!.Count);
        Assert.Equal(new Dc2SpawnRecord(2, 0, 5692, 5), st202[0]);
        Assert.Equal(new Dc2SpawnRecord(16, 0, 6000, 1), st202[1]);

        Assert.Empty(graph.ForRoom("999")!);
        Assert.Null(graph.ForRoom("nope"));
    }

    [Fact]
    public void LoadEmbedded_HasTheRaptorRooms()
    {
        // the real embedded graph: ST202 (18 hardcoded raptors) and ST102 are present.
        var graph = Dc2SpawnGraph.LoadEmbedded();
        Assert.NotNull(graph.ForRoom("202"));
        Assert.NotNull(graph.ForRoom("102"));
    }
}
