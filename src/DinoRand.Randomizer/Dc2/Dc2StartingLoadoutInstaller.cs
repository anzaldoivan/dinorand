using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// Install-time step for <c>--dc2-randomize-start-weapon</c> / <c>--dc2-start-weapon</c>
/// (docs/decisions/dc2/loadout/DC2-STARTING-LOADOUT-PLAN.md I2; I3 human gate PENDING — lever stays default-off):
/// applies <see cref="Dc2StartingLoadoutPatch"/> to the game's <c>Dino2.exe</c>. Mirrors
/// <see cref="Dc2BgmShuffleInstaller"/>: one-time pristine <c>.bak</c> backup, refuse
/// unrecognized builds, non-compounding (ids are absolute). Restore rewrites only the two
/// weapon-id bytes, leaving other exe patches (WP-gate, BGM shuffle, …) intact.
/// Reuses <see cref="Dc2BgmShuffleOutcome"/> — same outcome semantics.
/// </summary>
public static class Dc2StartingLoadoutInstaller
{
    /// <summary>
    /// Resolve the DC2 install and set the starting main weapons. Null ids mean "random from
    /// the character's band" (seed-keyed); with <paramref name="restore"/> both revert to canonical.
    /// </summary>
    public static Dc2BgmShuffleOutcome Apply(string installDir, int seed, byte? dylanId, byte? reginaId,
                                             bool restore = false, Action<string>? log = null, bool allowUnsafe = false,
                                             bool addAndEquip = false)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[start-weapon] no DC2 Data folder under {installDir}; skipped");
            return Dc2BgmShuffleOutcome.NotFound;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        return ApplyToFile(Path.Combine(gameRoot, Dc2CharacterSkinInstaller.ExeName), seed, dylanId, reginaId, restore, log, allowUnsafe, addAndEquip);
    }

    /// <summary>File-level worker (testable without a full install layout).</summary>
    public static Dc2BgmShuffleOutcome ApplyToFile(string exePath, int seed, byte? dylanId, byte? reginaId,
                                                   bool restore = false, Action<string>? log = null, bool allowUnsafe = false,
                                                   bool addAndEquip = false)
    {
        if (!File.Exists(exePath))
        {
            log?.Invoke($"[start-weapon] {Path.GetFileName(exePath)} not found at {exePath}; skipped");
            return Dc2BgmShuffleOutcome.NotFound;
        }

        var bytes = File.ReadAllBytes(exePath);
        try
        {
            Dc2StartingLoadoutPatch.Validate(bytes);
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"[start-weapon] {ex.Message}");
            return Dc2BgmShuffleOutcome.UnrecognizedVersion;
        }

        if (restore)
        {
            Dc2StartingLoadoutPatch.RestoreCanonical(bytes);
            Dc2StartWeaponAppendPatch.Restore(bytes);   // no-op if never appended
            Dc2WeaponRingGuardPatch.Restore(bytes); // no-op if the guard was never installed
            File.WriteAllBytes(exePath, bytes);
            log?.Invoke("[start-weapon] starting weapons restored to canonical (Dylan shotgun 0x01, Regina handgun 0x02)");
            return Dc2BgmShuffleOutcome.Restored;
        }

        // Random picks draw only from fire-witnessed ids (all owned mains by construction);
        // explicit ids are the witness tool and may go beyond, with a warning. Non-owned-main ids
        // (a SUB, or a main another character owns — e.g. Dylan 0x05/0x09, Regina 0x06; also the
        // fire-empty mains 0x04/0x07) are refused by the patch unless allowUnsafe: as the sole
        // starting main they empty the weapon ring → div-0 at 0x496EAC.
        // Add-and-equip installs the ring-builder zero-guard, which makes every in-band id a safe
        // starting pick (div-0 neutralized; all band ids are WEP_P-loadable). Random then draws from
        // the FULL band instead of the fire-witnessed subset. Cross-character-shared mains (id 0x05,
        // owned by both) are dropped from the RANDOM pool so one character's roll can't silently arm
        // the other (shared inventory array + ownership-filtered ring); explicit picks still allowed.
        static byte[] NoShared(byte[] band) =>
            band.Where(id => !Dc2StartingLoadoutPatch.CrossCharacterSharedMainIds.Contains(id)).ToArray();
        uint rng = (uint)seed;
        byte d = dylanId ?? Pick(NoShared(addAndEquip ? Dc2StartingLoadoutPatch.DylanWeaponIds
                                                       : Dc2StartingLoadoutPatch.DylanFireWitnessedIds), ref rng);
        byte r = reginaId ?? Pick(NoShared(addAndEquip ? Dc2StartingLoadoutPatch.ReginaWeaponIds
                                                        : Dc2StartingLoadoutPatch.ReginaFireWitnessedIds), ref rng);
        WarnShared(dylanId, "Dylan", log);
        WarnShared(reginaId, "Regina", log);
        if (!addAndEquip)
        {
            WarnUnwitnessed(dylanId, "Dylan", Dc2StartingLoadoutPatch.SelectableDylanIds, Dc2StartingLoadoutPatch.DylanFireWitnessedIds, log);
            WarnUnwitnessed(reginaId, "Regina", Dc2StartingLoadoutPatch.SelectableReginaIds, Dc2StartingLoadoutPatch.ReginaFireWitnessedIds, log);
        }

        // Capture the pristine original exactly once (same contract as the skin/bgm installer).
        var backupPath = exePath + Dc2CharacterSkinInstaller.BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(exePath, backupPath);

        if (addAndEquip)
            Dc2WeaponRingGuardPatch.Apply(bytes); // idempotent; must precede the relaxed loadout apply
        Dc2StartingLoadoutPatch.Apply(bytes, d, r, allowUnsafe, addAndEquip);
        // Append the chosen weapon(s) as extra records so the default shotgun/handgun survives (the
        // player can switch to a one-handed main + sub-weapon — fixes the two-handed soft-lock).
        Dc2StartWeaponAppendPatch.Apply(bytes, d, r);
        File.WriteAllBytes(exePath, bytes);
        log?.Invoke($"[start-weapon] Dylan weapon id 0x{d:X2}, Regina weapon id 0x{r:X2} "
                    + (addAndEquip ? "(add-and-equip: weapon-ring div-0 guard installed; full band unlocked) " : "")
                    + $"(subweapons untouched: Machete/Large Stun Gun kept; backup: {Path.GetFileName(backupPath)})");
        return Dc2BgmShuffleOutcome.Applied;
    }

    private static void WarnShared(byte? id, string who, Action<string>? log)
    {
        if (id is { } v && Dc2StartingLoadoutPatch.CrossCharacterSharedMainIds.Contains(v))
            log?.Invoke($"[start-weapon] warn: {who} id 0x{v:X2} is owned by BOTH characters — it will "
                        + "also appear as an extra main in the other character's weapon ring (shared inventory).");
    }

    private static void WarnUnwitnessed(byte? id, string who, byte[] selectable, byte[] witnessed, Action<string>? log)
    {
        if (id is not { } exp || witnessed.Contains(exp)) return;
        if (!selectable.Contains(exp))
            log?.Invoke($"[start-weapon] warn: {who} id 0x{exp:X2} is not an owned main — bricks the "
                        + "weapon-select menu (div-0); installed only because --allow-unsafe was set (investigation).");
        else
            log?.Invoke($"[start-weapon] warn: {who} id 0x{exp:X2} has no clean in-game fire witness yet — may crash on fire.");
    }

    /// <summary>splitmix32 — same deterministic PRNG family as the other levers.</summary>
    private static byte Pick(byte[] band, ref uint state)
    {
        state += 0x9E3779B9u;
        uint z = state;
        z = (z ^ (z >> 16)) * 0x21F0AAADu;
        z = (z ^ (z >> 15)) * 0x735A2D97u;
        z ^= z >> 15;
        return band[z % (uint)band.Length];
    }
}
