using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Inserts new entries into a Dino Crisis room package, rebuilding the 16-byte entry table and the
/// sector-aligned payload layout. This is the multi-entry generalisation of
/// <see cref="RoomFile"/>'s grow-the-last-entry repack: it lets a texture-aware import add the
/// donor's relocated texture + palette as <b>new</b> VRAM-upload entries <i>before</i> the RDT (the
/// last entry), which a single-entry grow cannot do (docs/reference/dc1/textures/TEXTURE-IMPORT-VRAM.md §(d)(ii)).
///
/// <para>Each DC1 entry header is <c>u32 type; u32 size; u16 dstX,dstY; u16 W,H</c> (a PSX LoadImage
/// RECT). The loader walks the table from offset 0, advancing the payload cursor by
/// <c>align(size, 2048)</c> per entry and stopping at the first out-of-range type / zero size — so
/// inserting an entry only requires re-emitting the table + payloads in the new order; no payload
/// content changes. Standalone (depends only on <see cref="GianEntryType"/>); never mutates input.</para>
/// </summary>
public static class PackageRepacker
{
    private const int HeaderSize = 2048;
    private const int SectorSize = 2048;

    /// <summary>A new package entry to insert: its <see cref="GianEntryType"/>, VRAM destination rect
    /// and raw payload bytes (already in on-disk form — LZSS-compressed for type 8, raw for type 2).</summary>
    public readonly record struct NewEntry(GianEntryType Type, VramRect Dst, byte[] Payload);

    private readonly record struct Existing(GianEntryType Type, int Size, VramRect Dst, int PayloadOffset);

    /// <summary>
    /// Return a copy of <paramref name="package"/> with <paramref name="inserts"/> added as new
    /// entries immediately before the RDT (the last entry). The relative order of the existing
    /// entries and of the inserts is preserved; the RDT stays last.
    /// </summary>
    public static byte[] InsertEntriesBeforeRdt(ReadOnlySpan<byte> package, IReadOnlyList<NewEntry> inserts)
    {
        var existing = Walk(package);
        if (existing.Count == 0) throw new InvalidOperationException("not a Gian package (no entries)");
        if (inserts.Count == 0) return package.ToArray();

        int total = existing.Count + inserts.Count;
        if (total > HeaderSize / 16)
            throw new InvalidOperationException($"too many entries ({total}) for the {HeaderSize / 16}-slot table");

        // New entry order: [existing except RDT] + [inserts] + [RDT].
        var headers = new List<(GianEntryType Type, VramRect Dst, int Size)>(total);
        var payloads = new List<ReadOnlyMemory<byte>>(total);
        var pkg = package.ToArray();

        for (int i = 0; i < existing.Count - 1; i++)
        {
            var e = existing[i];
            headers.Add((e.Type, e.Dst, e.Size));
            payloads.Add(new ReadOnlyMemory<byte>(pkg, e.PayloadOffset, e.Size));
        }
        foreach (var ins in inserts)
        {
            headers.Add((ins.Type, ins.Dst, ins.Payload.Length));
            payloads.Add(ins.Payload);
        }
        var rdt = existing[^1];
        headers.Add((rdt.Type, rdt.Dst, rdt.Size));
        payloads.Add(new ReadOnlyMemory<byte>(pkg, rdt.PayloadOffset, rdt.Size));

        int size = HeaderSize;
        foreach (var h in headers) size += Align(h.Size);
        var result = new byte[size];

        int pos = HeaderSize;
        for (int i = 0; i < headers.Count; i++)
        {
            int hdr = i * 16;
            var (type, dst, sz) = headers[i];
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(hdr, 4), (uint)type);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(hdr + 4, 4), (uint)sz);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(hdr + 8, 2), (ushort)dst.X);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(hdr + 10, 2), (ushort)dst.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(hdr + 12, 2), (ushort)dst.W);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(hdr + 14, 2), (ushort)dst.H);
            payloads[i].Span.CopyTo(result.AsSpan(pos, sz));
            pos += Align(sz);
        }
        return result;
    }

    /// <summary>
    /// Return a copy of a <b>DC2</b> (32-byte-entry) package with the payload of its single
    /// <paramref name="type"/> entry replaced by <paramref name="newPayload"/> — sizes may differ.
    /// The 2048-byte header is copied verbatim (every reserve dword preserved — e.g. the SOUND
    /// entry's sound-bank index) except the replaced entry's size dword; payloads are re-laid at
    /// sector alignment. DC2 derives payload offsets by walking aligned sizes, so nothing else
    /// needs patching (same scheme as the T8-validated <c>Dc2ScdBlob.RepackWithBlob</c>). Never
    /// mutates the input.
    /// </summary>
    public static byte[] ReplaceEntryDc2(ReadOnlySpan<byte> package, GianEntryType type, ReadOnlySpan<byte> newPayload)
    {
        var pkg = GianPackage.TryParse(package)
                  ?? throw new InvalidDataException("not a recognized Gian package");
        if (!pkg.IsDc2)
            throw new InvalidDataException("not a DC2 (32-byte-entry) Gian package");
        var matches = Enumerable.Range(0, pkg.Entries.Count).Where(i => pkg.Entries[i].Type == type).ToList();
        if (matches.Count != 1)
            throw new InvalidDataException($"package has {matches.Count} {type} entries (expected exactly 1)");
        int idx = matches[0];

        int total = HeaderSize;
        for (int i = 0; i < pkg.Entries.Count; i++)
            total += Align(i == idx ? newPayload.Length : (int)pkg.Entries[i].DeclaredSize);

        var result = new byte[total];
        package.Slice(0, HeaderSize).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(idx * pkg.EntrySize + 4, 4), (uint)newPayload.Length);

        int pos = HeaderSize;
        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            var e = pkg.Entries[i];
            ReadOnlySpan<byte> src = i == idx
                ? newPayload
                : package.Slice(e.PayloadOffset, (int)e.DeclaredSize);
            src.CopyTo(result.AsSpan(pos));
            pos += Align(src.Length);
        }
        return result;
    }

    private static List<Existing> Walk(ReadOnlySpan<byte> file)
    {
        var list = new List<Existing>();
        if (file.Length < HeaderSize) return list;
        int pos = HeaderSize;
        for (int i = 0; i < HeaderSize / 16; i++)
        {
            int hdr = i * 16;
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(hdr, 4));
            uint sz = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(hdr + 4, 4));
            if (type > (uint)GianEntryType.Lzss2 || sz == 0) break;
            if (pos + (long)sz > file.Length) break;
            int x = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 8, 2));
            int y = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 10, 2));
            int w = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 12, 2));
            int h = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(hdr + 14, 2));
            list.Add(new Existing((GianEntryType)type, (int)sz, new VramRect(x, y, w, h), pos));
            pos += Align((int)sz);
            if (pos > file.Length) break;
        }
        return list;
    }

    private static int Align(int v) => (v + SectorSize - 1) & ~(SectorSize - 1);
}
