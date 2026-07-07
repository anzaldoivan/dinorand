namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch that makes a <b>randomizer-injected T-Rex</b>
/// (species E10, spawn TYPE <c>0x03</c>) killable, while leaving the game's own scripted T-Rex
/// setpieces untouched. Decision record + full RE: <c>docs/decisions/dc2/enemies/DC2-TREX-KILLABLE-LEVER-PLAN.md</c>
/// (and DC2-TREX-KILLABILITY-RE.md §9, LIVE-CONFIRMED).
///
/// <para><b>Mechanism.</b> The campaign T-Rex has finite HP but a phase-gated survival clamp
/// (<c>cur = max/2</c> floor + min-1) that makes it unkillable. The clamp is skipped when the actor
/// phase field <c>[actor+0x1C4]</c> is 2 (Extra Crisis). Phase is written only in the fightable-setup
/// routine <c>0x4238d0</c> from scene globals: 0 = campaign, 1 = the special room
/// (<c>byte[scene+0x1091]==9 &amp;&amp; byte[scene+0x1090]==3</c> ⇒ ST903 Edward City tank boss), 2 = EC.</para>
///
/// <para><b>The lever.</b> The hook lives in the <b>per-frame tick</b> <c>0x4235E0</c>, at its survival-
/// clamp gate <c>0x42360A</c> (over <c>mov eax,[esi+0x1c4]</c>). A 6-byte hook at <see cref="HookOffset"/>
/// (<c>jmp</c> + <c>nop</c>; a verified clean boundary that no branch enters mid-window) jumps to a
/// <see cref="CaveLength"/>-byte code cave in the zero slack at the end of <c>.text</c>
/// (<see cref="CaveOffset"/>). The cave — only for an E10 actor (<c>[esi+0x60]==0x00640000</c>) whose
/// <i>current</i> room (<c>word[scene+0x1090]</c>) is NOT a vanilla T-Rex boss room (<c>0x0200</c>=ST200
/// or <c>0x0903</c>=ST903) — writes <c>[esi+0x1C4]=2</c>, then re-runs the stolen phase read (so the
/// clamp gate sees phase 2 and skips) and jumps back. Vanilla T-Rex rooms are exactly {ST200, ST903},
/// so "E10 ∧ not-a-vanilla-boss-room" ≡ "randomizer-injected T-Rex room": those become killable, the
/// two scripted bosses are left exactly as vanilla. Phase 2 is the known-good EC path.</para>
///
/// <para><b>Why the tick, not the setup.</b> A CE live capture (2026-07-06) proved an op-1a-injected
/// T-Rex spawns straight into a fightable state (HP cur=10000 / max=20000 — the spawn-descriptor
/// defaults, NOT the setup routine's cur==max) and never executes the one-time fightable-setup
/// <c>0x4238d0</c>, so a hook there never ran and phase stayed 0. The tick <c>0x4235E0</c> runs for
/// every T-Rex in every state, so forcing phase there catches injected actors too.</para>
///
/// <para><b>Safety.</b> Dino2.exe is non-ASLR (fixed imagebase 0x400000, no <c>.reloc</c>), so the
/// cave's absolute <c>[0x876DB8]</c> load needs no relocation fix-up. The offsets are specific to the
/// pristine rebirth build (length <see cref="Dc2WpGatePatch.ExpectedLength"/>), so <see cref="Apply"/>
/// refuses anything else: exact length + the original hook bytes + the cave region being free zero
/// slack. All methods work on an in-memory <c>byte[]</c>; file I/O and pristine <c>.bak</c> backup are
/// the installer's concern (<see cref="Dc2RaptorPatch"/> contract). Restores only its own two slices
/// so it composes with the other Dino2.exe patches.</para>
/// </summary>
public static class Dc2TrexKillablePatch
{
    /// <summary>File offset of the hook site (VA <c>0x42360A</c>, <c>.text</c> raw = VA − 0x400000):
    /// the tick clamp gate <c>mov eax,[esi+0x1c4]</c> (6 bytes) the cave steals and re-runs.</summary>
    public const int HookOffset = 0x2360A;

    /// <summary>File offset of the code cave (VA <c>0x4E73F8</c>) — the zero slack between the end of
    /// <c>.text</c> code (VirtualSize) and the section's raw/aligned end; mapped, executable, unused.</summary>
    public const int CaveOffset = 0xE73F8;

    /// <summary>Length of the installed cave (and the free slack required when pristine).</summary>
    public const int CaveLength = 57;

    // ---- the two reversible edits ----
    private static readonly byte[] HookOriginal = { 0x8B, 0x86, 0xC4, 0x01, 0x00, 0x00 }; // mov eax,[esi+0x1c4]
    private static readonly byte[] HookPatched  = { 0xE9, 0xE9, 0x3D, 0x0C, 0x00, 0x90 }; // jmp 0x4E73F8 ; nop

    /// <summary>The 57-byte cave (VA 0x4E73F8): E10 guard <c>cmp dword[esi+0x60],0x640000; jne skip</c>;
    /// <c>mov edx,[0x876DB8]</c>; <c>movzx ecx, word[edx+0x1090]</c>; <c>cmp cx,0x0200; je skip</c>;
    /// <c>cmp cx,0x0903; je skip</c>; <c>mov dword[esi+0x1C4],2</c>; <c>skip: mov eax,[esi+0x1c4]</c>
    /// (stolen phase read); <c>jmp 0x423610</c> (the clamp-gate continuation).</summary>
    private static readonly byte[] CavePatched = Convert.FromHexString(
        "817e600000640075258b15b86d87000fb78a90100000"
        + "6681f900027411" + "6681f90309740a" + "c786c401000002000000"
        + "8b86c4010000" + "e9dfc1f3ff");

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

    /// <summary>True iff <paramref name="exe"/> is the pristine rebirth build, ready to patch:
    /// correct length, original hook bytes present, and the cave region is free zero slack. (False once
    /// patched, or for any other file. Only inspects this patch's own two sites, so it stays true when
    /// other Dino2.exe patches — raptor/shop/bgm — are already applied.)</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe)
        => RightBuild(exe) && Matches(exe, HookOffset, HookOriginal) && CaveIsFreeSlack(exe);

    /// <summary>True iff this exe already carries the killable-T-Rex lever, so the installer can skip it
    /// idempotently.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
        => RightBuild(exe) && Matches(exe, HookOffset, HookPatched) && Matches(exe, CaveOffset, CavePatched);

    /// <summary>Apply the hook + cave in place. Throws <see cref="InvalidOperationException"/> (leaving
    /// <paramref name="exe"/> untouched) unless <see cref="IsRecognizedPristine"/> — so an unknown
    /// build, an already-patched file, or a wrong-length buffer is never corrupted.</summary>
    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!IsRecognizedPristine(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized pristine rebirth build the killable-T-Rex lever targets "
                + "(wrong length, already patched, or a different version); refusing to patch.");
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
