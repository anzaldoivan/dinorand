using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2CharacterSkinInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2SkinGateOutcome
{
    /// <summary>The gate patch was applied (and a pristine backup captured).</summary>
    Applied,
    /// <summary>The exe already carried the patch; nothing changed.</summary>
    AlreadyApplied,
    /// <summary>No <c>Dino2.exe</c> was found in the game root.</summary>
    NotFound,
    /// <summary>The exe is not the recognized build the patch targets; left untouched.</summary>
    UnrecognizedVersion,
}

/// <summary>
/// Install-time step for the DC2 character-skin swap: applies <see cref="Dc2WpGatePatch"/> to the
/// game's <c>Dino2.exe</c> so the swapped WP&lt;n&gt;A graft files load unconditionally. Mirrors
/// <see cref="Dc2MotionTrailInstaller"/>: back the exe up once to a <c>.bak</c> sibling, apply
/// non-destructively, stay idempotent; <see cref="Restore"/> copies the backup back.
/// </summary>
public static class Dc2CharacterSkinInstaller
{
    /// <summary>The game exe that lives in the DC2 game root (beside <c>Data\</c>).</summary>
    public const string ExeName = "Dino2.exe";

    /// <summary>Suffix of the one-time pristine backup written next to the exe.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// Resolve <c>&lt;gameRoot&gt;\Dino2.exe</c> for the DC2 install at
    /// <paramref name="installDir"/> and apply the WP-gate patch.
    /// </summary>
    public static Dc2SkinGateOutcome Apply(string installDir, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[skin-gate] no DC2 Data folder under {installDir}; skipped");
            return Dc2SkinGateOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, ExeName), log);
    }

    /// <summary>
    /// Apply the gate patch to the exe at <paramref name="exePath"/>: detect already-patched
    /// (no-op), refuse an unrecognized build (untouched), else back up once and patch.
    /// </summary>
    public static Dc2SkinGateOutcome ApplyToFile(string exePath, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[skin-gate] {ExeName} not found at {exePath}; skipped");
            return Dc2SkinGateOutcome.NotFound;
        }

        var bytes = File.ReadAllBytes(exePath);

        if (Dc2WpGatePatch.IsApplied(bytes))
        {
            log?.Invoke("[skin-gate] Dino2.exe already patched; nothing to do");
            return Dc2SkinGateOutcome.AlreadyApplied;
        }
        if (!Dc2WpGatePatch.IsRecognizedPristine(bytes))
        {
            log?.Invoke("[skin-gate] Dino2.exe is not the recognized build; left untouched");
            return Dc2SkinGateOutcome.UnrecognizedVersion;
        }

        // Capture the pristine original exactly once (never re-capture an already-modded file).
        var backupPath = exePath + BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        Dc2WpGatePatch.Apply(bytes);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[skin-gate] WP-gate patch applied to {ExeName} (backup: {Path.GetFileName(backupPath)})");
        return Dc2SkinGateOutcome.Applied;
    }

    /// <summary>Copy the <c>.bak</c> backup back over the exe. Returns false when no backup exists
    /// (nothing was ever applied).</summary>
    public static bool Restore(string exePath, Action<string>? log = null)
    {
        var backupPath = exePath + BackupSuffix;
        if (!File.Exists(backupPath)) return false;
        File.Copy(backupPath, exePath, overwrite: true);
        log?.Invoke($"[skin-gate] restored {ExeName} from {Path.GetFileName(backupPath)}");
        return true;
    }

    // ---- Classic REbirth HD-audio wavebanks -------------------------------------------------
    // CR serves each package's SFX from CR\data\<package>\snd.wbk, BYPASSING the engine-side CORE
    // SOUND bank (live-witnessed 2026-07-04: the rebirth build's sound-bank tables 0x878BA0 stay
    // all-zero while SFX play — docs/decisions/dc2/voice/DC2-CHARACTER-VOICE-SFX-PLAN.md §6). So on a CR install
    // the voice swap must also swap the per-character CR wavebanks; a vanilla install needs
    // nothing (the swapped CORE SOUND entry in the Data overlay covers it).

    /// <summary>The wavebank file name inside each <c>CR\data\&lt;package&gt;</c> folder.</summary>
    public const string WavebankName = "snd.wbk";

    /// <summary>One-time wavebank backup suffix (matches the tools-style sibling convention).</summary>
    public const string WavebankBackupSuffix = ".dinorand-bak";

    /// <summary>
    /// The CR wavebank swaps a (resolved) skin pair implies: (target CORE dir, donor CORE dir).
    /// Donor mapping = <see cref="Dc2PlayerModelSwap.MenuAtlasDonors"/> (the same per-char CORE
    /// registry the Data-side swap uses). Skins must be resolved — <see cref="Dc2CharacterSkin.Random"/>
    /// throws.
    /// </summary>
    public static IReadOnlyList<(string TargetCore, string DonorCore)> PlanCrWavebankSwaps(
        Dc2CharacterSkin dylanSkin, Dc2CharacterSkin reginaSkin)
    {
        var plan = new List<(string, string)>();
        foreach (var (skin, targetCore) in new[]
                 {
                     (dylanSkin, Passes.Dc2PlayerModelSwap.DylanCoreFile),
                     (reginaSkin, Passes.Dc2PlayerModelSwap.ReginaCoreFile),
                 })
        {
            if (skin == Dc2CharacterSkin.Stock) continue;
            if (!Passes.Dc2PlayerModelSwap.MenuAtlasDonors.TryGetValue(skin, out var donorCore))
                throw new ArgumentOutOfRangeException(nameof(skin), skin,
                    "unresolved skin — call Dc2PlayerModelSwap.ResolveSkin first");
            // CR folders are the package names without the .DAT extension.
            plan.Add((Path.GetFileNameWithoutExtension(targetCore), Path.GetFileNameWithoutExtension(donorCore)));
        }
        return plan;
    }

    /// <summary>
    /// Swap the CR HD wavebanks for the given (resolved) skins under
    /// <c>&lt;gameRoot&gt;\CR\data\</c>. No CR dir → no-op ("vanilla install"). Each target bank is
    /// backed up ONCE to a <c>.dinorand-bak</c> sibling (never re-captured, so re-rolls don't
    /// compound); a missing target/donor bank skips that pair. Never throws for missing files —
    /// this step is cosmetic-only. Returns a short summary for the install status line.
    /// </summary>
    public static string ApplyCrWavebanks(string installDir, Dc2CharacterSkin dylanSkin,
                                          Dc2CharacterSkin reginaSkin, Action<string>? log = null)
    {
        var crData = FindCrDataDir(installDir);
        if (crData is null)
        {
            log?.Invoke("[cr-voice] no CR\\data dir — vanilla install, CORE SOUND swap covers it");
            return "no CR audio";
        }

        int swapped = 0;
        foreach (var (target, donor) in PlanCrWavebankSwaps(dylanSkin, reginaSkin))
        {
            string targetWbk = Path.Combine(crData, target, WavebankName);
            string donorWbk = Path.Combine(crData, donor, WavebankName);
            if (!File.Exists(targetWbk) || !File.Exists(donorWbk))
            {
                log?.Invoke($"[cr-voice] {target}/{donor} {WavebankName} missing — skipped");
                continue;
            }
            string backup = targetWbk + WavebankBackupSuffix;
            if (!File.Exists(backup))
                File.Copy(targetWbk, backup);
            File.Copy(donorWbk, targetWbk, overwrite: true);
            log?.Invoke($"[cr-voice] {target}\\{WavebankName} ← {donor} (backup {Path.GetFileName(backup)})");
            swapped++;
        }
        return $"{swapped} CR wavebank(s) swapped";
    }

    /// <summary>Undo every CR wavebank swap: any <c>snd.wbk.dinorand-bak</c> under
    /// <c>CR\data\CORE*</c> is copied back and removed. Returns the number restored.</summary>
    public static int RestoreCrWavebanks(string installDir, Action<string>? log = null)
    {
        var crData = FindCrDataDir(installDir);
        if (crData is null) return 0;
        int restored = 0;
        foreach (var dir in Directory.EnumerateDirectories(crData, "CORE*"))
        {
            string backup = Path.Combine(dir, WavebankName + WavebankBackupSuffix);
            if (!File.Exists(backup)) continue;
            File.Copy(backup, Path.Combine(dir, WavebankName), overwrite: true);
            File.Delete(backup);
            log?.Invoke($"[cr-voice] restored {Path.GetFileName(dir)}\\{WavebankName}");
            restored++;
        }
        return restored;
    }

    /// <summary><c>&lt;gameRoot&gt;\CR\data</c> for the install, or null when absent
    /// (vanilla, non-CR install). GameRoot = the Data dir's parent, as for the exe.</summary>
    private static string? FindCrDataDir(string installDir)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null) return null;
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        var crData = Path.Combine(gameRoot, "CR", "data");
        return Directory.Exists(crData) ? crData : null;
    }
}
