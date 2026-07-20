using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>Result of installing the character-shared-weapon lever.</summary>
public enum Dc2SharedWeaponOutcome
{
    Applied,
    AlreadyApplied,
    Restored,
    NotFound,
    UnrecognizedVersion,
}

/// <summary>
/// Installs "Enable Character Shared Weapons" (DC2, experimental): grants both owner bits to the SUB
/// weapons so Regina and Dylan share the Machete and the Large Stungun, the way the seven natively
/// dual-owned catalog ids already work.
///
/// <para>Exe-only and tiny — a two-bit edit to <c>Dino2.exe</c>'s item catalog. No Data files are
/// generated or touched, so unlike <see cref="Dc2CrossCharWeaponInstaller"/> there is nothing to
/// build or clean up. Shares the same <c>.bak</c> contract, so the Restore button reverts it.</para>
///
/// <para>Decision record: <c>docs/reference/dc2/weapon/DC2-NATIVE-WEAPON-SHARING-DECODE.md</c> (K125).</para>
/// </summary>
public static class Dc2SharedWeaponInstaller
{
    private const string ExeName = "Dino2.exe";
    private const string BackupSuffix = ".bak";

    public static Dc2SharedWeaponOutcome Apply(
        string installDir, bool restore = false, Action<string>? log = null, bool includeMainWeapons = true)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[dc2-shared-weapons] no DC2 Data folder under {installDir}; skipped");
            return Dc2SharedWeaponOutcome.NotFound;
        }
        return ApplyToDataDir(dataDir, restore, log, includeMainWeapons);
    }

    /// <summary>Directory-level worker (the testable entry point).</summary>
    public static Dc2SharedWeaponOutcome ApplyToDataDir(
        string dataDir, bool restore = false, Action<string>? log = null, bool includeMainWeapons = true)
    {
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        var exePath = Path.Combine(gameRoot, ExeName);
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[dc2-shared-weapons] {ExeName} not found at {exePath}; skipped");
            return Dc2SharedWeaponOutcome.NotFound;
        }

        var exe = File.ReadAllBytes(exePath);
        if (exe.Length != Dc2WpGatePatch.ExpectedLength)
        {
            log?.Invoke("[dc2-shared-weapons] Dino2.exe is not the recognized build; left untouched");
            return Dc2SharedWeaponOutcome.UnrecognizedVersion;
        }

        if (restore)
        {
            // The graft lever owns the MAIN half (grafted WEP_P files + the 0x71B230 slots + those
            // weapons' owner bits), so its restore reverts the exe wholesale from .bak and removes
            // the generated files — which also reverts the SUB bits.
            if (includeMainWeapons)
            {
                Dc2CrossCharWeaponInstaller.ApplyToDataDir(dataDir, restore: true, log);
                log?.Invoke("[dc2-shared-weapons] reverted MAIN and SUB weapon sharing");
                return Dc2SharedWeaponOutcome.Restored;
            }
            Dc2SharedWeaponPatch.Restore(exe);
            File.WriteAllBytes(exePath, exe);
            log?.Invoke("[dc2-shared-weapons] reverted the shared SUB owner bits to vanilla");
            return Dc2SharedWeaponOutcome.Restored;
        }

        // MAIN weapons can only be shared once their geometry graft exists — an owner bit on a MAIN
        // whose 0x71B230 slot is NULL runs the loader on a stale blob. Delegate that half to the
        // proven graft installer rather than writing the same bits from here (it sets the MAIN owner
        // bits itself; a second writer would fight it on restore).
        if (includeMainWeapons)
        {
            var graft = Dc2CrossCharWeaponInstaller.ApplyToDataDir(dataDir, restore: false, log);
            if (graft is Dc2CrossCharWeaponOutcome.NotFound or Dc2CrossCharWeaponOutcome.UnrecognizedVersion)
            {
                log?.Invoke("[dc2-shared-weapons] MAIN sharing needs the weapon grafts; aborted without writing");
                return graft == Dc2CrossCharWeaponOutcome.NotFound
                    ? Dc2SharedWeaponOutcome.NotFound
                    : Dc2SharedWeaponOutcome.UnrecognizedVersion;
            }
            exe = File.ReadAllBytes(exePath);
            if (!Dc2SharedWeaponPatch.MainWeaponsReady(exe))
            {
                log?.Invoke("[dc2-shared-weapons] graft slots still NULL after the graft install; aborted");
                return Dc2SharedWeaponOutcome.UnrecognizedVersion;
            }
        }

        if (Dc2SharedWeaponPatch.IsApplied(exe)
            && (!includeMainWeapons || Dc2SharedWeaponPatch.MainWeaponsReady(exe)))
        {
            log?.Invoke("[dc2-shared-weapons] Dino2.exe already patched; nothing to do");
            return Dc2SharedWeaponOutcome.AlreadyApplied;
        }

        var backupPath = exePath + BackupSuffix;
        if (!File.Exists(backupPath)) File.Copy(exePath, backupPath);
        Dc2SharedWeaponPatch.Apply(exe);
        File.WriteAllBytes(exePath, exe);

        log?.Invoke(includeMainWeapons
            ? $"[dc2-shared-weapons] shared {Dc2CrossCharWeaponPatch.Pairs.Count} MAIN weapons (via the "
              + $"geometry grafts) and {Dc2SharedWeaponPatch.SharedIds.Count} SUB weapons between Regina and Dylan"
            : $"[dc2-shared-weapons] shared {Dc2SharedWeaponPatch.SharedIds.Count} SUB weapons "
              + "(Machete, Large Stungun) between Regina and Dylan");
        return Dc2SharedWeaponOutcome.Applied;
    }
}
