using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2PuzzleCodeScrambleInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2PuzzleCodeOutcome
{
    /// <summary>The elevator-code scramble was written (pristine backup captured first).</summary>
    Applied,
    /// <summary>Restore: the 8 candidate slots rewritten to the vanilla codes.</summary>
    Restored,
    /// <summary>No <c>Dino2.exe</c> (or no DC2 Data dir) was found.</summary>
    NotFound,
    /// <summary>The exe is not the recognized build; left untouched.</summary>
    UnrecognizedVersion,
}

/// <summary>
/// Install-time step for <c>--dc2-scramble-puzzle-codes</c> (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §3, K108):
/// applies <see cref="Dc2ElevatorCodePatch"/> to the game's <c>Dino2.exe</c>. Mirrors
/// <see cref="Dc2ShopShuffleInstaller"/>: one-time pristine <c>.bak</c> backup, refuse
/// unrecognized builds, idempotent-safe (codes are derived from the seed, never from the current
/// bytes, so re-running never compounds). Restore rewrites only the 8 digit-byte slots
/// (<see cref="Dc2ElevatorCodePatch.RestoreCanonical"/>), leaving other exe patches intact.
/// Displayed==checked is automatic (single runtime copy at <c>scene_mgr+0x1204</c>) — no document work.
/// </summary>
public static class Dc2PuzzleCodeScrambleInstaller
{
    /// <summary>
    /// Resolve the DC2 install at <paramref name="installDir"/> and scramble (or, with
    /// <paramref name="restore"/>, un-scramble) the exe's elevator-code candidates.
    /// </summary>
    public static Dc2PuzzleCodeOutcome Apply(string installDir, int seed, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[puzzle-codes] no DC2 Data folder under {installDir}; skipped");
            return Dc2PuzzleCodeOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), seed, restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2PuzzleCodeOutcome ApplyToFile(string exePath, int seed, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[puzzle-codes] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2PuzzleCodeOutcome.NotFound;
        }

        var bytes = File.ReadAllBytes(exePath);
        try
        {
            Dc2ElevatorCodePatch.Validate(bytes);
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"[puzzle-codes] {ex.Message}");
            return Dc2PuzzleCodeOutcome.UnrecognizedVersion;
        }

        if (restore)
        {
            Dc2ElevatorCodePatch.RestoreCanonical(bytes);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[puzzle-codes] elevator-code candidates restored to vanilla");
            return Dc2PuzzleCodeOutcome.Restored;
        }

        // Capture the pristine original exactly once (same contract as the skin/bgm/shop installer).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        var patchPlan = Dc2ExecutablePatchPlanner.PlanElevatorCodes(seed);
        var entries = Dc2ElevatorCodePatch.ApplyPlan(bytes, patchPlan.ElevatorCodes!);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[puzzle-codes] seed {seed}: 8 elevator-code candidates rewritten (backup: {Path.GetFileName(backupPath)}); read the in-game file for the rolled code");
        foreach (var e in entries)
            log?.Invoke($"[puzzle-codes]   slot {e.Slot}: {e.OldCode} -> {e.NewCode}");
        return Dc2PuzzleCodeOutcome.Applied;
    }
}
