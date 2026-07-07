using System.Globalization;
using System.Text.Json;
using DinoRand.Randomizer.Maps;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the DC1 room-data source-of-truth ownership model (see <c>data/dc1/README.md</c>).
/// <para>
/// <c>map.json</c> is the one engine-loaded overlay (door-rando metadata + the future
/// progression-logic <c>requires</c> overlay); <c>room-data.json</c> is the upstream research DB.
/// These guards keep the overlay and the upstream in sync so a name/floor edit in one without the
/// other fails the build, and they assert the two retired files (<c>rooms.json</c>,
/// <c>wiki-aliases.json</c>) stay gone with their data preserved upstream.
/// </para>
/// </summary>
public class RoomDataConsistencyTests
{
    private static readonly string[] AllowedFloors =
        { "B3", "B2", "B1", "1F", "2F", "Outdoors", "Unknown" };

    /// <summary>Room codes whose names <c>wiki-aliases.json</c> used to override before retirement.</summary>
    private static readonly string[] FormerlyAliasedCodes =
        { "0200", "0401", "0404", "0407", "050C", "0601" };

    /// <summary>Rooms documented upstream (<c>room-data.json</c>) but deliberately kept OUT of the
    /// engine overlay (<c>map.json</c>): the game's unused ST60E shell (an unfinished room), which must
    /// never be a door-shuffle target. The research DB keeps it for completeness; the door scope omits it.</summary>
    private static readonly string[] UnusedRoomsExcludedFromMap = { "060E" };

    private static int ParseCode(string ssrr) =>
        int.Parse(ssrr, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string DataPath(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "dc1", file);
        Assert.True(File.Exists(path), $"{file} not found at {path}");
        return path;
    }

    private static JsonDocument LoadDoc(string file) =>
        JsonDocument.Parse(File.ReadAllText(DataPath(file)));

    [Fact]
    public void MapJson_And_RoomDataJson_CoverTheSameRoomSet()
    {
        using var map = LoadDoc("map.json");
        using var rd = LoadDoc("room-data.json");

        var mapCodes = map.RootElement.GetProperty("rooms").EnumerateObject()
            .Select(p => ParseCode(p.Name)).ToHashSet();
        var rdCodes = rd.RootElement.GetProperty("rooms").EnumerateObject()
            .Select(p => ParseCode(p.Name)).ToHashSet();
        var excluded = UnusedRoomsExcludedFromMap.Select(ParseCode).ToHashSet();

        Assert.NotEmpty(mapCodes);
        // The overlay must add no rooms of its own, and may omit only the documented unused-room
        // exclusions (ST60E). So map ⊆ room-data, and the room-data-only remainder is exactly that set —
        // re-adding 060E to map.json (room-data-only becomes empty) or dropping any real room fails here.
        Assert.True(mapCodes.IsSubsetOf(rdCodes),
            "map.json must not contain rooms absent from room-data.json. " +
            $"map-only: [{string.Join(",", mapCodes.Except(rdCodes).Select(c => $"0x{c:X4}"))}]");
        Assert.True(excluded.SetEquals(rdCodes.Except(mapCodes)),
            "room-data.json minus map.json must equal exactly the documented unused-room exclusions " +
            $"([{string.Join(",", excluded.Select(c => $"0x{c:X4}"))}]). " +
            $"Actual room-data-only: [{string.Join(",", rdCodes.Except(mapCodes).Select(c => $"0x{c:X4}"))}]");
    }

    [Fact]
    public void MapJson_RoomNames_MatchRoomDataJson()
    {
        using var map = LoadDoc("map.json");
        using var rd = LoadDoc("room-data.json");
        var rdRooms = rd.RootElement.GetProperty("rooms");

        foreach (var room in map.RootElement.GetProperty("rooms").EnumerateObject())
        {
            if (!room.Value.TryGetProperty("name", out var mapName)) continue;
            Assert.True(rdRooms.TryGetProperty(room.Name, out var rdRoom),
                $"map.json room {room.Name} missing from room-data.json");
            Assert.Equal(rdRoom.GetProperty("name").GetString(), mapName.GetString());
        }
    }

    [Fact]
    public void MapJson_Floors_AreInTheAllowedSet()
    {
        using var map = LoadDoc("map.json");
        foreach (var room in map.RootElement.GetProperty("rooms").EnumerateObject())
        {
            if (!room.Value.TryGetProperty("floor", out var floor)) continue;
            Assert.Contains(floor.GetString(), AllowedFloors);
        }
    }

    [Fact]
    public void MapJson_BeginEndAndStaticTargets_ResolveToRealRooms()
    {
        using var map = LoadDoc("map.json");
        var rooms = map.RootElement.GetProperty("rooms");
        var codes = rooms.EnumerateObject().Select(p => ParseCode(p.Name)).ToHashSet();

        var be = map.RootElement.GetProperty("beginEnd");
        Assert.Contains(ParseCode(be.GetProperty("start").GetString()!), codes);
        Assert.Contains(ParseCode(be.GetProperty("end").GetString()!), codes);

        foreach (var room in rooms.EnumerateObject())
        {
            if (!room.Value.TryGetProperty("staticTargets", out var targets)) continue;
            foreach (var t in targets.EnumerateArray())
                Assert.Contains(ParseCode(t.GetString()!), codes);
        }
    }

    [Fact]
    public void DoorMap_Parse_CoversEveryMapJsonRoom()
    {
        using var map = LoadDoc("map.json");
        var jsonCodes = map.RootElement.GetProperty("rooms").EnumerateObject()
            .Select(p => ParseCode(p.Name)).ToHashSet();

        var doorMap = DoorMap.Parse(File.ReadAllText(DataPath("map.json")));
        Assert.True(jsonCodes.SetEquals(doorMap.RoomCodes.ToHashSet()),
            "DoorMap.Parse must surface exactly the map.json room set.");
    }

    [Fact]
    public void RetiredFiles_AreGone_AndTheirDataIsPreservedUpstream()
    {
        // rooms.json and wiki-aliases.json were retired into the owner-per-concern model.
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "data", "dc1", "rooms.json")),
            "rooms.json is retired (filesystem enumeration is authoritative) and must not be reintroduced.");
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "data", "dc1", "wiki-aliases.json")),
            "wiki-aliases.json is retired; its values live in room-data.json.wiki_title.");

        // The 6 formerly-aliased rooms must still carry their canonical wiki_title upstream.
        using var rd = LoadDoc("room-data.json");
        var rdRooms = rd.RootElement.GetProperty("rooms");
        foreach (var code in FormerlyAliasedCodes)
        {
            Assert.True(rdRooms.TryGetProperty(code, out var room),
                $"room {code} missing from room-data.json");
            Assert.True(room.TryGetProperty("wiki_title", out var wt)
                && !string.IsNullOrWhiteSpace(wt.GetString()),
                $"room-data.json {code} must keep a non-empty wiki_title (was the wiki-aliases source).");
        }
    }
}
