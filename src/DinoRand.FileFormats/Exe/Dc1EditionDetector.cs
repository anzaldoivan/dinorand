using System;
using System.IO;
using System.Security.Cryptography;
using DinoRand.FileFormats.Stage;

namespace DinoRand.FileFormats.Exe;

/// <summary>Which DC1 install variant owns the puzzle-code DOCUMENT text (the keypad-CHECK table in
/// DINO.exe is build-independent; the document is not — see
/// docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md §18).</summary>
public enum Dc1Edition
{
    /// <summary>Could not be classified — the document lever must refuse (fail-loud, never desync).</summary>
    Unknown,

    /// <summary>GOG European release (french/german/…): document text is inline Latin glyph tokens in the
    /// room RDT — the <see cref="MgmtOfficeDocumentCode"/> RDT-rewrite lever applies.</summary>
    GogInlineText,

    /// <summary>Classic REbirth over the JP SourceNext base, English mode (<c>config.ini [DLL]
    /// JapaneseEnable=0</c>): the English document text is injected at runtime by REbirth's
    /// <c>ddraw.dll</c>, NOT stored in the game Data files — only a ddraw.dll patch can sync it.</summary>
    RebirthEnglish,

    /// <summary>Classic REbirth in Japanese mode (<c>JapaneseEnable=1</c>): JP text in the room files.</summary>
    RebirthJapanese,
}

/// <summary>
/// Classifies a DC1 install for edition-aware puzzle-code document sync. REbirth is identified by its
/// <c>ddraw.dll</c> version lock (size + SHA256, same lock as <see cref="DdrawPatcher"/>) — presence alone
/// is NOT enough, the GOG releases ship their own (different) 1,654,784-byte <c>ddraw.dll</c> wrapper.
/// A non-REbirth install is <see cref="Dc1Edition.GogInlineText"/> only if the row-0 code run is actually
/// locatable in <c>st100.dat</c>'s RDT (the JP-data sets carry JP-glyph text the Latin lever cannot see).
/// </summary>
public static class Dc1EditionDetector
{
    /// <summary>SHA256 of the stock REbirth <c>ddraw.dll</c> this repo's levers are locked to
    /// (3,536,384 B — <see cref="DdrawPatcher.StockDllLength"/>).</summary>
    public const string RebirthDdrawSha256 =
        "249F1B8F4961AD86FD647C45EDBF190172F6BB876A5606B5620AC8AF5F136DE9";

    /// <summary>Pure classification core (unit-testable without files).</summary>
    /// <param name="rebirthDdraw">game-root <c>ddraw.dll</c> matches the REbirth version lock.</param>
    /// <param name="japaneseEnable"><c>config.ini [DLL] JapaneseEnable</c>, or null if absent.</param>
    /// <param name="inlineCodeRun">the row-0 document code run is locatable in the room RDT.</param>
    public static Dc1Edition Classify(bool rebirthDdraw, bool? japaneseEnable, bool inlineCodeRun)
    {
        if (rebirthDdraw)
            return japaneseEnable switch
            {
                false => Dc1Edition.RebirthEnglish,
                true => Dc1Edition.RebirthJapanese,
                null => Dc1Edition.Unknown, // REbirth DLL but no readable config — refuse to guess
            };
        return inlineCodeRun ? Dc1Edition.GogInlineText : Dc1Edition.Unknown;
    }

    /// <summary>Classify the install whose <c>Data\</c> directory is <paramref name="dataDir"/>
    /// (<c>ddraw.dll</c> / <c>config.ini</c> live beside it in the game root).</summary>
    public static Dc1Edition Detect(string dataDir)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar))
                      ?? dataDir;
        return Classify(
            IsRebirthDdraw(Path.Combine(root, "ddraw.dll")),
            ReadJapaneseEnable(Path.Combine(root, "config.ini")),
            HasInlineCodeRun(dataDir));
    }

    /// <summary>True when <paramref name="ddrawPath"/> is the locked REbirth build — stock
    /// (size + SHA256) or already carrying this repo's own patches (the
    /// <see cref="DdrawPatcher"/> vertex-table expansion and/or the
    /// <see cref="RebirthTextPatcher"/> puzzle-code text section), which append to the stock image
    /// without disturbing its anchors. Re-running the randomizer must keep classifying its own output
    /// as REbirth.</summary>
    public static bool IsRebirthDdraw(string ddrawPath)
    {
        if (!File.Exists(ddrawPath)) return false;
        long len = new FileInfo(ddrawPath).Length;
        if (len < DdrawPatcher.StockDllLength) return false;
        if (len == DdrawPatcher.StockDllLength)
        {
            using var s = File.OpenRead(ddrawPath);
            return Convert.ToHexString(SHA256.HashData(s)) == RebirthDdrawSha256;
        }
        byte[] bytes = File.ReadAllBytes(ddrawPath);
        if (len == DdrawPatcher.StockDllLength + DdrawPatcher.NewSectionSize)
            return DdrawPatcher.IsRebirthVertexTablesExpanded(bytes);
        // longer: only our text patch (optionally atop the vertex lift) produces this — verify the
        // stock image is still in place (PE anchors + the embedded 7z resource blob) and the resource
        // DataEntry was repointed by the text patch.
        return bytes[0] == 'M' && bytes[1] == 'Z'
               && ExePatcher.ReadUInt32(bytes, 0x3C) == 0x138
               && ExePatcher.ReadUInt32(bytes, 0x138) == 0x00004550
               && bytes[RebirthTextPatcher.ResourceBlobFileOffset] == 0x37
               && bytes[RebirthTextPatcher.ResourceBlobFileOffset + 1] == 0x7A
               && RebirthTextPatcher.IsPuzzleCodeTextPatched(bytes);
    }

    /// <summary>Read <c>[DLL] JapaneseEnable</c> from a REbirth <c>config.ini</c>; null when the file,
    /// section, or key is missing.</summary>
    public static bool? ReadJapaneseEnable(string configIniPath)
    {
        if (!File.Exists(configIniPath)) return null;
        bool inDll = false;
        foreach (var raw in File.ReadLines(configIniPath))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inDll = line.Equals("[DLL]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inDll) continue;
            int eq = line.IndexOf('=');
            if (eq < 0 || !line.AsSpan(0, eq).Trim().Equals("JapaneseEnable", StringComparison.OrdinalIgnoreCase))
                continue;
            return int.TryParse(line.AsSpan(eq + 1).Trim(), out int v) ? v != 0 : null;
        }
        return null;
    }

    /// <summary>True when the row-0 lock's document code run (JP-build <c>0375</c>) is locatable as Latin
    /// digit glyphs in <c>st100.dat</c>'s decompressed RDT — the marker of an inline-text (GOG European)
    /// data set. Any parse failure is simply "not inline text".</summary>
    public static bool HasInlineCodeRun(string dataDir)
    {
        var row0 = Dc1PuzzleCodeSync.Family[0];
        string path = Path.Combine(dataDir, row0.DocFile!);
        if (!File.Exists(path)) return false;
        try
        {
            var room = RoomFile.Read(row0.DocStage, row0.DocRoom, File.ReadAllBytes(path));
            return MgmtOfficeDocumentCode.FindKnownCodeOffset(room.RdtBuffer, row0.OriginalDigits) >= 0;
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException
                                       or IndexOutOfRangeException)
        {
            return false;
        }
    }
}
