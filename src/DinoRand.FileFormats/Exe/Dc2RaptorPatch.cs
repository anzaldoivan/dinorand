namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patches for the raptor tier system
/// (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md; build-gated like <see cref="Dc2WpGatePatch"/> — the offsets are
/// specific to the rebirth-package pristine build, length <see cref="Dc2WpGatePatch.ExpectedLength"/>).
/// Two independent levers:
/// <list type="bullet">
/// <item><b>Wave pair table</b> (VA <c>0x703E9C</c>, .rdata → file <c>0xE8E9C</c>): 10 entries × 4
/// bytes, bytes 0–1 = the two candidate tier variants the wave spawner coin-flips between
/// (<c>idx = desc4 + 3*[G+0x1388]</c>). Rewriting both bytes of every entry with seeded weighted
/// draws gives exact weighted tier control for ALL wave-spawned raptors.</item>
/// <item><b>Blue-raptor combo threshold</b> (arm site VA <c>0x41E59F</c>:
/// <c>cmp word [eax+0xC24], imm8; jbe</c> → imm8 at file <c>0x1E5A6</c>, vanilla <c>0x13</c>):
/// the max-combo count that arms the variant-5 super-raptor latch for the next room. User value
/// N (1–20) ⇒ imm8 N−1 ("spawn at ≥ N hits"); 20 = vanilla.</item>
/// </list>
/// All methods work on an in-memory <c>byte[]</c>; file I/O and pristine <c>.bak</c> backup are the
/// installer's concern (same contract as <see cref="Dc2MusicTablePatch"/>).
/// </summary>
public static class Dc2RaptorPatch
{
    /// <summary>File offset of the wave pair table (VA <c>0x703E9C</c>, .rdata raw = VA − 0x61B000).</summary>
    public const int PairTableOffset = 0xE8E9C;

    /// <summary>Entries in the pair table; each is 4 bytes of which bytes 0–1 are the variant pair.</summary>
    public const int PairTableEntries = 10;

    /// <summary>Vanilla pair table (all 40 bytes) — the pristine-recognition window and the restore image.</summary>
    public static ReadOnlySpan<byte> PairTableVanilla => new byte[]
    {
        0x00,0x00,0x01,0x00, 0x00,0x01,0x01,0x00, 0x01,0x01,0x01,0x00, 0x06,0x06,0x01,0x00,
        0x02,0x03,0x00,0x00, 0x03,0x03,0x01,0x00, 0x04,0x04,0x01,0x00, 0x02,0x04,0x00,0x00,
        0x04,0x04,0x01,0x00, 0x05,0x05,0x01,0x00,
    };

    /// <summary>File offset of the combo-threshold imm8 (VA <c>0x41E5A6</c>, .text raw = VA − 0x400000).</summary>
    public const int ComboImmOffset = 0x1E5A6;

    private const int ComboSiteOffset = 0x1E59F; // cmp word [eax+0xC24], imm8; jbe
    private static readonly byte[] ComboSitePrefix = { 0x66, 0x83, 0xB8, 0x24, 0x0C, 0x00, 0x00 };

    public const int VanillaComboThreshold = 20; // imm8 0x13, arm when combo > 19
    public const int MinComboThreshold = 1;
    public const int MaxComboThreshold = 20;

    private const int MaxVariant = 7; // v8+ = garbage stat records (RAPTOR-TIER-RE.md §1)

    private static bool RightBuild(ReadOnlySpan<byte> exe) => exe.Length == Dc2WpGatePatch.ExpectedLength;

    /// <summary>True iff the exe is the recognized build AND the combo cmp site is intact (any imm8 —
    /// re-rolling a seed re-patches an already-patched exe restored from .bak by the installer).</summary>
    public static bool IsComboSiteRecognized(ReadOnlySpan<byte> exe) =>
        RightBuild(exe) && exe.Slice(ComboSiteOffset, ComboSitePrefix.Length).SequenceEqual(ComboSitePrefix)
                        && exe[ComboImmOffset + 1] == 0x76; // jbe follows the imm8

    /// <summary>True iff the exe is the recognized build with the VANILLA pair table.</summary>
    public static bool IsPairTablePristine(ReadOnlySpan<byte> exe) =>
        RightBuild(exe) && exe.Slice(PairTableOffset, PairTableVanilla.Length).SequenceEqual(PairTableVanilla);

    /// <summary>True iff the exe is the recognized build (pair table pristine or already re-paired —
    /// entry structure intact: bytes 2–3 of every entry unchanged, pair bytes all ≤ 7).</summary>
    public static bool IsPairTableRecognized(ReadOnlySpan<byte> exe)
    {
        if (!RightBuild(exe)) return false;
        for (int i = 0; i < PairTableEntries; i++)
        {
            var entry = exe.Slice(PairTableOffset + i * 4, 4);
            var vanilla = PairTableVanilla.Slice(i * 4, 4);
            if (entry[0] > MaxVariant || entry[1] > MaxVariant) return false;
            if (entry[2] != vanilla[2] || entry[3] != vanilla[3]) return false;
        }
        return true;
    }

    /// <summary>Write a planned 20-byte pair set (from <c>Dc2RaptorTierPlanner.PlanPairTable</c>)
    /// into the table: pairs[2i]/pairs[2i+1] → entry i bytes 0/1. Throws (buffer untouched) on an
    /// unrecognized build/table or an out-of-range variant.</summary>
    public static void ApplyPairTable(byte[] exe, ReadOnlySpan<byte> pairs)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (pairs.Length != PairTableEntries * 2)
            throw new ArgumentException($"expected {PairTableEntries * 2} pair bytes", nameof(pairs));
        foreach (var v in pairs)
            if (v > MaxVariant)
                throw new ArgumentOutOfRangeException(nameof(pairs), v, "raptor variant must be 0..7");
        if (!IsPairTableRecognized(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized rebirth build (or the wave pair table is unrecognizable); "
                + "refusing to patch.");
        for (int i = 0; i < PairTableEntries; i++)
        {
            exe[PairTableOffset + i * 4] = pairs[i * 2];
            exe[PairTableOffset + i * 4 + 1] = pairs[i * 2 + 1];
        }
    }

    /// <summary>Set the blue-raptor combo threshold: variant-5 super raptors arm when the max combo
    /// reaches <paramref name="threshold"/> hits (1–20; 20 = vanilla). Throws (buffer untouched) on
    /// an unrecognized build/site or an out-of-range threshold.</summary>
    public static void ApplyComboThreshold(byte[] exe, int threshold)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (threshold is < MinComboThreshold or > MaxComboThreshold)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "combo threshold must be 1..20");
        if (!IsComboSiteRecognized(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized rebirth build (or the combo cmp site is unrecognizable); "
                + "refusing to patch.");
        exe[ComboImmOffset] = (byte)(threshold - 1); // cmp …, imm8; jbe ⇒ arm when combo > imm8
    }
}
