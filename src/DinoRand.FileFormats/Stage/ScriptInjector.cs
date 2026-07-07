using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// One pc-relative SCD branch found in a room script: the byte offset of its opcode, whether it
/// jumps forward (<c>pc += s16[+2]</c>) or backward (<c>pc -= s16[+2]</c>), and its resolved
/// absolute target offset. Surfaced by <see cref="ScriptInjector.BranchSites"/> so a byte
/// insertion can re-derive each operand from the shifted endpoints.
/// </summary>
public readonly record struct BranchSite(int Offset, bool Forward, int Target);

/// <summary>
/// Inserts a fixed-length record into a room's decompressed RDT SCD script and relocates every
/// reference that the shift would otherwise break — the risky core of the "ADD an enemy to a room"
/// feature (docs/decisions/dc1/enemies/ADD-ENEMY-PLAN.md). DOM-free and enemy-agnostic: it moves bytes and fixes pointers,
/// it does not know about <c>0x20</c> records.
///
/// <para><b>What a shift breaks, and how each is fixed.</b> Inserting <c>K</c> bytes at offset
/// <c>O</c> moves every byte at <c>offset &gt;= O</c> to <c>offset + K</c>. Three kinds of stored
/// reference encode an absolute or relative position and so must be rewritten:</para>
/// <list type="number">
/// <item><b>File-form RDT pointers</b> (<c>0x8010xxxx</c>, the engine's load-time relocation set —
/// see <see cref="SpeciesImporter"/>). Every 4-aligned dword whose target is <c>&gt;= O</c> gains
/// <c>K</c>. Pointers to fixed PSX RAM outside the RDT (e.g. <c>0x80150000</c>) are left alone — they
/// are never relocated. This is the same set the loader relocates, so matching it is correct by
/// construction.</item>
/// <item><b>Function-offset-table entries</b> (RDT header <c>+0x14</c>; each entry is a subroutine
/// offset from the table base — see <see cref="RoomScript"/>). These are <i>not</i> file-form
/// pointers, so they need a separate fixup: every subroutine whose absolute start is <c>&gt;= O</c>
/// gains <c>K</c>. Insertion is required to be <i>after</i> the table (<c>O &gt; tableBase</c>), so
/// the entry storage and the <c>+0x14</c> header pointer themselves never move.</item>
/// <item><b>Pc-relative SCD branch operands.</b> The only branch opcodes that encode a byte offset
/// are <c>0x0a</c> (loop-next, <c>pc -= s16[+2]</c>), <c>0x0c</c> (goto) and <c>0x0e</c>
/// (conditional goto), both <c>pc += s16[+2]</c>. Each is re-derived from its shifted endpoints, so a
/// branch whose opcode and target lie on the same side of <c>O</c> is unchanged and one that straddles
/// <c>O</c> grows/shrinks by <c>K</c>. <c>0x0f</c> (gosub) takes a <b>subroutine index</b>, not a byte
/// offset (<c>tableBase + index*4</c>), so it never needs relocation; <c>0x10</c>/<c>0x04</c>
/// (return / conditional return) and the entity opcodes (<c>0x31</c>/<c>0x54</c>/<c>0x57</c>) carry no
/// byte target. (All control-flow semantics verified from the <c>DINO.exe</c> handlers.)</item>
/// </list>
///
/// <para><b>Alignment.</b> <c>K</c> must be a multiple of 4 so that every dword keeps its 4-alignment
/// after the shift; the 4-aligned pointer scan (and the engine's own loader) then still sees every
/// pointer, including the inserted record's. <c>O</c> must be 4-aligned so the inserted record's own
/// dword fields land 4-aligned.</para>
/// </summary>
public static class ScriptInjector
{
    /// <summary>PSX base the RDT's file-form pointers are relative to (<c>0x80100000</c>).</summary>
    public const uint PsxBase = RoomScript.PsxRdtBase;

    // pc-relative branch opcodes (verified from DINO.exe handlers):
    private const byte LoopNext = 0x0a; // pc -= s16[+2] (backward)
    private const byte Goto = 0x0c;     // pc += s16[+2] (forward)
    private const byte CondGoto = 0x0e; // pc += s16[+2] (forward, conditional)

    /// <summary>
    /// Insert <paramref name="record"/> at 4-aligned offset <paramref name="offset"/> in the
    /// decompressed RDT <paramref name="rdt"/>, relocating file-form pointers, function-table entries
    /// and pc-relative branch operands so the script stays valid. Returns the grown buffer.
    /// </summary>
    /// <exception cref="ArgumentException">When the inputs violate the alignment / placement
    /// invariants (offset not 4-aligned, record length not a positive multiple of 4, no readable
    /// function table, or the offset is not strictly after the table base).</exception>
    public static byte[] Insert(ReadOnlySpan<byte> rdt, int offset, ReadOnlySpan<byte> record)
    {
        int k = record.Length;
        if (k <= 0 || (k & 3) != 0)
            throw new ArgumentException($"record length {k} must be a positive multiple of 4", nameof(record));
        if ((offset & 3) != 0)
            throw new ArgumentException($"offset {offset:x} must be 4-aligned", nameof(offset));
        if (offset < 0 || offset > rdt.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (!TryReadFuncTable(rdt, out int tableBase, out var starts))
            throw new ArgumentException("RDT has no readable function-offset table", nameof(rdt));
        if (offset <= tableBase)
            throw new ArgumentException(
                $"offset {offset:x} must be after the function table at {tableBase:x}", nameof(offset));

        // Branch sites are read from the ORIGINAL buffer (offsets/targets are pre-shift).
        var branches = BranchSites(rdt);

        // 1. Splice the bytes.
        int newLen = rdt.Length + k;
        var result = new byte[newLen];
        rdt[..offset].CopyTo(result);
        record.CopyTo(result.AsSpan(offset, k));
        rdt[offset..].CopyTo(result.AsSpan(offset + k));

        // 2. File-form RDT pointers whose target moved (>= O) gain K.
        for (int o = 0; o + 4 <= newLen; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(o, 4));
            if (v >= PsxBase && v < PsxBase + (uint)newLen && (int)(v - PsxBase) >= offset)
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(o, 4), v + (uint)k);
        }

        // 3. Function-table entries whose subroutine start moved (>= O) gain K. The table is before O
        //    (asserted), so the entry storage at tableBase+i*4 has not moved.
        int n = starts.Count;
        for (int i = 0; i < n; i++)
        {
            int entryOff = tableBase + i * 4;
            uint entry = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(entryOff, 4));
            if (tableBase + (int)entry >= offset)
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(entryOff, 4), entry + (uint)k);
        }

        // 4. Pc-relative branch operands re-derived from the shifted endpoints.
        foreach (var br in branches)
        {
            int a2 = Shift(br.Offset, offset, k);     // shifted opcode offset
            int b2 = Shift(br.Target, offset, k);     // shifted target
            short newOperand = (short)(br.Forward ? b2 - a2 : a2 - b2);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(a2 + 2, 2), newOperand);
        }

        return result;
    }

    private static int Shift(int x, int offset, int k) => x >= offset ? x + k : x;

    /// <summary>
    /// Every pc-relative SCD branch in the script: <c>0x0a</c> (backward) and <c>0x0c</c>/<c>0x0e</c>
    /// (forward). Walks each subroutine declared by the function table with <see cref="DcOpcodes"/>;
    /// a subroutine that derails on trailing non-code data contributes only the branches walked before
    /// the derail (matching <see cref="RoomScript"/>). Returns an empty list if the table is unreadable.
    /// </summary>
    public static IReadOnlyList<BranchSite> BranchSites(ReadOnlySpan<byte> rdt)
    {
        var sites = new List<BranchSite>();
        if (!TryReadFuncTable(rdt, out _, out var starts))
            return sites;

        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i];
            int end = i + 1 < starts.Count ? starts[i + 1] : rdt.Length;
            int pos = start;
            while (pos < end)
            {
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > end) break; // trailing data / derail
                byte op = rdt[pos];
                if (op == Goto || op == CondGoto)
                    sites.Add(new BranchSite(pos, true, pos + BinaryPrimitives.ReadInt16LittleEndian(rdt.Slice(pos + 2, 2))));
                else if (op == LoopNext)
                    sites.Add(new BranchSite(pos, false, pos - BinaryPrimitives.ReadInt16LittleEndian(rdt.Slice(pos + 2, 2))));
                pos += len;
            }
        }
        return sites;
    }

    /// <summary>
    /// The index of the subroutine in which <paramref name="offset"/> lands exactly on an opcode boundary
    /// (so a record can be spliced there without splitting an instruction), or <c>-1</c> if the offset is
    /// not a clean, reachable boundary in any subroutine. Walks each subroutine with <see cref="DcOpcodes"/>
    /// exactly as <see cref="BranchSites"/> does. Used to validate a caller-chosen injection offset (the
    /// <c>--add-enemy-at</c> / event-sub-targeting path) before handing it to <see cref="Insert"/>.
    /// </summary>
    public static int SubroutineAtBoundary(ReadOnlySpan<byte> rdt, int offset)
    {
        if (!TryReadFuncTable(rdt, out _, out var starts)) return -1;
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i];
            int end = i + 1 < starts.Count ? starts[i + 1] : rdt.Length;
            if (offset < start || offset >= end) continue;
            int pos = start;
            while (pos < end)
            {
                if (pos == offset) return i;            // landed exactly on an opcode boundary
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > end) break;  // derailed before reaching the offset
                pos += len;
            }
            return -1; // inside this sub's span but mid-instruction / past a derail
        }
        return -1;
    }

    /// <summary>
    /// Read the self-describing function-offset table at RDT header <c>+0x14</c>: its base offset and
    /// each subroutine's absolute start offset. Mirrors <see cref="RoomScript"/>'s reader.
    /// </summary>
    public static bool TryReadFuncTable(ReadOnlySpan<byte> rdt, out int tableBase, out IReadOnlyList<int> starts)
    {
        tableBase = -1;
        starts = Array.Empty<int>();
        if (rdt.Length < 0x18) return false;

        uint ptr = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(0x14, 4));
        long bo = (long)ptr - PsxBase;
        if (bo < 0 || bo + 4 > rdt.Length) return false;
        int b = (int)bo;

        uint first = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(b, 4));
        if (first == 0 || first % 4 != 0 || b + (long)first > rdt.Length) return false;

        int n = (int)(first / 4);
        var list = new int[n];
        for (int i = 0; i < n; i++)
        {
            uint entry = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(b + i * 4, 4));
            long s = b + (long)entry;
            if (s < 0 || s > rdt.Length) return false;
            list[i] = (int)s;
        }
        tableBase = b;
        starts = list;
        return true;
    }
}
