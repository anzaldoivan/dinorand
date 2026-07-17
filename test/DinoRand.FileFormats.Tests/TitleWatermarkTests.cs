using System.Buffers.Binary;
using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Graphics;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Passes;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// TDD suite for the title-screen seed watermark (docs/decisions/cross/SEED-WATERMARK-PLAN.md):
/// the authored 5×7 <c>BitmapFont</c> blit, the DC1 <c>t_image.imd</c> bare-TIM edit, the DC2
/// <c>TITLE.DAT</c>/<c>TITLE2.DAT</c> LZSS0 background edit via <c>PackageRepacker.ReplaceEntry</c>,
/// and the two always-on passes. Synthetic fixtures only — no game bytes; the two
/// <c>RealInstall_*</c> contract tests are env-gated and skip without an install.
/// </summary>
public class TitleWatermarkTests
{
    // ---- fixture builders (synthetic; no game bytes) ---------------------------------------------

    private const int W = 320, H = 240;
    private const int PageBytes = W * H * 2;          // 153,600 — raw 15-bit full-screen page
    private const int TimFileSize = 20 + PageBytes;   // 153,620 — 8B TIM header + 12B image block + pixels

    /// <summary>Bare 16bpp TIM shaped exactly like DC1's <c>t_image.imd</c>, filled with
    /// <paramref name="fill"/> (default: PSX "opaque black" 0x8000, the real title's convention).</summary>
    private static byte[] BuildTim(ushort fill = 0x8000)
    {
        var tim = new byte[TimFileSize];
        BinaryPrimitives.WriteUInt32LittleEndian(tim.AsSpan(0), 0x10);            // magic
        BinaryPrimitives.WriteUInt32LittleEndian(tim.AsSpan(4), 0x02);            // type: 16bpp, no CLUT
        BinaryPrimitives.WriteUInt32LittleEndian(tim.AsSpan(8), 12 + PageBytes);  // image block size
        BinaryPrimitives.WriteUInt16LittleEndian(tim.AsSpan(12), 0);              // x
        BinaryPrimitives.WriteUInt16LittleEndian(tim.AsSpan(14), 0);              // y
        BinaryPrimitives.WriteUInt16LittleEndian(tim.AsSpan(16), W);
        BinaryPrimitives.WriteUInt16LittleEndian(tim.AsSpan(18), H);
        for (int i = 20; i < tim.Length; i += 2)
            BinaryPrimitives.WriteUInt16LittleEndian(tim.AsSpan(i), fill);
        return tim;
    }

    /// <summary>A raw 15-bit page filled with <paramref name="fill"/> (STP clear, like DC2's titles).</summary>
    private static byte[] BuildPage(ushort fill = 0x1084)
    {
        var page = new byte[PageBytes];
        for (int i = 0; i < page.Length; i += 2)
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(i), fill);
        return page;
    }

    /// <summary>Synthetic DC2 (32-byte-entry) Gian package from (type, payload) pairs; reserve
    /// fields get a distinctive non-zero marker on entries past the first so header preservation
    /// is provable (the first entry's reserve tail must stay zero — it is the DC2 stride probe).</summary>
    private static byte[] BuildDc2Package(params (GianEntryType Type, byte[] Payload)[] entries)
    {
        static int Align(int v) => (v + 2047) & ~2047;
        int total = GianPackage.HeaderSize + entries.Sum(e => Align(e.Payload.Length));
        var pkg = new byte[total];
        int pos = GianPackage.HeaderSize;
        for (int i = 0; i < entries.Length; i++)
        {
            int off = i * GianPackage.Dc2EntrySize;
            BinaryPrimitives.WriteUInt32LittleEndian(pkg.AsSpan(off), (uint)entries[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(pkg.AsSpan(off + 4), (uint)entries[i].Payload.Length);
            if (i > 0) // res0/res1 marker (e.g. a VRAM rect / RAM base); first entry must stay zero
            {
                BinaryPrimitives.WriteUInt32LittleEndian(pkg.AsSpan(off + 8), 0xC0FFEE00u + (uint)i);
                BinaryPrimitives.WriteUInt32LittleEndian(pkg.AsSpan(off + 12), 0xBEEF0000u + (uint)i);
            }
            entries[i].Payload.CopyTo(pkg.AsSpan(pos));
            pos += Align(entries[i].Payload.Length);
        }
        return pkg;
    }

    private static ushort Px(ReadOnlySpan<byte> pixels, int x, int y, int width = W)
        => BinaryPrimitives.ReadUInt16LittleEndian(pixels.Slice((y * width + x) * 2));

    /// <summary>(x,y) positions whose pixel differs between two equal-size 16bpp buffers.</summary>
    private static List<(int X, int Y)> DiffPositions(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width)
    {
        var diffs = new List<(int, int)>();
        for (int i = 0; i < a.Length; i += 2)
            if (a[i] != b[i] || a[i + 1] != b[i + 1])
                diffs.Add(((i / 2) % width, (i / 2) / width));
        return diffs;
    }

    // ---- 1. font unit -----------------------------------------------------------------------------

    [Fact]
    public void Font_BlitsExpected5x7Pattern_ForA()
    {
        var expected = new[]
        {
            ".###.",
            "#...#",
            "#...#",
            "#####",
            "#...#",
            "#...#",
            "#...#",
        };

        var buf = new byte[16 * 16 * 2]; // zeros: STP clear
        BitmapFont.Blit(buf, 16, 16, 1, 1, "A");

        for (int row = 0; row < 7; row++)
            for (int col = 0; col < 5; col++)
            {
                ushort px = Px(buf, 1 + col, 1 + row, 16);
                if (expected[row][col] == '#')
                    Assert.Equal(0x7FFF, px);
                else
                    Assert.Equal(0, px);
            }
    }

    [Fact]
    public void Font_PreservesStpBit_OfOverwrittenPixel()
    {
        var stpSet = new byte[8 * 8 * 2];
        for (int i = 0; i < stpSet.Length; i += 2)
            BinaryPrimitives.WriteUInt16LittleEndian(stpSet.AsSpan(i), 0x8000); // opaque black
        BitmapFont.Blit(stpSet, 8, 8, 0, 0, "I");

        var stpClear = new byte[8 * 8 * 2];
        BitmapFont.Blit(stpClear, 8, 8, 0, 0, "I");

        // Any lit pixel: white with the background's bit 15 carried through.
        bool sawLit = false;
        for (int i = 0; i < stpSet.Length; i += 2)
        {
            ushort a = BinaryPrimitives.ReadUInt16LittleEndian(stpSet.AsSpan(i));
            ushort b = BinaryPrimitives.ReadUInt16LittleEndian(stpClear.AsSpan(i));
            if (b == 0x7FFF) { Assert.Equal(0xFFFF, a); sawLit = true; }
            else { Assert.Equal(0, b); Assert.Equal(0x8000, a); }
        }
        Assert.True(sawLit, "the glyph lit no pixels");
    }

    [Fact]
    public void Font_SkipsUnmappedChars_ButKeepsAdvance()
    {
        var withGap = new byte[64 * 16 * 2];
        BitmapFont.Blit(withGap, 64, 16, 0, 0, "A?A");

        var reference = new byte[64 * 16 * 2];
        BitmapFont.Blit(reference, 64, 16, 0, 0, "A");
        BitmapFont.Blit(reference, 64, 16, 2 * BitmapFont.Advance, 0, "A");

        Assert.Equal(reference, withGap); // '?' drew nothing but consumed one advance
    }

    [Fact]
    public void Font_IsCaseSensitive_ForTheSeedStringAlphabet()
    {
        // The DINO-{base64url} seed string is case-sensitive and uses '_' — every
        // base64url character must render, and 'a' must NOT render as 'A'
        // (an uppercase-folded string could not be re-typed into the GUI).
        var lower = new byte[16 * 16 * 2];
        var upper = new byte[16 * 16 * 2];
        BitmapFont.Blit(lower, 16, 16, 0, 0, "a");
        BitmapFont.Blit(upper, 16, 16, 0, 0, "A");
        Assert.NotEqual(upper, lower);
        Assert.NotEqual(new byte[16 * 16 * 2], lower); // lowercase has its own glyph, not a skip

        const string base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        foreach (var c in base64UrlAlphabet)
        {
            var buf = new byte[16 * 16 * 2];
            BitmapFont.Blit(buf, 16, 16, 0, 0, c.ToString());
            Assert.NotEqual(new byte[16 * 16 * 2], buf); // every alphabet char draws something
        }
    }

    [Fact]
    public void Font_ClipsAtImageEdge_WithoutWrappingOrThrowing()
    {
        const int w = 20, h = 10;
        var buf = new byte[w * h * 2];
        BitmapFont.Blit(buf, w, h, 12, 2, "WWW"); // 3 glyphs × 6px from x=12 → runs past x=19

        // Nothing wrapped to the left column of any row and nothing landed outside rows 2..8.
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(0, Px(buf, 0, y, w));
            if (y is < 2 or > 8)
                for (int x = 0; x < w; x++)
                    Assert.Equal(0, Px(buf, x, y, w));
        }
        // The visible part did draw.
        Assert.Contains(DiffPositions(new byte[w * h * 2], buf, w), p => p.X >= 12);
    }

    // ---- 2/3/4. DC1 bare-TIM edit ------------------------------------------------------------------

    [Fact]
    public void Dc1_Apply_EditsOnlyTheTextRegion_AndKeepsHeader()
    {
        var tim = BuildTim();
        var output = Dc1TitleWatermark.Apply(tim, "DINORAND V0.5.1", "SEED 12345");

        Assert.Equal(TimFileSize, output.Length);
        Assert.Equal(tim.AsSpan(0, 20).ToArray(), output.AsSpan(0, 20).ToArray()); // header untouched
        Assert.Equal(tim, BuildTim()); // input never mutated

        var diffs = DiffPositions(tim.AsSpan(20), output.AsSpan(20), W);
        Assert.NotEmpty(diffs);
        // Locked anchor: two lines at (4,4) and (4,12); 5×7 glyphs ⇒ rows 4..18, x ≥ 4.
        Assert.All(diffs, p =>
        {
            Assert.InRange(p.Y, 4, 4 + BitmapFont.LineHeight + BitmapFont.GlyphHeight - 1);
            Assert.InRange(p.X, 4, 4 + 15 * BitmapFont.Advance - 1); // longest line = 15 chars
        });
        // Text pixels are white and preserve the 0x8000 background's STP bit.
        foreach (var (x, y) in diffs)
            Assert.Equal(0xFFFF, Px(output.AsSpan(20), x, y));

        Assert.Equal(output, Dc1TitleWatermark.Apply(tim, "DINORAND V0.5.1", "SEED 12345")); // deterministic
    }

    [Theory]
    [InlineData(0)]  // magic
    [InlineData(4)]  // type (0x08 CLUT TIM)
    [InlineData(16)] // width
    public void Dc1_Apply_RejectsWrongFormat(int corruptOffset)
    {
        var tim = BuildTim();
        tim[corruptOffset] = 0x08;
        Assert.Throws<InvalidDataException>(() => Dc1TitleWatermark.Apply(tim, "A", "B"));
    }

    [Fact]
    public void Dc1_Apply_RejectsTruncatedFile()
    {
        var tim = BuildTim().AsSpan(0, 1000).ToArray();
        Assert.Throws<InvalidDataException>(() => Dc1TitleWatermark.Apply(tim, "A", "B"));
    }

    // ---- 5. PackageRepacker.ReplaceEntry ------------------------------------------------------------

    [Fact]
    public void ReplaceEntry_DifferentSizedPayload_RoundTrips()
    {
        var pkg = BuildDc2Package(
            (GianEntryType.Sound, new byte[100]),
            (GianEntryType.Lzss0, Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray()),
            (GianEntryType.Data, Enumerable.Repeat((byte)0xAB, 50).ToArray()));

        var replacement = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var rebuilt = PackageRepacker.ReplaceEntryDc2(pkg, 1, replacement);

        var parsed = GianPackage.TryParse(rebuilt);
        Assert.NotNull(parsed);
        Assert.True(parsed!.IsDc2);
        Assert.Equal(3, parsed.Entries.Count);
        Assert.Equal(new[] { GianEntryType.Sound, GianEntryType.Lzss0, GianEntryType.Data },
                     parsed.Entries.Select(e => e.Type).ToArray());

        // Replaced payload is the new bytes; neighbors byte-identical; sector alignment holds.
        Assert.Equal(replacement,
            rebuilt.AsSpan(parsed.Entries[1].PayloadOffset, (int)parsed.Entries[1].DeclaredSize).ToArray());
        Assert.Equal(new byte[100],
            rebuilt.AsSpan(parsed.Entries[0].PayloadOffset, 100).ToArray());
        Assert.Equal(Enumerable.Repeat((byte)0xAB, 50).ToArray(),
            rebuilt.AsSpan(parsed.Entries[2].PayloadOffset, 50).ToArray());
        Assert.All(parsed.Entries, e => Assert.Equal(0, e.PayloadOffset % GianPackage.SectorSize));
    }

    [Fact]
    public void ReplaceEntry_PreservesHeaderReserveBytes()
    {
        var pkg = BuildDc2Package(
            (GianEntryType.Sound, new byte[10]),
            (GianEntryType.Lzss0, new byte[10]));

        var rebuilt = PackageRepacker.ReplaceEntryDc2(pkg, 1, new byte[7000]);

        // Every header byte except entry 1's size u32 (offset 32+4..32+7) is unchanged —
        // reserve fields (VRAM rects / RAM bases) must survive verbatim.
        for (int i = 0; i < GianPackage.HeaderSize; i++)
        {
            if (i is >= 36 and < 40) continue;
            Assert.True(pkg[i] == rebuilt[i], $"header byte 0x{i:X} changed");
        }
        Assert.Equal(7000u, BinaryPrimitives.ReadUInt32LittleEndian(rebuilt.AsSpan(36)));
    }

    // ---- 6. DC2 package watermark -------------------------------------------------------------------

    [Fact]
    public void Dc2_Apply_WatermarksTheFullscreenLzss0_AndOnlyIt()
    {
        var page = BuildPage();
        var smallTable = Enumerable.Range(0, 656).Select(i => (byte)i).ToArray();
        var pkg = BuildDc2Package(
            (GianEntryType.Sound, new byte[300]),
            (GianEntryType.Lzss0, Lzss.Compress(smallTable)),   // decoy: TITLE2's small LZSS0
            (GianEntryType.Lzss0, Lzss.Compress(page)),         // the 153,600-B background
            (GianEntryType.Palette, new byte[64]));

        var rebuilt = Dc2TitleWatermark.Apply(pkg, "DINORAND V0.5.1", "SEED 42");
        var parsed = GianPackage.TryParse(rebuilt)!;

        Assert.Equal(4, parsed.Entries.Count);

        // The small LZSS0 is untouched (size-based selection, not positional).
        Assert.Equal(smallTable, Lzss.Decompress(
            rebuilt.AsSpan(parsed.Entries[1].PayloadOffset, (int)parsed.Entries[1].DeclaredSize)));

        // The big LZSS0 decompresses to the directly-blitted page.
        var expected = (byte[])page.Clone();
        BitmapFont.Blit(expected, W, H, 4, 4, "DINORAND V0.5.1");
        BitmapFont.Blit(expected, W, H, 4, 4 + BitmapFont.LineHeight, "SEED 42");
        var actual = Lzss.Decompress(
            rebuilt.AsSpan(parsed.Entries[2].PayloadOffset, (int)parsed.Entries[2].DeclaredSize));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Dc2_Apply_Throws_WhenNoFullscreenPageExists()
    {
        var pkg = BuildDc2Package(
            (GianEntryType.Sound, new byte[300]),
            (GianEntryType.Lzss0, Lzss.Compress(new byte[500])));
        Assert.Throws<InvalidDataException>(() => Dc2TitleWatermark.Apply(pkg, "A", "B"));
    }

    // ---- 7. DC1 pass seam ----------------------------------------------------------------------------

    private static RandomizationContext Dc1Context(string? installDir, RandomizerConfig? config = null, int seed = 12345)
    {
        var rooms = Array.Empty<RoomFile>();
        return new RandomizationContext(new DinoCrisis1(), rooms, RoomGraph.Build(rooms),
                                        new Seed(seed), config ?? new RandomizerConfig(), _ => { }, installDir);
    }

    private static string MakeDc1Install(string? timName = "t_image.imd", byte[]? tim = null)
    {
        var root = Directory.CreateTempSubdirectory("dinorand-wm-dc1-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        // GetDataDir validates a Data dir by room-file presence — a real install always has rooms.
        File.WriteAllBytes(Path.Combine(root, "Data", "st100.dat"), new byte[16]);
        if (timName is not null)
            File.WriteAllBytes(Path.Combine(root, "Data", timName), tim ?? BuildTim());
        return root;
    }

    [Fact]
    public void Dc1Pass_EnabledByDefault_TestOptOutWorks()
    {
        Assert.True(new TitleWatermarkPass().IsEnabled(new RandomizerConfig()));
        Assert.False(new TitleWatermarkPass().IsEnabled(new RandomizerConfig { TitleWatermark = false }));
    }

    [Fact]
    public void Dc1Pass_EmitsWatermarkedLooseFile()
    {
        var root = MakeDc1Install();
        var ctx = Dc1Context(root);
        new TitleWatermarkPass().Apply(ctx);

        var (rel, bytes) = Assert.Single(ctx.LooseFiles);
        Assert.Equal("Data/t_image.imd", rel);
        Assert.Equal(TimFileSize, bytes.Length);
        Assert.NotEqual(BuildTim(), bytes); // watermarked

        // The seed is part of the text: a different seed yields different pixels.
        var other = Dc1Context(root, seed: 99999);
        new TitleWatermarkPass().Apply(other);
        Assert.NotEqual(bytes, other.LooseFiles["Data/t_image.imd"]);
    }

    [Fact]
    public void Dc1Pass_WatermarksTheGuiSeedString_NotTheRawInt()
    {
        // The GUI's seed box shows AppSeed.ToString() == SeedString.Encode(seed, config)
        // ("DINO-…", seed + config). The title must show the SAME identity, byte-for-byte:
        // pass output == a direct Apply with that exact string.
        var config = new RandomizerConfig { ShuffleKeyItems = true }; // config alters the encoding
        var root = MakeDc1Install();
        var ctx = Dc1Context(root, config);
        new TitleWatermarkPass().Apply(ctx);

        var expected = Dc1TitleWatermark.Apply(BuildTim(),
            $"DINORAND V{SpoilerLogBuilder.AppVersion()}",
            SeedString.Encode(ctx.Seed, config));
        Assert.Equal(expected, ctx.LooseFiles["Data/t_image.imd"]);

        // Same seed int, different config ⇒ different identity ⇒ different pixels.
        var otherCfg = Dc1Context(root, new RandomizerConfig());
        new TitleWatermarkPass().Apply(otherCfg);
        Assert.NotEqual(ctx.LooseFiles["Data/t_image.imd"], otherCfg.LooseFiles["Data/t_image.imd"]);
    }

    [Fact]
    public void Dc2Pass_WatermarksTheGuiSeedString_NotTheRawInt()
    {
        var dataDir = Directory.CreateTempSubdirectory("dinorand-wm-dc2-str-").FullName;
        File.WriteAllBytes(Path.Combine(dataDir, "TITLE.DAT"), BuildTitleDat());

        var outDir = Directory.CreateTempSubdirectory("dinorand-wm-out-").FullName;
        var sink = new Dc2OutputDirSink(outDir);
        var config = new RandomizerConfig();
        var ctx = new Dc2RandomizationContext(new DinoCrisis2(), Array.Empty<FileFormats.Stage.Dc2.Dc2RoomFile>(),
                                              new Seed(7), config, _ => { }, sink, dataDir);
        new Dc2TitleWatermarkPass().Apply(ctx);

        var expected = Dc2TitleWatermark.Apply(BuildTitleDat(),
            $"DINORAND V{SpoilerLogBuilder.AppVersion()}",
            SeedString.Encode(ctx.Seed, config));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(outDir, "TITLE.DAT")));
    }

    [Fact]
    public void Dc1Pass_FindsFile_CaseInsensitively()
    {
        var root = MakeDc1Install(timName: "T_IMAGE.IMD");
        var ctx = Dc1Context(root);
        new TitleWatermarkPass().Apply(ctx);
        Assert.Single(ctx.LooseFiles);
    }

    [Fact]
    public void Dc1Pass_SkipsQuietly_WhenFileMissingOrMalformed()
    {
        var missing = Dc1Context(MakeDc1Install(timName: null));
        new TitleWatermarkPass().Apply(missing);
        Assert.Empty(missing.LooseFiles);

        var malformed = Dc1Context(MakeDc1Install(tim: new byte[300]));
        new TitleWatermarkPass().Apply(malformed);
        Assert.Empty(malformed.LooseFiles);

        var noInstall = Dc1Context(installDir: null);
        new TitleWatermarkPass().Apply(noInstall); // must not throw
        Assert.Empty(noInstall.LooseFiles);
    }

    [Fact]
    public void Dc1Pass_ReadsPristineBackup_NotTheLiveFile()
    {
        var root = MakeDc1Install(tim: BuildTim(fill: 0x0000)); // "live" file: distinctive fill
        // Pristine loose backup from a previous install: the 0x8000 original.
        var backup = Path.Combine(root, "Data", GameInstaller.BackupDirName,
                                  GameInstaller.LooseBackupSubdir, "Data");
        Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, "t_image.imd"), BuildTim(fill: 0x8000));

        var ctx = Dc1Context(root);
        new TitleWatermarkPass().Apply(ctx);

        var bytes = ctx.LooseFiles["Data/t_image.imd"];
        // An untouched corner pixel proves the source: pristine 0x8000, not live 0x0000.
        Assert.Equal(0x8000, Px(bytes.AsSpan(20), W - 1, H - 1));
    }

    // ---- 8. DC2 pass seam ----------------------------------------------------------------------------

    private static byte[] BuildTitleDat() => BuildDc2Package(
        (GianEntryType.Sound, new byte[100]),
        (GianEntryType.Lzss0, Lzss.Compress(BuildPage())),
        (GianEntryType.Lzss0, Lzss.Compress(new byte[656])));

    [Fact]
    public void Dc2Pass_EmitsBothWatermarkedTitleFiles()
    {
        var dataDir = Directory.CreateTempSubdirectory("dinorand-wm-dc2-").FullName;
        File.WriteAllBytes(Path.Combine(dataDir, "TITLE.DAT"), BuildTitleDat());
        File.WriteAllBytes(Path.Combine(dataDir, "TITLE2.DAT"), BuildTitleDat());

        var outDir = Directory.CreateTempSubdirectory("dinorand-wm-out-").FullName;
        var sink = new Dc2OutputDirSink(outDir);
        var ctx = new Dc2RandomizationContext(new DinoCrisis2(), Array.Empty<FileFormats.Stage.Dc2.Dc2RoomFile>(),
                                              new Seed(7), new RandomizerConfig(), _ => { }, sink, dataDir);
        new Dc2TitleWatermarkPass().Apply(ctx);

        foreach (var name in new[] { "TITLE.DAT", "TITLE2.DAT" })
        {
            var written = Path.Combine(outDir, name);
            Assert.True(File.Exists(written), $"{name} not emitted");
            var parsed = GianPackage.TryParse(File.ReadAllBytes(written));
            Assert.NotNull(parsed);
            var vanilla = GianPackage.TryParse(BuildTitleDat())!;
            Assert.Equal(vanilla.Entries.Count, parsed!.Entries.Count);
            Assert.NotEqual(BuildTitleDat(), File.ReadAllBytes(written)); // watermarked
        }
    }

    [Fact]
    public void Dc2Pass_SkipsQuietly_WhenTitleFilesAbsent()
    {
        var dataDir = Directory.CreateTempSubdirectory("dinorand-wm-dc2-empty-").FullName;
        var sink = new Dc2OutputDirSink(Directory.CreateTempSubdirectory("dinorand-wm-out-").FullName);
        var ctx = new Dc2RandomizationContext(new DinoCrisis2(), Array.Empty<FileFormats.Stage.Dc2.Dc2RoomFile>(),
                                              new Seed(7), new RandomizerConfig(), _ => { }, sink, dataDir);
        new Dc2TitleWatermarkPass().Apply(ctx); // must not throw
        Assert.Equal(0, sink.FilesWritten);
    }

    [Fact]
    public void Dc2Pass_ReadsPristineBackup_NotTheLiveFile()
    {
        var dataDir = Directory.CreateTempSubdirectory("dinorand-wm-dc2-bak-").FullName;
        // Live file: a decoy with a DIFFERENT page fill; backup: the pristine build.
        var live = BuildDc2Package(
            (GianEntryType.Sound, new byte[100]),
            (GianEntryType.Lzss0, Lzss.Compress(BuildPage(fill: 0x7C00))),
            (GianEntryType.Lzss0, Lzss.Compress(new byte[656])));
        File.WriteAllBytes(Path.Combine(dataDir, "TITLE.DAT"), live);
        var backupDir = Path.Combine(dataDir, GameInstaller.BackupDirName);
        Directory.CreateDirectory(backupDir);
        File.WriteAllBytes(Path.Combine(backupDir, "TITLE.DAT"), BuildTitleDat());

        var outDir = Directory.CreateTempSubdirectory("dinorand-wm-out-").FullName;
        var sink = new Dc2OutputDirSink(outDir);
        var ctx = new Dc2RandomizationContext(new DinoCrisis2(), Array.Empty<FileFormats.Stage.Dc2.Dc2RoomFile>(),
                                              new Seed(7), new RandomizerConfig(), _ => { }, sink, dataDir);
        new Dc2TitleWatermarkPass().Apply(ctx);

        var parsed = GianPackage.TryParse(File.ReadAllBytes(Path.Combine(outDir, "TITLE.DAT")))!;
        var bg = Lzss.Decompress(File.ReadAllBytes(Path.Combine(outDir, "TITLE.DAT"))
            .AsSpan(parsed.Entries[1].PayloadOffset, (int)parsed.Entries[1].DeclaredSize));
        // Corner pixel proves the pristine (0x1084) source, not the live decoy (0x7C00).
        Assert.Equal(0x1084, Px(bg, W - 1, H - 1));
    }

    // ---- 9. installer loose-overlay of the DC1 title image --------------------------------------------

    [Fact]
    public void Installer_OverlaysAndRestores_TitleImageLooseFile()
    {
        var root = MakeDc1Install();
        var dataDir = Path.Combine(root, "Data");
        var original = File.ReadAllBytes(Path.Combine(dataDir, "t_image.imd"));

        var modDir = Directory.CreateTempSubdirectory("dinorand-wm-mod-").FullName;
        Directory.CreateDirectory(Path.Combine(modDir, "Data"));
        var watermarked = Dc1TitleWatermark.Apply(original, "DINORAND V0", "SEED 1");
        File.WriteAllBytes(Path.Combine(modDir, "Data", "t_image.imd"), watermarked);

        GameInstaller.Install(dataDir, modDir);
        Assert.Equal(watermarked, File.ReadAllBytes(Path.Combine(dataDir, "t_image.imd")));

        GameInstaller.Restore(dataDir);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(dataDir, "t_image.imd")));
    }

    // ---- 10. install-gated byte-fact contracts (skip without .env) ------------------------------------

    [Fact]
    public void RealInstall_Dc1TitleImage_MatchesContract()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(Path.Combine(root, "Data"))) return; // env-gated
        var path = Directory.EnumerateFiles(Path.Combine(root, "Data"))
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), "t_image.imd", StringComparison.OrdinalIgnoreCase));
        if (path is null) return;

        var output = Dc1TitleWatermark.Apply(File.ReadAllBytes(path), "DINORAND", "SEED 1");
        Assert.Equal(TimFileSize, output.Length);
    }

    [Fact]
    public void RealInstall_Dc2TitleContainers_HaveExactlyOneFullscreenLzss0()
    {
        var dataDir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrEmpty(dataDir) || !Directory.Exists(dataDir)) return; // env-gated

        foreach (var name in new[] { "TITLE.DAT", "TITLE2.DAT" })
        {
            var path = Path.Combine(dataDir, name);
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            var pkg = GianPackage.TryParse(bytes);
            Assert.NotNull(pkg);
            int fullscreen = pkg!.Entries.Count(e => e.Type == GianEntryType.Lzss0
                && Lzss.Decompress(bytes.AsSpan(e.PayloadOffset, (int)e.DeclaredSize)).Length == PageBytes);
            Assert.Equal(1, fullscreen);
        }
    }
}
