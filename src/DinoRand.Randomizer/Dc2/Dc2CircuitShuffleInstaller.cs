using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>What <see cref="Dc2CircuitShuffleInstaller"/> did to the circuit room files.</summary>
public enum Dc2CircuitShuffleOutcome
{
    /// <summary>Both circuit rooms rewritten (pristine <c>ST*.DAT.bak</c> captured first).</summary>
    Applied,
    /// <summary>Restore: the rooms reverted from their <c>.bak</c>s (backups removed).</summary>
    Restored,
    /// <summary>No DC2 Data dir, or a circuit room file is missing.</summary>
    NotFound,
    /// <summary>A room failed its vanilla-script pin; nothing was written.</summary>
    UnrecognizedContent,
}

/// <summary>
/// Install-time step for <c>--dc2-shuffle-circuits</c> (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 2, K110):
/// applies <see cref="Dc2CircuitPatch"/> to ST607.DAT + ST402.DAT under the room-file
/// backup-and-swap contract (<c>ST*.DAT.bak</c>, the <c>--dc2-swap-enemies</c> convention).
/// Non-compounding: the shuffle is always computed from the pristine bytes (the <c>.bak</c> when
/// one exists), so re-running with another seed never stacks. Both rooms are validated before
/// either is written — a pin failure leaves the install untouched. The RNG stream per room is
/// <c>Seed.RngFor("DC2 Circuits:&lt;file&gt;")</c>, shared with the <see cref="Passes.Dc2CircuitShuffle"/>
/// pass so the CLI and the GUI produce identical bytes for the same seed.
/// </summary>
public static class Dc2CircuitShuffleInstaller
{
    /// <summary>Resolve the DC2 install at <paramref name="installDir"/> and shuffle (or, with
    /// <paramref name="restore"/>, revert) both circuit rooms.</summary>
    public static Dc2CircuitShuffleOutcome Apply(string installDir, int seed, bool restore = false, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[circuits] no DC2 Data folder under {installDir}; skipped");
            return Dc2CircuitShuffleOutcome.NotFound;
        }
        return ApplyToDataDir(dataDir, seed, restore, log);
    }

    /// <summary>Directory-level worker (testable against a synthetic Data dir).</summary>
    public static Dc2CircuitShuffleOutcome ApplyToDataDir(string dataDir, int seed, bool restore = false, Action<string>? log = null)
    {
        // Case-insensitive lookup — the St502.dat lesson (STATIC-SCD-RE cont.42).
        var caseInsensitive = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
        var targets = new List<(Dc2CircuitPatch.RoomSpec Room, string Path)>();
        foreach (var room in Dc2CircuitPatch.Rooms)
        {
            var path = Directory.EnumerateFiles(dataDir, room.FileName, caseInsensitive).FirstOrDefault();
            if (path is null)
            {
                log?.Invoke($"[circuits] {room.FileName} not found in {dataDir}; skipped");
                return Dc2CircuitShuffleOutcome.NotFound;
            }
            targets.Add((room, path));
        }

        if (restore)
        {
            int restored = 0;
            foreach (var (_, path) in targets)
            {
                var bak = path + Dc2BackupSwapSink.BackupSuffix;
                if (!File.Exists(bak)) continue;
                File.Copy(bak, path, overwrite: true);
                File.Delete(bak);
                restored++;
                log?.Invoke($"[circuits] restored {Path.GetFileName(path)} from .bak (backup removed)");
            }
            if (restored == 0)
                log?.Invoke("[circuits] nothing to restore (no ST*.DAT.bak backups found)");
            return Dc2CircuitShuffleOutcome.Restored;
        }

        // Shuffle from the PRISTINE bytes (the .bak when one exists) and validate BOTH rooms
        // before writing either, so a pin failure never leaves a half-applied install.
        var outputs = new List<(string Path, byte[] Bytes, Dc2CircuitPatch.RoutineResult[] Results)>();
        foreach (var (room, path) in targets)
        {
            var bak = path + Dc2BackupSwapSink.BackupSuffix;
            byte[] pristine = File.ReadAllBytes(File.Exists(bak) ? bak : path);
            var rng = new Seed(seed).RngFor($"DC2 Circuits:{room.FileName}");
            byte[] bytes;
            Dc2CircuitPatch.RoutineResult[] results;
            try
            {
                bytes = Dc2CircuitPatch.ShuffleRoom(pristine, room, rng, out results);
            }
            catch (InvalidOperationException ex)
            {
                log?.Invoke($"[circuits] {ex.Message}");
                return Dc2CircuitShuffleOutcome.UnrecognizedContent;
            }
            outputs.Add((path, bytes, results));
        }

        foreach (var (path, bytes, results) in outputs)
        {
            Dc2BackupSwapSink.EmitTo(path, bytes); // backup-once + overwrite
            foreach (var r in results)
                log?.Invoke($"[circuits]   {Path.GetFileName(path)} routine[{r.RoutineIndex}]: "
                    + $"{string.Join(",", r.OldIds)} -> {string.Join(",", r.NewIds)}");
        }
        log?.Invoke($"[circuits] seed {seed}: blink order shuffled in {outputs.Count} room(s) (backups: ST*.DAT.bak)");
        return Dc2CircuitShuffleOutcome.Applied;
    }
}
