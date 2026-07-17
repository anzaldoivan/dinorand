namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// Emits a Classic REbirth <c>patch\ST*.d2p</c> room patch — the wrapper's decoded non-destructive
/// override slot (docs/reference/dc2/loader/LOADER-DC2.md §5): a flat list of dword-indexed
/// overwrites CR memcpy's over the decompressed SCD blob at <c>0x5e0000</c> on room entry.
/// Because every DinoRand room pass edits that same blob in place
/// (<see cref="Dc2ScdBlob.BlobBaseVa"/>), a <c>.d2p</c> is a pure word-diff of vanilla vs edited —
/// no LZSS, no <c>Data\*.DAT</c> write (docs/parity/NONDESTRUCTIVE-INSTALL-PARITY.md).
///
/// <para>Wire format (byte-cited from the CR apply loop, <c>ddraw.dll 0x1009d990</c>):
/// <c>record = {u24 LE dstWordIndex, u24 LE lenWords, payload[lenWords*4]}</c>, repeated;
/// terminator = <c>dstWordIndex == 0xFFFFFF</c>.</para>
/// </summary>
public static class Dc2D2pWriter
{
    /// <summary>Directory the wrapper probes, relative to the game dir (<c>rebirth\</c>):
    /// <c>patch\ST%X%02X.d2p</c>.</summary>
    public const string PatchDirName = "patch";

    private const int RecordHeaderSize = 6;

    /// <summary>File name CR's hook sprintf's for a room: <c>ST&lt;stageHex&gt;&lt;room2hex&gt;.d2p</c>.</summary>
    public static string FileNameFor(int stage, int room) => $"ST{stage:X}{room:X2}.d2p";

    /// <summary>Word-diff two equal-length decompressed SCD blobs into <c>.d2p</c> bytes: maximal
    /// runs of differing dwords, one record per run. A trailing partial word is compared over its
    /// real bytes and emitted zero-padded (CR's memcpy pads into the room arena past the blob end).
    /// Returns <c>null</c> when the blobs are identical (no patch needed).</summary>
    /// <exception cref="InvalidDataException">Blob lengths differ — a resize cannot be expressed
    /// as in-place word overwrites.</exception>
    public static byte[]? Build(ReadOnlySpan<byte> vanillaBlob, ReadOnlySpan<byte> editedBlob)
    {
        if (vanillaBlob.Length != editedBlob.Length)
            throw new InvalidDataException(
                $"SCD blob length changed ({vanillaBlob.Length} → {editedBlob.Length} bytes); " +
                "a .d2p is in-place word overwrites and cannot express a resize");

        int words = (vanillaBlob.Length + 3) / 4;
        using var ms = new MemoryStream();
        int runStart = -1;
        for (int w = 0; w <= words; w++)                      // <= : a final flush iteration
        {
            bool differs = w < words && !WordEquals(vanillaBlob, editedBlob, w);
            if (differs && runStart < 0)
                runStart = w;
            else if (!differs && runStart >= 0)
            {
                WriteRecord(ms, runStart, w - runStart, editedBlob);
                runStart = -1;
            }
        }
        if (ms.Length == 0) return null;

        for (int i = 0; i < RecordHeaderSize; i++) ms.WriteByte(0xFF);  // terminator: idx 0xFFFFFF
        return ms.ToArray();
    }

    /// <summary>Diff the decompressed LZSS0 SCD blobs of two <c>ST*.DAT</c> Gian packages
    /// (vanilla vs pass-edited). Returns <c>null</c> when the blobs are identical.</summary>
    public static byte[]? BuildFromPackages(ReadOnlySpan<byte> vanillaPackage, ReadOnlySpan<byte> editedPackage)
        => Build(Dc2ScdBlob.Decompress(vanillaPackage), Dc2ScdBlob.Decompress(editedPackage));

    private static bool WordEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int word)
    {
        int start = word * 4;
        int end = Math.Min(start + 4, a.Length);
        return a[start..end].SequenceEqual(b[start..end]);
    }

    private static void WriteRecord(MemoryStream ms, int wordIndex, int lenWords, ReadOnlySpan<byte> edited)
    {
        Span<byte> header = stackalloc byte[RecordHeaderSize];
        header[0] = (byte)wordIndex; header[1] = (byte)(wordIndex >> 8); header[2] = (byte)(wordIndex >> 16);
        header[3] = (byte)lenWords; header[4] = (byte)(lenWords >> 8); header[5] = (byte)(lenWords >> 16);
        ms.Write(header);

        int start = wordIndex * 4;
        int realBytes = Math.Min(lenWords * 4, edited.Length - start);
        ms.Write(edited.Slice(start, realBytes));
        for (int i = realBytes; i < lenWords * 4; i++) ms.WriteByte(0x00);   // partial-tail padding
    }
}
