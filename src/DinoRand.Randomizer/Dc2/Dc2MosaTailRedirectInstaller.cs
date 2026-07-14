using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2MosaTailRedirectInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2MosaTailRedirectOutcome
{
    Applied,
    Restored,
    NotFound,
    UnrecognizedVersion,
    /// <summary>Already carried the lever (apply) or wasn't patched (restore) — nothing to write.</summary>
    Skipped,
}

/// <summary>
/// Install-time exe step for the Mosasaurus tail-redirect lever
/// (docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md §9): writes the hook + code cave
/// (<see cref="Dc2MosaTailRedirectPatch"/>) into the game's <c>Dino2.exe</c>. Mirrors
/// <see cref="Dc2MosaGrabSuppressInstaller"/>: one-time pristine <c>.bak</c>, refuse unrecognized builds,
/// idempotent (skips if already applied), and restores only its own two slices so the other Dino2.exe
/// patches survive.
/// </summary>
public static class Dc2MosaTailRedirectInstaller
{
    /// <summary>E80 Mosasaurus spawn TYPE (the species the lever targets).</summary>
    private const int MosaType = 0x0a;

    /// <summary>True iff a run with <paramref name="config"/> can inject an E80 Mosasaurus into a land
    /// room — water-level swaps admit E80 to the weighted donor pool, or a fixed-species pin selects it —
    /// or the user forced it on (<see cref="RandomizerConfig.Dc2RedirectMosaTail"/>). Same predicate as the
    /// grab/knockback levers. Gating on "spawnable" (not "actually placed") is sufficient because the patch
    /// is a harmless no-op when no injected Mosasaurus exists — its cave only rewrites the pattern of a
    /// TYPE-0x0a actor outside the four native rooms, so a seed that rolls no Mosasaurus leaves behaviour
    /// identical to vanilla.</summary>
    public static bool WantedFor(RandomizerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Dc2RedirectMosaTail) return true;         // manual override (CLI --dc2-mosa-tail-to-bite)
        if (!config.RandomizeEnemies) return false;          // no cross-species swap ⇒ no injected mosa
        return config.Dc2EnemyMode == Dc2EnemyDistributionMode.Fixed
            ? config.Dc2FixedSpeciesType == MosaType          // pinned all-Mosasaurus run
            : config.Dc2AllowWaterLevelEnemySwaps;            // E80 is in the weighted pool only with water swaps on
    }

    public static Dc2MosaTailRedirectOutcome Apply(
        string installDir, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[mosa-tail-to-bite] no DC2 Data folder under {installDir}; skipped");
            return Dc2MosaTailRedirectOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2MosaTailRedirectOutcome ApplyToFile(
        string exePath, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[mosa-tail-to-bite] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2MosaTailRedirectOutcome.NotFound;
        }
        var bytes = File.ReadAllBytes(exePath);

        if (restore)
        {
            if (!Dc2MosaTailRedirectPatch.IsApplied(bytes))
            {
                log?.Invoke("[mosa-tail-to-bite] lever not present; restore skipped");
                return Dc2MosaTailRedirectOutcome.Skipped;
            }
            Dc2MosaTailRedirectPatch.Restore(bytes);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[mosa-tail-to-bite] hook + cave reverted to vanilla");
            return Dc2MosaTailRedirectOutcome.Restored;
        }

        if (Dc2MosaTailRedirectPatch.IsApplied(bytes))
        {
            log?.Invoke("[mosa-tail-to-bite] lever already applied; skipped");
            return Dc2MosaTailRedirectOutcome.Skipped;
        }
        if (!Dc2MosaTailRedirectPatch.IsRecognizedPristine(bytes))
        {
            log?.Invoke("[mosa-tail-to-bite] Dino2.exe is not the recognized rebirth build; exe patch skipped "
                + "(room-file enemy edits still apply)");
            return Dc2MosaTailRedirectOutcome.UnrecognizedVersion;
        }

        // Capture the pristine original exactly once (shared .bak with the other exe installers).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        Dc2MosaTailRedirectPatch.Apply(bytes);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[mosa-tail-to-bite] exe patched: injected Mosasaurus does the bite instead of the "
            + $"OOB tail strike outside ST700/702/703/704 (backup: {Path.GetFileName(backupPath)})");
        return Dc2MosaTailRedirectOutcome.Applied;
    }
}
