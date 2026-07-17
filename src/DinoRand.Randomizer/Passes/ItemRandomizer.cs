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

    private readonly record struct Slot(int Room, ItemRecord Record, string? Link = null,
                                        PickupVisual Visual = PickupVisual.GenericPanel);

    public void Apply(RandomizationContext context)
    {
        var rng = context.Seed.RngFor(Name);
        var game = context.Game;
        var graph = context.Graph;
        var keyItems = game.KeyItemIds;

        // Reachable playable world (door keys already seated by ProgressionPass) and the no-key
        // starting region — the two reachability frontiers the fill respects.
        var doorKeys = DoorKeys(graph, game);
        var reach = KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, doorKeys);
        var startRegion = KeyItemPlacer.Reachable(graph, game, game.StartRoomCode, new HashSet<int>());

        // Rerollable pickups: real (non-empty), non-key records in reachable rooms. (A key seated
        // here by ProgressionPass is left alone.) Reading off the graph nodes scopes us to `reach`.
        var slots = new List<Slot>();
        foreach (var node in graph.Nodes)
        {
            if (!reach.Contains(node.Code)) continue;
            // Set-piece rooms whose specific items are progression-critical (e.g. the Grenade Launcher
            // finale caches) stay vanilla — never collected as slots, so never rerolled/pool-placed.
            if (game.ItemProtectedRoomCodes.Contains(node.Code)) continue;
            foreach (var ni in node.Items)
            {
                var r = ni.Record;
                if (r.IsEmptySlot || keyItems.Contains(r.ItemId)) continue;
                // Fixed-priority pickups (map.json item overlay) stay exactly vanilla — the per-item
                // form of room protection: never collected, so never rerolled or pool-placed.
                if (ni.Priority == ItemPriority.Fixed) continue;
                slots.Add(new Slot(node.Code, r, ni.Link, ni.Visual));
            }
        }

        if (slots.Count == 0)
        {
            context.Log("[items] no rerollable pickups in the reachable world");
            context.Spoiler.Section(SpoilerSectionTitle, SpoilerColumns)
                .AddNote("no rerollable pickups in the reachable world");
            return;
        }

        // Vanilla amounts, snapshotted before any mutation, so the spoiler can detect an
        // amount-only change (e.g. the ammo-quantity dial landing on the vanilla item id).
        // ItemRecord has identity equality, so the default comparer is per-record.
        var vanillaAmounts = slots.ToDictionary(s => s.Record, s => s.Record.Amount);

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
            return;
        }

        if (context.Config.ReplaceItemPool)
            ReplaceMode(slots, startRegion, context, rng);
        else
            ShuffleExisting(slots, rng);

        // A single physical pickup can decode as several 0x28 AOT records at one position quad (camera-
        // cut / state copies — e.g. st308's Med Pak ×2). The steps above assign each record
        // independently, so a spot could otherwise show different items per camera angle. Collapse each
        // group of same-(room, quad, original-id) records onto its first record's result. Runs after
        // assignment and never touches the RNG, so non-duplicate pickups stay byte-identical.
        SyncDuplicatePickups(slots);

        // Relocation twins authored at *different* positions (the map.json itemLinks overlay) are one
        // logical pickup the quad grouping above can't see — mirror them onto the group's first result
        // so a linked id can't desync or duplicate across its copies. Inert for today's data (every
        // shuffleable record is unlinked; linked twins are key items the slot collection already
        // excludes), correct for any future non-key twin.
        SyncLinkedPickups(slots);

        var mode = context.Config.ReplaceItemPool ? "rerolled" : "shuffled";
        context.Log($"[items] {mode} {slots.Count} non-key pickups across " +
                    $"{slots.Select(s => s.Room).Distinct().Count()} reachable rooms");

        RecordSpoiler(context, slots, vanillaAmounts, mode);
    }

    private const string SpoilerSectionTitle = "Items (DC1)";
    private static readonly string[] SpoilerColumns = { "Room", "Vanilla item", "New item" };

    /// <summary>
    /// Record the per-pickup diff (docs/decisions/cross/SPOILER-LOG-PLAN.md §4): one row per <b>physical</b>
    /// pickup whose item or amount changed — AOT copies and relocation twins collapse onto their
    /// canonical record, mirroring exactly how <see cref="SyncDuplicatePickups"/> /
    /// <see cref="SyncLinkedPickups"/> grouped them. Runs after all assignment; no RNG, no I/O.
    /// </summary>
    private static void RecordSpoiler(RandomizationContext context, List<Slot> slots,
                                      Dictionary<ItemRecord, int> vanillaAmounts, string mode)
    {
        var section = context.Spoiler.Section(SpoilerSectionTitle, SpoilerColumns);
        var seenQuads = new HashSet<(int, int, string)>();
        var seenLinks = new HashSet<(int, string?)>();
        int changed = 0;
        foreach (var s in slots)
        {
            if (HasQuad(s.Record) && !seenQuads.Add(PickupKey(s))) continue;   // AOT copy
            if (s.Link is not null && !seenLinks.Add((s.Room, s.Link))) continue; // relocation twin
            var rec = s.Record;
            int vanillaAmount = vanillaAmounts.GetValueOrDefault(rec, rec.Amount);
            if (rec.ItemId == rec.OriginalItemId && rec.Amount == vanillaAmount) continue;
            section.AddRow(Spoiler.Dc1RoomNames.Describe(s.Room),
                           Describe(rec.OriginalItemId, vanillaAmount),
                           Describe(rec.ItemId, rec.Amount));
            changed++;
        }
        section.AddNote($"mode: {mode} ({(context.Config.ReplaceItemPool ? "replace-with-pool" : "shuffle existing")}); "
            + $"{changed} of {slots.Count} rerollable pickup(s) changed");
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
        var assigned = new HashSet<ItemRecord>();

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
                spot.Record.ItemId = placeId;
                spot.Record.Amount = Math.Max(1, spot.Record.Amount);
                assigned.Add(spot.Record);
                placedWeapons.Add(placeId);
            }
        }
        else
        {
            // Keep weapon/part pickups as their vanilla item (still never clobbered by consumables).
            foreach (var s in slots)
                if (weaponSet.Contains(s.Record.OriginalItemId))
                {
                    s.Record.ItemId = s.Record.OriginalItemId;
                    assigned.Add(s.Record);
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

        var fill = slots.Where(s => !assigned.Contains(s.Record)).OrderBy(_ => rng.Next()).ToList();
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
        var floored = new HashSet<ItemRecord>();
        for (int i = 0; i < ammoFloor; i++) { SetItem(early[i].Record, WeightedPick(ammo, rng), cfg); floored.Add(early[i].Record); }
        for (int i = 0; i < healthFloor; i++) { SetItem(early[ammoFloor + i].Record, WeightedPick(health, rng), cfg); floored.Add(early[ammoFloor + i].Record); }

        foreach (var s in fill)
        {
            if (floored.Contains(s.Record)) continue;
            SetItem(s.Record, WeightedPick(consumables, weights, total, rng), cfg);
        }
    }

    /// <summary>Force each removed starting weapon into a distinct no-key start-region pickup and lock it
    /// (remove from <paramref name="slots"/>), so a seed that strips a start weapon still hands the player one
    /// before any key gate. Picks deterministically from <paramref name="rng"/>; if the start region has too
    /// few free pickups it logs a warning and places what it can (the EXE patch still removed the grant).</summary>
    private static void ForcePlaceRemovedStartWeapons(List<Slot> slots, IReadOnlyList<int> weapons,
                                                      IReadOnlySet<int> startRegion, Random rng, RandomizationContext context)
    {
        var shuffled = slots.Where(s => startRegion.Contains(s.Room)).OrderBy(_ => rng.Next()).ToList();
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
            slot.Record.ItemId = weapon;
            slot.Record.Amount = 1;
            slots.Remove(slot);
            context.Log($"[items] start weapon 0x{weapon:X2} removed from inventory → placed in start-region room 0x{slot.Room:X3}");
            // Locked out of the rerollable set above, so the main spoiler sweep never sees it —
            // record the forced placement here (docs/decisions/cross/SPOILER-LOG-PLAN.md §4).
            context.Spoiler.Section(SpoilerSectionTitle, SpoilerColumns)
                .AddRow(Spoiler.Dc1RoomNames.Describe(slot.Room),
                        Spoiler.Dc1ItemNames.NameOf(slot.Record.OriginalItemId),
                        $"{Spoiler.Dc1ItemNames.NameOf(weapon)} (removed start weapon, force-placed)");
        }
    }

    private static void SetItem(ItemRecord rec, ItemPoolEntry pick, RandomizerConfig cfg)
    {
        rec.ItemId = pick.ItemId;
        rec.Amount = ScaledAmount(pick.Category, Math.Max(1, rec.Amount), cfg.AmmoQuantity, cfg.AmmoReduction);
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

    /// <summary>Shuffle mode: keep the vanilla multiset of items but reassign which slot each lands in
    /// (Fisher–Yates over the ids). Each slot keeps its own count. Reach-filtered slots only.</summary>
    private static void ShuffleExisting(List<Slot> slots, Random rng)
    {
        var ids = slots.Select(s => s.Record.ItemId).ToArray();
        for (int i = ids.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }
        for (int i = 0; i < slots.Count; i++)
        {
            slots[i].Record.ItemId = ids[i];
            slots[i].Record.Amount = Math.Max(1, slots[i].Record.Amount);
        }
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

    /// <summary>Byte offset / length of the placement's 2D position quad within an item record's raw
    /// bytes (four corner X/Z words; constant Y). Used only to identify co-located AOT duplicates.</summary>
    private const int QuadOffset = 0x04;
    private const int QuadLength = 0x10;

    /// <summary>True when this record carries a decoded position quad (the real walker fills
    /// <see cref="ItemRecord.Raw"/>; records without it — e.g. synthetic ones — are never grouped).</summary>
    private static bool HasQuad(ItemRecord r) => r.Raw is { Length: >= QuadOffset + QuadLength };

    /// <summary>Grouping key for one physical pickup: room + the position quad + the original item id.
    /// Same key ⇒ the same pickup's AOT copies; a different id at the same quad is a different pickup.</summary>
    private static (int, int, string) PickupKey(Slot s)
        => (s.Room, s.Record.OriginalItemId, Convert.ToHexString(s.Record.Raw, QuadOffset, QuadLength));

    /// <summary>Give every AOT copy of one physical pickup the same item as its first record, so a spot
    /// can't show different items per camera angle. Records lacking a quad are each their own group.</summary>
    private static void SyncDuplicatePickups(List<Slot> slots)
    {
        foreach (var group in slots.Where(s => HasQuad(s.Record)).GroupBy(PickupKey))
        {
            var copies = group.ToList();
            if (copies.Count <= 1) continue;
            var canon = copies[0].Record;
            foreach (var s in copies.Skip(1))
            {
                s.Record.ItemId = canon.ItemId;
                s.Record.Amount = canon.Amount;
            }
        }
    }

    /// <summary>Mirror every linked slot (a relocation twin tagged by the map.json <c>itemLinks</c>
    /// overlay) onto the first record of its <c>(room, link)</c> group, so the one logical pickup keeps
    /// a single assignment even when its copies sit at different positions.</summary>
    private static void SyncLinkedPickups(List<Slot> slots)
    {
        foreach (var group in slots.Where(s => s.Link is not null).GroupBy(s => (s.Room, s.Link)))
        {
            var copies = group.ToList();
            if (copies.Count <= 1) continue;
            var canon = copies[0].Record;
            foreach (var s in copies.Skip(1))
            {
                s.Record.ItemId = canon.ItemId;
                s.Record.Amount = canon.Amount;
            }
        }
    }

    private static HashSet<int> DoorKeys(RoomGraph graph, GameDefinition game)
    {
        var keys = new HashSet<int>();
        foreach (var node in graph.Nodes)
            foreach (var edge in node.Edges)
                foreach (var k in game.KeyItemsForDoor(edge.Door.DoorType))
                    keys.Add(k);
        return keys;
    }
}
