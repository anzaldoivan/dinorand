using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Logic;

namespace DinoRand.Randomizer.Passes;

/// <summary>How <see cref="EmergencyBoxRandomizer"/> sets each box's plug cost.</summary>
public enum PlugCostMode
{
    /// <summary>Vanilla costs (default).</summary>
    Vanilla,
    /// <summary>Permute the 17 vanilla costs among the boxes; the total stays 26.</summary>
    Shuffle,
    /// <summary>Draw each cost from 1–3, then lower the largest until the total fits the plug supply.</summary>
    Random,
}

/// <summary>How <see cref="EmergencyBoxRandomizer"/> reassigns box ids (contents, colour message, Access pool
/// and unlock flag).</summary>
public enum BoxIdMode
{
    /// <summary>Every box keeps its id (default).</summary>
    Off,
    /// <summary>Permute ids among boxes of the same colour, so each box still looks like its colour.</summary>
    WithinColour,
    /// <summary>Permute all ids, recolouring each box whose colour changes (<see cref="EmergencyBoxTexture"/>).
    /// EXPERIMENTAL: the recoloured models have not been seen in game.</summary>
    Full,
}

/// <summary>
/// DC1 emergency boxes, edited in the room scripts (EMERGENCY-BOX-DATA.md "Engine handler"). Each box is a
/// <c>0x28</c> subtype-8 record (<see cref="EmergencyBoxRecord"/>): byte <c>+0x1D</c> is its plug cost and byte
/// <c>+0x1C</c> its id, which picks the contents record in the exe, the colour message, the same-colour Access
/// pool and the unlock flag. A box location is every record that has the same vanilla id: nine boxes also have
/// a record in the stage A/B/C copy of their room, and all records of a location get the same values. Those
/// copies are otherwise left alone: only their box bytes change.
///
/// <para>Costs stay 1–3: the "needs N plugs" prompt is built as one digit plus the cost (<c>0x64F7F4</c>), and
/// 0 would open the box without setting its flag. Boxes are optional storage, not progression, so the only
/// guard is that the plug supply covers the total cost (<see cref="PlugEconomy.PlugSupply"/>). The colour
/// counts stay 8/5/4 under an id permutation, so the Access pools stay valid, and the exe contents options
/// compose with it because both are keyed by id. When a location's colour changes, its model is recoloured
/// from the new id's own vanilla box (<see cref="EmergencyBoxTexture"/>).</para>
///
/// <para>Arrange Game's AST copies of St502 and st50C are outside the rooms DinoRand writes, so in an Arrange
/// Game those two rooms keep their vanilla boxes 3, 10, 4 and 11.</para>
/// </summary>
public sealed class EmergencyBoxRandomizer : IRandomizationPass
{
    public string Name => "emergency-boxes";

    /// <summary>Box ids that keep their location. ST105's shuttle event tests box 8's unlock flag (9:55, whose
    /// only producer is that box, STATIC-SCD-RE §D), so box 8 stays in ST105. In Full mode ST60D's box 15 also
    /// stays: only its body colour can be changed (<see cref="EmergencyBoxTexture.ByBoxId"/>), and within the
    /// yellows it would move nowhere else.</summary>
    public static IReadOnlySet<int> PinnedIds(BoxIdMode mode)
        => mode == BoxIdMode.Full ? new HashSet<int> { 8, 15 } : new HashSet<int> { 8 };

    /// <summary>Box locations that keep their vanilla plug cost. The shuttle needs ST105's box opened (see
    /// <see cref="PinnedIds"/>), so its 1-plug price stays, as it is on the way through the game.</summary>
    public static readonly IReadOnlySet<int> PinnedCostIds = new HashSet<int> { 8 };

    public const int MinCost = 1, MaxCost = 3;

    public bool IsEnabled(RandomizerConfig config)
        => config.PlugCostMode != PlugCostMode.Vanilla || config.BoxIdMode != BoxIdMode.Off;

    public void Apply(RandomizationContext context)
    {
        var game = context.Game;
        if (game.PlugItemId is not { } plug || game.EmergencyBoxes.Count == 0) return;

        // The stage A/B/C copies are opaque to every pass (GameDefinition.IsRandomizedRoom); parse just the ones
        // that hold a box, and write them through the room-output override so no other record of theirs changes.
        var copies = context.AllRooms().Where(r => r.Package is null)
            .Select(r => (Opaque: r, Parsed: RoomFile.Read(r.Stage, r.Room, r.OriginalBytes)))
            .Where(x => x.Parsed.ParsedCleanly && x.Parsed.Script!.EmergencyBoxes.Count > 0)
            .ToList();
        var roomOf = BoxRecords(context.AllRooms().Concat(copies.Select(x => x.Parsed))).ToDictionary(x => x.Box, x => x.Room);
        var locations = roomOf.Keys
            .GroupBy(b => (int)b.OriginalBoxId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var catalogue = game.EmergencyBoxes.OrderBy(b => b.BoxId).ToList();
        if (!catalogue.All(b => locations.ContainsKey(b.BoxId)) || locations.Count != catalogue.Count
            || locations.Values.Any(recs => recs.Any(r => r.OriginalPlugCost != recs[0].OriginalPlugCost)))
        {
            context.Log($"[boxes] skipped: found {locations.Count} box ids in the room scripts, expected {catalogue.Count} with one cost each");
            return;
        }

        var ids = catalogue.Select(b => b.BoxId).ToArray();
        var costs = catalogue.Select(b => (int)locations[b.BoxId][0].OriginalPlugCost).ToArray();
        int supply = PlugEconomy.PlugSupply(context.AllRooms().SelectMany(r => r.Items), plug);

        if (context.Config.PlugCostMode != PlugCostMode.Vanilla)
        {
            var free = Enumerable.Range(0, ids.Length).Where(i => !PinnedCostIds.Contains(ids[i])).ToArray();
            int pinnedTotal = costs.Sum() - free.Sum(i => costs[i]);
            var drawn = free.Select(i => costs[i]).ToArray();
            var rng = context.Seed.RngFor("plug-costs");
            if (context.Config.PlugCostMode == PlugCostMode.Shuffle) Shuffle(drawn, rng);
            else drawn = RandomCosts(free.Length, supply - pinnedTotal, rng);
            for (int k = 0; k < free.Length; k++) costs[free[k]] = drawn[k];
        }
        if (context.Config.PlugCostMode != PlugCostMode.Vanilla && costs.Sum() > supply)
            throw new InvalidOperationException($"emergency boxes cost {costs.Sum()} plugs but only {supply} can be picked up");

        if (context.Config.BoxIdMode != BoxIdMode.Off)
            ids = PermuteIds(ids, context.Config.BoxIdMode, context.Seed.RngFor("box-ids"));

        // Plan every recolour before changing anything, so a room the table does not fit leaves all rooms alone.
        var recolours = new List<(RoomFile Room, (int ClutX, int ClutY, int Index, ushort Value)[] Writes)>();
        for (int i = 0; i < catalogue.Count; i++)
        {
            var location = catalogue[i];
            var box = catalogue[ids[i]];
            if (box.Colour == location.Colour) continue;
            var donor = roomOf[locations[box.BoxId][0]];
            foreach (var record in locations[location.BoxId])
            {
                var room = roomOf[record];
                var writes = EmergencyBoxTexture.PlanRecolour(room, location.BoxId, donor, box.BoxId)
                    ?? throw new InvalidOperationException($"box ids {context.Config.BoxIdMode}: cannot recolour the box in {room}");
                recolours.Add((room, writes));
            }
        }
        foreach (var (room, writes) in recolours)
            foreach (var w in writes)
                room.SetClutEntry(w.ClutX, w.ClutY, w.Index, w.Value);

        var spoiler = context.Spoiler.Section("Emergency boxes", "Location", "Box", "Plugs");
        for (int i = 0; i < catalogue.Count; i++)
        {
            var location = catalogue[i];
            var box = catalogue[ids[i]];
            foreach (var record in locations[location.BoxId])
            {
                record.BoxId = (byte)ids[i];
                record.PlugCost = (byte)costs[i];
            }
            spoiler.AddRow($"{location.Name} (0x{location.RoomCode:X3})",
                ids[i] == location.BoxId ? $"box {ids[i]} ({box.Colour})" : $"box {ids[i]} ({box.Colour}), contents of {box.Name}",
                costs[i] == location.PlugCost ? $"{costs[i]}" : $"{costs[i]} (was {location.PlugCost})");
        }
        foreach (var (opaque, parsed) in copies)
            context.SetRoomOutput(opaque, parsed.Write());
        spoiler.AddNote($"plug costs {context.Config.PlugCostMode}, box ids {context.Config.BoxIdMode}; "
            + $"boxes cost {costs.Sum()} plugs in all, {supply} can be picked up");
        context.Log($"[boxes] plug costs {context.Config.PlugCostMode} (total {costs.Sum()}, supply {supply}), "
            + $"box ids {context.Config.BoxIdMode} ({ids.Where((id, i) => id != catalogue[i].BoxId).Count()} moved, "
            + $"{recolours.Count} box models recoloured)");
    }

    /// <summary>The box records of the rooms whose script parsed cleanly (<see cref="RoomFile.Write"/> writes
    /// only those).</summary>
    private static IEnumerable<(EmergencyBoxRecord Box, RoomFile Room)> BoxRecords(IEnumerable<RoomFile> rooms)
        => rooms.Where(r => r.ParsedCleanly).SelectMany(r => r.Script!.EmergencyBoxes.Select(b => (b, r)));

    /// <summary>Each cost drawn from <see cref="MinCost"/>..<see cref="MaxCost"/>, then the largest lowered one at a
    /// time until the total is at most <paramref name="supply"/>.</summary>
    internal static int[] RandomCosts(int count, int supply, Random rng)
    {
        if (count * MinCost > supply)
            throw new InvalidOperationException($"{count} emergency boxes need at least {count * MinCost} plugs but only {supply} can be picked up");
        var costs = new int[count];
        for (int i = 0; i < count; i++) costs[i] = rng.Next(MinCost, MaxCost + 1);
        while (costs.Sum() > supply)
        {
            int max = costs.Max();
            var largest = Enumerable.Range(0, count).Where(i => costs[i] == max).ToArray();
            costs[largest[rng.Next(largest.Length)]]--;
        }
        return costs;
    }

    /// <summary>A permutation of <paramref name="ids"/> (indexed by location) that keeps
    /// <see cref="PinnedIds"/> in place and, for <see cref="BoxIdMode.WithinColour"/>, each id's colour.</summary>
    internal static int[] PermuteIds(int[] ids, BoxIdMode mode, Random rng)
    {
        var result = (int[])ids.Clone();
        var groups = Enumerable.Range(0, ids.Length)
            .Where(i => !PinnedIds(mode).Contains(ids[i]))
            .GroupBy(i => mode == BoxIdMode.WithinColour ? EmergencyBoxRecord.ColourOf(ids[i]) : EmergencyBoxColour.Red);
        foreach (var group in groups)
        {
            var slots = group.ToArray();
            var values = slots.Select(i => ids[i]).ToArray();
            Shuffle(values, rng);
            for (int k = 0; k < slots.Length; k++) result[slots[k]] = values[k];
        }
        return result;
    }

    private static void Shuffle(int[] a, Random rng)
    {
        for (int i = a.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (a[i], a[j]) = (a[j], a[i]);
        }
    }
}
