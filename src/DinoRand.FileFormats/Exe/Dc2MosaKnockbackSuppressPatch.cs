namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch that stops a <b>randomizer-injected E80 Mosasaurus</b>
/// (spawn TYPE <c>0x0a</c>) from knocking the player OUT OF BOUNDS in a LAND room, while leaving the four
/// native aquatic rooms (ST700/702/703/704) byte-identical. Decision record + full RE:
/// <c>docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md</c> §8.5, <c>KNOWLEDGE-AND-QUESTIONS.md</c>
/// K105.
///
/// <para><b>Mechanism (live capture 2026-07-13).</b> The mosa's tail/proximity knockback is applied by a
/// shared <i>"push actor <c>edi</c> away from actor <c>ebx</c>"</i> knockback/collision-separation
/// applicator whose player-position add head is <c>add word[edi+0x40],ax; mov eax,[edi+0x64]</c> at
/// VA <c>0x452D84</c> (<c>edi</c> = the shoved actor, <c>ebx</c> = the attacker, <c>ax</c>/<c>cx</c> =
/// X/Z delta on the stack). Two aquatic mosas in a cramped land room fire it thousands of times per second
/// shoving each other apart, and the player caught in / tail-hit by that impulse storm is flung far
/// (live: player X 14529→1847→11547). The animation mover <c>0x48e400</c> is only downstream easing — the
/// OOB <i>source</i> is this applicator, and unlike the mover it carries the attacker in a register.</para>
///
/// <para><b>The lever.</b> The hook (<c>jmp</c> + 2 <c>nop</c> over the 7-byte stolen pair) routes to a
/// <see cref="CaveLength"/>-byte cave in the end-of-<c>.text</c> zero slack. The cave replays the shove
/// verbatim <i>unless</i> all three hold — the shoved actor is the player
/// (<c>edi == [[0x876DB8]+0x940]</c>), the attacker is a Mosasaurus (<c>byte[ebx+0x58]==0x0a</c>), and the
/// room is a non-native land room (<c>word[[0x876DB8]+0x1090] ∉ {0x0700,0x0702,0x0703,0x0704}</c>) — in
/// which case it <b>skips the whole shove and its chain propagation</b> (<c>jmp 0x452dac</c>), so the mosa
/// can no longer displace the player in land rooms. Enemy↔enemy separation (<c>edi</c> ≠ player),
/// non-aquatic knockback, and the native aquatic encounters are all untouched.</para>
///
/// <para><b>Safety.</b> Offsets are specific to the pristine rebirth build
/// (<see cref="Dc2WpGatePatch.ExpectedLength"/>), so <see cref="Apply"/> refuses anything else: exact
/// length + the original hook bytes + the cave region being free zero slack. In-memory <c>byte[]</c> only;
/// file I/O and the pristine <c>.bak</c> are the installer's concern. Restores only its own two slices so
/// it composes with the other Dino2.exe patches (its cave sits below the T-Rex and grab-suppress caves in
/// the same slack, chosen not to overlap).</para>
/// </summary>
public static class Dc2MosaKnockbackSuppressPatch
{
    /// <summary>File offset of the knockback applicator's player-position add head (VA <c>0x452D84</c>):
    /// steals <c>add word[edi+0x40],ax; mov eax,[edi+0x64]</c> (7 bytes) so the cave can gate the whole
    /// shove + chain propagation.</summary>
    public const int HookOffset = 0x52D84;

    /// <summary>File offset of the cave (VA <c>0x4E75A0</c>) — end-of-<c>.text</c> zero slack, below the
    /// T-Rex/grab-suppress caves so the levers compose.</summary>
    public const int CaveOffset = 0xE75A0;

    /// <summary>Length of the installed cave (and the free slack required when pristine).</summary>
    public const int CaveLength = 79;

    private const int HookLen = 7;

    // ---- the two reversible edits ----
    // Stolen: add word[edi+0x40],ax (66 01 47 40) ; mov eax,[edi+0x64] (8b 47 64).
    private static readonly byte[] HookOriginal = { 0x66, 0x01, 0x47, 0x40, 0x8B, 0x47, 0x64 };
    private static readonly byte[] HookPatched = { 0xE9, 0x17, 0x48, 0x09, 0x00, 0x90, 0x90 }; // jmp 0x4E75A0 ; nop×2

    // 79-byte cave (capstone-verified). Shape: mov eax,[0x876DB8]; mov eax,[eax+0x940]; cmp edi,eax; jne
    // van (target != player → vanilla); cmp byte[ebx+0x58],0x0a; jne van (attacker != mosa → vanilla);
    // mov eax,[0x876DB8]; movzx eax,word[eax+0x1090]; cmp ax,{0700,0702,0703,0704} je van (native room →
    // vanilla); jmp 0x452dac (SUPPRESS: skip shove + chain). van: mov ax,[esp+0x30] (reload X delta, eax
    // clobbered by the guard); add word[edi+0x40],ax (stolen); mov eax,[edi+0x64] (stolen); jmp 0x452d8b
    // (resume vanilla at the Z add).
    private static readonly byte[] Cave = Convert.FromHexString(
        "a1b86d87008b80400900003bf8752f807b580a7529a1b86d87000fb78090100000663d0007"
        + "7417663d02077411663d0307740b663d04077405e9ceb7f6ff668b442430660147408b4764e99cb7f6ff");

    private static bool RightBuild(ReadOnlySpan<byte> exe) => exe.Length == Dc2WpGatePatch.ExpectedLength;

    private static bool Matches(ReadOnlySpan<byte> exe, int offset, ReadOnlySpan<byte> expected)
        => offset + expected.Length <= exe.Length && exe.Slice(offset, expected.Length).SequenceEqual(expected);

    private static bool CaveIsFreeSlack(ReadOnlySpan<byte> exe, int offset)
    {
        if (offset + CaveLength > exe.Length) return false;
        foreach (var b in exe.Slice(offset, CaveLength))
            if (b != 0x00) return false;
        return true;
    }

    /// <summary>True iff <paramref name="exe"/> is the pristine rebirth build, ready to patch: correct
    /// length, the original hook bytes present, and the cave region free zero slack. (False once patched,
    /// or for any other file. Only inspects this patch's own two sites, so it stays true when the other
    /// Dino2.exe patches are already applied.)</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
        && Matches(exe, HookOffset, HookOriginal)
        && CaveIsFreeSlack(exe, CaveOffset);

    /// <summary>True iff this exe already carries the knockback-suppress lever, so the installer can skip
    /// it idempotently.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
        && Matches(exe, HookOffset, HookPatched)
        && Matches(exe, CaveOffset, Cave);

    /// <summary>Apply the hook + cave in place. Throws <see cref="InvalidOperationException"/> (leaving
    /// <paramref name="exe"/> untouched) unless <see cref="IsRecognizedPristine"/> — so an unknown build,
    /// an already-patched file, or a wrong-length buffer is never corrupted.</summary>
    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!IsRecognizedPristine(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized pristine rebirth build the Mosasaurus knockback-suppress "
                + "lever targets (wrong length, already patched, or a different version); refusing to patch.");
        HookPatched.CopyTo(exe.AsSpan(HookOffset));
        Cave.CopyTo(exe.AsSpan(CaveOffset));
    }

    /// <summary>Revert the lever's own two slices (hook → original, cave → zero slack), leaving every other
    /// Dino2.exe patch intact. No-op-safe: throws only on a wrong-length buffer.</summary>
    public static void Restore(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException("Dino2.exe wrong length; refusing to restore.");
        if (!IsApplied(exe)) return;
        HookOriginal.CopyTo(exe.AsSpan(HookOffset));
        exe.AsSpan(CaveOffset, CaveLength).Clear();
    }
}
