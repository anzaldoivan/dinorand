using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>Auto-trigger logic for the Mosasaurus grab-suppress lever: it must turn on exactly when the
/// run can inject an E80 Mosasaurus into a land room (water-level swaps admit E80 to the weighted pool,
/// or a fixed pin selects it) or the user forces it, so the frontend applies it without a separate
/// toggle. Harmless no-op when no injected Mosasaurus exists.</summary>
public class Dc2MosaGrabSuppressInstallerWantedForTests
{
    private static RandomizerConfig Cfg(bool enemies, Dc2EnemyDistributionMode mode,
        int? fixedType = null, bool water = false, bool force = false) => new()
    {
        RandomizeEnemies = enemies,
        Dc2EnemyMode = mode,
        Dc2FixedSpeciesType = fixedType,
        Dc2AllowWaterLevelEnemySwaps = water,
        Dc2SuppressMosaGrab = force,
    };

    [Fact] public void ManualFlag_ForcesOn_EvenWithEnemiesOff() =>
        Assert.True(Dc2MosaGrabSuppressInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, force: true)));

    [Fact] public void EnemiesOff_NoInjectedMosa_False() =>
        Assert.False(Dc2MosaGrabSuppressInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, water: true)));

    [Fact] public void Weighted_WaterSwapsOn_True() => // E80 enters the weighted pool
        Assert.True(Dc2MosaGrabSuppressInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, water: true)));

    [Fact] public void Weighted_WaterSwapsOff_False() => // no aquatic donors admitted
        Assert.False(Dc2MosaGrabSuppressInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, water: false)));

    [Fact] public void Fixed_MosaPin_True() =>
        Assert.True(Dc2MosaGrabSuppressInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x0a)));

    [Fact] public void Fixed_OtherPin_False() => // T-Rex 0x03 is not a Mosasaurus
        Assert.False(Dc2MosaGrabSuppressInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x03)));
}

/// <summary>
/// Unit tests for <see cref="Dc2MosaGrabSuppressPatch"/> — the Dino2.exe hooks + code caves that keep an
/// injected E80 Mosasaurus's grab IN-BOUNDS in land rooms by neutralizing only the player-Y copy, while
/// leaving the four native aquatic rooms (ST700/702/703/704) byte-identical. RE + decision record:
/// docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md.
///
/// <para>The grab (behavior states 19 <c>0x440860</c> / 20 <c>0x440d60</c>) copies the mosa's own XYZ+rot
/// onto the player each frame; the OOB launch is isolated to the single player-Y write
/// <c>mov word[edi+0x42],dx</c> at VA 0x4408AF / 0x440DB9 (the Allosaurus grab, by contrast, never copies
/// enemy pos onto the player and stays in-bounds — §3e of the plan). Each hook steals the 8-byte
/// <c>mov dx,[esi+0x42]; mov word[edi+0x42],dx</c> pair; the cave re-runs it only for a non-E80 actor or a
/// native aquatic room, and skips the player-Y write for an injected (TYPE 0x0a) mosa outside them.</para>
///
/// <para>The hook/cave bytes are independently transcribed here so a regression in the patcher's tables
/// fails the test rather than silently shipping a wrong offset into the game exe.</para>
/// </summary>
public class Dc2MosaGrabSuppressPatchTests
{
    // Both hook sites steal the same 8-byte pair `mov word[edi+0x40],cx; mov dx,[esi+0x42]` — the head
    // of the player-position copy (X write + the mosa-Y read), so the cave can gate the WHOLE copy.
    private static readonly byte[] HookOriginal = { 0x66, 0x89, 0x4F, 0x40, 0x66, 0x8B, 0x56, 0x42 };
    // jmp <cave> ; nop nop nop  (E9 rel32 + 3 padding over the 8-byte stolen window).
    private static readonly byte[] Hook19Patched = { 0xE9, 0x54, 0x6C, 0x0A, 0x00, 0x90, 0x90, 0x90 }; // -> 0x4E7500
    private static readonly byte[] Hook20Patched = { 0xE9, 0x9A, 0x67, 0x0A, 0x00, 0x90, 0x90, 0x90 }; // -> 0x4E7550

    // 68-byte caves, verified via capstone. Shape (both): E80 guard cmp byte[esi+0x58],0x0a; jne van;
    // mov eax,[0x876DB8]; movzx ecx,word[eax+0x1090]; cmp cx,{0700,0702,0703,0704} je van;
    // jmp <after-the-whole-player-copy> (SUPPRESS X/Y/Z/rot); van: mov cx,[esi+0x40] (reload, guard
    // clobbered ecx); mov word[edi+0x40],cx (stolen X); mov dx,[esi+0x42] (stolen); jmp <resume Y/Z/rot>.
    // Only the two final rel32s differ between the two states.
    private static readonly byte[] Cave19 = Convert.FromHexString(
        "807e580a752da1b86d87000fb788901000006681f90007741a6681f9020774136681f90307740c6681f904077405e99093f5ff668b4e4066894f40668b5642e96b93f5ff");
    private static readonly byte[] Cave20 = Convert.FromHexString(
        "807e580a752da1b86d87000fb788901000006681f90007741a6681f9020774136681f90307740c6681f904077405e94a98f5ff668b4e4066894f40668b5642e92598f5ff");

    private const int HookLen = 8;

    internal static byte[] NewPristine()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        HookOriginal.CopyTo(exe.AsSpan(Dc2MosaGrabSuppressPatch.Hook19Offset));
        HookOriginal.CopyTo(exe.AsSpan(Dc2MosaGrabSuppressPatch.Hook20Offset));
        return exe;
    }

    /// <summary>Regression pin for the RE: the two hooks are the head of the player-position copy in grab
    /// states 19/20 (VA 0x4408A7 / 0x440DB1), each stealing the <c>mov word[edi+0x40],cx; mov dx,[esi+0x42]</c>
    /// pair so the cave can gate the whole XYZ+rot copy, not just Y.</summary>
    [Fact]
    public void Hooks_TargetThePlayerPositionCopy()
    {
        Assert.Equal(0x408A7, Dc2MosaGrabSuppressPatch.Hook19Offset); // VA 0x4408A7 (head of the player XYZ+rot copy)
        Assert.Equal(0x40DB1, Dc2MosaGrabSuppressPatch.Hook20Offset); // VA 0x440DB1
        Assert.Equal(68, Dc2MosaGrabSuppressPatch.CaveLength);
        var exe = NewPristine();
        Assert.Equal(HookOriginal, exe.Skip(Dc2MosaGrabSuppressPatch.Hook19Offset).Take(HookLen).ToArray());
        Assert.Equal(HookOriginal, exe.Skip(Dc2MosaGrabSuppressPatch.Hook20Offset).Take(HookLen).ToArray());
    }

    /// <summary>The caves must carry the E80 TYPE guard (cmp byte[esi+0x58],0x0a) and the exact four
    /// native-room blocklist words (0x0700/0x0702/0x0703/0x0704) — so a regression that widens or narrows
    /// the blocklist, or drops the species guard, fails here rather than in-game.</summary>
    [Fact]
    public void Cave_CarriesE80GuardAndNativeRoomBlocklist()
    {
        foreach (var cave in new[] { Cave19, Cave20 })
        {
            var hex = Convert.ToHexString(cave).ToLowerInvariant();
            Assert.StartsWith("807e580a", hex);                 // cmp byte[esi+0x58],0x0a
            Assert.Contains("6681f90007", hex);                 // cmp cx,0x0700  ST700
            Assert.Contains("6681f90207", hex);                 // cmp cx,0x0702  ST702
            Assert.Contains("6681f90307", hex);                 // cmp cx,0x0703  ST703
            Assert.Contains("6681f90407", hex);                 // cmp cx,0x0704  ST704
            Assert.Contains("668b4e4066894f40668b5642", hex);   // vanilla path re-runs reload cx + stolen X write + mosa-Y read
        }
    }

    [Fact]
    public void Pristine_IsRecognized_AndNotApplied()
    {
        var exe = NewPristine();
        Assert.True(Dc2MosaGrabSuppressPatch.IsRecognizedPristine(exe));
        Assert.False(Dc2MosaGrabSuppressPatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_WritesExactlyTheTwoHooksAndTwoCaves()
    {
        var exe = NewPristine();
        Dc2MosaGrabSuppressPatch.Apply(exe);

        Assert.Equal(Hook19Patched, exe.Skip(Dc2MosaGrabSuppressPatch.Hook19Offset).Take(HookLen).ToArray());
        Assert.Equal(Hook20Patched, exe.Skip(Dc2MosaGrabSuppressPatch.Hook20Offset).Take(HookLen).ToArray());
        Assert.Equal(Cave19, exe.Skip(Dc2MosaGrabSuppressPatch.Cave19Offset).Take(Dc2MosaGrabSuppressPatch.CaveLength).ToArray());
        Assert.Equal(Cave20, exe.Skip(Dc2MosaGrabSuppressPatch.Cave20Offset).Take(Dc2MosaGrabSuppressPatch.CaveLength).ToArray());
        Assert.True(Dc2MosaGrabSuppressPatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_TouchesNothingOutsideTheFourWindows()
    {
        var before = NewPristine();
        var after = NewPristine();
        Dc2MosaGrabSuppressPatch.Apply(after);

        for (int i = 0; i < after.Length; i++)
        {
            bool inH19 = i >= Dc2MosaGrabSuppressPatch.Hook19Offset && i < Dc2MosaGrabSuppressPatch.Hook19Offset + HookLen;
            bool inH20 = i >= Dc2MosaGrabSuppressPatch.Hook20Offset && i < Dc2MosaGrabSuppressPatch.Hook20Offset + HookLen;
            bool inC19 = i >= Dc2MosaGrabSuppressPatch.Cave19Offset && i < Dc2MosaGrabSuppressPatch.Cave19Offset + Dc2MosaGrabSuppressPatch.CaveLength;
            bool inC20 = i >= Dc2MosaGrabSuppressPatch.Cave20Offset && i < Dc2MosaGrabSuppressPatch.Cave20Offset + Dc2MosaGrabSuppressPatch.CaveLength;
            if (!inH19 && !inH20 && !inC19 && !inC20)
                Assert.True(before[i] == after[i], $"byte at 0x{i:x} changed unexpectedly");
        }
    }

    [Fact]
    public void CaveRegions_DoNotCollideWithTrexOrInostraCaves()
    {
        // The three DC2 exe caves live in the same end-of-.text zero slack; they must not overlap so the
        // levers compose in any order.
        (int off, int len)[] regions =
        {
            (Dc2TrexKillablePatch.CaveOffset, Dc2TrexKillablePatch.CaveLength),
            (Dc2MosaGrabSuppressPatch.Cave19Offset, Dc2MosaGrabSuppressPatch.CaveLength),
            (Dc2MosaGrabSuppressPatch.Cave20Offset, Dc2MosaGrabSuppressPatch.CaveLength),
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
        Dc2MosaGrabSuppressPatch.Apply(exe);
        Dc2MosaGrabSuppressPatch.Restore(exe);
        Assert.Equal(pristine, exe);
        Assert.True(Dc2MosaGrabSuppressPatch.IsRecognizedPristine(exe));
    }

    [Fact]
    public void Restore_OnUnpatchedExe_IsNoOp()
    {
        var exe = NewPristine();
        var snapshot = (byte[])exe.Clone();
        Dc2MosaGrabSuppressPatch.Restore(exe);
        Assert.Equal(snapshot, exe);
    }

    [Fact]
    public void Apply_IsNotDoubleApplied()
    {
        var exe = NewPristine();
        Dc2MosaGrabSuppressPatch.Apply(exe);
        Assert.True(Dc2MosaGrabSuppressPatch.IsApplied(exe));
        Assert.False(Dc2MosaGrabSuppressPatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaGrabSuppressPatch.Apply(exe));
    }

    [Fact]
    public void Apply_RefusesWrongLength()
    {
        var tooShort = new byte[Dc2WpGatePatch.ExpectedLength - 1];
        Assert.False(Dc2MosaGrabSuppressPatch.IsRecognizedPristine(tooShort));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaGrabSuppressPatch.Apply(tooShort));
    }

    [Fact]
    public void Apply_RefusesForeignHook_AndLeavesBufferUntouched()
    {
        var foreign = NewPristine();
        foreign[Dc2MosaGrabSuppressPatch.Hook20Offset] = 0x90; // corrupt one sentinel byte
        var snapshot = (byte[])foreign.Clone();

        Assert.False(Dc2MosaGrabSuppressPatch.IsRecognizedPristine(foreign));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaGrabSuppressPatch.Apply(foreign));
        Assert.Equal(snapshot, foreign);
    }

    [Fact]
    public void Apply_RefusesWhenCaveSlackIsOccupied()
    {
        var exe = NewPristine();
        exe[Dc2MosaGrabSuppressPatch.Cave20Offset + 4] = 0x12;
        Assert.False(Dc2MosaGrabSuppressPatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2MosaGrabSuppressPatch.Apply(exe));
    }

    /// <summary>Real-data end-to-end pin, gated on <c>DINORAND_DC2_DIR</c>. Confirms the patcher recognizes
    /// the shipping pristine exe and the two hooks land on the exact player-Y-write pairs. Skipped when the
    /// exe can't be located or is already patched/different.</summary>
    [Fact]
    public void RealExe_Pristine_AppliesAndReverts()
    {
        var path = LocateDino2Exe();
        if (path is null) return;

        var bytes = File.ReadAllBytes(path);
        if (!Dc2MosaGrabSuppressPatch.IsRecognizedPristine(bytes)) return; // already patched/different → skip

        Assert.Equal(HookOriginal, bytes.Skip(Dc2MosaGrabSuppressPatch.Hook19Offset).Take(HookLen).ToArray());
        Assert.Equal(HookOriginal, bytes.Skip(Dc2MosaGrabSuppressPatch.Hook20Offset).Take(HookLen).ToArray());

        var pristine = (byte[])bytes.Clone();
        Dc2MosaGrabSuppressPatch.Apply(bytes);
        Assert.True(Dc2MosaGrabSuppressPatch.IsApplied(bytes));
        Assert.Equal(Cave19, bytes.Skip(Dc2MosaGrabSuppressPatch.Cave19Offset).Take(Dc2MosaGrabSuppressPatch.CaveLength).ToArray());
        Assert.Equal(Cave20, bytes.Skip(Dc2MosaGrabSuppressPatch.Cave20Offset).Take(Dc2MosaGrabSuppressPatch.CaveLength).ToArray());
        Dc2MosaGrabSuppressPatch.Restore(bytes);
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
