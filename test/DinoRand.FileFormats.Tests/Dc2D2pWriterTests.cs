using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// <see cref="Dc2D2pWriter"/> — the Classic REbirth <c>patch\ST*.d2p</c> emitter
/// (docs/reference/dc2/loader/LOADER-DC2.md §5, docs/parity/NONDESTRUCTIVE-INSTALL-PARITY.md).
/// Format tests lock the decoded wire format; <c>ApplyD2p</c> simulates CR's apply loop
/// (<c>ddraw.dll 0x1009d990</c>) so every patch is verified to reproduce the edited blob.
/// Runner tests (install-gated, repo-walk pattern) guard that the flag is purely additive.
/// </summary>
public class Dc2D2pWriterTests
{
    // ---- wire format (pure, always runs) ---------------------------------------------------------

    [Fact]
    public void FileNameFor_MatchesTheWrapperSprintf()
    {
        Assert.Equal("ST105.d2p", Dc2D2pWriter.FileNameFor(1, 0x05));
        Assert.Equal("ST000.d2p", Dc2D2pWriter.FileNameFor(0, 0x00));
        Assert.Equal("ST40B.d2p", Dc2D2pWriter.FileNameFor(4, 0x0B));
    }

    [Fact]
    public void Build_IdenticalBlobs_ReturnsNull()
    {
        var blob = MakeBlob(64);
        Assert.Null(Dc2D2pWriter.Build(blob, (byte[])blob.Clone()));
    }

    [Fact]
    public void Build_LengthMismatch_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Dc2D2pWriter.Build(MakeBlob(64), MakeBlob(68)));
    }

    [Fact]
    public void Build_SingleWordDiff_EmitsExactRecordAndTerminator()
    {
        var vanilla = MakeBlob(32);
        var edited = (byte[])vanilla.Clone();
        edited[8] = 0xAA; edited[11] = 0xBB;                       // word index 2

        var d2p = Dc2D2pWriter.Build(vanilla, edited)!;

        Assert.Equal(new byte[]
        {
            0x02, 0x00, 0x00,                                      // u24 LE dstWordIndex = 2
            0x01, 0x00, 0x00,                                      // u24 LE lenWords = 1
            edited[8], edited[9], edited[10], edited[11],          // payload
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,                    // terminator record header
        }, d2p);
    }

    [Fact]
    public void Build_ContiguousDiffWords_CoalesceIntoOneRecord()
    {
        var vanilla = MakeBlob(48);
        var edited = (byte[])vanilla.Clone();
        for (int i = 12; i < 24; i++) edited[i] ^= 0xFF;           // words 3,4,5

        var d2p = Dc2D2pWriter.Build(vanilla, edited)!;

        Assert.Equal(6 + 12 + 6, d2p.Length);                      // one header + 3 words + terminator
        Assert.Equal(3, d2p[0] | d2p[1] << 8 | d2p[2] << 16);      // idx 3
        Assert.Equal(3, d2p[3] | d2p[4] << 8 | d2p[5] << 16);      // len 3
        Assert.Equal(edited.AsSpan(12, 12).ToArray(), d2p[6..18]);
    }

    [Fact]
    public void Build_SeparatedRuns_EmitTwoRecords_IncludingFirstAndLastWord()
    {
        var vanilla = MakeBlob(40);
        var edited = (byte[])vanilla.Clone();
        edited[0] ^= 0x01;                                         // word 0
        edited[36] ^= 0x01;                                        // word 9 (last)

        var d2p = Dc2D2pWriter.Build(vanilla, edited)!;

        Assert.Equal(2 * (6 + 4) + 6, d2p.Length);
        Assert.Equal(0, d2p[0] | d2p[1] << 8 | d2p[2] << 16);
        Assert.Equal(9, d2p[10] | d2p[11] << 8 | d2p[12] << 16);
        Assert.Equal(edited, ApplyD2p(vanilla, d2p));
    }

    [Fact]
    public void Build_UnalignedTail_PadsFinalWordWithZeros()
    {
        var vanilla = MakeBlob(10);                                // 2 words + 2 tail bytes
        var edited = (byte[])vanilla.Clone();
        edited[9] ^= 0x5A;                                         // diff inside the tail

        var d2p = Dc2D2pWriter.Build(vanilla, edited)!;

        Assert.Equal(2, d2p[0] | d2p[1] << 8 | d2p[2] << 16);      // final (partial) word index
        Assert.Equal(new byte[] { edited[8], edited[9], 0x00, 0x00 }, d2p[6..10]);
        Assert.Equal(edited, ApplyD2p(vanilla, d2p));
    }

    [Fact]
    public void Build_FuzzedEdits_RoundTripThroughTheCrApplyLoop()
    {
        var rng = new Random(424242);
        for (int iter = 0; iter < 50; iter++)
        {
            var vanilla = new byte[4 * rng.Next(1, 400)];
            rng.NextBytes(vanilla);
            var edited = (byte[])vanilla.Clone();
            for (int m = rng.Next(0, 30); m > 0; m--)
                edited[rng.Next(edited.Length)] = (byte)rng.Next(256);

            var d2p = Dc2D2pWriter.Build(vanilla, edited);
            if (vanilla.AsSpan().SequenceEqual(edited)) { Assert.Null(d2p); continue; }
            Assert.Equal(edited, ApplyD2p(vanilla, d2p!));
        }
    }

    // ---- package level (synthetic — always runs) --------------------------------------------------

    [Fact]
    public void BuildFromPackages_DiffsTheDecompressedScdBlobs()
    {
        var blobA = MakeBlob(300);
        var blobB = (byte[])blobA.Clone();
        blobB[123] ^= 0x77;
        var pkgA = PackageWithBlob(blobA);
        var pkgB = PackageWithBlob(blobB);

        Assert.Null(Dc2D2pWriter.BuildFromPackages(pkgA, (byte[])pkgA.Clone()));

        var d2p = Dc2D2pWriter.BuildFromPackages(pkgA, pkgB)!;
        Assert.Equal(blobB, ApplyD2p(blobA, d2p));
    }

    // ---- runner integration (install-gated: no-op without the game data) --------------------------

    [Fact]
    public void Runner_FlagOff_EmitsNoPatchDir_AndFlagIsPurelyAdditive()
    {
        if (Dc2Install() is not { } install) return;
        var seed = new Seed(20260717);

        using var t = new TempDirs();
        new Dc2RandomizerRunner(new DinoCrisis2())
            .Run(install, t.A, seed, new RandomizerConfig(), emitSpoiler: false);
        new Dc2RandomizerRunner(new DinoCrisis2())
            .Run(install, t.B, seed, new RandomizerConfig { Dc2EmitD2pPatches = true }, emitSpoiler: false);

        Assert.False(Directory.Exists(Path.Combine(t.A, Dc2D2pWriter.PatchDirName)),
            "flag off must not create a patch dir");
        var patchDir = Path.Combine(t.B, Dc2D2pWriter.PatchDirName);
        Assert.True(Directory.Exists(patchDir) && Directory.GetFiles(patchDir, "*.d2p").Length > 0,
            "flag on must emit at least one .d2p for the touched rooms");

        // Additive: every non-patch output byte is identical between the two runs.
        foreach (var fileA in Directory.GetFiles(t.A))
        {
            var fileB = Path.Combine(t.B, Path.GetFileName(fileA));
            Assert.Equal(File.ReadAllBytes(fileA), File.ReadAllBytes(fileB));
        }
    }

    [Fact]
    public void Runner_EveryEmittedD2p_ReproducesTheEmittedRoomBlob()
    {
        if (Dc2Install() is not { } install) return;
        var dataDir = new DinoCrisis2().GetDataDir(install)!;

        using var t = new TempDirs();
        new Dc2RandomizerRunner(new DinoCrisis2())
            .Run(install, t.A, new Seed(20260717), new RandomizerConfig { Dc2EmitD2pPatches = true },
                 emitSpoiler: false);

        var patches = Directory.GetFiles(Path.Combine(t.A, Dc2D2pWriter.PatchDirName), "*.d2p");
        Assert.NotEmpty(patches);
        foreach (var patch in patches)
        {
            var stem = Path.GetFileNameWithoutExtension(patch);
            var sourceDat = Directory.EnumerateFiles(dataDir)
                .Single(f => Path.GetFileName(f).Equals(stem + ".DAT", StringComparison.OrdinalIgnoreCase));
            var emittedDat = Directory.EnumerateFiles(t.A)
                .Single(f => Path.GetFileName(f).Equals(stem + ".DAT", StringComparison.OrdinalIgnoreCase));

            var applied = ApplyD2p(DecompressBlob(File.ReadAllBytes(sourceDat)),
                                   File.ReadAllBytes(patch));
            Assert.Equal(DecompressBlob(File.ReadAllBytes(emittedDat)), applied);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static byte[] MakeBlob(int length)
    {
        var blob = new byte[length];
        for (int i = 0; i < length; i++) blob[i] = (byte)((i * 13 + 5) & 0xFF);
        return blob;
    }

    private static byte[] PackageWithBlob(byte[] blob) =>
        SyntheticRoom.Package(GianPackage.Dc2EntrySize,
            (GianEntryType.Lzss0, Lzss.Compress(blob)),
            (GianEntryType.Data, new byte[16]));

    private static byte[] DecompressBlob(byte[] package)
    {
        var pkg = GianPackage.TryParse(package)!;
        var entry = pkg.Entries.First(e => e.Type == GianEntryType.Lzss0);
        return Lzss.Decompress(package.AsSpan(entry.PayloadOffset, (int)entry.DeclaredSize));
    }

    /// <summary>Simulates CR's decoded apply loop (<c>ddraw.dll 0x1009d990</c>): read u24 idx /
    /// u24 lenWords, memcpy payload to <c>blob + idx*4</c>, stop at idx <c>0xFFFFFF</c>. The copy is
    /// clamped at the blob end the way the arena absorbs a padded final word.</summary>
    private static byte[] ApplyD2p(byte[] vanillaBlob, byte[] d2p)
    {
        var blob = (byte[])vanillaBlob.Clone();
        int p = 0;
        while (true)
        {
            int idx = d2p[p] | d2p[p + 1] << 8 | d2p[p + 2] << 16;
            if (idx == 0xFFFFFF) break;
            int len = d2p[p + 3] | d2p[p + 4] << 8 | d2p[p + 5] << 16;
            Array.Copy(d2p, p + 6, blob, idx * 4, Math.Min(len * 4, blob.Length - idx * 4));
            p += 6 + len * 4;
        }
        return blob;
    }

    /// <summary>The real DC2 install, or null to no-op — also when a seed is currently APPLIED
    /// (the runner would then re-randomize already-randomized rooms and the pristine-baseline
    /// assertions don't hold; the standard install-gated skip condition).</summary>
    private static string? Dc2Install()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DinoRand.sln")))
            {
                var install = Path.Combine(dir.FullName, "4249140_DinoCrisis2");
                if (new DinoCrisis2().GetDataDir(install) is not { } dataDir) return null;
                return Randomizer.Install.GameInstaller.IsInstalled(dataDir) ? null : install;
            }
        return null;
    }

    private sealed class TempDirs : IDisposable
    {
        public string A { get; } = MakeTemp();
        public string B { get; } = MakeTemp();
        private static string MakeTemp()
        {
            var d = Path.Combine(Path.GetTempPath(), "dinorand-d2p-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }
        public void Dispose()
        {
            foreach (var d in new[] { A, B })
                try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }
}
