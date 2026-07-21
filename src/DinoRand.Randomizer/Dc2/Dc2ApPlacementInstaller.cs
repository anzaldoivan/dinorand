using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Dc2;

/// <summary>Output-only installer for a complete static Archipelago v2 placement.</summary>
public static class Dc2ApPlacementInstaller
{
    public const int OtherWorldMarker = -1;

    public sealed record StaticPlacement(long ApLocationId, string SourceId, int ItemId);
    public sealed record RoomPlan(string RoomId, IReadOnlyList<Dc2ItemEditor.ItemEdit> Edits);
    public sealed record PlacementPlan(int PlacementCount, IReadOnlyList<RoomPlan> Rooms);
    public sealed record Result(int RoomsWritten, int LocationsPatched);

    /// <summary>Adapt the string-keyed <c>placements</c>/<c>source_ids</c> maps emitted in AP v2 slot data.</summary>
    public static PlacementPlan CreatePlanFromSlotData(
        int logicVersion,
        IReadOnlyDictionary<string, int> placements,
        IReadOnlyDictionary<string, string> sourceIds)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(sourceIds);
        if (!placements.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(sourceIds.Keys))
            throw Refuse("slot-data placements and source_ids must have identical AP-location keys");
        var rows = new List<StaticPlacement>(placements.Count);
        foreach (var pair in placements)
        {
            if (!long.TryParse(pair.Key, out long apLocationId))
                throw Refuse($"slot-data AP location id '{pair.Key}' is not an integer");
            rows.Add(new StaticPlacement(apLocationId, sourceIds[pair.Key], pair.Value));
        }
        return CreatePlan(logicVersion, rows);
    }

    /// <summary>Validate the complete AP contract before any room or output file is touched.</summary>
    public static PlacementPlan CreatePlan(int logicVersion, IReadOnlyList<StaticPlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        Dc2ItemData data = Dc2ItemData.LoadEmbedded();
        if (logicVersion != data.Version)
            throw Refuse($"logic version {logicVersion} does not match embedded v{data.Version}");
        if (placements.Count != data.Locations.Count)
            throw Refuse($"placement must cover all {data.Locations.Count} DC2 locations");

        var byApId = data.Locations.ToDictionary(x => x.ApId);
        var seen = new HashSet<long>();
        var edits = new List<(string RoomId, Dc2ItemEditor.ItemEdit Edit)>();
        foreach (StaticPlacement placement in placements)
        {
            if (placement.ItemId == OtherWorldMarker)
                throw Refuse($"OTHER_WORLD_MARKER at AP location {placement.ApLocationId}");
            if (!seen.Add(placement.ApLocationId))
                throw Refuse($"duplicate AP location id {placement.ApLocationId}");
            if (!byApId.TryGetValue(placement.ApLocationId, out Dc2ItemLocation? location))
                throw Refuse($"unknown AP location id {placement.ApLocationId}");
            if (!StringComparer.Ordinal.Equals(placement.SourceId, location.SourceId))
                throw Refuse($"AP location {placement.ApLocationId} source identity mismatch");
            if (!data.Catalog.ContainsKey(placement.ItemId))
                throw Refuse($"unknown DC2 catalog id 0x{placement.ItemId:x}");
            if (PlacementClass(data, placement.ItemId) != location.PlacementClass)
                throw Refuse($"{location.SourceId} cannot accept cross-class item 0x{placement.ItemId:x2}");
            edits.Add((location.RoomId, new Dc2ItemEditor.ItemEdit(location.Site, placement.ItemId)));
        }
        if (seen.Count != byApId.Count)
            throw Refuse("placement does not cover every embedded AP location exactly once");

        RoomPlan[] rooms = edits.GroupBy(x => x.RoomId, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(group => new RoomPlan(group.Key, group.Select(x => x.Edit).ToArray()))
            .ToArray();
        return new PlacementPlan(placements.Count, rooms);
    }

    /// <summary>
    /// Read each room from the pristine backup when present (else live Data), validate and patch all
    /// rooms in memory, then write each output once. No networking, process memory, or game file is modified.
    /// </summary>
    public static Result WriteRooms(
        string dataDir,
        string outputDir,
        int logicVersion,
        IReadOnlyList<StaticPlacement> placements,
        Action<string>? log = null)
    {
        PlacementPlan plan = CreatePlan(logicVersion, placements);
        var outputs = new List<(string Name, byte[] Bytes, int Count)>();
        foreach (RoomPlan room in plan.Rooms)
        {
            string source = FindPristineRoom(dataDir, room.RoomId)
                ?? throw new FileNotFoundException($"DC2 AP install: {room.RoomId} not found under {dataDir}");
            byte[] input = File.ReadAllBytes(source);
            byte[] output = Dc2ItemEditor.ApplyEdits(input, room.RoomId, room.Edits);
            outputs.Add((Path.GetFileName(source), output, room.Edits.Count));
        }

        Directory.CreateDirectory(outputDir);
        foreach (var output in outputs)
        {
            File.WriteAllBytes(Path.Combine(outputDir, output.Name), output.Bytes);
            log?.Invoke($"  {output.Name}: {output.Count} AP item location(s)");
        }
        return new Result(outputs.Count, plan.PlacementCount);
    }

    public static Result WriteRoomsFromSlotData(
        string dataDir,
        string outputDir,
        int logicVersion,
        IReadOnlyDictionary<string, int> placements,
        IReadOnlyDictionary<string, string> sourceIds,
        Action<string>? log = null)
    {
        _ = CreatePlanFromSlotData(logicVersion, placements, sourceIds);
        var rows = placements.Select(pair =>
            new StaticPlacement(long.Parse(pair.Key), sourceIds[pair.Key], pair.Value)).ToArray();
        return WriteRooms(dataDir, outputDir, logicVersion, rows, log);
    }

    private static string? FindPristineRoom(string dataDir, string roomId)
    {
        string name = roomId + ".DAT";
        foreach (string directory in new[] { Path.Combine(dataDir, GameInstaller.BackupDirName), dataDir })
        {
            if (!Directory.Exists(directory)) continue;
            string? match = Directory.EnumerateFiles(directory)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }

    private static Dc2ItemEditor.ItemRewriteClass Classify(int itemId) => itemId switch
    {
        >= 0x1a and <= 0x1d or 0x1f => Dc2ItemEditor.ItemRewriteClass.Health,
        >= 0x21 and <= 0x2e or >= 0x30 and <= 0x34 => Dc2ItemEditor.ItemRewriteClass.GenericKey,
        0x2f => Dc2ItemEditor.ItemRewriteClass.SpecialKey2f,
        _ => throw Refuse($"catalog id 0x{itemId:x2} is not writer-compatible"),
    };

    private static string PlacementClass(Dc2ItemData data, int itemId)
    {
        if (data.FixedLifecycleItemIds.Contains(itemId))
            return $"fixed_lifecycle_{itemId:x2}";
        return Classify(itemId) switch
        {
            Dc2ItemEditor.ItemRewriteClass.Health => "health",
            Dc2ItemEditor.ItemRewriteClass.GenericKey => "generic_key",
            Dc2ItemEditor.ItemRewriteClass.SpecialKey2f => "special_key_2f",
            _ => throw Refuse($"catalog id 0x{itemId:x2} has no placement class"),
        };
    }

    private static InvalidOperationException Refuse(string message)
        => new($"DC2 AP placement refused: {message}");
}
