using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DinoRand.FileFormats.Exe;

/// <summary>Owner bits in the DC2 item catalog flags word (<c>0x704260[id]+0xA</c>).</summary>
public enum Dc2WeaponOwner : ushort
{
    Regina = 0x01,
    Dylan = 0x02,
    David = 0x04,
}

/// <summary>
/// Per-character weapon USABILITY — the item-catalog owner bits at <c>0x704260[id]+0xA</c>.
///
/// <para>K125: usability is exactly this one static <c>.rdata</c> bit. The same word feeds the shop
/// stock builder (<c>0x408F99</c>: <c>mask &amp; (1 &lt;&lt; byte[scene+0x108F])</c>), the weapon-menu
/// ring builder <c>0x496D70</c> and the char-switch restock <c>0x452390</c>, so setting a bit makes
/// the weapon usable AND shop-purchasable for that character with no other edit. Possession is a
/// separate, GLOBAL thing (<c>flag(9,id)</c> — no character term).</para>
///
/// <para><b>Revert is vanilla-relative.</b> Seven ids ship dual-owned in retail
/// (<c>0x00 0x05 0x10 0x13 0x14 0x16 0x19</c>), so a blanket <c>flags &amp; ~bit</c> revert would
/// strip a bit the retail build set. <see cref="Revoke"/> restores that bit's vanilla state from
/// <see cref="VanillaFlags"/> and fails closed on ids with no recorded baseline.</para>
/// </summary>
public static class Dc2OwnerBits
{
    /// <summary>File offset of item-catalog id 0 (VA <c>0x704260</c>).</summary>
    public const int CatalogOffset = 0xE9260;
    public const int Stride = 12;
    public const int FlagsField = 0xA;

    /// <summary>Catalog ids scanned for ownership (<c>0x00..0x39</c>, K75).</summary>
    public const int IdCount = 0x3A;

    private const ushort ClassMask = 0x08 | 0x10;   // MAIN | SUB — a real weapon row
    private const ushort OwnerMask = 0x01 | 0x02 | 0x04;

    /// <summary>File offset of the flags u16 for a catalog id.</summary>
    public static int FlagsOffset(byte id) => CatalogOffset + id * Stride + FlagsField;

    /// <summary>PSX-verified retail flags for every weapon/sub id (<c>SLUS_012.79</c> catalog
    /// <c>0x80019770</c>, byte-identical in the PC port). The revert baseline — see
    /// <c>data/dc2/weapon-catalog.json</c>.</summary>
    public static IReadOnlyDictionary<byte, ushort> VanillaFlags { get; } = new Dictionary<byte, ushort>
    {
        [0x00] = 0x004B, [0x01] = 0x004A, [0x02] = 0x0049, [0x03] = 0x002A,
        [0x04] = 0x004A, [0x05] = 0x002B, [0x06] = 0x0029, [0x07] = 0x0049,
        [0x08] = 0x0029, [0x09] = 0x004A, [0x0A] = 0x00CC, [0x0B] = 0x004C,
        [0x10] = 0x0053, [0x11] = 0x00D2, [0x12] = 0x00D1, [0x13] = 0x0053,
        [0x14] = 0x0053, [0x15] = 0x00D4, [0x16] = 0x0053, [0x17] = 0x00D2,
        [0x18] = 0x00D1, [0x19] = 0x00D3,
    };

    /// <summary>The raw flags u16 for a catalog id.</summary>
    public static ushort Read(ReadOnlySpan<byte> exe, byte id)
        => BinaryPrimitives.ReadUInt16LittleEndian(exe.Slice(FlagsOffset(id), 2));

    private static void Write(byte[] exe, byte id, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(FlagsOffset(id), 2), value);

    public static bool HasOwner(ReadOnlySpan<byte> exe, byte id, Dc2WeaponOwner owner)
        => (Read(exe, id) & (ushort)owner) != 0;

    /// <summary>Make a weapon usable by <paramref name="owner"/>. Idempotent.</summary>
    public static void Grant(byte[] exe, byte id, Dc2WeaponOwner owner)
    {
        ArgumentNullException.ThrowIfNull(exe);
        Write(exe, id, (ushort)(Read(exe, id) | (ushort)owner));
    }

    /// <summary>Revert <paramref name="owner"/>'s bit to whatever the retail build shipped — NOT a
    /// blanket clear. Seven ids are natively dual-owned, so clearing unconditionally would strip a
    /// bit vanilla set. Only this one bit is touched, so it composes with other levers editing the
    /// same word. Fails closed on ids with no PSX-verified baseline.</summary>
    public static void Revoke(byte[] exe, byte id, Dc2WeaponOwner owner)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!VanillaFlags.TryGetValue(id, out ushort vanilla))
            throw new ArgumentOutOfRangeException(
                nameof(id), $"No PSX-verified vanilla flags recorded for catalog id 0x{id:X2}; refusing to guess.");

        ushort bit = (ushort)owner;
        Write(exe, id, (ushort)((Read(exe, id) & ~bit) | (vanilla & bit)));
    }

    /// <summary>Assign Regina/Dylan ownership exactly while preserving David and every non-owner
    /// catalog flag. Refuses ids without a verified retail baseline, or rows whose non-Regina/Dylan
    /// bits no longer match that baseline.</summary>
    public static void SetOwners(byte[] exe, byte id, bool regina, bool dylan)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!VanillaFlags.TryGetValue(id, out ushort vanilla))
            throw new ArgumentOutOfRangeException(
                nameof(id), $"No PSX-verified vanilla flags recorded for catalog id 0x{id:X2}; refusing to guess.");

        const ushort playableMask = (ushort)Dc2WeaponOwner.Regina | (ushort)Dc2WeaponOwner.Dylan;
        ushort current = Read(exe, id);
        if ((current & ~playableMask) != (vanilla & ~playableMask))
            throw new InvalidOperationException(
                $"Catalog id 0x{id:X2} has unrecognized non-owner flags; refusing to replace ownership.");

        ushort owners = (ushort)((regina ? (ushort)Dc2WeaponOwner.Regina : 0)
                                 | (dylan ? (ushort)Dc2WeaponOwner.Dylan : 0));
        Write(exe, id, (ushort)((current & ~playableMask) | owners));
    }

    /// <summary>Ids the retail build ships usable by BOTH Regina and Dylan, derived from the catalog
    /// rather than hardcoded. Rows with no MAIN/SUB class bit (items, key items) are not weapons and
    /// are excluded — their two clear owner bits must not read as "dual".</summary>
    public static IReadOnlyList<byte> NativelyDual(ReadOnlySpan<byte> exe)
    {
        const ushort dual = (ushort)Dc2WeaponOwner.Regina | (ushort)Dc2WeaponOwner.Dylan;
        var ids = new List<byte>();
        for (int id = 0; id < IdCount; id++)
        {
            ushort flags = Read(exe, (byte)id);
            if ((flags & ClassMask) == 0) continue;
            if ((flags & dual) == dual) ids.Add((byte)id);
        }
        return ids;
    }
}
