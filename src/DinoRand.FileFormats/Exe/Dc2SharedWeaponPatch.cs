using System;
using System.Collections.Generic;

namespace DinoRand.FileFormats.Exe;

/// <summary>
/// "Enable Character Shared Weapons" (DC2, experimental, default OFF) — grants BOTH owner bits to
/// the weapons that need <b>no model graft</b>, so Regina and Dylan share them the way the seven
/// natively dual-owned ids already do.
///
/// <para><b>Scope is SUB weapons only, and that is a hard safety bound, not a simplification.</b>
/// SUB weapons resolve their file as <c>0x213 + subIndex</c> with no character term
/// (<c>DC2-WEAPON-SYSTEM-DECODE.md</c> §1c), so both characters already load one shared file and the
/// owner bit alone is sufficient. A MAIN weapon granted to a character whose <c>0x71B230</c> slot is
/// NULL makes the loader skip the file and run on a stale blob — the documented crash. Sharing MAIN
/// weapons requires the geometry graft and belongs to
/// <see cref="Dc2CrossCharWeaponPatch"/>.</para>
///
/// <para>Pure owner-bit edits: no code cave, no <c>.data</c> slack, no file writes. Composes with
/// the graft lever because both revert through <see cref="Dc2OwnerBits.Revoke"/>, which restores the
/// vanilla bit rather than clearing it.</para>
///
/// <para>Known cosmetic limitation (K125): the shared weapons keep the owner's inventory icon on
/// both characters — <c>0x11</c> and <c>0x12</c> sit on contested atlas UVs ((64,128) and (0,96))
/// and the icon UV is global per weapon id, with no per-character indirection.</para>
/// </summary>
public static class Dc2SharedWeaponPatch
{
    /// <summary>The SUB weapons that are single-owner in retail: Machete (Dylan) and Large Stungun
    /// (Regina). Both become dual-owned.</summary>
    public static IReadOnlyList<byte> SharedIds { get; } = new byte[] { 0x11, 0x12 };

    private static bool RightBuild(ReadOnlySpan<byte> exe) => exe.Length == Dc2WpGatePatch.ExpectedLength;

    /// <summary>True iff every cross-served MAIN weapon's <c>0x71B230</c> slot resolves to a file, i.e.
    /// the geometry graft is installed. MAIN sharing is only legal in that state: an owner bit on a
    /// MAIN whose slot is NULL makes <c>LoadWeaponFiles</c> skip the file and run on a stale blob.
    /// The graft lever already sets the MAIN owner bits itself, so this is a precondition check for
    /// the installer, not a licence for this class to write them.</summary>
    public static bool MainWeaponsReady(ReadOnlySpan<byte> exe)
    {
        if (!RightBuild(exe)) return false;
        var buf = exe.ToArray();
        foreach (var p in Dc2CrossCharWeaponPatch.Pairs)
            if (Dc2CrossCharWeaponPatch.ReadCatalogName(
                    buf, Dc2CrossCharWeaponPatch.CatalogSlot(p.Target, p.WeaponId)) is null)
                return false;
        return true;
    }

    /// <summary>The owner a given SUB weapon does NOT have in retail.</summary>
    private static Dc2WeaponOwner CrossOwner(byte id)
        => id == 0x11 ? Dc2WeaponOwner.Regina : Dc2WeaponOwner.Dylan;

    /// <summary>True iff every shared id already carries both owner bits.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
    {
        if (!RightBuild(exe)) return false;
        foreach (var id in SharedIds)
            if (!Dc2OwnerBits.HasOwner(exe, id, CrossOwner(id))) return false;
        return true;
    }

    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException("Dino2.exe wrong length; refusing to patch.");

        foreach (var id in SharedIds)
            Dc2OwnerBits.Grant(exe, id, CrossOwner(id));
    }

    /// <summary>Revert this lever's own bits to vanilla, leaving every other Dino2.exe patch intact.</summary>
    public static void Restore(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException("Dino2.exe wrong length; refusing to restore.");

        foreach (var id in SharedIds)
            Dc2OwnerBits.Revoke(exe, id, CrossOwner(id));
    }
}
