using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2TriceratopsKillableInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2TriceratopsKillableOutcome
{
    Applied,
    Restored,
    NotFound,
    UnrecognizedVersion,
    /// <summary>Already carried the lever (apply) or wasn't patched (restore) — nothing to write.</summary>
    Skipped,
}

/// <summary>
/// Install-time exe step for the killable-Triceratops lever
/// (docs/decisions/dc2/crash-rcas/DC2-ST001-TRICERATOPS-WAVE-DEDICATED-BASE-CRASH-RCA.md §7b): remaps
/// E70's out-of-range death animation index (<see cref="Dc2TriceratopsKillablePatch"/>) so an injected
/// Triceratops dies with a real animation instead of crashing. Mirrors
/// <see cref="Dc2TrexKillableInstaller"/>: one-time pristine <c>.bak</c>, refuse unrecognized builds,
/// idempotent, and restores only its own byte so the other Dino2.exe patches survive.
/// </summary>
public static class Dc2TriceratopsKillableInstaller
{
    /// <summary>E70 Triceratops spawn TYPE (the setpiece species the lever targets).</summary>
    private const int TriceratopsType = 0x09;

    /// <summary>True iff a run with <paramref name="config"/> can inject a Triceratops — setpiece
    /// enemies in the weighted donor pool, or a fixed-species pin on Triceratops — or the user forced it
    /// on (<see cref="RandomizerConfig.Dc2MakeTriceratopsKillable"/>). Used by the CLI and the UI to
    /// auto-apply the lever whenever the randomizer can inject an E70, so it doesn't crash on death.
    /// Gating on "spawnable" (not "actually placed") is sufficient: the patch is a harmless one-byte
    /// no-op when no injected Triceratops exists — the remapped instruction only ever runs in E70's own
    /// death handler, so a seed that rolls no Triceratops leaves behaviour identical to vanilla.</summary>
    public static bool WantedFor(RandomizerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Dc2MakeTriceratopsKillable) return true;  // manual override (--dc2-triceratops-killable)
        if (!config.RandomizeEnemies) return false;          // no cross-species swap ⇒ no injected E70
        return config.Dc2EnemyMode == Dc2EnemyDistributionMode.Fixed
            ? config.Dc2FixedSpeciesType == TriceratopsType   // pinned all-Triceratops run
            : config.IncludeDc2SetpieceEnemies;               // E70 is in the weighted setpiece pool
    }

    public static Dc2TriceratopsKillableOutcome Apply(
        string installDir, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[triceratops-killable] no DC2 Data folder under {installDir}; skipped");
            return Dc2TriceratopsKillableOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2TriceratopsKillableOutcome ApplyToFile(
        string exePath, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[triceratops-killable] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2TriceratopsKillableOutcome.NotFound;
        }
        var bytes = File.ReadAllBytes(exePath);

        if (restore)
        {
            if (!Dc2TriceratopsKillablePatch.IsApplied(bytes))
            {
                log?.Invoke("[triceratops-killable] lever not present; restore skipped");
                return Dc2TriceratopsKillableOutcome.Skipped;
            }
            Dc2TriceratopsKillablePatch.Restore(bytes);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[triceratops-killable] death-anim remap reverted to vanilla");
            return Dc2TriceratopsKillableOutcome.Restored;
        }

        if (Dc2TriceratopsKillablePatch.IsApplied(bytes))
        {
            log?.Invoke("[triceratops-killable] lever already applied; skipped");
            return Dc2TriceratopsKillableOutcome.Skipped;
        }
        if (!Dc2TriceratopsKillablePatch.IsRecognizedPristine(bytes))
        {
            log?.Invoke("[triceratops-killable] Dino2.exe is not the recognized rebirth build; exe patch "
                + "skipped (room-file enemy edits still apply)");
            return Dc2TriceratopsKillableOutcome.UnrecognizedVersion;
        }

        // Capture the pristine original exactly once (shared .bak with the other exe installers).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        Dc2TriceratopsKillablePatch.Apply(bytes);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[triceratops-killable] exe patched: injected Triceratops now plays its death "
            + $"animation instead of crashing (backup: {Path.GetFileName(backupPath)})");
        return Dc2TriceratopsKillableOutcome.Applied;
    }
}
