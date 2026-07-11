using DinoRand.Randomizer.Voice;

namespace DinoRand.Randomizer.Bgm;

/// <summary>
/// DC1 external BGM import (docs/decisions/cross/BGM-RANDO-PLAN.md). Ports the useful half of BioRand's
/// <c>BgmRandomiser</c>: for each <c>Sound/BGM/</c> slot, draw a same-tag donor track from a BioRand-layout
/// datapack (<see cref="BgmDataPack"/>), transcode it to the slot's own RIFF format (<see cref="BgmCodec"/>),
/// and register the overwrite as a loose install file — the DC1 catalog binds id → <c>bgm\&lt;name&gt;</c> and
/// is consumed at init, and the slot's <c>*.dat</c> is plain RIFF/WAVE, so replacing the file bytes reroutes
/// the music with <b>no exe patch</b> (BGM-SYSTEM.md §1a/§1b; the catalog <c>size</c> is a cached value, not
/// the on-disk length). Reversed by the <c>GameInstaller</c> loose-file backup contract.
///
/// <para>Distinct from the shipped <c>--shuffle-bgm</c> exe lever, which permutes the game's <i>own</i> tracks
/// and needs no assets; the two compose. A no-op when no <see cref="RandomizerConfig.BgmPacksRoot"/> is
/// configured, the pack holds no music, or no install <c>Sound/BGM/</c> is present.</para>
/// </summary>
public sealed class BgmRandomizer : IRandomizationPass
{
    public string Name => "bgm";

    public bool IsEnabled(RandomizerConfig config) => config.RandomizeBgm;

    public void Apply(RandomizationContext ctx) => Emit(ctx);

    /// <summary>Build and register the imported BGM overwrites. Separated from <see cref="Apply"/> so the
    /// emission is unit-testable. No-op (logs why) when a precondition is missing.</summary>
    public static void Emit(RandomizationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Config.BgmPacksRoot))
        {
            ctx.Log("[bgm] no BgmPacksRoot configured — nothing to import.");
            return;
        }

        var pool = BgmDataPack.LoadAll(ctx.Config.BgmPacksRoot!);
        if (pool.Count == 0)
        {
            ctx.Log($"[bgm] no music tracks under {ctx.Config.BgmPacksRoot} (expected <pack>/data/bgm/<tag>/*.ogg|wav).");
            return;
        }

        var bgmDir = ctx.InstallDir is { } install ? FindBgmDir(install) : null;
        if (bgmDir is null)
        {
            ctx.Log("[bgm] no install Sound/BGM directory found — cannot resolve slot names/formats; skipped.");
            return;
        }

        var manifest = BgmManifest.LoadDefault("dc1");
        var byTag = BgmDataPack.ByTag(pool);
        var drawer = new TagDrawer(byTag, pool, ctx.Seed.RngFor("bgm"));

        int written = 0;
        foreach (var slotPath in Directory.EnumerateFiles(bgmDir, "*.dat")
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(slotPath);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var tag = manifest.TagOf(stem);

            var donor = drawer.Next(tag);
            if (donor is not { } clip) continue;      // pool empty for this tag and every fallback

            // Match the slot's own channels+rate so the replacement is format-compatible; default to a
            // safe stereo 44.1k if the original header can't be read (defensive — slots are RIFF/WAVE).
            var fmt = Dc1VoiceCatalog.TryReadFormat(slotPath);
            int channels = fmt?.Channels ?? 2;
            int rate = fmt?.SampleRate ?? 44100;

            byte[] bytes;
            using (var src = clip.Open())
                bytes = BgmCodec.Transcode(src, clip.Extension, channels, rate);

            ctx.AddLooseFile($"Sound/BGM/{fileName}", bytes);
            written++;
        }

        ctx.Log($"[bgm] imported {written} BGM slot(s) from {ctx.Config.BgmPacksRoot} (pool: {pool.Count} track(s)).");
    }

    /// <summary>
    /// Locate the <c>Sound/BGM</c> directory under a DC1 install (case-insensitively), or <c>null</c>. Accepts
    /// the install root, a <c>Sound</c> dir, or a path already at <c>BGM</c>. Mirrors
    /// <see cref="Dc1VoiceCatalog.FindVoiceDir"/> (the sibling <c>Sound/VOICE</c> lookup).
    /// </summary>
    public static string? FindBgmDir(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return null;

        foreach (var candidate in new[]
                 {
                     installDir,
                     Path.Combine(installDir, "BGM"),
                     Path.Combine(installDir, "Sound", "BGM"),
                 })
            if (Directory.Exists(candidate) &&
                string.Equals(Path.GetFileName(candidate), "BGM", StringComparison.OrdinalIgnoreCase))
                return candidate;

        var sound = Directory.EnumerateDirectories(installDir)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Sound", StringComparison.OrdinalIgnoreCase));
        if (sound == null) return null;
        return Directory.EnumerateDirectories(sound)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "BGM", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Per-tag endless-bag draw with fallback (slot tag → the <c>all</c> pool → the whole pool),
    /// so a slot always gets some donor while a track never repeats until its bag is exhausted.</summary>
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
            var pool = ResolvePool(tag);
            if (pool.Count == 0) return null;

            if (!_bags.TryGetValue(tag, out var bag) || bag.Count == 0)
                _bags[tag] = bag = Refill(pool);
            return bag.Dequeue();
        }

        private IReadOnlyList<BgmClipSource> ResolvePool(string tag)
        {
            if (_byTag.TryGetValue(tag, out var p) && p.Count > 0) return p;
            if (_byTag.TryGetValue(BgmManifest.DefaultTag, out var d) && d.Count > 0) return d;
            return _all;
        }

        private Queue<BgmClipSource> Refill(IReadOnlyList<BgmClipSource> pool)
        {
            var shuffled = pool.ToArray();
            for (int i = shuffled.Length - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            return new Queue<BgmClipSource>(shuffled);
        }
    }
}
