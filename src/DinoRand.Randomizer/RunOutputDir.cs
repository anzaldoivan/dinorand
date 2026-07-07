namespace DinoRand.Randomizer;

/// <summary>
/// Per-run hygiene for the randomizer's working output dir. The CLI/App reuse a fixed mod dir
/// (e.g. <c>mod_dinorand</c> / <c>mod_dinorand_dc2</c>) across runs; without cleaning it, stale room
/// files from an earlier run/experiment survive and — because the installer historically overlaid every
/// <c>*.dat</c> present — got installed alongside the current run's output, shipping a broken seed
/// (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md). Clearing stale room files before the passes write makes a
/// reused dir contain only the current run's output.
/// </summary>
public static class RunOutputDir
{
    /// <summary>Remove stale <c>*.dat</c> room files from <paramref name="outputDir"/> so a reused dir
    /// starts clean. Leaves the dir (and any non-<c>.dat</c> artifacts) in place; a no-op if the dir does
    /// not exist yet. Returns the number of files removed.</summary>
    public static int ClearStaleRoomFiles(string outputDir)
    {
        if (!Directory.Exists(outputDir)) return 0;
        int removed = 0;
        // DC2 rooms are uppercase ST*.DAT; EnumerateFiles patterns are case-sensitive on Linux/WSL.
        var opts = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var path in Directory.EnumerateFiles(outputDir, "*.dat", opts))
        {
            try { File.Delete(path); removed++; }
            catch (IOException) { /* locked file: leave it; scoped install still ignores unlisted files */ }
        }
        return removed;
    }
}
