using System.Text.Json;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Source-data contract for <c>data/dc1/map.json</c> — the authoritative logic graph. Guards against the
/// exact class of drift that shipped once: the 0309 shuttle node-split lived in C# code, tests and docs
/// but was silently ABSENT from map.json for weeks, and nothing compared the shipped data to the authored
/// intent (GRAPH-LOGIC-PARITY-PLAN "parity contract"). Each construct below is asserted PRESENT in the
/// shipped map.json; a lost gate → RED.
///
/// <para>Deliberately reads map.json directly (no <c>DINORAND_DC1_DIR</c> install) so it runs on CI too —
/// the original loss slipped through precisely because CI has no game install, so an install-gated test
/// would not have caught it. The install-gated graph-stamping counterparts stay in
/// <see cref="KeyItemPlacerTests"/> (RealInstall_ElevatorDescent_*, RealInstall_HallB1ShuttleSplit_*).</para>
/// </summary>
public class Dc1MapContractTests
{
    private static JsonElement Rooms()
    {
        var path = FindRepoFile(Path.Combine("data", "dc1", "map.json"));
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("rooms").Clone();
    }

    private static string FindRepoFile(string relative)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DinoRand.sln"))) d = d.Parent;
        Assert.NotNull(d);
        var path = Path.Combine(d!.FullName, relative);
        Assert.True(File.Exists(path), $"repo file not found: {relative}");
        return path;
    }

    /// <summary>Locate a room by its hex code regardless of map.json's mixed casing ('030A' vs '030a').</summary>
    private static JsonElement Room(JsonElement rooms, int code)
    {
        foreach (var p in rooms.EnumerateObject())
            if (int.TryParse(p.Name, System.Globalization.NumberStyles.HexNumber, null, out var c) && c == code)
                return p.Value;
        Assert.Fail($"room {code:x4} absent from map.json");
        return default;
    }

    /// <summary>The door sub-object from <paramref name="from"/> to <paramref name="to"/> (hex-normalised keys).</summary>
    private static JsonElement Door(JsonElement rooms, int from, int to)
    {
        var doors = Room(rooms, from).GetProperty("doors");
        foreach (var p in doors.EnumerateObject())
            if (int.TryParse(p.Name, System.Globalization.NumberStyles.HexNumber, null, out var c) && c == to)
                return p.Value;
        Assert.Fail($"door {from:x4}->{to:x4} absent from map.json");
        return default;
    }

    private static int[] IntArray(JsonElement door, string field)
    {
        if (!door.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<int>();
        return arr.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
            ? int.Parse(e.GetString()!, System.Globalization.NumberStyles.HexNumber)
            : e.GetInt32()).ToArray();
    }

    private static void AssertDoorRequires(JsonElement rooms, int from, int to, int[] items, int[] visitRooms)
    {
        var d = Door(rooms, from, to);
        Assert.Equal(items.OrderBy(x => x), IntArray(d, "requires").OrderBy(x => x));
        Assert.Equal(visitRooms.OrderBy(x => x), IntArray(d, "requiresRoom").OrderBy(x => x));
    }

    // --- (A) The 0309 shuttle-car node-split (REGION-SCHEMA-PLAN §2). The drift that shipped. ------------

    [Fact]
    public void Map_HallB1_IsNodeSplit_WithBothDoorSets()
    {
        var r0309 = Room(Rooms(), 0x0309);
        Assert.True(r0309.TryGetProperty("nodeSplit", out var ns) && ns.ValueKind == JsonValueKind.True,
            "0309 lost its \"nodeSplit\": true — the generator-stairs / shuttle fence flattens (GRAPH-LOGIC-PARITY §8k)");

        var regions = r0309.GetProperty("regions");
        int[] Doors(string region) => IntArrayOf(regions.GetProperty(region).GetProperty("doors"));
        Assert.Equal(new[] { 0x0307, 0x030A, 0x030D }.OrderBy(x => x), Doors("west").OrderBy(x => x));
        Assert.Equal(new[] { 0x0306, 0x0113, 0x050B, 0x0604 }.OrderBy(x => x), Doors("shuttle").OrderBy(x => x));
    }

    private static int[] IntArrayOf(JsonElement arr) => arr.EnumerateArray()
        .Select(e => int.Parse(e.GetString()!, System.Globalization.NumberStyles.HexNumber)).ToArray();

    // --- (B) Facility-elevator descent gates (communication-room / requiresRoom model, cont.64). --------

    [Fact]
    public void Map_ElevatorDescent_GatedByForgeAndPowerRooms()
    {
        var rooms = Rooms();
        AssertDoorRequires(rooms, 0x0113, 0x0309, items: new[] { 0x41 }, visitRooms: new[] { 0x010B });
        AssertDoorRequires(rooms, 0x0113, 0x050B, items: Array.Empty<int>(), visitRooms: new[] { 0x0506 });
        AssertDoorRequires(rooms, 0x0113, 0x0604, items: Array.Empty<int>(), visitRooms: new[] { 0x0506 });
        AssertDoorRequires(rooms, 0x0405, 0x060F, items: Array.Empty<int>(), visitRooms: new[] { 0x030B });
        AssertDoorRequires(rooms, 0x060F, 0x030C, items: Array.Empty<int>(), visitRooms: new[] { 0x030B });
        // Large Elevator B3 stop also requires the heliport waypoint 0401 (030B alone is start-reachable
        // via the B1 Room Key, leaving a phantom bypass into the whole B3 endgame). cont. placement-gates.md.
        AssertDoorRequires(rooms, 0x060F, 0x0600, items: Array.Empty<int>(), visitRooms: new[] { 0x030B, 0x0401 });
    }

    // --- (C) The 7 DDK Input+Code disc PAIR gates (native op58-3 AND-gate, [[ddk-disc-relocation]]). -----

    [Theory]
    [InlineData(0x0107, 0x0113, 0x63, 0x6A)] // N
    [InlineData(0x0203, 0x0202, 0x62, 0x69)] // H
    [InlineData(0x0304, 0x0300, 0x65, 0x6C)] // E
    [InlineData(0x0309, 0x0306, 0x64, 0x6B)] // L
    [InlineData(0x0506, 0x0507, 0x67, 0x6E)] // S
    [InlineData(0x0507, 0x0508, 0x68, 0x6F)] // D
    [InlineData(0x0604, 0x0609, 0x66, 0x6D)] // W
    public void Map_DdkPairDoors_RequireBothDiscs(int from, int to, int input, int code)
        => AssertDoorRequires(Rooms(), from, to, items: new[] { input, code }, visitRooms: Array.Empty<int>());
}
