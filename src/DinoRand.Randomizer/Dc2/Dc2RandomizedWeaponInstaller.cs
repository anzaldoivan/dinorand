using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

public enum Dc2RandomizedWeaponOutcome
{
    Applied,
    Restored,
    NotFound,
    UnrecognizedVersion,
}

/// <summary>Builds the existing cross-character grafts, then atomically applies a seed-planned
/// exact Regina/Dylan MAIN ownership layout.</summary>
public static class Dc2RandomizedWeaponInstaller
{
    private const string ExeName = "Dino2.exe";
    private const string BackupSuffix = ".bak";

    public static Dc2RandomizedWeaponOutcome Apply(
        string installDir, Seed seed, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[dc2-randomized-weapons] no DC2 Data folder under {installDir}; skipped");
            return Dc2RandomizedWeaponOutcome.NotFound;
        }
        return ApplyToDataDir(dataDir, seed, restore, log);
    }

    public static Dc2RandomizedWeaponOutcome ApplyToDataDir(
        string dataDir, Seed seed, bool restore = false, Action<string>? log = null)
    {
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        var exePath = Path.Combine(gameRoot, ExeName);
        if (!File.Exists(exePath)) return Dc2RandomizedWeaponOutcome.NotFound;

        var exe = File.ReadAllBytes(exePath);
        try
        {
            if (restore)
            {
                Dc2RandomizedWeaponPatch.Restore(exe);
                File.WriteAllBytes(exePath, exe);
                foreach (var pair in Dc2CrossCharWeaponPatch.Pairs)
                {
                    var graft = Path.Combine(dataDir, pair.GraftFile);
                    if (File.Exists(graft)) File.Delete(graft);
                }
                log?.Invoke("[dc2-randomized-weapons] restored native ownership and removed generated WEP_P grafts");
                return Dc2RandomizedWeaponOutcome.Restored;
            }

            var built = Dc2CrossCharWeaponInstaller.BuildGrafts(dataDir, log);
            if (built is null) return Dc2RandomizedWeaponOutcome.NotFound;

            var plan = Dc2RandomizedWeaponPlanner.Plan(seed);
            Dc2RandomizedWeaponPatch.Apply(exe, plan.ReginaOnly);

            foreach (var (name, bytes) in built)
                File.WriteAllBytes(Path.Combine(dataDir, name), bytes);
            var backupPath = exePath + BackupSuffix;
            if (!File.Exists(backupPath)) File.Copy(exePath, backupPath);
            File.WriteAllBytes(exePath, exe);

            log?.Invoke($"[dc2-randomized-weapons] Regina: {string.Join(", ", plan.ReginaOnly.Select(id => $"0x{id:X2}"))}; "
                        + $"Dylan: {string.Join(", ", plan.DylanOnly.Select(id => $"0x{id:X2}"))}");
            return Dc2RandomizedWeaponOutcome.Applied;
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"[dc2-randomized-weapons] {ex.Message}");
            return Dc2RandomizedWeaponOutcome.UnrecognizedVersion;
        }
    }
}
