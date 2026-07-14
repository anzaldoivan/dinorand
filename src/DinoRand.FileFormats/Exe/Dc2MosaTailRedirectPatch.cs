namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch that redirects a <b>randomizer-injected E80 Mosasaurus</b>
/// (spawn TYPE <c>0x0a</c>) away from its <b>wide-turn tail strike</b> and onto its <b>narrow bite</b> in
/// non-native LAND rooms, so the mosa never performs the out-of-bounds move — while leaving the four native
/// aquatic rooms (ST700/702/703/704) byte-identical. Decision record + full RE:
/// <c>docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md</c> §9.
///
/// <para><b>Mechanism.</b> The E80 attack FSM is nested (K106, CE-confirmed 2026-07-13): the outer state
/// <c>byte[esi+0x54]</c> routes to the <b>state-1 hub</b> <c>0x43fc80</c>, which dispatches a second selector
/// <c>byte[esi+0x55]</c> — the <b>attack-pattern id</b> — through sub-tables <c>0x7211f8</c> (aim) and
/// <c>0x721218</c> (execute). The melee strikes are patterns 0/1/2/3 (their aim-handlers = states 5/6/7/8
/// call the executor <c>0x440160</c>); pattern <b>2</b> is the wide-turn strike (state 7 <c>0x43ff40</c>,
/// turn cap <c>0x3c</c>) — the tail sweep — and pattern <b>0</b> the narrow front strike (state 5) — the
/// bite.</para>
///
/// <para><b>The lever.</b> The hook steals the hub's 5-byte prologue <c>push esi; mov esi,[esp+8]</c>
/// (VA 0x43fc80) and jumps to a <see cref="CaveLength"/>-byte cave in the end-of-<c>.text</c> zero slack.
/// The cave replays the prologue (so <c>esi</c> = the actor), then — only for a TYPE-0x0a actor
/// (<c>byte[esi+0x58]==0x0a</c>) outside the four native rooms
/// (<c>word[[0x876DB8]+0x1090]</c> ∉ <c>{0x0700,0x0702,0x0703,0x0704}</c>) whose current pattern is 2 —
/// rewrites <c>byte[esi+0x55]:=0</c>, so the sub-dispatch runs the bite instead of the tail. Any other
/// actor, room, or pattern falls straight through to the unchanged hub. This changes <b>which behavior</b>
/// the mosa runs; it touches no player-movement code.</para>
///
/// <para><b>Why TYPE, not the model base.</b> Every aquatic species shares model base
/// <c>[actor+0x60]==0x640000</c>, so that is not a Mosasaurus discriminator; the spawn TYPE
/// <c>byte[actor+0x58]</c> is (<c>0x0a</c> ↔ E80 uniquely). Dino2.exe is non-ASLR (fixed imagebase
/// 0x400000, no <c>.reloc</c>), so the cave's absolute <c>[0x876DB8]</c> load needs no fix-up. The cave sits
/// below the T-Rex / Inostra / grab / knockback caves in the same slack, chosen not to overlap, so all
/// levers compose.</para>
///
/// <para><b>Safety.</b> Offsets are specific to the pristine rebirth build
/// (<see cref="Dc2WpGatePatch.ExpectedLength"/>), so <see cref="Apply"/> refuses anything else: exact
/// length + the original hook prologue + the cave region being free zero slack. In-memory <c>byte[]</c>
/// only; file I/O and pristine <c>.bak</c> are the installer's concern. Restores only its own two slices so
/// it composes with the other Dino2.exe patches.</para>
///
/// <para><b>Runtime caveat (NOT a unit-test claim).</b> Which pattern is the tail is a best-guess
/// (pattern 2 = the wide-turn strike); the OOB fling was not reproduced live in the CE session (§9.1). The
/// in-game verify must confirm the redirect actually keeps the player in-bounds in ST105 — and that a
/// two-mosa enemy-enemy separation isn't the real cause.</para>
/// </summary>
public static class Dc2MosaTailRedirectPatch
{
    /// <summary>File offset of the E80 state-1 attack-pattern hub prologue (VA <c>0x43FC80</c>): steals
    /// <c>push esi; mov esi,[esp+8]</c> (5 bytes) so the cave can gate the pattern selector
    /// <c>byte[esi+0x55]</c> before the sub-dispatch.</summary>
    public const int HookOffset = 0x3FC80;

    /// <summary>File offset of the cave (VA <c>0x4E7600</c>) — end-of-<c>.text</c> zero slack, below the
    /// T-Rex / Inostra / grab / knockback caves so the levers compose.</summary>
    public const int CaveOffset = 0xE7600;

    /// <summary>Length of the installed cave (and the free slack required when pristine).</summary>
    public const int CaveLength = 66;

    private const int HookLen = 5;

    // ---- the two reversible edits ----
    // Hook steals the hub prologue: push esi (56) ; mov esi,[esp+8] (8B 74 24 08).
    private static readonly byte[] HookOriginal = { 0x56, 0x8B, 0x74, 0x24, 0x08 };
    private static readonly byte[] HookPatched = { 0xE9, 0x7B, 0x79, 0x0A, 0x00 }; // jmp 0x4E7600

    // 66-byte cave (capstone-verified). Shape: replay stolen (push esi; mov esi,[esp+8]); guard
    // cmp byte[esi+0x58],0x0a jne done; mov eax,[0x876DB8]; movzx ecx,word[eax+0x1090];
    // cmp cx,{0700,0702,0703,0704} je done; cmp byte[esi+0x55],2 jne done; mov byte[esi+0x55],0;
    // done: jmp 0x43fc85 (resume the hub).
    private static readonly byte[] Cave = Convert.FromHexString(
        "568b742408807e580a7532a1b86d87000fb788901000006681f90007741f"
        + "6681f9020774186681f9030774116681f90407740a807e55027504c6465500e94386f5ff");

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
    /// length, the original hook prologue present, and the cave region free zero slack. (False once patched,
    /// or for any other file. Only inspects this patch's own two sites, so it stays true when the other
    /// Dino2.exe patches are already applied.)</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
        && Matches(exe, HookOffset, HookOriginal)
        && CaveIsFreeSlack(exe, CaveOffset);

    /// <summary>True iff this exe already carries the tail-redirect lever, so the installer can skip it
    /// idempotently.</summary>
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
                "Dino2.exe is not the recognized pristine rebirth build the Mosasaurus tail-redirect lever "
                + "targets (wrong length, already patched, or a different version); refusing to patch.");
        HookPatched.CopyTo(exe.AsSpan(HookOffset));
        Cave.CopyTo(exe.AsSpan(CaveOffset));
    }

    /// <summary>Revert the lever's own two slices (hook → original prologue, cave → zero slack), leaving
    /// every other Dino2.exe patch intact. No-op-safe: throws only on a wrong-length buffer.</summary>
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
