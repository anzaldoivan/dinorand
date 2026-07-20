using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Install;

/// <summary>
/// Loop-closing install step for the Archipelago runtime client (AP-CLIENT-PLAN.md D5): writes
/// AP's fill into the local DC1 rooms through the same SCD item-record surface the key shuffle
/// uses (id@+0x1c) plus the take-index rekey (+0x20, EXE-SYMBOLS cont.81). Produces patched
/// room files in a mod dir; <see cref="GameInstaller.Install"/> then overlays them with backup
/// exactly like a standalone run. AP's fill REPLACES KeyItemPlacer — no randomizer pass runs.
/// </summary>
public static class ApPlacementInstaller
{
    /// <summary>One record edit: <paramref name="RecOffset"/> is the record's offset in the
    /// decompressed RDT (ap-client-checks.json coordinates == <c>ItemRecord.FileOffset</c>).</summary>
    public sealed record RecordPatch(string Room, int RecOffset, byte ItemId, ushort TakeIndex);

    public sealed record Result(int RoomsWritten, int RecordsPatched);

    /// <summary>
    /// Patch <paramref name="patches"/> into the rooms under <paramref name="dataDir"/> and write
    /// the edited files to <paramref name="outDir"/>. Sources prefer the pristine backup
    /// (<c>Data/.dinorand_backup</c>) over the live file — a previously-installed randomization
    /// must never leak into an AP seed (its record offsets may not even line up).
    /// </summary>
    public static Result WriteRooms(string dataDir, string outDir,
        IReadOnlyList<RecordPatch> patches, Action<string>? log = null)
    {
        Directory.CreateDirectory(outDir);
        var byRoom = patches.GroupBy(p => p.Room.ToLowerInvariant()).ToList();
        int records = 0;
        foreach (var group in byRoom)
        {
            string room = group.Key; // lowercase 4-hex, e.g. "010f"
            int stage = Convert.ToInt32(room[..2], 16);
            int roomNo = Convert.ToInt32(room[2..], 16);
            string path = FindPristineRoom(dataDir, stage, roomNo)
                ?? throw new FileNotFoundException($"AP install: room {room} not found under {dataDir}");

            var rf = RoomFile.ReadFromFile(stage, roomNo, path);
            if (!rf.ParsedCleanly)
                throw new InvalidDataException($"AP install: {Path.GetFileName(path)} did not parse cleanly");
            foreach (var patch in group)
            {
                var rec = rf.Items.FirstOrDefault(i => i.FileOffset == patch.RecOffset)
                    ?? throw new InvalidDataException(
                        $"AP install: {room} has no item record at 0x{patch.RecOffset:x} — "
                        + "ap-client-checks.json is stale vs this install");
                rec.ItemId = patch.ItemId;
                rec.TakeIndex = patch.TakeIndex;
                records++;
            }
            string outPath = Path.Combine(outDir, $"st{stage:x}{roomNo:x2}.dat");
            File.WriteAllBytes(outPath, rf.Write());
            log?.Invoke($"  {Path.GetFileName(outPath)}: {group.Count()} record(s)");
        }
        return new Result(byRoom.Count, records);
    }

    /// <summary>The pristine source for a room: <c>Data/.dinorand_backup</c> copy when one
    /// exists, else the live file. Case-insensitive (dc1-st502-case-glob-bug).</summary>
    private static string? FindPristineRoom(string dataDir, int stage, int room)
    {
        string name = $"st{stage:x}{room:x2}.dat";
        foreach (var dir in new[] { Path.Combine(dataDir, GameInstaller.BackupDirName), dataDir })
        {
            if (!Directory.Exists(dir)) continue;
            var hit = Directory.EnumerateFiles(dir)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }
}
