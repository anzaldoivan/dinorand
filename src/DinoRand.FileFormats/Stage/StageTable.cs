using DinoRand.FileFormats.Primitives;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// A per-stage table file (<c>stN.bin</c>). Empirically (all 9 stages) the leading u32
/// is <b>stageNumber + 4</b> — a small encoded id, NOT a record/pointer count (an
/// earlier scaffold over-fit to <c>st1.bin</c>, whose first records happen to look like
/// <c>0x80130000</c>-based pointers; stages 2–9 show no such pointer header). The
/// remaining layout is stage-specific and still under analysis (see FORMAT.md).
///
/// This type therefore reads the header id and preserves the rest verbatim so a
/// read→write round-trip is byte-exact. Decoding the body is Phase 0 work.
/// </summary>
public sealed class StageTable
{
    /// <summary>PS1 work-RAM base that absolute pointers in DC <c>.dat</c> files use.</summary>
    public const uint RamBase = 0x80130000;

    /// <summary>1-based stage number (the N in stN.bin).</summary>
    public int Stage { get; private set; }

    /// <summary>Leading u32. Observed == <see cref="Stage"/> + 4 across all stages.</summary>
    public uint Header { get; private set; }

    public byte[] OriginalBytes { get; private set; } = Array.Empty<byte>();

    public static StageTable Read(int stage, ReadOnlySpan<byte> bytes)
    {
        var t = new StageTable { Stage = stage, OriginalBytes = bytes.ToArray() };
        if (bytes.Length >= 4)
        {
            var r = new ByteReader(bytes);
            t.Header = r.ReadU32();
        }
        return t;
    }

    /// <summary>Serialize back; byte-exact until the body parser lands.</summary>
    public byte[] Write() => OriginalBytes;

    /// <summary>Rebase a 0x8013xxxx absolute pointer to a file offset (best effort).</summary>
    public static long ToFileOffset(uint ramPointer) => ramPointer - RamBase;

    public static StageTable ReadFromFile(int stage, string path)
        => Read(stage, File.ReadAllBytes(path));
}
