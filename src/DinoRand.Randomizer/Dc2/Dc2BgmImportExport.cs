using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Bgm;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Voice;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// DC2 music EXPORT (docs/decisions/dc2/audio/DC2-BGM-IMPORT-FEASIBILITY.md): unpacks every
/// <c>Data/M[SEF]_*.DAT</c> container's tracks to playable <c>.mp3</c> files under an <c>--out</c> dir.
/// A container payload IS a standard MPEG-1 L3 stream, so export is a verbatim slice — no decode, no
/// ffmpeg — and the files drop straight into the <c>bgm-export/dc2/&lt;tag&gt;/</c> mood folders that feed
/// <see cref="Dc2BgmImportInstaller"/>. Read-only on the install.
/// </summary>
public static class Dc2BgmExportInstaller
{
    /// <summary>Export all container tracks as <c>&lt;outDir&gt;/&lt;stem&gt;_t&lt;idx&gt;.mp3</c>. Returns the file count
    /// (0 if no DC2 Data dir / no music). Never writes into the install.</summary>
    public static int Export(string installDir, string outDir, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[bgm-export] no DC2 Data folder under {installDir}; skipped");
            return 0;
        }
        Directory.CreateDirectory(outDir);

        int files = 0, banks = 0;
        foreach (string name in Dc2MusicTablePatch.CanonicalNames)
        {
            string path = Path.Combine(dataDir, name);
            if (!File.Exists(path)) continue; // vestigial table entry (no file) — nothing to export
            IReadOnlyList<Dc2MusicTrack> tracks;
            try { tracks = Dc2MusicContainer.ReadTracks(File.ReadAllBytes(path)); }
            catch (InvalidOperationException ex) { log?.Invoke($"[bgm-export] skip {name}: {ex.Message}"); continue; }

            string stem = Path.GetFileNameWithoutExtension(name);
            foreach (var t in tracks)
            {
                // Single-track banks keep the plain stem; multi-track add the slot index so drops are distinct.
                string outName = tracks.Count == 1 ? $"{stem}.mp3" : $"{stem}_t{t.TrackIndex}.mp3";
                File.WriteAllBytes(Path.Combine(outDir, outName), t.Payload);
                files++;
            }
            banks++;
        }
        WriteReadme(outDir);
        log?.Invoke($"[bgm-export] wrote {files} track(s) from {banks} bank(s) to {outDir}");
        return files;
    }

    private static void WriteReadme(string outDir)
    {
        string readme = Path.Combine(outDir, "README.txt");
        if (File.Exists(readme)) return;
        File.WriteAllText(readme,
            "DC2 music export (dinorand --dc2-export-bgm).\n\n" +
            "Each file is a DC2 in-game music track as a plain MP3 (MPEG-1 Layer III, 44.1kHz stereo).\n" +
            "Play them to sort by mood, then drop the ones you want into a donor pack:\n" +
            "    <pack>/data/bgm/<tag>/<anything>.mp3   (tags: safe, calm, creepy, danger, boss, countdown, results, all)\n" +
            "and re-import with:  dinorand --dc2-import-bgm --install <dc2Dir> --bgm-packs <pack> --out <modDir>\n" +
            ".ogg/.wav donors work too (transcoded via ffmpeg on PATH); .mp3 donors are used as-is.\n");
    }
}

/// <summary>
/// DC2 external music IMPORT (docs/decisions/dc2/audio/DC2-BGM-IMPORT-FEASIBILITY.md): the DC2 counterpart
/// to DC1's <c>--bgm-import</c> (<see cref="BgmRandomizer"/>). For each <c>Data/M[SEF]_*.DAT</c> container,
/// draws a same-mood donor per track from a BioRand-layout pack (<see cref="BgmDataPack"/>), turns it into
/// an MP3 payload, and rebuilds the container via <see cref="Dc2MusicContainer.WriteTracks"/> — preserving
/// each track's index+flag so the framing stays byte-faithful. <c>.mp3</c> donors are used verbatim;
/// <c>.ogg/.wav</c> are transcoded to PCM (<see cref="BgmCodec"/>) then MP3-encoded via ffmpeg
/// (<see cref="Dc2MusicCodec"/>). Read-only on the install: rebuilt containers are written to <c>--out</c>.
/// </summary>
public static class Dc2BgmImportInstaller
{
    /// <summary>Rebuild each music container with donor audio, writing the new <c>M[SEF]_*.DAT</c> files
    /// under <paramref name="outDir"/>. Returns the number of containers written.</summary>
    public static int Import(string installDir, string packsRoot, string outDir, int seed, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[bgm-import] no DC2 Data folder under {installDir}; skipped");
            return 0;
        }
        var pool = BgmDataPack.LoadAll(packsRoot, new[] { ".ogg", ".wav", ".mp3" });
        if (pool.Count == 0)
        {
            log?.Invoke($"[bgm-import] no donor tracks under {packsRoot} (expected <pack>/data/bgm/<tag>/*.mp3|ogg|wav); skipped");
            return 0;
        }
        Directory.CreateDirectory(outDir);

        var manifest = BgmManifest.LoadDefault("dc2");
        var drawer = new TagDrawer(BgmDataPack.ByTag(pool), pool, new Random(seed));
        bool warnedNoFfmpeg = false;

        int written = 0;
        foreach (string name in Dc2MusicTablePatch.CanonicalNames)
        {
            string path = Path.Combine(dataDir, name);
            if (!File.Exists(path)) continue;
            IReadOnlyList<Dc2MusicTrack> tracks;
            try { tracks = Dc2MusicContainer.ReadTracks(File.ReadAllBytes(path)); }
            catch (InvalidOperationException ex) { log?.Invoke($"[bgm-import] skip {name}: {ex.Message}"); continue; }

            string tag = manifest.TagOf(name);
            var rebuilt = new List<Dc2MusicTrack>(tracks.Count);
            bool ok = true;
            foreach (var t in tracks)
            {
                if (drawer.Next(tag) is not { } clip) { ok = false; break; }
                byte[]? payload = ToMp3Payload(clip, ref warnedNoFfmpeg, log);
                if (payload is null) { ok = false; break; }
                rebuilt.Add(new Dc2MusicTrack(t.TrackIndex, t.Flag, payload));
            }
            if (!ok) continue;

            File.WriteAllBytes(Path.Combine(outDir, name), Dc2MusicContainer.WriteTracks(rebuilt));
            written++;
        }
        log?.Invoke($"[bgm-import] wrote {written} container(s) to {outDir} (pool: {pool.Count} donor track(s), seed {seed}).");
        if (written > 0)
            log?.Invoke($"[bgm-import] copy the {written} file(s) from {outDir} into the install's Data\\ to apply (back up the originals first).");
        return written;
    }

    /// <summary>Turn a donor clip into an MP3 payload: <c>.mp3</c> verbatim; <c>.ogg/.wav</c> via
    /// PCM transcode + ffmpeg MP3 encode (null when ffmpeg is unavailable for a non-mp3 donor).</summary>
    private static byte[]? ToMp3Payload(BgmClipSource clip, ref bool warnedNoFfmpeg, Action<string>? log)
    {
        using var src = clip.Open();
        if (string.Equals(clip.Extension, ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            return ms.ToArray(); // already a standard MP3 stream — the exact container payload format
        }
        if (!Dc2MusicCodec.FfmpegAvailable)
        {
            if (!warnedNoFfmpeg)
            {
                log?.Invoke("[bgm-import] ffmpeg not found on PATH — .ogg/.wav donors need it to encode MP3; " +
                            "either install ffmpeg or supply .mp3 donors. Skipping non-mp3 donors.");
                warnedNoFfmpeg = true;
            }
            return null;
        }
        var wav = BgmCodec.Transcode(src, clip.Extension, Dc2MusicCodec.GameChannels, Dc2MusicCodec.GameSampleRate);
        var (interleaved, ch, rate) = WavAudio.ReadPcmInterleaved(wav);
        return Dc2MusicCodec.EncodePayload(interleaved, ch, rate);
    }

    /// <summary>Per-tag endless-bag draw with fallback (slot tag → the <c>all</c> pool → the whole pool) —
    /// same policy as <see cref="BgmRandomizer"/>'s drawer, kept local (that one is private).</summary>
    private sealed class TagDrawer
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<BgmClipSource>> _byTag;
        private readonly IReadOnlyList<BgmClipSource> _all;
        private readonly Random _rng;
        private readonly Dictionary<string, Queue<BgmClipSource>> _bags = new(StringComparer.OrdinalIgnoreCase);

        public TagDrawer(IReadOnlyDictionary<string, IReadOnlyList<BgmClipSource>> byTag,
                         IReadOnlyList<BgmClipSource> all, Random rng)
        {
            _byTag = byTag;
            _all = all;
            _rng = rng;
        }

        public BgmClipSource? Next(string tag)
        {
            var pool = _byTag.TryGetValue(tag, out var p) && p.Count > 0 ? p
                     : _byTag.TryGetValue(BgmManifest.DefaultTag, out var d) && d.Count > 0 ? d
                     : _all;
            if (pool.Count == 0) return null;
            if (!_bags.TryGetValue(tag, out var bag) || bag.Count == 0)
            {
                var shuffled = pool.ToArray();
                for (int i = shuffled.Length - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
                }
                _bags[tag] = bag = new Queue<BgmClipSource>(shuffled);
            }
            return bag.Dequeue();
        }
    }
}
