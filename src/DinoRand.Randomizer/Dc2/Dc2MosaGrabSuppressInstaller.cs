using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2MosaGrabSuppressInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2MosaGrabSuppressOutcome
{
    Applied,
    Restored,
    NotFound,
    UnrecognizedVersion,
    /// <summary>Already carried the lever (apply) or wasn't patched (restore) — nothing to write.</summary>
    Skipped,
}

/// <summary>
/// Install-time exe step for the Mosasaurus grab-suppress lever
/// (docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md): writes the hooks + code caves
/// (<see cref="Dc2MosaGrabSuppressPatch"/>) into the game's <c>Dino2.exe</c>. Mirrors
/// <see cref="Dc2TrexKillableInstaller"/>: one-time pristine <c>.bak</c>, refuse unrecognized builds,
/// idempotent (skips if already applied), and restores only its own four slices so the other Dino2.exe
/// patches survive.
/// </summary>
public static class Dc2MosaGrabSuppressInstaller
{
    /// <summary>E80 Mosasaurus spawn TYPE (the species the lever targets).</summary>
    private const int MosaType = 0x0a;

    /// <summary>True iff a run with <paramref name="config"/> can inject an E80 Mosasaurus into a land
    /// room — water-level swaps admit E80 to the weighted donor pool, or a fixed-species pin selects it —
    /// or the user forced it on (<see cref="RandomizerConfig.Dc2SuppressMosaGrab"/>). Used by the CLI and
    /// UI to auto-apply the lever whenever the randomizer can inject a Mosasaurus. Gating on "spawnable"
    /// (not "actually placed") is sufficient because the patch is a harmless no-op when no injected
    /// Mosasaurus exists — its caves only fire for a TYPE-0x0a actor outside the four native rooms, so a
    /// seed that rolls no Mosasaurus leaves behaviour identical to vanilla.</summary>
    public static bool WantedFor(RandomizerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Dc2SuppressMosaGrab) return true;         // manual override (CLI --dc2-mosa-no-grab)
        if (!config.RandomizeEnemies) return false;          // no cross-species swap ⇒ no injected mosa
        return config.Dc2EnemyMode == Dc2EnemyDistributionMode.Fixed
            ? config.Dc2FixedSpeciesType == MosaType          // pinned all-Mosasaurus run
            : config.Dc2AllowWaterLevelEnemySwaps;            // E80 is in the weighted pool only with water swaps on
    }

    public static Dc2MosaGrabSuppressOutcome Apply(
        string installDir, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[mosa-no-grab] no DC2 Data folder under {installDir}; skipped");
            return Dc2MosaGrabSuppressOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2MosaGrabSuppressOutcome ApplyToFile(
        string exePath, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[mosa-no-grab] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2MosaGrabSuppressOutcome.NotFound;
        }
        var bytes = File.ReadAllBytes(exePath);

        if (restore)
        {
            if (!Dc2MosaGrabSuppressPatch.IsApplied(bytes))
            {
                log?.Invoke("[mosa-no-grab] lever not present; restore skipped");
                return Dc2MosaGrabSuppressOutcome.Skipped;
            }
            Dc2MosaGrabSuppressPatch.Restore(bytes);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[mosa-no-grab] hooks + caves reverted to vanilla");
            return Dc2MosaGrabSuppressOutcome.Restored;
        }

        if (Dc2MosaGrabSuppressPatch.IsApplied(bytes))
        {
            log?.Invoke("[mosa-no-grab] lever already applied; skipped");
            return Dc2MosaGrabSuppressOutcome.Skipped;
        }
        if (!Dc2MosaGrabSuppressPatch.IsRecognizedPristine(bytes))
        {
            log?.Invoke("[mosa-no-grab] Dino2.exe is not the recognized rebirth build; exe patch skipped "
                + "(room-file enemy edits still apply)");
            return Dc2MosaGrabSuppressOutcome.UnrecognizedVersion;
        }

        // Capture the pristine original exactly once (shared .bak with the other exe installers).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        Dc2MosaGrabSuppressPatch.Apply(bytes);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[mosa-no-grab] exe patched: injected Mosasaurus grab stays in-bounds outside "
            + $"ST700/702/703/704 (backup: {Path.GetFileName(backupPath)})");
        return Dc2MosaGrabSuppressOutcome.Applied;
    }
}
