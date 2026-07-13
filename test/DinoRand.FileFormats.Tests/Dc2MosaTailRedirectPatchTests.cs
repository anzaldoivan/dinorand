using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>Auto-trigger logic for the Mosasaurus tail-redirect lever: it must turn on exactly when the
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
/// Unit tests for <see cref="Dc2MosaTailRedirectPatch"/> — the Dino2.exe hook + code cave that redirects an
/// injected E80 Mosasaurus's <b>wide-turn tail strike</b> (attack-pattern id <c>byte[esi+0x55]==2</c>, state
/// 7 <c>0x43ff40</c>) to the <b>narrow bite</b> (pattern 0) in non-native land rooms, so the mosa never
/// performs the OOB-causing move — while leaving the four native aquatic rooms (ST700/702/703/704)
/// byte-identical. RE + decision record: docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md §9.
///
/// <para>The E80 attack FSM is nested: outer state <c>byte[esi+0x54]</c> routes to the state-1 hub
/// <c>0x43fc80</c>, which dispatches a second selector <c>byte[esi+0x55]</c> (the attack-pattern id) through
/// sub-tables <c>0x7211f8</c> (aim) + <c>0x721218</c> (execute) — K106, CE-confirmed 2026-07-13. The hook
/// steals the hub's 5-byte prologue <c>push esi; mov esi,[esp+8]</c> (VA 0x43fc80); the cave replays it,
/// then — only for a TYPE-0x0a actor outside the four native rooms whose pattern is 2 — rewrites
/// <c>byte[esi+0x55]:=0</c> so the mosa runs the bite instead of the tail. Any other actor/room/pattern falls
/// straight through to the unchanged hub.</para>
///
/// <para>The hook/cave bytes are independently transcribed here so a regression in the patcher's tables
/// fails the test rather than silently shipping a wrong offset into the game exe.</para>
/// </summary>
public class Dc2MosaTailRedirectPatchTests
{
    // Hook steals the state-1 hub prologue `push esi; mov esi,[esp+8]` (5 bytes) at VA 0x43fc80.
    private static readonly byte[] HookOriginal = { 0x56, 0x8B, 0x74, 0x24, 0x08 };
    // jmp 0x4E7600  (E9 rel32, exactly 5 bytes — no nop padding needed).
    private static readonly byte[] HookPatched = { 0xE9, 0x7B, 0x79, 0x0A, 0x00 };

    // 66-byte cave, capstone-verified. Shape: replay stolen (push esi; mov esi,[esp+8]); E80 guard
    // cmp byte[esi+0x58],0x0a jne done; mov eax,[0x876DB8]; movzx ecx,word[eax+0x1090];
    // cmp cx,{0700,0702,0703,0704} je done; cmp byte[esi+0x55],2 jne done; mov byte[esi+0x55],0;
    // done: jmp 0x43fc85 (resume the hub).
    private static readonly byte[] Cave = Convert.FromHexString(
        "568b742408807e580a7532a1b86d87000fb788901000006681f90007741f6681f9020774186681f9030774116681f90407740a807e55027504c6465500e94386f5ff");

    private const int HookLen = 5;

    internal static byte[] NewPristine()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        HookOriginal.CopyTo(exe.AsSpan(Dc2MosaTailRedirectPatch.HookOffset));
        return exe;
    }

    /// <summary>Regression pin for the RE: the hook is the E80 state-1 attack-pattern hub prologue
    /// (VA 0x43fc80), stealing <c>push esi; mov esi,[esp+8]</c> (5 bytes) so the cave can gate the pattern
    /// selector <c>byte[esi+0x55]</c> before the sub-dispatch.</summary>
    [Fact]
    public void Hook_TargetsTheStateOneAttackPatternHub()
    {
        Assert.Equal(0x3FC80, Dc2MosaTailRedirectPatch.HookOffset); // VA 0x43FC80 (state-1 hub 0x43fc80)
        Assert.Equal(0xE7600, Dc2MosaTailRedirectPatch.CaveOffset); // VA 0x4E7600 (end-of-.text zero slack)
        Assert.Equal(66, Dc2MosaTailRedirectPatch.CaveLength);
        var exe = NewPristine();
        Assert.Equal(HookOriginal, exe.Skip(Dc2MosaTailRedirectPatch.HookOffset).Take(HookLen).ToArray());
    }

    /// <summary>The cave must carry the E80 TYPE guard (cmp byte[esi+0x58],0x0a), the exact four
    /// native-room blocklist words, AND the pattern-2→0 substitution (cmp byte[esi+0x55],2 / mov
    /// byte[esi+0x55],0) — so a regression that drops the species guard, widens/narrows the blocklist, or
    /// changes which pattern is redirected fails here rather than in-game.</summary>
    [Fact]
    public void Cave_CarriesGuard_Blocklist_AndPattern2To0Substitution()
    {
        var hex = Convert.ToHexString(Cave).ToLowerInvariant();
        Assert.StartsWith("568b742408", hex);       // replay stolen: push esi; mov esi,[esp+8]
        Assert.Contains("807e580a", hex);           // cmp byte[esi+0x58],0x0a  (E80 TYPE guard)
        Assert.Contains("6681f90007", hex);         // cmp cx,0x0700  ST700
        Assert.Contains("6681f90207", hex);         // cmp cx,0x0702  ST702
        Assert.Contains("6681f90307", hex);         // cmp cx,0x0703  ST703
        Assert.Contains("6681f90407", hex);         // cmp cx,0x0704  ST704
        Assert.Contains("807e5502", hex);           // cmp byte[esi+0x55],0x02  (tail pattern)
        Assert.Contains("c6465500", hex);           // mov byte[esi+0x55],0x00  (→ bite pattern)
        Assert.EndsWith("e94386f5ff", hex);         // jmp 0x43fc85  (resume the hub)
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

    /// <summary>Real-data end-to-end pin, gated on <c>DINORAND_DC2_DIR</c>. Confirms the patcher recognizes
    /// the shipping pristine exe and the hook lands on the exact state-1 hub prologue. Skipped when the exe
    /// can't be located or is already patched/different.</summary>
    [Fact]
    public void RealExe_Pristine_AppliesAndReverts()
    {
        var path = LocateDino2Exe();
        if (path is null) return;

        var bytes = File.ReadAllBytes(path);
        if (!Dc2MosaTailRedirectPatch.IsRecognizedPristine(bytes)) return; // already patched/different → skip

        Assert.Equal(HookOriginal, bytes.Skip(Dc2MosaTailRedirectPatch.HookOffset).Take(HookLen).ToArray());

        var pristine = (byte[])bytes.Clone();
        Dc2MosaTailRedirectPatch.Apply(bytes);
        Assert.True(Dc2MosaTailRedirectPatch.IsApplied(bytes));
        Assert.Equal(Cave, bytes.Skip(Dc2MosaTailRedirectPatch.CaveOffset).Take(Dc2MosaTailRedirectPatch.CaveLength).ToArray());
        Dc2MosaTailRedirectPatch.Restore(bytes);
        Assert.Equal(pristine, bytes);
    }

    private static string? LocateDino2Exe()
    {
        var dir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        // DINORAND_DC2_DIR points at rebirth/Data; the exe is one level up (rebirth/Dino2.exe).
        foreach (var c in new[]
        {
            Path.Combine(dir, "rebirth", "Dino2.exe"),
            Path.Combine(dir, "..", "Dino2.exe"),
            Path.Combine(dir, "Dino2.exe"),
        })
            if (File.Exists(c)) return c;
        return null;
    }
}
