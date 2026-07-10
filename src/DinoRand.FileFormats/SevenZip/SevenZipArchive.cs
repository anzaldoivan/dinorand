using System;
using System.Collections.Generic;
using System.IO;

namespace DinoRand.FileFormats.SevenZip;

/// <summary>
/// Minimal 7z container reader + rebuild writer, scoped to what Classic REbirth's embedded resource
/// archive needs (docs/reference/dc1/puzzle/REBIRTH-DDRAW-TEXT-STORE-RE.md): non-solid archives where
/// every folder is a single one-coder (Copy / LZMA1 / LZMA2) stream carrying one file. Anything else
/// (bind pairs, multi-stream folders, encryption) is refused loudly — never guessed at.
///
/// <para><see cref="RebuildWithReplacedStreams"/> re-emits the archive reusing every untouched
/// compressed stream byte-verbatim; replaced files are written with the 7z <b>Copy</b> codec (no
/// encoder needed) and re-CRC'd, and the original <c>FilesInfo</c> block (names, order, attributes)
/// is copied through unchanged, so the file↔stream mapping is preserved exactly.</para>
/// </summary>
public sealed class SevenZipArchive
{
    private const int SignatureHeaderSize = 0x20;
    private static readonly byte[] Signature = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

    // property ids (7zFormat.txt)
    private const byte KEnd = 0x00, KHeader = 0x01, KMainStreamsInfo = 0x04, KFilesInfo = 0x05,
        KPackInfo = 0x06, KUnpackInfo = 0x07, KSubStreamsInfo = 0x08, KSize = 0x09, KCrc = 0x0A,
        KFolder = 0x0B, KCodersUnpackSize = 0x0C, KNumUnpackStream = 0x0D, KName = 0x11,
        KEncodedHeader = 0x17;

    /// <summary>One folder == one packed stream == one file's data in this archive family.</summary>
    public sealed class Stream7z
    {
        public byte[] CoderId = Array.Empty<byte>();
        public byte[] CoderProps = Array.Empty<byte>();
        public long PackOffset;   // absolute offset in the archive blob
        public long PackSize;
        public long UnpackSize;
        public uint Crc;
        public bool CrcDefined;
    }

    public List<Stream7z> Streams { get; } = new();

    /// <summary>Archive index (order in <see cref="Streams"/>) per non-empty file, keyed by its
    /// forward-slash-normalized name from FilesInfo.</summary>
    public Dictionary<string, int> StreamIndexByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    private byte[] _archive = Array.Empty<byte>();
    private byte[] _filesInfoRaw = Array.Empty<byte>(); // the kFilesInfo block, copied through verbatim on rebuild

    // ---- number / bit-vector primitives ----

    private sealed class Reader
    {
        public byte[] Buf; public int Pos;
        public Reader(byte[] b, int pos = 0) { Buf = b; Pos = pos; }
        public byte Byte() => Buf[Pos++];

        public long Number()
        {
            byte first = Byte();
            byte mask = 0x80;
            long value = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((first & mask) == 0)
                {
                    value |= (long)(first & (mask - 1)) << (8 * i);
                    return value;
                }
                value |= (long)Byte() << (8 * i);
                mask >>= 1;
            }
            return value;
        }

        public bool[] BitVector(int count)
        {
            var v = new bool[count];
            byte b = 0, mask = 0;
            for (int i = 0; i < count; i++)
            {
                if (mask == 0) { b = Byte(); mask = 0x80; }
                v[i] = (b & mask) != 0;
                mask >>= 1;
            }
            return v;
        }

        public bool[] BoolVectorWithAllDefined(int count)
            => Byte() != 0 ? FilledTrue(count) : BitVector(count);

        private static bool[] FilledTrue(int n) { var v = new bool[n]; Array.Fill(v, true); return v; }

        public uint U32() { uint v = (uint)(Buf[Pos] | Buf[Pos + 1] << 8 | Buf[Pos + 2] << 16 | Buf[Pos + 3] << 24); Pos += 4; return v; }
        public ulong U64() { ulong v = 0; for (int i = 0; i < 8; i++) v |= (ulong)Buf[Pos + i] << (8 * i); Pos += 8; return v; }
    }

    private static void WriteNumber(List<byte> o, long value)
    {
        // minimal 7z variable-length number
        byte firstByte = 0, mask = 0x80;
        int i;
        for (i = 0; i < 8; i++)
        {
            if (value < (1L << (7 * (i + 1))))
            {
                firstByte |= (byte)(value >> (8 * i));
                break;
            }
            firstByte |= mask;
            mask >>= 1;
        }
        o.Add(firstByte);
        for (int k = 0; k < i; k++) o.Add((byte)(value >> (8 * k)));
    }

    private static void WriteU32(List<byte> o, uint v)
    {
        o.Add((byte)v); o.Add((byte)(v >> 8)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 24));
    }

    private static void WriteU64(List<byte> o, ulong v)
    {
        for (int i = 0; i < 8; i++) o.Add((byte)(v >> (8 * i)));
    }

    // ---- parse ----

    public static SevenZipArchive Read(byte[] archive)
    {
        var a = new SevenZipArchive { _archive = archive };
        if (archive.Length < SignatureHeaderSize || !archive.AsSpan(0, 6).SequenceEqual(Signature))
            throw new InvalidDataException("not a 7z archive (bad signature)");
        var r = new Reader(archive, 12);
        long nextHeaderOffset = (long)r.U64();
        long nextHeaderSize = (long)r.U64();
        uint nextHeaderCrc = r.U32();
        long headerAbs = SignatureHeaderSize + nextHeaderOffset;
        if (headerAbs + nextHeaderSize > archive.Length)
            throw new InvalidDataException("7z: header beyond end of archive");
        var header = archive.AsSpan((int)headerAbs, (int)nextHeaderSize).ToArray();
        if (Crc32.Compute(header) != nextHeaderCrc)
            throw new InvalidDataException("7z: header CRC mismatch");

        var hr = new Reader(header);
        byte id = hr.Byte();
        if (id == KEncodedHeader)
        {
            // the header itself is a compressed stream described by a StreamsInfo
            var tmp = new SevenZipArchive { _archive = archive };
            tmp.ParseStreamsInfo(hr);
            if (tmp.Streams.Count != 1)
                throw new InvalidDataException("7z: encoded header must be a single stream");
            byte[] decodedHeader = tmp.ExtractStream(0);
            hr = new Reader(decodedHeader);
            id = hr.Byte();
            header = decodedHeader;
        }
        if (id != KHeader) throw new InvalidDataException($"7z: expected kHeader, got 0x{id:X2}");

        id = hr.Byte();
        if (id == KMainStreamsInfo)
        {
            a.ParseStreamsInfo(hr);
            id = hr.Byte();
        }
        if (id == KFilesInfo)
        {
            int start = hr.Pos - 1;
            a.ParseFilesInfo(hr);
            a._filesInfoRaw = header.AsSpan(start, hr.Pos - start).ToArray();
            id = hr.Byte();
        }
        if (id != KEnd) throw new InvalidDataException($"7z: expected kEnd of header, got 0x{id:X2}");
        return a;
    }

    private void ParseStreamsInfo(Reader r)
    {
        long packPos = 0;
        var packSizes = new List<long>();
        byte id = r.Byte();

        if (id == KPackInfo)
        {
            packPos = r.Number();
            long numPack = r.Number();
            for (id = r.Byte(); id != KEnd; id = r.Byte())
            {
                if (id == KSize)
                {
                    for (long i = 0; i < numPack; i++) packSizes.Add(r.Number());
                }
                else
                {
                    throw new InvalidDataException($"7z: unsupported PackInfo property 0x{id:X2}");
                }
            }
            id = r.Byte();
        }

        if (id != KUnpackInfo) throw new InvalidDataException("7z: missing UnpackInfo");
        if (r.Byte() != KFolder) throw new InvalidDataException("7z: missing kFolder");
        long numFolders = r.Number();
        if (r.Byte() != 0) throw new InvalidDataException("7z: external folder data unsupported");
        for (long f = 0; f < numFolders; f++)
        {
            long numCoders = r.Number();
            if (numCoders != 1)
                throw new InvalidDataException("7z: multi-coder folders unsupported (bind pairs)");
            byte flags = r.Byte();
            int idSize = flags & 0x0F;
            var coderId = new byte[idSize];
            for (int i = 0; i < idSize; i++) coderId[i] = r.Byte();
            if ((flags & 0x10) != 0)
                throw new InvalidDataException("7z: complex coders unsupported");
            byte[] props = Array.Empty<byte>();
            if ((flags & 0x20) != 0)
            {
                long propSize = r.Number();
                props = new byte[propSize];
                for (long i = 0; i < propSize; i++) props[i] = r.Byte();
            }
            Streams.Add(new Stream7z { CoderId = coderId, CoderProps = props });
        }
        if (r.Byte() != KCodersUnpackSize) throw new InvalidDataException("7z: missing kCodersUnpackSize");
        foreach (var s in Streams) s.UnpackSize = r.Number();

        for (byte sub = r.Byte(); sub != KEnd; sub = r.Byte())
        {
            if (sub == KCrc)
            {
                bool[] def = r.BoolVectorWithAllDefined(Streams.Count);
                for (int i = 0; i < Streams.Count; i++)
                    if (def[i]) { Streams[i].Crc = r.U32(); Streams[i].CrcDefined = true; }
            }
            else
            {
                throw new InvalidDataException($"7z: unsupported UnpackInfo property 0x{sub:X2}");
            }
        }

        byte next = r.Byte();
        if (next == KSubStreamsInfo)
        {
            for (byte sub = r.Byte(); sub != KEnd; sub = r.Byte())
            {
                if (sub == KNumUnpackStream)
                {
                    foreach (var s in Streams)
                        if (r.Number() != 1)
                            throw new InvalidDataException("7z: multi-substream folders unsupported (solid archive)");
                }
                else if (sub == KCrc)
                {
                    // digests for streams whose folder CRC is undefined
                    int undefinedCount = 0;
                    foreach (var s in Streams) if (!s.CrcDefined) undefinedCount++;
                    bool[] def = r.BoolVectorWithAllDefined(undefinedCount);
                    int k = 0;
                    foreach (var s in Streams)
                    {
                        if (s.CrcDefined) continue;
                        if (def[k++]) { s.Crc = r.U32(); s.CrcDefined = true; }
                    }
                }
                else if (sub == KSize)
                {
                    throw new InvalidDataException("7z: substream sizes unsupported (solid archive)");
                }
                else
                {
                    throw new InvalidDataException($"7z: unsupported SubStreamsInfo property 0x{sub:X2}");
                }
            }
            next = r.Byte();
        }
        if (next != KEnd) throw new InvalidDataException($"7z: expected end of StreamsInfo, got 0x{next:X2}");

        if (packSizes.Count != Streams.Count)
            throw new InvalidDataException(
                $"7z: {packSizes.Count} pack streams != {Streams.Count} folders (multi-stream folders unsupported)");
        long off = SignatureHeaderSize + packPos;
        for (int i = 0; i < Streams.Count; i++)
        {
            Streams[i].PackOffset = off;
            Streams[i].PackSize = packSizes[i];
            off += packSizes[i];
        }
    }

    private void ParseFilesInfo(Reader r)
    {
        long numFiles = r.Number();
        bool[] emptyStream = new bool[numFiles];
        string[]? names = null;
        while (true)
        {
            long type = r.Number();
            if (type == KEnd) break;
            long size = r.Number();
            int end = r.Pos + (int)size;
            if (type == 0x0E) // kEmptyStream
            {
                emptyStream = r.BitVector((int)numFiles);
            }
            else if (type == KName)
            {
                if (r.Byte() != 0) throw new InvalidDataException("7z: external names unsupported");
                names = new string[numFiles];
                for (long i = 0; i < numFiles; i++)
                {
                    var chars = new List<char>();
                    while (true)
                    {
                        char c = (char)(r.Byte() | (r.Byte() << 8));
                        if (c == 0) break;
                        chars.Add(c);
                    }
                    names[i] = new string(chars.ToArray()).Replace('\\', '/');
                }
            }
            r.Pos = end; // every property block is size-delimited
        }
        if (names is null) throw new InvalidDataException("7z: archive has no file names");
        int streamIdx = 0;
        for (int i = 0; i < numFiles; i++)
        {
            if (emptyStream[i]) continue; // directory / empty file: no stream
            if (streamIdx >= Streams.Count) throw new InvalidDataException("7z: more files than streams");
            StreamIndexByName[names[i]] = streamIdx++;
        }
        if (streamIdx != Streams.Count)
            throw new InvalidDataException($"7z: {streamIdx} non-empty files != {Streams.Count} streams");
    }

    // ---- extraction ----

    /// <summary>Decode stream <paramref name="index"/> and verify it against the archive's own CRC.</summary>
    public byte[] ExtractStream(int index)
    {
        var s = Streams[index];
        var packed = _archive.AsSpan((int)s.PackOffset, (int)s.PackSize);
        var output = new byte[s.UnpackSize];
        if (s.CoderId.Length == 1 && s.CoderId[0] == 0x00)      // Copy
        {
            if (s.PackSize != s.UnpackSize) throw new InvalidDataException("7z: Copy stream size mismatch");
            packed.CopyTo(output);
        }
        else if (s.CoderId.Length == 1 && s.CoderId[0] == 0x21) // LZMA2
        {
            Lzma.DecodeLzma2(packed, output);
        }
        else if (s.CoderId.Length == 3 && s.CoderId[0] == 0x03 && s.CoderId[1] == 0x01 && s.CoderId[2] == 0x01) // LZMA1
        {
            Lzma.DecodeLzma1(s.CoderProps, packed, output);
        }
        else
        {
            throw new InvalidDataException($"7z: unsupported coder {Convert.ToHexString(s.CoderId)}");
        }
        if (s.CrcDefined && Crc32.Compute(output) != s.Crc)
            throw new InvalidDataException($"7z: stream {index} CRC mismatch after decode");
        return output;
    }

    /// <summary>Extract a file by its archive name (forward slashes).</summary>
    public byte[] ExtractFile(string name) => ExtractStream(StreamIndexByName[name]);

    // ---- rebuild ----

    /// <summary>
    /// Re-emit the whole archive with the given streams replaced by new plaintext (stored with the
    /// Copy codec, CRC recomputed); all other packed streams and the FilesInfo block are copied
    /// byte-verbatim. Returns a complete, self-consistent 7z blob (plain uncompressed header).
    /// </summary>
    public byte[] RebuildWithReplacedStreams(IReadOnlyDictionary<int, byte[]> replacements)
    {
        if (_filesInfoRaw.Length == 0)
            throw new InvalidOperationException("7z rebuild requires an archive read with FilesInfo");

        // pack area, folder order preserved
        var pack = new List<byte>();
        var packSizes = new long[Streams.Count];
        var unpackSizes = new long[Streams.Count];
        var crcs = new uint[Streams.Count];
        for (int i = 0; i < Streams.Count; i++)
        {
            if (replacements.TryGetValue(i, out var plain))
            {
                packSizes[i] = plain.Length;
                unpackSizes[i] = plain.Length;
                crcs[i] = Crc32.Compute(plain);
                pack.AddRange(plain);
            }
            else
            {
                var s = Streams[i];
                packSizes[i] = s.PackSize;
                unpackSizes[i] = s.UnpackSize;
                if (!s.CrcDefined)
                    throw new InvalidDataException($"7z rebuild: stream {i} has no CRC to carry over");
                crcs[i] = s.Crc;
                pack.AddRange(_archive.AsSpan((int)s.PackOffset, (int)s.PackSize).ToArray());
            }
        }

        // plain (uncompressed) header
        var h = new List<byte> { KHeader, KMainStreamsInfo, KPackInfo };
        WriteNumber(h, 0);                        // packPos
        WriteNumber(h, Streams.Count);            // numPackStreams
        h.Add(KSize);
        foreach (long sz in packSizes) WriteNumber(h, sz);
        h.Add(KEnd);                              // end PackInfo

        h.Add(KUnpackInfo);
        h.Add(KFolder);
        WriteNumber(h, Streams.Count);
        h.Add(0);                                 // not external
        for (int i = 0; i < Streams.Count; i++)
        {
            WriteNumber(h, 1);                    // numCoders
            if (replacements.ContainsKey(i))
            {
                h.Add(0x01);                      // flags: idSize=1, simple, no attrs
                h.Add(0x00);                      // Copy coder id
            }
            else
            {
                var s = Streams[i];
                bool hasProps = s.CoderProps.Length > 0;
                h.Add((byte)(s.CoderId.Length | (hasProps ? 0x20 : 0x00)));
                h.AddRange(s.CoderId);
                if (hasProps)
                {
                    WriteNumber(h, s.CoderProps.Length);
                    h.AddRange(s.CoderProps);
                }
            }
        }
        h.Add(KCodersUnpackSize);
        foreach (long sz in unpackSizes) WriteNumber(h, sz);
        h.Add(KEnd);                              // end UnpackInfo

        h.Add(KSubStreamsInfo);
        h.Add(KCrc);
        h.Add(1);                                 // all defined
        foreach (uint c in crcs) WriteU32(h, c);
        h.Add(KEnd);                              // end SubStreamsInfo
        h.Add(KEnd);                              // end MainStreamsInfo

        h.AddRange(_filesInfoRaw);                // names/order/attributes, verbatim
        h.Add(KEnd);                              // end Header

        byte[] header = h.ToArray();
        uint headerCrc = Crc32.Compute(header);

        // signature header
        var outBuf = new List<byte>(SignatureHeaderSize + pack.Count + header.Length);
        outBuf.AddRange(Signature);
        outBuf.Add(0); outBuf.Add(4);             // format version 0.4
        var startHeader = new List<byte>();
        WriteU64(startHeader, (ulong)pack.Count); // NextHeaderOffset
        WriteU64(startHeader, (ulong)header.Length);
        WriteU32(startHeader, headerCrc);
        WriteU32(outBuf, Crc32.Compute(startHeader.ToArray())); // StartHeaderCRC
        outBuf.AddRange(startHeader);
        outBuf.AddRange(pack);
        outBuf.AddRange(header);
        return outBuf.ToArray();
    }
}

/// <summary>Standard CRC-32 (reflected, poly 0xEDB88320) — the 7z digest.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
