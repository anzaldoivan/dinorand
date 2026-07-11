using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>Auto-trigger logic for the killable-Triceratops lever: it must turn on exactly when the run
/// can inject a Triceratops (setpiece enemies / fixed-Triceratops pin) or the user forces it, so the
/// frontend applies it without a separate toggle.</summary>
public class Dc2TriceratopsKillableInstallerWantedForTests
{
    private static RandomizerConfig Cfg(bool enemies, Dc2EnemyDistributionMode mode,
        int? fixedType = null, bool setpiece = false, bool force = false) => new()
    {
        RandomizeEnemies = enemies,
        Dc2EnemyMode = mode,
        Dc2FixedSpeciesType = fixedType,
        IncludeDc2SetpieceEnemies = setpiece,
        Dc2MakeTriceratopsKillable = force,
    };

    [Fact] public void ManualFlag_ForcesOn_EvenWithEnemiesOff() =>
        Assert.True(Dc2TriceratopsKillableInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, force: true)));

    [Fact] public void EnemiesOff_NoInjectedTriceratops_False() =>
        Assert.False(Dc2TriceratopsKillableInstaller.WantedFor(Cfg(false, Dc2EnemyDistributionMode.Weighted, setpiece: true)));

    [Fact] public void Weighted_SetpieceIncluded_True() =>
        Assert.True(Dc2TriceratopsKillableInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, setpiece: true)));

    [Fact] public void Weighted_SetpieceExcluded_False() =>
        Assert.False(Dc2TriceratopsKillableInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Weighted, setpiece: false)));

    [Fact] public void Fixed_TriceratopsPin_True() => // E70 = TYPE 0x09
        Assert.True(Dc2TriceratopsKillableInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x09)));

    [Fact] public void Fixed_OtherSetpiecePin_False() => // Gigano 0x06 is a setpiece but not a Triceratops
        Assert.False(Dc2TriceratopsKillableInstaller.WantedFor(Cfg(true, Dc2EnemyDistributionMode.Fixed, fixedType: 0x06, setpiece: true)));
}

/// <summary>
/// Unit tests for <see cref="Dc2TriceratopsKillablePatch"/> — the single-byte Dino2.exe remap that
/// makes a randomizer-injected Triceratops (E70, spawn TYPE 0x09) killable without crashing. RE +
/// decision record: docs/decisions/dc2/crash-rcas/DC2-ST001-TRICERATOPS-WAVE-DEDICATED-BASE-CRASH-RCA.md §7b.
///
/// <para><b>Mechanism.</b> E70 is a setpiece model with a short animation set. Its death path
/// (per-species tick 0x43cf10 → state 3 handler 0x43d3d0 → sub-state 1|2 death-entry 0x43ea37) binds
/// animation-package index <b>8</b> (<c>push 8</c> at VA 0x43ea40), which is one past the highest valid
/// index. <c>0x48e050 BindModelPackage</c> indexes <c>[[actor+0x88]+pkgRef*4]</c> with no bounds check,
/// so index 8 reads a garbage descriptor → <c>[actor+0x9c]</c> = wild ptr → AV at 0x48dff9. The lever
/// remaps that immediate <b>8 → 7</b>, the same valid death clip the sub-state-3 death-entry (0x43eb5d
/// <c>push 7</c>) and an alive state (0x43e93d) already bind.</para>
///
/// <para>The remapped byte and its anchor context are independently transcribed here so a regression in
/// the patcher's offsets fails the test rather than silently shipping.</para>
/// </summary>
public class Dc2TriceratopsKillablePatchTests
{
    // Death-entry 0x43ea37: inc cl (FE C1); push 8 (6A 08); push esi (56), at VA 0x43ea3d.
    private static readonly byte[] AnchorOriginal = { 0xFE, 0xC1, 0x6A, 0x08, 0x56 };
    private static readonly byte[] AnchorPatched  = { 0xFE, 0xC1, 0x6A, 0x07, 0x56 };
    private const int AnchorLen = 5;

    /// <summary>A synthetic buffer that looks exactly like the pristine rebirth Dino2.exe to the
    /// recognizer: correct length, original death-entry bytes at their offset.</summary>
    private static byte[] NewPristine()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        AnchorOriginal.CopyTo(exe.AsSpan(Dc2TriceratopsKillablePatch.AnchorOffset));
        return exe;
    }

    /// <summary>Offset regression pin: the remapped byte is the sub-1|2 death-entry pkgRef at VA
    /// 0x43ea40 (foff 0x3EA40), inside the anchor at VA 0x43ea3d (foff 0x3EA3D).</summary>
    [Fact]
    public void Offsets_ArePinnedToTheDeathEntry()
    {
        Assert.Equal(0x3EA3D, Dc2TriceratopsKillablePatch.AnchorOffset);
        Assert.Equal(0x3EA40, Dc2TriceratopsKillablePatch.RemapOffset);
    }

    [Fact]
    public void Pristine_IsRecognized_AndNotApplied()
    {
        var exe = NewPristine();
        Assert.True(Dc2TriceratopsKillablePatch.IsRecognizedPristine(exe));
        Assert.False(Dc2TriceratopsKillablePatch.IsApplied(exe));
    }

    [Fact]
    public void Apply_RemapsExactlyOneByte_8To7()
    {
        var before = NewPristine();
        var after = NewPristine();
        Dc2TriceratopsKillablePatch.Apply(after);

        Assert.Equal(0x07, after[Dc2TriceratopsKillablePatch.RemapOffset]);
        Assert.Equal(AnchorPatched, after.Skip(Dc2TriceratopsKillablePatch.AnchorOffset).Take(AnchorLen).ToArray());
        Assert.True(Dc2TriceratopsKillablePatch.IsApplied(after));

        for (int i = 0; i < after.Length; i++)
            if (i != Dc2TriceratopsKillablePatch.RemapOffset)
                Assert.True(before[i] == after[i], $"byte at 0x{i:x} changed unexpectedly");
    }

    [Fact]
    public void Restore_ReversesApplyExactly()
    {
        var pristine = NewPristine();
        var exe = NewPristine();
        Dc2TriceratopsKillablePatch.Apply(exe);
        Dc2TriceratopsKillablePatch.Restore(exe);
        Assert.Equal(pristine, exe);
        Assert.True(Dc2TriceratopsKillablePatch.IsRecognizedPristine(exe));
    }

    [Fact]
    public void Restore_OnUnpatchedExe_IsNoOp()
    {
        var exe = NewPristine();
        var snapshot = (byte[])exe.Clone();
        Dc2TriceratopsKillablePatch.Restore(exe);
        Assert.Equal(snapshot, exe);
    }

    [Fact]
    public void Apply_IsNotDoubleApplied()
    {
        var exe = NewPristine();
        Dc2TriceratopsKillablePatch.Apply(exe);
        Assert.True(Dc2TriceratopsKillablePatch.IsApplied(exe));
        Assert.False(Dc2TriceratopsKillablePatch.IsRecognizedPristine(exe));
        Assert.Throws<InvalidOperationException>(() => Dc2TriceratopsKillablePatch.Apply(exe));
    }

    [Fact]
    public void Apply_RefusesWrongLength()
    {
        var tooShort = new byte[Dc2WpGatePatch.ExpectedLength - 1];
        Assert.False(Dc2TriceratopsKillablePatch.IsRecognizedPristine(tooShort));
        Assert.Throws<InvalidOperationException>(() => Dc2TriceratopsKillablePatch.Apply(tooShort));
    }

    [Fact]
    public void Apply_RefusesForeignAnchor_AndLeavesBufferUntouched()
    {
        var foreign = NewPristine();
        foreign[Dc2TriceratopsKillablePatch.RemapOffset] = 0x05; // not the expected push-8 pkgRef
        var snapshot = (byte[])foreign.Clone();

        Assert.False(Dc2TriceratopsKillablePatch.IsRecognizedPristine(foreign));
        Assert.Throws<InvalidOperationException>(() => Dc2TriceratopsKillablePatch.Apply(foreign));
        Assert.Equal(snapshot, foreign);
    }

    /// <summary>Real-data end-to-end pin, gated on <c>DINORAND_DC2_DIR</c> (the rebirth <c>Data</c> dir;
    /// <c>Dino2.exe</c> is its sibling). Confirms the patcher recognizes the shipping pristine exe, and
    /// that the RE claim holds against the real bytes: the sub-1|2 death-entry binds pkgRef <b>8</b>
    /// (the crash) while the sub-3 death-entry (VA 0x43eb5d) binds pkgRef <b>7</b> — the valid death clip
    /// the lever remaps toward. Skipped when the env var is unset or the exe is already patched.</summary>
    [Fact]
    public void RealExe_DeathEntryRemap_MatchesDecode()
    {
        var dir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        // .env DINORAND_DC2_DIR points at rebirth\Data; Dino2.exe sits one level up.
        var path = Path.Combine(dir, "..", "Dino2.exe");
        if (!File.Exists(path)) path = Path.Combine(dir, "rebirth", "Dino2.exe");
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        if (!Dc2TriceratopsKillablePatch.IsRecognizedPristine(bytes)) return; // already patched/different → skip

        // Decode invariants against the real exe.
        Assert.Equal(0x08, bytes[Dc2TriceratopsKillablePatch.RemapOffset]);        // sub-1|2 death-entry: push 8 (out of range)
        Assert.Equal(0x07, bytes[0x43eb5e - 0x400000]);                            // sub-3 death-entry:  push 7 (valid death clip)
        Assert.Equal(AnchorOriginal, bytes.Skip(Dc2TriceratopsKillablePatch.AnchorOffset).Take(AnchorLen).ToArray());

        var pristine = (byte[])bytes.Clone();
        Dc2TriceratopsKillablePatch.Apply(bytes);
        Assert.True(Dc2TriceratopsKillablePatch.IsApplied(bytes));
        Assert.Equal(0x07, bytes[Dc2TriceratopsKillablePatch.RemapOffset]);        // now both death branches bind clip 7
        Dc2TriceratopsKillablePatch.Restore(bytes);
        Assert.Equal(pristine, bytes);
    }
}
