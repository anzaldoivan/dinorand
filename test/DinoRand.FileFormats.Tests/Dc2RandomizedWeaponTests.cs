using System.Text.Json;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public class Dc2RandomizedWeaponTests
{
    private const uint NameVa = 0x71BEC8;
    private const int NameOffset = 0x100EC8;

    private static byte[] MakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        foreach (var (id, flags) in Dc2OwnerBits.VanillaFlags)
            BitConverter.GetBytes(flags).CopyTo(exe, Dc2OwnerBits.FlagsOffset(id));

        "WEP_TEST.DAT\0"u8.CopyTo(exe.AsSpan(NameOffset));
        foreach (byte id in Enumerable.Range(0, 12).Select(i => (byte)i))
        {
            if (Dc2OwnerBits.HasOwner(exe, id, Dc2WeaponOwner.Regina))
                SetSlot(exe, Dc2CrossCharTarget.Regina, id, NameVa);
            if (Dc2OwnerBits.HasOwner(exe, id, Dc2WeaponOwner.Dylan))
                SetSlot(exe, Dc2CrossCharTarget.Dylan, id, NameVa);
        }
        return exe;
    }

    private static void SetSlot(byte[] exe, Dc2CrossCharTarget target, byte id, uint value)
        => BitConverter.GetBytes(value).CopyTo(exe,
            Dc2CrossCharWeaponPatch.CatalogBaseOffset
            + Dc2CrossCharWeaponPatch.CatalogSlot(target, id) * 4);

    [Fact]
    public void Same_seed_produces_the_same_three_three_layout()
    {
        var a = Dc2RandomizedWeaponPlanner.Plan(new Seed(12345));
        var b = Dc2RandomizedWeaponPlanner.Plan(new Seed(12345));

        Assert.Equal(a.ReginaOnly, b.ReginaOnly);
        Assert.Equal(a.DylanOnly, b.DylanOnly);
        Assert.Equal(3, a.ReginaOnly.Count);
        Assert.Equal(3, a.DylanOnly.Count);
        Assert.Empty(a.ReginaOnly.Intersect(a.DylanOnly));
        Assert.Equal(Dc2RandomizedWeaponPatch.Domain.Order(),
            a.ReginaOnly.Concat(a.DylanOnly).Order());
    }

    [Fact]
    public void Fixed_distinct_seeds_produce_different_valid_layouts()
    {
        var a = Dc2RandomizedWeaponPlanner.Plan(new Seed(1));
        var b = Dc2RandomizedWeaponPlanner.Plan(new Seed(2));

        Assert.NotEqual(a.ReginaOnly, b.ReginaOnly);
        Assert.All(new[] { a, b }, plan =>
        {
            Assert.Equal(3, plan.ReginaOnly.Count);
            Assert.Equal(3, plan.DylanOnly.Count);
            Assert.Empty(plan.ReginaOnly.Intersect(plan.DylanOnly));
        });
    }

    [Fact]
    public void Apply_sets_random_domain_and_every_pinned_owner_exactly()
    {
        var exe = MakeExe();
        var plan = Dc2RandomizedWeaponPlanner.Plan(new Seed(77));

        Dc2RandomizedWeaponPatch.Apply(exe, plan.ReginaOnly);

        Assert.All(plan.ReginaOnly, id => AssertOwners(exe, id, regina: true, dylan: false));
        Assert.All(plan.DylanOnly, id => AssertOwners(exe, id, regina: false, dylan: true));
        AssertOwners(exe, 0x01, false, true);  // starter
        AssertOwners(exe, 0x02, true, false);  // starter
        foreach (byte id in new byte[] { 0x00, 0x05, 0x10, 0x13, 0x14, 0x16, 0x19 })
            AssertOwners(exe, id, true, true);
        foreach (byte id in new byte[] { 0x0A, 0x0B, 0x15 })
        {
            AssertOwners(exe, id, false, false);
            Assert.True(Dc2OwnerBits.HasOwner(exe, id, Dc2WeaponOwner.David));
        }
        foreach (byte id in new byte[] { 0x11, 0x17 }) AssertOwners(exe, id, false, true);
        foreach (byte id in new byte[] { 0x12, 0x18 }) AssertOwners(exe, id, true, false);
        foreach (byte id in Dc2OwnerBits.VanillaFlags.Keys)
            Assert.Equal(Dc2OwnerBits.VanillaFlags[id] & ~0x03,
                Dc2OwnerBits.Read(exe, id) & ~0x03);

        Assert.Contains((byte)0x01, OwnedMains(exe, Dc2WeaponOwner.Dylan));
        Assert.Contains((byte)0x02, OwnedMains(exe, Dc2WeaponOwner.Regina));
        Dc2RandomizedWeaponPatch.ValidateOwnedMainSlots(exe);
    }

    [Fact]
    public void Door_tool_pins_match_the_generated_door_guard_registry()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "data", "dc2", "door-guards.json")));
        var rows = doc.RootElement.GetProperty("_subweapon_key_use_gates")
            .GetProperty("subtype_to_subweapon");

        Assert.Equal(new[] { "0x12", "0x18" }, rows.GetProperty("1").GetProperty("catalogIds")
            .EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Regina", rows.GetProperty("1").GetProperty("character").GetString());
        Assert.Equal(new[] { "0x11", "0x17" }, rows.GetProperty("2").GetProperty("catalogIds")
            .EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("Dylan", rows.GetProperty("2").GetProperty("character").GetString());
    }

    [Fact]
    public void Null_required_main_slot_fails_atomically()
    {
        var exe = MakeExe();
        var before = (byte[])exe.Clone();
        var plan = Dc2RandomizedWeaponPlanner.Plan(new Seed(77));
        SetSlot(exe, Dc2CrossCharTarget.Regina, 0x00, 0);
        before = (byte[])exe.Clone();

        var error = Assert.Throws<InvalidOperationException>(
            () => Dc2RandomizedWeaponPatch.Apply(exe, plan.ReginaOnly));

        Assert.Contains("NULL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, exe);
    }

    [Fact]
    public void Apply_then_restore_is_byte_identical()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();

        Dc2RandomizedWeaponPatch.Apply(exe,
            Dc2RandomizedWeaponPlanner.Plan(new Seed(-99)).ReginaOnly);
        Dc2RandomizedWeaponPatch.Restore(exe);

        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void Seed_byte_16_bit_7_roundtrips_and_off_is_historical()
    {
        var seed = new Seed(42);
        var historical = SeedString.Encode(seed, new RandomizerConfig());
        var explicitOff = SeedString.Encode(seed,
            new RandomizerConfig { Dc2RandomizeWeapons = false });
        var enabled = SeedString.Encode(seed,
            new RandomizerConfig { Dc2RandomizeWeapons = true });

        Assert.Equal(historical, explicitOff);
        Assert.Equal(17, Payload(enabled).Length);
        Assert.Equal(0x80, Payload(enabled)[16] & 0x80);
        Assert.True(SeedString.TryParse(enabled, out _, out var parsed));
        Assert.True(parsed.Dc2RandomizeWeapons);
    }

    [Fact]
    public void Shop_can_shuffle_prices_and_recovery_without_touching_catalog_masks()
    {
        var exe = Dc2ShopTablePatchTests.MakeExe();
        var masks = Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadMask(exe, id)).ToArray();
        var prices = Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadPrice(exe, id)).ToArray();
        var recovery = Enumerable.Range(0, Dc2ShopTablePatch.RecoveryIds.Length)
            .Select(i => Dc2ShopTablePatch.ReadRecoveryPrice(exe, i)).ToArray();

        Dc2ShopTablePatch.Shuffle(exe, seed: 1234, shuffleCatalogMasks: false);

        Assert.Equal(masks, Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadMask(exe, id)));
        Assert.NotEqual(prices, Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadPrice(exe, id)));
        Assert.NotEqual(recovery, Enumerable.Range(0, Dc2ShopTablePatch.RecoveryIds.Length)
            .Select(i => Dc2ShopTablePatch.ReadRecoveryPrice(exe, i)));
    }

    [Fact]
    public void Shop_default_still_shuffles_complete_masks()
    {
        var exe = Dc2ShopTablePatchTests.MakeExe();
        var before = Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadMask(exe, id)).ToArray();

        Dc2ShopTablePatch.Shuffle(exe, seed: 1234);

        Assert.NotEqual(before, Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadMask(exe, id)));
    }

    private static void AssertOwners(byte[] exe, byte id, bool regina, bool dylan)
    {
        Assert.Equal(regina, Dc2OwnerBits.HasOwner(exe, id, Dc2WeaponOwner.Regina));
        Assert.Equal(dylan, Dc2OwnerBits.HasOwner(exe, id, Dc2WeaponOwner.Dylan));
    }

    private static byte[] OwnedMains(byte[] exe, Dc2WeaponOwner owner)
        => Enumerable.Range(0, 12).Select(i => (byte)i)
            .Where(id => Dc2OwnerBits.HasOwner(exe, id, owner)).ToArray();

    private static byte[] Payload(string value)
    {
        var b64 = value["DINO-".Length..].Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '='));
    }
}
