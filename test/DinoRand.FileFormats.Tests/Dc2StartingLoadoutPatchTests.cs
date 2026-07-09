using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Dc2StartingLoadoutPatch (docs/dc2/DC2-STARTING-LOADOUT-PLAN.md I2) on a synthetic exe image:
/// the real build's length with the four bootstrap byte sequences laid in at their file offsets —
/// no game files in the repo.
/// </summary>
public class Dc2StartingLoadoutPatchTests
{
    private static byte[] MakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        new byte[] { 0xBA, 0x01, 0x01, 0x00, 0x00 }.CopyTo(exe, 0x50D68);                          // Dylan
        new byte[] { 0x66, 0xC7, 0x80, 0xB6, 0x10, 0x00, 0x00, 0x02, 0x02 }.CopyTo(exe, 0x50E96);  // Regina
        new byte[] { 0x66, 0xC7, 0x82, 0xBA, 0x10, 0x00, 0x00, 0x0A, 0x05 }.CopyTo(exe, 0x50EB7);  // David
        new byte[] { 0xC6, 0x80, 0x8F, 0x10, 0x00, 0x00, 0x01 }.CopyTo(exe, 0x50E7D);              // char := 1
        // 0x496750 inventory-template sites (byte-verified against the rebirth exe)
        new byte[] { 0x88, 0x9C, 0x24, 0xC0, 0x00, 0x00, 0x00, 0x88, 0x9C, 0x24, 0xC1, 0x00, 0x00, 0x00 }.CopyTo(exe, 0x96786);
        new byte[] { 0x88, 0x5C, 0x24, 0x54, 0x88, 0x5C, 0x24, 0x55 }.CopyTo(exe, 0x968B6);
        new byte[] { 0x88, 0x5C, 0x24, 0x0C, 0x88, 0x5C, 0x24, 0x0D }.CopyTo(exe, 0x969E9);
        new byte[] { 0xC6, 0x84, 0x24, 0xCD, 0x00, 0x00, 0x00, 0x02 }.CopyTo(exe, 0x967BA);
        new byte[] { 0xC6, 0x44, 0x24, 0x61, 0x02 }.CopyTo(exe, 0x968D5);
        new byte[] { 0xC6, 0x44, 0x24, 0x19, 0x02 }.CopyTo(exe, 0x969FE);
        // Dylan equip-word stores (rewrite windows; the edx imm above is the dual-use START ROOM)
        new byte[] { 0x8B, 0x0D, 0xB8, 0x6D, 0x87, 0x00, 0x66, 0x89, 0x91, 0xB4, 0x10, 0x00, 0x00 }.CopyTo(exe, 0x50E84);
        new byte[] { 0x8B, 0x0D, 0xB8, 0x6D, 0x87, 0x00, 0xB8, 0xB0, 0x04, 0x00, 0x00, 0x66, 0x89, 0x91, 0xB8, 0x10, 0x00, 0x00 }.CopyTo(exe, 0x50E9F);
        // Count/countMax store-pairs (R0 Dylan / R1 Regina) per mode template — pristine register forms.
        foreach (var (off, hex) in CountSiteOriginals)
            Convert.FromHexString(hex).CopyTo(exe, off);
        // Pristine item-catalog ownership flags (0x704260 → file 0xE9260, +0xA) for the live owned-MAIN
        // guard. Apply reads these to decide replace-mode menu-safety.
        foreach (var (id, flags) in PristineCatalogFlags)
        {
            int o = 0xE9260 + id * 12 + 0xA;
            exe[o] = (byte)flags; exe[o + 1] = (byte)(flags >> 8);
        }
        return exe;
    }

    /// <summary>Overwrite one id's live item-catalog flags (0x704260 → file 0xE9260, +0xA) — models an
    /// external weapon-shuffle tool having rewritten the owner/kind bits on the install being patched.</summary>
    private static void SetCatalogFlags(byte[] exe, byte id, ushort flags)
    {
        int o = 0xE9260 + id * 12 + 0xA;
        exe[o] = (byte)flags; exe[o + 1] = (byte)(flags >> 8);
    }

    // Pristine 0x704260 flags (MAIN 0x08, owner Dylan 0x02 / Regina 0x01); ids 01–0b MAINs, 10–15 SUBs.
    private static readonly (byte Id, ushort Flags)[] PristineCatalogFlags =
    {
        (0x01, 0x004a), (0x02, 0x0049), (0x03, 0x002a), (0x04, 0x004a), (0x05, 0x002b), (0x06, 0x0029),
        (0x07, 0x0049), (0x08, 0x0029), (0x09, 0x004a), (0x0a, 0x00cc), (0x0b, 0x004c),
        (0x10, 0x0053), (0x11, 0x00d2), (0x12, 0x00d1), (0x13, 0x0053), (0x14, 0x0053), (0x15, 0x00d4),
    };

    // (offset, pristine store-pair bytes) for the 6 count/countMax sites the ammo override rewrites.
    private static readonly (int Off, string Hex)[] CountSiteOriginals =
    {
        (0x967A3, "6689bc24c80000006689bc24ca000000"), // mode0 R0 Dylan
        (0x967D1, "6689bc24d40000006689bc24d6000000"), // mode0 R1 Regina
        (0x968C7, "668974245c668974245e"),             // mode1 R0 Dylan
        (0x968E3, "66897c246866897c246a"),             // mode1 R1 Regina
        (0x96950, "66897424146689742416"),             // else  R0 Dylan
        (0x9695A, "66897424206689742422"),             // else  R1 Regina
    };

    /// <summary>Every byte this lever may legitimately write.</summary>
    private static readonly HashSet<int> OwnedOffsets = BuildOwnedOffsets();

    private static HashSet<int> BuildOwnedOffsets()
    {
        // Append-mode lever: the ONLY bytes Apply may change are the equip-word stores (equip the
        // chosen weapon) + the Regina equip byte + the dual-use room imm. The inventory-template
        // records and count stores stay pristine — the chosen weapon is APPENDED as an extra record
        // (Dc2StartWeaponAppendPatch), so the default shotgun/handgun survives.
        var s = new HashSet<int>
        {
            Dc2StartingLoadoutPatch.LegacyDylanWeaponIdOffset,
            Dc2StartingLoadoutPatch.ReginaWeaponIdOffset,
        };
        foreach (var (off, len) in new[] { (0x50E84, 13), (0x50E9F, 18) }) // Dylan equip sites only
            for (int i = 0; i < len; i++) s.Add(off + i);
        return s;
    }

    [Fact]
    public void SyntheticExe_IsCanonical()
    {
        var exe = MakeExe();
        Assert.True(Dc2StartingLoadoutPatch.IsCanonical(exe));
        Assert.Equal((Dc2StartingLoadoutPatch.DylanCanonicalId, Dc2StartingLoadoutPatch.ReginaCanonicalId),
                     Dc2StartingLoadoutPatch.Read(exe));
    }

    [Fact]
    public void Apply_KeepsDefaultRecords_EquipsNewWeapon_WritesOnlyEquipSites()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        Dc2StartingLoadoutPatch.Apply(exe, dylanId: 0x03, reginaId: 0x08);

        Assert.Equal(((byte)0x03, (byte)0x08), Dc2StartingLoadoutPatch.Read(exe));
        // Only the equip sites (+ Regina equip byte + room imm) may change; everything else stays
        // pristine — including the inventory-template records + count stores, so the default
        // shotgun/handgun record survives (fixes the two-handed sub-weapon soft-lock).
        for (int i = 0; i < exe.Length; i++)
            if (!OwnedOffsets.Contains(i))
                Assert.Equal(pristine[i], exe[i]);
        // The dual-use edx imm (START ROOM) stays canonical 0x0101 — the spawn-room RCA fix
        Assert.Equal(0x01, exe[Dc2StartingLoadoutPatch.LegacyDylanWeaponIdOffset]);
        Assert.Equal(0x01, exe[Dc2StartingLoadoutPatch.LegacyDylanWeaponIdOffset + 1]);
        // subweapon (hi) bytes explicitly untouched: Machete SUB1 / Large Stun Gun SUB2
        Assert.Equal(0x01, exe[0x50E84 + 8]);  // equip site 1 imm16 hi = SUB1
        Assert.Equal(0x02, exe[Dc2StartingLoadoutPatch.ReginaWeaponIdOffset + 1]);
        // equip-site rewrite carries the id; EP constant preserved in site 2
        Assert.Equal(0x03, exe[0x50E84 + 7]);
        Assert.Equal(0x03, exe[0x50E9F + 7]);
        Assert.Equal(0xB8, exe[0x50E9F + 9]); // mov eax,0x4B0 kept
        // inventory-template records stay PRISTINE (default weapons preserved, not overwritten)
        foreach (var (off, len) in new[] { (0x96786, 14), (0x968B6, 8), (0x969E9, 8) })
            Assert.Equal(pristine.Skip(off).Take(len), exe.Skip(off).Take(len));
        Assert.Equal(0x02, exe[0x967C1]); // Regina handgun template id kept canonical
    }

    [Fact]
    public void Apply_CanonicalDylan_RestoresStockTemplateMovs()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        Dc2StartingLoadoutPatch.Apply(exe, 0x03, 0x08);
        Dc2StartingLoadoutPatch.Apply(exe, Dc2StartingLoadoutPatch.DylanCanonicalId,
                                           Dc2StartingLoadoutPatch.ReginaCanonicalId);
        Assert.Equal(pristine, exe); // canonical apply == byte-identical pristine
    }

    [Fact]
    public void Apply_Then_Restore_RoundTripsToPristine()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        Dc2StartingLoadoutPatch.Apply(exe, 0x03, 0x05);
        Dc2StartingLoadoutPatch.RestoreCanonical(exe);
        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void Apply_OverAlreadyPatched_IsAbsolute_NotCompounding()
    {
        var exe = MakeExe();
        Dc2StartingLoadoutPatch.Apply(exe, 0x03, 0x05);
        Dc2StartingLoadoutPatch.Apply(exe, 0x01, 0x08); // re-run on patched exe: still valid, absolute
        Assert.Equal(((byte)0x01, (byte)0x08), Dc2StartingLoadoutPatch.Read(exe));
    }

    [Theory]
    [InlineData(0x00, 0x02)] // unarmed excluded
    [InlineData(0x02, 0x02)] // Regina id on Dylan (cross-character NO-GO)
    [InlineData(0x01, 0x01)] // Dylan id on Regina
    [InlineData(0x0A, 0x02)] // David id on Dylan
    public void Apply_RejectsOutOfBandIds_AndWritesNothing(byte dylanId, byte reginaId)
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2StartingLoadoutPatch.Apply(exe, dylanId, reginaId));
        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void SelectableIds_ArePristineOwnedMains_MinusFireEmpty()
    {
        // Pristine re-audit (DC2-WEAPON-SYSTEM-INVESTIGATION.md §3): every band id is an owned MAIN, so
        // replace-mode selectable = the full band minus the fire-empty residual 0x07 (Regina). id 05
        // (dual-owned) stays selectable. Grounded by weapon_catalog_probe.py.
        Assert.Equal(new byte[] { 0x01, 0x03, 0x04, 0x05, 0x09 }, Dc2StartingLoadoutPatch.SelectableDylanIds);
        Assert.Equal(new byte[] { 0x02, 0x05, 0x06, 0x08 }, Dc2StartingLoadoutPatch.SelectableReginaIds);
        Assert.True(Dc2StartingLoadoutPatch.IsSelectableStartId(Dc2StartingLoadoutPatch.DylanWeaponIds, 0x05));
        Assert.True(Dc2StartingLoadoutPatch.IsSelectableStartId(Dc2StartingLoadoutPatch.ReginaWeaponIds, 0x06));
        Assert.False(Dc2StartingLoadoutPatch.IsSelectableStartId(Dc2StartingLoadoutPatch.ReginaWeaponIds, 0x07));
    }

    [Theory]
    [InlineData(0x01, 0x07)] // Regina Grenade Gun — the sole replace-mode refusal (fire-empty → post-fire div-0)
    public void Apply_RefusesFireEmptyMain_AndWritesNothing(byte dylanId, byte reginaId)
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc2StartingLoadoutPatch.Apply(exe, dylanId, reginaId));
        Assert.Equal("reginaId", ex.ParamName);
        Assert.Contains("weapon menu", ex.Message); // the reason is surfaced
        Assert.Equal(pristine, exe);
    }

    [Theory]
    [InlineData(0x01, 0x07)] // fire-empty Regina main — only this needs the override in replace mode
    public void Apply_AllowUnsafe_PermitsFireEmpty_ForInvestigation(byte dylanId, byte reginaId)
    {
        var exe = MakeExe();
        Dc2StartingLoadoutPatch.Apply(exe, dylanId, reginaId, allowUnsafe: true);
        Assert.Equal((dylanId, reginaId), Dc2StartingLoadoutPatch.Read(exe));
    }

    [Theory]
    [InlineData(0x01, 0x02)] // canonical
    [InlineData(0x03, 0x05)]
    [InlineData(0x03, 0x08)]
    [InlineData(0x04, 0x06)] // pristine-audit additions: now installable without an override
    [InlineData(0x09, 0x02)]
    [InlineData(0x05, 0x06)]
    public void Apply_AllowsPristineOwnedMainBandMembers(byte dylanId, byte reginaId)
    {
        var exe = MakeExe();
        Dc2StartingLoadoutPatch.Apply(exe, dylanId, reginaId); // no override needed
        Assert.Equal((dylanId, reginaId), Dc2StartingLoadoutPatch.Read(exe));
    }

    [Fact]
    public void SelectableBands_ArePristineOwnedMains_KeepWireBandIntact()
    {
        // Pristine re-audit: 04/05/09 (Dylan) and 06 (Regina) are owned mains → now selectable;
        // only the fire-empty 0x07 (Regina) is excluded.
        foreach (var id in new byte[] { 0x04, 0x05, 0x09 })
            Assert.Contains(id, Dc2StartingLoadoutPatch.SelectableDylanIds);
        Assert.Contains((byte)0x06, Dc2StartingLoadoutPatch.SelectableReginaIds);
        Assert.DoesNotContain((byte)0x07, Dc2StartingLoadoutPatch.SelectableReginaIds);
        // wire bands unchanged (seed byte-22 index stability)
        foreach (var id in new byte[] { 0x04, 0x05, 0x09 })
            Assert.Contains(id, Dc2StartingLoadoutPatch.DylanWeaponIds);
        foreach (var id in new byte[] { 0x06, 0x07 })
            Assert.Contains(id, Dc2StartingLoadoutPatch.ReginaWeaponIds);
    }

    [Theory]
    [InlineData(0x04, 0x07)] // Dylan SUB + Regina fire-empty main
    [InlineData(0x05, 0x06)] // Dylan Regina-owned main + Regina SUB
    [InlineData(0x09, 0x02)] // Dylan SUB
    public void Apply_AddAndEquipMode_AllowsFullBand_WithoutUnsafe(byte dylanId, byte reginaId)
    {
        // With the ring-builder zero-guard installed (Dc2WeaponRingGuardPatch), the div-0 that the
        // owned-MAIN guard existed to prevent is structurally impossible for any id, and every id in
        // each character's own band is WEP_P-loadable. So add-and-equip mode admits the full band
        // without --allow-unsafe. These picks all throw in replace mode (owned-MAIN guard).
        var exe = MakeExe();
        Dc2StartingLoadoutPatch.Apply(exe, dylanId, reginaId, addAndEquip: true);
        Assert.Equal((dylanId, reginaId), Dc2StartingLoadoutPatch.Read(exe));
    }

    [Fact]
    public void Apply_AddAndEquipMode_StillRejectsOutOfBand_AndId0()
    {
        var exe = MakeExe();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc2StartingLoadoutPatch.Apply(exe, 0x00, 0x02, addAndEquip: true)); // id 0 excluded (not in band)
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc2StartingLoadoutPatch.Apply(exe, 0x02, 0x02, addAndEquip: true)); // Regina id on Dylan
    }

    [Fact]
    public void CrossCharacterSharedMainIds_IsId05_InBothBands()
    {
        // id 0x05 is the only MAIN the catalog marks owned by both Regina+Dylan (flags 0x2B). Placed
        // in one character's template slot it also shows in the other's ring (shared inventory array),
        // so random draws must exclude it. It IS in both frozen wire bands (dual-owned).
        Assert.Equal(new byte[] { 0x05 }, Dc2StartingLoadoutPatch.CrossCharacterSharedMainIds);
        Assert.Contains((byte)0x05, Dc2StartingLoadoutPatch.DylanWeaponIds);
        Assert.Contains((byte)0x05, Dc2StartingLoadoutPatch.ReginaWeaponIds);
        // Filtering either band for random leaves a non-empty, 05-free pool.
        foreach (var band in new[] { Dc2StartingLoadoutPatch.DylanWeaponIds, Dc2StartingLoadoutPatch.ReginaWeaponIds })
        {
            var pool = band.Where(id => !Dc2StartingLoadoutPatch.CrossCharacterSharedMainIds.Contains(id)).ToArray();
            Assert.NotEmpty(pool);
            Assert.DoesNotContain((byte)0x05, pool);
        }
    }

    [Fact]
    public void Apply_SwappedMain_LeavesTemplateCountStoresPristine()
    {
        // Under append semantics the chosen weapon's ammo lives in its APPENDED record
        // (Dc2StartWeaponAppendPatch), so the mode-template count/countMax stores of the default
        // shotgun/handgun stay pristine (their real starting ammo is preserved). Verified in
        // Dc2StartWeaponAppendPatchTests: the appended record carries the weapon's own count.
        var exe = MakeExe();
        Dc2StartingLoadoutPatch.Apply(exe, 0x03, 0x08);
        foreach (var (off, hex) in CountSiteOriginals)
            Assert.Equal(Convert.FromHexString(hex), exe.Skip(off).Take(hex.Length / 2).ToArray());
    }

    [Fact]
    public void Apply_CanonicalMain_LeavesStockAmmoStores()
    {
        // Canonical shotgun/handgun keep the template's deliberate 100/200 start amount (no override).
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        Dc2StartingLoadoutPatch.Apply(exe, Dc2StartingLoadoutPatch.DylanCanonicalId, Dc2StartingLoadoutPatch.ReginaCanonicalId);
        foreach (var (off, hex) in CountSiteOriginals)
            Assert.Equal(Convert.FromHexString(hex), exe.Skip(off).Take(hex.Length / 2).ToArray());
        Assert.Equal(pristine, exe); // canonical apply == byte-identical pristine
    }

    [Fact]
    public void Apply_AmmoOverride_RoundTripsToPristine()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        Dc2StartingLoadoutPatch.Apply(exe, 0x03, 0x05); // both swapped (RL=20 / Regina 05=300) → count sites patched
        Dc2StartingLoadoutPatch.RestoreCanonical(exe);
        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void Apply_ReplaceMode_ChecksLiveCatalog_NotAStaticSet()
    {
        // External weapon-shuffle tools can rewrite the 0x704260 owner/class flags. If id 0x03 is made
        // a SUB in THIS exe, replace-mode Apply must refuse it (it would empty Dylan's ring → div-0),
        // even though 0x03 is in the pristine "selectable" set. add-and-equip bypasses (ring guard).
        var exe = MakeExe();
        int o = 0xE9260 + 0x03 * 12 + 0xA;
        exe[o] = 0x53; exe[o + 1] = 0x00; // id 03 → SUB (flags 0x0053)

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc2StartingLoadoutPatch.Apply(exe, 0x03, Dc2StartingLoadoutPatch.ReginaCanonicalId));
        Assert.Equal("dylanId", ex.ParamName);
        Assert.Contains("weapon menu", ex.Message);

        // add-and-equip no longer bypasses blindly (DC2-STARTING-LOADOUT-CROSS-CHAR-RCA.md): a live SUB /
        // cross-exposed id has a NULL grid slot for the character the corrupt catalog exposes it to →
        // it would crash on equip, so add-and-equip refuses it too (the ring guard only covers the
        // div-0). --allow-unsafe still forces it (investigation).
        var ex2 = Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc2StartingLoadoutPatch.Apply(exe, 0x03, Dc2StartingLoadoutPatch.ReginaCanonicalId, addAndEquip: true));
        Assert.Equal("dylanId", ex2.ParamName);
        Assert.Contains("grid slot", ex2.Message);
        Dc2StartingLoadoutPatch.Apply(exe, 0x03, Dc2StartingLoadoutPatch.ReginaCanonicalId, addAndEquip: true, allowUnsafe: true);
        Assert.Equal(((byte)0x03, Dc2StartingLoadoutPatch.ReginaCanonicalId), Dc2StartingLoadoutPatch.Read(exe));
    }

    [Fact]
    public void Apply_AddAndEquip_RejectsCrossExposedMain_OnCorruptedCatalog()
    {
        // Repro of the DINO-wyjHEyIA… crash (DC2-STARTING-LOADOUT-CROSS-CHAR-RCA.md): an external
        // weapon-shuffle tool flipped id 0x06 (Regina's Submachine Gun) from MAIN-Regina (0x29) to a
        // dual-owned SUB (0x53) on the install. Add-and-equip appends BOTH characters' picks into the
        // SHARED inventory (Dc2StartWeaponAppendPatch), so Regina's 0x06 surfaces in Dylan's corrupt-
        // catalog subweapon menu — but 0x06 has a NULL Dylan WEP_P grid slot (0x06 ∉ DylanWeaponIds),
        // so equipping it is the §5 stale-blob crash (garbage vtable dispatch at 0x4bf073). The ring
        // guard only neutralizes the div-0, NOT this cross-character crash → add-and-equip must refuse.
        var exe = MakeExe();
        SetCatalogFlags(exe, 0x06, 0x0053); // the user's live-install state

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc2StartingLoadoutPatch.Apply(exe, Dc2StartingLoadoutPatch.DylanCanonicalId, 0x06, addAndEquip: true));
        Assert.Equal("reginaId", ex.ParamName);
        Assert.Contains("grid slot", ex.Message); // the reason is surfaced
    }

    [Fact]
    public void Apply_AddAndEquip_AllowsSingleOwnerMain_OnPristineCatalog()
    {
        // Boundary (don't over-reject): on a pristine catalog 0x06 is a Regina-only MAIN (0x29). The
        // ring's ownership filter hides it from Dylan, so it is cross-safe and add-and-equip installs
        // it exactly as before — the fix keys on the LIVE catalog, byte-identical on a clean exe.
        var exe = MakeExe();
        Dc2StartingLoadoutPatch.Apply(exe, Dc2StartingLoadoutPatch.DylanCanonicalId, 0x06, addAndEquip: true);
        Assert.Equal((Dc2StartingLoadoutPatch.DylanCanonicalId, (byte)0x06), Dc2StartingLoadoutPatch.Read(exe));
    }

    [Fact]
    public void IsCrossSafeAddAndEquipMain_KeysOnGridOwnership_NotNaiveSingleOwner()
    {
        // The predicate: MAIN-classed AND every catalog-owner also owns the WEP_P grid slot (band
        // membership). id 0x05 is catalog dual-owned (MRD) but BOTH characters own its grid slot
        // (Dylan WEP_P105 / Regina WEP_P005) → no NULL slot → still safe. id 0x06 corrupted to a
        // dual-owned SUB grants Dylan menu access without a grid slot → unsafe.
        var exe = MakeExe();
        Assert.True(Dc2StartingLoadoutPatch.IsCrossSafeAddAndEquipMain(exe, 0x05));  // dual grid-owned
        Assert.True(Dc2StartingLoadoutPatch.IsCrossSafeAddAndEquipMain(exe, 0x06));  // pristine Regina MAIN
        SetCatalogFlags(exe, 0x06, 0x0053);
        Assert.False(Dc2StartingLoadoutPatch.IsCrossSafeAddAndEquipMain(exe, 0x06)); // corrupted → cross-exposed
    }

    [Fact]
    public void Validate_RefusesWrongLength()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Dc2StartingLoadoutPatch.Validate(new byte[100]));
        Assert.Contains("unexpected length", ex.Message);
    }

    [Fact]
    public void Validate_RefusesForeignBootstrapBytes()
    {
        var exe = MakeExe();
        exe[0x50EB8] ^= 0xFF; // corrupt a fixed fingerprint byte (David preset)
        Assert.Throws<InvalidOperationException>(() => Dc2StartingLoadoutPatch.Validate(exe));
    }

    [Fact]
    public void Validate_AcceptsOldLeverState_EquipPatchedTemplatesPristine()
    {
        // An exe patched by the equip-only v1 lever (I3 partial-fail state): equip byte in-band,
        // template sites stock. Must validate so Apply/Restore can repair it.
        var exe = MakeExe();
        exe[Dc2StartingLoadoutPatch.LegacyDylanWeaponIdOffset] = 0x03;
        Dc2StartingLoadoutPatch.Validate(exe);
        Dc2StartingLoadoutPatch.RestoreCanonical(exe);
        Assert.Equal(MakeExe(), exe);
    }

    [Fact]
    public void Apply_RepairsLegacyV2State_SpawnRoomImmBackToCanonical()
    {
        // v2 lever state (the spawn-room bug): dual-use edx imm patched to 0x04 + template
        // sites rewritten. Re-applying the same pick must move the id to the equip sites and
        // restore the START ROOM imm to canonical 0x0101.
        var exe = MakeExe();
        exe[Dc2StartingLoadoutPatch.LegacyDylanWeaponIdOffset] = 0x04;
        new byte[] { 0x66, 0xC7, 0x84, 0x24, 0xC0, 0x00, 0x00, 0x00, 0x01, 0x04, 0x90, 0x90, 0x90, 0x90 }.CopyTo(exe, 0x96786);
        new byte[] { 0x66, 0xC7, 0x44, 0x24, 0x54, 0x01, 0x04, 0x90 }.CopyTo(exe, 0x968B6);
        new byte[] { 0x66, 0xC7, 0x44, 0x24, 0x0C, 0x01, 0x04, 0x90 }.CopyTo(exe, 0x969E9);
        Dc2StartingLoadoutPatch.Validate(exe);
        Assert.Equal(((byte)0x04, Dc2StartingLoadoutPatch.ReginaCanonicalId), Dc2StartingLoadoutPatch.Read(exe));

        // 0x04 is a Dylan owned main (pristine audit) → re-applies without an override; repairs the room imm.
        Dc2StartingLoadoutPatch.Apply(exe, 0x04, Dc2StartingLoadoutPatch.ReginaCanonicalId);
        Assert.Equal(0x01, exe[Dc2StartingLoadoutPatch.LegacyDylanWeaponIdOffset]); // room repaired
        Assert.Equal(((byte)0x04, Dc2StartingLoadoutPatch.ReginaCanonicalId), Dc2StartingLoadoutPatch.Read(exe));
        Assert.Equal(0x04, exe[0x50E84 + 7]); // id now lives in the equip-site rewrite
    }

    [Fact]
    public void Validate_RefusesForeignTemplateBytes()
    {
        var exe = MakeExe();
        exe[0x968B6] = 0xC7; // neither the stock mov pair nor this lever's imm16 shape
        Assert.Throws<InvalidOperationException>(() => Dc2StartingLoadoutPatch.Validate(exe));
    }

    [Fact]
    public void Validate_RefusesOutOfBandWeaponByte()
    {
        var exe = MakeExe();
        exe[Dc2StartingLoadoutPatch.LegacyDylanWeaponIdOffset] = 0x13; // not this lever's write
        Assert.Throws<InvalidOperationException>(() => Dc2StartingLoadoutPatch.Validate(exe));
    }
}
