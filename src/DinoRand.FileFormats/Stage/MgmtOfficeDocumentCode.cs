using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// The DC1 "Journal of the guardsmen" document code lever — the prose combination the safe puzzle tells the
/// player (Locker Room, room <c>0100</c>, <c>st100.dat</c>). This is the counterpart to
/// <see cref="DinoRand.FileFormats.Exe.MgmtOfficeSafeCode"/> (which holds the code the keypad actually
/// checks, in <c>DINO.exe</c>): a randomizer must write BOTH from the same seed-derived number or the
/// document shows a combination the safe rejects (the <b>displayed == checked</b> invariant).
///
/// <para><b>Text format (decoded 2026-07-02).</b> DC1 room documents are a stream of 16-bit little-endian
/// glyph tokens in the decompressed RDT. A normal glyph is <c>(index &lt;&lt; 1) | 0x8000</c> — high byte
/// <c>0x80</c>, low byte <c>index*2</c> (always even). The glyph index map: <c>0</c>=space,
/// <c>1..26</c>=<c>A..Z</c>, <c>27..52</c>=<c>a..z</c>, <c>53..62</c>=<c>0..9</c>, and punctuation above
/// (<c>65</c>=<c>?</c>, <c>67</c>=<c>"</c>, <c>68</c>=<c>:</c>, <c>72</c>=<c>-</c>, <c>77</c>=<c>.</c>,
/// <c>78</c>=<c>,</c>, <c>79</c>=<c>'</c>). Control tokens use other high bytes (<c>0xA000 0x1001 0x2000</c>
/// = paragraph break, <c>0xC000</c> = line break). The glyph indices are language-independent (the digits
/// are identical across <c>english/</c> and <c>french/</c>). See
/// <c>docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md</c> §16.</para>
///
/// <para>In this JP-sourced build the document reads <c>"0375"</c> (a single code), matching the safe — not
/// the <c>"0426/0375"</c> of other releases. Operates on the <b>decompressed</b> RDT buffer
/// (<see cref="RoomFile.RdtBuffer"/>); re-LZSS + package repack back to disk is the caller's concern.</para>
/// </summary>
public static class MgmtOfficeDocumentCode
{
    /// <summary>Number of digits in the code.</summary>
    public const int DigitCount = 4;

    /// <summary>Glyph index of the double-quote that brackets the code in the text.</summary>
    public const int QuoteGlyphIndex = 67;

    /// <summary>First digit ('0') glyph index; digit <c>d</c> is <c>DigitGlyphBase + d</c>.</summary>
    public const int DigitGlyphBase = 53;

    /// <summary>Encode a glyph index to its 16-bit token.</summary>
    public static ushort GlyphToken(int index) => (ushort)((index << 1) | 0x8000);

    /// <summary>The token for a displayed digit (0..9).</summary>
    public static ushort DigitToken(int digit)
    {
        if (digit is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(digit), digit, "code digit must be 0..9");
        return GlyphToken(DigitGlyphBase + digit);
    }

    /// <summary>Decode a digit token back to its displayed digit, or -1 if it is not a digit glyph.</summary>
    public static int DigitFromToken(ushort token)
    {
        if ((token & 0xFF00) != 0x8000) return -1;
        int index = (token & 0xFF) >> 1;
        int d = index - DigitGlyphBase;
        return d is >= 0 and <= 9 ? d : -1;
    }

    private static readonly ushort Quote = GlyphToken(QuoteGlyphIndex);

    /// <summary>
    /// Locate a specific known code (by its current digit values) as a contiguous digit-glyph run in the
    /// decompressed RDT — robust for codes that are NOT quoted (e.g. Computer Room "Code: 5037"). Requires
    /// the run to be bounded by non-digit-glyph tokens and to be unique. Returns the byte offset of the first
    /// digit token, or -1 if not found / not unique. This is the general lever behind the whole keypad-code
    /// family (see <c>data/dc1/puzzle-codes.json</c>); <see cref="FindCodeDigitsOffset"/> is the quoted-only
    /// special case used by the Mgmt-Office safe.
    /// </summary>
    public static int FindKnownCodeOffset(ReadOnlySpan<byte> rdt, ReadOnlySpan<int> expectedDigits)
    {
        int n = expectedDigits.Length;
        if (n == 0) return -1;
        int found = -1;
        for (int o = 0; o + 2 * n <= rdt.Length; o += 2)
        {
            bool match = true;
            for (int k = 0; k < n; k++)
            {
                ushort t = BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(o + 2 * k, 2));
                if (DigitFromToken(t) != expectedDigits[k]) { match = false; break; }
            }
            if (!match) continue;
            // digit-bounded: token before and after must not be a digit glyph
            if (o >= 2 && DigitFromToken(BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(o - 2, 2))) >= 0) continue;
            int after = o + 2 * n;
            if (after + 2 <= rdt.Length &&
                DigitFromToken(BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(after, 2))) >= 0) continue;
            if (found >= 0) return -1; // ambiguous
            found = o;
        }
        return found;
    }

    /// <summary>Overwrite a specific known code (located by its current digits) with new digits, in place.</summary>
    public static bool TryRewriteKnownCode(Span<byte> rdt, ReadOnlySpan<int> expectedDigits, ReadOnlySpan<int> newDigits)
    {
        if (expectedDigits.Length != newDigits.Length)
            throw new ArgumentException("digit counts differ", nameof(newDigits));
        int off = FindKnownCodeOffset(rdt, expectedDigits);
        if (off < 0) return false;
        for (int k = 0; k < newDigits.Length; k++)
            BinaryPrimitives.WriteUInt16LittleEndian(rdt.Slice(off + 2 * k, 2), DigitToken(newDigits[k]));
        return true;
    }

    /// <summary>
    /// Locate the unique <c>" d d d d "</c> quoted-4-digit code run in the decompressed RDT. Returns the
    /// byte offset of the first digit token, or -1 if not found / not unique (ambiguity is treated as
    /// "not found" so a randomizer never edits the wrong run).
    /// </summary>
    public static int FindCodeDigitsOffset(ReadOnlySpan<byte> rdt)
    {
        int found = -1;
        for (int o = 0; o + 2 * (DigitCount + 2) <= rdt.Length; o += 2)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(o, 2)) != Quote) continue;
            bool allDigits = true;
            for (int k = 0; k < DigitCount; k++)
            {
                ushort t = BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(o + 2 + 2 * k, 2));
                if (DigitFromToken(t) < 0) { allDigits = false; break; }
            }
            if (!allDigits) continue;
            ushort closing = BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(o + 2 + 2 * DigitCount, 2));
            if (closing != Quote) continue;
            if (found >= 0) return -1; // more than one candidate -> refuse
            found = o + 2; // offset of the first digit token
        }
        return found;
    }

    /// <summary>Read the 4-digit code the document displays, or return false if no unique run is found.</summary>
    public static bool TryReadCode(ReadOnlySpan<byte> rdt, out int[] digits)
    {
        digits = Array.Empty<int>();
        int off = FindCodeDigitsOffset(rdt);
        if (off < 0) return false;
        var result = new int[DigitCount];
        for (int k = 0; k < DigitCount; k++)
            result[k] = DigitFromToken(BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(off + 2 * k, 2)));
        digits = result;
        return true;
    }

    /// <summary>Overwrite the document's displayed code in-place in the decompressed RDT.</summary>
    public static void WriteCode(Span<byte> rdt, ReadOnlySpan<int> digits)
    {
        if (digits.Length != DigitCount)
            throw new ArgumentException($"code must be {DigitCount} digits", nameof(digits));
        int off = FindCodeDigitsOffset(rdt);
        if (off < 0)
            throw new InvalidOperationException("Mgmt-Office document code run not found (or not unique) in RDT.");
        for (int k = 0; k < DigitCount; k++)
            BinaryPrimitives.WriteUInt16LittleEndian(rdt.Slice(off + 2 * k, 2), DigitToken(digits[k]));
    }
}
