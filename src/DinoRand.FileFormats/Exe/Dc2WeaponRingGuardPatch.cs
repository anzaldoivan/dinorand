namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch that zero-guards the weapon-select ring builder's
/// two unguarded divides, so opening the weapon menu can never <c>#DE</c> when the current
/// character has zero owned mains (or subs). Decision record:
/// <c>docs/reference/dc2/weapon/DC2-WEAPON-SYSTEM-INVESTIGATION.md</c> §3 + the add-and-equip plan.
///
/// <para><b>The fault.</b> Ring builder <c>0x496D70</c> lays weapons around a fixed-point circle:
/// MAIN branch <c>0x496E9E</c> and SUB branch <c>0x496EC3</c> each do
/// <c>mov eax,0x1000; mov cl,[esp+X]; cdq; idiv edi</c> where <c>edi</c> = the owned MAIN
/// (<c>byte[esi+0x5B]</c>) / SUB (<c>byte[esi+0x5C]</c>) count, then <c>and eax,0xFFF; imul eax,idx</c>.
/// With <c>edi==0</c> the <c>idiv</c> at <c>0x496EAC</c>/<c>0x496ED1</c> faults. This is the
/// starting-loadout menu crash class (a sole main that is a SUB / other-character main / fire-empty
/// weapon leaves the ring empty), and a pre-existing exposure for any non-playable character whose
/// restock template stocks no owned main.</para>
///
/// <para><b>The guard.</b> Because <c>0x1000 &amp; 0xFFF == 0</c>, simply <i>skipping</i> the divide
/// when <c>edi==0</c> leaves <c>eax=0x1000</c> → <c>and eax,0xFFF</c> → 0 → angle 0, the correct
/// "nothing to lay out" result. Each idiv site's 5-byte <c>mov eax,0x1000</c> (VA <c>0x496EA2</c> /
/// <c>0x496EC7</c>) is overwritten with a <c>jmp</c> to a cave that re-does the <c>mov eax</c> +
/// <c>mov cl</c>, then <c>test edi,edi; jz</c> over the <c>cdq; idiv</c>, and jumps back to the
/// instruction after the original idiv (<c>0x496EAE</c> / <c>0x496ED3</c>) — the stolen
/// <c>mov cl/cdq/idiv</c> bytes become dead, so no NOP padding is needed. Both idiv sites are entered
/// only by fall-through (no branch targets inside the stolen windows), so the hook is boundary-safe.</para>
///
/// <para><b>Safety.</b> Dino2.exe is non-ASLR (fixed imagebase 0x400000, no <c>.reloc</c>), so the
/// cave's absolute jumps need no relocation fix-up. The offsets are specific to the pristine rebirth
/// build (length <see cref="Dc2WpGatePatch.ExpectedLength"/>); <see cref="Apply"/> refuses anything
/// else. The cave sits in the <c>.text</c> zero slack right after the killable-T-Rex cave
/// (<see cref="Dc2TrexKillablePatch.CaveOffset"/> + its length), so the two compose. Works on an
/// in-memory <c>byte[]</c>; file I/O + pristine <c>.bak</c> are the installer's concern. Restores only
/// its own three slices so it composes with the other Dino2.exe patches.</para>
/// </summary>
public static class Dc2WeaponRingGuardPatch
{
    /// <summary>File offset of the MAIN-branch hook site (VA <c>0x496EA2</c>, <c>mov eax,0x1000</c>).</summary>
    public const int HookMainOffset = 0x96EA2;

    /// <summary>File offset of the SUB-branch hook site (VA <c>0x496EC7</c>, <c>mov eax,0x1000</c>).</summary>
    public const int HookSubOffset = 0x96EC7;

    /// <summary>File offset of the guard cave (VA <c>0x4E7440</c>) — <c>.text</c> zero slack just past
    /// the killable-T-Rex cave (<c>0xE73F8</c> + 57 = <c>0xE7431</c>).</summary>
    public const int CaveOffset = 0xE7440;

    /// <summary>Length of the installed cave (two 21-byte guard stubs).</summary>
    public const int CaveLength = 42;

    // Both idiv sites hold the same original bytes: mov eax,0x1000.
    private static readonly byte[] HookOriginal = { 0xB8, 0x00, 0x10, 0x00, 0x00 };
    // jmp 0x4E7440 (main) / jmp 0x4E7455 (sub) — capstone-verified rel32s.
    private static readonly byte[] HookMainPatched = { 0xE9, 0x99, 0x05, 0x05, 0x00 };
    private static readonly byte[] HookSubPatched  = { 0xE9, 0x89, 0x05, 0x05, 0x00 };

    /// <summary>The 42-byte cave (VA 0x4E7440). caveA: <c>mov eax,0x1000; mov cl,[esp+0x12];
    /// test edi,edi; jz +3; cdq; idiv edi; jmp 0x496EAE</c>. caveB (0x4E7455): same with
    /// <c>[esp+0x13]</c> and <c>jmp 0x496ED3</c>. Capstone-verified.</summary>
    private static readonly byte[] CavePatched = Convert.FromHexString(
        "b8001000008a4c241285ff740399f7ffe959fafaff"    // caveA -> jmp 0x496EAE
        + "b8001000008a4c241385ff740399f7ffe969fafaff"); // caveB -> jmp 0x496ED3

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
    /// length, both original hook bytes present, and the cave region is free zero slack. (False once
    /// patched or for any other file. Only inspects this patch's own sites, so it stays true when other
    /// Dino2.exe patches are already applied.)</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
           && Matches(exe, HookMainOffset, HookOriginal)
           && Matches(exe, HookSubOffset, HookOriginal)
           && CaveIsFreeSlack(exe);

    /// <summary>True iff this exe already carries the ring-guard lever, so the installer can skip it
    /// idempotently.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
           && Matches(exe, HookMainOffset, HookMainPatched)
           && Matches(exe, HookSubOffset, HookSubPatched)
           && Matches(exe, CaveOffset, CavePatched);

    /// <summary>Apply the two hooks + cave in place. Throws <see cref="InvalidOperationException"/>
    /// (leaving <paramref name="exe"/> untouched) unless <see cref="IsRecognizedPristine"/> —
    /// idempotent-safe: an already-applied file is a no-op.</summary>
    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (IsApplied(exe)) return;
        if (!IsRecognizedPristine(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized pristine rebirth build the weapon-ring-guard lever targets "
                + "(wrong length, already patched by a foreign edit, or a different version); refusing to patch.");
        HookMainPatched.CopyTo(exe.AsSpan(HookMainOffset));
        HookSubPatched.CopyTo(exe.AsSpan(HookSubOffset));
        CavePatched.CopyTo(exe.AsSpan(CaveOffset));
    }

    /// <summary>Revert the lever's own three slices (both hooks → original, cave → zero slack), leaving
    /// every other Dino2.exe patch intact. No-op-safe: throws only on a wrong-length buffer.</summary>
    public static void Restore(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException("Dino2.exe wrong length; refusing to restore.");
        if (!IsApplied(exe)) return;
        HookOriginal.CopyTo(exe.AsSpan(HookMainOffset));
        HookOriginal.CopyTo(exe.AsSpan(HookSubOffset));
        exe.AsSpan(CaveOffset, CaveLength).Clear();
    }
}
