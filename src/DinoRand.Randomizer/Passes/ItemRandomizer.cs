using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// Phase 1. Randomizes non-key item pickups (docs/decisions/cross/ITEM-RANDO-PLAN.md). Two modes: a pure shuffle (same
/// items, new spots) and replace-with-pool. Key items are never touched here — that is
/// <see cref="KeyItemPlacer"/>'s job. Replace-mode is reachability-aware: it only places into the
/// playable reachable world, pool-places weapons/parts (so they are never overwritten with
/// consumables — the old clobber bug), links ammo to the weapons actually granted, and guarantees a
/// floor of consumables in the starting region.
/// </summary>
public sealed class ItemRandomizer : IRandomizationPass
{
    /// <summary>Minimum ammo / health pickups guaranteed inside the no-key starting region.</summary>
    private const int StartRegionAmmoFloor = 3;
    private const int StartRegionHealthFloor = 1;

    public string Name => "items";

    public bool IsEnabled(RandomizerConfig config) => config.RandomizeItems;

    private sealed class Slot
    {
        internal Slot(int room, LogicalPickupId logicalId, IReadOnlyList<NodeItem> members)
        {
            Room = room;
            LogicalId = logicalId;
            Members = members;
            Visual = members.Max(member => member.Visual);
        }

        internal int Room { get; }
        internal LogicalPickupId LogicalId { get; }
        internal IReadOnlyList<NodeItem> Members { get; }
        internal ItemRecord Record => Members[0].Record;
        internal PickupVisual Visual { get; }
        internal IReadOnlyList<PickupPlacementEdit> Edits { get; private set; } =
            Array.Empty<PickupPlacementEdit>();

        internal void Set(int itemId, int amount, string? note = null)
        {
            if (Edits.Count != 0)
                throw new InvalidOperationException($"logical pickup {LogicalId} was assigned more than once");
            Edits = Members.Select(member => new PickupPlacementEdit(
                member.Location, member.Record, member.Record.ItemId, member.Record.Amount,
                itemId, amount, note)).ToArray();
            foreach (var member in Members)
            {
                member.Record.ItemId = itemId;
                member.Record.Amount = amount;
            }
        }
    }

    public void Apply(RandomizationContext context)
    {
        var rng = context.Seed.RngFor(Name);
        var game = context.Game;
        var graph = context.Graph;
        var keyItems = game.KeyItemIds;

        // Reachable playable world (all physical progression entitlements already seated by
        // ProgressionPass, including overlay-only DDK gates) and the no-key starting region.
        var playableKeys = KeyPlacementPlanner.PlayableKeyEntitlements(graph, game);
        var reach = KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, playableKeys);
        var startRegion = KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, new HashSet<int>());

        // Canonical logical pickups in original graph enumeration order. Physical identity and group
        // membership are explicit; equal ids or geometry never imply one pickup.
        var physical = new HashSet<PhysicalPickupId>();
        var groups = new Dictionary<LogicalPickupId, List<NodeItem>>();
        var groupOrder = new List<LogicalPickupId>();
        foreach (var node in graph.Nodes)
        {
            foreach (var ni in node.Items)
            {
                var location = ni.Location;
                if (!physical.Add(location.PhysicalId))
                    throw new InvalidOperationException(
                        $"duplicate physical pickup identity {location.PhysicalId}");
                if (!groups.TryGetValue(location.LogicalId, out var members))
                {
                    groups[location.LogicalId] = members = new List<NodeItem>();
                    groupOrder.Add(location.LogicalId);
                }
                members.Add(ni);
            }
        }

        var slots = new List<Slot>();
        foreach (var logicalId in groupOrder)
        {
            var members = groups[logicalId];
            var first = members[0];
            var tuple = (first.Record.ItemId, first.Record.Amount);
            if (members.Any(member => (member.Record.ItemId, member.Record.Amount) != tuple))
                throw new InvalidOperationException(
                    $"logical pickup {logicalId} has divergent physical record contents");
            if (members.Any(member =>
                    !reach.Contains(member.RoomCode)
                    || game.EndingZoneRoomCodes.Contains(member.RoomCode)
                    || game.ItemProtectedRoomCodes.Contains(member.RoomCode)
                    || member.Record.IsEmptySlot
                    || keyItems.Contains(member.Record.ItemId)
                    || member.Priority == ItemPriority.Fixed
                    || member.Excluded
                    || member.AllowedPlacementClass != PickupPlacementClass.Ordinary
                    || !member.Requires.SatisfiedBy(playableKeys, reach)))
                continue;
            slots.Add(new Slot(first.RoomCode, logicalId, members));
        }

        if (slots.Count == 0)
        {
            context.Log("[items] no rerollable pickups in the reachable world");
            context.Spoiler.Section(SpoilerSectionTitle, SpoilerColumns)
                .AddNote("no rerollable pickups in the reachable world");
            return;
        }

        var spoilerSlots = slots.ToArray();

        // Beatability coupling for the starting-inventory feature: any vanilla starting weapon the config
        // REMOVES from the start kit (StartingWeapons override) must be obtainable in the world, reachable
        // before any key gate. Force-place each removed weapon into a no-key start-region slot and lock it
        // (remove from the rerollable set) so neither mode overwrites it. Mirrors BioRand's pull-from-pool.
        // (Ammo linking is unaffected: the player still ends up with the game's starting weapons — at start
        // or in the world — so game.StartingWeaponIds remains the correct ammo-linking set.)
        var startWeapons = (context.Config.StartingWeapons ?? game.StartingWeaponIds).ToHashSet();
        var removedStartWeapons = game.StartingWeaponIds.Where(w => !startWeapons.Contains(w)).ToList();
        if (removedStartWeapons.Count > 0)
            ForcePlaceRemovedStartWeapons(slots, removedStartWeapons, startRegion, rng, context);

        if (slots.Count == 0)
        {
            context.Log("[items] all rerollable pickups consumed by forced start-weapon placement");
            RecordSpoiler(context, spoilerSlots,
                context.Config.ReplaceItemPool ? "rerolled" : "shuffled");
            return;
        }

        if (context.Config.ReplaceItemPool)
            ReplaceMode(slots, startRegion, context, rng);
        else
            ShuffleExisting(slots, rng);

        var mode = context.Config.ReplaceItemPool ? "rerolled" : "shuffled";
        context.Log($"[items] {mode} {slots.Count} logical non-key pickups across " +
                    $"{slots.Select(s => s.Room).Distinct().Count()} reachable rooms");

        RecordSpoiler(context, spoilerSlots, mode);
    }

    private const string SpoilerSectionTitle = "Items (DC1)";
    private static readonly string[] SpoilerColumns = { "Room", "Vanilla item", "New item" };

    /// <summary>
    /// Project the committed assignment ledger: one row per changed physical record. Runs after
    /// assignment and consumes captured before/after values only; no RNG, mutable-state inference, or I/O.
    /// </summary>
    private static void RecordSpoiler(RandomizationContext context, IReadOnlyList<Slot> slots, string mode)
    {
        var section = context.Spoiler.Section(SpoilerSectionTitle, SpoilerColumns);
        int changed = 0;
        foreach (var edit in slots.SelectMany(slot => slot.Edits).Where(edit => edit.Changed))
        {
            section.AddRow(
                $"{Spoiler.Dc1RoomNames.Describe(edit.Location.RoomCode)} " +
                $"[record 0x{edit.Location.RecordOffset:X}]",
                Describe(edit.BeforeItemId, edit.BeforeAmount),
                Describe(edit.ItemId, edit.Amount) + (edit.Note is null ? "" : $" ({edit.Note})"));
            changed++;
        }
        section.AddNote($"mode: {mode} ({(context.Config.ReplaceItemPool ? "replace-with-pool" : "shuffle existing")}); "
            + $"{changed} of {slots.Sum(slot => slot.Members.Count)} rerollable physical record(s) changed");
    }

    /// <summary>"Med. Pak S" / "9mm Parabellum ×15".</summary>
    private static string Describe(int itemId, int amount)
        => Spoiler.Dc1ItemNames.NameOf(itemId) + (amount > 1 ? $" ×{amount}" : "");

    /// <summary>Replace-mode: pool-place weapons/parts into reachable spots, then fill the rest with
    /// linked ammo + health by ratio, guaranteeing a starting-region consumable floor.</summary>
    private static void ReplaceMode(List<Slot> slots, IReadOnlySet<int> startRegion,
                                    RandomizationContext context, Random rng)
    {
        var game = context.Game;
        var cfg = context.Config;
        var assigned = new HashSet<Slot>();

        // 1. Weapons/parts: the distinct vanilla weapon set present in these spots, pool-placed into
        //    random reachable slots (so each stays obtainable — the min-weapon guarantee — and never
        //    gets clobbered by a consumable). Placement uses OriginalItemId so it is stable.
        var weaponSet = new HashSet<int>(game.WeaponIds.Concat(game.WeaponPartIds));
        var partSet = new HashSet<int>(game.WeaponPartIds);
        var placedWeapons = new List<int>();
        if (cfg.RandomizeWeapons)
        {
            // Per-family enable filter (§7.4): drop weapons/parts whose family the config disabled (their
            // slots fall through to consumable fill). An id with no known family is always kept; at the
            // default WeaponFamily.All every id passes, so the filter is a no-op and the run is byte-identical.
            var toPlace = slots.Select(s => s.Record.OriginalItemId)
                               .Where(weaponSet.Contains)
                               .Where(id => game.WeaponFamilyOf(id) is not { } fam
                                            || cfg.EnabledWeaponFamilies.HasFlag(fam))
                               .Distinct().ToList();
            // Base weapons that will be in the seed (placed bases + starting), so an upgrade part is
            // only kept when its base weapon is obtainable (§7.3 — no dead upgrades).
            var availableBases = toPlace.Where(game.WeaponIds.Contains)
                                        .Concat(game.StartingWeaponIds).ToHashSet();
            // Weapons/parts prefer spots WITH a ground visual (AvoidHiddenPickupSpots, on by default):
            // an interaction-only slot renders nothing, so a weapon there is findable only by examining
            // the spot. Visible slots vastly outnumber the weapon set, so hidden slots are overflow-only;
            // the shuffle consumes the same RNG either way (flag off ⇒ byte-identical order).
            var shuffledSlots = slots.OrderBy(_ => rng.Next()).ToList();
            var free = cfg.AvoidHiddenPickupSpots
                ? shuffledSlots.Where(s => s.Visual != PickupVisual.InteractionOnly)
                               .Concat(shuffledSlots.Where(s => s.Visual == PickupVisual.InteractionOnly))
                               .ToList()
                : shuffledSlots;
            var suppressedParts = new HashSet<int>();   // parts consumed by a pre-upgraded weapon
            int fi = 0;
            foreach (var w in toPlace)
            {
                int placeId = w;
                if (partSet.Contains(w))
                {
                    if (suppressedParts.Contains(w)) continue;      // already applied to a pre-upgraded weapon
                    // Upgrade parts: skip when their base weapon is absent, or by the upgrade-chance roll
                    // (short-circuited at >=1.0 so the default consumes no RNG → byte-identical).
                    bool baseAvailable = game.WeaponForPart(w) is int b && availableBases.Contains(b);
                    bool rolled = cfg.WeaponUpgradeChance >= 1.0 || rng.NextDouble() < cfg.WeaponUpgradeChance;
                    if (!baseAvailable || !rolled) continue;        // leave the slot for consumable fill
                }
                else
                {
                    // EXPERIMENTAL (§7.3): a small chance the found base weapon is already upgraded — place
                    // its variant id instead and suppress the consumed part. Off (0) short-circuits before
                    // the roll, so the default consumes no RNG and is byte-identical.
                    var variants = game.WeaponUpgradeVariants(w);
                    if (variants.Count > 0 && cfg.PreUpgradedWeaponChance > 0
                        && rng.NextDouble() < cfg.PreUpgradedWeaponChance)
                    {
                        var (part, result) = variants[rng.Next(variants.Count)];
                        placeId = result;
                        suppressedParts.Add(part);
                    }
                }
                var spot = free[fi++];
                spot.Set(placeId, Math.Max(1, spot.Record.Amount));
                assigned.Add(spot);
                placedWeapons.Add(placeId);
            }
        }
        else
        {
            // Keep weapon/part pickups as their vanilla item (still never clobbered by consumables).
            foreach (var s in slots)
                if (weaponSet.Contains(s.Record.OriginalItemId))
                {
                    s.Set(s.Record.OriginalItemId, Math.Max(1, s.Record.Amount));
                    assigned.Add(s);
                    placedWeapons.Add(s.Record.OriginalItemId);
                }
        }

        // 2. Linked ammo: ammo for weapons actually granted — placed weapons, the starting weapons,
        //    and any weapon a placed PART upgrades into (so Handgun Slides → Glock 35 → 40S&W, the
        //    dead-weapon fix). Intersected with the curated consumable pool's ammo entries.
        var granted = placedWeapons.Concat(game.StartingWeaponIds).ToList();
        foreach (var id in placedWeapons)
            if (game.WeaponUpgradeFromPart(id) is int upgraded)
                granted.Add(upgraded);
        var linkedAmmo = granted.SelectMany(game.AmmoForWeapon).ToHashSet();
        var ammo = game.ItemPool.Where(e => e.Category == ItemCategory.Ammo && linkedAmmo.Contains(e.ItemId))
                                .ToList();
        if (ammo.Count == 0) // no weapons at all → fall back to the full ammo pool so the seed isn't dry
            ammo = game.ItemPool.Where(e => e.Category == ItemCategory.Ammo).ToList();
        var health = game.ItemPool.Where(e => e.Category == ItemCategory.Health).ToList();

        // 3. Fill the remaining slots from the combined consumable pool (linked ammo + health),
        //    weighting each entry by its intrinsic weight scaled by the category ratio — the existing
        //    ratio model (equal ratios reproduce the pool's intrinsic split; both-zero falls back to
        //    intrinsic). Starting-region slots first get a guaranteed ammo/health floor.
        var consumables = ammo.Concat(health).ToList();
        var weights = EffectiveWeights(consumables, cfg);
        var total = weights.Sum();

        var fill = slots.Where(s => !assigned.Contains(s)).OrderBy(_ => rng.Next()).ToList();
        var early = fill.Where(s => startRegion.Contains(s.Room)).ToList();

        // The floor obeys the same enable-test as the fill weights (docs/decisions/cross/ITEM-RATIO-ZERO-PLAN.md): a
        // category with ratio 0 is disabled and gets ZERO floor — its budget redistributes to the
        // enabled one so the opening keeps the same total consumable count. both-zero is the legacy
        // fallback (both enabled), so the default/non-zero path is byte-identical to before.
        bool bothZero = cfg.RatioAmmo == 0 && cfg.RatioHealth == 0;
        bool ammoEnabled = (bothZero || cfg.RatioAmmo > 0) && ammo.Count > 0;
        bool healthEnabled = (bothZero || cfg.RatioHealth > 0) && health.Count > 0;
        int ammoFloor, healthFloor;
        if (ammoEnabled && healthEnabled)
        {
            ammoFloor = Math.Min(StartRegionAmmoFloor, early.Count);
            healthFloor = Math.Min(StartRegionHealthFloor, Math.Max(0, early.Count - ammoFloor));
        }
        else if (ammoEnabled)
        {
            ammoFloor = Math.Min(StartRegionAmmoFloor + StartRegionHealthFloor, early.Count);
            healthFloor = 0;
        }
        else if (healthEnabled)
        {
            healthFloor = Math.Min(StartRegionAmmoFloor + StartRegionHealthFloor, early.Count);
            ammoFloor = 0;
        }
        else
        {
            ammoFloor = healthFloor = 0;
        }
        var floored = new HashSet<Slot>();
        for (int i = 0; i < ammoFloor; i++) { SetItem(early[i], WeightedPick(ammo, rng), cfg); floored.Add(early[i]); }
        for (int i = 0; i < healthFloor; i++) { SetItem(early[ammoFloor + i], WeightedPick(health, rng), cfg); floored.Add(early[ammoFloor + i]); }

        foreach (var s in fill)
        {
            if (floored.Contains(s)) continue;
            SetItem(s, WeightedPick(consumables, weights, total, rng), cfg);
        }
    }

    /// <summary>Force each removed starting weapon into a distinct no-key start-region pickup and lock it
    /// (remove from <paramref name="slots"/>), so a seed that strips a start weapon still hands the player one
    /// before any key gate. Picks deterministically from <paramref name="rng"/>; if the start region has too
    /// few free pickups it logs a warning and places what it can (the EXE patch still removed the grant).</summary>
    private static void ForcePlaceRemovedStartWeapons(List<Slot> slots, IReadOnlyList<int> weapons,
                                                      IReadOnlySet<int> startRegion, Random rng, RandomizationContext context)
    {
        var noKeys = new HashSet<int>();
        var shuffled = slots.Where(s => startRegion.Contains(s.Room)
                                      && s.Members.All(member =>
                                          member.Requires.SatisfiedBy(noKeys, startRegion)))
                            .OrderBy(_ => rng.Next()).ToList();
        // A forced start weapon is important — prefer a spot with a ground visual (same overflow rule
        // as the weapon pool-place; flag off keeps the plain shuffled order, byte-identical).
        var early = context.Config.AvoidHiddenPickupSpots
            ? shuffled.Where(s => s.Visual != PickupVisual.InteractionOnly)
                      .Concat(shuffled.Where(s => s.Visual == PickupVisual.InteractionOnly)).ToList()
            : shuffled;
        int taken = 0;
        foreach (var weapon in weapons)
        {
            if (taken >= early.Count)
            {
                context.Log($"[items] WARNING: removed start weapon 0x{weapon:X2} could not be placed " +
                            "(no free start-region pickup) — seed may be unbeatable; keep RandomizeItems on with enough early spots");
                continue;
            }
            var slot = early[taken++];
            slot.Set(weapon, 1, "removed start weapon, force-placed");
            slots.Remove(slot);
            context.Log($"[items] start weapon 0x{weapon:X2} removed from inventory → placed in start-region room 0x{slot.Room:X3}");
        }
    }

    private static void SetItem(Slot slot, ItemPoolEntry pick, RandomizerConfig cfg)
    {
        slot.Set(pick.ItemId,
            ScaledAmount(pick.Category, Math.Max(1, slot.Record.Amount),
                         cfg.AmmoQuantity, cfg.AmmoReduction));
    }

    /// <summary>Per-entry weight = intrinsic × category ratio; both ratios 0 ⇒ intrinsic (legacy
    /// fallback). Equal ratios scale uniformly, so they reproduce the intrinsic distribution.</summary>
    private static double[] EffectiveWeights(IReadOnlyList<ItemPoolEntry> pool, RandomizerConfig cfg)
    {
        if (cfg.RatioAmmo == 0 && cfg.RatioHealth == 0)
            return pool.Select(p => p.Weight).ToArray();
        return pool.Select(p => p.Weight * (p.Category == ItemCategory.Ammo ? cfg.RatioAmmo : cfg.RatioHealth))
                   .ToArray();
    }

    private static ItemPoolEntry WeightedPick(IReadOnlyList<ItemPoolEntry> pool, double[] weights,
                                              double total, Random rng)
    {
        double roll = rng.NextDouble() * total;
        for (int i = 0; i < pool.Count; i++)
        {
            roll -= weights[i];
            if (roll <= 0) return pool[i];
        }
        return pool[^1];
    }

    /// <summary>Intrinsic-weight pick within a single category (used for the start-region floor).</summary>
    private static ItemPoolEntry WeightedPick(List<ItemPoolEntry> pool, Random rng)
    {
        double total = pool.Sum(p => p.Weight);
        double roll = rng.NextDouble() * total;
        foreach (var e in pool)
        {
            roll -= e.Weight;
            if (roll <= 0) return e;
        }
        return pool[^1];
    }

    /// <summary>Shuffle mode: conserve the logical-pickup multiset of (item id, amount) tuples and
    /// reassign each tuple with the established Fisher–Yates order. Reach-filtered logical slots only.</summary>
    private static void ShuffleExisting(List<Slot> slots, Random rng)
    {
        var items = slots.Select(s => (s.Record.ItemId, s.Record.Amount)).ToArray();
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        for (int i = 0; i < slots.Count; i++)
            slots[i].Set(items[i].ItemId, Math.Max(1, items[i].Amount));
    }

    /// <summary>Scale an ammo stack by the average-quantity dial (0–7). 0 leaves the amount unchanged.
    /// Health and other categories are never scaled.</summary>
    private static int ScaledAmount(ItemCategory category, int amount, byte ammoQuantity, byte ammoReduction)
    {
        // Signed level: +n grows stacks (×1+0.5n), −n shrinks them (÷1+0.5n, floored at 1); 0 = vanilla.
        // The two unsigned bytes are mutually exclusive in practice, so the net is just their difference.
        int level = ammoQuantity - ammoReduction;
        if (category != ItemCategory.Ammo || level == 0)
            return amount;
        double factor = level > 0 ? (1 + 0.5 * level) : 1.0 / (1 + 0.5 * -level);
        return Math.Max(1, (int)Math.Round(amount * factor));
    }

}
