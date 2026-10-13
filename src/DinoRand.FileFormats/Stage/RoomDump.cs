namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Read-only text dump of a DC1 room file: its package entries, the records the randomizer decodes,
/// and every script instruction with its proven name (<see cref="DcOpcodes.Name"/>; <c>OP_xx</c>
/// where none is established). An inspection aid — it never edits anything.
/// </summary>
public static class RoomDump
{
    public static void Write(TextWriter o, RoomFile room)
    {
        o.WriteLine($"room {room.Stage:X2}{room.Room:X2}");
        if (room.Package is not { } pkg)
        {
            o.WriteLine("  not a recognised room package");
            return;
        }

        o.WriteLine($"package: {pkg.Entries.Count} entries");
        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            var e = pkg.Entries[i];
            string load = e.LoadAddress is { } a ? $"  load 0x{a:X8}" : "";
            string mark = i == pkg.RoomDataIndex ? "  <- room data" : "";
            o.WriteLine($"  [{i}] type {(uint)e.Type} ({e.Type})  {e.DeclaredSize} B{load}{mark}");
        }

        var rdt = room.RdtBuffer;
        var script = RoomScript.Parse(rdt, room.Stage, room.Room);
        o.WriteLine($"room data: {rdt.Length} B decompressed, script {(script.ParsedCleanly ? "walks cleanly" : "DOES NOT walk cleanly")}");

        o.WriteLine($"doors: {script.Doors.Count}");
        foreach (var d in script.Doors)
            o.WriteLine($"  0x{d.FileOffset:X5}  -> {d.TargetStage:X2}{d.TargetRoom:X2}  lock {d.LockId}  type {d.DoorType}");

        o.WriteLine($"items: {script.Items.Count}");
        foreach (var it in script.Items)
            o.WriteLine($"  0x{it.FileOffset:X5}  id 0x{it.ItemId:X2} x{it.Amount}  take {it.TakeIndex}");

        o.WriteLine($"enemies: {script.Enemies.Count}");
        foreach (var e in script.Enemies) WriteCharacter(o, e);

        o.WriteLine($"scripted entrances: {script.ScriptedEntrances.Count}");
        foreach (var e in script.ScriptedEntrances) WriteCharacter(o, e);

        o.WriteLine($"camera sets: {script.CameraShots.Count}");
        foreach (var c in script.CameraShots)
            o.WriteLine($"  0x{c.FileOffset:X5}  {DcOpcodes.Name(c.Opcode)}  at ({c.EyeX}, {c.EyeY}, {c.EyeZ})  " +
                        $"target ({c.TargetX}, {c.TargetY}, {c.TargetZ})");

        var cameraZones = RoomZones.ReadCameraZones(rdt);
        o.WriteLine($"camera zones: {cameraZones.Count}");
        foreach (var z in cameraZones)
            o.WriteLine($"  camera {z.CameraId}  group {z.FloorGroup}  {Quad(z.Area)}");

        var floorZones = RoomZones.ReadFloorZones(rdt);
        o.WriteLine($"floor zones: {floorZones.Count}");
        foreach (var z in floorZones)
            o.WriteLine($"  level {z.Level}  step {z.Step}  {Quad(z.Area)}");

        WriteInstructions(o, rdt);
    }

    private static void WriteCharacter(TextWriter o, EnemyRecord e)
        => o.WriteLine($"  0x{e.FileOffset:X5}  {DcOpcodes.Name(e.Opcode)}  slot {e.Slot}  cat {e.Category}  " +
                       $"{e.Species} ({e.SpeciesBoneCount} bones)  model 0x{e.ModelPtr:X8}");

    private static string Quad(ZoneQuad q)
        => $"({q.X0},{q.Z0}) ({q.X1},{q.Z1}) ({q.X2},{q.Z2}) ({q.X3},{q.Z3})";

    private static void WriteInstructions(TextWriter o, ReadOnlySpan<byte> rdt)
    {
        if (!RoomScript.TryReadFunctionTable(rdt, out int table, out var starts))
        {
            o.WriteLine("script: no function table");
            return;
        }

        o.WriteLine($"script: {starts.Count} subroutines, table at 0x{table:X5}");
        for (int i = 0; i < starts.Count; i++)
        {
            int end = i + 1 < starts.Count ? starts[i + 1] : rdt.Length;
            o.WriteLine($"sub {i} @0x{starts[i]:X5}");
            int pos = starts[i];
            while (pos < end)
            {
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > end)
                {
                    // Normal after the last subroutine: message text and other non-code data follow.
                    o.WriteLine($"  0x{pos:X5}  -- {end - pos} B of non-code data --");
                    break;
                }
                o.WriteLine($"  0x{pos:X5}  {rdt[pos]:X2}  {DcOpcodes.Name(rdt[pos]),-18} " +
                            Convert.ToHexString(rdt.Slice(pos, len)).Chunk(2).Select(c => new string(c))
                                   .Aggregate((a, b) => a + " " + b));
                pos += len;
            }
        }
    }
}
