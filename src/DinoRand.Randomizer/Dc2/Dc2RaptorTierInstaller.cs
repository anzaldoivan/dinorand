using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2RaptorTierInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2RaptorTierPatchOutcome
{
    Applied,
    Restored,
    NotFound,
    UnrecognizedVersion,
    /// <summary>Nothing to write (all tier weights 0 AND vanilla combo threshold).</summary>
    Skipped,
}

/// <summary>
/// Install-time exe step for the raptor tier feature (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §4): writes the
/// seeded weighted wave pair table (<see cref="Dc2RaptorPatch.ApplyPairTable"/>) and the
/// blue-raptor combo threshold (<see cref="Dc2RaptorPatch.ApplyComboThreshold"/>) into the game's
/// <c>Dino2.exe</c>. Mirrors <see cref="Dc2BgmShuffleInstaller"/>: one-time pristine <c>.bak</c>,
/// refuse unrecognized builds, idempotent (both patches are absolute rewrites computed from the
/// seed/config, never compounding). Restore rewrites only these two slices from the vanilla image.
/// </summary>
public static class Dc2RaptorTierInstaller
{
    public static Dc2RaptorTierPatchOutcome Apply(
        string installDir, Seed seed, RandomizerConfig config, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[raptor-tiers] no DC2 Data folder under {installDir}; skipped");
            return Dc2RaptorTierPatchOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), seed, config, restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2RaptorTierPatchOutcome ApplyToFile(
        string exePath, Seed seed, RandomizerConfig config, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[raptor-tiers] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2RaptorTierPatchOutcome.NotFound;
        }
        var bytes = File.ReadAllBytes(exePath);

        if (restore)
        {
            if (!Dc2RaptorPatch.IsPairTableRecognized(bytes) || !Dc2RaptorPatch.IsComboSiteRecognized(bytes))
            {
                log?.Invoke("[raptor-tiers] exe is not the recognized build; restore skipped");
                return Dc2RaptorTierPatchOutcome.UnrecognizedVersion;
            }
            Dc2RaptorPatch.PairTableVanilla.CopyTo(bytes.AsSpan(Dc2RaptorPatch.PairTableOffset));
            Dc2RaptorPatch.ApplyComboThreshold(bytes, Dc2RaptorPatch.VanillaComboThreshold);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[raptor-tiers] pair table + combo threshold restored to vanilla");
            return Dc2RaptorTierPatchOutcome.Restored;
        }

        var tiers = Dc2RaptorTierTable.LoadEmbedded();
        var weights = tiers.EffectiveWeights(config.Dc2RaptorTierWeights);
        // RoomTier mode consumes no RNG (identity table, seed-stable); MixedTiers draws seeded.
        var pairs = config.Dc2RandomizeRaptorTiers
            ? Dc2RaptorTierPlanner.PlanPairTable(seed.RngFor("Dc2RaptorTiers:pairtable"), weights,
                                                 config.Dc2RaptorColourMode, tiers)
            : null;
        bool patchCombo = config.Dc2BlueRaptorComboThreshold != Dc2RaptorPatch.VanillaComboThreshold;
        if (pairs is null && !patchCombo)
        {
            log?.Invoke("[raptor-tiers] nothing to patch (weights empty / vanilla combo threshold)");
            return Dc2RaptorTierPatchOutcome.Skipped;
        }

        if (!Dc2RaptorPatch.IsPairTableRecognized(bytes) || !Dc2RaptorPatch.IsComboSiteRecognized(bytes))
        {
            log?.Invoke("[raptor-tiers] Dino2.exe is not the recognized rebirth build; exe patch skipped "
                + "(room-file tier edits still apply)");
            return Dc2RaptorTierPatchOutcome.UnrecognizedVersion;
        }

        // Capture the pristine original exactly once (same contract as the skin/bgm installers).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        if (pairs is not null)
            Dc2RaptorPatch.ApplyPairTable(bytes, pairs);
        Dc2RaptorPatch.ApplyComboThreshold(bytes, config.Dc2BlueRaptorComboThreshold);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[raptor-tiers] exe patched: "
            + (pairs is null ? "pair table vanilla" : $"pair table ← [{string.Join(" ", pairs)}]")
            + $", blue-raptor combo threshold = {config.Dc2BlueRaptorComboThreshold}"
            + $" (backup: {Path.GetFileName(backupPath)})");
        return Dc2RaptorTierPatchOutcome.Applied;
    }
}
