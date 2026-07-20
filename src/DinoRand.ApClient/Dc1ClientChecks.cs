using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinoRand.ApClient;

/// <summary>
/// The authored per-location check table <c>data/dc1/ap-client-checks.json</c>
/// (scripts/gen_ap_client_checks.py; AP-CLIENT-PLAN.md §2): AP location name → taken-flag
/// predicate (group 7, any-of indices) + the per-record take indices the installer writes at
/// <c>rec+0x20</c> (the rekey plan). Shipped embedded so the client and installer read one
/// artifact.
/// </summary>
public sealed class Dc1ClientChecks
{
    public required IReadOnlyList<Entry> Locations { get; init; }

    public sealed class Entry
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        /// <summary>The AP location id (the apworld's sorted-name scheme; parity-checked by
        /// gen_ap_logic --check) — what LocationChecks sends and slot_data placements key on.</summary>
        [JsonPropertyName("apId")] public required long ApId { get; init; }
        [JsonPropertyName("key")] public required string Key { get; init; }
        [JsonPropertyName("room")] public required string Room { get; init; }
        [JsonPropertyName("predicate")] public required Predicate Predicate { get; init; }
        [JsonPropertyName("records")] public required IReadOnlyList<RecordPlan> Records { get; init; }
        [JsonPropertyName("class")] public required string Class { get; init; }
        [JsonPropertyName("excluded")] public bool Excluded { get; init; }
        [JsonPropertyName("sharedWith")] public IReadOnlyList<string>? SharedWith { get; init; }
    }

    public sealed class Predicate
    {
        [JsonPropertyName("kind")] public required string Kind { get; init; }
        [JsonPropertyName("group")] public required int Group { get; init; }
        [JsonPropertyName("anyOf")] public required IReadOnlyList<int> AnyOf { get; init; }
    }

    public sealed class RecordPlan
    {
        [JsonPropertyName("room")] public required string Room { get; init; }
        /// <summary>Record offset in the room's decompressed RDT buffer, hex ("0x393b8") —
        /// the same coordinate space as <c>ItemRecord.FileOffset</c>.</summary>
        [JsonPropertyName("rec")] public required string Rec { get; init; }
        [JsonPropertyName("vanillaTake")] public required int VanillaTake { get; init; }
        /// <summary>The FINAL take index the installer writes at rec+0x20 (== VanillaTake
        /// unless this location was rekeyed).</summary>
        [JsonPropertyName("take")] public required int Take { get; init; }

        [JsonIgnore] public int RecOffset => Convert.ToInt32(Rec, 16);
    }

    private sealed class FileShape
    {
        [JsonPropertyName("version")] public int Version { get; init; }
        [JsonPropertyName("flagGroup")] public int FlagGroup { get; init; }
        [JsonPropertyName("locations")] public required List<Entry> Locations { get; init; }
    }

    public static Dc1ClientChecks LoadEmbedded()
    {
        using var s = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("DinoRand.ApClient.Data.dc1.ap-client-checks.json")
            ?? throw new InvalidOperationException("embedded ap-client-checks.json missing");
        return Load(s);
    }

    public static Dc1ClientChecks Load(Stream stream)
    {
        var shape = JsonSerializer.Deserialize<FileShape>(stream)
                    ?? throw new InvalidOperationException("ap-client-checks.json: empty");
        if (shape.Version != 1 || shape.FlagGroup != 7)
            throw new InvalidOperationException(
                $"ap-client-checks.json: unsupported version {shape.Version}/group {shape.FlagGroup}");
        return new Dc1ClientChecks { Locations = shape.Locations };
    }
}
