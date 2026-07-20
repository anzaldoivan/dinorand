using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Dc2;

/// <summary>Result of installing the cross-character-weapon lever.</summary>
public enum Dc2CrossCharWeaponOutcome
{
    Applied,
    AlreadyApplied,
    Restored,
    NotFound,
    UnrecognizedVersion,
}

/// <summary>
/// Installs "Enable Cross Character Weapons" (DC2): builds the eight grafted <c>WEP_P</c> packages
/// from the user's own Data files, then applies <see cref="Dc2CrossCharWeaponPatch"/> to
/// <c>Dino2.exe</c>. Nothing is shipped — every graft is generated at install time from the player's
/// install, mirroring the character-skin swap.
/// Decision record: <c>docs/decisions/dc2/models/DC2-CROSS-CHAR-WEAPON-MODEL-SWAP.md</c>.
/// </summary>
public static class Dc2CrossCharWeaponInstaller
{
    private const string ExeName = "Dino2.exe";
    private const string BackupSuffix = ".bak";

    /// <summary>Resolve the DC2 Data dir + exe under <paramref name="installDir"/> and apply (or
    /// restore) the lever.</summary>
    public static Dc2CrossCharWeaponOutcome Apply(
        string installDir, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[dc2-cross-char] no DC2 Data folder under {installDir}; skipped");
            return Dc2CrossCharWeaponOutcome.NotFound;
        }
        return ApplyToDataDir(dataDir, restore, log);
    }

    /// <summary>Directory-level worker (the testable entry point).</summary>
    public static Dc2CrossCharWeaponOutcome ApplyToDataDir(
        string dataDir, bool restore = false, Action<string>? log = null)
    {
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        var exePath = Path.Combine(gameRoot, ExeName);
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[dc2-cross-char] {ExeName} not found at {exePath}; skipped");
            return Dc2CrossCharWeaponOutcome.NotFound;
        }

        if (restore)
        {
            var bak = exePath + BackupSuffix;
            if (File.Exists(bak)) File.Copy(bak, exePath, overwrite: true);
            foreach (var p in Dc2CrossCharWeaponPatch.Pairs)
            {
                var graft = Path.Combine(dataDir, p.GraftFile);
                if (File.Exists(graft)) File.Delete(graft);
            }
            log?.Invoke("[dc2-cross-char] restored Dino2.exe and removed the generated WEP_P grafts");
            return Dc2CrossCharWeaponOutcome.Restored;
        }

        var exe = File.ReadAllBytes(exePath);
        if (Dc2CrossCharWeaponPatch.IsApplied(exe))
        {
            log?.Invoke("[dc2-cross-char] Dino2.exe already patched; nothing to do");
            return Dc2CrossCharWeaponOutcome.AlreadyApplied;
        }
        if (!Dc2CrossCharWeaponPatch.IsRecognizedPristine(exe))
        {
            log?.Invoke("[dc2-cross-char] Dino2.exe is not the recognized build; left untouched");
            return Dc2CrossCharWeaponOutcome.UnrecognizedVersion;
        }

        // Build every graft BEFORE writing anything, so a failure leaves the install untouched.
        var built = BuildGrafts(dataDir, log);
        if (built is null) return Dc2CrossCharWeaponOutcome.NotFound;

        foreach (var (name, bytes) in built)
            File.WriteAllBytes(Path.Combine(dataDir, name), bytes);

        var backupPath = exePath + BackupSuffix;
        if (!File.Exists(backupPath)) File.Copy(exePath, backupPath);
        Dc2CrossCharWeaponPatch.Apply(exe);
        File.WriteAllBytes(exePath, exe);

        int unwitnessed = Dc2CrossCharWeaponPatch.Pairs.Count(p => !p.HeadGraftSafe);
        log?.Invoke($"[dc2-cross-char] built {built.Count} WEP_P grafts and patched {ExeName} "
                    + $"({unwitnessed} of them still awaiting an in-game witness)");
        return Dc2CrossCharWeaponOutcome.Applied;
    }

    /// <summary>Build every canonical graft in memory without writing files.</summary>
    internal static List<(string Name, byte[] Bytes)>? BuildGrafts(
        string dataDir, Action<string>? log = null)
    {
        var built = new List<(string Name, byte[] Bytes)>(Dc2CrossCharWeaponPatch.Pairs.Count);
        foreach (var p in Dc2CrossCharWeaponPatch.Pairs)
        {
            var owner = ReadPristine(dataDir, p.OwnerFile);
            var geom = ReadPristine(dataDir, p.GeometryFile);
            if (owner is null || geom is null)
            {
                log?.Invoke($"[dc2-cross-char] {p.OwnerFile} or {p.GeometryFile} missing; skipped");
                return null;
            }
            built.Add((p.GraftFile, Dc2WeaponGraft.Build(owner, geom)));
        }
        return built;
    }

    /// <summary>Pristine bytes for a Data file: prefer the backup dir, then the sibling backup, then
    /// the live file — and refuse when two captures disagree (the K82 poisoned-capture guard).</summary>
    private static byte[]? ReadPristine(string dataDir, string fileName)
    {
        var backupDir = Path.Combine(dataDir, GameInstaller.BackupDirName, fileName);
        var sibling = Path.Combine(dataDir, fileName + GameInstaller.SiblingBackupSuffix);
        var live = Path.Combine(dataDir, fileName);

        if (File.Exists(backupDir) && File.Exists(sibling)
            && !File.ReadAllBytes(backupDir).AsSpan().SequenceEqual(File.ReadAllBytes(sibling)))
            throw new InvalidOperationException(
                $"the two pristine captures of {fileName} disagree; refusing to build a graft from a "
                + "possibly-poisoned source.");

        var path = File.Exists(backupDir) ? backupDir : File.Exists(sibling) ? sibling : live;
        if (!File.Exists(path)) return null;

        var bytes = File.ReadAllBytes(path);
        var pkg = GianPackage.TryParse(bytes);
        return pkg is not null && pkg.IsDc2 && pkg.Entries.Count > 0 ? bytes : null;
    }
}
