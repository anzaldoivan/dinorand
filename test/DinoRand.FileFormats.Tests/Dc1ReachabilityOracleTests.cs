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
/// <para>Install-gated on <c>DINORAND_DC1_DIR</c> (the oracle is derived from the room .dat door graph, which
/// the star-topology overlay alone cannot supply), like every other <c>RealInstall_*</c> test — CI without an
/// install regenerates nothing; the no-install oracle↔source consistency tripwire in <c>gen_ap_logic --check</c>
/// is the CI backstop. Regenerate after an intended change with
/// <c>DINORAND_UPDATE_ORACLE=1 dotnet test --filter Dc1ReachabilityOracleTests</c>, then stage the file.</para>
/// </summary>
public class Dc1ReachabilityOracleTests
{
    private static readonly DinoCrisis1 Game = new();

    [Fact]
    public void RealInstall_Oracle_MatchesEngine_ByteIdentical()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return; // no game files (CI) — skip; gen_ap_logic --check guards there

        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return;
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
                + "Dc1ReachabilityOracleTests. NOT the AP star model — see GRAPH-LOGIC-PARITY parity contract.");
            w.WriteNumber("version", 1);
            w.WriteString("startRoom", $"{Game.StartRoomCode:x4}");
            w.WriteString("goalRoom", $"{Game.GoalRoomCode:x4}");
            WriteHexArray(w, "gatingKeys", gatingKeys, "x2");
            WriteHexArray(w, "nodeSplitRooms", splitRooms, "x4");

            w.WriteStartArray("probes");
            WriteProbe(w, "empty", Array.Empty<int>(), Reach);
            foreach (var k in gatingKeys) WriteProbe(w, $"key-{k:x2}", new[] { k }, Reach);
            WriteProbe(w, "all", gatingKeys, Reach);
            foreach (var d in ddk) WriteProbe(w, $"all-minus-ddk-{d:x2}", gatingKeys.Where(x => x != d), Reach);
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
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

    private static string FindRepoFile(string relative)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DinoRand.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, relative);
    }
}
