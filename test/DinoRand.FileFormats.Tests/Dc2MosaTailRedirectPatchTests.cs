using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>Auto-trigger logic for the Mosasaurus missed-grab cancellation lever: it must turn on exactly when the
/// run can inject an E80 Mosasaurus into a land room (water-level swaps admit E80 to the weighted pool,
/// or a fixed pin selects it) or the user forces it — same predicate as the grab/knockback levers.
/// Harmless no-op when no injected Mosasaurus exists.</summary>
public class Dc2MosaTailRedirectInstallerWantedForTests
{
    private static RandomizerConfig Cfg(bool enemies, Dc2EnemyDistributionMode mode,
        int? fixedType = null, bool water = false, bool force = false) => new()
    {
        RandomizeEnemies = enemies,
        Dc2EnemyMode = mode,
        Dc2FixedSpeciesType = fixedType,
        Dc2AllowWaterLevelEnemySwaps = water,
        Dc2RedirectMosaTail = force,
    };

    [Fact] public void ManualFlag_ForcesOn_EvenWithEnemiesOff() =>
        Assert.True(Dc2MosaTailRedirectInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, force: true)));

    [Fact] public void EnemiesOff_NoInjectedMosa_False() =>
        Assert.False(Dc2MosaTailRedirectInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, water: true)));

    [Fact] public void Weighted_WaterSwapsOn_True() => // E80 enters the weighted pool
        Assert.True(Dc2MosaTailRedirectInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, water: true)));

    [Fact] public void Weighted_WaterSwapsOff_False() => // no aquatic donors admitted
        Assert.False(Dc2MosaTailRedirectInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, water: false)));

    [Fact] public void Fixed_MosaPin_True() =>
        Assert.True(Dc2MosaTailRedirectInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x0a)));

    [Fact] public void Fixed_OtherPin_False() => // T-Rex 0x03 is not a Mosasaurus
        Assert.False(Dc2MosaTailRedirectInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x03)));
}

/// <summary>
/// Unit tests for <see cref="Dc2MosaTailRedirectPatch"/> — the Dino2.exe hook + code cave that cancels the
/// user-labelled tail-looking <b>missed-grab continuation</b> of selector 6 in non-native rooms, while
/// preserving the four native aquatic rooms (ST700/702/703/704). RE + decision record:
/// docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md §10.6.
///
/// <para>The hook is in the selector-6 B-handler <c>0x440860</c>, not the nested selector hub and not a
/// selector-2 writer. It steals the six-byte ref-5 bind/effect head at VA <c>0x440942</c>. The cave reads the
/// current room: native rooms replay those six bytes and resume at <c>0x440948</c>; every other room enters
/// the original cleanup at <c>0x44097f</c>. No actor TYPE guard or <c>actor+0x55</c> selector rewrite is needed
/// or emitted.</para>
///
/// <para>The hook/cave bytes are independently transcribed here so a regression in the patcher's tables
/// fails the test rather than silently shipping a wrong offset into the game exe.</para>
/// </summary>
public class Dc2MosaTailRedirectPatchTests
{
    private const int CaveVa = 0x4E7600;
    private const int HookLen = 6;

    // VA 0x440942 / file 0x40942: mov al,[esi+0x56]; push ebx; push 0x40.
    private static readonly byte[] HookOriginal = Convert.FromHexString("8A4656536A40");
    // jmp 0x4E7600 + nop, exactly six bytes.
    private static readonly byte[] HookPatched = Convert.FromHexString("E9B96C0A0090");

    // Exact 49-byte runtime-validated cave at VA/file offset 0x4E7600/0xE7600.
    private static readonly byte[] Cave = Convert.FromHexString(
        "50a1b86d87000fb78090100000663d00077412663d02077206663d0407760658e95a93f5ff"
        + "588a4656536a40e91793f5ff");

    // Released v0.5.0-v0.5.2 candidate fingerprint. It is retained only so an exact old install can be
    // restored; it is not an active patch path.
    private const int LegacyHookOffset = 0x3FC80;
    private const int LegacyCaveLength = 66;
    private static readonly byte[] LegacyHookOriginal = Convert.FromHexString("568B742408");
    private static readonly byte[] LegacyHookPatched = Convert.FromHexString("E97B790A00");
    private static readonly byte[] LegacyCave = Convert.FromHexString(
        "568b742408807e580a7532a1b86d87000fb788901000006681f90007741f"
        + "6681f9020774186681f9030774116681f90407740a807e55027504c6465500e94386f5ff");

    private static int ShortTarget(int instructionOffset)
        => CaveVa + instructionOffset + 2 + (sbyte)Cave[instructionOffset + 1];

    private static int Rel32Target(int instructionOffset)
        => CaveVa + instructionOffset + 5 + BitConverter.ToInt32(Cave, instructionOffset + 1);

    internal static byte[] NewPristine()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        HookOriginal.CopyTo(exe.AsSpan(Dc2MosaTailRedirectPatch.HookOffset));
        LegacyHookOriginal.CopyTo(exe.AsSpan(LegacyHookOffset));
        return exe;
    }

    /// <summary>Regression pin for the RE: the hook is the selector-6 B-handler's ref-5 transition
    /// (VA 0x440942), stealing six bytes and entering the missed-grab cancellation cave.</summary>
    [Fact]
    public void Hook_TargetsSelector6MissedGrabContinuation()
    {
        Assert.Equal(0x40942, Dc2MosaTailRedirectPatch.HookOffset);
        Assert.Equal(0xE7600, Dc2MosaTailRedirectPatch.CaveOffset);
        Assert.Equal(49, Dc2MosaTailRedirectPatch.CaveLength);
        Assert.Equal(6, HookOriginal.Length);
        Assert.Equal(6, HookPatched.Length);
        var exe = NewPristine();
        Assert.Equal(HookOriginal, exe.Skip(Dc2MosaTailRedirectPatch.HookOffset).Take(HookLen).ToArray());
    }

    /// <summary>The cave is independently pinned to the exact runtime-validated 49 bytes.</summary>
    [Fact]
    public void Cave_IsExactlyTheRuntimeValidated49Bytes()
    {
        Assert.Equal(49, Cave.Length);
        Assert.Equal(
            "50A1B86D87000FB78090100000663D00077412663D02077206663D0407760658E95A93F5FF588A4656536A40E91793F5FF",
            Convert.ToHexString(Cave));
    }

    [Fact]
    public void Cave_NativeRoomsResumeOriginalRef5Continuation_OtherRoomsEnterCleanup()
    {
        // 0700 and the inclusive 0702..0704 range branch to the replay at cave+0x25; values below 0702
        // take the non-native path at cave+0x1f, and values above 0704 fall through to that same path.
        Assert.Equal(CaveVa + 0x25, ShortTarget(17));
        Assert.Equal(CaveVa + 0x1F, ShortTarget(23));
        Assert.Equal(CaveVa + 0x25, ShortTarget(29));
        Assert.Equal(0x44097F, Rel32Target(32));
        Assert.Equal(0x440948, Rel32Target(44));
    }

    [Fact]
    public void Cave_DoesNotRewriteActorTypeOrNestedSelector()
    {
        var hex = Convert.ToHexString(Cave);
        Assert.DoesNotContain("807E58", hex); // no TYPE guard: this hook is already inside E80's handler
        Assert.DoesNotContain("55", hex);     // no actor+0x55 selector read/write or selector remap
    }

    [Fact]
    public void Pristine_IsRecognized_AndNotApplied()
    {
        var exe = NewPristine();
        Assert.True(Dc2MosaTailRedirectPatch.IsRecognizedPristine(exe));
        Assert.False(Dc2MosaTailRedirectPatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_WritesExactlyTheHookAndCave()
    {
        var exe = NewPristine();
        Dc2MosaTailRedirectPatch.Apply(exe);

        Assert.Equal(HookPatched, exe.Skip(Dc2MosaTailRedirectPatch.HookOffset).Take(HookLen).ToArray());
        Assert.Equal(Cave, exe.Skip(Dc2MosaTailRedirectPatch.CaveOffset).Take(Dc2MosaTailRedirectPatch.CaveLength).ToArray());
        Assert.True(Dc2MosaTailRedirectPatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_TouchesNothingOutsideTheTwoWindows()
    {
        var before = NewPristine();
        var after = NewPristine();
        Dc2MosaTailRedirectPatch.Apply(after);

        for (int i = 0; i < after.Length; i++)
        {
            bool inHook = i >= Dc2MosaTailRedirectPatch.HookOffset && i < Dc2MosaTailRedirectPatch.HookOffset + HookLen;
            bool inCave = i >= Dc2MosaTailRedirectPatch.CaveOffset && i < Dc2MosaTailRedirectPatch.CaveOffset + Dc2MosaTailRedirectPatch.CaveLength;
            if (!inHook && !inCave)
                Assert.True(before[i] == after[i], $"byte at 0x{i:x} changed unexpectedly");
        }
    }

    [Fact]
    public void CaveRegion_DoesNotCollideWithSiblingCaves()
    {
        // Every DC2 exe cave lives in the same end-of-.text zero slack; they must not overlap so the levers
        // compose in any order.
        (int off, int len)[] regions =
        {
            (Dc2TrexKillablePatch.CaveOffset, Dc2TrexKillablePatch.CaveLength),
            (Dc2InostraSpawnGuardPatch.CaveOffset, Dc2InostraSpawnGuardPatch.CaveLength),
            (Dc2MosaGrabSuppressPatch.Cave19Offset, Dc2MosaGrabSuppressPatch.CaveLength),
            (Dc2MosaGrabSuppressPatch.Cave20Offset, Dc2MosaGrabSuppressPatch.CaveLength),
            (Dc2MosaKnockbackSuppressPatch.CaveOffset, Dc2MosaKnockbackSuppressPatch.CaveLength),
            (Dc2MosaTailRedirectPatch.CaveOffset, Dc2MosaTailRedirectPatch.CaveLength),
        };
        for (int a = 0; a < regions.Length; a++)
            for (int b = a + 1; b < regions.Length; b++)
            {
                bool overlap = regions[a].off < regions[b].off + regions[b].len
                            && regions[b].off < regions[a].off + regions[a].len;
                Assert.False(overlap, $"cave regions {a} and {b} overlap");
            }

        Assert.True(Dc2MosaKnockbackSuppressPatch.CaveOffset + Dc2MosaKnockbackSuppressPatch.CaveLength
            < Dc2MosaTailRedirectPatch.CaveOffset);
        Assert.True(Dc2MosaTailRedirectPatch.CaveOffset + Dc2MosaTailRedirectPatch.CaveLength
            < Dc2WpGatePatch.ExpectedLength);
    }

    [Fact]
    public void CaveSlackBoundary_IsOutsideTheOwnedWindow()
    {
        var exe = NewPristine();
        exe[Dc2MosaTailRedirectPatch.CaveOffset - 1] = 0xA5;
        exe[Dc2MosaTailRedirectPatch.CaveOffset + Dc2MosaTailRedirectPatch.CaveLength] = 0x5A;

        Dc2MosaTailRedirectPatch.Apply(exe);

        Assert.Equal(0xA5, exe[Dc2MosaTailRedirectPatch.CaveOffset - 1]);
        Assert.Equal(0x5A, exe[Dc2MosaTailRedirectPatch.CaveOffset + Dc2MosaTailRedirectPatch.CaveLength]);
    }

    [Fact]
    public void Restore_ReversesApplyExactly()
    {
        var pristine = NewPristine();
        var exe = NewPristine();
        Dc2MosaTailRedirectPatch.Apply(exe);
        Dc2MosaTailRedirectPatch.Restore(exe);
        Assert.Equal(pristine, exe);
        Assert.True(Dc2MosaTailRedirectPatch.IsRecognizedPristine(exe));
    }

    [Fact]
    public void Restore_OnUnpatchedExe_IsNoOp()
    {
        var exe = NewPristine();
        var snapshot = (byte[])exe.Clone();
        Dc2MosaTailRedirectPatch.Restore(exe);
        Assert.Equal(snapshot, exe);
    }

    [Fact]
    public void Apply_IsNotDoubleApplied()
    {
        var exe = NewPristine();
        Dc2MosaTailRedirectPatch.Apply(exe);
        Assert.True(Dc2MosaTailRedirectPatch.IsApplied(exe));
        Assert.False(Dc2MosaTailRedirectPatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaTailRedirectPatch.Apply(exe));
    }

    [Fact]
    public void Apply_RefusesWrongLength()
    {
        var tooShort = new byte[Dc2WpGatePatch.ExpectedLength - 1];
        Assert.False(Dc2MosaTailRedirectPatch.IsRecognizedPristine(tooShort));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaTailRedirectPatch.Apply(tooShort));
    }

    [Fact]
    public void Apply_RefusesForeignHook_AndLeavesBufferUntouched()
    {
        var foreign = NewPristine();
        foreign[Dc2MosaTailRedirectPatch.HookOffset] = 0x90; // corrupt one sentinel byte
        var snapshot = (byte[])foreign.Clone();

        Assert.False(Dc2MosaTailRedirectPatch.IsRecognizedPristine(foreign));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaTailRedirectPatch.Apply(foreign));
        Assert.Equal(snapshot, foreign);
    }

    [Fact]
    public void Apply_RefusesWhenCaveSlackIsOccupied()
    {
        var exe = NewPristine();
        exe[Dc2MosaTailRedirectPatch.CaveOffset + 4] = 0x12;
        Assert.False(Dc2MosaTailRedirectPatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaTailRedirectPatch.Apply(exe));
    }

    [Fact]
    public void Apply_RefusesPartiallyPatchedInput_AndLeavesBufferUntouched()
    {
        var exe = NewPristine();
        HookPatched.CopyTo(exe.AsSpan(Dc2MosaTailRedirectPatch.HookOffset));
        var snapshot = (byte[])exe.Clone();

        Assert.False(Dc2MosaTailRedirectPatch.IsApplied(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaTailRedirectPatch.Apply(exe));
        Assert.Equal(snapshot, exe);
    }

    [Fact]
    public void ReleasedLegacyCandidate_IsRestoredExactly_ButNeverApplied()
    {
        var pristine = NewPristine();
        var exe = (byte[])pristine.Clone();
        LegacyHookPatched.CopyTo(exe.AsSpan(LegacyHookOffset));
        LegacyCave.CopyTo(exe.AsSpan(Dc2MosaTailRedirectPatch.CaveOffset));

        Assert.True(Dc2MosaTailRedirectPatch.IsLegacyApplied(exe));
        Assert.False(Dc2MosaTailRedirectPatch.IsApplied(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaTailRedirectPatch.Apply(exe));

        Dc2MosaTailRedirectPatch.Restore(exe);
        Assert.Equal(pristine, exe);
        Assert.True(Dc2MosaTailRedirectPatch.IsRecognizedPristine(exe));

        var partial = (byte[])pristine.Clone();
        LegacyHookPatched.CopyTo(partial.AsSpan(LegacyHookOffset));
        LegacyCave.CopyTo(partial.AsSpan(Dc2MosaTailRedirectPatch.CaveOffset));
        partial[Dc2MosaTailRedirectPatch.CaveOffset + 10] ^= 0x01;
        var snapshot = (byte[])partial.Clone();
        Assert.False(Dc2MosaTailRedirectPatch.IsLegacyApplied(partial));
        Dc2MosaTailRedirectPatch.Restore(partial);
        Assert.Equal(snapshot, partial);
    }

    /// <summary>Real-data end-to-end pin, gated on <c>DINORAND_DC2_DIR</c>. Reads the pristine backup when
    /// available, applies only to the in-memory copy, and restores it byte-identically.</summary>
    [Fact]
    public void RealExe_Pristine_AppliesAndReverts()
    {
        var path = LocateDino2PristineImage();
        if (path is null) return;

        var bytes = File.ReadAllBytes(path);
        if (!Dc2MosaTailRedirectPatch.IsRecognizedPristine(bytes)) return; // different or legacy-patched → skip

        Assert.Equal(HookOriginal, bytes.Skip(Dc2MosaTailRedirectPatch.HookOffset).Take(HookLen).ToArray());

        var pristine = (byte[])bytes.Clone();
        Dc2MosaTailRedirectPatch.Apply(bytes);
        Assert.True(Dc2MosaTailRedirectPatch.IsApplied(bytes));
        Assert.Equal(Cave, bytes.Skip(Dc2MosaTailRedirectPatch.CaveOffset).Take(Dc2MosaTailRedirectPatch.CaveLength).ToArray());
        Dc2MosaTailRedirectPatch.Restore(bytes);
        Assert.Equal(pristine, bytes);
    }

    private static string? LocateDino2PristineImage()
    {
        var dir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        // DINORAND_DC2_DIR points at rebirth/Data; the exe is one level up (rebirth/Dino2.exe).
        foreach (var c in new[]
        {
            Path.Combine(dir, "rebirth", "Dino2.exe.bak"),
            Path.Combine(dir, "..", "Dino2.exe.bak"),
            Path.Combine(dir, "Dino2.exe.bak"),
            Path.Combine(dir, "rebirth", "Dino2.exe"),
            Path.Combine(dir, "..", "Dino2.exe"),
            Path.Combine(dir, "Dino2.exe"),
        })
            if (File.Exists(c)) return c;
        return null;
    }
}
