namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible patch for the Dino Crisis 2 <b>Classic REbirth</b> wrapper
/// <c>ddraw.dll</c> (the DirectDraw→Direct3D9 re-implementation) that fixes the <b>MotionTrail
/// over-brightening</b>.
///
/// <para><b>The bug.</b> The wrapper's motion-trail is recursive framebuffer feedback: each frame it
/// averages the two ping-pong accumulation buffers 50/50 (the <c>BLUR.PSO</c> pixel shader does
/// <c>lerp(A, B, 0.5)</c>). That average is energy-conserving only if the texture <i>read</i> encoding
/// equals the render-target <i>write</i> encoding. The trail renderer (<c>0x1006b2e0</c>) never pins
/// sRGB state, so it inherits whatever gamma state the previous frame's composite left set; when read
/// and write disagree the buffer is re-encoded through the ~2.2 gamma curve every frame, and because
/// the buffer is fed back the error compounds into a runaway brightness ramp (the author's
/// "gamma is too high"). Decode/RCA in <c>docs/dc2</c>.</para>
///
/// <para><b>The fix.</b> Force the trail draw to operate in a single, consistent encoding by setting
/// <c>D3DSAMP_SRGBTEXTURE=0</c> on both source samplers and <c>D3DRS_SRGBWRITEENABLE=0</c> just before
/// the draw (PSX-faithful: average in stored space). It is implemented as a 5-byte hook over the
/// renderer's <c>mov eax,ds:0x1033ba44</c> at file offset <see cref="HookOffset"/> that jumps to a
/// 68-byte code cave written into existing <c>int3</c> padding at <see cref="CaveOffset"/> (the cave
/// loads the device pointer position-independently, issues the three state-sets, restores registers and
/// jumps back). The DLL is ASLR-enabled (DYNAMICBASE), and the overwritten instruction's absolute
/// operand carried a base-relocation entry, so the loader would otherwise rewrite the hook's jump
/// target — the patch also neutralizes that one <c>.reloc</c> entry at <see cref="RelocEntryOffset"/>
/// (HIGHLOW <c>0x3628</c> → type-0 ABSOLUTE no-op).</para>
///
/// <para><b>Safety.</b> The offsets are specific to one shipping build, so <see cref="Apply"/> refuses
/// anything that is not the exact known pristine DLL: it checks the exact file length plus the three
/// sentinel byte windows (original hook bytes, the cave region being free <c>int3</c> slack, and the
/// reloc entry). A different Classic REbirth build fails recognition and is left untouched rather than
/// corrupted. All methods work on an in-memory <c>byte[]</c>; reading/backing up/writing the file is
/// the installer's concern (<c>Dc2MotionTrailInstaller</c>).</para>
/// </summary>
public static class Dc2DdrawTrailPatch
{
    /// <summary>Exact size of the recognized pristine <c>ddraw.dll</c> (md5 <c>a545d485…</c>). A
    /// length mismatch means a different build, which these offsets must not be applied to.</summary>
    public const int ExpectedLength = 3_609_088;

    /// <summary>File offset of the hooked instruction <c>mov eax,ds:0x1033ba44</c> (VA 0x1006b627).</summary>
    public const int HookOffset = 0x6AA27;

    /// <summary>File offset of the code cave (VA 0x1000a3c5), an inter-function <c>int3</c> pad.</summary>
    public const int CaveOffset = 0x97C5;

    /// <summary>Bytes of the cave the patch installs (and the slack it requires when pristine).</summary>
    public const int CaveLength = 68;

    /// <summary>File offset of the <c>.reloc</c> HIGHLOW entry (RVA 0x6b628) covering the hook's
    /// operand; neutralized so ASLR can't rewrite the jump target.</summary>
    public const int RelocEntryOffset = 0x361C06;

    // ---- the three reversible edits ----
    private static readonly byte[] HookOriginal = { 0xA1, 0x44, 0xBA, 0x33, 0x10 }; // mov eax,ds:0x1033ba44
    private static readonly byte[] HookPatched  = { 0xE9, 0x99, 0xED, 0xF9, 0xFF }; // jmp 0x1000a3c5

    private static readonly byte[] RelocOriginal = { 0x28, 0x36 }; // HIGHLOW (type 3) offset 0x628
    private static readonly byte[] RelocPatched  = { 0x00, 0x00 }; // ABSOLUTE (type 0) no-op

    /// <summary>The 68-byte cave: <c>push esi</c>; PIC device-pointer load
    /// (<c>call$+5 / pop eax / add eax,0x331679 / mov esi,[eax]</c>);
    /// <c>SetSamplerState(0,SRGBTEXTURE,0)</c>; <c>SetSamplerState(1,SRGBTEXTURE,0)</c>;
    /// <c>SetRenderState(SRGBWRITEENABLE,0)</c>; <c>mov eax,esi</c>; <c>pop esi</c>;
    /// <c>jmp 0x1006b62c</c>.</summary>
    private static readonly byte[] CavePatched = Convert.FromHexString(
        "56e8000000005805791633008b306a006a0b6a00568b0eff91140100006a006a0b6a01568b0e" +
        "ff91140100006a0068c2000000568b0eff91e40000008bc65ee923120600");

    private static bool Matches(ReadOnlySpan<byte> dll, int offset, ReadOnlySpan<byte> expected)
        => offset + expected.Length <= dll.Length && dll.Slice(offset, expected.Length).SequenceEqual(expected);

    private static bool CaveIsFreeSlack(ReadOnlySpan<byte> dll)
    {
        if (CaveOffset + CaveLength > dll.Length) return false;
        foreach (var b in dll.Slice(CaveOffset, CaveLength))
            if (b != 0xCC) return false;
        return true;
    }

    /// <summary>True iff <paramref name="dll"/> is the exact known pristine build, ready to patch:
    /// correct length, original hook bytes present, the cave region is free <c>int3</c> slack, and the
    /// reloc entry is the original HIGHLOW. (False once patched, or for any other file.)</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> dll)
        => dll.Length == ExpectedLength
           && Matches(dll, HookOffset, HookOriginal)
           && Matches(dll, RelocEntryOffset, RelocOriginal)
           && CaveIsFreeSlack(dll);

    /// <summary>True iff this DLL already carries the trail fix (hook jump + neutralized reloc),
    /// so the installer can skip it idempotently.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> dll)
        => dll.Length == ExpectedLength
           && Matches(dll, HookOffset, HookPatched)
           && Matches(dll, RelocEntryOffset, RelocPatched);

    /// <summary>
    /// Apply the three edits in place. Throws <see cref="InvalidOperationException"/> (leaving
    /// <paramref name="dll"/> untouched) unless <see cref="IsRecognizedPristine"/> — so an unknown
    /// build, an already-patched file, or a wrong-length buffer is never corrupted.
    /// </summary>
    public static void Apply(byte[] dll)
    {
        ArgumentNullException.ThrowIfNull(dll);
        if (!IsRecognizedPristine(dll))
            throw new InvalidOperationException(
                "ddraw.dll is not the recognized pristine Classic REbirth build the MotionTrail patch "
                + "targets (wrong length, already patched, or a different version); refusing to patch.");

        HookPatched.CopyTo(dll.AsSpan(HookOffset));
        CavePatched.CopyTo(dll.AsSpan(CaveOffset));
        RelocPatched.CopyTo(dll.AsSpan(RelocEntryOffset));
    }
}
