namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch that makes a <b>randomizer-injected Triceratops</b>
/// (species E70, spawn TYPE <c>0x09</c>) killable without crashing. RE + decision record:
/// <c>docs/decisions/dc2/crash-rcas/DC2-ST001-TRICERATOPS-WAVE-DEDICATED-BASE-CRASH-RCA.md</c> §7b
/// (and KNOWLEDGE-AND-QUESTIONS.md K90).
///
/// <para><b>Mechanism.</b> E70 is a <i>setpiece</i> model with a short animation set. When it dies,
/// its per-species tick (<c>0x43cf10</c>) dispatches death state <c>[actor+0x54]==3</c> via table
/// <c>0x72110c[3] = 0x43d3d0</c>, which sub-dispatches on <c>[actor+0x55]</c> via <c>0x721160</c>:
/// sub-state 1|2 → <c>0x43e9b0</c> → death-entry <c>0x43ea37</c>, sub-state 3 → <c>0x43ea90</c> →
/// death-entry <c>0x43eb55</c>. The sub-3 death-entry binds animation package index <b>7</b>
/// (<c>0x43eb5d push 7</c>) — a valid clip (E70's alive state <c>0x43e93d</c> also binds 7 and
/// animates fine) — but the sub-1|2 death-entry binds index <b>8</b> (<c>0x43ea3f push 8</c>), which
/// is <b>one past</b> the highest valid index. <c>0x48e050 BindModelPackage</c> does
/// <c>edi = [[actor+0x88] + pkgRef*4]</c> with <b>no bounds check</b>, so index 8 reads past E70's
/// package-pointer table → a garbage descriptor → <c>[actor+0x9c]</c> = a wild pointer (<c>0xff8d</c>)
/// → the anim processor <c>0x48dfe0</c> dereferences it at <c>0x48dff9 mov eax,[ecx]</c> → access
/// violation (dump <c>04-19-41</c>). Death reliably enters sub-state 1|2, so killing an injected E70
/// reliably crashes.</para>
///
/// <para><b>The lever.</b> A single immediate byte: the <c>push 8</c> pkgRef of the sub-1|2 death-entry
/// at VA <c>0x43ea40</c> (<c>.text</c> raw = VA − 0x400000 = <see cref="RemapOffset"/>) is remapped
/// <b>8 → 7</b>, so both death branches request E70's real, in-range death clip. The instruction lives
/// only inside E70's death handler, so no runtime species guard is needed — it can never run for any
/// other actor. No code cave: the fix is entirely in-place.</para>
///
/// <para><b>Scope — DEATH only (known tech debt).</b> This lever fixes the <i>death</i> crash. E70 is a
/// set-piece with a short animation set, so its AI can request other animations it lacks — witnessed
/// live 2026-07-11 (dump <c>10-03-58</c>): a <b>side attack</b> binds an out-of-range attack
/// <c>pkgRef</c> → <c>[actor+0xa0]</c> wild → AV at <c>0x479c0f</c> (RCA §7d, K95). Making an injected
/// Triceratops a fully crash-safe combat enemy needs every AI-requestable animation audited, or a general
/// out-of-range guard in <c>0x48E050</c> — neither is built. Deferred.</para>
///
/// <para><b>Safety.</b> The offset is specific to the pristine rebirth build (length
/// <see cref="Dc2WpGatePatch.ExpectedLength"/>), and <see cref="Apply"/> refuses anything else: exact
/// length + the original death-entry bytes (<c>inc cl; push 8; push esi</c>) at their offset. All
/// methods work on an in-memory <c>byte[]</c>; file I/O and pristine <c>.bak</c> backup are the
/// installer's concern. Touches exactly one byte, so it composes with every other Dino2.exe patch.</para>
/// </summary>
public static class Dc2TriceratopsKillablePatch
{
    /// <summary>File offset of the death-entry anchor (VA <c>0x43ea3d</c>): the 5 bytes
    /// <c>inc cl; push 8; push esi</c> whose <c>push 8</c> immediate (offset +3) the lever remaps.</summary>
    public const int AnchorOffset = 0x3EA3D;

    /// <summary>File offset of the single remapped byte (VA <c>0x43ea40</c>) — the <c>push</c> immediate
    /// (E70 death animation-package index) inside the sub-state 1|2 death-entry <c>0x43ea37</c>.</summary>
    public const int RemapOffset = AnchorOffset + 3; // 0x3EA40

    // inc cl (FE C1); push imm8 (6A ??); push esi (56)
    private static readonly byte[] AnchorOriginal = { 0xFE, 0xC1, 0x6A, 0x08, 0x56 }; // push 8 (out of range)
    private static readonly byte[] AnchorPatched  = { 0xFE, 0xC1, 0x6A, 0x07, 0x56 }; // push 7 (valid death clip)

    private const byte VanillaPkgRef = 0x08;
    private const byte KillablePkgRef = 0x07;

    private static bool RightBuild(ReadOnlySpan<byte> exe) => exe.Length == Dc2WpGatePatch.ExpectedLength;

    private static bool Matches(ReadOnlySpan<byte> exe, int offset, ReadOnlySpan<byte> expected)
        => offset + expected.Length <= exe.Length && exe.Slice(offset, expected.Length).SequenceEqual(expected);

    /// <summary>True iff <paramref name="exe"/> is the pristine rebirth build, ready to patch: correct
    /// length and the original death-entry bytes (<c>push 8</c>) present. False once patched, or for any
    /// other file. Inspects only this patch's own site, so it stays true when other Dino2.exe patches
    /// (raptor / trex-killable / shop / bgm) are already applied.</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe)
        => RightBuild(exe) && Matches(exe, AnchorOffset, AnchorOriginal);

    /// <summary>True iff this exe already carries the killable-Triceratops lever (so the installer can
    /// skip it idempotently).</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
        => RightBuild(exe) && Matches(exe, AnchorOffset, AnchorPatched);

    /// <summary>Remap the death-entry pkgRef in place. Throws <see cref="InvalidOperationException"/>
    /// (leaving <paramref name="exe"/> untouched) unless <see cref="IsRecognizedPristine"/> — so an
    /// unknown build, an already-patched file, or a wrong-length buffer is never corrupted.</summary>
    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!IsRecognizedPristine(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized pristine rebirth build the killable-Triceratops lever "
                + "targets (wrong length, already patched, or a different version); refusing to patch.");
        exe[RemapOffset] = KillablePkgRef;
    }

    /// <summary>Revert the lever's single byte (pkgRef 7 → 8), leaving every other Dino2.exe patch
    /// intact. No-op-safe: throws only on a wrong-length buffer.</summary>
    public static void Restore(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException("Dino2.exe wrong length; refusing to restore.");
        if (!IsApplied(exe)) return;
        exe[RemapOffset] = VanillaPkgRef;
    }
}
