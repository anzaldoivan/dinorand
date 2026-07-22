using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2.Passes;

/// <summary>Standalone DC2 v2 item/key shuffle over the 42 proven, fillable E1 writer sites.</summary>
public sealed class Dc2ItemRandomizer : IDc2RandomizationPass
{
    public string Name => "DC2 Items";

    public bool IsEnabled(RandomizerConfig config) => config.RandomizeItems || config.ShuffleKeyItems;

    public void Apply(Dc2RandomizationContext context)
    {
        Dc2ItemData data = Dc2ItemData.LoadEmbedded();
        Dc2ItemPlan plan = Dc2ItemPlanner.Plan(
            data,
            context.Seed,
            context.Config.RandomizeItems,
            context.Config.ShuffleKeyItems);

        var roomsById = context.Rooms.ToDictionary(
            room => $"ST{room.Stage}{room.Room:X2}",
            StringComparer.OrdinalIgnoreCase);
        var missingRooms = data.Locations.Select(x => x.RoomId).Distinct(StringComparer.Ordinal)
            .Where(roomId => !roomsById.ContainsKey(roomId)).Order(StringComparer.Ordinal).ToArray();
        foreach (string roomId in missingRooms)
            context.Log($"[dc2-items] {roomId} not among the loaded rooms; its item sites were skipped");

        int changed = 0;
        int emitted = 0;
        foreach (var roomGroup in plan.Placements.Where(x => x.ItemId != x.OriginalItemId)
                     .GroupBy(x => x.RoomId, StringComparer.Ordinal))
        {
            if (!roomsById.TryGetValue(roomGroup.Key, out Dc2RoomFile? room))
                continue;
            var edits = roomGroup.Select(x => new Dc2ItemEditor.ItemEdit(x.Site, x.ItemId)).ToArray();
            byte[] output = Dc2ItemEditor.ApplyEdits(context.CurrentBytes(room), roomGroup.Key, edits);
            context.EmitRoom(room, output);
            changed += edits.Length;
            emitted++;
        }

        var spoiler = context.Spoiler.Section(
            "Items and key items (DC2)", "Source ID", "Physical room", "Vanilla", "Placed");
        foreach (Dc2ItemPlacement placement in plan.Placements)
        {
            spoiler.AddRow(
                placement.SourceId,
                placement.RoomId,
                $"{data.Catalog[placement.OriginalItemId]} (0x{placement.OriginalItemId:x2})",
                $"{placement.ItemName} (0x{placement.ItemId:x2})");
        }
        spoiler.AddNote($"Beatability: {plan.Diagnostics}.");
        spoiler.AddNote("The health and generic-key class multisets are conserved exactly; item 0x2f is fixed outside this 42-site surface.");
        if (missingRooms.Length != 0)
            spoiler.AddNote($"Partial layout: skipped {missingRooms.Length} missing source room(s): {string.Join(", ", missingRooms)}.");

        context.Log($"[dc2-items] {changed} item site(s) changed across {emitted} emitted room(s); {plan.Diagnostics}.");
    }
}
