using System;
using System.Linq;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// DC2 weapon OWNERSHIP bits — item catalog <c>0x704260[id]+0xA</c>, bits
/// <c>0x01</c>/<c>0x02</c>/<c>0x04</c> = Regina/Dylan/David.
///
/// <para>K125 (2026-07-20) established that per-character weapon <b>usability</b> is exactly this
/// one static bit — the same bits drive the shop stock builder (<c>0x408F99</c>,
/// <c>mask &amp; (1 &lt;&lt; byte[scene+0x108F])</c>), the weapon-menu ring builder
/// <c>0x496D70</c>, and the char-switch restock <c>0x452390</c>. Possession is separate and
/// GLOBAL (<c>flag(9,id)</c>, no character term).</para>
///
/// <para><b>The defect these tests pin.</b> <see cref="Dc2CrossCharWeaponPatch"/> reverted an owner
/// bit with a blanket <c>flags &amp; ~targetBit</c>. That is only correct because no
/// <i>natively dual-owned</i> id is in its pair list today. K125 grew the known dual set from 3 to
/// <b>7</b> ids (<c>0x00 0x05 0x10 0x13 0x14 0x16 0x19</c>) — and <c>0x16</c> Chain Mine is a shop
/// subweapon, a plausible future addition. Add any of them and Apply→Restore silently stops being
/// byte-identical AND strips a bit the retail build shipped. Revert must be
/// <b>vanilla-relative</b>, not a computed clear.</para>
/// </summary>
public class Dc2OwnerBitsTests
{
    /// <summary>Synthetic pristine image: real build length, every weapon/sub id carrying its
    /// PSX-verified vanilla flags, and the cross-char catalog slots shaped so the graft lever's
    /// own pristine check passes.</summary>
    private static byte[] MakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        foreach (var (id, flags) in Dc2OwnerBits.VanillaFlags)
            BitConverter.GetBytes(flags).CopyTo(exe, Dc2OwnerBits.FlagsOffset(id));
        foreach (var slot in new[] { 0x1E9, 0x1F9 })
            BitConverter.GetBytes(0x71BEC8u).CopyTo(exe, Dc2CrossCharWeaponPatch.CatalogBaseOffset + slot * 4);
        return exe;
    }

    // ---- B3: the dual set is DERIVED from the catalog, never hardcoded -------------------

    [Fact]
    public void NativelyDual_derives_the_seven_K125_ids_from_the_catalog()
    {
        var exe = MakeExe();

        Assert.Equal(
            new byte[] { 0x00, 0x05, 0x10, 0x13, 0x14, 0x16, 0x19 },
            Dc2OwnerBits.NativelyDual(exe).ToArray());
    }

    [Fact]
    public void NativelyDual_ignores_rows_with_no_class_flags()
    {
        // Item/key-item rows (ids 0x1A+) carry flags 0x0000 — two clear owner bits must not read
        // as "dual". Guards against a naive (flags & 3) == 3 scan over the whole 0x3A-id catalog.
        var exe = MakeExe();
        Assert.DoesNotContain((byte)0x1A, Dc2OwnerBits.NativelyDual(exe));
        Assert.DoesNotContain((byte)0x33, Dc2OwnerBits.NativelyDual(exe));
    }

    // ---- B2: the boundary case — revert must not clear a bit vanilla already set ---------

    [Fact]
    public void Revoke_keeps_an_owner_bit_that_the_retail_build_already_set()
    {
        var exe = MakeExe();
        // 0x16 Chain Mine is natively dual (0x0053): BOTH owner bits are set in retail.
        Assert.True(Dc2OwnerBits.HasOwner(exe, 0x16, Dc2WeaponOwner.Regina));
        Assert.True(Dc2OwnerBits.HasOwner(exe, 0x16, Dc2WeaponOwner.Dylan));

        Dc2OwnerBits.Grant(exe, 0x16, Dc2WeaponOwner.Regina);   // no-op, already owned
        Dc2OwnerBits.Revoke(exe, 0x16, Dc2WeaponOwner.Regina);  // must NOT strip the vanilla bit

        Assert.True(Dc2OwnerBits.HasOwner(exe, 0x16, Dc2WeaponOwner.Regina));
        Assert.Equal(Dc2OwnerBits.VanillaFlags[0x16], Dc2OwnerBits.Read(exe, 0x16));
    }

    [Fact]
    public void Grant_then_Revoke_restores_the_exact_vanilla_flags_word()
    {
        var exe = MakeExe();
        foreach (var id in Dc2OwnerBits.VanillaFlags.Keys)
        {
            Dc2OwnerBits.Grant(exe, id, Dc2WeaponOwner.Regina);
            Dc2OwnerBits.Grant(exe, id, Dc2WeaponOwner.Dylan);
            Dc2OwnerBits.Revoke(exe, id, Dc2WeaponOwner.Regina);
            Dc2OwnerBits.Revoke(exe, id, Dc2WeaponOwner.Dylan);

            Assert.Equal(Dc2OwnerBits.VanillaFlags[id], Dc2OwnerBits.Read(exe, id));
        }
    }

    // ---- B5: granting touches ONLY that id's flags word ---------------------------------

    [Fact]
    public void Grant_touches_only_the_flags_word_of_that_id()
    {
        var exe = MakeExe();
        var before = (byte[])exe.Clone();

        Dc2OwnerBits.Grant(exe, 0x11, Dc2WeaponOwner.Regina);

        int flagsAt = Dc2OwnerBits.FlagsOffset(0x11);
        for (int i = 0; i < exe.Length; i++)
        {
            if (i == flagsAt || i == flagsAt + 1) continue;
            if (exe[i] != before[i]) Assert.Fail($"Grant wrote outside the flags word at 0x{i:X}");
        }
        Assert.NotEqual(before[flagsAt], exe[flagsAt]);
    }

    [Fact]
    public void Revoke_refuses_an_id_with_no_recorded_vanilla_flags()
    {
        // Fail closed: reverting an id we have no PSX-verified baseline for would be a guess.
        var exe = MakeExe();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Dc2OwnerBits.Revoke(exe, 0x2A, Dc2WeaponOwner.Regina));
    }

    // ---- B4: the guard that keeps the B2 defect unreachable in the shipped lever ---------

    [Fact]
    public void No_cross_char_target_is_a_natively_dual_id()
    {
        var exe = MakeExe();
        var dual = Dc2OwnerBits.NativelyDual(exe).ToHashSet();

        foreach (var p in Dc2CrossCharWeaponPatch.Pairs)
            Assert.DoesNotContain(p.WeaponId, dual);
        foreach (var id in Dc2CrossCharWeaponPatch.SubWeaponIds)
            Assert.DoesNotContain(id, dual);
    }

    // ---- B1: regression guard on the shipped lever ---------------------------------------

    [Fact]
    public void CrossChar_apply_then_restore_is_byte_identical()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();

        Dc2CrossCharWeaponPatch.Apply(exe);
        Assert.NotEqual(pristine, exe);

        Dc2CrossCharWeaponPatch.Restore(exe);
        Assert.Equal(pristine, exe);
    }

    // ---- the shared-weapons lever (owner-bit only, no model graft) -----------------------

    [Fact]
    public void SharedWeapons_grants_both_owner_bits_to_the_sub_weapons()
    {
        var exe = MakeExe();
        Dc2SharedWeaponPatch.Apply(exe);

        // Machete 0x11 is Dylan's, Large Stungun 0x12 is Regina's — after the lever both are dual.
        Assert.True(Dc2OwnerBits.HasOwner(exe, 0x11, Dc2WeaponOwner.Regina));
        Assert.True(Dc2OwnerBits.HasOwner(exe, 0x11, Dc2WeaponOwner.Dylan));
        Assert.True(Dc2OwnerBits.HasOwner(exe, 0x12, Dc2WeaponOwner.Regina));
        Assert.True(Dc2OwnerBits.HasOwner(exe, 0x12, Dc2WeaponOwner.Dylan));
        Assert.True(Dc2SharedWeaponPatch.IsApplied(exe));
    }

    [Fact]
    public void SharedWeapons_touches_no_MAIN_weapon()
    {
        // A MAIN weapon granted to a character whose 0x71B230 slot is NULL makes the loader skip
        // the file and run on a stale blob — the documented crash. This lever must never do that.
        var exe = MakeExe();
        var before = (byte[])exe.Clone();
        Dc2SharedWeaponPatch.Apply(exe);

        foreach (var (id, _) in Dc2OwnerBits.VanillaFlags.Where(kv => (kv.Value & 0x08) != 0))
            Assert.Equal(before[Dc2OwnerBits.FlagsOffset(id)], exe[Dc2OwnerBits.FlagsOffset(id)]);
    }

    // ---- MAIN weapons: allowed, but only on top of the proven geometry graft ---------------

    [Fact]
    public void MainWeaponsReady_is_false_until_the_graft_lever_has_populated_the_slots()
    {
        // THE safety gate. A MAIN weapon usable by a character whose 0x71B230 slot is NULL makes
        // LoadWeaponFiles skip the file and run on a stale blob — the documented crash. MAIN sharing
        // is therefore only legal once the geometry graft has repointed every cross slot, which is
        // the configuration that was actually proven in-game.
        var exe = MakeExe();
        Assert.False(Dc2SharedWeaponPatch.MainWeaponsReady(exe));

        Dc2CrossCharWeaponPatch.Apply(exe);
        Assert.True(Dc2SharedWeaponPatch.MainWeaponsReady(exe));
    }

    [Fact]
    public void The_graft_lever_alone_already_makes_every_cross_served_main_dual_owned()
    {
        // Why MAIN sharing delegates instead of setting its own bits: the graft lever's owner-bit
        // edit ALREADY leaves each cross-served main usable by both characters. A second lever
        // writing the same bits would fight it on restore.
        var exe = MakeExe();
        Dc2CrossCharWeaponPatch.Apply(exe);

        var dual = Dc2OwnerBits.NativelyDual(exe).ToHashSet();
        foreach (var p in Dc2CrossCharWeaponPatch.Pairs)
            Assert.Contains(p.WeaponId, dual);
        foreach (var id in Dc2CrossCharWeaponPatch.SubWeaponIds)
            Assert.Contains(id, dual);
    }

    [Fact]
    public void SharedWeapons_apply_then_restore_is_byte_identical()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();

        Dc2SharedWeaponPatch.Apply(exe);
        Assert.NotEqual(pristine, exe);

        Dc2SharedWeaponPatch.Restore(exe);
        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void SharedWeapons_composes_with_the_cross_char_lever_in_any_restore_order()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();

        Dc2CrossCharWeaponPatch.Apply(exe);
        Dc2SharedWeaponPatch.Apply(exe);
        // Both levers claim the same two SUB owner bits; restoring either must leave the other's
        // state vanilla-correct rather than double-clearing.
        Dc2CrossCharWeaponPatch.Restore(exe);
        Dc2SharedWeaponPatch.Restore(exe);

        Assert.Equal(pristine, exe);
    }
}
