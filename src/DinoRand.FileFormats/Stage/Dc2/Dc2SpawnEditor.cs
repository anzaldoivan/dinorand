using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// Focused editor for a single Dino Crisis 2 enemy <b>spawn literal operand</b> inside an
/// <c>ST*.DAT</c> room file — the enemy-side twin of <see cref="Dc2DoorEditor"/> and the write half
/// of the T10 lever (docs/reference/dc2/spawn/ENEMY-SPAWNER-RE.md §10 P1; docs/decisions/dc2/RANDO-ROADMAP-PLAN.md Phase 1).
///
/// <para>Enemy placement is a slot-5 SCD program: the spawn opcode <c>0x1a</c> reads species
/// <c>TYPE</c>, position <c>X/Y/Z</c> and <c>SLOT</c> from a block filled by <c>op 0x05</c>
/// push-immediates. The <b>mode-0 (literal)</b> operands are literal little-endian words in the
/// decompressed SCD blob, so they are room-file editable (the same editability class as the door
/// dest). <c>tools/dc2_re/edit_spawn.py</c> pins each editable operand's blob offset into
/// <c>data/dc2/spawn-graph.json</c> (<c>fields[].value_off</c>); this class rewrites the 2-byte word
/// at such an offset and repacks the package.</para>
///
/// <para><b>Scope (D4, "size-preserving first"):</b> this changes a literal <i>value</i> in place,
/// which never changes the operand count — the safe initial write. Reuses <see cref="Dc2ScdBlob"/>
/// for the decompress/re-LZSS/repack plumbing (T8-gated) and never mutates its input.</para>
///
/// <para><b>Caller responsibility:</b> a <c>value_off</c> must come from the spawn graph (a real
/// mode-0 literal operand). Editing <c>TYPE</c> to a species whose model file is not resident in the
/// room will spawn against an unloaded model — only swap to a resident species (the per-room resident
/// <c>E*.DAT</c> set is docs/dc2 T3d, partial). Position edits are always safe.</para>
/// </summary>
public static class Dc2SpawnEditor
{
    /// <summary>Read the 2-byte little-endian operand word at <paramref name="valueOffset"/> in a
    /// <b>decompressed</b> SCD blob (signed: <c>posY</c> can be negative).</summary>
    public static short ReadOperand(ReadOnlySpan<byte> blob, int valueOffset)
    {
        if (valueOffset < 0 || valueOffset + 2 > blob.Length)
            throw new ArgumentOutOfRangeException(nameof(valueOffset),
                $"operand offset 0x{valueOffset:X} + 2 is outside the {blob.Length}-byte blob");
        return BinaryPrimitives.ReadInt16LittleEndian(blob.Slice(valueOffset, 2));
    }

    /// <summary>Decompress the package's SCD blob and read the operand at
    /// <paramref name="valueOffset"/> in one step.</summary>
    public static short ReadOperandFromPackage(ReadOnlySpan<byte> packageBytes, int valueOffset)
        => ReadOperand(Dc2ScdBlob.Decompress(packageBytes), valueOffset);

    /// <summary>
    /// Return a fresh package buffer with the spawn operand word at <paramref name="valueOffset"/>
    /// set to <paramref name="newValue"/> (little-endian): the SCD blob is decompressed, the 2 bytes
    /// are overwritten, the blob is re-LZSS-compressed and the package repacked so it re-parses
    /// cleanly. The input is never mutated.
    /// </summary>
    public static byte[] WriteOperand(ReadOnlySpan<byte> packageBytes, int valueOffset, short newValue)
    {
        var blob = Dc2ScdBlob.Decompress(packageBytes);
        if (valueOffset < 0 || valueOffset + 2 > blob.Length)
            throw new ArgumentOutOfRangeException(nameof(valueOffset),
                $"operand offset 0x{valueOffset:X} + 2 is outside the {blob.Length}-byte blob");
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(valueOffset, 2), newValue);
        return Dc2ScdBlob.RepackWithBlob(packageBytes, blob);
    }

    /// <summary>Read the single byte at <paramref name="offset"/> in the package's decompressed
    /// SCD blob (e.g. a wave descriptor's species TYPE byte at <c>desc+1</c>, K65).</summary>
    public static byte ReadByteFromPackage(ReadOnlySpan<byte> packageBytes, int offset)
    {
        var blob = Dc2ScdBlob.Decompress(packageBytes);
        if (offset < 0 || offset >= blob.Length)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"offset 0x{offset:X} is outside the {blob.Length}-byte blob");
        return blob[offset];
    }

    /// <summary>
    /// Return a fresh package buffer with the single blob byte at <paramref name="offset"/> set to
    /// <paramref name="newValue"/> — the write primitive for the K65 wave-descriptor species lever
    /// (<c>desc+1</c>, one byte: category to load AND spawned <c>[actor+0x58]</c>) and for op-0x05
    /// push MODE bytes (<c>push+1</c>, e.g. mode-6 → mode-0 in <c>Dc2GenericSpawnNormalize</c>).
    /// A word write cannot do these: it would clobber the neighbouring descriptor/operand byte.
    /// Same decompress → edit → re-LZSS → repack path as <see cref="WriteOperand"/> (T8-gated).
    /// </summary>
    public static byte[] WriteByte(ReadOnlySpan<byte> packageBytes, int offset, byte newValue)
    {
        var blob = Dc2ScdBlob.Decompress(packageBytes);
        if (offset < 0 || offset >= blob.Length)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"offset 0x{offset:X} is outside the {blob.Length}-byte blob");
        blob[offset] = newValue;
        return Dc2ScdBlob.RepackWithBlob(packageBytes, blob);
    }

    /// <summary>
    /// Apply a batch of word + byte blob edits in ONE decompress → re-LZSS round trip (a combined
    /// K65 room plan touches ~20 offsets; per-edit repacking would recompress the blob each time).
    /// </summary>
    public static byte[] ApplyEdits(
        ReadOnlySpan<byte> packageBytes,
        IEnumerable<(int Offset, short Value)> wordEdits,
        IEnumerable<(int Offset, byte Value)> byteEdits)
    {
        var blob = Dc2ScdBlob.Decompress(packageBytes);
        foreach (var (off, val) in wordEdits)
        {
            if (off < 0 || off + 2 > blob.Length)
                throw new ArgumentOutOfRangeException(nameof(wordEdits),
                    $"operand offset 0x{off:X} + 2 is outside the {blob.Length}-byte blob");
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(off, 2), val);
        }
        foreach (var (off, val) in byteEdits)
        {
            if (off < 0 || off >= blob.Length)
                throw new ArgumentOutOfRangeException(nameof(byteEdits),
                    $"offset 0x{off:X} is outside the {blob.Length}-byte blob");
            blob[off] = val;
        }
        return Dc2ScdBlob.RepackWithBlob(packageBytes, blob);
    }
}
