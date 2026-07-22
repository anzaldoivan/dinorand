using System.Reflection;
using System.Text.Json;
using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2;

/// <summary>The validated, embedded DC2 item/AP contract consumed by both standalone and static AP installs.</summary>
public sealed class Dc2ItemData
{
    private const string CatalogResource = "DinoRand.Randomizer.Data.dc2.items.json";
    private const string SourcesResource = "DinoRand.Randomizer.Data.dc2.item-sources.json";
    private const string LogicResource = "DinoRand.Randomizer.Data.dc2.dc2_logic.json";
    private static readonly Lazy<Dc2ItemData> Embedded = new(LoadAndValidate);

    private Dc2ItemData(
        int version,
        string startRoomId,
        string goalRoomId,
        IReadOnlyDictionary<int, string> catalog,
        IReadOnlyList<Dc2ItemLocation> locations,
        IReadOnlyList<Dc2LogicEdge> edges,
        IReadOnlyList<string> dispositions,
        IReadOnlySet<int> progressionItemIds,
        IReadOnlySet<int> fixedLifecycleItemIds)
    {
        Version = version;
        StartRoomId = startRoomId;
        GoalRoomId = goalRoomId;
        Catalog = catalog;
        Locations = locations;
        Edges = edges;
        ConditionalCommitDispositions = dispositions;
        ProgressionItemIds = progressionItemIds;
        FixedLifecycleItemIds = fixedLifecycleItemIds;
    }

    public int Version { get; }
    public string StartRoomId { get; }
    public string GoalRoomId { get; }
    public IReadOnlyDictionary<int, string> Catalog { get; }
    public IReadOnlyList<Dc2ItemLocation> Locations { get; }
    public IReadOnlyList<Dc2LogicEdge> Edges { get; }
    public IReadOnlyList<string> ConditionalCommitDispositions { get; }
    public IReadOnlySet<int> ProgressionItemIds { get; }
    public IReadOnlySet<int> FixedLifecycleItemIds { get; }

    public static Dc2ItemData LoadEmbedded() => Embedded.Value;

    private static Dc2ItemData LoadAndValidate()
    {
        using JsonDocument catalogJson = Open(CatalogResource);
        using JsonDocument sourcesJson = Open(SourcesResource);
        using JsonDocument logicJson = Open(LogicResource);

        Require(sourcesJson.RootElement.GetProperty("schema_version").GetInt32() == 2,
            "item-sources.json schema v2 is required");

        var catalog = catalogJson.RootElement.GetProperty("catalog").EnumerateArray()
            .ToDictionary(
                row => ParseHex(row.GetProperty("id").GetString()!),
                row => row.GetProperty("name").ValueKind == JsonValueKind.Null
                    ? $"Item {row.GetProperty("id").GetString()}"
                    : row.GetProperty("name").GetString()!);
        Require(catalog.Count == 0x3a && catalog.Keys.Order().SequenceEqual(Enumerable.Range(0, 0x3a)),
            "items.json catalog must contain each id 0x00-0x39 exactly once");

        var sites = new Dictionary<string, Dc2ItemEditor.ItemSiteSpec>(StringComparer.Ordinal);
        var clearedOwnershipIds = new HashSet<int>();
        foreach (JsonElement source in sourcesJson.RootElement.GetProperty("sources").EnumerateArray())
        {
            string sourceType = source.GetProperty("source_type").GetString()!;
            if (sourceType == "group9_clear" && source.TryGetProperty("catalog_id", out JsonElement clearId)
                && clearId.ValueKind == JsonValueKind.String)
                clearedOwnershipIds.Add(ParseHex(clearId.GetString()!));
            if (!source.TryGetProperty("eligible_item_rewrite", out JsonElement eligible) || !eligible.GetBoolean())
                continue;
            string recordClass = source.GetProperty("record_class").GetString()!;
            if (recordClass is not ("op35_take" or "op2c_give"))
                continue;
            if (source.GetProperty("rewrite_class").GetString() == "health")
                Require(source.GetProperty("grant_quantity").GetInt32() == 1,
                    $"{source.GetProperty("source_id").GetString()} health grant quantity must remain one");
            var provenance = source.GetProperty("provenance");
            var site = new Dc2ItemEditor.ItemSiteSpec(
                source.GetProperty("source_id").GetString()!,
                provenance.GetProperty("room").GetString()!,
                provenance.GetProperty("routine_ordinal").GetInt32(),
                provenance.GetProperty("vm_directory_index").GetInt32(),
                provenance.GetProperty("vm_directory_indices").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                provenance.GetProperty("routine_start").GetInt32(),
                provenance.GetProperty("op_blob_off").GetInt32(),
                checked((byte)ParseHex(provenance.GetProperty("opcode").GetString()!)),
                recordClass == "op35_take"
                    ? Dc2ItemEditor.ItemRecordClass.Op35Take
                    : Dc2ItemEditor.ItemRecordClass.Op2cGive,
                ParseHex(source.GetProperty("catalog_id").GetString()!),
                ParseRewriteClass(source.GetProperty("rewrite_class").GetString()!),
                ParsePin(source.GetProperty("item_operand")),
                ParsePin(source.GetProperty("p3_operand")),
                ParsePin(source.GetProperty("flag5_operand")),
                ParsePin(source.GetProperty(recordClass == "op35_take" ? "cleanup_secondary_operand" : "cleanup_operand")),
                recordClass == "op2c_give" ? ParsePin(source.GetProperty("kind_operand")) : null,
                recordClass == "op35_take" ? ParsePin(source.GetProperty("slot_operand")) : null);
            Require(sites.TryAdd(site.SourceId, site), $"duplicate item source id {site.SourceId}");
        }
        Require(sites.Count == 51, $"item-sources.json must expose 51 writer-eligible sites, found {sites.Count}");

        JsonElement logic = logicJson.RootElement;
        int version = logic.GetProperty("version").GetInt32();
        Require(version == 2, $"unsupported dc2_logic version {version}");
        string start = "ST" + logic.GetProperty("startRoom").GetString();
        string goal = "ST" + logic.GetProperty("goalRoom").GetString();
        Require(start == "ST101" && goal == "ST504", "dc2_logic start/goal contract changed");

        var locations = new List<Dc2ItemLocation>();
        foreach (JsonElement row in logic.GetProperty("locations").EnumerateArray())
        {
            string sourceId = row.GetProperty("sourceId").GetString()!;
            if (!sites.TryGetValue(sourceId, out var site))
                throw new InvalidOperationException(
                    $"invalid embedded DC2 item contract: AP location {sourceId} has no pinned writer site");
            int itemId = row.GetProperty("itemId").GetInt32();
            if (!catalog.TryGetValue(itemId, out string? catalogName))
                throw new InvalidOperationException(
                    $"invalid embedded DC2 item contract: AP location {sourceId} has unknown catalog id");
            Require(site.ExpectedItemId == itemId, $"AP location {sourceId} disagrees with item-sources.json");
            Require(site.RewriteClass == ParseRewriteClass(row.GetProperty("rewriteClass").GetString()!),
                $"AP location {sourceId} rewrite class disagrees with item-sources.json");
            string room = row.GetProperty("room").GetString()!;
            Require(site.RoomId == "ST" + room, $"AP location {sourceId} room disagrees with its writer site");
            locations.Add(new Dc2ItemLocation(
                sourceId,
                row.GetProperty("apId").GetInt64(),
                row.GetProperty("name").GetString()!,
                site.RoomId,
                ParseRoomCode(room),
                itemId,
                catalogName,
                row.GetProperty("sourceType").GetString()!,
                site.RewriteClass,
                row.GetProperty("placementClass").GetString()!,
                ParseInts(row.GetProperty("requiresItems")),
                ParseRooms(row.GetProperty("requiresRooms")),
                site));
        }
        Require(locations.Count == 42, $"dc2_logic must expose exactly 42 fillable locations, found {locations.Count}");
        Require(locations.Select(x => x.ApId).Distinct().Count() == locations.Count, "duplicate DC2 AP location id");
        Require(locations.Select(x => x.SourceId).Distinct(StringComparer.Ordinal).Count() == locations.Count,
            "duplicate DC2 AP source identity");
        Require(locations.All(x => x.SourceType is "op35_take" or "op2c_give"),
            "SAT-1 or another unsupported record class entered the writer surface");

        int[] pool = logic.GetProperty("items").GetProperty("poolItemIds").EnumerateArray()
            .Select(x => x.GetInt32()).Order().ToArray();
        Require(pool.SequenceEqual(locations.Select(x => x.ItemId).Order()),
            "DC2 item pool is not the exact fillable-location multiset");

        var edges = logic.GetProperty("edges").EnumerateArray().Select(row => new Dc2LogicEdge(
            "ST" + row.GetProperty("from").GetString(),
            "ST" + row.GetProperty("to").GetString(),
            ParseInts(row.GetProperty("requiresItems")),
            ParseRooms(row.GetProperty("requiresRooms")))).ToArray();
        Require(edges.Count(x => x.RequiredItems.SequenceEqual(new[] { 0x2e })) == 1,
            "DC2 logic must contain exactly one Gas Mask gate");

        string[] dispositions = logic.GetProperty("conditionalCommitDispositions").EnumerateArray()
            .Select(x => x.GetProperty("disposition").GetString()!).ToArray();
        Require(dispositions.Length == 91, $"expected 91 conditional-commit dispositions, found {dispositions.Length}");
        Require(dispositions.All(x => !string.IsNullOrWhiteSpace(x)), "conditional commit without a disposition");

        var progression = logic.GetProperty("items").GetProperty("progressionItemIds").EnumerateArray()
            .Select(x => x.GetInt32()).ToHashSet();
        Require(progression.SetEquals(new[] { 0x2e }), "runtime currently supports Gas Mask as the sole progression item");
        var fixedLifecycle = logic.GetProperty("items").GetProperty("fixedLifecycleItemIds")
            .EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
        clearedOwnershipIds.ExceptWith(progression);
        Require(fixedLifecycle.SetEquals(clearedOwnershipIds),
            "fixed lifecycle item ids must match nonprogression group-9 clear sites");

        return new Dc2ItemData(version, start, goal, catalog, locations, edges, dispositions,
            progression, fixedLifecycle);
    }

    private static JsonDocument Open(string resourceName)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"missing embedded DC2 item resource {resourceName}");
        return JsonDocument.Parse(stream);
    }

    private static Dc2ItemEditor.OperandPin ParsePin(JsonElement element) => new(
        element.GetProperty("block_off").GetInt32(),
        checked((byte)element.GetProperty("mode").GetInt32()),
        checked((short)element.GetProperty("value").GetInt32()),
        element.GetProperty("push_off").GetInt32(),
        element.GetProperty("value_off").GetInt32());

    private static Dc2ItemEditor.ItemRewriteClass ParseRewriteClass(string value) => value switch
    {
        "health" => Dc2ItemEditor.ItemRewriteClass.Health,
        "generic_key" => Dc2ItemEditor.ItemRewriteClass.GenericKey,
        "special_2f" or "special_key_2f" => Dc2ItemEditor.ItemRewriteClass.SpecialKey2f,
        _ => throw new InvalidOperationException($"unknown DC2 item rewrite class {value}"),
    };

    private static int[] ParseInts(JsonElement element) => element.EnumerateArray().Select(x => x.GetInt32()).ToArray();
    private static string[] ParseRooms(JsonElement element) => element.EnumerateArray().Select(x => "ST" + x.GetString()).ToArray();
    private static int ParseHex(string value) => Convert.ToInt32(value[2..], 16);
    private static int ParseRoomCode(string value) => checked((value[0] - '0') << 8 | Convert.ToInt32(value[1..], 16));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"invalid embedded DC2 item contract: {message}");
    }
}

public sealed record Dc2ItemLocation(
    string SourceId,
    long ApId,
    string Name,
    string RoomId,
    int RoomCode,
    int ItemId,
    string ItemName,
    string SourceType,
    Dc2ItemEditor.ItemRewriteClass RewriteClass,
    string PlacementClass,
    IReadOnlyList<int> RequiredItems,
    IReadOnlyList<string> RequiredRooms,
    Dc2ItemEditor.ItemSiteSpec Site);

public sealed record Dc2LogicEdge(
    string FromRoomId,
    string ToRoomId,
    IReadOnlyList<int> RequiredItems,
    IReadOnlyList<string> RequiredRooms);
