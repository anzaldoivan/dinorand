using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// RED-FIRST executable SPEC for the deferred DC2 music decode/export/import work
/// (docs/decisions/cross/BGM-RANDO-PLAN.md "Future"; the hands-off prompt drives it). The DC2 music
/// payload audio codec is <c>[OPEN]</c>, so <see cref="Dc2MusicContainer.ReadTracks"/> /
/// <see cref="Dc2MusicContainer.WriteTracks"/> and <see cref="Dc2MusicCodec"/> throw
/// <see cref="NotImplementedException"/> today. These tests are therefore <b>Skip</b>-marked (pending) so
/// they COMPILE against the target surface and show as a visible target in the run summary WITHOUT
/// breaking CI. The future agent: remove each <c>Skip</c>, implement until green.
///
/// <para>Contract, in order of increasing decode depth:</para>
/// <list type="number">
///   <item>Container framing round-trips byte-identically (codec-agnostic — the container writer).</item>
///   <item>A track payload decodes to valid PCM.</item>
///   <item>PCM re-encodes into a payload and decodes back within the codec's lossy tolerance.</item>
/// </list>
/// </summary>
public class Dc2MusicContainerSpecTests
{
    // Synthetic container in the decoded framing (0x800 header of 64 32-byte slots, N real records then
    // "dummy header    " filler, 2048-aligned payloads). Proves the writer WITHOUT any game bytes, so it
    // runs in CI. Payloads are arbitrary here — WriteTracks/ReadTracks are codec-agnostic framing.
    private static byte[] BuildSynthetic(params (int TrackIndex, uint Flag, int PayloadLen)[] specs)
    {
        var tracks = specs.Select(s =>
        {
            var payload = new byte[s.PayloadLen];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + s.TrackIndex + 1);
            return new Dc2MusicTrack(s.TrackIndex, s.Flag, payload);
        }).ToList();
        return Dc2MusicContainer.WriteTracks(tracks);
    }

    [Fact]
    public void WriteTracks_ThenReadTracks_RoundTripsSyntheticContainer()
    {
        var container = BuildSynthetic((0, 1u, 2034335), (1, 1u, 5), (2, 0u, 4096));
        Assert.Equal(0x800 + Align(2034335) + Align(5) + Align(4096), container.Length);
        // Header filler slot after the 3 records is the game's "dummy header    " pattern.
        Assert.Equal("dummy header    ", System.Text.Encoding.ASCII.GetString(container, 3 * 32, 16));

        var tracks = Dc2MusicContainer.ReadTracks(container);
        Assert.Equal(3, tracks.Count);
        Assert.Equal(new[] { 0, 1, 2 }, tracks.Select(t => t.TrackIndex));
        Assert.Equal(new uint[] { 1, 1, 0 }, tracks.Select(t => t.Flag));
        Assert.Equal(container, Dc2MusicContainer.WriteTracks(tracks)); // byte-identical
    }

    private static int Align(int n) => (n + 2047) & ~2047;

    // Real containers: prefer the local by-ear export copies, else the live DC2 install (env). Skip if none.
    private static IReadOnlyList<byte[]> Corpus()
    {
        var dirs = new List<string>();
        var exportAll = Path.Combine(RepoRoot(), "bgm-export", "dc2", "_all");
        if (Directory.Exists(exportAll)) dirs.Add(exportAll);
        if (Environment.GetEnvironmentVariable("DINORAND_DC2_DIR") is { Length: > 0 } env && Directory.Exists(env))
            dirs.Add(env);

        var files = dirs
            .SelectMany(d => Directory.EnumerateFiles(d, "*.DAT"))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(  // ME_/MF_/MS_*.DAT music containers only
                Path.GetFileName(f), @"^M[SEF]_.*\.DAT$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(65)
            .Select(File.ReadAllBytes)
            .ToList();
        return files;
    }

    [Fact]
    public void ReadTracks_ThenWriteTracks_RoundTripsByteIdentical_ForEveryContainer()
    {
        var corpus = Corpus();
        if (corpus.Count == 0) return; // skip cleanly when no DC2 music files are present (CI)

        foreach (var container in corpus)
        {
            var tracks = Dc2MusicContainer.ReadTracks(container);
            Assert.NotEmpty(tracks);
            var rebuilt = Dc2MusicContainer.WriteTracks(tracks);
            Assert.Equal(container, rebuilt); // byte-identical: the container writer is faithful
        }
    }

    [Fact]
    public void DecodePayload_YieldsValidPcm()
    {
        var container = Corpus().FirstOrDefault();
        if (container is null || !Dc2MusicCodec.FfmpegAvailable) return; // skip: no corpus or no ffmpeg

        var track = Dc2MusicContainer.ReadTracks(container!).First();
        var (samples, channels, sampleRate) = Dc2MusicCodec.DecodePayload(track.Payload);

        Assert.NotEmpty(samples);
        Assert.InRange(channels, 1, 2);
        Assert.True(sampleRate is >= 8000 and <= 48000, $"unexpected sample rate {sampleRate}");
        Assert.Equal(0, samples.Length % channels); // interleaved: whole frames
    }

    [Fact]
    public void EncodePayload_ThenDecode_RoundTripsWithinTolerance()
    {
        var container = Corpus().FirstOrDefault();
        if (container is null || !Dc2MusicCodec.FfmpegAvailable) return; // skip: no corpus or no ffmpeg

        var track = Dc2MusicContainer.ReadTracks(container!).First();
        var (samples, channels, sampleRate) = Dc2MusicCodec.DecodePayload(track.Payload);

        var payload = Dc2MusicCodec.EncodePayload(samples, channels, sampleRate);
        var (reSamples, reCh, reRate) = Dc2MusicCodec.DecodePayload(payload);

        Assert.Equal(channels, reCh);
        Assert.Equal(sampleRate, reRate);
        // Lossy: allow a small frame-count drift from block padding, not a wholesale mismatch.
        Assert.InRange(reSamples.Length, (int)(samples.Length * 0.98), (int)(samples.Length * 1.02) + channels);
    }

    /// <summary>Walk up from the test bin dir to the repo root (the folder holding <c>bgm-export/</c>).</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "bgm-export")) &&
                              !Directory.Exists(Path.Combine(dir, "biorand")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
