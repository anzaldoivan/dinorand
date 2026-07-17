using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// Shuffles the stungun "circuit" blink choreography of Dino Crisis 2's two circuit minigames
/// (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 2, decode K110): the blink patterns are
/// FIXED scripted sequences in the room files — slot-5 routines made of
/// <c>SetFlag(7, boxId, 1)</c> runs, one flag per box, each blink a 3-push (<c>group, id, value</c>
/// literals) + op-<c>0x1c</c> record. The editable box-id literal is the middle push's 16-bit value
/// (at push offset + 2). This class locates those literals by WALKING the script (never hardcoded
/// offsets), pins the vanilla sequence before touching anything, and rewrites the ids in place —
/// sequence LENGTH, wait/compare cadence, terminator (<c>SetFlag(7,23/26,1)</c>) and every other
/// byte untouched. Same room-blob literal-edit class as the shipped spawn lever
/// (<see cref="Dc2SpawnEditor"/>, whose <see cref="Dc2SpawnEditor.ApplyEdits"/> does the write).
/// </summary>
public static class Dc2CircuitPatch
{
    /// <summary>One blink routine: its index in the room's sorted slot-5 routine table, the vanilla
    /// blink id sequence (the pre-write pin), and the terminator flag id that must stay untouched.</summary>
    public sealed record RoutineSpec(int RoutineIndex, IReadOnlyList<int> VanillaIds, int TerminatorId);

    /// <summary>One circuit room: its <c>ST*.DAT</c> file, stage/room ids, the box-id alphabet the
    /// generated sequence draws from, and its blink routines.</summary>
    public sealed record RoomSpec(string FileName, int Stage, int Room,
                                  IReadOnlyList<int> BoxIds, IReadOnlyList<RoutineSpec> Routines);

    /// <summary>The two circuit rooms (K110, vanilla sequences byte-verified 2026-07-17):
    /// ST607 generator stabilizer (3 boxes, routines 7/8) and ST402 Missile Silo bridge
    /// (5 boxes, routines 23/24). Completion/retry routines (ST607 r6/r16, ST402 r16/r22)
    /// are deliberately absent — they must never be touched.</summary>
    public static readonly IReadOnlyList<RoomSpec> Rooms = new[]
    {
        new RoomSpec("ST607.DAT", 6, 7, new[] { 17, 18, 19 }, new[]
        {
            new RoutineSpec(7, new[] { 17, 18, 19, 18, 19, 18, 17, 19, 19, 17, 18, 17, 19, 17, 19, 18 }, 23),
            new RoutineSpec(8, new[] { 17, 18, 19, 17, 19, 18, 19, 18, 17, 18 }, 23),
        }),
        new RoomSpec("ST402.DAT", 4, 2, new[] { 16, 17, 18, 19, 20 }, new[]
        {
            new RoutineSpec(23, new[] { 16, 18, 20, 18, 16, 18, 19, 16, 20, 19, 18, 20, 17, 17, 18, 16, 20, 17, 19, 16, 18 }, 26),
            new RoutineSpec(24, new[] { 16, 18, 20, 17, 18, 19, 20, 19, 16, 17, 18, 17, 19, 16 }, 26),
        }),
    };

    /// <summary>What one routine's shuffle did.</summary>
    public readonly record struct RoutineResult(int RoutineIndex, int[] OldIds, int[] NewIds);

    /// <summary>
    /// Return a fresh package buffer with <paramref name="room"/>'s blink id literals rewritten to
    /// seed-derived sequences (same length, every box id at least once, no two identical ids
    /// adjacent). Pins the vanilla sequences first (<see cref="LocateBlinkIdOffsets"/>) and writes
    /// nothing on mismatch. Never mutates its input.
    /// </summary>
    public static byte[] ShuffleRoom(ReadOnlySpan<byte> packageBytes, RoomSpec room, Random rng,
                                     out RoutineResult[] results)
    {
        var blob = Dc2ScdBlob.Decompress(packageBytes);
        var edits = new List<(int Offset, short Value)>();
        var res = new List<RoutineResult>(room.Routines.Count);
        foreach (var spec in room.Routines)
        {
            int[] offs = LocateBlinkIdOffsets(blob, room, spec);
            int[] seq = GenerateSequence(room.BoxIds, offs.Length, rng);
            for (int i = 0; i < offs.Length; i++)
                edits.Add((offs[i], (short)seq[i]));
            res.Add(new RoutineResult(spec.RoutineIndex, spec.VanillaIds.ToArray(), seq));
        }
        results = res.ToArray();
        return Dc2SpawnEditor.ApplyEdits(packageBytes, edits, Array.Empty<(int, byte)>());
    }

    /// <summary>
    /// Locate the blink box-id literal offsets of <paramref name="spec"/> in a <b>decompressed</b>
    /// SCD blob by walking the routine's opcodes, and verify the pre-write pin: the routine must
    /// hold exactly the vanilla blink sequence plus its terminator (<c>SetFlag(7,TerminatorId,1)</c>
    /// last). Throws <see cref="InvalidOperationException"/> with a clear message otherwise —
    /// the room file is then not the recognized vanilla script and nothing may be written.
    /// Returned offsets exclude the terminator's.
    /// </summary>
    public static int[] LocateBlinkIdOffsets(byte[] blob, RoomSpec room, RoutineSpec spec)
    {
        var (start, size) = ScriptSection(blob, room);
        var routines = RoutineBounds(blob, start, size);
        if (spec.RoutineIndex >= routines.Count)
            throw new InvalidOperationException(
                $"{room.FileName}: slot-5 has only {routines.Count} routines, expected at least {spec.RoutineIndex + 1} — not the recognized vanilla script; refusing to shuffle circuits.");
        var (rs, re) = routines[spec.RoutineIndex];

        // Linear opcode walk collecting each op-0x1c SetFlag whose 3 preceding ops are literal
        // pushes (program order: group, id, value — K110). Stops at RETURN/unknown like the
        // reference walker (tools/dc2_re/decode_script.py).
        var flags = new List<(int IdOff, short Group, short Id, short Value)>();
        var pushRun = new List<int>();
        int ip = rs;
        while (ip < re)
        {
            byte op = blob[ip];
            if (op == 0x1c && pushRun.Count >= 3)
            {
                int pGroup = pushRun[^3], pId = pushRun[^2], pVal = pushRun[^1];
                if (blob[pGroup + 1] == 0 && blob[pId + 1] == 0 && blob[pVal + 1] == 0) // mode 0 = literal
                    flags.Add((pId + 2,
                        BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(pGroup + 2, 2)),
                        BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(pId + 2, 2)),
                        BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(pVal + 2, 2))));
            }
            if (op == 0x05) pushRun.Add(ip);
            else pushRun.Clear();
            int len = OpLength(op);
            if (len == 0) break; // RETURN or an op outside the walkable set
            ip += len;
        }

        var group7 = flags.Where(f => f is { Group: 7, Value: 1 }).ToList();
        if (group7.Count != spec.VanillaIds.Count + 1)
            throw new InvalidOperationException(
                $"{room.FileName} routine[{spec.RoutineIndex}]: found {group7.Count} SetFlag(7,·,1) records, expected {spec.VanillaIds.Count} blinks + 1 terminator — not the recognized vanilla script; refusing to shuffle circuits.");
        if (group7[^1].Id != spec.TerminatorId)
            throw new InvalidOperationException(
                $"{room.FileName} routine[{spec.RoutineIndex}]: last SetFlag(7,·,1) id is {group7[^1].Id}, expected terminator {spec.TerminatorId} — not the recognized vanilla script; refusing to shuffle circuits.");
        for (int i = 0; i < spec.VanillaIds.Count; i++)
            if (group7[i].Id != spec.VanillaIds[i])
                throw new InvalidOperationException(
                    $"{room.FileName} routine[{spec.RoutineIndex}]: blink {i} id is {group7[i].Id} (at blob 0x{group7[i].IdOff:X}), expected vanilla {spec.VanillaIds[i]} — not the recognized vanilla script; refusing to shuffle circuits.");

        return group7.Take(spec.VanillaIds.Count).Select(f => f.IdOff).ToArray();
    }

    /// <summary>
    /// A seed-derived blink sequence over <paramref name="alphabet"/>: exact <paramref name="length"/>,
    /// no two identical ids adjacent (a repeated blink is invisible to the player), and every id
    /// present at least once (retry until covered — overwhelmingly the first draw at these lengths).
    /// </summary>
    public static int[] GenerateSequence(IReadOnlyList<int> alphabet, int length, Random rng)
    {
        if (alphabet.Count < 2 || length < alphabet.Count)
            throw new ArgumentException($"need ≥2 box ids and length ≥ alphabet size (got {alphabet.Count} ids, length {length})");
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            var seq = new int[length];
            int prev = -1;
            for (int i = 0; i < length; i++)
            {
                int pick;
                do pick = alphabet[rng.Next(alphabet.Count)];
                while (pick == prev);
                seq[i] = pick;
                prev = pick;
            }
            if (alphabet.All(seq.Contains))
                return seq;
        }
        throw new InvalidOperationException("could not generate a covering blink sequence"); // unreachable in practice
    }

    // ---- slot-5 walker (C# port of tools/dc2_re/decode_script.py script_section/subprograms) ----

    /// <summary>Slot-5 bounds: directory entry [5] (u32 VA at blob+0x14, base 0x5e0000); section
    /// end = the next-higher directory pointer, else blob end.</summary>
    private static (int Start, int Size) ScriptSection(byte[] blob, RoomSpec room)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x14, 4));
        if (v < Dc2ScdBlob.BlobBaseVa || v >= Dc2ScdBlob.BlobBaseVa + (uint)blob.Length)
            throw new InvalidOperationException(
                $"{room.FileName}: no slot-5 script section (directory entry 5 out of range) — refusing to shuffle circuits.");
        int start = (int)(v - Dc2ScdBlob.BlobBaseVa);
        int end = blob.Length;
        for (int i = 0; i < 0x80; i += 4)
        {
            uint p = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(i, 4));
            if (p < Dc2ScdBlob.BlobBaseVa || p >= Dc2ScdBlob.BlobBaseVa + (uint)blob.Length) continue;
            int o = (int)(p - Dc2ScdBlob.BlobBaseVa);
            if (o > start && o < end) end = o;
        }
        return (start, end - start);
    }

    /// <summary>Self-bounding routine directory at <c>start+0x1c</c> (entries occupy bytes below the
    /// lowest routine offset seen so far); returns [start,end) bounds sorted ascending — the same
    /// routine indexing K110 uses.</summary>
    private static List<(int Start, int End)> RoutineBounds(byte[] blob, int start, int size)
    {
        int opbase = start + 0x1c;
        var offs = new List<int>();
        int bound = size, i = 0;
        while (i * 4 < bound)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(opbase + i * 4, 4));
            if (v >= (uint)size) break;
            offs.Add((int)v);
            bound = Math.Min(bound, (int)v);
            i++;
        }
        var starts = offs.Distinct().OrderBy(o => o).Select(o => opbase + o).ToList();
        var res = new List<(int, int)>(starts.Count);
        for (int k = 0; k < starts.Count; k++)
            res.Add((starts[k], k + 1 < starts.Count ? starts[k + 1] : start + size));
        return res;
    }

    /// <summary>Opcode byte lengths from the derived table (opcode_lengths.py); 0 = stop the walk
    /// (RETURN 0x04 or an opcode outside the walkable set).</summary>
    private static int OpLength(byte op) => op switch
    {
        0x01 or 0x02 or 0x03 => 6,
        0x05 or 0x06 or 0x07 or 0x08 => 4,
        0x19 => 4, // jump — treated as 4 for a linear listing, like the reference walker
        >= 0x10 and <= 0x59 => 2,
        _ => 0,
    };
}
