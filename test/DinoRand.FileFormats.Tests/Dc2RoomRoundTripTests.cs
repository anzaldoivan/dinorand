using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// T8 — DC2 (Rebirth) round-trip safety gate over the full 89-room corpus
/// (docs/dc2/RANDO-ROADMAP-PLAN.md Phase 0). The C# CI twin of the standalone
/// <c>tools/dc2_re/roundtrip_t8.py</c> harness, run against the <b>shipped</b> codec
/// (<see cref="Lzss"/>) and container (<see cref="GianPackage"/>) — the same building
/// blocks any DC2 write path uses. Before any <c>ST*.DAT</c> is ever written, this must be
/// green on every room.
///
/// <para><b>Basis = rebirth.</b> The rebirth room blob differs byte-for-byte from
/// english/japanese (README.md; T0), so the gate reads <c>rebirth/Data</c>. Point
/// <c>DINORAND_DC2_DIR</c> at any DC2 install's parent to override; with no game files
/// present it no-ops (the project's "no game files → skip" convention).</para>
///
/// Two properties are asserted per room and one is measured:
/// <list type="number">
/// <item><b>P1 container repack identity</b> — re-emitting the package (2048-byte header +
/// each entry's sector-aligned payload, verbatim) reproduces the original file
/// byte-for-byte: the <c>Write(Read(x)) == x</c> non-destructive guarantee.</item>
/// <item><b>P2 codec round-trip fidelity</b> — every compressed payload survives
/// <c>Decompress → Compress → Decompress</c> identically, so a slot-5 edit's recompression
/// is lossless.</item>
/// <item><b>P3 re-LZSS size tolerance</b> (measured, not failed) — the recompressed SCD blob
/// never grows (our compressor is ≤ the original); whether the engine accepts a
/// sector-count change is a RUNTIME/HUMAN-GATED confirm (OPEN #9).</item>
/// </list>
/// </summary>
public class Dc2RoomRoundTripTests
{
    private static readonly System.Text.RegularExpressions.Regex RoomPattern =
        new(@"^st([0-9a-f])([0-9a-f]{2})\.dat$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static IEnumerable<object[]> RoomFiles()
    {
        var dataDir = FindRebirthDataDir();
        if (dataDir is null) yield break;
        foreach (var path in Directory.EnumerateFiles(dataDir, "ST*.DAT"))
            if (RoomPattern.IsMatch(Path.GetFileName(path)))
                yield return new object[] { path };
    }

    /// <summary>P1 — re-emitting the container verbatim reproduces the file byte-for-byte.</summary>
    [Theory]
    [MemberData(nameof(RoomFiles))]
    public void Room_ContainerRepack_IsByteIdentical(string path)
    {
        var original = File.ReadAllBytes(path);
        var pkg = GianPackage.TryParse(original);
        Assert.NotNull(pkg);

        var rebuilt = new byte[original.Length];
        Array.Copy(original, rebuilt, GianPackage.HeaderSize); // header verbatim
        int pos = GianPackage.HeaderSize;
        foreach (var e in pkg!.Entries)
        {
            // Copy the entry's full sector-aligned span (payload + its original padding) verbatim.
            Array.Copy(original, e.PayloadOffset, rebuilt, pos, e.AlignedSize);
            pos += e.AlignedSize;
        }

        // The parsed entries must tile the file exactly: no gaps, no trailing data.
        Assert.Equal(original.Length, pos);
        Assert.Equal(original, rebuilt);
    }

    /// <summary>P2 — every compressed payload survives Decompress→Compress→Decompress intact.</summary>
    [Theory]
    [MemberData(nameof(RoomFiles))]
    public void Room_CompressedPayloads_RoundTripLosslessly(string path)
    {
        var original = File.ReadAllBytes(path);
        var pkg = GianPackage.TryParse(original);
        Assert.NotNull(pkg);

        int compressedEntries = 0;
        foreach (var e in pkg!.Entries)
        {
            if (e.Type is not (GianEntryType.Lzss0 or GianEntryType.Lzss1 or GianEntryType.Lzss2))
                continue;
            compressedEntries++;

            var decomp = Lzss.Decompress(original.AsSpan(e.PayloadOffset, (int)e.DeclaredSize));
            var reDecoded = Lzss.Decompress(Lzss.Compress(decomp));
            Assert.Equal(decomp, reDecoded);
        }

        // Every DC2 room blob carries at least one LZSS0 SCD segment.
        Assert.True(compressedEntries > 0, $"{Path.GetFileName(path)} has no compressed payload");
    }

    /// <summary>P3 — the recompressed SCD blob's <b>sector cost</b> never exceeds the original's, so a
    /// re-encode never needs more 2048-byte sectors than the game reserved and downstream payloads never
    /// have to shift outward. (The shipped compressor is not size-optimal — a handful of rooms grow by
    /// ~1 raw byte — but the sector-aligned footprint is the property that matters for an in-place-safe
    /// write; whether the engine tolerates a sector-count change at all is a RUNTIME/HUMAN-GATED confirm,
    /// OPEN #9.)</summary>
    [Theory]
    [MemberData(nameof(RoomFiles))]
    public void Room_RecompressedScdBlob_FitsOriginalSectorCount(string path)
    {
        var original = File.ReadAllBytes(path);
        var pkg = GianPackage.TryParse(original);
        Assert.NotNull(pkg);

        var scd = pkg!.Entries.FirstOrDefault(e => e.Type == GianEntryType.Lzss0);
        if (scd.Type != GianEntryType.Lzss0) return; // no SCD blob → nothing to measure

        var decomp = Lzss.Decompress(original.AsSpan(scd.PayloadOffset, (int)scd.DeclaredSize));
        int recompressed = Lzss.Compress(decomp).Length;

        static int Sectors(int n) => (n + GianPackage.SectorSize - 1) / GianPackage.SectorSize;
        Assert.True(Sectors(recompressed) <= Sectors((int)scd.DeclaredSize),
            $"{Path.GetFileName(path)}: recompress {scd.DeclaredSize}->{recompressed} crosses a sector " +
            $"boundary ({Sectors((int)scd.DeclaredSize)}->{Sectors(recompressed)} sectors); needs the " +
            "variable-segment write path (OPEN #9)");
    }

    /// <summary>Walk up to the repo root, then locate the canonical rebirth Data dir. Honors
    /// <c>DINORAND_DC2_DIR</c> (its parent of a <c>Data</c> dir, or the Data dir itself). Returns
    /// null if not found → the theories yield no cases and the gate no-ops.</summary>
    private static string? FindRebirthDataDir()
    {
        var env = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (!string.IsNullOrEmpty(env))
        {
            if (HasRooms(env)) return env;
            var d = Path.Combine(env, "Data");
            if (HasRooms(d)) return d;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var reb = Path.Combine(dir.FullName, "4249140_DinoCrisis2", "rebirth", "Data");
            if (HasRooms(reb)) return reb;
            if (File.Exists(Path.Combine(dir.FullName, "DinoRand.sln"))) break;
        }
        return null;
    }

    private static bool HasRooms(string dir) =>
        Directory.Exists(dir) &&
        Directory.EnumerateFiles(dir, "ST*.DAT").Any(f => RoomPattern.IsMatch(Path.GetFileName(f)));
}
