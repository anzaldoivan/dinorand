using System;
using System.Collections.Generic;
using System.IO;
using DinoRand.FileFormats.SevenZip;
using DinoRand.FileFormats.Stage;

namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Classic REbirth English document-text lever for the DC1 keypad-code family — the ddraw.dll
/// counterpart of <see cref="MgmtOfficeDocumentCode"/> (which handles the GOG inline-RDT builds).
///
/// <para>On a REbirth-English install the document text the player reads is injected at runtime from
/// per-room <c>diff/ST*.diff</c> / <c>diff/AST*.diff</c> patch files inside a 7z archive embedded as
/// PE resource <c>DATA/107</c> (decode: docs/reference/dc1/puzzle/REBIRTH-DDRAW-TEXT-STORE-RE.md).
/// This patcher rewrites the six 8-byte digit-glyph runs (ST = JP-region text, AST = US-region — both
/// are written, mirroring <see cref="MgmtOfficeSafeCode.WriteRow"/>'s <c>bothRegions</c>), rebuilds
/// the archive (untouched streams reused byte-verbatim; the six modified diffs stored with the 7z
/// Copy codec), appends it as a new <c>.dinotxt</c> section and repoints the resource DataEntry —
/// the same append shape as <see cref="DdrawPatcher"/>, so it composes with the vertex-lift patch
/// (apply the vertex lift first; this patcher accepts stock or vertex-lifted input).</para>
///
/// <para>Version lock: the resource geometry below is the locked REbirth build (stock 3,536,384 B,
/// SHA256 <c>249F1B8F…</c>); every diff's stock digit bytes are verified before anything is written,
/// and the input DLL is never modified (a new buffer is returned). Reversal is the installer's
/// backup file.</para>
/// </summary>
public static class RebirthTextPatcher
{
    /// <summary>Resource <c>DATA/107/1040</c> payload (the embedded 7z) in the locked build.</summary>
    public const int ResourceBlobFileOffset = 0x295BC0;
    public const int ResourceBlobSize = 0xB9CE3;

    /// <summary>File offset of the resource's DataEntry struct (RVA, Size, Codepage, Reserved).</summary>
    public const int ResourceDataEntryFileOffset = 0x2949D8;
    public const uint StockResourceRva = 0x4563C0;

    private const int PeSigOffset = 0x138;
    private const int NumberOfSectionsOffset = 0x13E;
    private const int SizeOfImageOffset = 0x188;
    private const int SectionTableOffset = 0x230;
    private const int SectionHeaderSize = 0x28;
    private const int FileAlignment = 0x200;
    private const int SectionAlignment = 0x1000;

    /// <summary>One documented lock's digit run inside one diff file of the embedded archive.</summary>
    /// <param name="Row">Keypad-table row (matches <see cref="Dc1PuzzleCodeSync.CodeLock.Row"/>).</param>
    /// <param name="DiffPath">Archive path of the diff file.</param>
    /// <param name="Offset">Byte offset of the first digit token within the (decompressed) diff.</param>
    /// <param name="StockDigits">The digits the pristine diff displays (verify-before-write).</param>
    public sealed record DigitRun(int Row, string DiffPath, int Offset, int[] StockDigits);

    /// <summary>The six runs: ST (JP-region text) + AST (US-region text) per documented lock.
    /// Decoded + byte-cited in REBIRTH-DDRAW-TEXT-STORE-RE.md §4.</summary>
    public static readonly IReadOnlyList<DigitRun> DigitRuns = new[]
    {
        new DigitRun(0, "diff/ST100.diff",  0x594, new[] { 0, 3, 7, 5 }),
        new DigitRun(0, "diff/AST100.diff", 0x594, new[] { 0, 4, 2, 6 }),
        new DigitRun(1, "diff/ST200.diff",  0x610, new[] { 7, 6, 8, 7 }),
        new DigitRun(1, "diff/AST200.diff", 0x610, new[] { 8, 1, 5, 9 }),
        new DigitRun(2, "diff/ST302.diff",  0x5CE, new[] { 5, 0, 3, 7 }),
        new DigitRun(2, "diff/AST302.diff", 0x5CE, new[] { 7, 2, 4, 8 }),
    };

    /// <summary>
    /// Rewrite the documented locks' digit runs to <paramref name="codeForRow"/>'s codes and return a
    /// NEW dll buffer with the rebuilt archive appended as <c>.dinotxt</c> and the resource DataEntry
    /// repointed. Accepts the stock DLL or one already carrying the <see cref="DdrawPatcher"/> vertex
    /// lift; throws <see cref="InvalidOperationException"/> on any precondition mismatch (foreign
    /// build, already text-patched, stock digit mismatch) — it never writes a byte it cannot verify.
    /// </summary>
    public static byte[] PatchPuzzleCodeText(ReadOnlySpan<byte> dll, Func<int, int[]> codeForRow)
    {
        int newSectionIndex = VerifyLayout(dll);

        // -- patch the six diffs inside the embedded 7z --
        var blob = dll.Slice(ResourceBlobFileOffset, ResourceBlobSize).ToArray();
        var archive = SevenZipArchive.Read(blob);
        var replacements = new Dictionary<int, byte[]>();
        foreach (var run in DigitRuns)
        {
            if (!archive.StreamIndexByName.TryGetValue(run.DiffPath, out int idx))
                throw new InvalidOperationException($"REbirth archive has no '{run.DiffPath}' (unknown build).");
            if (!replacements.TryGetValue(idx, out var diff))
                diff = archive.ExtractStream(idx); // LZMA2-decoded + CRC-verified against the archive's digest

            for (int k = 0; k < run.StockDigits.Length; k++)
            {
                ushort want = MgmtOfficeDocumentCode.DigitToken(run.StockDigits[k]);
                ushort got = (ushort)(diff[run.Offset + 2 * k] | (diff[run.Offset + 2 * k + 1] << 8));
                if (got != want)
                    throw new InvalidOperationException(
                        $"{run.DiffPath} @0x{run.Offset:X}: expected stock digit token 0x{want:X4}, found 0x{got:X4} " +
                        "(unknown REbirth build).");
            }
            int[] code = codeForRow(run.Row);
            if (code.Length != run.StockDigits.Length)
                throw new ArgumentException($"row {run.Row}: code must be {run.StockDigits.Length} digits");
            for (int k = 0; k < code.Length; k++)
            {
                ushort tok = MgmtOfficeDocumentCode.DigitToken(code[k]);
                diff[run.Offset + 2 * k] = (byte)tok;
                diff[run.Offset + 2 * k + 1] = (byte)(tok >> 8);
            }
            replacements[idx] = diff;
        }
        byte[] rebuilt = archive.RebuildWithReplacedStreams(replacements);

        // -- append as a new section + repoint the resource DataEntry --
        int rawSize = (rebuilt.Length + FileAlignment - 1) & ~(FileAlignment - 1);
        var outBuf = new byte[dll.Length + rawSize];
        dll.CopyTo(outBuf);
        rebuilt.CopyTo(outBuf, dll.Length);
        var span = outBuf.AsSpan();

        uint sizeOfImage = ExePatcher.ReadUInt32(dll, SizeOfImageOffset);
        uint newRva = sizeOfImage; // SizeOfImage is section-aligned in both accepted layouts
        int hdr = SectionTableOffset + newSectionIndex * SectionHeaderSize;
        System.Text.Encoding.ASCII.GetBytes(".dinotxt").CopyTo(span.Slice(hdr, 8));
        ExePatcher.WriteUInt32(span, hdr + 0x08, (uint)rebuilt.Length);   // VirtualSize
        ExePatcher.WriteUInt32(span, hdr + 0x0C, newRva);                 // VirtualAddress
        ExePatcher.WriteUInt32(span, hdr + 0x10, (uint)rawSize);          // SizeOfRawData
        ExePatcher.WriteUInt32(span, hdr + 0x14, (uint)dll.Length);       // PointerToRawData
        ExePatcher.WriteUInt32(span, hdr + 0x24, 0x40000040);             // read-only initialized data

        ExePatcher.WriteUInt16(span, NumberOfSectionsOffset, (ushort)(newSectionIndex + 1));
        uint virtSize = (uint)((rebuilt.Length + SectionAlignment - 1) & ~(SectionAlignment - 1));
        ExePatcher.WriteUInt32(span, SizeOfImageOffset, sizeOfImage + virtSize);

        ExePatcher.WriteUInt32(span, ResourceDataEntryFileOffset, newRva);            // DataEntry.Rva
        ExePatcher.WriteUInt32(span, ResourceDataEntryFileOffset + 4, (uint)rebuilt.Length); // DataEntry.Size
        return outBuf;
    }

    /// <summary>True when <paramref name="dll"/> already carries a puzzle-code text patch (the
    /// resource DataEntry no longer points at the stock blob).</summary>
    public static bool IsPuzzleCodeTextPatched(ReadOnlySpan<byte> dll)
        => dll.Length > ResourceDataEntryFileOffset + 8
           && ExePatcher.ReadUInt32(dll, ResourceDataEntryFileOffset) != StockResourceRva;

    /// <summary>Verify the locked layout (stock or vertex-lifted) and return the index of the free
    /// section-header slot the new section will occupy.</summary>
    private static int VerifyLayout(ReadOnlySpan<byte> dll)
    {
        bool stock = dll.Length == DdrawPatcher.StockDllLength;
        bool lifted = dll.Length == DdrawPatcher.StockDllLength + DdrawPatcher.NewSectionSize
                      && DdrawPatcher.IsRebirthVertexTablesExpanded(dll);
        if (!stock && !lifted)
            throw new InvalidOperationException(
                $"ddraw.dll is 0x{dll.Length:X} bytes — neither the stock REbirth build (0x{DdrawPatcher.StockDllLength:X}) " +
                "nor its vertex-lifted form. Refusing to patch an unknown build.");
        if (dll[0] != 'M' || dll[1] != 'Z' || ExePatcher.ReadUInt32(dll, 0x3C) != PeSigOffset
            || ExePatcher.ReadUInt32(dll, PeSigOffset) != 0x00004550)
            throw new InvalidOperationException("Not the expected PE layout (MZ/PE anchors mismatch).");

        int sections = ExePatcher.ReadUInt16(dll, NumberOfSectionsOffset);
        if (sections != (stock ? 6 : 7))
            throw new InvalidOperationException($"Unexpected section count {sections} for this layout.");
        for (int i = 0; i < SectionHeaderSize; i++)
            if (dll[SectionTableOffset + sections * SectionHeaderSize + i] != 0)
                throw new InvalidOperationException("The next section-header slot is not empty.");

        uint sizeOfImage = ExePatcher.ReadUInt32(dll, SizeOfImageOffset);
        uint expectedImage = stock ? DdrawPatcher.NewSectionRva
                                   : DdrawPatcher.NewSectionRva + DdrawPatcher.NewSectionSize;
        if (sizeOfImage != expectedImage)
            throw new InvalidOperationException($"SizeOfImage 0x{sizeOfImage:X} != expected 0x{expectedImage:X}.");

        if (ExePatcher.ReadUInt32(dll, ResourceDataEntryFileOffset) != StockResourceRva
            || ExePatcher.ReadUInt32(dll, ResourceDataEntryFileOffset + 4) != ResourceBlobSize)
            throw new InvalidOperationException(
                "Resource DATA/107 DataEntry does not hold the stock values (already text-patched, or an unknown build).");
        if (dll[ResourceBlobFileOffset] != 0x37 || dll[ResourceBlobFileOffset + 1] != 0x7A)
            throw new InvalidOperationException("Embedded resource is not a 7z archive (unknown build).");
        return sections;
    }
}
