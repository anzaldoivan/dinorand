using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>Auto-trigger logic for the Inostra spawn-guard lever. Unlike the Triceratops/T-Rex killable
/// levers (which gate on the specific species being injectable), this guard patches a SHARED emitter tick
/// driver used by ~10 actor classes and is byte-identical whenever the emitter is armed, so it is a
/// zero-cost safety net that should turn on for ANY DC2 cross-species run — not only ones that can inject
/// E50 — because any injected donor could drive the same emitter into the un-armed NULL-cursor crash.</summary>
public class Dc2InostraSpawnGuardInstallerWantedForTests
{
    private const int InostraType = 0x0e;
    private const int GiganoType = 0x06;

    private static RandomizerConfig Cfg(bool enemies, Dc2EnemyDistributionMode mode,
        int? fixedType = null, bool force = false) => new()
    {
        RandomizeEnemies = enemies,
        Dc2EnemyMode = mode,
        Dc2FixedSpeciesType = fixedType,
        Dc2MakeInostraSpawnSafe = force,
    };

    [Fact] public void ManualFlag_ForcesOn_EvenWithEnemiesOff() =>
        Assert.True(Dc2InostraSpawnGuardInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, force: true)));

    [Fact] public void EnemiesOff_NoInjectedDonor_False() =>
        Assert.False(Dc2InostraSpawnGuardInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted)));

    [Fact] public void Weighted_AnyRun_True() => // E50 is a default donor; any weighted swap can place it
        Assert.True(Dc2InostraSpawnGuardInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted)));

    [Fact] public void Fixed_InostraPin_True() => // E50 = TYPE 0x0e
        Assert.True(Dc2InostraSpawnGuardInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: InostraType)));

    // NEW behaviour: the shared-emitter guard is zero-cost, so a fixed pin on ANY species still wants it —
    // any injected donor that drives the emitter unarmed hits the identical crash.
    [Fact] public void Fixed_NonInostraPin_StillWanted() =>
        Assert.True(Dc2InostraSpawnGuardInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: GiganoType)));
}

/// <summary>
/// Unit tests for <see cref="Dc2InostraSpawnGuardPatch"/> — the hook+cave Dino2.exe guard that stops a
/// randomizer-injected Inostrancevia (E50, spawn TYPE 0x0e) from crashing when the PSX-recompiled
/// emergence/burst emitter runs with a NULL spawn-descriptor list. RCA:
/// docs/decisions/dc2/crash-rcas/DC2-INOSTRA-SPAWN-DESCRIPTOR-NULL-RCA.md (K97).
///
/// <para><b>Mechanism.</b> The emitter's per-frame tick driver (0x4131d0) loads the manager
/// (<c>esi=[esp+8]</c>), dispatches the state fn, then reads the cursor <c>ecx=[esi+0xc]</c> and checks the
/// terminator <c>cmp dword[ecx],-1</c> at 0x4131e7 with no NULL check; when the manager (an <c>.psxseg</c>
/// global, e.g. 0x5e7aa4) was ticked un-armed, <c>[esi+0xc]</c> is a committed-zero (NULL) slot → read of
/// [NULL] → AV. The lever hooks 0x4131d5 (right after <c>esi=manager</c> loads, before the dispatch), tests
/// the cursor, and bails via the driver's own epilogue when it is NULL (skips the whole tick), otherwise
/// re-runs the stolen <c>push esi; movsx eax,byte[esi+3]</c> and continues into the dispatch.</para>
///
/// <para>The hook and cave bytes are transcribed here independently so a regression in the patcher's
/// offsets/encoding fails the test rather than silently shipping a wild jump.</para>
/// </summary>
public class Dc2InostraSpawnGuardPatchTests
{
    // 0x4131d5: push esi (56) then movsx eax,byte[esi+3] (0F BE 46 03).
    private static readonly byte[] HookOriginal = { 0x56, 0x0F, 0xBE, 0x46, 0x03 };
    private static readonly byte[] HookPatched  = { 0xE9, 0xE6, 0x42, 0x0D, 0x00 }; // jmp 0x4E74C0
    private static readonly byte[] CavePatched =
        Convert.FromHexString("8b460c" + "85c0" + "740a" + "56" + "0fbe4603" + "e909bdf2ff" + "5e" + "c3");

    /// <summary>A synthetic buffer that looks exactly like the pristine rebirth Dino2.exe to the
    /// recognizer: correct length, original hook bytes at the hook offset, zero slack at the cave.</summary>
    private static byte[] NewPristine()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        HookOriginal.CopyTo(exe.AsSpan(Dc2InostraSpawnGuardPatch.HookOffset));
        return exe; // cave region is already zero
    }

    /// <summary>Offset + encoding regression pin: hook at VA 0x4131d5 (foff 0x131d5), cave at VA 0x4E74C0
    /// (foff 0xE74C0), and the hook jmp / cave return-jmp rel32s resolve to exactly those VAs.</summary>
    [Fact]
    public void Offsets_AndJumpEncodings_ArePinned()
    {
        Assert.Equal(0x131D5, Dc2InostraSpawnGuardPatch.HookOffset);
        Assert.Equal(0xE74C0, Dc2InostraSpawnGuardPatch.CaveOffset);
        Assert.Equal(19, Dc2InostraSpawnGuardPatch.CaveLength);

        // hook jmp E9 rel32 → cave
        int hookRel = BitConverter.ToInt32(HookPatched, 1);
        Assert.Equal(Dc2InostraSpawnGuardPatch.CaveOffset,
            Dc2InostraSpawnGuardPatch.HookOffset + 5 + hookRel);

        // cave back-jmp (E9 at cave offset +12) → 0x4131da (the driver's state-dispatch call)
        int backRel = BitConverter.ToInt32(CavePatched, 13);
        int backJmpFoff = Dc2InostraSpawnGuardPatch.CaveOffset + 12;
        Assert.Equal(0x131DA, backJmpFoff + 5 + backRel);
    }

    [Fact]
    public void Pristine_IsRecognized_AndNotApplied()
    {
        var exe = NewPristine();
        Assert.True(Dc2InostraSpawnGuardPatch.IsRecognizedPristine(exe));
        Assert.False(Dc2InostraSpawnGuardPatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_WritesHookAndCave_AndTouchesNothingElse()
    {
        var before = NewPristine();
        var after = NewPristine();
        Dc2InostraSpawnGuardPatch.Apply(after);

        Assert.Equal(HookPatched, after.Skip(Dc2InostraSpawnGuardPatch.HookOffset).Take(5).ToArray());
        Assert.Equal(CavePatched, after.Skip(Dc2InostraSpawnGuardPatch.CaveOffset).Take(19).ToArray());
        Assert.True(Dc2InostraSpawnGuardPatch.IsApplied(after));

        for (int i = 0; i < after.Length; i++)
        {
            bool inHook = i >= Dc2InostraSpawnGuardPatch.HookOffset && i < Dc2InostraSpawnGuardPatch.HookOffset + 5;
            bool inCave = i >= Dc2InostraSpawnGuardPatch.CaveOffset && i < Dc2InostraSpawnGuardPatch.CaveOffset + 19;
            if (!inHook && !inCave)
                Assert.True(before[i] == after[i], $"byte at 0x{i:x} changed unexpectedly");
        }
    }

    [Fact]
    public void Restore_ReversesApplyExactly()
    {
        var pristine = NewPristine();
        var exe = NewPristine();
        Dc2InostraSpawnGuardPatch.Apply(exe);
        Dc2InostraSpawnGuardPatch.Restore(exe);
        Assert.Equal(pristine, exe);
        Assert.True(Dc2InostraSpawnGuardPatch.IsRecognizedPristine(exe));
    }

    [Fact]
    public void Restore_OnUnpatchedExe_IsNoOp()
    {
        var exe = NewPristine();
        var snapshot = (byte[])exe.Clone();
        Dc2InostraSpawnGuardPatch.Restore(exe);
        Assert.Equal(snapshot, exe);
    }

    [Fact]
    public void Apply_IsNotDoubleApplied()
    {
        var exe = NewPristine();
        Dc2InostraSpawnGuardPatch.Apply(exe);
        Assert.True(Dc2InostraSpawnGuardPatch.IsApplied(exe));
        Assert.False(Dc2InostraSpawnGuardPatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2InostraSpawnGuardPatch.Apply(exe));
    }

    [Fact]
    public void Apply_RefusesWrongLength()
    {
        var tooShort = new byte[Dc2WpGatePatch.ExpectedLength - 1];
        Assert.False(Dc2InostraSpawnGuardPatch.IsRecognizedPristine(tooShort));
        Assert.Throws<InvalidOperationException>(() => Dc2InostraSpawnGuardPatch.Apply(tooShort));
    }

    [Fact]
    public void Apply_RefusesForeignHook_AndLeavesBufferUntouched()
    {
        var foreign = NewPristine();
        foreign[Dc2InostraSpawnGuardPatch.HookOffset] = 0x90; // clobber the recognized hook byte
        var snapshot = (byte[])foreign.Clone();

        Assert.False(Dc2InostraSpawnGuardPatch.IsRecognizedPristine(foreign));
        Assert.Throws<InvalidOperationException>(() => Dc2InostraSpawnGuardPatch.Apply(foreign));
        Assert.Equal(snapshot, foreign);
    }

    [Fact]
    public void Apply_RefusesOccupiedCaveSlack()
    {
        var occupied = NewPristine();
        occupied[Dc2InostraSpawnGuardPatch.CaveOffset + 3] = 0xCC; // something already living in the slack
        Assert.False(Dc2InostraSpawnGuardPatch.IsRecognizedPristine(occupied));
        Assert.Throws<InvalidOperationException>(() => Dc2InostraSpawnGuardPatch.Apply(occupied));
    }

    /// <summary>Real-data end-to-end pin, gated on <c>DINORAND_DC2_DIR</c> (the rebirth <c>Data</c> dir;
    /// <c>Dino2.exe</c> is its sibling). Confirms the patcher recognizes the shipping pristine exe and
    /// that the RE claim holds against the real bytes: the hook site is the driver's dispatch prologue
    /// (<c>push esi; movsx eax,byte[esi+3]</c>), the driver's NULL-deref terminator check at VA 0x4131e7 is
    /// <c>cmp dword[ecx],-1</c> (<c>83 39 FF</c>), and the cave slack is free. Skipped when the env var is
    /// unset or the exe is already patched.</summary>
    [Fact]
    public void RealExe_HookSiteIsTheEmitterTickDriver_AndRoundTrips()
    {
        var dir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var path = Path.Combine(dir, "..", "Dino2.exe");
        if (!File.Exists(path)) path = Path.Combine(dir, "rebirth", "Dino2.exe");
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        if (!Dc2InostraSpawnGuardPatch.IsRecognizedPristine(bytes)) return; // already patched/different → skip

        // Decode invariants against the real exe.
        Assert.Equal(HookOriginal, bytes.Skip(Dc2InostraSpawnGuardPatch.HookOffset).Take(5).ToArray());
        // 0x4131e1 mov ecx,[esi+0xc] then 0x4131e7 cmp dword[ecx],-1 — the NULL-deref the guard prevents.
        Assert.Equal(new byte[] { 0x8B, 0x4E, 0x0C }, bytes.Skip(0x4131E1 - 0x400000).Take(3).ToArray());
        Assert.Equal(new byte[] { 0x83, 0x39, 0xFF }, bytes.Skip(0x4131E7 - 0x400000).Take(3).ToArray());

        var pristine = (byte[])bytes.Clone();
        Dc2InostraSpawnGuardPatch.Apply(bytes);
        Assert.True(Dc2InostraSpawnGuardPatch.IsApplied(bytes));
        Dc2InostraSpawnGuardPatch.Restore(bytes);
        Assert.Equal(pristine, bytes);
    }
}
