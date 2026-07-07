namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible 2-byte patch for <c>Dino2.exe</c> that opens the <b>WP-gate</b>: the
/// per-weapon <c>WP&lt;n&gt;A.DAT</c> character-graft request inside <c>LoadWeaponFiles</c>
/// (<c>0x482420</c>) is gated on the item-ownership flag (9,0x33) (a protective-gear unlockable —
/// docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §8). Nopping the <c>je</c> at VA <c>0x48263B</c>
/// makes the WP&lt;n&gt;A load unconditional for chars 0/1, which is what lets the character-skin
/// swap (Dylan → Gail/Rick graft files under Dylan's WP slots) render without the player owning the
/// item. Chosen over granting the flag because the flag also HALVES incoming damage (scaler
/// <c>0x479960</c>) — the patch keeps gameplay untouched. In-game verified 2026-07-03 (§9).
///
/// <para><b>Safety.</b> Mirrors <see cref="Dc2DdrawTrailPatch"/>: offsets are specific to the one
/// shipping build, so <see cref="Apply"/> refuses anything that is not the exact known pristine exe
/// (length + the <c>test eax,eax / je</c> byte window). All methods work on an in-memory
/// <c>byte[]</c>; file I/O and backup are the installer's concern
/// (<c>Dc2CharacterSkinInstaller</c>).</para>
/// </summary>
public static class Dc2WpGatePatch
{
    /// <summary>Exact size of the recognized pristine <c>Dino2.exe</c> (SourceNext/Capcom 2002 PC
    /// port as shipped in the rebirth package). A length mismatch means a different build.</summary>
    public const int ExpectedLength = 1_204_224;

    /// <summary>File offset of the gate's <c>je +0x62</c> (VA <c>0x48263B</c>; .text raw offset =
    /// VA − 0x400000 for this build).</summary>
    public const int GateOffset = 0x8263B;

    private static readonly byte[] ContextOriginal = { 0x85, 0xC0, 0x74, 0x62 }; // test eax,eax; je +0x62
    private static readonly byte[] ContextPatched  = { 0x85, 0xC0, 0x90, 0x90 }; // test eax,eax; nop nop

    private static bool Matches(ReadOnlySpan<byte> exe, ReadOnlySpan<byte> expected)
        => exe.Length == ExpectedLength
           && exe.Slice(GateOffset - 2, expected.Length).SequenceEqual(expected);

    /// <summary>True iff <paramref name="exe"/> is the exact known pristine build, ready to patch.
    /// (False once patched, or for any other file.)</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe) => Matches(exe, ContextOriginal);

    /// <summary>True iff this exe already carries the WP-gate nops, so the installer can skip it
    /// idempotently.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe) => Matches(exe, ContextPatched);

    /// <summary>
    /// Nop the gate in place. Throws <see cref="InvalidOperationException"/> (leaving
    /// <paramref name="exe"/> untouched) unless <see cref="IsRecognizedPristine"/> — an unknown
    /// build, an already-patched file, or a wrong-length buffer is never corrupted.
    /// </summary>
    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!IsRecognizedPristine(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized pristine build the WP-gate patch targets "
                + "(wrong length, already patched, or a different version); refusing to patch.");
        exe[GateOffset] = 0x90;
        exe[GateOffset + 1] = 0x90;
    }
}
