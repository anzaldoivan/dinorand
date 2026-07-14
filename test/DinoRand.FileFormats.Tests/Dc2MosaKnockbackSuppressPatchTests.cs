using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>Auto-trigger logic for the Mosasaurus knockback-suppress lever: it must turn on exactly when
/// the run can inject an E80 Mosasaurus into a land room (water-level swaps admit E80 to the weighted pool,
/// or a fixed pin selects it) or the user forces it — the same predicate as the grab-suppress lever, since
/// both target an injected aquatic Mosasaurus in a non-native room. Harmless no-op when none exists.</summary>
public class Dc2MosaKnockbackSuppressInstallerWantedForTests
{
    private static RandomizerConfig Cfg(bool enemies, Dc2EnemyDistributionMode mode,
        int? fixedType = null, bool water = false, bool force = false) => new()
    {
        RandomizeEnemies = enemies,
        Dc2EnemyMode = mode,
        Dc2FixedSpeciesType = fixedType,
        Dc2AllowWaterLevelEnemySwaps = water,
        Dc2SuppressMosaKnockback = force,
    };

    [Fact] public void ManualFlag_ForcesOn_EvenWithEnemiesOff() =>
        Assert.True(Dc2MosaKnockbackSuppressInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, force: true)));

    [Fact] public void EnemiesOff_NoInjectedMosa_False() =>
        Assert.False(Dc2MosaKnockbackSuppressInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, water: true)));

    [Fact] public void Weighted_WaterSwapsOn_True() =>
        Assert.True(Dc2MosaKnockbackSuppressInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, water: true)));

    [Fact] public void Weighted_WaterSwapsOff_False() =>
        Assert.False(Dc2MosaKnockbackSuppressInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, water: false)));

    [Fact] public void Fixed_MosaPin_True() =>
        Assert.True(Dc2MosaKnockbackSuppressInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x0a)));

    [Fact] public void Fixed_OtherPin_False() =>
        Assert.False(Dc2MosaKnockbackSuppressInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x03)));
}

/// <summary>
/// Unit tests for <see cref="Dc2MosaKnockbackSuppressPatch"/> — the Dino2.exe hook + code cave that keeps
/// an injected E80 Mosasaurus's TAIL/proximity knockback from flinging the player OUT OF BOUNDS in land
/// rooms, while leaving native aquatic rooms (ST700/702/703/704) byte-identical. RE + decision record:
/// docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md §8.5, KNOWLEDGE-AND-QUESTIONS.md K105.
///
/// <para>Live capture (2026-07-13) pinned the OOB writer to the shared "push actor EDI away from actor EBX"
/// knockback/separation applicator at VA 0x452D84 (<c>add word[edi+0x40],ax</c>) — with the attacker in
/// EBX and the target in EDI. The hook steals the 7-byte <c>add word[edi+0x40],ax; mov eax,[edi+0x64]</c>
/// pair; the cave re-runs it (vanilla) unless the target is the player (<c>edi==[[0x876DB8]+0x940]</c>),
/// the attacker is the Mosasaurus (<c>byte[ebx+0x58]==0x0a</c>), and the room is a non-native land room
/// (<c>word[[0x876DB8]+0x1090]∉{0x700,0x702,0x703,0x704}</c>), in which case it skips the whole shove +
/// chain propagation (<c>jmp 0x452dac</c>).</para>
///
/// <para>The hook/cave bytes are independently transcribed here so a regression in the patcher's tables
/// fails the test rather than silently shipping a wrong offset into the game exe.</para>
/// </summary>
public class Dc2MosaKnockbackSuppressPatchTests
{
    // Stolen 7-byte pair `add word[edi+0x40],ax (66 01 47 40); mov eax,[edi+0x64] (8b 47 64)` — head of the
    // knockback applicator's player-position add, so the cave can gate the whole shove + chain propagation.
    private static readonly byte[] HookOriginal = { 0x66, 0x01, 0x47, 0x40, 0x8B, 0x47, 0x64 };
    // jmp <cave 0x4E75A0> ; nop nop  (E9 rel32 + 2 padding over the 7-byte stolen window).
    private static readonly byte[] HookPatched = { 0xE9, 0x17, 0x48, 0x09, 0x00, 0x90, 0x90 };

    // 79-byte cave, capstone-verified. Shape: player check (mov eax,[0x876DB8]; mov eax,[eax+0x940];
    // cmp edi,eax; jne van); mosa guard (cmp byte[ebx+0x58],0x0a; jne van); room blocklist (movzx
    // eax,word[eax+0x1090]; cmp ax,{0700,0702,0703,0704} je van); jmp 0x452dac (SUPPRESS whole shove);
    // van: mov ax,[esp+0x30] (reload X delta, eax clobbered); add word[edi+0x40],ax (stolen); mov
    // eax,[edi+0x64] (stolen); jmp 0x452d8b (resume vanilla at the Z add).
    private static readonly byte[] Cave = Convert.FromHexString(
        "a1b86d87008b80400900003bf8752f807b580a7529a1b86d87000fb78090100000663d0007"
        + "7417663d02077411663d0307740b663d04077405e9ceb7f6ff668b442430660147408b4764e99cb7f6ff");

    private const int HookLen = 7;

    internal static byte[] NewPristine()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        HookOriginal.CopyTo(exe.AsSpan(Dc2MosaKnockbackSuppressPatch.HookOffset));
        return exe;
    }

    /// <summary>Regression pin for the RE: the hook is the head of the knockback applicator's player-add
    /// (VA 0x452D84), stealing the <c>add word[edi+0x40],ax; mov eax,[edi+0x64]</c> pair so the cave can
    /// gate the whole shove.</summary>
    [Fact]
    public void Hook_TargetsTheKnockbackApplicator()
    {
        Assert.Equal(0x52D84, Dc2MosaKnockbackSuppressPatch.HookOffset);  // VA 0x452D84
        Assert.Equal(0xE75A0, Dc2MosaKnockbackSuppressPatch.CaveOffset);  // VA 0x4E75A0
        Assert.Equal(79, Dc2MosaKnockbackSuppressPatch.CaveLength);
        var exe = NewPristine();
        Assert.Equal(HookOriginal, exe.Skip(Dc2MosaKnockbackSuppressPatch.HookOffset).Take(HookLen).ToArray());
    }

    /// <summary>The cave must carry the player-target guard (cmp edi,[[0x876DB8]+0x940]), the E80 attacker
    /// guard (cmp byte[ebx+0x58],0x0a), and the exact four native-room blocklist words — so a regression
    /// that drops a guard or widens/narrows the blocklist fails here rather than in-game.</summary>
    [Fact]
    public void Cave_CarriesPlayerAndMosaGuardAndRoomBlocklist()
    {
        var hex = Convert.ToHexString(Cave).ToLowerInvariant();
        Assert.Contains("8b80400900", hex);   // mov eax,[eax+0x940]  (player ptr deref)
        Assert.Contains("3bf8", hex);          // cmp edi,eax          (target == player?)
        Assert.Contains("807b580a", hex);      // cmp byte[ebx+0x58],0x0a  (attacker == mosa?)
        Assert.Contains("0fb78090100000", hex);// movzx eax,word[eax+0x1090]  (room word)
        Assert.Contains("663d0007", hex);      // cmp ax,0x0700  ST700
        Assert.Contains("663d0207", hex);      // cmp ax,0x0702  ST702
        Assert.Contains("663d0307", hex);      // cmp ax,0x0703  ST703
        Assert.Contains("663d0407", hex);      // cmp ax,0x0704  ST704
        Assert.Contains("66014740", hex);      // vanilla re-runs the stolen add word[edi+0x40],ax
    }

    [Fact]
    public void Pristine_IsRecognized_AndNotApplied()
    {
        var exe = NewPristine();
        Assert.True(Dc2MosaKnockbackSuppressPatch.IsRecognizedPristine(exe));
        Assert.False(Dc2MosaKnockbackSuppressPatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_WritesExactlyTheHookAndCave()
    {
        var exe = NewPristine();
        Dc2MosaKnockbackSuppressPatch.Apply(exe);
        Assert.Equal(HookPatched, exe.Skip(Dc2MosaKnockbackSuppressPatch.HookOffset).Take(HookLen).ToArray());
        Assert.Equal(Cave, exe.Skip(Dc2MosaKnockbackSuppressPatch.CaveOffset).Take(Dc2MosaKnockbackSuppressPatch.CaveLength).ToArray());
        Assert.True(Dc2MosaKnockbackSuppressPatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_TouchesNothingOutsideTheTwoWindows()
    {
        var before = NewPristine();
        var after = NewPristine();
        Dc2MosaKnockbackSuppressPatch.Apply(after);
        for (int i = 0; i < after.Length; i++)
        {
            bool inHook = i >= Dc2MosaKnockbackSuppressPatch.HookOffset && i < Dc2MosaKnockbackSuppressPatch.HookOffset + HookLen;
            bool inCave = i >= Dc2MosaKnockbackSuppressPatch.CaveOffset && i < Dc2MosaKnockbackSuppressPatch.CaveOffset + Dc2MosaKnockbackSuppressPatch.CaveLength;
            if (!inHook && !inCave)
                Assert.True(before[i] == after[i], $"byte at 0x{i:x} changed unexpectedly");
        }
    }

    [Fact]
    public void CaveRegion_DoesNotCollideWithTrexOrGrabCaves()
    {
        // All DC2 exe caves live in the same end-of-.text zero slack; they must not overlap so the levers
        // compose in any order.
        (int off, int len)[] regions =
        {
            (Dc2TrexKillablePatch.CaveOffset, Dc2TrexKillablePatch.CaveLength),
            (Dc2MosaGrabSuppressPatch.Cave19Offset, Dc2MosaGrabSuppressPatch.CaveLength),
            (Dc2MosaGrabSuppressPatch.Cave20Offset, Dc2MosaGrabSuppressPatch.CaveLength),
            (Dc2MosaKnockbackSuppressPatch.CaveOffset, Dc2MosaKnockbackSuppressPatch.CaveLength),
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
        Dc2MosaKnockbackSuppressPatch.Apply(exe);
        Dc2MosaKnockbackSuppressPatch.Restore(exe);
        Assert.Equal(pristine, exe);
        Assert.True(Dc2MosaKnockbackSuppressPatch.IsRecognizedPristine(exe));
    }

    [Fact]
    public void Restore_OnUnpatchedExe_IsNoOp()
    {
        var exe = NewPristine();
        var snapshot = (byte[])exe.Clone();
        Dc2MosaKnockbackSuppressPatch.Restore(exe);
        Assert.Equal(snapshot, exe);
    }

    [Fact]
    public void Apply_IsNotDoubleApplied()
    {
        var exe = NewPristine();
        Dc2MosaKnockbackSuppressPatch.Apply(exe);
        Assert.True(Dc2MosaKnockbackSuppressPatch.IsApplied(exe));
        Assert.False(Dc2MosaKnockbackSuppressPatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaKnockbackSuppressPatch.Apply(exe));
    }

    [Fact]
    public void Apply_RefusesWrongLength()
    {
        var tooShort = new byte[Dc2WpGatePatch.ExpectedLength - 1];
        Assert.False(Dc2MosaKnockbackSuppressPatch.IsRecognizedPristine(tooShort));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaKnockbackSuppressPatch.Apply(tooShort));
    }

    [Fact]
    public void Apply_RefusesForeignHook_AndLeavesBufferUntouched()
    {
        var foreign = NewPristine();
        foreign[Dc2MosaKnockbackSuppressPatch.HookOffset] = 0x90; // corrupt one sentinel byte
        var snapshot = (byte[])foreign.Clone();
        Assert.False(Dc2MosaKnockbackSuppressPatch.IsRecognizedPristine(foreign));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaKnockbackSuppressPatch.Apply(foreign));
        Assert.Equal(snapshot, foreign);
    }

    [Fact]
    public void Apply_RefusesWhenCaveSlackIsOccupied()
    {
        var exe = NewPristine();
        exe[Dc2MosaKnockbackSuppressPatch.CaveOffset + 4] = 0x12;
        Assert.False(Dc2MosaKnockbackSuppressPatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaKnockbackSuppressPatch.Apply(exe));
    }

    /// <summary>Real-data end-to-end pin, gated on <c>DINORAND_DC2_DIR</c>. Confirms the patcher recognizes
    /// the shipping pristine exe and the hook lands on the exact knockback-applicator bytes. Skipped when
    /// the exe can't be located or is already patched/different.</summary>
    [Fact]
    public void RealExe_Pristine_AppliesAndReverts()
    {
        var path = LocateDino2Exe();
        if (path is null) return;

        var bytes = File.ReadAllBytes(path);
        if (!Dc2MosaKnockbackSuppressPatch.IsRecognizedPristine(bytes)) return; // already patched/different → skip

        Assert.Equal(HookOriginal, bytes.Skip(Dc2MosaKnockbackSuppressPatch.HookOffset).Take(HookLen).ToArray());
        var pristine = (byte[])bytes.Clone();
        Dc2MosaKnockbackSuppressPatch.Apply(bytes);
        Assert.True(Dc2MosaKnockbackSuppressPatch.IsApplied(bytes));
        Assert.Equal(Cave, bytes.Skip(Dc2MosaKnockbackSuppressPatch.CaveOffset).Take(Dc2MosaKnockbackSuppressPatch.CaveLength).ToArray());
        Dc2MosaKnockbackSuppressPatch.Restore(bytes);
        Assert.Equal(pristine, bytes);
    }

    private static string? LocateDino2Exe()
    {
        var dir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return null;
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
