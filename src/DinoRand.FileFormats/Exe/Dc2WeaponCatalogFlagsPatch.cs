namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Repairs the DC2 weapon/item catalog flags word (VA <c>0x704260</c> → file <c>0xE9260</c>, the
/// <c>u16</c> at <c>+0xA</c> of each 12-byte record) to its PSX <b>source-of-truth</b> value —
/// decoded from the original disc <c>SLUS_012.79</c> @ RAM <c>0x80019770</c>
/// (docs/reference/dc2/weapon/DC2-WEAPON-CATALOG-SOURCE-OF-TRUTH.md, data/dc2/weapon-catalog.json).
///
/// <para>External weapon-shuffle tools rewrite these flags in place, and the byte is <b>overloaded</b>:
/// owner (<c>0x01</c> Regina / <c>0x02</c> Dylan / <c>0x04</c> David) + MAIN (<c>0x08</c>) / SUB
/// (<c>0x10</c>) class + the <b>icon-width bit</b> (<c>0x20</c>; byte-verified at <c>0x404E04</c>
/// <c>test byte[id*12+0x70426A],0x20</c> → wide 128px else narrow 64px). So one corrupted flag both
/// mis-renders the weapon-select icon — e.g. Antitank <c>0x09</c> flipped <c>0x4A→0x2A</c> sets the
/// wide bit, so its 128px blit overdraws the neighbour atlas tile ("two weapons in one slot") — and
/// mis-classes the weapon for the ring/ownership logic. Restoring the flags fixes both.</para>
///
/// <para>Icon <c>(U,V)</c> coordinates (<c>+8</c>/<c>+9</c>) are already pristine on every id (they
/// match the PSX disc byte-for-byte) and are <b>never</b> touched. The patch is idempotent and
/// byte-identical on a clean exe — that is the whole point of a "source of truth".</para>
/// </summary>
public static class Dc2WeaponCatalogFlagsPatch
{
    /// <summary>Catalog file offset (VA <c>0x704260</c>) in the recognized rebirth build — same const as
    /// <see cref="Dc2StartingLoadoutPatch"/>.</summary>
    public const int CatalogFileOffset = 0xE9260;
    private const int Stride = 12, FlagsFieldOffset = 0xA;

    /// <summary>The icon-width bit inside the flags word (set = 128px wide icon, clear = 64px).</summary>
    public const ushort WideIconBit = 0x20;

    /// <summary>PSX source-of-truth flags per weapon/item id (<c>SLUS_012.79</c> @ <c>0x80019770</c>;
    /// identical to a pristine <c>Dino2.exe</c> <c>0x704260</c>). The empty rows <c>0x0C–0x0F</c> are
    /// <c>0x0000</c> in both builds and are omitted (nothing to repair).</summary>
    public static readonly IReadOnlyList<(byte Id, ushort Flags)> TruthFlags = new (byte, ushort)[]
    {
        (0x00, 0x004b), (0x01, 0x004a), (0x02, 0x0049), (0x03, 0x002a), (0x04, 0x004a), (0x05, 0x002b),
        (0x06, 0x0029), (0x07, 0x0049), (0x08, 0x0029), (0x09, 0x004a), (0x0a, 0x00cc), (0x0b, 0x004c),
        (0x10, 0x0053), (0x11, 0x00d2), (0x12, 0x00d1), (0x13, 0x0053), (0x14, 0x0053), (0x15, 0x00d4),
    };

    private static int FlagsFo(byte id) => CatalogFileOffset + id * Stride + FlagsFieldOffset;

    /// <summary>The flags word currently stored for <paramref name="id"/> (little-endian).</summary>
    public static ushort ReadFlags(byte[] exe, byte id)
    {
        int o = FlagsFo(id);
        return (ushort)(exe[o] | (exe[o + 1] << 8));
    }

    /// <summary>Ids whose live flags differ from PSX-truth (the corruption set).</summary>
    public static IEnumerable<byte> CorruptedIds(byte[] exe)
    {
        Validate(exe);
        foreach (var (id, truth) in TruthFlags)
            if (ReadFlags(exe, id) != truth) yield return id;
    }

    /// <summary>True iff every catalog flags word already equals PSX-truth.</summary>
    public static bool IsPristine(byte[] exe) => !CorruptedIds(exe).Any();

    /// <summary>Rewrite every catalog flags word to PSX-truth. Touches <b>only</b> the two flag bytes
    /// per id (never the icon <c>U</c>/<c>V</c> at <c>+8</c>/<c>+9</c> or the resource pointers).
    /// Idempotent; returns the number of ids repaired (0 on a clean exe).</summary>
    public static int Apply(byte[] exe)
    {
        Validate(exe);
        int repaired = 0;
        foreach (var (id, truth) in TruthFlags)
        {
            int o = FlagsFo(id);
            if ((ushort)(exe[o] | (exe[o + 1] << 8)) == truth) continue;
            exe[o] = (byte)truth;
            exe[o + 1] = (byte)(truth >> 8);
            repaired++;
        }
        return repaired;
    }

    /// <summary>Throw unless <paramref name="exe"/> is the recognized build length
    /// (<see cref="Dc2WpGatePatch.ExpectedLength"/>).</summary>
    public static void Validate(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (exe.Length != Dc2WpGatePatch.ExpectedLength)
            throw new InvalidOperationException(
                $"Dino2.exe has unexpected length {exe.Length} (expected {Dc2WpGatePatch.ExpectedLength}) — unrecognized build; refusing to touch the weapon catalog.");
    }
}
