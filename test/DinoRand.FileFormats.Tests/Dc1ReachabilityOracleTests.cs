using System.Text;
using System.Text.Json;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Gap C — a golden reachability oracle produced by the AUTHORITATIVE engine (KeyItemPlacer.Reachable over
/// RoomGraph.Build + the map.json overlay), snapshotted to <c>data/dc1/reachability-oracle.json</c>. This
/// test regenerates it from the engine and asserts byte-identical, so ANY drift in the engine's reachability
/// model — a lost gate, a removed node-split, a door-graph change — surfaces as a stale-oracle RED (removing
/// 0309's node-split flattens the descent-key probes → mismatch). It is the engine-side counterpart to the
/// source-side <see cref="Dc1MapContractTests"/> and the fail-closed <c>gen_ap_logic.py</c> tripwire.
///
/// <para>Install-gated on <c>DINORAND_DC1_DIR</c> (the oracle is derived from the room .dat door graph),
/// like every other <c>RealInstall_*</c> test — CI without an
/// install regenerates nothing; the no-install oracle↔source consistency tripwire in <c>gen_ap_logic --check</c>
/// is the CI backstop. Regenerate after an intended change with
/// <c>DINORAND_UPDATE_ORACLE=1 dotnet test --filter Dc1ReachabilityOracleTests</c>, then stage the file.</para>
/// </summary>
public class Dc1ReachabilityOracleTests
{
    private static readonly DinoCrisis1 Game = new();

    [Fact]
    public void CommittedOracle_ExportsInstallFreePhysicalNodesAndEdges()
    {
        var path = FindRepoFile(Path.Combine("data", "dc1", "reachability-oracle.json"));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.NotEmpty(root.GetProperty("nodes").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("edges").EnumerateArray());
        Assert.All(root.GetProperty("edges").EnumerateArray(), edge =>
        {
            Assert.True(edge.TryGetProperty("requiresAnyItems", out _));
            Assert.True(edge.TryGetProperty("requiresItems", out _));
            Assert.True(edge.TryGetProperty("requiresRooms", out _));
            Assert.True(edge.TryGetProperty("requiresLatch", out _));
            Assert.True(edge.TryGetProperty("setsLatch", out _));
        });
    }

    [Fact]
    public void CommittedOracle_0609HasOnlyTraversableIncomingSources_AndDdkWIsAndGated()
    {
        var path = FindRepoFile(Path.Combine("data", "dc1", "reachability-oracle.json"));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var incoming = doc.RootElement.GetProperty("edges").EnumerateArray()
            .Where(edge => edge.GetProperty("to").GetString() == "0609")
            .ToList();

        Assert.Equal(new[] { "0604", "060b" }, incoming
            .Select(edge => edge.GetProperty("from").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(source => source)
            .ToArray());
        Assert.DoesNotContain(incoming, edge => edge.GetProperty("from").GetString() is "0503" or "050f" or "0607");

        var ddkW = Assert.Single(incoming, edge => edge.GetProperty("from").GetString() == "0604");
        Assert.Equal(new[] { 0x66, 0x6d }, ddkW.GetProperty("requiresItems")
            .EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.Contains(incoming, edge => edge.GetProperty("from").GetString() == "060b");
    }

    [Fact]
    public void CommittedOracle_CoAreaKeyOnlyGatesCarryingOutRoomToRestStation()
    {
        var path = FindRepoFile(Path.Combine("data", "dc1", "reachability-oracle.json"));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var gatedEdges = doc.RootElement.GetProperty("edges").EnumerateArray()
            .Where(edge => edge.GetProperty("requiresAnyItems").EnumerateArray()
                               .Concat(edge.GetProperty("requiresItems").EnumerateArray())
                               .Any(item => item.GetInt32() == 0x31))
            .Select(edge => (
                From: edge.GetProperty("from").GetString()!,
                To: edge.GetProperty("to").GetString()!))
            .Distinct()
            .OrderBy(edge => edge.From, StringComparer.Ordinal)
            .ThenBy(edge => edge.To, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { (From: "0600", To: "0603") }, gatedEdges);
    }

    [Fact]
    public void CommittedOracle_KeyCardADoesNotGateGeneralWeaponsStorageRoute()
    {
        var path = FindRepoFile(Path.Combine("data", "dc1", "reachability-oracle.json"));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var edges = doc.RootElement.GetProperty("edges").EnumerateArray().ToArray();

        void AssertKeyCardA(string from, string to, bool expected)
        {
            var matching = edges.Where(edge => edge.GetProperty("from").GetString() == from
                                               && edge.GetProperty("to").GetString() == to)
                .ToArray();
            Assert.NotEmpty(matching);
            Assert.All(matching, edge =>
            {
                var requiresA = edge.GetProperty("requiresAnyItems").EnumerateArray()
                                    .Concat(edge.GetProperty("requiresItems").EnumerateArray())
                                    .Any(item => item.GetInt32() == 0x3A);
                Assert.Equal(expected, requiresA);
            });
        }

        foreach (var (from, to) in new[]
                 {
                     ("0602", "0605"), ("0602", "0615"),
                     ("0605", "0602"), ("0605", "0606"),
                     ("0615", "0602"), ("0615", "0606"),
                     ("0606", "0605"), ("0606", "0615"),
                 })
            AssertKeyCardA(from, to, expected: false);

        foreach (var (from, to) in new[]
                 {
                     ("0606", "0607"), ("0607", "0606"),
                     ("0606", "0611"), ("0611", "0606"),
                 })
            AssertKeyCardA(from, to, expected: true);
    }

    [Fact]
    public void RealInstall_Oracle_MatchesEngine_ByteIdentical()
    {
        bool required = Environment.GetEnvironmentVariable("DINORAND_REQUIRE_REAL_INSTALL") == "1";
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root))
        {
            Assert.False(required, "required DC1 oracle fixture variable DINORAND_DC1_DIR is missing");
            return; // no game files (CI) — skip; gen_ap_logic --check guards there
        }

        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0)
        {
            Assert.False(required, "required DC1 oracle fixture contains no recognized rooms");
            return;
        }
        var rooms = refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
        var expected = BuildOracleJson(rooms);

        var path = FindRepoFile(Path.Combine("data", "dc1", "reachability-oracle.json"));
        if (Environment.GetEnvironmentVariable("DINORAND_UPDATE_ORACLE") == "1")
        {
            File.WriteAllText(path, expected);
            return;
        }
        Assert.True(File.Exists(path), $"oracle missing — regenerate with DINORAND_UPDATE_ORACLE=1: {path}");
        Assert.Equal(expected, File.ReadAllText(path));
    }

    // --- Deterministic oracle builder (engine truth) -------------------------------------------------

    private static string BuildOracleJson(IReadOnlyList<RoomFile> rooms)
    {
        var graph = RoomGraph.Build(rooms, Game.Requirements);

        // Every item id that gates any edge: the door-TYPE key-set OR the edge's authored requires.
        var gating = new SortedSet<int>();
        foreach (var n in graph.Nodes)
            foreach (var e in n.Edges)
            {
                foreach (var k in Game.KeyItemsForDoor(e.Door.DoorType)) gating.Add(k);
                foreach (var k in e.Requires.Items ?? Array.Empty<int>()) gating.Add(k);
            }
        var gatingKeys = gating.ToList();
        var ddk = gatingKeys.Where(k => k is >= 0x62 and <= 0x6f).ToList();
        var splitRooms = Game.Requirements.NodeSplits.Keys.OrderBy(x => x).ToList();

        List<string> Reach(IEnumerable<int> held) =>
            KeyItemPlacer.Reachable(graph, Game, Game.StartRoomCode, new HashSet<int>(held))
                .OrderBy(x => x).Select(x => $"{x:x4}").ToList();

        var opts = new JsonWriterOptions { Indented = true };
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, opts))
        {
            w.WriteStartObject();
            w.WriteString("_generated_by", "Dc1ReachabilityOracleTests (engine: KeyItemPlacer.Reachable over "
                + "RoomGraph.Build(rooms, DinoCrisis1.Requirements) — the door .dat graph + map.json overlay)");
            w.WriteString("_source", "engine truth from the room .dat door graph (DINORAND_DC1_DIR) + "
                + "data/dc1/map.json; regenerate with DINORAND_UPDATE_ORACLE=1 dotnet test --filter "
                + "Dc1ReachabilityOracleTests. Export contains derived topology only, never room bytes.");
            w.WriteNumber("version", 2);
            w.WriteString("startRoom", $"{Game.StartRoomCode:x4}");
            w.WriteString("goalRoom", $"{Game.GoalRoomCode:x4}");
            WriteHexArray(w, "gatingKeys", gatingKeys, "x2");
            WriteHexArray(w, "nodeSplitRooms", splitRooms, "x4");
            WriteHexArray(w, "itemProtectedRooms", Game.ItemProtectedRoomCodes.OrderBy(value => value), "x4");
            WriteHexArray(w, "endingRooms", Game.EndingZoneRoomCodes.OrderBy(value => value), "x4");

            var orderedNodes = graph.Nodes.OrderBy(node => node.Code)
                .ThenBy(node => node.RegionIndex).ToList();
            var splitCodes = orderedNodes.GroupBy(node => node.Code)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            string NodeId(RoomNode node) => splitCodes.Contains(node.Code)
                ? $"{node.Code:x4}:{node.RegionName ?? node.RegionIndex.ToString()}"
                : $"{node.Code:x4}";

            w.WriteString("startNode", NodeId(orderedNodes.First(node =>
                node.Code == Game.StartRoomCode && node.RegionIndex == 0)));
            w.WriteString("goalNode", NodeId(orderedNodes.First(node =>
                node.Code == Game.GoalRoomCode && node.RegionIndex == 0)));
            w.WriteStartArray("nodes");
            foreach (var node in orderedNodes)
            {
                w.WriteStartObject();
                w.WriteString("id", NodeId(node));
                w.WriteString("room", $"{node.Code:x4}");
                w.WriteBoolean("primary", node.RegionIndex == 0);
                if (node.RegionName is null) w.WriteNull("region");
                else w.WriteString("region", node.RegionName);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("edges");
            foreach (var source in orderedNodes)
                foreach (var edge in source.Edges.OrderBy(edge => NodeId(edge.Target))
                             .ThenBy(edge => edge.Door.FileOffset))
                {
                    w.WriteStartObject();
                    w.WriteString("from", NodeId(source));
                    w.WriteString("to", NodeId(edge.Target));
                    if (edge.Door.GatesOnStoryLatch) w.WriteNumber("requiresLatch", edge.Door.LockId);
                    else w.WriteNull("requiresLatch");
                    if (edge.Door.SetsStoryLatch) w.WriteNumber("setsLatch", edge.Door.LockId);
                    else w.WriteNull("setsLatch");
                    WriteNumberArray(w, "requiresAnyItems",
                        Game.KeyItemsForDoor(edge.Door.DoorType).Distinct().OrderBy(value => value));
                    WriteNumberArray(w, "requiresItems",
                        (edge.Requires.Items ?? Array.Empty<int>())
                            .Concat(edge.Target.Requires.Items ?? Array.Empty<int>())
                            .Distinct().OrderBy(value => value));
                    WriteHexArray(w, "requiresRooms",
                        (edge.Requires.RoomsVisited ?? Array.Empty<int>())
                            .Concat(edge.Target.Requires.RoomsVisited ?? Array.Empty<int>())
                            .Distinct().OrderBy(value => value), "x4");
                    w.WriteEndObject();
                }
            w.WriteEndArray();

            w.WriteStartArray("probes");
            WriteProbe(w, "empty", Array.Empty<int>(), Reach);
            foreach (var k in gatingKeys) WriteProbe(w, $"key-{k:x2}", new[] { k }, Reach);
            WriteProbe(w, "all", gatingKeys, Reach);
            foreach (var d in ddk) WriteProbe(w, $"all-minus-ddk-{d:x2}", gatingKeys.Where(x => x != d), Reach);
            w.WriteEndArray();

            w.WriteEndObject();
        }
        // Utf8JsonWriter indents with Environment.NewLine on net8.0 — normalize so the oracle is
        // byte-identical across Windows and WSL.
        return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private static void WriteProbe(Utf8JsonWriter w, string name, IEnumerable<int> held,
                                   Func<IEnumerable<int>, List<string>> reach)
    {
        var h = held.ToList();
        w.WriteStartObject();
        w.WriteString("name", name);
        WriteHexArray(w, "held", h, "x2");
        var r = reach(h);
        w.WriteStartArray("reach");
        foreach (var c in r) w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteNumber("count", r.Count);
        w.WriteEndObject();
    }

    private static void WriteHexArray(Utf8JsonWriter w, string field, IEnumerable<int> ids, string fmt)
    {
        w.WriteStartArray(field);
        foreach (var i in ids) w.WriteStringValue(i.ToString(fmt));
        w.WriteEndArray();
    }

    private static void WriteNumberArray(Utf8JsonWriter w, string field, IEnumerable<int> values)
    {
        w.WriteStartArray(field);
        foreach (var value in values) w.WriteNumberValue(value);
        w.WriteEndArray();
    }

    private static string FindRepoFile(string relative)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DinoRand.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, relative);
    }
}
