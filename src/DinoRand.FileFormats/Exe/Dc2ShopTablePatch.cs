using System.Buffers.Binary;

namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Reversible shuffle of Dino Crisis 2's shop economy inside <c>Dino2.exe</c>
/// (docs/decisions/dc2/shop/DC2-SHOP-RANDO-PLAN.md, static decode 2026-07-05):
/// <list type="bullet">
/// <item>master price/ammo table at VA <c>0x71DCB8</c> (id*8: <c>{price:u32, qty:u16, qty:u16}</c>,
/// sentinel price 999999 = not-for-sale) — prices are permuted among the for-sale ids;</item>
/// <item>item-catalog availability bitmasks at VA <c>0x704260 + id*12 + 0xA</c> (tested against
/// <c>1 &lt;&lt; byte[scene+0x108F]</c> at <c>0x408F99</c> — the shift is by the current-character
/// code, NOT a "shop progression level"; K116) — permuted among the same ids, so every item stays
/// purchasable by some character (never zeroed).</item>
/// <item><b>tools economy</b> — the recovery-item shop (Hemostat/Med Pak S/M/L, catalog ids
/// 0x1A..0x1D) is priced in a dedicated stride-8 table (working <c>word[0x71D8B8+6]</c>, master
/// <c>word[0x71DD8E]</c>, both difficulty-scaled by the same rebuild <c>0x40D780</c> as weapons —
/// decode K117). Those four prices are permuted freely; recovery items never gate progression, so
/// there is no protected subset.</item>
/// </list>
/// Only the 11 for-sale weapon ids of the validated shop slice (ids 0x00..0x19) plus the four
/// recovery ids are touched; ammo qty fields are left alone (semantics unwitnessed — plan I3). The
/// shuffle is computed from the canonical values, so re-running with another seed never compounds.
/// </summary>
public static class Dc2ShopTablePatch
{
    /// <summary>File offset of master price record id 0 (VA <c>0x71DCB8</c>; <c>.data</c> raw <c>0x100000</c> = VA <c>0x71B000</c>).</summary>
    public const int PriceTableOffset = 0x102CB8;

    /// <summary>
    /// File offset of the <em>working</em> price record id 0 (VA <c>0x71D7E8</c>). This is the table
    /// the shop UI and purchase path actually read (<c>0x408FBD/0x4090F2/0x409668</c>). The rebuild
    /// routine <c>0x40D780</c> only copies master→working when the difficulty byte
    /// (<c>scene+0x108E</c>) is easy (<c>&gt;&gt;1</c>) or hard (<c>&lt;&lt;1</c>); on <b>Normal it
    /// skips the write entirely</b> and the statically-baked retail values stand. So a master-only
    /// patch is invisible on Normal — every price edit must be mirrored here (stride 8, same index).
    /// </summary>
    public const int WorkingPriceTableOffset = 0x1027E8;

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

    /// <summary>
    /// Mandatory progression weapons that must never be randomized: <b>Aquagrenade</b> (catalog id
    /// <c>0x0B</c>), <b>Firewall</b> (<c>0x13</c>), and <b>Chain Mine</b> (<c>0x16</c>) are required
    /// to clear progression obstacles, so they are excluded from the permutation and keep their
    /// vanilla price, slot, and stock mask (never repriced, re-gated, or removed). Ids per
    /// <c>data/dc2/items.json</c>.
    /// </summary>
    public static readonly int[] ProtectedIds = { 0x0B, 0x13, 0x16 };

    /// <summary>
    /// Tools economy — the recovery-item shop price table (decode K117). WORKING price is the word
    /// at <c>0x71D8B8+6</c> (the game reads this on Normal), MASTER is the word at <c>0x71DD8E</c>;
    /// both are stride-8, position-indexed (record 0..3 = Hemostat/Med Pak S/M/L), and both are
    /// difficulty-scaled by the same rebuild routine <c>0x40D780</c> that rebuilds weapon prices —
    /// so, like weapons, every price edit must land in both tables to show at all difficulties.
    /// (Resusc. Pak, the Normal+ append record at <c>0x71D8E0</c> with a difficulty-hardcoded price,
    /// is deliberately excluded.)
    /// </summary>
    public const int RecoveryMasterPriceOffset = 0x102D8E;   // VA 0x71DD8E, word, stride 8
    public const int RecoveryWorkingPriceOffset = 0x1028BE;  // VA 0x71D8BE (= 0x71D8B8 + 6), word, stride 8

    /// <summary>Recovery catalog ids in table order (ids per <c>data/dc2/items.json</c>); position-indexed, not id-indexed.</summary>
    public static readonly int[] RecoveryIds = { 0x1A, 0x1B, 0x1C, 0x1D };

    /// <summary>Canonical (retail, Normal-difficulty) recovery prices, in <see cref="RecoveryIds"/> order.</summary>
    public static readonly ushort[] CanonicalRecoveryPrices = { 100, 300, 800, 1000 };

    /// <summary>One for-sale id before/after a shuffle (Old == New for a field left in place). Recovery
    /// entries carry <c>OldMask == NewMask == 0</c> (recovery items have no stock mask).</summary>
    public readonly record struct ShopShuffleEntry(int ItemId, uint OldPrice, uint NewPrice, ushort OldMask, ushort NewMask);

    /// <summary>
    /// Permute prices and availability masks among <see cref="ForSaleIds"/> (independent
    /// splitmix32 Fisher–Yates permutations, keyed by <paramref name="seed"/>). Validates first
    /// via <see cref="Validate"/> and writes nothing on failure. Returns one entry per for-sale id.
    /// </summary>
    public static ShopShuffleEntry[] Shuffle(byte[] exe, int seed, bool shuffleCatalogMasks = true)
    {
        ArgumentNullException.ThrowIfNull(exe);
        Validate(exe, validateCatalogMasks: shuffleCatalogMasks);

        // Only the non-protected ids take part in the permutation; Firewall/Chain Mine stay put.
        int[] free = Enumerable.Range(0, ForSaleIds.Length)
            .Where(i => !ProtectedIds.Contains(ForSaleIds[i])).ToArray();

        uint rng = (uint)seed;
        int[] pricePerm = Permutation(free.Length, ref rng);
        int[] maskPerm = Permutation(free.Length, ref rng);
        // Drawn AFTER the weapon permutations so a given seed's weapon result is unchanged.
        int[] recPerm = Permutation(RecoveryIds.Length, ref rng);

        // Default every id to canonical, then permute the free ids among themselves.
        var newPrices = (uint[])CanonicalPrices.Clone();
        var newMasks = ForSaleIds.Select(id => ReadMask(exe, id)).ToArray();
        for (int k = 0; k < free.Length; k++)
        {
            newPrices[free[k]] = CanonicalPrices[free[pricePerm[k]]];
            if (shuffleCatalogMasks)
                newMasks[free[k]] = CanonicalMasks[free[maskPerm[k]]];
        }

        var result = new ShopShuffleEntry[ForSaleIds.Length + RecoveryIds.Length];
        for (int i = 0; i < ForSaleIds.Length; i++)
        {
            WritePrice(exe, ForSaleIds[i], newPrices[i]);
            if (shuffleCatalogMasks)
                WriteMask(exe, ForSaleIds[i], newMasks[i]);
            result[i] = new ShopShuffleEntry(ForSaleIds[i], CanonicalPrices[i], newPrices[i],
                shuffleCatalogMasks ? CanonicalMasks[i] : newMasks[i], newMasks[i]);
        }

        // Tools economy: permute the recovery prices (no protected subset — none gate progression).
        for (int i = 0; i < RecoveryIds.Length; i++)
        {
            ushort newRec = CanonicalRecoveryPrices[recPerm[i]];
            WriteRecoveryPrice(exe, i, newRec);
            result[ForSaleIds.Length + i] = new ShopShuffleEntry(RecoveryIds[i], CanonicalRecoveryPrices[i], newRec, 0, 0);
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
        for (int i = 0; i < RecoveryIds.Length; i++)
            WriteRecoveryPrice(exe, i, CanonicalRecoveryPrices[i]);
    }

    /// <summary>True iff every for-sale price/mask and recovery price is canonical (no shuffle applied).</summary>
    public static bool IsCanonical(byte[] exe)
    {
        Validate(exe);
        for (int i = 0; i < ForSaleIds.Length; i++)
            if (ReadPrice(exe, ForSaleIds[i]) != CanonicalPrices[i]
                || ReadWorkingPrice(exe, ForSaleIds[i]) != CanonicalPrices[i]
                || ReadMask(exe, ForSaleIds[i]) != CanonicalMasks[i])
                return false;
        for (int i = 0; i < RecoveryIds.Length; i++)
            if (ReadRecoveryPrice(exe, i) != CanonicalRecoveryPrices[i]
                || ReadRecoveryMasterPrice(exe, i) != CanonicalRecoveryPrices[i])
                return false;
        return true;
    }

    /// <summary>
    /// Throw <see cref="InvalidOperationException"/> unless <paramref name="exe"/> is the
    /// recognized build (exact length) whose for-sale prices and masks are each a permutation of
    /// the canonical sets — i.e. pristine or previously shuffled by this patch, never anything
    /// else — and whose not-for-sale slice ids still carry the 999999 sentinel.
    /// </summary>
    public static void Validate(byte[] exe, bool validateCatalogMasks = true)
    {
        if (exe.Length != Dc2WpGatePatch.ExpectedLength)
            throw new InvalidOperationException(
                $"Dino2.exe has unexpected length {exe.Length} (expected {Dc2WpGatePatch.ExpectedLength}) — unrecognized build; refusing to touch the shop tables.");

        var prices = ForSaleIds.Select(id => ReadPrice(exe, id)).ToArray();
        var masks = ForSaleIds.Select(id => ReadMask(exe, id)).ToArray();
        if (!prices.Order().SequenceEqual(CanonicalPrices.Order()) ||
            (validateCatalogMasks && !masks.Order().SequenceEqual(CanonicalMasks.Order())))
            throw new InvalidOperationException(
                "shop price/mask slice is not a permutation of the recognized retail set — unrecognized build; refusing to touch the shop tables.");

        if (!validateCatalogMasks)
        {
            const ushort playableOwnerMask = 0x03;
            for (int i = 0; i < ForSaleIds.Length; i++)
            {
                byte id = (byte)ForSaleIds[i];
                ushort vanilla = Dc2OwnerBits.VanillaFlags[id];
                if ((masks[i] & ~playableOwnerMask) != (vanilla & ~playableOwnerMask))
                    throw new InvalidOperationException(
                        $"shop catalog id 0x{id:X2} has unrecognized non-owner flags; refusing to shuffle prices.");
            }
        }

        var rec = Enumerable.Range(0, RecoveryIds.Length).Select(i => ReadRecoveryPrice(exe, i)).ToArray();
        if (!rec.Order().SequenceEqual(CanonicalRecoveryPrices.Order()))
            throw new InvalidOperationException(
                "recovery price slice is not a permutation of the recognized retail set — unrecognized build; refusing to touch the shop tables.");

        for (int id = 0; id < 0x1A; id++)
            if (!ForSaleIds.Contains(id) && ReadPrice(exe, id) != NotForSale)
                throw new InvalidOperationException(
                    $"shop slice id {id:#x} lost its not-for-sale sentinel — unrecognized build; refusing to touch the shop tables.");
    }

    /// <summary>Master-table Normal price of <paramref name="itemId"/>.</summary>
    public static uint ReadPrice(byte[] exe, int itemId)
        => BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(PriceTableOffset + itemId * 8, 4));

    /// <summary>Working-table (game-read) Normal price of <paramref name="itemId"/> — see <see cref="WorkingPriceTableOffset"/>.</summary>
    public static uint ReadWorkingPrice(byte[] exe, int itemId)
        => BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(WorkingPriceTableOffset + itemId * 8, 4));

    /// <summary>Catalog availability bitmask of <paramref name="itemId"/>.</summary>
    public static ushort ReadMask(byte[] exe, int itemId)
        => BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(CatalogOffset + itemId * 12 + 0xA, 2));

    // Writes both the master table (drives the Easy/Hard rebuild) and the working table (read
    // directly on Normal, where the rebuild skips the write) so the price lands at every difficulty.
    private static void WritePrice(byte[] exe, int itemId, uint price)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(PriceTableOffset + itemId * 8, 4), price);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(WorkingPriceTableOffset + itemId * 8, 4), price);
    }

    private static void WriteMask(byte[] exe, int itemId, ushort mask)
        => BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(CatalogOffset + itemId * 12 + 0xA, 2), mask);

    /// <summary>Working-table (game-read on Normal) recovery price at table position <paramref name="index"/> (0..3).</summary>
    public static ushort ReadRecoveryPrice(byte[] exe, int index)
        => BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(RecoveryWorkingPriceOffset + index * 8, 2));

    /// <summary>Master-table recovery price (drives the Easy/Hard rebuild) at table position <paramref name="index"/> (0..3).</summary>
    public static ushort ReadRecoveryMasterPrice(byte[] exe, int index)
        => BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(RecoveryMasterPriceOffset + index * 8, 2));

    // Mirror into both master (Easy/Hard rebuild source) and working (read directly on Normal), same
    // dual-write reason as the weapon prices.
    private static void WriteRecoveryPrice(byte[] exe, int index, ushort price)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(RecoveryMasterPriceOffset + index * 8, 2), price);
        BinaryPrimitives.WriteUInt16LittleEndian(exe.AsSpan(RecoveryWorkingPriceOffset + index * 8, 2), price);
    }

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
