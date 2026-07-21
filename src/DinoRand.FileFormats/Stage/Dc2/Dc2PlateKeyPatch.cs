using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// Re-keys the Regina Key-Plate TERMINAL of Dino Crisis 2 (ST205, the blue-panel terminal;
/// docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 4, decode K118): which colour key-plate the
/// terminal accepts is 100% room-file SAT-9 routing. ST205 slot-5 <c>routine[2]</c> registers 18
/// op-0x39 use-plate records (6 per puzzle-state × 3 states); each record's <c>block+0x00</c> = the
/// routine index it fires and <c>block+0x08</c> = the required plate catalog id (0x25–0x2a). Exactly
/// one plate per state routes to an ACCEPT routine (r13 terminal-1, r20 terminal-2); the rest route to
/// per-plate REJECT routines (SetFlag(9,plate,1)+gosub r19 "not correct for this terminal"). The
/// vanilla blue terminal (Group B) accepts BLUE via r20.
///
/// <para>The re-key lever permutes ONLY the <c>block+0x00</c> routine-idx literals of Group B: the
/// seed-chosen plate's record is pointed at the accept routine (r20) and BLUE is demoted to its own
/// reject routine (r16). The <c>block+0x08</c> plate ids are NEVER touched, so every reject routine
/// stays coupled to the plate it sets — the only clean length-preserving permutation (K118 (c);
/// swapping plate ids or naïvely swapping routine ids would mis-set a reject routine's flag(9)). Plus
/// a hue-shift of the ST205 blue-family panel palette (entry 13) so the visual matches the new colour.
/// Same room-blob literal-edit class as the shipped circuit lever (<see cref="Dc2CircuitPatch"/>).</para>
/// </summary>
public static class Dc2PlateKeyPatch
{
    // Plate catalog ids (K113, locked).
    public const int Green = 0x25, Blue = 0x26, Red = 0x27, Yellow = 0x28, White = 0x29, Purple = 0x2a;

    /// <summary>The six key-plate catalog ids, in flag-ordinal order (ordinal = id − 0x25).</summary>
    public static readonly IReadOnlyList<int> PlateIds = new[] { Green, Blue, Red, Yellow, White, Purple };

    /// <summary>The plate the vanilla blue terminal accepts (Group B → r20).</summary>
    public const int VanillaCorrectPlate = Blue;

    /// <summary>Slot-5 routine (sorted index) that registers the terminal's op-0x39 records.</summary>
    public const int ScriptRoutineIndex = 2;

    /// <summary>Accept routine of the blue terminal (Group B). Its record fires the success sequence.</summary>
    public const int Terminal2AcceptRoutine = 20;

    /// <summary>ST205 package entry holding the blue-violet slot-panel CLUT (K113 ent12 texture,
    /// entry 13 = its paired PALETTE; 128 B = 64 BGR555 entries).</summary>
    public const int PaletteEntryIndex = 13;

    /// <summary>The vanilla 18-record signature of routine[2], in program order: (plate id, routine
    /// idx). Group A (terminal-1, S1) = [0..5], Group B (blue terminal, S2) = [6..11] with the accept
    /// at [6], Group C (terminal-2 locked, S3) = [12..17]. Decoded from ST205.DAT 2026-07-18 (K118).
    /// A room that does not present exactly this is refused (see <see cref="LocateRecords"/>).</summary>
    public static readonly IReadOnlyList<(int Plate, int Routine)> VanillaRecords = new (int, int)[]
    {
        (Green, 13), (Red, 14), (Yellow, 15), (Blue, 16), (White, 17), (Purple, 18),   // Group A (terminal-1)
        (Blue, 20), (Red, 14), (Yellow, 15), (Green, 21), (White, 17), (Purple, 18),   // Group B (blue terminal)
        (Red, 14), (Yellow, 15), (Blue, 16), (Green, 21), (White, 17), (Purple, 18),   // Group C (terminal-2 locked)
    };

    /// <summary>Each plate's dedicated REJECT routine (SetFlag(9,plate,1)+gosub r19). GREEN=r21,
    /// BLUE=r16, RED=r14, YELLOW=r15, WHITE=r17, PURPLE=r18 (K118 classification (b)). Demoting a
    /// plate off the accept routine points its record here, keeping routine↔plate coupling intact.</summary>
    public static int RejectRoutineOf(int plate) => plate switch
    {
        Green => 21, Blue => 16, Red => 14, Yellow => 15, White => 17, Purple => 18,
        _ => throw new ArgumentOutOfRangeException(nameof(plate), $"0x{plate:X} is not a key-plate id"),
    };

    /// <summary>One located op-0x39 record: the op offset, the routine-idx literal (block+0x00, the
    /// re-key target) and its blob offset, and the required plate id (block+0x08) and its offset — all
    /// decompressed-blob byte offsets.</summary>
    public readonly record struct Record(int OpOffset, int RoutineIdxOffset, int RoutineIndex, int PlateId, int PlateIdOffset);

    /// <summary>One routine-idx literal rewrite (decompressed-blob offset + old/new 16-bit value).</summary>
    public readonly record struct RoutingEdit(int Offset, int OldValue, int NewValue);

    /// <summary>What <see cref="ApplyRoom"/> did.</summary>
    public readonly record struct Result(int TargetPlate, int VanillaPlate, bool Changed, RoutingEdit[] RoutingEdits);

    /// <summary>Uniformly pick one of the six key-plate catalog ids for the terminal to accept.</summary>
    public static int SelectRequiredPlate(Random rng) => PlateIds[rng.Next(PlateIds.Count)];

    /// <summary>Validate an explicit key-plate plan before room application.</summary>
    internal static void ValidatePlan(int targetPlate)
    {
        if (!PlateIds.Contains(targetPlate))
            throw new ArgumentOutOfRangeException(nameof(targetPlate), $"0x{targetPlate:X} is not a key-plate id");
    }

    /// <summary>Walk <see cref="ScriptRoutineIndex"/> of a <b>decompressed</b> SCD blob, collect its 18
    /// op-0x39 records, and verify they match <see cref="VanillaRecords"/> exactly. Throws
    /// <see cref="InvalidOperationException"/> ("refusing to re-key") on any deviation.</summary>
    public static Record[] LocateRecords(byte[] blob)
    {
        var records = CollectRecords(blob);
        if (records.Length != VanillaRecords.Count)
            throw new InvalidOperationException(
                $"ST205 routine[{ScriptRoutineIndex}]: found {records.Length} op-0x39 records, expected {VanillaRecords.Count} — not the recognized vanilla script; refusing to re-key plate door.");
        for (int i = 0; i < VanillaRecords.Count; i++)
            if (records[i].PlateId != VanillaRecords[i].Plate || records[i].RoutineIndex != VanillaRecords[i].Routine)
                throw new InvalidOperationException(
                    $"ST205 routine[{ScriptRoutineIndex}] record {i}: (plate 0x{records[i].PlateId:X2}, routine {records[i].RoutineIndex}) "
                    + $"!= vanilla (plate 0x{VanillaRecords[i].Plate:X2}, routine {VanillaRecords[i].Routine}) — refusing to re-key plate door.");
        return records;
    }

    /// <summary>Return a fresh package with the blue terminal re-keyed so <paramref name="targetPlate"/>
    /// is accepted (routing permutation) and its blue panel recoloured to match. Byte-identical when
    /// <paramref name="targetPlate"/> == <see cref="VanillaCorrectPlate"/>. Never mutates its input.</summary>
    public static byte[] ApplyRoom(ReadOnlySpan<byte> packageBytes, int targetPlate, out Result result)
    {
        ValidatePlan(targetPlate);

        var records = LocateRecords(Dc2ScdBlob.Decompress(packageBytes)); // validates the vanilla signature
        if (targetPlate == VanillaCorrectPlate)
        {
            result = new Result(targetPlate, VanillaCorrectPlate, false, Array.Empty<RoutingEdit>());
            return packageBytes.ToArray(); // blue is already correct — no-op, byte-identical
        }

        // Group B = the six records around the unique accept routine (r20). Promote the seed plate's
        // record to the accept routine; demote blue to its own reject routine. block+0x08 untouched.
        int acceptIdx = Array.FindIndex(records, r => r.RoutineIndex == Terminal2AcceptRoutine);
        var groupB = records.Skip(acceptIdx).Take(6).ToArray();
        var acceptRec = groupB[0];                                     // blue → r20
        var targetRec = groupB.Single(r => r.PlateId == targetPlate);  // seed plate → its reject routine

        var routing = new[]
        {
            new RoutingEdit(acceptRec.RoutineIdxOffset, acceptRec.RoutineIndex, RejectRoutineOf(VanillaCorrectPlate)),
            new RoutingEdit(targetRec.RoutineIdxOffset, targetRec.RoutineIndex, Terminal2AcceptRoutine),
        };
        var wordEdits = routing.Select(e => (e.Offset, (short)e.NewValue));
        byte[] pkg = Dc2SpawnEditor.ApplyEdits(packageBytes, wordEdits, Array.Empty<(int, byte)>());

        // recolour the blue panel to match the new correct colour (entry 13, uncompressed palette)
        var gp = GianPackage.TryParse(pkg)
                 ?? throw new InvalidOperationException("ST205: not a recognized Gian package; refusing to re-key plate door.");
        if (PaletteEntryIndex >= gp.Entries.Count || gp.Entries[PaletteEntryIndex].Type != GianEntryType.Palette)
            throw new InvalidOperationException(
                $"ST205: entry {PaletteEntryIndex} is not a PALETTE — not the recognized vanilla script; refusing to re-key plate door.");
        var e0 = gp.Entries[PaletteEntryIndex];
        var recolored = RecolorPanelPalette(pkg.AsSpan(e0.PayloadOffset, (int)e0.DeclaredSize), targetPlate);
        pkg = PackageRepacker.ReplaceEntryDc2(pkg, PaletteEntryIndex, recolored);

        result = new Result(targetPlate, VanillaCorrectPlate, true, routing);
        return pkg;
    }

    /// <summary>Hue-shift the blue-family entries of a 64-entry BGR555 panel palette toward
    /// <paramref name="targetPlate"/>'s colour, leaving grey/other entries and the length untouched.
    /// Byte-identical when <paramref name="targetPlate"/> == <see cref="VanillaCorrectPlate"/> (blue).</summary>
    public static byte[] RecolorPanelPalette(ReadOnlySpan<byte> palette, int targetPlate)
    {
        var outp = palette.ToArray();
        if (targetPlate == VanillaCorrectPlate) return outp; // blue = the vanilla panel hue

        var (wantR, wantG, wantB) = ChromaticChannels(targetPlate);
        for (int i = 0; i + 1 < outp.Length; i += 2)
        {
            int v = outp[i] | (outp[i + 1] << 8);
            int r = v & 0x1f, g = (v >> 5) & 0x1f, b = (v >> 10) & 0x1f;
            if (!(b > g && b >= r && b >= 8)) continue; // blue-family only (K118 (d) classification)

            // desaturated base (grey floor) + the blue chroma re-cast onto the target's channels.
            int lo = Math.Min(r, Math.Min(g, b)), chroma = b - lo;
            int nr = Math.Min(31, lo + (wantR ? chroma : 0));
            int ng = Math.Min(31, lo + (wantG ? chroma : 0));
            int nb = Math.Min(31, lo + (wantB ? chroma : 0));
            int nv = (nr & 0x1f) | ((ng & 0x1f) << 5) | ((nb & 0x1f) << 10) | (v & 0x8000); // keep the STP bit
            outp[i] = (byte)nv; outp[i + 1] = (byte)(nv >> 8);
        }
        return outp;
    }

    /// <summary>Which BGR555 channels carry chroma for a plate colour (grey = none).</summary>
    private static (bool R, bool G, bool B) ChromaticChannels(int plate) => plate switch
    {
        Red => (true, false, false),
        Green => (false, true, false),
        Blue => (false, false, true),
        Yellow => (true, true, false),
        White => (true, true, true),
        Purple => (true, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(plate), $"0x{plate:X} is not a key-plate id"),
    };

    // ---- slot-5 walker (self-contained C# port of tools/dc2_re/decode_script.py, mirrors
    // Dc2CircuitPatch; ponytail: duplicated per-patch like the reference walker — extract to a shared
    // Dc2ScriptWalker only if a third consumer appears) ---------------------------------------------

    /// <summary>Collect the op-0x39 records of routine[<see cref="ScriptRoutineIndex"/>] WITHOUT the
    /// vanilla-signature check (so a re-keyed blob can be re-read). block+0x00 (routine idx) = the last
    /// push, block+0x08 (plate id) = the 3rd-from-last push (op-0x39 builder 0x489F40, K118).</summary>
    private static Record[] CollectRecords(byte[] blob)
    {
        var (start, size) = ScriptSection(blob);
        var routines = RoutineBounds(blob, start, size);
        if (ScriptRoutineIndex >= routines.Count)
            throw new InvalidOperationException(
                $"ST205: slot-5 has only {routines.Count} routines, expected at least {ScriptRoutineIndex + 1} — refusing to re-key plate door.");
        var (rs, re) = routines[ScriptRoutineIndex];

        var records = new List<Record>();
        var pushRun = new List<int>();
        int ip = rs;
        while (ip < re)
        {
            byte op = blob[ip];
            if (op == 0x39 && pushRun.Count >= 3)
            {
                int pRoutine = pushRun[^1], pPlate = pushRun[^3];
                records.Add(new Record(ip, pRoutine + 2,
                    BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(pRoutine + 2, 2)),
                    BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(pPlate + 2, 2)),
                    pPlate + 2));
            }
            if (op == 0x05) pushRun.Add(ip); else pushRun.Clear();
            int len = OpLength(op);
            if (len == 0) break; // RETURN or an op outside the walkable set
            ip += len;
        }
        return records.ToArray();
    }

    /// <summary>Slot-5 bounds: directory entry [5] (u32 VA at blob+0x14, base 0x5e0000); section end =
    /// the next-higher directory pointer, else blob end.</summary>
    private static (int Start, int Size) ScriptSection(byte[] blob)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x14, 4));
        if (v < Dc2ScdBlob.BlobBaseVa || v >= Dc2ScdBlob.BlobBaseVa + (uint)blob.Length)
            throw new InvalidOperationException("ST205: no slot-5 script section (directory entry 5 out of range) — refusing to re-key plate door.");
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

    /// <summary>Self-bounding routine directory at <c>start+0x1c</c>; returns [start,end) bounds sorted
    /// ascending — the routine indexing K118 uses.</summary>
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

    /// <summary>Opcode byte lengths (opcode_lengths.py); 0 = stop the walk (RETURN 0x04 or an opcode
    /// outside the walkable set). op-0x39 (SAT-9) is a 2-byte commit like the other 0x10–0x59 ops.</summary>
    private static int OpLength(byte op) => op switch
    {
        0x01 or 0x02 or 0x03 => 6,
        0x05 or 0x06 or 0x07 or 0x08 => 4,
        0x19 => 4,
        >= 0x10 and <= 0x59 => 2,
        _ => 0,
    };
}
