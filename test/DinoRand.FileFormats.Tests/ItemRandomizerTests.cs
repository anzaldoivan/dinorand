using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for the item pass (ITEM-RANDO-PLAN.md): the ratio model, linked ammo, weapon
/// pool-placement (no clobber), and reachability filtering. Worlds are built in memory at the start
/// room (so they are reachable) — no game files needed.
/// </summary>
public class ItemRandomizerTests
{
    private static readonly DinoCrisis1 Game = new();
    private static readonly int Start = Game.StartRoomCode;            // 0x010d
    private static readonly int StStage = (Start >> 8) & 0xff, StRoom = Start & 0xff;

    private static ItemCategory CategoryOf(int itemId) =>
        Game.ItemPool.First(p => p.ItemId == itemId).Category;
    private static bool InPool(int itemId) => Game.ItemPool.Any(p => p.ItemId == itemId);

    // A reachable room (= the start room) of `count` rerollable pickups, plus optional weapon records
    // (so linked ammo spans the weapons' ammo types).
    private static RoomFile StartRoom(int count, params int[] weapons)
    {
        var room = new RoomFile(StStage, StRoom);
        int fo = 0;
        for (int i = 0; i < count; i++)
            room.Items.Add(new ItemRecord { ItemId = 0x16, OriginalItemId = 0x16, Amount = 1, FileOffset = fo++ });
        foreach (var w in weapons)
            room.Items.Add(new ItemRecord { ItemId = w, OriginalItemId = w, Amount = 1, FileOffset = fo++ });
        return room;
    }

    // A rerollable record carrying a position quad in Raw (X words at +04/+08/+0c/+10, Z at
    // +06/+0a/+0e/+12 — the real walker populates Raw; the unit harness must set it so the multi-cut
    // dedup can group co-located AOTs by position).
    private static ItemRecord QuadRec(int id, short x, short z, int fo)
    {
        var raw = new byte[44];
        foreach (var off in new[] { 0x04, 0x08, 0x0c, 0x10 }) BitConverter.GetBytes(x).CopyTo(raw, off);
        foreach (var off in new[] { 0x06, 0x0a, 0x0e, 0x12 }) BitConverter.GetBytes(z).CopyTo(raw, off);
        return new ItemRecord { ItemId = id, OriginalItemId = id, Amount = 1, FileOffset = fo, Raw = raw };
    }

    private static List<int> Reroll(RandomizerConfig config, int slots = 6000, int seed = 1, params int[] weapons)
    {
        var room = StartRoom(slots, weapons);
        var rooms = new[] { room };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(seed), config, _ => { });
        new ItemRandomizer().Apply(ctx);
        return room.Items.Select(i => i.ItemId).ToList();
    }

    // All four ammo-bearing weapons (handgun is the starting weapon) so the full ammo pool is linked,
    // matching the pre-linked-ammo distribution the ratio tests assert.
    private static readonly int[] FullAmmoWeapons = { 0x01, 0x07, 0x09 }; // Shotgun, Glock35, Grenade Gun

    private static double HealthShare(List<int> ids)
    {
        var pool = ids.Where(InPool).ToList();
        return pool.Count(id => CategoryOf(id) == ItemCategory.Health) / (double)pool.Count;
    }

    // --- Ratio model (unchanged semantics, now over the linked consumable pool) -------------------

    [Fact]
    public void DefaultRatios_ReproduceTheIntrinsicSplit()
    {
        // Equal ratios (16/16) → uniform scaling → the pool's intrinsic ~0.29 health split.
        Assert.InRange(HealthShare(Reroll(new RandomizerConfig(), weapons: FullAmmoWeapons)), 0.27, 0.32);
    }

    [Fact]
    public void ZeroRatios_FallBackToIntrinsicWeights()
    {
        Assert.InRange(HealthShare(Reroll(new RandomizerConfig { RatioAmmo = 0, RatioHealth = 0 },
                                          weapons: FullAmmoWeapons)), 0.27, 0.32);
    }

    [Fact]
    public void EqualRatios_ReproduceTheIntrinsicSequenceExactly()
    {
        // Equal scaling leaves roll/total unchanged → identical pick sequence for one seed. Weapon
        // placement + the start-region floor are ratio-independent, so the whole sequence matches.
        var zero = Reroll(new RandomizerConfig { RatioAmmo = 0, RatioHealth = 0 }, weapons: FullAmmoWeapons);
        var equal5 = Reroll(new RandomizerConfig { RatioAmmo = 5, RatioHealth = 5 }, weapons: FullAmmoWeapons);
        var equal16 = Reroll(new RandomizerConfig { RatioAmmo = 16, RatioHealth = 16 }, weapons: FullAmmoWeapons);
        Assert.Equal(zero, equal5);
        Assert.Equal(zero, equal16);
    }

    [Fact]
    public void HighAmmoRatio_ShiftsTheMixTowardAmmo()
    {
        var baseHealth = HealthShare(Reroll(new RandomizerConfig(), weapons: FullAmmoWeapons));
        var biasedHealth = HealthShare(Reroll(new RandomizerConfig { RatioAmmo = 31, RatioHealth = 1 },
                                              weapons: FullAmmoWeapons));
        Assert.True(biasedHealth < baseHealth);
        Assert.InRange(biasedHealth, 0.0, 0.05);
    }

    // --- Zeroed-category semantics (ITEM-RATIO-ZERO-PLAN.md): a zeroed ratio means EXACTLY zero of
    //     that category, including the start-region floor (which redistributes to the enabled one). ---

    [Fact]
    public void HealthRatioZero_ProducesNoHealthAnywhere()
    {
        // RatioHealth=0 (ammo>0) must yield zero health — previously the start-region floor leaked 1.
        // Checked on a large world AND a tiny one (where the floor used to dominate).
        foreach (var slots in new[] { 3000, 6 })
        {
            var ids = Reroll(new RandomizerConfig { RatioAmmo = 16, RatioHealth = 0 },
                             slots: slots, seed: 8, weapons: FullAmmoWeapons);
            Assert.DoesNotContain(ids.Where(InPool), id => CategoryOf(id) == ItemCategory.Health);
        }
    }

    [Fact]
    public void AmmoRatioZero_ProducesNoAmmoAnywhere()
    {
        // RatioAmmo=0 (health>0) must yield zero ammo — previously the start-region floor leaked 3.
        foreach (var slots in new[] { 3000, 6 })
        {
            var ids = Reroll(new RandomizerConfig { RatioAmmo = 0, RatioHealth = 16 },
                             slots: slots, seed: 8, weapons: FullAmmoWeapons);
            Assert.DoesNotContain(ids.Where(InPool), id => CategoryOf(id) == ItemCategory.Ammo);
        }
    }

    [Fact]
    public void ZeroedCategory_StartRegionFloorRedistributesToEnabledCategory()
    {
        // Health-only: the opening still gets its guaranteed consumable floor, drawn entirely from the
        // enabled (health) category. A near-empty tiny world isolates the floor.
        var ids = Reroll(new RandomizerConfig { RatioAmmo = 0, RatioHealth = 16 }, slots: 6, seed: 8,
                         weapons: FullAmmoWeapons);
        var health = ids.Count(id => InPool(id) && CategoryOf(id) == ItemCategory.Health);
        Assert.True(health >= 4, $"expected the redistributed floor (>=4 health), got {health}");
    }

    [Fact]
    public void AmmoQuantity_ScalesOnlyAmmoStacks_AndZeroIsNoOp()
    {
        List<int> RerollAmounts(byte qty, out List<int> ids)
        {
            var room = StartRoom(400, FullAmmoWeapons);
            foreach (var i in room.Items) i.Amount = 2;
            var rooms = new[] { room };
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(9),
                                               new RandomizerConfig { AmmoQuantity = qty }, _ => { });
            new ItemRandomizer().Apply(ctx);
            ids = room.Items.Select(i => i.ItemId).ToList();
            return room.Items.Select(i => i.Amount).ToList();
        }

        var zero = RerollAmounts(0, out _);
        Assert.All(zero, a => Assert.Equal(2, a));

        var scaled = RerollAmounts(4, out var ids);
        for (int i = 0; i < scaled.Count; i++)
            if (InPool(ids[i]))                              // weapons aren't scaled / pooled
                Assert.Equal(CategoryOf(ids[i]) == ItemCategory.Ammo ? 6 : 2, scaled[i]);
    }

    [Fact]
    public void AmmoReduction_ShrinksOnlyAmmoStacks_AndZeroIsNoOp()
    {
        List<int> RerollAmounts(byte reduction, out List<int> ids)
        {
            var room = StartRoom(400, FullAmmoWeapons);
            foreach (var i in room.Items) i.Amount = 6;
            var rooms = new[] { room };
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(9),
                                               new RandomizerConfig { AmmoReduction = reduction }, _ => { });
            new ItemRandomizer().Apply(ctx);
            ids = room.Items.Select(i => i.ItemId).ToList();
            return room.Items.Select(i => i.Amount).ToList();
        }

        var zero = RerollAmounts(0, out _);
        Assert.All(zero, a => Assert.Equal(6, a));           // reduction 0 = vanilla (no change)

        var shrunk = RerollAmounts(4, out var ids);          // ammo ÷ (1 + 0.5·4) = ÷3 → 6 becomes 2
        for (int i = 0; i < shrunk.Count; i++)
            if (InPool(ids[i]))                              // weapons aren't scaled / pooled
                Assert.Equal(CategoryOf(ids[i]) == ItemCategory.Ammo ? 2 : 6, shrunk[i]);
    }

    [Theory]
    [InlineData(4, 0, 6, 18)]
    [InlineData(0, 4, 6, 2)]
    public void AmmoQuantityChanges_SurviveRoomWriteRead(
        byte quantity, byte reduction, ushort originalAmount, int expectedAmount)
    {
        var file = SyntheticRoom.Dc1Room(
            new[] { new SyntheticRoom.Item(0x16, originalAmount) },
            Array.Empty<SyntheticRoom.Door>(),
            Array.Empty<SyntheticRoom.Enemy>());
        var room = RoomFile.Read(StStage, StRoom, file);
        var rooms = new[] { room };
        var config = new RandomizerConfig
        {
            ReplaceItemPool = true,
            RatioAmmo = 16,
            RatioHealth = 0,
            AmmoQuantity = quantity,
            AmmoReduction = reduction,
        };
        var context = new RandomizationContext(
            Game, rooms, RoomGraph.Build(rooms), new Seed(9), config, _ => { });

        new ItemRandomizer().Apply(context);
        var reread = RoomFile.Read(StStage, StRoom, room.Write());

        var item = Assert.Single(reread.Items);
        Assert.Equal(ItemCategory.Ammo, CategoryOf(item.ItemId));
        Assert.Equal(expectedAmount, item.Amount);
    }

    // --- Shuffle mode ----------------------------------------------------------------------------

    [Fact]
    public void ShuffleMode_PreservesTheItemMultiset()
    {
        var room = new RoomFile(StStage, StRoom);
        int fo = 0;
        foreach (var entry in Game.ItemPool)
            room.Items.Add(new ItemRecord { ItemId = entry.ItemId, OriginalItemId = entry.ItemId, Amount = 1, FileOffset = fo++ });
        var before = room.Items.Select(i => i.ItemId).OrderBy(x => x).ToList();

        var rooms = new[] { room };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(3),
                                           new RandomizerConfig { ReplaceItemPool = false }, _ => { });
        new ItemRandomizer().Apply(ctx);

        var after = room.Items.Select(i => i.ItemId).OrderBy(x => x).ToList();
        Assert.Equal(before, after);                                            // same multiset
        Assert.NotEqual(before, room.Items.Select(i => i.ItemId).ToList());     // order changed
    }

    [Fact]
    public void KeyAndEmptySlots_AreNeverRerolled()
    {
        var room = StartRoom(0);
        room.Items.Add(new ItemRecord { ItemId = 0xFF, OriginalItemId = 0xFF, FileOffset = 0 }); // empty
        int keyId = Game.KeyItemIds.First();
        room.Items.Add(new ItemRecord { ItemId = keyId, OriginalItemId = keyId, FileOffset = 1 }); // key

        var rooms = new[] { room };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(7), new RandomizerConfig(), _ => { });
        new ItemRandomizer().Apply(ctx);

        Assert.Equal(0xFF, room.Items[0].ItemId);
        Assert.Equal(keyId, room.Items[1].ItemId);
    }

    // --- v1 features: clobber-fix, linked ammo, reachability --------------------------------------

    [Fact]
    public void ReplaceMode_PlacesWeapons_NeverClobbersThemWithConsumables()
    {
        // The old bug: replace mode overwrote weapon pickups with ammo/health. Now the weapon survives.
        var room = StartRoom(40, 0x01); // a Shotgun among consumable spots
        var rooms = new[] { room };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(2),
                                           new RandomizerConfig(), _ => { }); // default = replace
        new ItemRandomizer().Apply(ctx);

        Assert.Contains(0x01, room.Items.Select(i => i.ItemId)); // Shotgun still obtainable
    }

    [Fact]
    public void LinkedAmmo_OnlyAmmoForGrantedWeapons()
    {
        // Only a Shotgun is granted (+ the starting Handgun). So ammo must be limited to shotgun shells
        // (0x10/0x11) and 9mm (0x16) — never grenade rounds, darts, or 40S&W.
        var ids = Reroll(new RandomizerConfig(), slots: 3000, seed: 4, weapons: 0x01);
        var ammo = ids.Where(id => InPool(id) && CategoryOf(id) == ItemCategory.Ammo).Distinct().ToHashSet();
        Assert.Subset(new HashSet<int> { 0x10, 0x11, 0x16 }, ammo);
        foreach (var forbidden in new[] { 0x12, 0x13, 0x14, 0x17, 0x18 })
            Assert.DoesNotContain(forbidden, ammo);
    }

    [Fact]
    public void LinkedAmmo_Glock35Placed_Yields40SWAmmo()
    {
        // Evidence for the conditional: when the Glock 35 (0x07) IS granted, its ammo (40S&W 0x17)
        // enters the linked pool and appears in the fill — proving the 0x17=0 in real seeds is because
        // the Glock 35 is never *placed* (it is an upgrade-only weapon), not a linking failure.
        var ids = Reroll(new RandomizerConfig(), slots: 3000, seed: 4, weapons: 0x07);
        var ammo = ids.Where(id => InPool(id) && CategoryOf(id) == ItemCategory.Ammo).Distinct().ToHashSet();
        Assert.Contains(0x17, ammo);
    }

    [Fact]
    public void LinkedAmmo_HandgunSlidesPartPlaced_Yields40SWAmmo()
    {
        // The dead-weapon fix: the Handgun Slides part (0x0e) upgrades the starting Glock 34 into the
        // Glock 35, which fires 40S&W (0x17). Placing the PART must link that ammo.
        var ids = Reroll(new RandomizerConfig(), slots: 3000, seed: 4, weapons: 0x0e);
        var ammo = ids.Where(id => InPool(id) && CategoryOf(id) == ItemCategory.Ammo).Distinct().ToHashSet();
        Assert.Contains(0x17, ammo);
    }

    [Fact]
    public void LinkedAmmo_NonUpgradePartPlaced_DoesNotIntroduceNewAmmo()
    {
        // Boundary: a part with no ammo-introducing upgrade (Shotgun Stock 0x0c) must NOT pull in
        // 40S&W — the link is narrow, only Handgun Slides → Glock 35 → 40S&W.
        var ids = Reroll(new RandomizerConfig(), slots: 3000, seed: 4, weapons: 0x0c);
        var ammo = ids.Where(id => InPool(id) && CategoryOf(id) == ItemCategory.Ammo).Distinct().ToHashSet();
        Assert.DoesNotContain(0x17, ammo);
    }

    [Fact]
    public void UnreachableRoom_IsLeftUntouched()
    {
        var start = StartRoom(20);
        var island = new RoomFile(5, 0x03);                 // 0x0503, no door from start → unreachable
        island.Items.Add(new ItemRecord { ItemId = 0x16, OriginalItemId = 0x16, Amount = 999, FileOffset = 0 });
        var rooms = new[] { start, island };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(5),
                                           new RandomizerConfig(), _ => { });
        new ItemRandomizer().Apply(ctx);

        Assert.Equal(999, island.Items[0].Amount);          // amount marker untouched → never rerolled
    }

    [Fact]
    public void ProtectedRoom_IsLeftVanilla_EvenWhenReachable()
    {
        // The Grenade Launcher set-piece rooms (0x060d/0x0610/0x0612) are progression-critical and
        // listed in DinoCrisis1.ItemProtectedRoomCodes — the pass must leave their pickups vanilla
        // even though they are reachable. A door from the start room makes 0x060d reachable, so the
        // only reason it stays untouched is the protection (not unreachability).
        int protectedCode = Game.ItemProtectedRoomCodes.First();   // 0x060d
        int pStage = (protectedCode >> 8) & 0xff, pRoom = protectedCode & 0xff;

        var start = StartRoom(20);
        start.Doors.Add(new DoorRecord
        {
            TargetStage = pStage, TargetRoom = pRoom, OriginalTargetStage = pStage,
            OriginalTargetRoom = pRoom, DoorType = 0,                // ungated → freely reachable
        });
        var setPiece = new RoomFile(pStage, pRoom);
        setPiece.Items.Add(new ItemRecord { ItemId = 0x09, OriginalItemId = 0x09, Amount = 1, FileOffset = 0 }); // Grenade Gun
        setPiece.Items.Add(new ItemRecord { ItemId = 0x18, OriginalItemId = 0x18, Amount = 4, FileOffset = 1 }); // Grenade rounds

        var rooms = new[] { start, setPiece };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(6),
                                           new RandomizerConfig(), _ => { });
        new ItemRandomizer().Apply(ctx);

        Assert.Equal(0x09, setPiece.Items[0].ItemId);      // Grenade Gun untouched
        Assert.Equal(1, setPiece.Items[0].Amount);
        Assert.Equal(0x18, setPiece.Items[1].ItemId);      // Grenade rounds untouched
        Assert.Equal(4, setPiece.Items[1].Amount);
    }

    // --- Weapon-upgrade chance (ITEM-RANDO-PLAN.md §7.3). An upgrade PART is placed only when its base
    //     weapon is in the seed and a chance roll passes; chance 1.0 (default) keeps every part. ---

    private static List<int> RerollWeapons(RandomizerConfig config, int seed, params int[] weaponsAndParts)
    {
        var room = new RoomFile(StStage, StRoom);
        int fo = 0;
        for (int i = 0; i < 60; i++)                                  // consumable spots to absorb omitted parts
            room.Items.Add(new ItemRecord { ItemId = 0x16, OriginalItemId = 0x16, Amount = 1, FileOffset = fo++ });
        foreach (var w in weaponsAndParts)
            room.Items.Add(new ItemRecord { ItemId = w, OriginalItemId = w, Amount = 1, FileOffset = fo++ });
        var rooms = new[] { room };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(seed), config, _ => { });
        new ItemRandomizer().Apply(ctx);
        return room.Items.Select(i => i.ItemId).ToList();
    }

    [Fact]
    public void WeaponUpgradeChance_Zero_OmitsUpgradeParts_ButKeepsBaseWeapons()
    {
        // Shotgun (0x01, base) + its Shotgun Parts (0x0b, upgrade). With chance 0 the part is omitted
        // (becomes a consumable) while the base weapon is still placed.
        var ids = RerollWeapons(new RandomizerConfig { WeaponUpgradeChance = 0.0 }, seed: 3, 0x01, 0x0b);
        Assert.Contains(0x01, ids);            // base weapon still placed
        Assert.DoesNotContain(0x0b, ids);      // upgrade part omitted
    }

    [Fact]
    public void DefaultWeaponUpgradeChance_PlacesUpgradeParts()
    {
        // Default 1.0 keeps the vanilla upgrade part (regression guard for byte-identical default).
        var ids = RerollWeapons(new RandomizerConfig(), seed: 3, 0x01, 0x0b);
        Assert.Contains(0x01, ids);
        Assert.Contains(0x0b, ids);
    }

    [Fact]
    public void UpgradePart_OmittedWhenBaseWeaponAbsent_EvenAtFullChance()
    {
        // Grenade-Gun part (0x0f, base 0x09) with NO Grenade Gun in the seed and not a starting weapon:
        // it must not be placed even at chance 1.0 (no dead upgrade).
        var ids = RerollWeapons(new RandomizerConfig(), seed: 3, 0x0f);
        Assert.DoesNotContain(0x0f, ids);
    }

    // --- Pre-upgraded weapon chance (ITEM-RANDO-PLAN.md §7.3, EXPERIMENTAL). A small chance a found base
    //     weapon is placed already-upgraded (its variant id) instead of base + loose part. The Grenade
    //     Gun has a single variant (0x09 -> 0x0a via 0x0f), so these are deterministic. ---

    [Fact]
    public void PreUpgradedWeaponChance_One_PlacesVariantInsteadOfBase()
    {
        // chance 1.0: the Grenade Gun (0x09) is placed as its upgraded variant (0x0a), not the base.
        var ids = RerollWeapons(new RandomizerConfig { PreUpgradedWeaponChance = 1.0 }, seed: 3, 0x09);
        Assert.Contains(0x0a, ids);            // pre-upgraded variant placed
        Assert.DoesNotContain(0x09, ids);      // base no longer present
    }

    [Fact]
    public void PreUpgradedWeaponChance_Default_KeepsBaseWeapon()
    {
        // Default 0.0: off — the base weapon is placed as-is, the variant never appears (byte-identical).
        var ids = RerollWeapons(new RandomizerConfig(), seed: 3, 0x09);
        Assert.Contains(0x09, ids);
        Assert.DoesNotContain(0x0a, ids);
    }

    [Fact]
    public void PreUpgradedWeapon_SuppressesTheConsumedPart()
    {
        // chance 1.0 with the consumed part (0x0f) also present: pre-upgrading 0x09->0x0a consumes 0x0f,
        // so the loose part is not separately placed.
        var ids = RerollWeapons(new RandomizerConfig { PreUpgradedWeaponChance = 1.0 }, seed: 3, 0x09, 0x0f);
        Assert.Contains(0x0a, ids);
        Assert.DoesNotContain(0x09, ids);
        Assert.DoesNotContain(0x0f, ids);      // the consumed part is suppressed
    }

    // --- Per-family EnabledWeapons toggles (ITEM-RANDO-PLAN.md §7.4). Clearing a family drops its base
    //     weapons, custom variants, and parts from pool placement (slots become consumables) and unlinks
    //     its ammo. Default = WeaponFamily.All = byte-identical to placing every family. ---

    [Fact]
    public void DisabledWeaponFamily_OmitsThoseWeapons_FromPlacement()
    {
        // Shotgun family disabled: a Shotgun (0x01) and its Shotgun Parts (0x0b) must vanish from the
        // pool (their slots become consumables), while the still-enabled Grenade Gun (0x09) is placed.
        var cfg = new RandomizerConfig { EnabledWeaponFamilies = WeaponFamily.Handgun | WeaponFamily.GrenadeGun };
        var ids = RerollWeapons(cfg, seed: 3, 0x01, 0x0b, 0x09);
        Assert.DoesNotContain(0x01, ids);   // shotgun base dropped
        Assert.DoesNotContain(0x0b, ids);   // shotgun part dropped
        Assert.Contains(0x09, ids);         // grenade gun (enabled) still placed
    }

    [Fact]
    public void DisabledWeaponFamily_OmitsLinkedAmmo()
    {
        // With the Shotgun disabled and not placed, its ammo (SG 0x10 / Slag 0x11) must not be linked into
        // the fill — but the always-granted starting Handgun keeps 9mm (0x16) present.
        var cfg = new RandomizerConfig { EnabledWeaponFamilies = WeaponFamily.Handgun | WeaponFamily.GrenadeGun };
        var ids = Reroll(cfg, slots: 3000, seed: 4, weapons: 0x01);
        var ammo = ids.Where(id => InPool(id) && CategoryOf(id) == ItemCategory.Ammo).Distinct().ToHashSet();
        Assert.Contains(0x16, ammo);        // 9mm still linked via the starting handgun
        Assert.DoesNotContain(0x10, ammo);  // no shotgun → no SG shells
        Assert.DoesNotContain(0x11, ammo);  // no shotgun → no slag
    }

    [Fact]
    public void DisabledHandgunFamily_KeepsStartingHandgunAmmo_ButDropsHandgunPickups()
    {
        // Disabling the Handgun family removes handgun *pickups/upgrades* (e.g. 0x06) but never the
        // starting Glock 34 — so 9mm (0x16) stays linked while the handgun pickup is dropped.
        var cfg = new RandomizerConfig { EnabledWeaponFamilies = WeaponFamily.Shotgun | WeaponFamily.GrenadeGun };
        var ids = RerollWeapons(cfg, seed: 3, 0x06);
        Assert.DoesNotContain(0x06, ids);   // handgun pickup dropped
        Assert.Contains(0x16, ids);         // 9mm still linked from the starting weapon
    }

    [Fact]
    public void DefaultEnabledWeaponFamilies_IsAllAndByteIdentical()
    {
        // The default (WeaponFamily.All) places every family and is identical to setting All explicitly —
        // the filter is a no-op at All (no RNG diverted), so behaviour is byte-identical to before §7.4.
        var baseline = RerollWeapons(new RandomizerConfig(), seed: 7, 0x01, 0x09);
        var explicitAll = RerollWeapons(new RandomizerConfig { EnabledWeaponFamilies = WeaponFamily.All },
                                        seed: 7, 0x01, 0x09);
        Assert.Equal(baseline, explicitAll);
        Assert.Contains(0x01, baseline);    // both weapon families placed
        Assert.Contains(0x09, baseline);
    }

    [Fact]
    public void PlugPickups_AreConservedByItemPass()
    {
        // Plugs (0x2b) are the consumable that opens emergency boxes and are key items, so the item pass
        // must never reroll them into consumables — the §7.4 plug-economy guarantee (PlugEconomy counts
        // the surviving plug supply). A loose plug among consumable spots stays exactly one plug.
        var room = StartRoom(200);
        room.Items.Add(new ItemRecord { ItemId = 0x2b, OriginalItemId = 0x2b, Amount = 1, FileOffset = 999 });
        var rooms = new[] { room };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(5),
                                           new RandomizerConfig(), _ => { });
        new ItemRandomizer().Apply(ctx);

        Assert.Equal(0x2b, room.Items.Single(i => i.FileOffset == 999).ItemId);   // plug untouched
        Assert.Equal(1, room.Items.Count(i => i.ItemId == 0x2b));                  // not duplicated/created
    }

    // --- Multi-camera-cut record dedup (ITEM-RANDO-PLAN.md §7.4). A single physical pickup decodes as
    //     several 0x28 AOT records at one position quad (e.g. st308's Med Pak M ×2); they must be kept
    //     consistent so a spot can't show different items per camera angle. ---

    [Fact]
    public void ExplicitLogicalPickup_AllCopiesGetOneConservedItemAndAmount()
    {
        var room = new RoomFile(StStage, StRoom);
        var first = QuadRec(0x16, 100, 10, 1); first.Amount = 15;
        var copy = QuadRec(0x16, 200, 20, 2); copy.Amount = 15;
        var health = QuadRec(0x1c, 300, 30, 3); health.Amount = 2;
        var aid = QuadRec(0x1d, 400, 40, 4); aid.Amount = 3;
        room.Items.AddRange(new[] { first, copy, health, aid });
        var rooms = new[] { room };
        var graph = RoomGraph.Build(rooms);
        foreach (var ni in graph.Nodes.SelectMany(n => n.Items).Where(n => n.Record == first || n.Record == copy))
            ni.LogicalId = new LogicalPickupId("explicit-two-record-pickup");
        var before = new[] { (0x16, 15), (0x1c, 2), (0x1d, 3) }.Order().ToArray();
        var ctx = new RandomizationContext(Game, rooms, graph, new Seed(1),
                                           new RandomizerConfig { ReplaceItemPool = false }, _ => { });
        new ItemRandomizer().Apply(ctx);

        Assert.Equal((first.ItemId, first.Amount), (copy.ItemId, copy.Amount));
        Assert.Equal(before, new[]
        {
            (first.ItemId, first.Amount), (health.ItemId, health.Amount), (aid.ItemId, aid.Amount),
        }.Order().ToArray());
    }

    [Fact]
    public void Spoiler_ListsEveryChangedPhysicalRecordOnce_AndOmitsUnchangedRecords()
    {
        var room = new RoomFile(StStage, StRoom);
        var first = QuadRec(0x16, 100, 10, 0x100); first.Amount = 15;
        var copy = QuadRec(0x16, 200, 20, 0x200); copy.Amount = 15;
        room.Items.AddRange(new[] { first, copy });
        for (int i = 0; i < 10; i++)
        {
            var record = QuadRec(i % 2 == 0 ? 0x1c : 0x1d,
                (short)(300 + i), (short)(30 + i), 0x300 + i);
            record.Amount = i + 1;
            room.Items.Add(record);
        }
        var rooms = new[] { room };
        var graph = RoomGraph.Build(rooms);
        foreach (var item in graph.Nodes.SelectMany(n => n.Items)
                     .Where(n => n.Record == first || n.Record == copy))
            item.LogicalId = new LogicalPickupId("explicit-two-record-pickup");
        var before = room.Items.ToDictionary(r => r.FileOffset, r => (r.ItemId, r.Amount));
        var context = new RandomizationContext(Game, rooms, graph, new Seed(7),
            new RandomizerConfig { ReplaceItemPool = false }, _ => { });

        new ItemRandomizer().Apply(context);

        var section = Assert.Single(context.Spoiler.Sections,
            section => section.Title == "Items (DC1)");
        Assert.Equal(new[] { "Room", "Vanilla item", "New item" }, section.Columns);
        var changed = room.Items.Where(record =>
            before[record.FileOffset] != (record.ItemId, record.Amount)).ToArray();
        var unchanged = room.Items.Except(changed).ToArray();
        Assert.NotEmpty(changed);
        Assert.Equal(changed.Length, section.Rows.Count);
        foreach (var record in changed)
            Assert.Single(section.Rows, row =>
                row[0].Contains($"record 0x{record.FileOffset:X}", StringComparison.Ordinal));
        foreach (var record in unchanged)
            Assert.DoesNotContain(section.Rows, row =>
                row[0].Contains($"record 0x{record.FileOffset:X}", StringComparison.Ordinal));
    }

    [Fact]
    public void SameIdAndQuadWithoutExplicitGroup_RemainIndependentLogicalPickups()
    {
        var room = new RoomFile(StStage, StRoom);
        for (int i = 0; i < 12; i++)
        {
            var a = QuadRec(0x16, (short)i, (short)-i, i * 2); a.Amount = i * 2 + 1;
            var b = QuadRec(0x16, (short)i, (short)-i, i * 2 + 1); b.Amount = i * 2 + 2;
            room.Items.Add(a); room.Items.Add(b);
        }
        var before = room.Items.Select(r => (r.ItemId, r.Amount)).Order().ToArray();
        var rooms = new[] { room };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(2),
            new RandomizerConfig { ReplaceItemPool = false }, _ => { });

        new ItemRandomizer().Apply(ctx);

        Assert.Equal(before, room.Items.Select(r => (r.ItemId, r.Amount)).Order().ToArray());
        for (int i = 0; i < 12; i++)
            Assert.NotEqual(room.Items[i * 2].Amount, room.Items[i * 2 + 1].Amount);
    }

    [Fact]
    public void MixedIdsAtSameQuad_AreNotMerged()
    {
        // Different original ids sharing one quad (the Grenade-Gun-+-ammo shape) are DIFFERENT pickups
        // and must stay independent — the dedup keys on (room, quad, original id), not quad alone. If it
        // wrongly grouped by quad, every (ammo,health) pair would be forced identical.
        var room = new RoomFile(StStage, StRoom);
        int fo = 0;
        var a = new List<ItemRecord>(); var h = new List<ItemRecord>();
        for (int i = 0; i < 25; i++)
        {
            short x = (short)(200 + i), z = (short)i;
            var ra = QuadRec(0x16, x, z, fo++);   // ammo-origin
            var rh = QuadRec(0x1c, x, z, fo++);   // health-origin, SAME quad
            room.Items.Add(ra); room.Items.Add(rh); a.Add(ra); h.Add(rh);
        }
        var rooms = new[] { room };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(1),
                                           new RandomizerConfig(), _ => { });
        new ItemRandomizer().Apply(ctx);

        int differing = Enumerable.Range(0, 25).Count(i => a[i].ItemId != h[i].ItemId);
        Assert.True(differing > 0, "mixed-id records at one quad must not be collapsed into one item");
    }

    private sealed class EdgeRequirement : IRequirementOverlay
    {
        public IReadOnlyDictionary<int, RegionSplit> NodeSplits =>
            new Dictionary<int, RegionSplit>();

        public void ApplyTo(RoomGraph graph)
        {
            foreach (var edge in graph.Nodes.Where(n => n.Code == Start).SelectMany(n => n.Edges))
                if (edge.Target.Code == 0x0200)
                    edge.Requires = Requirement.OfItems(0x62);
        }
    }

    [Fact]
    public void OverlayGatedButPlayableOrdinaryLocation_IsEligible()
    {
        bool changed = false;
        for (int seed = 1; seed <= 12 && !changed; seed++)
        {
            var start = StartRoom(0);
            start.Items.Add(new ItemRecord
            {
                ItemId = 0x62, OriginalItemId = 0x62, Amount = 1, FileOffset = 1,
            });
            start.Items.Add(new ItemRecord
            {
                ItemId = 0x1c, OriginalItemId = 0x1c, Amount = 2, FileOffset = 2,
            });
            start.Doors.Add(new DoorRecord { TargetStage = 2, TargetRoom = 0 });
            var gated = new RoomFile(2, 0);
            var pickup = new ItemRecord
            {
                ItemId = 0x16, OriginalItemId = 0x16, Amount = 999, FileOffset = 3,
            };
            gated.Items.Add(pickup);
            var rooms = new[] { start, gated };
            var graph = RoomGraph.Build(rooms, new EdgeRequirement());
            var ctx = new RandomizationContext(Game, rooms, graph, new Seed(seed),
                new RandomizerConfig { ReplaceItemPool = false }, _ => { });

            new ItemRandomizer().Apply(ctx);
            changed = pickup.ItemId != 0x16 || pickup.Amount != 999;
        }
        Assert.True(changed, "an authored key entitlement must open an overlay-gated playable pickup");
    }

    [Fact]
    public void ExcludedAndEndingLogicalPickups_RemainUnchanged()
    {
        var start = StartRoom(10);
        var excluded = start.Items[0]; excluded.Amount = 81;
        var endingCode = Game.EndingZoneRoomCodes.First();
        start.Doors.Add(new DoorRecord
        {
            TargetStage = endingCode >> 8, TargetRoom = endingCode & 0xff,
        });
        var ending = new RoomFile(endingCode >> 8, endingCode & 0xff);
        var sink = new ItemRecord { ItemId = 0x16, OriginalItemId = 0x16, Amount = 82, FileOffset = 100 };
        ending.Items.Add(sink);
        var rooms = new[] { start, ending };
        var graph = RoomGraph.Build(rooms);
        graph.Nodes.SelectMany(n => n.Items).Single(n => n.Record == excluded).Excluded = true;
        var ctx = new RandomizationContext(Game, rooms, graph, new Seed(4), new RandomizerConfig(), _ => { });

        new ItemRandomizer().Apply(ctx);

        Assert.Equal((0x16, 81), (excluded.ItemId, excluded.Amount));
        Assert.Equal((0x16, 82), (sink.ItemId, sink.Amount));
    }

    [Fact]
    public void ReplacementMode_UsesLogicalSiteCountAndSynchronizesEveryPhysicalMember()
    {
        static (RoomFile Room, RoomGraph Graph, ItemRecord? Copy) World(bool withCopy)
        {
            var room = StartRoom(10);
            ItemRecord? copy = null;
            if (withCopy)
            {
                copy = new ItemRecord
                {
                    ItemId = 0x16, OriginalItemId = 0x16, Amount = 1, FileOffset = 100,
                };
                room.Items.Insert(1, copy);
            }
            var graph = RoomGraph.Build(new[] { room });
            if (copy is not null)
                foreach (var ni in graph.Nodes.SelectMany(n => n.Items)
                             .Where(n => n.Record == room.Items[0] || n.Record == copy))
                    ni.LogicalId = new LogicalPickupId("replacement-copy");
            return (room, graph, copy);
        }

        var baseline = World(withCopy: false);
        var grouped = World(withCopy: true);
        var config = new RandomizerConfig { ReplaceItemPool = true };
        new ItemRandomizer().Apply(new RandomizationContext(Game, new[] { baseline.Room }, baseline.Graph,
            new Seed(44), config, _ => { }));
        var log = new List<string>();
        new ItemRandomizer().Apply(new RandomizationContext(Game, new[] { grouped.Room }, grouped.Graph,
            new Seed(44), config, log.Add));

        Assert.Equal(baseline.Room.Items.Select(r => (r.ItemId, r.Amount)),
            grouped.Room.Items.Where(r => r != grouped.Copy).Select(r => (r.ItemId, r.Amount)));
        Assert.Equal((grouped.Room.Items[0].ItemId, grouped.Room.Items[0].Amount),
                     (grouped.Copy!.ItemId, grouped.Copy.Amount));
        Assert.Contains(log, line => line.Contains("rerolled 10 logical non-key pickups"));
    }

    [Fact]
    public void SingletonShuffle_CompatibilitySequence_IsStable()
    {
        var room = new RoomFile(StStage, StRoom);
        for (int i = 0; i < 8; i++)
            room.Items.Add(new ItemRecord
            {
                ItemId = 0x10 + i, OriginalItemId = 0x10 + i, Amount = 1, FileOffset = i,
            });
        var rooms = new[] { room };
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(31415),
            new RandomizerConfig { ReplaceItemPool = false }, _ => { });

        new ItemRandomizer().Apply(ctx);

        Assert.Equal(new[] { 0x15, 0x11, 0x17, 0x10, 0x12, 0x14, 0x13, 0x16 },
                     room.Items.Select(r => r.ItemId));
    }

    // --- Item priorities (ITEM-RANDO-PLAN.md §7.1). A pickup marked Fixed stays exactly vanilla — a
    //     per-item form of room protection; Normal siblings still reroll. ---

    [Fact]
    public void FixedPriorityItem_StaysVanilla_WhileNormalSiblingsReroll()
    {
        var room = StartRoom(40);                       // 40 Normal consumable spots in the start room
        var fixedRec = new ItemRecord { ItemId = 0x1e, OriginalItemId = 0x1e, Amount = 7, FileOffset = 999 };
        room.Items.Add(fixedRec);                        // a Med Pak L we will pin as Fixed

        var rooms = new[] { room };
        var graph = RoomGraph.Build(rooms);
        // Mark only the Med Pak L's NodeItem Fixed (priority is stamped onto the graph from map.json).
        var ni = graph.Nodes.Single(n => n.Code == Start).Items.Single(i => i.Record == fixedRec);
        ni.Priority = ItemPriority.Fixed;

        var ctx = new RandomizationContext(Game, rooms, graph, new Seed(4), new RandomizerConfig(), _ => { });
        new ItemRandomizer().Apply(ctx);

        Assert.Equal(0x1e, fixedRec.ItemId);             // Fixed pickup untouched (id + amount)
        Assert.Equal(7, fixedRec.Amount);
        // Sanity: the Normal siblings did get rerolled (not all still 0x16).
        Assert.Contains(room.Items.Where(i => i != fixedRec), i => i.ItemId != 0x16);
    }

    [Fact]
    public void StartRegion_GetsAGuaranteedConsumableFloor()
    {
        var ids = Reroll(new RandomizerConfig { RatioAmmo = 31, RatioHealth = 1 }, slots: 30, seed: 8,
                         weapons: FullAmmoWeapons);
        // Even with health ratio ~0, the start-region floor guarantees at least one health pickup.
        Assert.Contains(ids.Where(InPool), id => CategoryOf(id) == ItemCategory.Health);
    }

    // --- Starting-weapon beatability coupling (docs/dc1/STARTING-INVENTORY.md) ----------------------

    [Fact]
    public void RemovedStartWeapon_IsForcePlacedInTheStartRegion()
    {
        // Empty StartingWeapons removes the vanilla Handgun (0x05) from the start kit → the item pass must
        // place it as a reachable world pickup so the seed stays beatable.
        var ids = Reroll(new RandomizerConfig { StartingWeapons = Array.Empty<int>() }, slots: 50, seed: 3);
        Assert.Contains(0x05, ids);
    }

    [Fact]
    public void KeptStartWeapon_IsNotInjectedIntoTheWorld()
    {
        // Default (StartingWeapons null = vanilla {0x05}) keeps the Handgun in the start kit, so it is NOT
        // force-placed (the start room has no 0x05 record otherwise).
        var ids = Reroll(new RandomizerConfig(), slots: 50, seed: 3);
        Assert.DoesNotContain(0x05, ids);
    }

    [Fact]
    public void ForcedStartWeapon_IsLocked_NotOverwrittenByConsumableFill()
    {
        // The forced weapon occupies exactly one slot and survives the consumable fill (it is removed from
        // the rerollable set), so it appears exactly once.
        var ids = Reroll(new RandomizerConfig { StartingWeapons = Array.Empty<int>() }, slots: 50, seed: 11);
        Assert.Equal(1, ids.Count(id => id == 0x05));
    }
}
