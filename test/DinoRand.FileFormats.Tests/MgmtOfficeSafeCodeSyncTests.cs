using System;
using System.IO;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Guards the DC1 Management Office safe-code randomizer invariant: <b>displayed == checked</b>. When a seed
/// changes the safe's accepted 4-digit code (DINO.exe <c>.data</c> JP row0, all 8 recompiled copies — §14,
/// CE-witnessed), the "Journal of the guardsmen" document (Locker Room, room <c>0100</c>, <c>st100.dat</c>
/// RDT glyph tokens — §16) must show the SAME number, or the player reads a combination the keypad no longer
/// accepts. Both levers derive from one seed-derived code via <see cref="MgmtOfficeSafeCode.DeriveFromSeed"/>.
/// </summary>
public class MgmtOfficeSafeCodeSyncTests
{
    // ---- safe code (DINO.exe .data) ----

    private static byte[] BuildExeWithDefaultCode()
    {
        int size = 0;
        int tableSpan = 2 * MgmtOfficeSafeCode.RowCount * MgmtOfficeSafeCode.DigitCount; // 12 rows * 4 bytes
        foreach (int fo in MgmtOfficeSafeCode.JpRow0FileOffsets)
            size = System.Math.Max(size, fo + tableSpan);
        var exe = new byte[size];
        byte[] jp0375 = { 0x01, 0x04, 0x08, 0x06 }; // "0375" stored = displayed+1
        foreach (int fo in MgmtOfficeSafeCode.JpRow0FileOffsets)
            jp0375.CopyTo(exe, fo);
        return exe;
    }

    // ---- document code (st100.dat RDT glyph tokens) ----
    // Minimal decompressed-RDT fragment: some prose, then the quoted code run `" 0 3 7 5 "`, then a period.
    private static byte[] BuildRdtWithDefaultCode()
    {
        var toks = new System.Collections.Generic.List<ushort>
        {
            MgmtOfficeDocumentCode.GlyphToken(0),  // space (filler so offset != 0)
            MgmtOfficeDocumentCode.GlyphToken(27), // 'a'
            MgmtOfficeDocumentCode.GlyphToken(0),  // space
            MgmtOfficeDocumentCode.GlyphToken(MgmtOfficeDocumentCode.QuoteGlyphIndex),
            MgmtOfficeDocumentCode.DigitToken(0),
            MgmtOfficeDocumentCode.DigitToken(3),
            MgmtOfficeDocumentCode.DigitToken(7),
            MgmtOfficeDocumentCode.DigitToken(5),
            MgmtOfficeDocumentCode.GlyphToken(MgmtOfficeDocumentCode.QuoteGlyphIndex),
            MgmtOfficeDocumentCode.GlyphToken(77), // '.'
        };
        var rdt = new byte[toks.Count * 2];
        for (int i = 0; i < toks.Count; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(rdt.AsSpan(i * 2, 2), toks[i]);
        return rdt;
    }

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(3, 0x04)]
    [InlineData(7, 0x08)]
    [InlineData(5, 0x06)]
    [InlineData(9, 0x0a)]
    public void SafeCode_EncodeDigit_UsesDisplayedPlusOne(int displayed, byte stored)
    {
        Assert.Equal(stored, MgmtOfficeSafeCode.EncodeDigit(displayed));
        Assert.Equal(displayed, MgmtOfficeSafeCode.DecodeDigit(stored));
    }

    [Fact]
    public void SafeCode_DefaultExe_Is0375()
        => Assert.Equal(new[] { 0, 3, 7, 5 }, MgmtOfficeSafeCode.ReadSafeCode(BuildExeWithDefaultCode()));

    [Fact]
    public void SafeCode_Write_PatchesAllEightCopies()
    {
        var exe = BuildExeWithDefaultCode();
        int[] digits = { 6, 7, 6, 7 };
        MgmtOfficeSafeCode.WriteSafeCode(exe, digits);
        byte[] expected = { 0x07, 0x08, 0x07, 0x08 }; // "6767" stored = displayed+1
        foreach (int fo in MgmtOfficeSafeCode.JpRow0FileOffsets)
            Assert.Equal(expected, exe[fo..(fo + MgmtOfficeSafeCode.DigitCount)]);
        Assert.Equal(digits, MgmtOfficeSafeCode.ReadSafeCode(exe));
    }

    [Fact]
    public void DocumentCode_DefaultRdt_Is0375()
    {
        Assert.True(MgmtOfficeDocumentCode.TryReadCode(BuildRdtWithDefaultCode(), out int[] digits));
        Assert.Equal(new[] { 0, 3, 7, 5 }, digits);
    }

    [Fact]
    public void DocumentCode_DigitToken_MatchesDecodedGameGlyphs()
    {
        // Verified from st100.dat: '0'=0x806a '3'=0x8070 '7'=0x8078 '5'=0x8074 (§16).
        Assert.Equal((ushort)0x806a, MgmtOfficeDocumentCode.DigitToken(0));
        Assert.Equal((ushort)0x8070, MgmtOfficeDocumentCode.DigitToken(3));
        Assert.Equal((ushort)0x8078, MgmtOfficeDocumentCode.DigitToken(7));
        Assert.Equal((ushort)0x8074, MgmtOfficeDocumentCode.DigitToken(5));
    }

    [Fact]
    public void DeriveFromSeed_IsDeterministicAndInRange()
    {
        int[] a = MgmtOfficeSafeCode.DeriveFromSeed(6767);
        int[] b = MgmtOfficeSafeCode.DeriveFromSeed(6767);
        Assert.Equal(a, b);
        Assert.Equal(MgmtOfficeSafeCode.DigitCount, a.Length);
        Assert.All(a, d => Assert.InRange(d, 0, 9));
    }

    /// <summary>
    /// Ground truth: the real <c>english/Data/st100.dat</c> document decodes to <c>0375</c> through the same
    /// lever, proving the glyph-token decode (§16) is correct against the shipped file — not just synthetic
    /// data. No-ops if the game files are not present in this checkout.
    /// </summary>
    [Fact]
    public void DocumentCode_RealSt100_DecodesTo0375()
    {
        string? st100 = FindGameFile(Path.Combine("english", "Data", "st100.dat"));
        if (st100 is null) return; // game data not in this checkout — skip silently

        var room = RoomFile.ReadFromFile(stage: 1, room: 0, st100);
        Assert.True(
            MgmtOfficeDocumentCode.TryReadCode(room.RdtBuffer, out int[] digits),
            "code run not found in real st100.dat RDT");
        Assert.Equal(new[] { 0, 3, 7, 5 }, digits);
    }

    /// <summary>
    /// The general document lever locates and rewrites each keypad-family code by value in its real room —
    /// including the non-quoted Computer Room code (§17). Proves one common function covers the family.
    /// </summary>
    [Theory]
    [InlineData("st200.dat", 2, 0, new[] { 7, 6, 8, 7 })] // Lounge safe, quoted
    [InlineData("st302.dat", 3, 2, new[] { 5, 0, 3, 7 })] // Computer Room gas code, non-quoted
    public void DocumentCode_FamilyCode_LocatedAndRewrittenInRealRoom(string file, int stage, int room, int[] original)
    {
        string? path = FindGameFile(Path.Combine("english", "Data", file));
        if (path is null) return; // game data not in this checkout

        var rf = RoomFile.ReadFromFile(stage, room, path);
        var rdt = (byte[])rf.RdtBuffer.Clone();

        int off = MgmtOfficeDocumentCode.FindKnownCodeOffset(rdt, original);
        Assert.True(off >= 0, $"{file}: original code {string.Concat(original)} not uniquely located");

        int[] scrambled = { 6, 7, 6, 7 };
        Assert.True(MgmtOfficeDocumentCode.TryRewriteKnownCode(rdt, original, scrambled));
        Assert.Equal(scrambled, MgmtOfficeDocumentCode.FindKnownCodeOffset(rdt, scrambled) is int o2 && o2 >= 0
            ? ReadDigits(rdt, o2, 4) : new[] { -1, -1, -1, -1 });
        // original no longer present
        Assert.True(MgmtOfficeDocumentCode.FindKnownCodeOffset(rdt, original) < 0);
    }

    [Fact]
    public void SafeCode_WriteRow_RoundTripsEveryRowAndRegion()
    {
        var exe = BuildExeWithDefaultCode();
        for (int row = 0; row < MgmtOfficeSafeCode.RowCount; row++)
        {
            int[] code = { row, (row + 3) % 10, (row + 7) % 10, (row + 1) % 10 };
            MgmtOfficeSafeCode.WriteRow(exe, row, code, bothRegions: true);
            Assert.Equal(code, MgmtOfficeSafeCode.ReadRow(exe, row, us: false));
            Assert.Equal(code, MgmtOfficeSafeCode.ReadRow(exe, row, us: true));
        }
    }

    /// <summary>
    /// End-to-end on-disk repack: scramble the family, and for each documented lock re-read the repacked room
    /// file from scratch (LZSS decompress + package parse) and confirm the document now shows the new code and
    /// the EXE row matches it. Proves the re-LZSS + package repack is game-valid (round-trips through the real
    /// reader). No-ops if game data is absent.
    /// </summary>
    [Fact]
    public void Family_ScrambleAndRepack_DocumentAndExeAgree_RealFiles()
    {
        string? st100 = FindGameFile(Path.Combine("english", "Data", "st100.dat"));
        if (st100 is null) return; // game data not in this checkout
        string dataDir = Path.GetDirectoryName(st100)!;
        string? exePath = FindGameFile(Path.Combine("english", "DINO.exe"));
        if (exePath is null) return;

        byte[] exe = File.ReadAllBytes(exePath);
        var results = Dc1PuzzleCodeSync.Scramble(
            exe,
            lk => { var p = Path.Combine(dataDir, lk.DocFile!); return File.Exists(p) ? File.ReadAllBytes(p) : null; },
            lk => Dc1PuzzleCodeSync.DeriveRowCode(seed: 4242, lk.Row));

        Assert.Equal(6, results.Count);
        foreach (var r in results)
        {
            // EXE row (both regions) now holds the new code.
            Assert.Equal(r.NewCode, MgmtOfficeSafeCode.ReadRow(exe, r.Lock.Row, us: false));
            Assert.Equal(r.NewCode, MgmtOfficeSafeCode.ReadRow(exe, r.Lock.Row, us: true));

            if (r.Lock.DocFile is null) { Assert.False(r.DocRewritten); continue; }

            Assert.True(r.DocRewritten, $"{r.Lock.Name}: document not rewritten");
            // Re-parse the repacked file from raw bytes and read the document code back.
            var reparsed = RoomFile.Read(r.Lock.DocStage, r.Lock.DocRoom, r.DocBytes!);
            Assert.True(
                MgmtOfficeDocumentCode.FindKnownCodeOffset(reparsed.RdtBuffer, r.NewCode) >= 0,
                $"{r.Lock.Name}: repacked document does not contain the new code");
            // original code is gone from the document
            Assert.True(MgmtOfficeDocumentCode.FindKnownCodeOffset(reparsed.RdtBuffer, r.Lock.OriginalDigits) < 0);
        }
    }

    /// <summary>
    /// FAIL-LOUD guard: a documented lock (<c>DocFile != null</c>) whose document bytes are unavailable must
    /// abort the whole scramble — never silently ship an EXE-only change (safe accepts the new code while the
    /// document still shows the old one, <b>displayed != checked</b>). Pure: no game files needed.
    /// </summary>
    [Fact]
    public void Scramble_DocumentedLock_MissingDoc_ThrowsInsteadOfSilentDesync()
    {
        var exe = BuildExeWithDefaultCode();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Dc1PuzzleCodeSync.Scramble(exe, _ => null, _ => new[] { 6, 7, 6, 7 }));
        Assert.Contains("st100.dat", ex.Message);      // first documented lock (row 0)
        Assert.Contains("displayed != checked", ex.Message);
    }

    /// <summary>
    /// FAIL-LOUD guard, present-but-unlocatable branch: a documented lock whose room bytes exist but do NOT
    /// contain its code run (the corrupted/altered-install case) must also abort. Feeds row 0 the wrong room's
    /// bytes so its <c>0375</c> run is absent. Real-file (skips if game data absent).
    /// </summary>
    [Fact]
    public void Scramble_DocumentedLock_CodeRunAbsent_ThrowsInsteadOfSilentDesync()
    {
        string? st302 = FindGameFile(Path.Combine("english", "Data", "st302.dat"));
        if (st302 is null) return; // game data not in this checkout
        byte[] wrongRoom = File.ReadAllBytes(st302); // has 5037, not row 0's 0375

        var exe = BuildExeWithDefaultCode();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Dc1PuzzleCodeSync.Scramble(exe, _ => wrongRoom, _ => new[] { 6, 7, 6, 7 }));
        Assert.Contains("0375", ex.Message);           // the code it could not locate
    }

    /// <summary>
    /// Exercises the real disk-write + backup path (<see cref="Dc1PuzzleCodeSync.ApplyToInstall"/>) in an
    /// isolated temp copy of the game files: writes patched files, makes one-time <c>.dinorand-codebak</c>
    /// backups, and the patched st100 re-reads to the new code. No-ops if game data is absent.
    /// </summary>
    [Fact]
    public void ApplyToInstall_WritesPatchedFilesAndBackup_TempCopy()
    {
        string? st100 = FindGameFile(Path.Combine("english", "Data", "st100.dat"));
        string? exeSrc = FindGameFile(Path.Combine("english", "DINO.exe"));
        if (st100 is null || exeSrc is null) return;
        string srcData = Path.GetDirectoryName(st100)!;

        string tmp = Path.Combine(Path.GetTempPath(), "dinorand_codesync_" + Guid.NewGuid().ToString("N"));
        string tmpData = Path.Combine(tmp, "Data");
        Directory.CreateDirectory(tmpData);
        try
        {
            string exePath = Path.Combine(tmp, "DINO.exe");
            File.Copy(exeSrc, exePath);
            foreach (var lk in Dc1PuzzleCodeSync.Family)
            {
                if (lk.DocFile is null) continue;
                var s = Path.Combine(srcData, lk.DocFile);
                if (File.Exists(s)) File.Copy(s, Path.Combine(tmpData, lk.DocFile));
            }

            var results = Dc1PuzzleCodeSync.ApplyToInstall(exePath, tmpData, seed: 99, codeForRow: null);

            // EXE on disk holds the new codes; backups exist.
            byte[] exe = File.ReadAllBytes(exePath);
            Assert.True(File.Exists(exePath + ".dinorand-codebak"));
            foreach (var r in results)
                Assert.Equal(r.NewCode, MgmtOfficeSafeCode.ReadRow(exe, r.Lock.Row, us: false));

            // st100 patched on disk re-reads to the new code; backup exists.
            var row0 = results[0];
            string doc = Path.Combine(tmpData, row0.Lock.DocFile!);
            Assert.True(File.Exists(doc + ".dinorand-codebak"));
            var room = RoomFile.ReadFromFile(row0.Lock.DocStage, row0.Lock.DocRoom, doc);
            Assert.True(MgmtOfficeDocumentCode.FindKnownCodeOffset(room.RdtBuffer, row0.NewCode) >= 0);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    private static int[] ReadDigits(byte[] rdt, int off, int count)
    {
        var d = new int[count];
        for (int k = 0; k < count; k++)
            d[k] = MgmtOfficeDocumentCode.DigitFromToken(
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(rdt.AsSpan(off + 2 * k, 2)));
        return d;
    }

    private static string? FindGameFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, relative);
            if (File.Exists(p)) return p;
            if (File.Exists(Path.Combine(dir.FullName, "DinoRand.sln"))) break;
        }
        return null;
    }

    /// <summary>
    /// THE INVARIANT. A seed derives the safe code; the randomizer writes it to the EXE keypad table AND the
    /// document glyph stream; the code the safe checks and the code the player reads must be equal.
    /// </summary>
    [Fact]
    public void RandomizedSafeCode_And_DocumentCode_AreTheSameNumber()
    {
        var exe = BuildExeWithDefaultCode();
        var rdt = BuildRdtWithDefaultCode();

        int[] code = MgmtOfficeSafeCode.DeriveFromSeed(seed: 12345);
        MgmtOfficeSafeCode.WriteSafeCode(exe, code);
        MgmtOfficeDocumentCode.WriteCode(rdt, code);

        int[] safe = MgmtOfficeSafeCode.ReadSafeCode(exe);
        Assert.True(
            MgmtOfficeDocumentCode.TryReadCode(rdt, out int[] document),
            "Mgmt-Office document code run not found in RDT (see MGMT-OFFICE-SAFE-PUZZLE-DECODE.md §16).");

        Assert.Equal(code, safe);      // safe checks the seed code
        Assert.Equal(code, document);  // document shows the seed code
        Assert.Equal(safe, document);  // displayed == checked
    }
}
