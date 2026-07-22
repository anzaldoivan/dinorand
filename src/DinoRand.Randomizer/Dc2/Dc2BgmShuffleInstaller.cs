using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2BgmShuffleInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2BgmShuffleOutcome
{
    /// <summary>The music-table shuffle was written (pristine backup captured first).</summary>
    Applied,
    /// <summary>Restore: the slice was rewritten to canonical order.</summary>
    Restored,
    /// <summary>No <c>Dino2.exe</c> (or no DC2 Data dir) was found.</summary>
    NotFound,
    /// <summary>The exe is not the recognized build; left untouched.</summary>
    UnrecognizedVersion,
}

/// <summary>
/// Install-time step for <c>--dc2-shuffle-bgm</c> (docs/decisions/dc2/audio/DC2-BGM-RANDO-PLAN.md I2;
/// live-witnessed in-game 2026-07-05, I3): applies
/// <see cref="Dc2MusicTablePatch"/> to the game's <c>Dino2.exe</c>. Mirrors
/// <see cref="Dc2CharacterSkinInstaller"/>: one-time pristine <c>.bak</c> backup, refuse
/// unrecognized builds, idempotent-safe (the shuffle is computed from the canonical table, so
/// re-running with another seed never compounds). The like-class grouping is derived from the
/// install's own <c>Data\</c> containers (<see cref="Dc2MusicContainer.ReadTrackIndexKey"/>);
/// table entries with no on-disk file (ME_2000/MS_0402/MS_0501, vestigial) are never moved.
/// Restore rewrites only the music slice (<see cref="Dc2MusicTablePatch.RestoreCanonical"/>),
/// leaving other exe patches (WP-gate, …) intact.
/// </summary>
public static class Dc2BgmShuffleInstaller
{
    /// <summary>
    /// Resolve the DC2 install at <paramref name="installDir"/> and shuffle (or, with
    /// <paramref name="restore"/>, un-shuffle) the exe's music table.
    /// </summary>
    public static Dc2BgmShuffleOutcome Apply(string installDir, int seed, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[bgm-shuffle] no DC2 Data folder under {installDir}; skipped");
            return Dc2BgmShuffleOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFiles(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), dataDir, seed, restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2BgmShuffleOutcome ApplyToFiles(string exePath, string dataDir, int seed, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[bgm-shuffle] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2BgmShuffleOutcome.NotFound;
        }

        var bytes = File.ReadAllBytes(exePath);
        try
        {
            Dc2MusicTablePatch.Validate(bytes);
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"[bgm-shuffle] {ex.Message}");
            return Dc2BgmShuffleOutcome.UnrecognizedVersion;
        }

        if (restore)
        {
            Dc2MusicTablePatch.RestoreCanonical(bytes);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[bgm-shuffle] music table restored to canonical order");
            return Dc2BgmShuffleOutcome.Restored;
        }

        // Like-class grouping from the install's own containers, composited with the BGM mood tag
        // (docs/decisions/cross/BGM-RANDO-PLAN.md): the class key is "<tag>|<trackIndexKey>", so a slot only
        // swaps with another of the SAME mood AND the same track-index set. With the v1 all-'all' manifest the
        // tag prefix is constant, so this is byte-equivalent to the prior track-index-only grouping until mood
        // tags are authored (data-only refinement). Unparseable/missing files get no class and never move.
        var manifest = Bgm.BgmManifest.LoadDefault("dc2");
        var classOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Dc2MusicTablePatch.CanonicalNames)
        {
            string path = Path.Combine(dataDir, name);
            if (File.Exists(path) && Dc2MusicContainer.ReadTrackIndexKey(path) is { } key)
                classOf[name] = $"{manifest.TagOf(name)}|{key}";
        }

        // Capture the pristine original exactly once (same contract as the skin/wpgate installer).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        var patchPlan = Dc2ExecutablePatchPlanner.PlanMusic(seed, classOf);
        var entries = Dc2MusicTablePatch.ApplyPlan(bytes, patchPlan.MusicNames!);
        File.WriteAllBytes(exePath, bytes);
        int moved = entries.Count(e => e.OldName != e.NewName);
        log?.Invoke($"[bgm-shuffle] seed {seed}: {moved}/{entries.Length} music slots rerouted (backup: {Path.GetFileName(backupPath)})");
        foreach (var e in entries.Where(e => e.OldName != e.NewName))
            log?.Invoke($"[bgm-shuffle]   slot {e.Slot}: {e.OldName} -> {e.NewName}");
        return Dc2BgmShuffleOutcome.Applied;
    }
}
