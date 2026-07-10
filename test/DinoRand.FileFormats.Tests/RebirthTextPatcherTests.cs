using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.SevenZip;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Guards the REbirth-English document lever (<see cref="RebirthTextPatcher"/>): the six keypad-family
/// digit runs inside ddraw.dll's embedded 7z diff archive are rewritten, everything else survives
/// byte-identical, and the patched PE serves the rebuilt archive through its resource DataEntry
/// (docs/reference/dc1/puzzle/REBIRTH-DDRAW-TEXT-STORE-RE.md). Real-DLL tests skip silently when the
/// REbirth install is not in this checkout.
/// </summary>
public class RebirthTextPatcherTests
{
    private static string? FindRebirthDdraw()
    {
        string? data = Dc1EditionDetectorTests.FindGameDir("english");
        if (data is null) return null;
        string p = Path.Combine(Path.GetDirectoryName(data)!, "ddraw.dll");
        return Dc1EditionDetector.IsRebirthDdraw(p) ? p : null;
    }

    private static int[] TestCode(int row) => new[] { 6 + row, 7, 6, 7 - row }; // distinct per row

    [Fact]
    public void DigitRuns_CoverBothRegionsOfEveryDocumentedLock()
    {
        // 3 documented locks × (ST JP-text + AST US-text) — the DLL mirror of WriteRow(bothRegions:true).
        Assert.Equal(6, RebirthTextPatcher.DigitRuns.Count);
        foreach (var lk in Dc1PuzzleCodeSync.Family)
        {
            if (lk.DocFile is null) continue;
            Assert.Contains(RebirthTextPatcher.DigitRuns,
                r => r.Row == lk.Row && r.DiffPath.StartsWith("diff/ST", StringComparison.Ordinal)
                     && r.StockDigits.AsSpan().SequenceEqual(lk.OriginalDigits));
            Assert.Contains(RebirthTextPatcher.DigitRuns,
                r => r.Row == lk.Row && r.DiffPath.StartsWith("diff/AST", StringComparison.Ordinal)
                     && r.StockDigits.AsSpan().SequenceEqual(lk.UsDigits));
        }
    }

    [Fact]
    public void PatchPuzzleCodeText_RealDll_RewritesDigitsAndPreservesEverythingElse()
    {
        string? ddraw = FindRebirthDdraw();
        if (ddraw is null) return; // REbirth install not in this checkout — skip silently

        byte[] dll = File.ReadAllBytes(ddraw);
        Assert.False(RebirthTextPatcher.IsPuzzleCodeTextPatched(dll));

        byte[] patched = RebirthTextPatcher.PatchPuzzleCodeText(dll, TestCode);
        Assert.True(RebirthTextPatcher.IsPuzzleCodeTextPatched(patched));
        Assert.False(RebirthTextPatcher.IsPuzzleCodeTextPatched(dll)); // input untouched

        // The rebuilt archive hangs off the appended section (raw data begins at the old EOF) and the
        // DataEntry points at it.
        int newSize = (int)ExePatcher.ReadUInt32(patched, RebirthTextPatcher.ResourceDataEntryFileOffset + 4);
        var newBlob = patched.AsSpan(dll.Length, newSize).ToArray();
        var arcOld = SevenZipArchive.Read(
            dll.AsSpan(RebirthTextPatcher.ResourceBlobFileOffset, RebirthTextPatcher.ResourceBlobSize).ToArray());
        var arcNew = SevenZipArchive.Read(newBlob);

        // Every digit run shows its new code…
        foreach (var run in RebirthTextPatcher.DigitRuns)
        {
            byte[] diff = arcNew.ExtractFile(run.DiffPath);
            int[] want = TestCode(run.Row);
            for (int k = 0; k < want.Length; k++)
            {
                ushort tok = (ushort)(diff[run.Offset + 2 * k] | (diff[run.Offset + 2 * k + 1] << 8));
                Assert.Equal(MgmtOfficeDocumentCode.DigitToken(want[k]), tok);
            }
        }

        // …and every OTHER file in the archive decodes byte-identical (CRC-verified on both sides).
        int identical = 0;
        foreach (var (name, idx) in arcOld.StreamIndexByName)
        {
            bool isPatched = RebirthTextPatcher.DigitRuns.Any(
                r => string.Equals(r.DiffPath, name, StringComparison.OrdinalIgnoreCase));
            byte[] a = arcOld.ExtractStream(idx);
            byte[] b = arcNew.ExtractStream(arcNew.StreamIndexByName[name]);
            if (isPatched) Assert.False(a.AsSpan().SequenceEqual(b), $"{name} should have changed");
            else { Assert.True(a.AsSpan().SequenceEqual(b), $"{name} was corrupted"); identical++; }
        }
        Assert.Equal(arcOld.StreamIndexByName.Count - 6, identical);
    }

    /// <summary>
    /// Every <c>VerifyLayout</c> precondition must refuse a tampered DLL — verify-before-write is the
    /// whole safety contract of the lever. Each mutation targets one locked-layout anchor; the
    /// unmutated control still patches. Works on the stock and the vertex-lifted DLL alike (offsets:
    /// e_lfanew field 0x3C→0x138, NumberOfSections 0x13E, SizeOfImage 0x188, section table 0x230 with
    /// 0x28-byte headers — the locked REbirth build's PE geometry).
    /// </summary>
    [Fact]
    public void PatchPuzzleCodeText_RefusesEveryTamperedLayout()
    {
        string? ddraw = FindRebirthDdraw();
        if (ddraw is null) return; // REbirth install not in this checkout — skip silently

        byte[] stock = File.ReadAllBytes(ddraw);
        int sections = ExePatcher.ReadUInt16(stock, 0x13E);
        var mutations = new (string Label, Action<byte[]> Mutate)[]
        {
            ("MZ anchor broken", d => d[0] = (byte)'X'),
            ("PE signature broken", d => ExePatcher.WriteUInt32(d, 0x138, 0xDEADBEEF)),
            ("section count changed", d => ExePatcher.WriteUInt16(d, 0x13E, (ushort)(sections + 1))),
            ("next section slot occupied", d => d[0x230 + sections * 0x28] = 1),
            ("SizeOfImage changed", d => ExePatcher.WriteUInt32(d, 0x188, 0x600000)),
            ("resource DataEntry repointed", d => ExePatcher.WriteUInt32(
                d, RebirthTextPatcher.ResourceDataEntryFileOffset, 0x12345678)),
            ("resource DataEntry size changed", d => ExePatcher.WriteUInt32(
                d, RebirthTextPatcher.ResourceDataEntryFileOffset + 4, 0x1000)),
            ("embedded blob not 7z", d => d[RebirthTextPatcher.ResourceBlobFileOffset] = 0x00),
            ("truncated (foreign length)", d => { /* handled below via resize */ }),
        };
        foreach (var (label, mutate) in mutations)
        {
            byte[] dll = label.StartsWith("truncated", StringComparison.Ordinal)
                ? stock.AsSpan(0, stock.Length - 0x200).ToArray()
                : (byte[])stock.Clone();
            mutate(dll);
            var ex = Record.Exception(() => RebirthTextPatcher.PatchPuzzleCodeText(dll, TestCode));
            Assert.True(ex is InvalidOperationException, $"{label}: expected refusal, got {ex?.GetType().Name ?? "no exception"}");
        }

        // control: the untampered DLL still patches
        Assert.True(RebirthTextPatcher.IsPuzzleCodeTextPatched(
            RebirthTextPatcher.PatchPuzzleCodeText(stock, TestCode)));
    }

    [Fact]
    public void PatchPuzzleCodeText_RefusesDoublePatchAndForeignBuild()
    {
        string? ddraw = FindRebirthDdraw();
        if (ddraw is null) return;

        byte[] dll = File.ReadAllBytes(ddraw);
        byte[] patched = RebirthTextPatcher.PatchPuzzleCodeText(dll, TestCode);
        Assert.Throws<InvalidOperationException>(() => RebirthTextPatcher.PatchPuzzleCodeText(patched, TestCode));

        var foreign = new byte[1234];
        Assert.Throws<InvalidOperationException>(() => RebirthTextPatcher.PatchPuzzleCodeText(foreign, TestCode));
    }
}
