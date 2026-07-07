using System.Globalization;
using System.Text.Json;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the hardcoded DC1 item data (<see cref="DinoCrisis1.ItemPool"/> + <see cref="DinoCrisis1.KeyItemIds"/>)
/// to the canonical item table in <c>data/dc1/items.json</c>. The JSON <c>allItems</c> array is the
/// id→name→category ground truth (residentevil123 forum + <c>st10c.dat</c>). The code curates a
/// hand-weighted placement subset, so — unlike the species table — these tests do NOT force the pool's
/// weights or membership to equal the JSON's <c>pool</c>. They DO guarantee the realistic invariants:
/// <list type="bullet">
///   <item>every <see cref="DinoCrisis1.ItemPool"/> entry uses a real id whose <b>name</b> and
///   ammo/health <b>category</b> match the canonical table — so a typo'd id, a renamed item, or a
///   miscategorised pool entry fails the build;</item>
///   <item>every canonical <b>key</b> item is contained in the progression <see cref="DinoCrisis1.KeyItemIds"/>
///   set — so a key item can never silently fall out of logic. (The set is a contiguous range that may
///   also include a few unused ids; that superset direction is intentional and not asserted.)</item>
/// </list>
/// </summary>
public class ItemTableTests
{
    private sealed record ItemRow(string id, string name, string? category);
    private sealed record WeaponFamilyRow(string name, string[] ids);
    private sealed record ItemsFile(ItemRow[] allItems,
                                    Dictionary<string, string[]>? weaponAmmo,
                                    string[]? startingWeapons,
                                    Dictionary<string, string>? weaponPartUpgrades,
                                    Dictionary<string, string>? weaponPartBase,
                                    Dictionary<string, Dictionary<string, string>>? weaponUpgradeVariants,
                                    Dictionary<string, WeaponFamilyRow>? weaponFamilies);

    /// <summary>Parse a <c>"0x2B"</c>-form id into its byte value.</summary>
    private static int ParseId(string hex) =>
        int.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static ItemsFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "dc1", "items.json");
        Assert.True(File.Exists(path), $"items.json not found at {path}");
        var file = JsonSerializer.Deserialize<ItemsFile>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(file);
        Assert.NotNull(file!.allItems);
        return file;
    }

    [Fact]
    public void ItemPool_EveryEntry_MatchesCanonicalIdNameAndCategory()
    {
        var byId = Load().allItems.ToDictionary(r => ParseId(r.id));
        var pool = new DinoCrisis1().ItemPool;
        Assert.NotEmpty(pool);

        foreach (var e in pool)
        {
            Assert.True(byId.TryGetValue(e.ItemId, out var row),
                $"ItemPool id 0x{e.ItemId:X2} ('{e.Name}') is not in items.json allItems");
            Assert.Equal(row!.name, e.Name);
            Assert.Equal(e.Category.ToString().ToLowerInvariant(), row.category);
        }
    }

    [Fact]
    public void KeyItemIds_ContainEveryCanonicalKeyItem()
    {
        var keyIds = new DinoCrisis1().KeyItemIds;
        var canonicalKeyIds = Load().allItems
            .Where(r => r.category == "key")
            .Select(r => ParseId(r.id))
            .ToList();

        Assert.NotEmpty(canonicalKeyIds);
        foreach (var id in canonicalKeyIds)
            Assert.True(keyIds.Contains(id),
                $"items.json key item 0x{id:X2} is missing from DinoCrisis1.KeyItemIds");
    }

    [Fact]
    public void WeaponAmmoAndStartingWeapons_MatchDinoCrisis1()
    {
        // Lock the linked-ammo metadata: DinoCrisis1.AmmoForWeapon / StartingWeaponIds must equal the
        // items.json source of truth (ITEM-RANDO-PLAN.md), so the two never drift.
        var file = Load();
        Assert.NotNull(file.weaponAmmo);
        var game = new DinoCrisis1();

        foreach (var (weaponHex, ammoHex) in file.weaponAmmo!)
        {
            var weapon = ParseId(weaponHex);
            var expected = ammoHex.Select(ParseId).OrderBy(x => x).ToArray();
            var actual = game.AmmoForWeapon(weapon).OrderBy(x => x).ToArray();
            Assert.True(expected.SequenceEqual(actual),
                $"AmmoForWeapon(0x{weapon:X2}) [{string.Join(",", actual.Select(x => $"0x{x:X2}"))}] " +
                $"!= items.json [{string.Join(",", expected.Select(x => $"0x{x:X2}"))}]");
        }

        Assert.NotNull(file.startingWeapons);
        Assert.Equal(file.startingWeapons!.Select(ParseId).OrderBy(x => x),
                     game.StartingWeaponIds.OrderBy(x => x));

        Assert.NotNull(file.weaponPartUpgrades);
        foreach (var (partHex, weaponHex) in file.weaponPartUpgrades!)
            Assert.Equal(ParseId(weaponHex), game.WeaponUpgradeFromPart(ParseId(partHex)));

        // weaponPartBase (§7.3): DinoCrisis1.WeaponForPart must equal the items.json map.
        Assert.NotNull(file.weaponPartBase);
        foreach (var (partHex, baseHex) in file.weaponPartBase!)
            Assert.Equal(ParseId(baseHex), game.WeaponForPart(ParseId(partHex)));

        // weaponUpgradeVariants (§7.3): DinoCrisis1.WeaponUpgradeVariants must equal the items.json map.
        Assert.NotNull(file.weaponUpgradeVariants);
        foreach (var (baseHex, variants) in file.weaponUpgradeVariants!)
        {
            var expected = variants.Select(kv => (ParseId(kv.Key), ParseId(kv.Value))).OrderBy(x => x).ToArray();
            var actual = game.WeaponUpgradeVariants(ParseId(baseHex)).OrderBy(x => x).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void WeaponFamilies_MatchDinoCrisis1()
    {
        // Lock the per-family EnabledWeapons metadata (§7.4): every id in items.json.weaponFamilies maps to
        // that family via DinoCrisis1.WeaponFamilyOf, the family-name list matches in order, and the JSON
        // covers every weapon/weapon_part id exactly once (so no toggle is ever silently un-gated).
        var file = Load();
        Assert.NotNull(file.weaponFamilies);
        var game = new DinoCrisis1();

        // Map each JSON family key to the WeaponFamily flag via DinoCrisis1's ordered name list.
        var nameToFlag = game.WeaponFamilies.ToDictionary(f => f.Name, f => f.Flag);

        var seen = new HashSet<int>();
        foreach (var (_, fam) in file.weaponFamilies!)
        {
            Assert.True(nameToFlag.TryGetValue(fam.name, out var flag),
                $"items.json weapon family '{fam.name}' has no matching DinoCrisis1.WeaponFamilies entry");
            foreach (var idHex in fam.ids)
            {
                var id = ParseId(idHex);
                Assert.Equal(flag, game.WeaponFamilyOf(id));
                Assert.True(seen.Add(id), $"weapon id 0x{id:X2} appears in more than one family");
            }
        }

        // Every weapon + weapon_part id in allItems must be assigned to exactly one family.
        var weaponIds = file.allItems
            .Where(r => r.category is "weapon" or "weapon_part")
            .Select(r => ParseId(r.id));
        foreach (var id in weaponIds)
            Assert.True(seen.Contains(id), $"weapon id 0x{id:X2} is not assigned to any weaponFamilies entry");
    }
}
