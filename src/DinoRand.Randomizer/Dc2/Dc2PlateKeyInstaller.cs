using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2PlateKeyInstaller"/> did to ST205.DAT.</summary>
public enum Dc2PlateKeyOutcome
{
    /// <summary>ST205 re-keyed (pristine <c>ST205.DAT.bak</c> captured first).</summary>
    Applied,
    /// <summary>Restore: ST205 reverted from its <c>.bak</c> (backup removed).</summary>
    Restored,
    /// <summary>No DC2 Data dir, or ST205.DAT is missing.</summary>
    NotFound,
    /// <summary>ST205 failed its vanilla-signature pin; nothing was written.</summary>
    UnrecognizedContent,
}

/// <summary>
/// Install-time step for <c>--dc2-rekey-plate-door</c> (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md
/// §4 item 4, K118): applies <see cref="Dc2PlateKeyPatch"/> to ST205.DAT under the room-file
/// backup-and-swap contract (<c>ST205.DAT.bak</c>). Non-compounding: the re-key is always computed
/// from the pristine bytes (the <c>.bak</c> when one exists). The RNG key is
/// <c>Seed.RngFor("DC2 Plate Key")</c>, shared with the <see cref="Passes.Dc2PlateKeyRekey"/> pass so
/// the CLI and the GUI produce identical bytes for the same seed.
/// </summary>
public static class Dc2PlateKeyInstaller
{
    /// <summary>Resolve the DC2 install at <paramref name="installDir"/> and re-key (or, with
    /// <paramref name="restore"/>, revert) ST205.</summary>
    public static Dc2PlateKeyOutcome Apply(string installDir, int seed, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[plate-key] no DC2 Data folder under {installDir}; skipped");
            return Dc2PlateKeyOutcome.NotFound;
        }
        return ApplyToDataDir(dataDir, seed, restore, log);
    }

    private const string RoomFile = "ST205.DAT";

    /// <summary>Directory-level worker (testable against a synthetic Data dir).</summary>
    public static Dc2PlateKeyOutcome ApplyToDataDir(string dataDir, int seed, bool restore = false, Action<string>? log = null)
    {
        // Case-insensitive lookup — the St502.dat lesson (STATIC-SCD-RE cont.42).
        var caseInsensitive = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
        var path = Directory.EnumerateFiles(dataDir, RoomFile, caseInsensitive).FirstOrDefault();
        if (path is null)
        {
            log?.Invoke($"[plate-key] {RoomFile} not found in {dataDir}; skipped");
            return Dc2PlateKeyOutcome.NotFound;
        }
        var bak = path + Dc2BackupSwapSink.BackupSuffix;

        if (restore)
        {
            if (!File.Exists(bak))
            {
                log?.Invoke("[plate-key] nothing to restore (no ST205.DAT.bak backup found)");
                return Dc2PlateKeyOutcome.Restored;
            }
            File.Copy(bak, path, overwrite: true);
            File.Delete(bak);
            log?.Invoke($"[plate-key] restored {RoomFile} from .bak (backup removed)");
            return Dc2PlateKeyOutcome.Restored;
        }

        // Re-key from the PRISTINE bytes (the .bak when one exists) so re-running never compounds.
        byte[] pristine = File.ReadAllBytes(File.Exists(bak) ? bak : path);
        int plate = Dc2PlateKeyPatch.SelectRequiredPlate(new Seed(seed).RngFor("DC2 Plate Key"));
        byte[] bytes;
        Dc2PlateKeyPatch.Result result;
        try
        {
            bytes = Dc2PlateKeyPatch.ApplyRoom(pristine, plate, out result);
        }
        catch (InvalidOperationException ex)
        {
            log?.Invoke($"[plate-key] {ex.Message}");
            return Dc2PlateKeyOutcome.UnrecognizedContent;
        }

        Dc2BackupSwapSink.EmitTo(path, bytes); // backup-once + overwrite
        log?.Invoke($"[plate-key] seed {seed}: ST205 terminal now accepts plate 0x{result.TargetPlate:X2}"
            + (result.Changed ? " (blue panel recoloured)" : " (blue — vanilla, byte-identical)"));
        return Dc2PlateKeyOutcome.Applied;
    }
}
