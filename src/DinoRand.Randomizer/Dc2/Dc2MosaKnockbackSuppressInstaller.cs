using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2MosaKnockbackSuppressInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2MosaKnockbackSuppressOutcome
{
    Applied,
    Restored,
    NotFound,
    UnrecognizedVersion,
    /// <summary>Already carried the lever (apply) or wasn't patched (restore) — nothing to write.</summary>
    Skipped,
}

/// <summary>
/// Install-time exe step for the Mosasaurus knockback-suppress lever
/// (docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md §8.5): writes the hook + code cave
/// (<see cref="Dc2MosaKnockbackSuppressPatch"/>) into the game's <c>Dino2.exe</c>. Mirrors
/// <see cref="Dc2MosaGrabSuppressInstaller"/>: one-time pristine <c>.bak</c>, refuse unrecognized builds,
/// idempotent (skips if already applied), and restores only its own two slices so the other Dino2.exe
/// patches survive.
/// </summary>
public static class Dc2MosaKnockbackSuppressInstaller
{
    /// <summary>E80 Mosasaurus spawn TYPE (the species the lever targets).</summary>
    private const int MosaType = 0x0a;

    /// <summary>True iff a run with <paramref name="config"/> can inject an E80 Mosasaurus into a land
    /// room — water-level swaps admit E80 to the weighted donor pool, or a fixed-species pin selects it —
    /// or the user forced it on (<see cref="RandomizerConfig.Dc2SuppressMosaKnockback"/>). Same predicate
    /// as the grab-suppress lever: both target an injected Mosasaurus in a non-native room, and the patch
    /// is a harmless no-op when no injected Mosasaurus exists — its cave only fires for a TYPE-0x0a
    /// attacker shoving the player outside the four native rooms.</summary>
    public static bool WantedFor(RandomizerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Dc2SuppressMosaKnockback) return true;    // manual override (CLI --dc2-mosa-no-knockback)
        if (!config.RandomizeEnemies) return false;          // no cross-species swap ⇒ no injected mosa
        return config.Dc2EnemyMode == Dc2EnemyDistributionMode.Fixed
            ? config.Dc2FixedSpeciesType == MosaType          // pinned all-Mosasaurus run
            : config.Dc2AllowWaterLevelEnemySwaps;            // E80 is in the weighted pool only with water swaps on
    }

    public static Dc2MosaKnockbackSuppressOutcome Apply(
        string installDir, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[mosa-no-knockback] no DC2 Data folder under {installDir}; skipped");
            return Dc2MosaKnockbackSuppressOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2MosaKnockbackSuppressOutcome ApplyToFile(
        string exePath, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[mosa-no-knockback] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2MosaKnockbackSuppressOutcome.NotFound;
        }
        var bytes = File.ReadAllBytes(exePath);

        if (restore)
        {
            if (!Dc2MosaKnockbackSuppressPatch.IsApplied(bytes))
            {
                log?.Invoke("[mosa-no-knockback] lever not present; restore skipped");
                return Dc2MosaKnockbackSuppressOutcome.Skipped;
            }
            Dc2MosaKnockbackSuppressPatch.Restore(bytes);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[mosa-no-knockback] hook + cave reverted to vanilla");
            return Dc2MosaKnockbackSuppressOutcome.Restored;
        }

        if (Dc2MosaKnockbackSuppressPatch.IsApplied(bytes))
        {
            log?.Invoke("[mosa-no-knockback] lever already applied; skipped");
            return Dc2MosaKnockbackSuppressOutcome.Skipped;
        }
        if (!Dc2MosaKnockbackSuppressPatch.IsRecognizedPristine(bytes))
        {
            log?.Invoke("[mosa-no-knockback] Dino2.exe is not the recognized rebirth build; exe patch skipped "
                + "(room-file enemy edits still apply)");
            return Dc2MosaKnockbackSuppressOutcome.UnrecognizedVersion;
        }

        // Capture the pristine original exactly once (shared .bak with the other exe installers).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        Dc2MosaKnockbackSuppressPatch.Apply(bytes);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[mosa-no-knockback] exe patched: injected Mosasaurus can no longer knock the player "
            + $"out of bounds outside ST700/702/703/704 (backup: {Path.GetFileName(backupPath)})");
        return Dc2MosaKnockbackSuppressOutcome.Applied;
    }
}
