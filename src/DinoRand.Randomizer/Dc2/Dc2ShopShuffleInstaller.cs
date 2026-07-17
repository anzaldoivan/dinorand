using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2ShopShuffleInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2ShopShuffleOutcome
{
    /// <summary>The shop price/stock shuffle was written (pristine backup captured first).</summary>
    Applied,
    /// <summary>Restore: prices and masks rewritten to canonical retail values.</summary>
    Restored,
    /// <summary>No <c>Dino2.exe</c> (or no DC2 Data dir) was found.</summary>
    NotFound,
    /// <summary>The exe is not the recognized build; left untouched.</summary>
    UnrecognizedVersion,
}

/// <summary>
/// Install-time step for <c>--dc2-shuffle-shop</c> (docs/decisions/dc2/shop/DC2-SHOP-RANDO-PLAN.md I1+I2):
/// applies <see cref="Dc2ShopTablePatch"/> to the game's <c>Dino2.exe</c>. Mirrors
/// <see cref="Dc2BgmShuffleInstaller"/>: one-time pristine <c>.bak</c> backup, refuse
/// unrecognized builds, idempotent-safe (computed from canonical, re-running never compounds).
/// Restore rewrites only the shop prices/masks, leaving other exe patches intact.
/// </summary>
public static class Dc2ShopShuffleInstaller
{
    /// <summary>
    /// Resolve the DC2 install at <paramref name="installDir"/> and shuffle (or, with
    /// <paramref name="restore"/>, un-shuffle) the exe's shop tables.
    /// </summary>
    public static Dc2ShopShuffleOutcome Apply(string installDir, int seed, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[shop-shuffle] no DC2 Data folder under {installDir}; skipped");
            return Dc2ShopShuffleOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), seed, restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2ShopShuffleOutcome ApplyToFile(string exePath, int seed, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[shop-shuffle] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2ShopShuffleOutcome.NotFound;
        }

        var bytes = File.ReadAllBytes(exePath);
        try
        {
            Dc2ShopTablePatch.Validate(bytes);
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"[shop-shuffle] {ex.Message}");
            return Dc2ShopShuffleOutcome.UnrecognizedVersion;
        }

        if (restore)
        {
            Dc2ShopTablePatch.RestoreCanonical(bytes);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[shop-shuffle] shop prices and stock unlocks restored to retail");
            return Dc2ShopShuffleOutcome.Restored;
        }

        // Capture the pristine original exactly once (same contract as the skin/bgm installer).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        var entries = Dc2ShopTablePatch.Shuffle(bytes, seed);
        File.WriteAllBytes(exePath, bytes);
        int moved = entries.Count(e => e.OldPrice != e.NewPrice || e.OldMask != e.NewMask);
        log?.Invoke($"[shop-shuffle] seed {seed}: {moved}/{entries.Length} shop items changed (backup: {Path.GetFileName(backupPath)})");
        foreach (var e in entries.Where(e => e.OldPrice != e.NewPrice || e.OldMask != e.NewMask))
        {
            // recovery (tools) items carry no stock mask (0/0) — show price only for those.
            string mask = (e.OldMask == 0 && e.NewMask == 0) ? "" : $", stock-mask 0x{e.OldMask:X2} -> 0x{e.NewMask:X2}";
            log?.Invoke($"[shop-shuffle]   item 0x{e.ItemId:X2}: price {e.OldPrice} -> {e.NewPrice}{mask}");
        }
        return Dc2ShopShuffleOutcome.Applied;
    }
}
