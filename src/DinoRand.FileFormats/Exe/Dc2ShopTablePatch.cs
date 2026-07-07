using System.Buffers.Binary;

namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Reversible shuffle of Dino Crisis 2's shop economy inside <c>Dino2.exe</c>
/// (docs/decisions/dc2/shop/DC2-SHOP-RANDO-PLAN.md, static decode 2026-07-05):
/// <list type="bullet">
/// <item>master price/ammo table at VA <c>0x71DCB8</c> (id*8: <c>{price:u32, qty:u16, qty:u16}</c>,
/// sentinel price 999999 = not-for-sale) — prices are permuted among the for-sale ids;</item>
/// <item>item-catalog availability bitmasks at VA <c>0x704260 + id*12 + 0xA</c> (tested against
/// <c>1 &lt;&lt; shopLevel</c> at <c>0x408F99</c>) — permuted among the same ids, so every item
/// stays purchasable at some shop level (never zeroed).</item>
/// </list>
/// Only the 11 for-sale ids of the validated shop slice (ids 0x00..0x19) are touched; ammo qty
/// fields are left alone (semantics unwitnessed — plan I3). The shuffle is computed from the
/// canonical values, so re-running with another seed never compounds.
/// </summary>
public static class Dc2ShopTablePatch
{
    /// <summary>File offset of master price record id 0 (VA <c>0x71DCB8</c>; <c>.data</c> raw <c>0x100000</c> = VA <c>0x71B000</c>).</summary>
    public const int PriceTableOffset = 0x102CB8;

    /// <summary>File offset of catalog record id 0 (VA <c>0x704260</c>; <c>.rdata</c> raw <c>0xE8000</c> = VA <c>0x703000</c>). Mask word at +0xA of each 12-byte record.</summary>
    public const int CatalogOffset = 0xE9260;

    /// <summary>Master-table price marking an id as not-for-sale (0xF423F).</summary>
    public const uint NotForSale = 999_999;

    /// <summary>The for-sale item ids of the shop slice, ascending (9 of them price-validated
    /// against GameFAQs retail; 0x14/0x16 carry the same layout).</summary>
    public static readonly int[] ForSaleIds = { 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0B, 0x13, 0x14, 0x16 };

    /// <summary>Canonical (retail, Normal-difficulty) prices, in <see cref="ForSaleIds"/> order.</summary>
    public static readonly uint[] CanonicalPrices = { 50_000, 18_000, 8_000, 12_000, 35_000, 50_000, 38_000, 20_000, 5_000, 12_000, 12_000 };

    /// <summary>Canonical availability bitmasks, in <see cref="ForSaleIds"/> order.</summary>
    public static readonly ushort[] CanonicalMasks = { 0x2A, 0x4A, 0x2B, 0x29, 0x49, 0x29, 0x4A, 0x4C, 0x53, 0x53, 0x53 };

    /// <summary>One for-sale id before/after a shuffle (Old == New for a field left in place).</summary>
    public readonly record struct ShopShuffleEntry(int ItemId, uint OldPrice, uint NewPrice, ushort OldMask, ushort NewMask);

    /// <summary>
    /// Permute prices and availability masks among <see cref="ForSaleIds"/> (independent
    /// splitmix32 Fisher–Yates permutations, keyed by <paramref name="seed"/>). Validates first
    /// via <see cref="Validate"/> and writes nothing on failure. Returns one entry per for-sale id.
    /// </summary>
    public static ShopShuffleEntry[] Shuffle(byte[] exe, int seed)
    {
        ArgumentNullException.ThrowIfNull(exe);
        Validate(exe);

        uint rng = (uint)seed;
        int[] pricePerm = Permutation(ForSaleIds.Length, ref rng);
        int[] maskPerm = Permutation(ForSaleIds.Length, ref rng);

        var result = new ShopShuffleEntry[ForSaleIds.Length];
        for (int i = 0; i < ForSaleIds.Length; i++)
        {
            uint newPrice = CanonicalPrices[pricePerm[i]];
            ushort newMask = CanonicalMasks[maskPerm[i]];
            WritePrice(exe, ForSaleIds[i], newPrice);
            WriteMask(exe, ForSaleIds[i], newMask);
            result[i] = new ShopShuffleEntry(ForSaleIds[i], CanonicalPrices[i], newPrice, CanonicalMasks[i], newMask);
        }
        return result;
    }

    /// <summary>Rewrite prices and masks to canonical (the un-shuffle; every other byte — and any
    /// other patch — untouched). Validates first; no-op on a pristine exe.</summary>
    public static void RestoreCanonical(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        Validate(exe);
        for (int i = 0; i < ForSaleIds.Length; i++)
        {
            WritePrice(exe, ForSaleIds[i], CanonicalPrices[i]);
            WriteMask(exe, ForSaleIds[i], CanonicalMasks[i]);
        }
    }

    /// <summary>True iff every for-sale price and mask is canonical (no shuffle applied).</summary>
    public static bool IsCanonical(byte[] exe)
    {
        Validate(exe);
        for (int i = 0; i < ForSaleIds.Length; i++)
            if (ReadPrice(exe, ForSaleIds[i]) != CanonicalPrices[i] || ReadMask(exe, ForSaleIds[i]) != CanonicalMasks[i])
                return false;
        return true;
    }

    /// <summary>
    /// Throw <see cref="InvalidOperationException"/> unless <paramref name="exe"/> is the
    /// recognized build (exact length) whose for-sale prices and masks are each a permutation of
    /// the canonical sets — i.e. pristine or previously shuffled by this patch, never anything
    /// else — and whose not-for-sale slice ids still carry the 999999 sentinel.
    /// </summary>
    public static void Validate(byte[] exe)
    {
        if (exe.Length != Dc2WpGatePatch.ExpectedLength)
            throw new InvalidOperationException(
                $"Dino2.exe has unexpected length {exe.Length} (expected {Dc2WpGatePatch.ExpectedLength}) — unrecognized build; refusing to touch the shop tables.");

        var prices = ForSaleIds.Select(id => ReadPrice(exe, id)).ToArray();
        var masks = ForSaleIds.Select(id => ReadMask(exe, id)).ToArray();
        if (!prices.Order().SequenceEqual(CanonicalPrices.Order()) ||
            !masks.Order().SequenceEqual(CanonicalMasks.Order()))
            throw new InvalidOperationException(
                "shop price/mask slice is not a permutation of the recognized retail set — unrecognized build; refusing to touch the shop tables.");

        for (int id = 0; id < 0x1A; id++)
            if (!ForSaleIds.Contains(id) && ReadPrice(exe, id) != NotForSale)
                throw new InvalidOperationException(
                    $"shop slice id {id:#x} lost its not-for-sale sentinel — unrecognized build; refusing to touch the shop tables.");
    }

    /// <summary>Master-table Normal price of <paramref name="itemId"/>.</summary>
    public static uint ReadPrice(byte[] exe, int itemId)
        => BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(PriceTableOffset + itemId * 8, 4));

    /// <summary>Catalog availability bitmask of <paramref name="itemId"/>.</summary>
    public static ushort ReadMask(byte[] exe, int itemId)
        => BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(CatalogOffset + itemId * 12 + 0xA, 2));

    private static void WritePrice(byte[] exe, int itemId, uint price)
        => BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(PriceTableOffset + itemId * 8, 4), price);

    private static void WriteMask(byte[] exe, int itemId, ushort mask)
        => BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(CatalogOffset + itemId * 12 + 0xA, 2), mask);

    private static int[] Permutation(int n, ref uint rng)
    {
        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        for (int i = n - 1; i > 0; i--)
        {
            int j = (int)(NextRand(ref rng) % (uint)(i + 1));
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        return perm;
    }

    // splitmix32 — same generator as Dc2MusicTablePatch so seeds behave consistently across levers.
    private static uint NextRand(ref uint state)
    {
        state += 0x9E3779B9;
        uint z = state;
        z = (z ^ (z >> 16)) * 0x21F0AAAD;
        z = (z ^ (z >> 15)) * 0x735A2D97;
        return z ^ (z >> 15);
    }
}
