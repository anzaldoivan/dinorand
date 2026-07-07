using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2MotionTrailInstaller"/> did to the wrapper's <c>ddraw.dll</c>.</summary>
public enum Dc2TrailFixOutcome
{
    /// <summary>The fix was applied (and a pristine backup captured).</summary>
    Applied,
    /// <summary>The DLL already carried the fix; nothing changed.</summary>
    AlreadyApplied,
    /// <summary>No <c>ddraw.dll</c> was found in the game root.</summary>
    NotFound,
    /// <summary>The DLL is not the recognized Classic REbirth build the patch targets; left untouched.</summary>
    UnrecognizedVersion,
}

/// <summary>
/// Install-time step that applies the DC2 <see cref="Dc2DdrawTrailPatch"/> (MotionTrail
/// over-brightening fix) to the game's Classic REbirth wrapper <c>ddraw.dll</c>. Mirrors the DC1
/// <c>GameInstaller</c> exe contract: back the file up once, apply non-destructively, and stay
/// idempotent so re-rolling a seed never re-patches or re-captures an already-modified file as
/// "pristine". Restore by copying <c>ddraw.dll.bak</c> back over <c>ddraw.dll</c>.
/// </summary>
public static class Dc2MotionTrailInstaller
{
    /// <summary>The wrapper DLL that lives in the DC2 game root (beside <c>Data\</c> / <c>Dino2.exe</c>).</summary>
    public const string DllName = "ddraw.dll";

    /// <summary>Suffix of the one-time pristine backup written next to the DLL.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// Resolve <c>&lt;gameRoot&gt;\ddraw.dll</c> for the DC2 install at <paramref name="installDir"/>
    /// (the game root is the parent of the located <c>Data\</c> dir) and apply the fix.
    /// Returns <see cref="Dc2TrailFixOutcome.NotFound"/> if no DC2 Data folder or no DLL is present.
    /// </summary>
    public static Dc2TrailFixOutcome Apply(string installDir, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[trail-fix] no DC2 Data folder under {installDir}; skipped");
            return Dc2TrailFixOutcome.NotFound;
        }

        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, DllName), log);
    }

    /// <summary>
    /// Apply the fix to the <c>ddraw.dll</c> at <paramref name="ddrawPath"/>: detect already-patched
    /// (no-op), refuse an unrecognized build (untouched), else back up once and patch.
    /// </summary>
    public static Dc2TrailFixOutcome ApplyToFile(string ddrawPath, Action<string>? log = null)
    {
        if (!File.Exists(ddrawPath))
        {
            log?.Invoke($"[trail-fix] {DllName} not found at {ddrawPath}; skipped");
            return Dc2TrailFixOutcome.NotFound;
        }

        var bytes = File.ReadAllBytes(ddrawPath);

        if (Dc2DdrawTrailPatch.IsApplied(bytes))
        {
            log?.Invoke("[trail-fix] ddraw.dll already patched; nothing to do");
            return Dc2TrailFixOutcome.AlreadyApplied;
        }

        if (!Dc2DdrawTrailPatch.IsRecognizedPristine(bytes))
        {
            log?.Invoke("[trail-fix] ddraw.dll is not the recognized Classic REbirth build; left untouched");
            return Dc2TrailFixOutcome.UnrecognizedVersion;
        }

        // Capture the pristine original exactly once (never re-capture an already-modded file).
        var backupPath = ddrawPath + BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(ddrawPath, backupPath);

        Dc2DdrawTrailPatch.Apply(bytes);
        File.WriteAllBytes(ddrawPath, bytes);
        log?.Invoke($"[trail-fix] applied MotionTrail fix to ddraw.dll (backup: {Path.GetFileName(backupPath)})");
        return Dc2TrailFixOutcome.Applied;
    }
}
