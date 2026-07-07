using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>Auto-trigger logic for the killable-T-Rex lever: it must turn on exactly when the run can
/// spawn a T-Rex (boss enemies / fixed-T-Rex pin) or the user forces it, so the frontend applies it
/// without a separate toggle.</summary>
public class Dc2TrexKillableInstallerWantedForTests
{
    private static RandomizerConfig Cfg(bool enemies, Dc2EnemyDistributionMode mode,
        int? fixedType = null, bool boss = false, bool force = false) => new()
    {
        RandomizeEnemies = enemies,
        Dc2EnemyMode = mode,
        Dc2FixedSpeciesType = fixedType,
        IncludeDc2BossEnemies = boss,
        Dc2MakeTrexKillable = force,
    };

    [Fact] public void ManualFlag_ForcesOn_EvenWithEnemiesOff() =>
        Assert.True(Dc2TrexKillableInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, force: true)));

    [Fact] public void EnemiesOff_NoInjectedTrex_False() =>
        Assert.False(Dc2TrexKillableInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, boss: true)));

    [Fact] public void Weighted_BossIncluded_True() =>
        Assert.True(Dc2TrexKillableInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, boss: true)));

    [Fact] public void Weighted_BossExcluded_False() =>
        Assert.False(Dc2TrexKillableInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, boss: false)));

    [Fact] public void Fixed_TrexPin_True() =>
        Assert.True(Dc2TrexKillableInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x03)));

    [Fact] public void Fixed_OtherBossPin_False() => // Gigano 0x06 is a boss but not a T-Rex
        Assert.False(Dc2TrexKillableInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x06, boss: true)));
}

/// <summary>
/// Unit tests for <see cref="Dc2TrexKillablePatch"/> — the Dino2.exe hook + code cave that makes a
/// randomizer-injected T-Rex killable outside the two vanilla boss rooms (ST200/ST903). RE +
/// decision record: docs/dc2/DC2-TREX-KILLABLE-LEVER-PLAN.md.
///
/// <para><b>Live-oracle fix (2026-07-06):</b> the first cut hooked the one-time fightable-setup
/// routine <c>0x4238d0</c>, but a CE capture proved an op-1a-injected T-Rex spawns straight into a
/// fightable state (HP cur=10000 / max=20000, i.e. NOT the setup's cur==max) and never runs
/// <c>0x4238d0</c> — so phase stayed 0 and the clamp held it alive. The hook therefore lives in the
/// <b>per-frame tick</b> <c>0x4235E0</c>, at its clamp gate <c>0x42360A</c> (VA), which runs for every
/// T-Rex in every state. The regression is pinned by <see cref="Hook_TargetsPerFrameTick_NotSetup"/>.</para>
///
/// <para>The patcher must recognize <b>only</b> the exact pristine rebirth build so version-specific
/// offsets can never corrupt a different build. The hook/cave bytes are independently transcribed here
/// so a regression in the patcher's tables fails the test rather than silently shipping.</para>
/// </summary>
public class Dc2TrexKillablePatchTests
{
    // Hooked instruction = the tick clamp gate `mov eax,[esi+0x1c4]` at VA 0x42360A (foff 0x2360A).
    private static readonly byte[] HookOriginal = { 0x8B, 0x86, 0xC4, 0x01, 0x00, 0x00 };
    private static readonly byte[] HookPatched  = { 0xE9, 0xE9, 0x3D, 0x0C, 0x00, 0x90 }; // jmp 0x4E73F8 ; nop

    // 57-byte cave, verified via capstone at VA 0x4E73F8 (all three guards resolve to the stolen
    // re-run, which reloads phase into eax then jmps back to the clamp gate continuation 0x423610):
    //   cmp dword[esi+0x60],0x640000; jne skip; mov edx,[0x876DB8]; movzx ecx,word[edx+0x1090];
    //   cmp cx,0x0200; je skip; cmp cx,0x0903; je skip; mov dword[esi+0x1C4],2;
    //   skip: mov eax,[esi+0x1C4]; jmp 0x423610
    private static readonly byte[] CavePatched = Convert.FromHexString(
        "817e600000640075258b15b86d87000fb78a90100000"
        + "6681f900027411" + "6681f90309740a" + "c786c401000002000000"
        + "8b86c4010000" + "e9dfc1f3ff");

    private const int HookLen = 6;

    /// <summary>A synthetic buffer that looks exactly like the pristine rebirth Dino2.exe to the
    /// recognizer: correct length, original hook bytes at their offset, cave region zeroed.</summary>
    private static byte[] NewPristine()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        HookOriginal.CopyTo(exe.AsSpan(Dc2TrexKillablePatch.HookOffset));
        return exe;
    }

    /// <summary>Regression pin for the live-oracle fix: the hook must be the per-frame tick clamp gate
    /// (VA 0x42360A / foff 0x2360A), stealing <c>mov eax,[esi+0x1c4]</c> — NOT the one-time setup site
    /// 0x423916, which injected T-Rex never execute.</summary>
    [Fact]
    public void Hook_TargetsPerFrameTick_NotSetup()
    {
        Assert.Equal(0x2360A, Dc2TrexKillablePatch.HookOffset);
        var exe = NewPristine();
        Assert.Equal(HookOriginal, exe.Skip(Dc2TrexKillablePatch.HookOffset).Take(HookLen).ToArray());
        Assert.Equal(57, Dc2TrexKillablePatch.CaveLength);
    }

    [Fact]
    public void Pristine_IsRecognized_AndNotApplied()
    {
        var exe = NewPristine();
        Assert.True(Dc2TrexKillablePatch.IsRecognizedPristine(exe));
        Assert.False(Dc2TrexKillablePatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_WritesExactlyTheHookAndCave()
    {
        var exe = NewPristine();
        Dc2TrexKillablePatch.Apply(exe);

        Assert.Equal(HookPatched, exe.Skip(Dc2TrexKillablePatch.HookOffset).Take(HookLen).ToArray());
        Assert.Equal(CavePatched, exe.Skip(Dc2TrexKillablePatch.CaveOffset).Take(Dc2TrexKillablePatch.CaveLength).ToArray());
        Assert.True(Dc2TrexKillablePatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_TouchesNothingOutsideTheTwoWindows()
    {
        var before = NewPristine();
        var after = NewPristine();
        Dc2TrexKillablePatch.Apply(after);

        for (int i = 0; i < after.Length; i++)
        {
            bool inHook = i >= Dc2TrexKillablePatch.HookOffset && i < Dc2TrexKillablePatch.HookOffset + HookLen;
            bool inCave = i >= Dc2TrexKillablePatch.CaveOffset && i < Dc2TrexKillablePatch.CaveOffset + Dc2TrexKillablePatch.CaveLength;
            if (!inHook && !inCave)
                Assert.True(before[i] == after[i], $"byte at 0x{i:x} changed unexpectedly");
        }
    }

    [Fact]
    public void Restore_ReversesApplyExactly()
    {
        var pristine = NewPristine();
        var exe = NewPristine();
        Dc2TrexKillablePatch.Apply(exe);
        Dc2TrexKillablePatch.Restore(exe);
        Assert.Equal(pristine, exe);
        Assert.True(Dc2TrexKillablePatch.IsRecognizedPristine(exe));
    }

    [Fact]
    public void Restore_OnUnpatchedExe_IsNoOp()
    {
        var exe = NewPristine();
        var snapshot = (byte[])exe.Clone();
        Dc2TrexKillablePatch.Restore(exe);
        Assert.Equal(snapshot, exe);
    }

    [Fact]
    public void Apply_IsNotDoubleApplied()
    {
        var exe = NewPristine();
        Dc2TrexKillablePatch.Apply(exe);
        Assert.True(Dc2TrexKillablePatch.IsApplied(exe));
        Assert.False(Dc2TrexKillablePatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2TrexKillablePatch.Apply(exe));
    }

    [Fact]
    public void Apply_RefusesWrongLength()
    {
        var tooShort = new byte[Dc2WpGatePatch.ExpectedLength - 1];
        Assert.False(Dc2TrexKillablePatch.IsRecognizedPristine(tooShort));
        Assert.Throws<InvalidOperationException>(() => Dc2TrexKillablePatch.Apply(tooShort));
    }

    [Fact]
    public void Apply_RefusesForeignHook_AndLeavesBufferUntouched()
    {
        var foreign = NewPristine();
        foreign[Dc2TrexKillablePatch.HookOffset] = 0x90; // corrupt one sentinel byte
        var snapshot = (byte[])foreign.Clone();

        Assert.False(Dc2TrexKillablePatch.IsRecognizedPristine(foreign));
        Assert.Throws<InvalidOperationException>(() => Dc2TrexKillablePatch.Apply(foreign));
        Assert.Equal(snapshot, foreign);
    }

    [Fact]
    public void Apply_RefusesWhenCaveSlackIsOccupied()
    {
        var exe = NewPristine();
        exe[Dc2TrexKillablePatch.CaveOffset + 4] = 0x12;
        Assert.False(Dc2TrexKillablePatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2TrexKillablePatch.Apply(exe));
    }

    /// <summary>Real-data end-to-end pin, gated on <c>DINORAND_DC2_DIR</c> (install root containing
    /// <c>rebirth\Dino2.exe</c>). Confirms the patcher recognizes the shipping pristine exe and the
    /// hook + cave land at the verified tick offsets. Skipped when the env var is unset.</summary>
    [Fact]
    public void RealExe_Pristine_AppliesAndReverts()
    {
        var dir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var path = Path.Combine(dir, "rebirth", "Dino2.exe");
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        if (!Dc2TrexKillablePatch.IsRecognizedPristine(bytes)) return; // already patched/different → skip

        // The stolen bytes at the hook site must be the tick clamp gate `mov eax,[esi+0x1c4]`.
        Assert.Equal(HookOriginal, bytes.Skip(Dc2TrexKillablePatch.HookOffset).Take(HookLen).ToArray());

        var pristine = (byte[])bytes.Clone();
        Dc2TrexKillablePatch.Apply(bytes);
        Assert.True(Dc2TrexKillablePatch.IsApplied(bytes));
        Assert.Equal(HookPatched, bytes.Skip(Dc2TrexKillablePatch.HookOffset).Take(HookLen).ToArray());
        Assert.Equal(CavePatched, bytes.Skip(Dc2TrexKillablePatch.CaveOffset).Take(Dc2TrexKillablePatch.CaveLength).ToArray());
        Dc2TrexKillablePatch.Restore(bytes);
        Assert.Equal(pristine, bytes);
    }
}
