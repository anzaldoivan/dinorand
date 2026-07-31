using DinoRand.ApClient;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Ap;

/// <summary>Prepares and installs one authenticated DC1 AP placement without touching the game first.</summary>
public static class Dc1ApInstallCoordinator
{
    public sealed record Result(
        ApPlacementInstaller.Result Placement,
        InstallResult Install,
        bool PickupCancelPatchApplied);

    public static Result Install(
        string dataDir,
        string outDir,
        ApConnection connection,
        Dc1ClientChecks checks,
        Action<string>? log = null,
        Action<string, string>? applyPickupCancelPatch = null)
    {
        if (connection.Slot <= 0)
            throw new InvalidOperationException("AP login returned an invalid player slot");

        var patches = new List<ApPlacementInstaller.RecordPatch>();
        foreach (var entry in checks.Locations)
        {
            if (!connection.Placements.TryGetValue(entry.ApId, out int placed))
                throw new InvalidOperationException(
                    $"slot_data has no placement for '{entry.Name}' — apworld/client contract drift");

            byte itemId = placed == ApSession.OtherWorldMarker
                ? Dc1Symbols.OtherWorldMarkerItemId
                : checked((byte)placed);
            foreach (var rec in entry.Records)
                patches.Add(new ApPlacementInstaller.RecordPatch(
                    rec.Room,
                    rec.RecOffset,
                    new Dc1ItemRecordClass(
                        checked((byte)rec.ExpectedOpcode),
                        checked((byte)rec.ExpectedSubtype),
                        rec.ExpectedLength),
                    rec.VanillaItemId,
                    rec.VanillaAmount,
                    checked((ushort)rec.VanillaTake),
                    itemId,
                    1,
                    checked((ushort)rec.Take),
                    Visual: null));
        }

        // WriteRooms fully parses and validates every source room before publishing output. The old
        // overlay is restored only after that preflight succeeds, so a bad server placement cannot
        // destroy the currently installed seed.
        var written = ApPlacementInstaller.WriteRooms(dataDir, outDir, patches, log);
        GameInstaller.Restore(dataDir);
        var installed = GameInstaller.Install(dataDir, outDir, $"AP {connection.SeedName}", written.WrittenFiles);

        bool pickupCancelPatchApplied = false;
        if (File.Exists(GameInstaller.ExePath(dataDir)))
        {
            var patch = applyPickupCancelPatch
                ?? ((data, seed) => GameInstaller.PatchExeItemPickupCancelFix(data, seed));
            patch(dataDir, connection.SeedName);
            pickupCancelPatchApplied = true;
        }

        log?.Invoke($"installed AP placement: {written.RecordsPatched} records in {written.RoomsWritten} rooms "
            + $"(backed up {installed.BackedUp}, overlaid {installed.Overlaid})");
        return new Result(written, installed, pickupCancelPatchApplied);
    }
}
