using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for <see cref="Dc2WeaponRingGuardPatch"/> — the Dino2.exe hook + code cave that
/// zero-guards the weapon-ring builder's two <c>idiv</c> sites (<c>0x496EAC</c>/<c>0x496ED1</c>) so
/// opening the weapon menu with zero owned mains/subs can no longer <c>#DE</c>. RE + decision record:
/// docs/dc2/DC2-WEAPON-SYSTEM-INVESTIGATION.md §3.
///
/// <para>The hook/cave bytes are independently transcribed here (capstone-verified) so a regression in
/// the patcher's tables fails the test rather than silently shipping a broken jump.</para>
/// </summary>
public class Dc2WeaponRingGuardPatchTests
{
    // Both idiv sites steal the same original `mov eax,0x1000`.
    private static readonly byte[] HookOriginal   = { 0xB8, 0x00, 0x10, 0x00, 0x00 };
    private static readonly byte[] HookMainPatched = { 0xE9, 0x99, 0x05, 0x05, 0x00 }; // jmp 0x4E7440
    private static readonly byte[] HookSubPatched  = { 0xE9, 0x89, 0x05, 0x05, 0x00 }; // jmp 0x4E7455

    // 42-byte cave (VA 0x4E7440), two 21-byte guard stubs. Each: mov eax,0x1000; mov cl,[esp+X];
    // test edi,edi; jz +3; cdq; idiv edi; jmp back. The guard signature `85 FF 74 03` must be present.
    private static readonly byte[] CavePatched = Convert.FromHexString(
        "b8001000008a4c241285ff740399f7ffe959fafaff"
        + "b8001000008a4c241385ff740399f7ffe969fafaff");

    private const int HookLen = 5;

    private static byte[] NewPristine()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        HookOriginal.CopyTo(exe.AsSpan(Dc2WeaponRingGuardPatch.HookMainOffset));
        HookOriginal.CopyTo(exe.AsSpan(Dc2WeaponRingGuardPatch.HookSubOffset));
        return exe;
    }

    [Fact]
    public void Offsets_AreTheDecodedIdivSites()
    {
        Assert.Equal(0x96EA2, Dc2WeaponRingGuardPatch.HookMainOffset);
        Assert.Equal(0x96EC7, Dc2WeaponRingGuardPatch.HookSubOffset);
        Assert.Equal(0xE7440, Dc2WeaponRingGuardPatch.CaveOffset);
        Assert.Equal(42, Dc2WeaponRingGuardPatch.CaveLength);
        // The cave must NOT overlap the killable-T-Rex cave (0xE73F8 + 57 = 0xE7431).
        Assert.True(Dc2WeaponRingGuardPatch.CaveOffset >= Dc2TrexKillablePatch.CaveOffset + Dc2TrexKillablePatch.CaveLength);
    }

    [Fact]
    public void Pristine_IsRecognized_AndNotApplied()
    {
        var exe = NewPristine();
        Assert.True(Dc2WeaponRingGuardPatch.IsRecognizedPristine(exe));
        Assert.False(Dc2WeaponRingGuardPatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_WritesBothHooksAndTheCave_WithGuardBytes()
    {
        var exe = NewPristine();
        Dc2WeaponRingGuardPatch.Apply(exe);

        Assert.Equal(HookMainPatched, exe.Skip(Dc2WeaponRingGuardPatch.HookMainOffset).Take(HookLen).ToArray());
        Assert.Equal(HookSubPatched, exe.Skip(Dc2WeaponRingGuardPatch.HookSubOffset).Take(HookLen).ToArray());
        Assert.Equal(CavePatched, exe.Skip(Dc2WeaponRingGuardPatch.CaveOffset).Take(Dc2WeaponRingGuardPatch.CaveLength).ToArray());
        Assert.True(Dc2WeaponRingGuardPatch.IsApplied(exe));

        // The zero-guard `test edi,edi; jz +3` (85 FF 74 03) must appear in each 21-byte stub.
        var cave = exe.Skip(Dc2WeaponRingGuardPatch.CaveOffset).Take(42).ToArray();
        var guard = new byte[] { 0x85, 0xFF, 0x74, 0x03 };
        Assert.Contains(guard, Windows(cave.Take(21).ToArray(), 4));
        Assert.Contains(guard, Windows(cave.Skip(21).ToArray(), 4));
    }

    [Fact]
    public void Apply_TouchesNothingOutsideItsThreeWindows()
    {
        var before = NewPristine();
        var after = NewPristine();
        Dc2WeaponRingGuardPatch.Apply(after);

        for (int i = 0; i < after.Length; i++)
        {
            bool inMain = i >= Dc2WeaponRingGuardPatch.HookMainOffset && i < Dc2WeaponRingGuardPatch.HookMainOffset + HookLen;
            bool inSub = i >= Dc2WeaponRingGuardPatch.HookSubOffset && i < Dc2WeaponRingGuardPatch.HookSubOffset + HookLen;
            bool inCave = i >= Dc2WeaponRingGuardPatch.CaveOffset && i < Dc2WeaponRingGuardPatch.CaveOffset + Dc2WeaponRingGuardPatch.CaveLength;
            if (!inMain && !inSub && !inCave)
                Assert.True(before[i] == after[i], $"byte at 0x{i:x} changed unexpectedly");
        }
    }

    [Fact]
    public void Restore_ReversesApplyExactly()
    {
        var pristine = NewPristine();
        var exe = NewPristine();
        Dc2WeaponRingGuardPatch.Apply(exe);
        Dc2WeaponRingGuardPatch.Restore(exe);
        Assert.Equal(pristine, exe);
        Assert.True(Dc2WeaponRingGuardPatch.IsRecognizedPristine(exe));
    }

    [Fact]
    public void Restore_OnUnpatchedExe_IsNoOp()
    {
        var exe = NewPristine();
        var snapshot = (byte[])exe.Clone();
        Dc2WeaponRingGuardPatch.Restore(exe);
        Assert.Equal(snapshot, exe);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var exe = NewPristine();
        Dc2WeaponRingGuardPatch.Apply(exe);
        var once = (byte[])exe.Clone();
        Dc2WeaponRingGuardPatch.Apply(exe); // second apply is a no-op, not a throw
        Assert.Equal(once, exe);
        Assert.True(Dc2WeaponRingGuardPatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_RefusesWrongLength()
    {
        var tooShort = new byte[Dc2WpGatePatch.ExpectedLength - 1];
        Assert.False(Dc2WeaponRingGuardPatch.IsRecognizedPristine(tooShort));
        Assert.Throws<InvalidOperationException>(() => Dc2WeaponRingGuardPatch.Apply(tooShort));
    }

    [Fact]
    public void Apply_RefusesForeignHook_AndLeavesBufferUntouched()
    {
        var foreign = NewPristine();
        foreign[Dc2WeaponRingGuardPatch.HookSubOffset] = 0x90; // corrupt one sentinel byte
        var snapshot = (byte[])foreign.Clone();

        Assert.False(Dc2WeaponRingGuardPatch.IsRecognizedPristine(foreign));
        Assert.Throws<InvalidOperationException>(() => Dc2WeaponRingGuardPatch.Apply(foreign));
        Assert.Equal(snapshot, foreign);
    }

    [Fact]
    public void Apply_RefusesWhenCaveSlackIsOccupied()
    {
        var exe = NewPristine();
        exe[Dc2WeaponRingGuardPatch.CaveOffset + 4] = 0x12;
        Assert.False(Dc2WeaponRingGuardPatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2WeaponRingGuardPatch.Apply(exe));
    }

    /// <summary>Composes with the killable-T-Rex lever: both caves coexist (no overlap), both stay
    /// individually recognizable.</summary>
    [Fact]
    public void ComposesWith_TrexKillablePatch()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        HookOriginal.CopyTo(exe.AsSpan(Dc2WeaponRingGuardPatch.HookMainOffset));
        HookOriginal.CopyTo(exe.AsSpan(Dc2WeaponRingGuardPatch.HookSubOffset));
        new byte[] { 0x8B, 0x86, 0xC4, 0x01, 0x00, 0x00 }.CopyTo(exe.AsSpan(Dc2TrexKillablePatch.HookOffset));

        Dc2TrexKillablePatch.Apply(exe);
        Dc2WeaponRingGuardPatch.Apply(exe);

        Assert.True(Dc2TrexKillablePatch.IsApplied(exe));
        Assert.True(Dc2WeaponRingGuardPatch.IsApplied(exe));

        // Reverting the ring guard leaves the T-Rex lever intact.
        Dc2WeaponRingGuardPatch.Restore(exe);
        Assert.True(Dc2TrexKillablePatch.IsApplied(exe));
        Assert.False(Dc2WeaponRingGuardPatch.IsApplied(exe));
    }

    /// <summary>Real-data pin, gated on <c>DINORAND_DC2_DIR</c> (install root with
    /// <c>rebirth\Dino2.exe</c>): the pristine shipping exe carries <c>mov eax,0x1000</c> at both idiv
    /// sites, the cave slack is free, and apply→restore round-trips byte-identically. Skipped when the
    /// env var is unset or the exe is already patched/different.</summary>
    [Fact]
    public void RealExe_Pristine_AppliesAndReverts()
    {
        var dir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var path = Path.Combine(dir, "rebirth", "Dino2.exe");
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        if (!Dc2WeaponRingGuardPatch.IsRecognizedPristine(bytes)) return;

        Assert.Equal(HookOriginal, bytes.Skip(Dc2WeaponRingGuardPatch.HookMainOffset).Take(HookLen).ToArray());
        Assert.Equal(HookOriginal, bytes.Skip(Dc2WeaponRingGuardPatch.HookSubOffset).Take(HookLen).ToArray());

        var pristine = (byte[])bytes.Clone();
        Dc2WeaponRingGuardPatch.Apply(bytes);
        Assert.True(Dc2WeaponRingGuardPatch.IsApplied(bytes));
        Assert.Equal(CavePatched, bytes.Skip(Dc2WeaponRingGuardPatch.CaveOffset).Take(42).ToArray());
        Dc2WeaponRingGuardPatch.Restore(bytes);
        Assert.Equal(pristine, bytes);
    }

    private static System.Collections.Generic.IEnumerable<byte[]> Windows(byte[] src, int size)
    {
        for (int i = 0; i + size <= src.Length; i++)
            yield return src.Skip(i).Take(size).ToArray();
    }
}
