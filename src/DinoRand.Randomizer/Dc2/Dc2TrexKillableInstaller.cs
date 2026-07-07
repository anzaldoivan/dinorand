using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2TrexKillableInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2TrexKillableOutcome
{
    Applied,
    Restored,
    NotFound,
    UnrecognizedVersion,
    /// <summary>Already carried the lever (apply) or wasn't patched (restore) — nothing to write.</summary>
    Skipped,
}

/// <summary>
/// Install-time exe step for the killable-T-Rex lever (docs/decisions/dc2/enemies/DC2-TREX-KILLABLE-LEVER-PLAN.md):
/// writes the hook + code cave (<see cref="Dc2TrexKillablePatch"/>) into the game's <c>Dino2.exe</c>.
/// Mirrors <see cref="Dc2RaptorTierInstaller"/>: one-time pristine <c>.bak</c>, refuse unrecognized
/// builds, idempotent (skips if already applied), and restores only its own two slices so the other
/// Dino2.exe patches survive.
/// </summary>
public static class Dc2TrexKillableInstaller
{
    /// <summary>E10 Tyrannosaurus spawn TYPE (the species the lever targets).</summary>
    private const int TrexType = 0x03;

    /// <summary>True iff a run with <paramref name="config"/> can spawn a T-Rex — boss enemies in the
    /// weighted donor pool, or a fixed-species pin on T-Rex — or the user forced it on
    /// (<see cref="RandomizerConfig.Dc2MakeTrexKillable"/>). Used by the CLI and the UI to auto-apply
    /// the lever whenever the randomizer can inject a T-Rex. Gating on "spawnable" (not "actually
    /// placed") is sufficient because the patch is a harmless no-op when no injected T-Rex exists — its
    /// cave only fires for an E10 actor outside the two vanilla boss rooms, so a seed that happens to
    /// roll no T-Rex leaves behaviour identical to vanilla.</summary>
    public static bool WantedFor(RandomizerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Dc2MakeTrexKillable) return true;         // manual override (CLI --dc2-trex-killable)
        if (!config.RandomizeEnemies) return false;          // no cross-species swap ⇒ no injected T-Rex
        return config.Dc2EnemyMode == Dc2EnemyDistributionMode.Fixed
            ? config.Dc2FixedSpeciesType == TrexType          // pinned all-T-Rex run
            : config.IncludeDc2BossEnemies;                   // T-Rex is in the weighted boss pool
    }

    public static Dc2TrexKillableOutcome Apply(
        string installDir, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[trex-killable] no DC2 Data folder under {installDir}; skipped");
            return Dc2TrexKillableOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2TrexKillableOutcome ApplyToFile(
        string exePath, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[trex-killable] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2TrexKillableOutcome.NotFound;
        }
        var bytes = File.ReadAllBytes(exePath);

        if (restore)
        {
            if (!Dc2TrexKillablePatch.IsApplied(bytes))
            {
                log?.Invoke("[trex-killable] lever not present; restore skipped");
                return Dc2TrexKillableOutcome.Skipped;
            }
            Dc2TrexKillablePatch.Restore(bytes);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[trex-killable] hook + cave reverted to vanilla");
            return Dc2TrexKillableOutcome.Restored;
        }

        if (Dc2TrexKillablePatch.IsApplied(bytes))
        {
            log?.Invoke("[trex-killable] lever already applied; skipped");
            return Dc2TrexKillableOutcome.Skipped;
        }
        if (!Dc2TrexKillablePatch.IsRecognizedPristine(bytes))
        {
            log?.Invoke("[trex-killable] Dino2.exe is not the recognized rebirth build; exe patch skipped "
                + "(room-file enemy edits still apply)");
            return Dc2TrexKillableOutcome.UnrecognizedVersion;
        }

        // Capture the pristine original exactly once (shared .bak with the other exe installers).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        Dc2TrexKillablePatch.Apply(bytes);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[trex-killable] exe patched: injected T-Rex is now killable outside ST200/ST903 "
            + $"(backup: {Path.GetFileName(backupPath)})");
        return Dc2TrexKillableOutcome.Applied;
    }
}
