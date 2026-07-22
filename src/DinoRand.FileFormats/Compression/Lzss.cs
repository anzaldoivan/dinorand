namespace DinoRand.FileFormats.Compression;

/// <summary>
/// LZSS de/compression as used by Dino Crisis package files (the <c>LZSS0/1/2</c>
/// segment types in the "Gian package" container — see <see cref="Stage.GianPackage"/>).
///
/// The decoder matches the game's algorithm exactly (clean-room from the documented
/// format; verified against real <c>stNXX.dat</c> texture segments):
/// a flag byte is consumed LSB-first, eight tokens per flag; a set bit copies one
/// literal byte; a clear bit reads two bytes <c>ch</c> then <c>t</c> and copies a match
/// of <c>length = (t &gt;&gt; 4) + 2</c> bytes from <c>distance = ((t &amp; 0x0F) &lt;&lt; 8) | ch</c>
/// behind the current output position. Distances are 1..4095, lengths 2..17.
///
/// The compressor is our own; it emits a stream this decoder reproduces exactly
/// (<c>Decompress(Compress(x)) == x</c>), which the unit tests verify. Most DC1 PC rooms
/// store the RDT LZSS-compressed (GPC_UNK: 106/149 rooms; the rest are raw GPC_DATA), so
/// room edits recompress through here — as do DC2 SCD blobs and texture/LZSS segments.
/// </summary>
public static class Lzss
{
    /// <summary>Back-reference window: 12-bit distance field → 1..4095 bytes.</summary>
    public const int WindowSize = 4095;

    /// <summary>Smallest run encodable as a match (length field 0 → 2 bytes).</summary>
    public const int MinMatch = 2;

    /// <summary>Largest encodable match (4-bit length field → 15 + <see cref="MinMatch"/>).</summary>
    public const int MaxMatch = MinMatch + 0x0F;

    /// <summary>Decompress an LZSS stream into raw bytes.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length * 4);
        int pos = 0;

        // flag carries a 0x100 sentinel bit so that after eight LSB-first shifts it
        // returns to 1, signalling that a fresh flag byte must be read.
        int flag = 1;
        while (pos < input.Length)
        {
            if (flag == 1)
            {
                if (pos >= input.Length) break;
                flag = input[pos++] | 0x100;
            }

            if (pos >= input.Length) break;
            int ch = input[pos++];

            if ((flag & 1) != 0)
            {
                // Literal.
                output.Add((byte)ch);
            }
            else
            {
                // Match: read the second byte, decode distance + length.
                if (pos >= input.Length) break;
                int t = input[pos++];
                int distance = ((t & 0x0F) << 8) | ch;
                int length = (t >> 4) + 2;

                int start = output.Count - distance;
                if (start < 0) throw new InvalidDataException("LZSS back-reference before start of stream.");
                for (int k = 0; k < length; k++)
                    output.Add(output[start + k]); // may overlap (run-length style)
            }

            flag >>= 1;
        }

        return output.ToArray();
    }

    /// <summary>
    /// Compress raw bytes into an LZSS stream (greedy longest-match). The match finder walks a
    /// hash chain over 2-byte prefixes instead of brute-scanning the whole 4 KiB window per byte
    /// (which cost seconds per room-sized buffer); candidates are visited nearest-first with the
    /// same strictly-greater acceptance, so the emitted stream is byte-identical to the old scan.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length);
        // head[pair] = most recent position starting with that 2-byte sequence; prev[] chains
        // to older positions. A pair is 16 bits, so the "hash" is exact — no false candidates.
        var head = new int[0x10000];
        Array.Fill(head, -1);
        var prev = new int[input.Length];
        int pos = 0;

        while (pos < input.Length)
        {
            int flagIndex = output.Count;
            output.Add(0); // placeholder for the flag byte
            int flags = 0;

            for (int bit = 0; bit < 8 && pos < input.Length; bit++)
            {
                (int matchDistance, int matchLength) = FindLongestMatch(input, pos, head, prev);

                if (matchLength >= MinMatch)
                {
                    int lenCode = matchLength - MinMatch;        // 0..15
                    int ch = matchDistance & 0xFF;               // low 8 bits of distance
                    int t = ((matchDistance >> 8) & 0x0F) | (lenCode << 4);
                    output.Add((byte)ch);
                    output.Add((byte)t);
                    for (int k = 0; k < matchLength; k++)
                        InsertChain(input, pos + k, head, prev);
                    pos += matchLength;
                    // match → flag bit stays 0
                }
                else
                {
                    flags |= 1 << bit; // literal → flag bit set
                    InsertChain(input, pos, head, prev);
                    output.Add(input[pos++]);
                }
            }

            output[flagIndex] = (byte)flags;
        }

        return output.ToArray();
    }

    /// <summary>Record <paramref name="pos"/> as the newest occurrence of its 2-byte prefix.</summary>
    private static void InsertChain(ReadOnlySpan<byte> input, int pos, int[] head, int[] prev)
    {
        if (pos + 1 >= input.Length) return; // no pair starts at the last byte
        int h = input[pos] | (input[pos + 1] << 8);
        prev[pos] = head[h];
        head[h] = pos;
    }

    private static (int distance, int length) FindLongestMatch(
        ReadOnlySpan<byte> input, int pos, int[] head, int[] prev)
    {
        if (pos + 1 >= input.Length)
            return (0, 0); // a match needs at least 2 bytes of lookahead

        int bestLength = 0;
        int bestDistance = 0;
        int windowStart = Math.Max(0, pos - WindowSize);

        // Chain positions are strictly decreasing, so the first entry below the window ends it.
        // Every candidate shares pos's 2-byte prefix, so each match is at least MinMatch long;
        // strictly-greater acceptance keeps the NEAREST start on ties, like the old full scan.
        int h = input[pos] | (input[pos + 1] << 8);
        for (int start = head[h]; start >= windowStart; start = prev[start])
        {
            int length = MinMatch;
            while (length < MaxMatch
                   && pos + length < input.Length
                   && input[start + length] == input[pos + length])
            {
                length++;
            }

            if (length > bestLength)
            {
                bestLength = length;
                bestDistance = pos - start;
                if (length == MaxMatch) break;
            }
        }

        return (bestDistance, bestLength);
    }
}
