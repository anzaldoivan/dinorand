namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch that keeps a <b>randomizer-injected E80 Mosasaurus</b>
/// (spawn TYPE <c>0x0a</c>) grab IN-BOUNDS when it fires in a LAND room, while leaving the four native
/// aquatic rooms (ST700/702/703/704) byte-identical. Decision record + full RE:
/// <c>docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md</c> (Alternative e).
///
/// <para><b>Mechanism.</b> The Mosasaurus grab (per-frame behavior states 19 <c>0x440860</c> / 20
/// <c>0x440d60</c>, dispatched from the tick <c>0x43f800</c> via table <c>0x7211e4</c>) copies the mosa's
/// own X/Y/Z+rotation onto the player each frame (<c>[edi+0x40/0x42/0x44/0x4a]</c>, <c>edi</c> = player).
/// In a land room this drags the player out of bounds — vertically (the mosa's animated surfacing height
/// via the Y write) <i>and</i> horizontally (a lunge/sweep dragging the player through a wall via the X/Z
/// writes). The Allosaurus grab, by contrast, never copies enemy position onto the player and stays
/// in-bounds — §3e of the plan.</para>
///
/// <para><b>The lever.</b> Each grab state's player-position copy begins at
/// <c>mov word[edi+0x40],cx; mov dx,[esi+0x42]</c> (VA 0x4408A7 / 0x440DB1). Each is hooked (<c>jmp</c> +
/// 3 <c>nop</c> over the 8-byte pair) to a <see cref="CaveLength"/>-byte cave in the end-of-<c>.text</c>
/// zero slack. For a non-E80 actor (<c>byte[esi+0x58]!=0x0a</c>) or a native Mosasaurus room
/// (<c>word[[0x876DB8]+0x1090]</c> ∈ <c>{0x0700,0x0702,0x0703,0x0704}</c>) the cave replays the copy
/// verbatim (reload <c>cx</c>, then the stolen X write + the mosa-Y read, falling back into the in-place
/// Y/Z/rot writes); otherwise — an injected TYPE-0x0a mosa outside those rooms — it <b>skips the whole
/// player-position copy</b> (X, Y, Z, and rotation) and jumps past it, so the mosa still plays its grab
/// animation but the player is never moved. The mosa's own self-positioning (before the copy) is
/// untouched.</para>
///
/// <para><b>Why TYPE, not the model base.</b> Every aquatic species shares model base
/// <c>[actor+0x60]==0x640000</c>, so that is not a Mosasaurus discriminator; the spawn TYPE
/// <c>byte[actor+0x58]</c> is (<c>0x0a</c> ↔ E80 uniquely). Dino2.exe is non-ASLR (fixed imagebase
/// 0x400000, no <c>.reloc</c>), so the cave's absolute <c>[0x876DB8]</c> load needs no fix-up. The two
/// caves live below the killable-T-Rex cave (<see cref="Dc2TrexKillablePatch.CaveOffset"/>) and the
/// Inostrancevia guard cave in the same slack, chosen not to overlap, so all levers compose.</para>
///
/// <para><b>Safety.</b> Offsets are specific to the pristine rebirth build
/// (<see cref="Dc2WpGatePatch.ExpectedLength"/>), so <see cref="Apply"/> refuses anything else: exact
/// length + both original hook byte-pairs + both cave regions being free zero slack. In-memory
/// <c>byte[]</c> only; file I/O and pristine <c>.bak</c> are the installer's concern. Restores only its own
/// four slices so it composes with the other Dino2.exe patches.</para>
/// </summary>
public static class Dc2MosaGrabSuppressPatch
{
    /// <summary>File offset of grab-state-19's player-position copy head (VA <c>0x4408A7</c>): steals
    /// <c>mov word[edi+0x40],cx; mov dx,[esi+0x42]</c> (8 bytes) so the cave can gate the whole XYZ+rot copy.</summary>
    public const int Hook19Offset = 0x408A7;

    /// <summary>File offset of grab-state-20's player-position copy head (VA <c>0x440DB1</c>).</summary>
    public const int Hook20Offset = 0x40DB1;

    /// <summary>File offset of the state-19 cave (VA <c>0x4E7500</c>) — end-of-<c>.text</c> zero slack,
    /// below the T-Rex/Inostra caves so the levers compose.</summary>
    public const int Cave19Offset = 0xE7500;

    /// <summary>File offset of the state-20 cave (VA <c>0x4E7550</c>).</summary>
    public const int Cave20Offset = 0xE7550;

    /// <summary>Length of each installed cave (and the free slack required per cave when pristine).</summary>
    public const int CaveLength = 68;

    private const int HookLen = 8;

    // ---- the four reversible edits ----
    // Both hooks steal the same pair: mov word[edi+0x40],cx (66 89 4f 40) ; mov dx,[esi+0x42] (66 8b 56 42).
    private static readonly byte[] HookOriginal = { 0x66, 0x89, 0x4F, 0x40, 0x66, 0x8B, 0x56, 0x42 };
    private static readonly byte[] Hook19Patched = { 0xE9, 0x54, 0x6C, 0x0A, 0x00, 0x90, 0x90, 0x90 }; // jmp 0x4E7500 ; nop×3
    private static readonly byte[] Hook20Patched = { 0xE9, 0x9A, 0x67, 0x0A, 0x00, 0x90, 0x90, 0x90 }; // jmp 0x4E7550 ; nop×3

    // 68-byte caves (capstone-verified). Shape: cmp byte[esi+0x58],0x0a; jne van; mov eax,[0x876DB8];
    // movzx ecx,word[eax+0x1090]; cmp cx,{0700,0702,0703,0704} je van; jmp <after whole copy> (SUPPRESS
    // X/Y/Z/rot); van: mov cx,[esi+0x40] (reload; guard clobbered ecx); mov word[edi+0x40],cx (stolen X);
    // mov dx,[esi+0x42] (stolen); jmp <resume in-place Y/Z/rot>. Only the two final rel32s differ.
    private static readonly byte[] Cave19 = Convert.FromHexString(
        "807e580a752da1b86d87000fb788901000006681f90007741a6681f902077413"
        + "6681f90307740c6681f904077405e99093f5ff668b4e4066894f40668b5642e96b93f5ff");
    private static readonly byte[] Cave20 = Convert.FromHexString(
        "807e580a752da1b86d87000fb788901000006681f90007741a6681f902077413"
        + "6681f90307740c6681f904077405e94a98f5ff668b4e4066894f40668b5642e92598f5ff");

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
    /// length, both original hook pairs present, and both cave regions free zero slack. (False once
    /// patched, or for any other file. Only inspects this patch's own four sites, so it stays true when
    /// the other Dino2.exe patches — raptor/shop/bgm/T-Rex/Inostra — are already applied.)</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
        && Matches(exe, Hook19Offset, HookOriginal) && Matches(exe, Hook20Offset, HookOriginal)
        && CaveIsFreeSlack(exe, Cave19Offset) && CaveIsFreeSlack(exe, Cave20Offset);

    /// <summary>True iff this exe already carries the grab-suppress lever, so the installer can skip it
    /// idempotently.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
        => RightBuild(exe)
        && Matches(exe, Hook19Offset, Hook19Patched) && Matches(exe, Hook20Offset, Hook20Patched)
        && Matches(exe, Cave19Offset, Cave19) && Matches(exe, Cave20Offset, Cave20);

    /// <summary>Apply both hooks + caves in place. Throws <see cref="InvalidOperationException"/> (leaving
    /// <paramref name="exe"/> untouched) unless <see cref="IsRecognizedPristine"/> — so an unknown build,
    /// an already-patched file, or a wrong-length buffer is never corrupted.</summary>
    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!IsRecognizedPristine(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized pristine rebirth build the Mosasaurus grab-suppress lever "
                + "targets (wrong length, already patched, or a different version); refusing to patch.");
        Hook19Patched.CopyTo(exe.AsSpan(Hook19Offset));
        Hook20Patched.CopyTo(exe.AsSpan(Hook20Offset));
        Cave19.CopyTo(exe.AsSpan(Cave19Offset));
        Cave20.CopyTo(exe.AsSpan(Cave20Offset));
    }

    /// <summary>Revert the lever's own four slices (both hooks → original, both caves → zero slack),
    /// leaving every other Dino2.exe patch intact. No-op-safe: throws only on a wrong-length buffer.</summary>
    public static void Restore(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException("Dino2.exe wrong length; refusing to restore.");
        if (!IsApplied(exe)) return;
        HookOriginal.CopyTo(exe.AsSpan(Hook19Offset));
        HookOriginal.CopyTo(exe.AsSpan(Hook20Offset));
        exe.AsSpan(Cave19Offset, CaveLength).Clear();
        exe.AsSpan(Cave20Offset, CaveLength).Clear();
    }
}
