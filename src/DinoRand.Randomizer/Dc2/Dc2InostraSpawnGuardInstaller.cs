using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2InostraSpawnGuardInstaller"/> did to <c>Dino2.exe</c>.</summary>
public enum Dc2InostraSpawnGuardOutcome
{
    Applied,
    Restored,
    NotFound,
    UnrecognizedVersion,
    /// <summary>Already carried the lever (apply) or wasn't patched (restore) — nothing to write.</summary>
    Skipped,
}

/// <summary>
/// Install-time exe step for the Inostra spawn-descriptor NULL guard
/// (docs/decisions/dc2/crash-rcas/DC2-INOSTRA-SPAWN-DESCRIPTOR-NULL-RCA.md): a NULL-cursor guard on the
/// PSX-recompiled emergence/burst emitter (<see cref="Dc2InostraSpawnGuardPatch"/>) so an injected
/// Inostrancevia (E50, TYPE 0x0e) doesn't crash in a room that armed no descriptor list for it. Mirrors
/// <see cref="Dc2TriceratopsKillableInstaller"/>: one-time pristine <c>.bak</c>, refuse unrecognized
/// builds, idempotent, and restores only its own slices so the other Dino2.exe patches survive.
/// </summary>
public static class Dc2InostraSpawnGuardInstaller
{
    /// <summary>True iff a run with <paramref name="config"/> does <b>any</b> DC2 cross-species enemy swap,
    /// or the user forced it on (<see cref="RandomizerConfig.Dc2MakeInostraSpawnSafe"/>). Unlike the
    /// species-specific killable levers, this guard patches a <b>shared</b> emitter tick driver
    /// (<c>0x4131d0</c>, referenced by ~10 actor-class vtables) and is byte-identical whenever the emitter
    /// is armed, so it is a zero-cost safety net: it only ever diverges on the un-armed NULL-cursor path,
    /// which is always a crash. E50 (a DEFAULT donor) is the witnessed trigger, but any injected donor that
    /// drives the same emitter unarmed hits the identical fault, so the guard is wanted for the whole
    /// cross-species pass — including a fixed pin on a non-E50 species — not just E50-injectable runs.</summary>
    public static bool WantedFor(RandomizerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Dc2MakeInostraSpawnSafe) return true; // manual override (--dc2-inostra-spawn-guard)
        return config.RandomizeEnemies;                  // any cross-species swap can inject an emerging donor
    }

    public static Dc2InostraSpawnGuardOutcome Apply(
        string installDir, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[inostra-spawn-guard] no DC2 Data folder under {installDir}; skipped");
            return Dc2InostraSpawnGuardOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), restore, log);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2InostraSpawnGuardOutcome ApplyToFile(
        string exePath, bool restore = false, Action<string>? log = null)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[inostra-spawn-guard] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2InostraSpawnGuardOutcome.NotFound;
        }
        var bytes = File.ReadAllBytes(exePath);

        if (restore)
        {
            if (!Dc2InostraSpawnGuardPatch.IsApplied(bytes))
            {
                log?.Invoke("[inostra-spawn-guard] lever not present; restore skipped");
                return Dc2InostraSpawnGuardOutcome.Skipped;
            }
            Dc2InostraSpawnGuardPatch.Restore(bytes);
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[inostra-spawn-guard] emitter NULL-guard reverted to vanilla");
            return Dc2InostraSpawnGuardOutcome.Restored;
        }

        if (Dc2InostraSpawnGuardPatch.IsApplied(bytes))
        {
            log?.Invoke("[inostra-spawn-guard] lever already applied; skipped");
            return Dc2InostraSpawnGuardOutcome.Skipped;
        }
        if (!Dc2InostraSpawnGuardPatch.IsRecognizedPristine(bytes))
        {
            log?.Invoke("[inostra-spawn-guard] Dino2.exe is not the recognized rebirth build; exe patch "
                + "skipped (room-file enemy edits still apply)");
            return Dc2InostraSpawnGuardOutcome.UnrecognizedVersion;
        }

        // Capture the pristine original exactly once (shared .bak with the other exe installers).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        Dc2InostraSpawnGuardPatch.Apply(bytes);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[inostra-spawn-guard] exe patched: injected Inostrancevia no longer crashes on the "
            + $"emergence emitter's NULL descriptor list (backup: {Path.GetFileName(backupPath)})");
        return Dc2InostraSpawnGuardOutcome.Applied;
    }
}
