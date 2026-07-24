namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch that cancels the user-labelled tail-looking
/// <b>missed-grab continuation</b> of a randomizer-injected E80 Mosasaurus outside its four native rooms.
/// The legacy class, config, and CLI names remain for compatibility; the active patch does not redirect a
/// selector or claim a bite substitution. Decision record + full RE:
/// <c>docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md</c> §10.6.
///
/// <para><b>Mechanism.</b> The hook is inside the selector-6 B-handler <c>0x440860</c>, which the pristine
/// nested B dispatch table <c>0x721218</c> maps from selector 6. It steals the six-byte ref-5 bind/effect
/// head at VA <c>0x440942</c>. The cave reads
/// <c>word[[0x876DB8]+0x1090]</c>: native rooms <c>{0x0700,0x0702,0x0703,0x0704}</c> replay the displaced
/// instructions and resume at <c>0x440948</c>; every other room enters the original cleanup at
/// <c>0x44097f</c>, skipping only the ref-5 continuation. Successful-contact substates 3/4 are untouched.
/// No TYPE guard or <c>actor+0x55</c> selector rewrite is needed at this handler.</para>
///
/// <para><b>Safety.</b> Offsets are specific to the pristine rebirth build
/// (<see cref="Dc2WpGatePatch.ExpectedLength"/>), so <see cref="Apply"/> refuses anything else: exact
/// length + the original six-byte hook sentinel + the cave region being free zero slack. In-memory
/// <c>byte[]</c> only; file I/O and pristine <c>.bak</c> are the installer's concern. Restores only its own
/// hook and cave slices, and recognizes the exact released legacy candidate only for restoration/migration.</para>
/// </summary>
public static class Dc2MosaTailRedirectPatch
{
    /// <summary>File offset of the selector-6 missed-grab ref-5 transition (VA <c>0x440942</c>): steals
    /// <c>mov al,[esi+0x56]; push ebx; push 0x40</c> (six bytes).</summary>
    public const int HookOffset = 0x40942;

    /// <summary>File offset of the cave (VA <c>0x4E7600</c>) — end-of-<c>.text</c> zero slack, below the
    /// T-Rex / Inostra / grab / knockback caves so the levers compose.</summary>
    public const int CaveOffset = 0xE7600;

    /// <summary>Length of the installed cave (and the free slack required when pristine).</summary>
    public const int CaveLength = 49;

    private const int HookLen = 6;

    // ---- the two reversible edits ----
    // Hook steals: mov al,[esi+0x56] (8A 46 56) ; push ebx (53) ; push 0x40 (6A 40).
    private static readonly byte[] HookOriginal = { 0x8A, 0x46, 0x56, 0x53, 0x6A, 0x40 };
    private static readonly byte[] HookPatched = { 0xE9, 0xB9, 0x6C, 0x0A, 0x00, 0x90 }; // jmp 0x4E7600 ; nop

    // 49 bytes (capstone-verified): save eax; read room; native 0700 or inclusive 0702..0704 replays
    // the displaced six bytes and jumps to 0x440948; all other rooms jump to original cleanup 0x44097f.
    private static readonly byte[] Cave = Convert.FromHexString(
        "50a1b86d87000fb78090100000663d00077412663d02077206663d0407760658e95a93f5ff"
        + "588a4656536a40e91793f5ff");

    // The released legacy candidate shipped in v0.5.0-v0.5.2. These bytes are historical compatibility
    // fingerprints only: they can be recognized and restored, never emitted by Apply.
    private const int LegacyHookOffset = 0x3FC80;
    private const int LegacyCaveLength = 66;
    private static readonly byte[] LegacyHookOriginal = { 0x56, 0x8B, 0x74, 0x24, 0x08 };
    private static readonly byte[] LegacyHookPatched = { 0xE9, 0x7B, 0x79, 0x0A, 0x00 };
    private static readonly byte[] LegacyCave = Convert.FromHexString(
        "568b742408807e580a7532a1b86d87000fb788901000006681f90007741f"
        + "6681f9020774186681f9030774116681f90407740a807e55027504c6465500e94386f5ff");

    private static bool RightBuild(ReadOnlySpan<byte> exe) => exe.Length == Dc2WpGatePatch.ExpectedLength;

    private static bool Matches(ReadOnlySpan<byte> exe, int offset, ReadOnlySpan<byte> expected)
        => offset + expected.Length <= exe.Length && exe.Slice(offset, expected.Length).SequenceEqual(expected);

    private static bool CaveIsFreeSlack(ReadOnlySpan<byte> exe, int offset, int length)
    {
        if (offset + length > exe.Length) return false;
        foreach (var b in exe.Slice(offset, length))
            if (b != 0x00) return false;
        return true;
    }

    /// <summary>True iff <paramref name="exe"/> is the pristine rebirth build, ready to patch: correct
    /// length, the original six-byte hook sentinel present, and the cave region free zero slack. (False once patched,
    /// or for any other file. Only inspects this patch's own two sites, so it stays true when the other
    /// Dino2.exe patches are already applied.)</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
        && Matches(exe, HookOffset, HookOriginal)
        && CaveIsFreeSlack(exe, CaveOffset, CaveLength);

    /// <summary>True iff this exe already carries the missed-grab cancellation lever, so the installer can skip it
    /// idempotently.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
        && Matches(exe, HookOffset, HookPatched)
        && Matches(exe, CaveOffset, Cave);

    /// <summary>True only for the exact released legacy candidate, with the current hook still pristine.
    /// This fingerprint exists solely to permit a safe in-memory restore/migration; <see cref="Apply"/>
    /// never emits it.</summary>
    public static bool IsLegacyApplied(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
        && Matches(exe, HookOffset, HookOriginal)
        && Matches(exe, LegacyHookOffset, LegacyHookPatched)
        && Matches(exe, CaveOffset, LegacyCave);

    /// <summary>Apply the validated hook + cave in place. Throws <see cref="InvalidOperationException"/>
    /// (leaving <paramref name="exe"/> untouched) unless <see cref="IsRecognizedPristine"/> — so an unknown
    /// build, a legacy/partial patch, or a wrong-length buffer is never corrupted.</summary>
    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!IsRecognizedPristine(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized pristine rebirth build the Mosasaurus missed-grab cancellation lever "
                + "targets (wrong length, already patched, or a different version); refusing to patch.");
        HookPatched.CopyTo(exe.AsSpan(HookOffset));
        Cave.CopyTo(exe.AsSpan(CaveOffset));
    }

    /// <summary>Revert the current lever's own two slices, or the exact released legacy fingerprint's two
    /// slices, leaving every other Dino2.exe patch intact. No-op-safe: throws only on a wrong-length buffer.</summary>
    public static void Restore(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException("Dino2.exe wrong length; refusing to restore.");
        if (IsApplied(exe))
        {
            HookOriginal.CopyTo(exe.AsSpan(HookOffset));
            exe.AsSpan(CaveOffset, CaveLength).Clear();
        }
        else if (IsLegacyApplied(exe))
        {
            LegacyHookOriginal.CopyTo(exe.AsSpan(LegacyHookOffset));
            exe.AsSpan(CaveOffset, LegacyCaveLength).Clear();
        }
    }
}
