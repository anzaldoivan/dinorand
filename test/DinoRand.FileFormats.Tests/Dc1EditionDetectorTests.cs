using System;
using System.IO;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Guards the DC1 install-edition classifier that routes the puzzle-code DOCUMENT lever: GOG European
/// (inline Latin RDT text) vs Classic REbirth English (ddraw.dll-injected text) vs REbirth Japanese vs
/// Unknown. Synthetic-input tests need no game files; the real-install tests skip when absent.
/// </summary>
public class Dc1EditionDetectorTests
{
    // ---- pure classification core ----

    [Theory]
    [InlineData(true, false, false, Dc1Edition.RebirthEnglish)]
    [InlineData(true, false, true, Dc1Edition.RebirthEnglish)]  // ddraw lock outranks the RDT probe
    [InlineData(true, true, false, Dc1Edition.RebirthJapanese)]
    [InlineData(true, null, false, Dc1Edition.Unknown)]          // REbirth DLL but no config — refuse to guess
    [InlineData(false, null, true, Dc1Edition.GogInlineText)]
    [InlineData(false, false, true, Dc1Edition.GogInlineText)]
    [InlineData(false, null, false, Dc1Edition.Unknown)]         // e.g. plain GOG-Japanese data (JP glyphs)
    public void Classify_CoversEveryEdition(bool rebirthDdraw, bool? japaneseEnable, bool inlineRun,
                                            Dc1Edition expected)
        => Assert.Equal(expected, Dc1EditionDetector.Classify(rebirthDdraw, japaneseEnable, inlineRun));

    // ---- config.ini [DLL] JapaneseEnable ----

    private static string WriteTempIni(string content)
    {
        string p = Path.Combine(Path.GetTempPath(), "dinorand_ini_" + Guid.NewGuid().ToString("N") + ".ini");
        File.WriteAllText(p, content);
        return p;
    }

    [Theory]
    [InlineData("[DLL]\nJapaneseEnable = 0\n", false)]
    [InlineData("[DLL]\nJapaneseEnable = 1\n", true)]
    [InlineData("[GAME]\nSetup = 1\n\n[DLL]\ninputMethod = 0\nJapaneseEnable = 0\nWideMode = 0\n", false)]
    public void ReadJapaneseEnable_ParsesDllSection(string ini, bool expected)
    {
        string p = WriteTempIni(ini);
        try { Assert.Equal(expected, Dc1EditionDetector.ReadJapaneseEnable(p)); }
        finally { File.Delete(p); }
    }

    [Theory]
    [InlineData("[GAME]\nJapaneseEnable = 1\n")] // key outside [DLL] does not count
    [InlineData("[DLL]\ninputMethod = 0\n")]     // key missing
    [InlineData("")]
    public void ReadJapaneseEnable_MissingOrMisplacedKey_ReturnsNull(string ini)
    {
        string p = WriteTempIni(ini);
        try { Assert.Null(Dc1EditionDetector.ReadJapaneseEnable(p)); }
        finally { File.Delete(p); }
    }

    [Fact]
    public void ReadJapaneseEnable_MissingFile_ReturnsNull()
        => Assert.Null(Dc1EditionDetector.ReadJapaneseEnable(
            Path.Combine(Path.GetTempPath(), "dinorand_no_such_" + Guid.NewGuid().ToString("N") + ".ini")));

    // ---- ddraw.dll version lock ----

    [Fact]
    public void IsRebirthDdraw_MissingFile_False()
        => Assert.False(Dc1EditionDetector.IsRebirthDdraw(
            Path.Combine(Path.GetTempPath(), "dinorand_no_such_ddraw_" + Guid.NewGuid().ToString("N") + ".dll")));

    [Fact]
    public void IsRebirthDdraw_WrongSizeOrContent_False()
    {
        // Wrong size (the GOG wrapper ddraw.dll case) and right-size-wrong-bytes both fail the lock.
        string small = WriteTempIni("not a dll");
        string sized = Path.Combine(Path.GetTempPath(), "dinorand_ddraw_" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(sized, new byte[DdrawPatcher.StockDllLength]); // right length, zeroed → SHA mismatch
        try
        {
            Assert.False(Dc1EditionDetector.IsRebirthDdraw(small));
            Assert.False(Dc1EditionDetector.IsRebirthDdraw(sized));
        }
        finally { File.Delete(small); File.Delete(sized); }
    }

    // ---- real installs (skip silently when not in this checkout) ----

    [Fact]
    public void Detect_RealRebirthEnglishInstall_ClassifiesRebirthEnglish()
    {
        string? data = FindGameDir("english");
        if (data is null) return;
        Assert.Equal(Dc1Edition.RebirthEnglish, Dc1EditionDetector.Detect(data));
    }

    [Fact]
    public void Detect_RealGogEuropeanInstall_ClassifiesGogInlineText()
    {
        string? data = FindGameDir("french");
        if (data is null) return;
        Assert.Equal(Dc1Edition.GogInlineText, Dc1EditionDetector.Detect(data));
    }

    [Fact]
    public void Detect_RealGogJapaneseInstall_ClassifiesUnknown()
    {
        // Plain GOG-Japanese data: no REbirth DLL, JP-glyph text the Latin probe cannot see → Unknown
        // (the document lever must warn/refuse, never silently desync).
        string? data = FindGameDir("japanese");
        if (data is null) return;
        Assert.Equal(Dc1Edition.Unknown, Dc1EditionDetector.Detect(data));
    }

    /// <summary>First install in this checkout whose document text is inline Latin RDT glyphs
    /// (<see cref="Dc1Edition.GogInlineText"/> — the GOG European sets), or null. Shared by every
    /// edition-aware real-file test; the REbirth-English and JP-data installs deliberately do NOT
    /// qualify (their room files carry no Latin code runs).</summary>
    internal static string? FindInlineTextDataDir()
    {
        foreach (var lang in new[] { "french", "german", "italian", "spanish", "english", "japanese" })
        {
            string? data = FindGameDir(lang);
            if (data is not null && Dc1EditionDetector.Detect(data) == Dc1Edition.GogInlineText)
                return data;
        }
        return null;
    }

    /// <summary>Locate <c>&lt;lang&gt;/Data</c> above the test bin dir (same walk as the other real-file tests).</summary>
    internal static string? FindGameDir(string language)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, language, "Data");
            if (Directory.Exists(p)) return p;
            if (File.Exists(Path.Combine(dir.FullName, "DinoRand.sln"))) break;
        }
        return null;
    }
}
