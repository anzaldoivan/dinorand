namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch that stops a <b>randomizer-injected Inostrancevia</b>
/// (species E50, spawn TYPE <c>0x0e</c>) from crashing the game when the PSX-recompiled emergence/burst
/// emitter is ticked in a room that never armed a spawn-descriptor list for it (witnessed live in ST902
/// Edward City; dumps <c>2026-07-10 22-35-04</c> and <c>2026-07-11 14-51-17</c>). RCA:
/// <c>docs/decisions/dc2/crash-rcas/DC2-INOSTRA-SPAWN-DESCRIPTOR-NULL-RCA.md</c> (and
/// KNOWLEDGE-AND-QUESTIONS.md K97).
///
/// <para><b>Mechanism.</b> The emitter is a small state machine whose global manager lives in
/// <c>.psxseg</c> (emulated PSX RAM, e.g. <c>0x5e7aa4</c>/<c>0x5e8004</c>). Its per-frame <b>tick driver</b>
/// <c>0x4131d0</c> loads the manager (<c>esi=[esp+8]</c>), dispatches the current state fn
/// (<c>call [byte[esi+3]*4 + 0x71e1b4]</c> — advance/process states, which only <i>advance</i> the
/// descriptor cursor, never <i>arm</i> it; the cursor is armed by the owning actor's own tick logic,
/// conditionally — e.g. <c>0x427fbd mov [mgr+0xc],0x5ed790</c>, skipped on a sibling branch), then reads
/// the cursor <c>ecx=[esi+0xc]</c> and checks the terminator <c>cmp dword[ecx],-1</c> at <c>0x4131e7</c>
/// with <b>no NULL check</b>. When the emitter is ticked while un-armed, <c>[esi+0xc]</c> is a committed-zero
/// <c>.psxseg</c> slot (NULL) and the cursor is dereferenced with no guard — both in the driver
/// (<c>0x4131e7</c>, dump <c>14-51-17</c>: read [NULL], <c>Ecx=0</c>) and in the process state
/// (<c>0x413260</c>, dump <c>22-35-04</c>: read [NULL+0xF]). Same "native-context asset absent elsewhere"
/// family as the Triceratops (K90/K95) and aquatic-residency (K69) crashes; the missing asset is the
/// descriptor list.</para>
///
/// <para><b>The lever.</b> A 5-byte hook at <see cref="HookOffset"/> (VA <c>0x4131d5</c>, right after the
/// driver loads <c>esi=manager</c> and before it dispatches — a clean boundary reached only by fall-through
/// from the entry, no branch lands mid-window) jumps to a <see cref="CaveLength"/>-byte code cave in the
/// zero slack at the end of <c>.text</c> (<see cref="CaveOffset"/>). The cave tests <c>[esi+0xc]</c>: if
/// NULL it pops the driver's saved <c>esi</c> and returns — <b>skipping the entire tick</b> (dispatch +
/// terminator check), so the un-armed emitter simply does nothing this frame (the semantically-correct
/// degenerate for an empty descriptor list); otherwise it re-runs the stolen
/// <c>push esi; movsx eax,byte[esi+3]</c> and jumps back to the dispatch (<c>0x4131da</c>). Guarding the
/// tick entry subsumes every downstream cursor deref (both <c>0x4131e7</c> and the process state
/// <c>0x413250</c>/<c>0x413260</c>), and is species-agnostic (it guards the shared emitter, not E50), so it
/// composes with every donor; injected Inostra is merely the witnessed trigger.</para>
///
/// <para><b>Safety.</b> Dino2.exe is non-ASLR (fixed imagebase 0x400000, no <c>.reloc</c>), so the cave's
/// hard-coded return address needs no relocation fix-up. The stack stays balanced: the bail path pops only
/// the driver's own entry <c>push esi</c> and never pushes the dispatch argument. The offsets are specific
/// to the pristine rebirth build (length <see cref="Dc2WpGatePatch.ExpectedLength"/>), and
/// <see cref="Apply"/> refuses anything else: exact length + the original hook bytes + the cave region being
/// free zero slack. All methods work on an in-memory <c>byte[]</c>; file I/O and pristine <c>.bak</c> backup
/// are the installer's concern. Restores only its own two slices so it composes with the other Dino2.exe
/// patches.</para>
/// </summary>
public static class Dc2InostraSpawnGuardPatch
{
    /// <summary>File offset of the hook site (VA <c>0x4131d5</c>, <c>.text</c> raw = VA − 0x400000): the
    /// driver's <c>push esi; movsx eax,byte[esi+3]</c> (the dispatch prologue) the cave steals and re-runs,
    /// with <c>esi</c> already = the emitter manager.</summary>
    public const int HookOffset = 0x131D5;

    /// <summary>File offset of the code cave (VA <c>0x4E74C0</c>) — the zero slack past the shipped
    /// killable-T-Rex (<c>0xE73F8</c>+57), weapon-ring-guard (<c>0xE7440</c>+42) and start-weapon-append
    /// (<c>0xE7470</c>+64 = <c>0xE74B0</c>) caves. Mapped, executable, unused.</summary>
    public const int CaveOffset = 0xE74C0;

    /// <summary>Length of the installed cave (and the free slack required when pristine).</summary>
    public const int CaveLength = 19;

    // ---- the two reversible edits ----
    // 0x4131d5: push esi (56) ; movsx eax,byte[esi+3] (0F BE 46 03).  The 5-byte jmp covers exactly these.
    private static readonly byte[] HookOriginal = { 0x56, 0x0F, 0xBE, 0x46, 0x03 };       // push esi ; movsx…
    private static readonly byte[] HookPatched  = { 0xE9, 0xE6, 0x42, 0x0D, 0x00 };       // jmp 0x4E74C0

    /// <summary>The 19-byte cave (VA 0x4E74C0): <c>mov eax,[esi+0xc]</c> (cursor); <c>test eax,eax;
    /// jz bail</c>; <c>push esi</c> + <c>movsx eax,byte[esi+3]</c> (stolen dispatch prologue);
    /// <c>jmp 0x4131da</c> (into the state dispatch); <c>bail: pop esi; ret</c> (undoes the driver's own
    /// entry <c>push esi</c> — whole tick skipped, nothing spawned).</summary>
    private static readonly byte[] CavePatched = Convert.FromHexString(
        "8b460c" + "85c0" + "740a" + "56" + "0fbe4603" + "e909bdf2ff" + "5e" + "c3");

    private static bool RightBuild(ReadOnlySpan<byte> exe) => exe.Length == Dc2WpGatePatch.ExpectedLength;

    private static bool Matches(ReadOnlySpan<byte> exe, int offset, ReadOnlySpan<byte> expected)
        => offset + expected.Length <= exe.Length && exe.Slice(offset, expected.Length).SequenceEqual(expected);

    private static bool CaveIsFreeSlack(ReadOnlySpan<byte> exe)
    {
        if (CaveOffset + CaveLength > exe.Length) return false;
        foreach (var b in exe.Slice(CaveOffset, CaveLength))
            if (b != 0x00) return false;
        return true;
    }

    /// <summary>True iff <paramref name="exe"/> is the pristine rebirth build, ready to patch: correct
    /// length, original hook bytes present, and the cave region is free zero slack. (False once patched,
    /// or for any other file. Only inspects this patch's own two sites, so it stays true when other
    /// Dino2.exe patches — raptor/trex/triceratops/shop/bgm — are already applied.)</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe)
        => RightBuild(exe) && Matches(exe, HookOffset, HookOriginal) && CaveIsFreeSlack(exe);

    /// <summary>True iff this exe already carries the spawn-guard lever, so the installer can skip it
    /// idempotently.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
        => RightBuild(exe) && Matches(exe, HookOffset, HookPatched) && Matches(exe, CaveOffset, CavePatched);

    /// <summary>Apply the hook + cave in place. Throws <see cref="InvalidOperationException"/> (leaving
    /// <paramref name="exe"/> untouched) unless <see cref="IsRecognizedPristine"/> — so an unknown build,
    /// an already-patched file, or a wrong-length buffer is never corrupted.</summary>
    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!IsRecognizedPristine(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized pristine rebirth build the Inostra spawn-guard lever "
                + "targets (wrong length, already patched, or a different version); refusing to patch.");
        HookPatched.CopyTo(exe.AsSpan(HookOffset));
        CavePatched.CopyTo(exe.AsSpan(CaveOffset));
    }

    /// <summary>Revert the lever's own two slices (hook → original, cave → zero slack), leaving every
    /// other Dino2.exe patch intact. No-op-safe: throws only on a wrong-length buffer.</summary>
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
