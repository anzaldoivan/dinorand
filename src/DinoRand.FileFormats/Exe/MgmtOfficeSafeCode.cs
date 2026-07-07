using System;

namespace DinoRand.FileFormats.Exe;

/// <summary>
/// The DC1 Management Office safe (room <c>0103</c>) 4-digit keypad code lever, plus the (not-yet-located)
/// document-code source it must stay in sync with.
///
/// <para><b>Safe code (located + CE-witnessed 2026-07-01).</b> DINO.exe AOT-recompiles the PSX stage
/// overlays and bakes their data globals into <c>.data</c> from the JP source build. The keypad comparator
/// reads a baked code table, NOT the loaded <c>english/Data</c> overlay bytes — which is why an
/// <c>ST1.DAT</c> edit is a no-op. There are 8 identical recompiled copies of the table; the room-0103 safe
/// reads copy #8 (compare instr <c>0x5df43f</c>, table <c>0x668a50</c>), variant/row 0. Digits are stored
/// <c>displayed+1</c> (<c>'0'</c>→<c>0x01</c> … <c>'9'</c>→<c>0x0a</c>); JP row0 = <c>01 04 08 06</c> =
/// <c>"0375"</c>. See <c>docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md</c> §14 and
/// <c>docs/reference/dc1/_registries/EXE-SYMBOLS.md</c> "Recompiled stage-overlay keypads".</para>
///
/// <para><b>Document code (located 2026-07-02).</b> The "Journal of the guardsmen" (Locker Room, room
/// <c>0100</c>) displays the same code as prose. It is a glyph-token stream in <c>st100.dat</c>'s RDT — see
/// <see cref="DinoRand.FileFormats.Stage.MgmtOfficeDocumentCode"/>. A randomizer keeps the two in sync by
/// writing both from the same seed-derived number (<see cref="DeriveFromSeed"/>) — the
/// <b>displayed == checked</b> invariant. See <c>docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md</c> §16.</para>
/// </summary>
public static class MgmtOfficeSafeCode
{
    /// <summary>Number of digits in the code.</summary>
    public const int DigitCount = 4;

    /// <summary>
    /// File offsets of JP row0 in all 8 recompiled copies of the keypad code table. Patching every copy is
    /// the correct lever shape (a single-copy patch left <c>0375</c> working — block order != stage order;
    /// CE read-BP witnessed the room-0103 safe reading copy #8, <see cref="Room0103TableFileOffset"/>).
    /// </summary>
    public static readonly int[] JpRow0FileOffsets =
    {
        0x260858, 0x260ba8, 0x2623a0, 0x263388, 0x264ae0, 0x2678f0, 0x2685ac, 0x268a50,
    };

    /// <summary>The copy the room-0103 safe actually compares against (CE-witnessed): block #8 JP row0.</summary>
    public const int Room0103TableFileOffset = 0x268a50;

    /// <summary>Number of code rows per region half in each copy (the 6 in-game keypad locks).</summary>
    public const int RowCount = 6;

    /// <summary>File offset of a code row within one copy. JP half = rows 0..5, US half = rows 6..11
    /// (<c>copyBase + (region*RowCount + row) * 4</c>).</summary>
    public static int RowFileOffset(int copyBase, int row, bool us)
    {
        if (row is < 0 or >= RowCount) throw new ArgumentOutOfRangeException(nameof(row));
        return copyBase + ((us ? RowCount : 0) + row) * DigitCount;
    }

    /// <summary>Encode a displayed digit (0..9) to its stored table byte (<c>displayed+1</c>).</summary>
    public static byte EncodeDigit(int displayed)
    {
        if (displayed is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(displayed), displayed, "code digit must be 0..9");
        return (byte)(displayed + 1);
    }

    /// <summary>Decode a stored table byte (1..0x0a) back to its displayed digit (0..9).</summary>
    public static int DecodeDigit(byte stored)
    {
        if (stored is < 1 or > 0x0a)
            throw new ArgumentOutOfRangeException(nameof(stored), stored, "stored digit byte must be 1..0x0a");
        return stored - 1;
    }

    /// <summary>
    /// Deterministically derive a 4-digit safe code from a seed. Pure, so the same seed always yields the
    /// same code (the invariant a randomizer relies on to write the SAME number to both the safe and the
    /// document).
    /// </summary>
    public static int[] DeriveFromSeed(int seed)
    {
        // Simple, stable, seed-only derivation. Not cryptographic — just a reproducible 0000..9999 code.
        unchecked
        {
            uint x = (uint)seed * 2654435761u + 0x9E3779B9u;
            var digits = new int[DigitCount];
            for (int i = 0; i < DigitCount; i++)
            {
                x ^= x >> 15;
                x *= 0x2C1B3C6Du;
                x ^= x >> 12;
                digits[i] = (int)(x % 10u);
            }
            return digits;
        }
    }

    /// <summary>Write the code into JP row0 of every recompiled copy (the safe-code lever).</summary>
    public static void WriteSafeCode(Span<byte> exe, ReadOnlySpan<int> digits)
    {
        if (digits.Length != DigitCount)
            throw new ArgumentException($"code must be {DigitCount} digits", nameof(digits));
        Span<byte> encoded = stackalloc byte[DigitCount];
        for (int i = 0; i < DigitCount; i++)
            encoded[i] = EncodeDigit(digits[i]);
        foreach (int fo in JpRow0FileOffsets)
        {
            if (fo + DigitCount > exe.Length)
                throw new ArgumentOutOfRangeException(nameof(exe), "EXE buffer too small for the code table");
            encoded.CopyTo(exe.Slice(fo, DigitCount));
        }
    }

    /// <summary>Read the code the room-0103 safe actually checks (copy #8, JP row0).</summary>
    public static int[] ReadSafeCode(ReadOnlySpan<byte> exe)
    {
        var digits = new int[DigitCount];
        for (int i = 0; i < DigitCount; i++)
            digits[i] = DecodeDigit(exe[Room0103TableFileOffset + i]);
        return digits;
    }

    /// <summary>Write a code row into every recompiled copy. When <paramref name="bothRegions"/> is true
    /// (default) the same digits go to both the JP and US halves, so the runtime region flag can't desync it.</summary>
    public static void WriteRow(Span<byte> exe, int row, ReadOnlySpan<int> digits, bool bothRegions = true)
    {
        if (digits.Length != DigitCount)
            throw new ArgumentException($"code must be {DigitCount} digits", nameof(digits));
        Span<byte> enc = stackalloc byte[DigitCount];
        for (int i = 0; i < DigitCount; i++) enc[i] = EncodeDigit(digits[i]);
        foreach (int copy in JpRow0FileOffsets)
        {
            foreach (bool us in bothRegions ? new[] { false, true } : new[] { false })
            {
                int fo = RowFileOffset(copy, row, us);
                if (fo + DigitCount > exe.Length)
                    throw new ArgumentOutOfRangeException(nameof(exe), "EXE buffer too small for the code table");
                enc.CopyTo(exe.Slice(fo, DigitCount));
            }
        }
    }

    /// <summary>Read a code row from copy #8 (the room-0103 witnessed copy), given half.</summary>
    public static int[] ReadRow(ReadOnlySpan<byte> exe, int row, bool us = false)
    {
        int fo = RowFileOffset(Room0103TableFileOffset, row, us);
        var digits = new int[DigitCount];
        for (int i = 0; i < DigitCount; i++) digits[i] = DecodeDigit(exe[fo + i]);
        return digits;
    }
}
