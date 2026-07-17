using System.Text.Json;

namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Reversible scramble of Dino Crisis 2's sub-level elevator security-code candidate table inside
/// <c>Dino2.exe</c> (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §3, decode K108): the 8 candidate
/// codes are <c>mov [esp+N], imm32</c> immediates inside the setup function <c>0x450d60</c>, one
/// byte per digit (LOW byte = FIRST displayed digit, RAW encoding). The runtime picks one via
/// <c>prng()&amp;7</c> into <c>scene_mgr+0x1204</c>, which BOTH the "Elevator Security Code" file
/// display and the keypad check read — displayed==checked holds automatically under any rewrite,
/// so no document work is needed. Offsets + vanilla codes come from the authored manifest
/// <c>data/dc2/puzzle-codes.json</c> (embedded; the DC2 mirror of <c>data/dc1/puzzle-codes.json</c>).
/// Generated digits stay 0–5 (all vanilla codes do; keypad acceptance of 6–9 is unproven — K108 G1).
/// The scramble is computed from scratch each time, so re-running with another seed never compounds.
/// </summary>
public static class Dc2ElevatorCodePatch
{
    /// <summary>Embedded-resource logical name of the manifest (see DinoRand.FileFormats.csproj).</summary>
    public const string ManifestResourceName = "DinoRand.FileFormats.Data.dc2.puzzle-codes.json";

    /// <summary>Digits per code (the keypad is 4-digit).</summary>
    public const int DigitCount = 4;

    /// <summary>Generated digits are 0..5 (exclusive upper bound 6) — the vanilla alphabet.</summary>
    public const int DigitAlphabet = 6;

    /// <summary>File offsets of the 8 imm32 digit-byte slots (VA = offset + 0x400000), from the manifest.</summary>
    public static readonly IReadOnlyList<int> Imm32FileOffsets;

    /// <summary>The 8 vanilla candidate codes, in slot order, from the manifest.</summary>
    public static readonly IReadOnlyList<string> VanillaCodes;

    static Dc2ElevatorCodePatch()
    {
        using var s = typeof(Dc2ElevatorCodePatch).Assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidOperationException($"embedded resource '{ManifestResourceName}' not found");
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        Imm32FileOffsets = root.GetProperty("elevator_code_table").GetProperty("imm32_file_offsets")
            .EnumerateArray().Select(e => Convert.ToInt32(e.GetString()!, 16)).ToArray();
        VanillaCodes = root.GetProperty("family")[0].GetProperty("vanilla_candidates")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    /// <summary>One candidate slot before/after a scramble.</summary>
    public readonly record struct CodeEntry(int Slot, string OldCode, string NewCode);

    /// <summary>
    /// Write 8 distinct seed-derived 4-digit codes (digits 0–5) into the candidate slots.
    /// Validates first via <see cref="Validate"/> and writes nothing on failure.
    /// </summary>
    public static CodeEntry[] Scramble(byte[] exe, int seed)
    {
        ArgumentNullException.ThrowIfNull(exe);
        Validate(exe);

        uint rng = (uint)seed;
        var codes = new List<string>(Imm32FileOffsets.Count);
        while (codes.Count < Imm32FileOffsets.Count)
        {
            var digits = new char[DigitCount];
            for (int d = 0; d < DigitCount; d++)
                digits[d] = (char)('0' + NextRand(ref rng) % DigitAlphabet);
            var code = new string(digits);
            if (!codes.Contains(code))
                codes.Add(code);
        }

        var result = new CodeEntry[Imm32FileOffsets.Count];
        for (int i = 0; i < Imm32FileOffsets.Count; i++)
        {
            result[i] = new CodeEntry(i, ReadCode(exe, i), codes[i]);
            WriteCode(exe, i, codes[i]);
        }
        return result;
    }

    /// <summary>Rewrite all 8 slots to the vanilla candidates (the un-scramble; every other byte —
    /// and any other patch — untouched). Validates first; no-op on a pristine exe.</summary>
    public static void RestoreCanonical(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        Validate(exe);
        for (int i = 0; i < Imm32FileOffsets.Count; i++)
            WriteCode(exe, i, VanillaCodes[i]);
    }

    /// <summary>True iff every slot holds its vanilla candidate (no scramble applied).</summary>
    public static bool IsCanonical(byte[] exe)
    {
        Validate(exe);
        for (int i = 0; i < Imm32FileOffsets.Count; i++)
            if (ReadCode(exe, i) != VanillaCodes[i])
                return false;
        return true;
    }

    /// <summary>
    /// Throw <see cref="InvalidOperationException"/> unless <paramref name="exe"/> is the recognized
    /// build (exact length) whose 8 slots each sit inside their pinned <c>c7 44 24 disp8</c>
    /// (<c>mov [esp+4*(slot+1)], imm32</c>) instruction with all digit bytes in 0–5 — i.e. pristine
    /// or previously scrambled by this patch, never anything else.
    /// </summary>
    public static void Validate(byte[] exe)
    {
        if (exe.Length != Dc2WpGatePatch.ExpectedLength)
            throw new InvalidOperationException(
                $"Dino2.exe has unexpected length {exe.Length} (expected {Dc2WpGatePatch.ExpectedLength}) — unrecognized build; refusing to touch the elevator-code table.");

        for (int i = 0; i < Imm32FileOffsets.Count; i++)
        {
            int off = Imm32FileOffsets[i];
            byte disp = (byte)(4 * (i + 1));
            if (exe[off - 4] != 0xC7 || exe[off - 3] != 0x44 || exe[off - 2] != 0x24 || exe[off - 1] != disp)
                throw new InvalidOperationException(
                    $"elevator-code slot {i}: expected the 'mov [esp+0x{disp:x2}], imm32' opcode (c7 44 24 {disp:x2}) at file 0x{off - 4:X} — unrecognized build; refusing to touch the elevator-code table.");
            for (int d = 0; d < DigitCount; d++)
                if (exe[off + d] >= DigitAlphabet)
                    throw new InvalidOperationException(
                        $"elevator-code slot {i}: digit byte {d} at file 0x{off + d:X} is 0x{exe[off + d]:X2}, not a 0–5 keypad digit — unrecognized build; refusing to touch the elevator-code table.");
        }
    }

    /// <summary>The 4-digit code in <paramref name="slot"/>, first displayed digit first.</summary>
    public static string ReadCode(byte[] exe, int slot)
    {
        int off = Imm32FileOffsets[slot];
        var digits = new char[DigitCount];
        for (int d = 0; d < DigitCount; d++)
            digits[d] = (char)('0' + exe[off + d]);
        return new string(digits);
    }

    private static void WriteCode(byte[] exe, int slot, string code)
    {
        int off = Imm32FileOffsets[slot];
        for (int d = 0; d < DigitCount; d++)
            exe[off + d] = (byte)(code[d] - '0');
    }

    // splitmix32 — same generator as Dc2ShopTablePatch so seeds behave consistently across levers.
    private static uint NextRand(ref uint state)
    {
        state += 0x9E3779B9;
        uint z = state;
        z = (z ^ (z >> 16)) * 0x21F0AAAD;
        z = (z ^ (z >> 15)) * 0x735A2D97;
        return z ^ (z >> 15);
    }
}
