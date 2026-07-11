using DinoRand.Randomizer.Voice;

namespace DinoRand.Randomizer.Bgm;

/// <summary>One donor music track discovered in a datapack: its mood tag (the folder it sat in), a
/// readable path, its file extension, and an on-demand stream opener (so a zip entry transcodes
/// without touching disk). The filename itself carries no metadata — the folder is the manifest.</summary>
/// <param name="Tag">The mood tag = the <c>data/bgm/&lt;tag&gt;/</c> folder name (lower-cased).</param>
public readonly record struct BgmClipSource(string Tag, string Path, string Extension, Func<Stream> Open);

/// <summary>
/// Loads a BioRand-layout datapack's music into a tag-keyed donor pool
/// (docs/decisions/cross/BGM-RANDO-PLAN.md). Layout — flattened from BioRand's <c>&lt;album&gt;/&lt;tag&gt;</c>
/// to just the tag for v1 (album selection is a future data change):
/// <code>
///   &lt;packRoot&gt;/data/bgm/&lt;tag&gt;/&lt;anything&gt;.{ogg,wav}
/// </code>
/// A pack is a directory tree or a <c>.zip</c> with the same internal layout — the exact
/// <see cref="IVoicePackSource"/> plumbing the voice loader uses, reused verbatim (folder = tag here,
/// where voice reads folder = actor). Reading donor metadata never decodes audio; only entry names are
/// read. The tag folder maps 1:1 onto <see cref="BgmManifest"/> tags, with <see cref="BgmManifest.DefaultTag"/>
/// (<c>all</c>) as the catch-all folder.
/// </summary>
public static class BgmDataPack
{
    private const string Category = "bgm";

    /// <summary>Donor extensions DC1 import reads (its <see cref="BgmCodec"/> decodes ogg/wav). DC2 import
    /// passes a superset including <c>.mp3</c> (its container payload format) to <see cref="LoadAll"/>.</summary>
    public static readonly string[] DefaultExtensions = { ".ogg", ".wav" };

    /// <summary>
    /// Load every pack directly under <paramref name="packsRoot"/> into one combined tag→tracks pool.
    /// Each immediate subdirectory is a folder-pack and each <c>*.zip</c> a zip-pack; when a pack exists
    /// as both, the folder wins (dedupe by base name). Returns an empty pool if the root is missing or
    /// holds no music.
    /// </summary>
    public static IReadOnlyList<BgmClipSource> LoadAll(string packsRoot, IReadOnlyCollection<string>? extensions = null)
    {
        var clips = new List<BgmClipSource>();
        if (string.IsNullOrWhiteSpace(packsRoot) || !Directory.Exists(packsRoot)) return clips;

        var exts = extensions ?? DefaultExtensions;
        foreach (var source in OpenPacks(packsRoot))
            using (source)
                LoadPack(source, clips, exts);
        return clips;
    }

    /// <summary>Group a loaded pool by tag (case-insensitive), for a per-tag draw.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<BgmClipSource>> ByTag(IReadOnlyList<BgmClipSource> clips)
    {
        var map = new Dictionary<string, List<BgmClipSource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in clips)
        {
            if (!map.TryGetValue(c.Tag, out var list)) map[c.Tag] = list = new List<BgmClipSource>();
            list.Add(c);
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<BgmClipSource>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    // Deduped folder-then-zip pack sources under packsRoot (folder wins over a same-named zip).
    // Same policy as VoiceDataPack.OpenPacks — kept local to avoid widening the voice loader's surface.
    private static IEnumerable<IVoicePackSource> OpenPacks(string packsRoot)
    {
        var sources = new List<IVoicePackSource>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.EnumerateDirectories(packsRoot))
            if (taken.Add(System.IO.Path.GetFileName(dir)))
                sources.Add(new FolderPackSource(dir));

        foreach (var zip in Directory.EnumerateFiles(packsRoot, "*.zip"))
            if (taken.Add(System.IO.Path.GetFileNameWithoutExtension(zip)))
                sources.Add(new ZipPackSource(zip));

        return sources;
    }

    private static void LoadPack(IVoicePackSource source, List<BgmClipSource> into, IReadOnlyCollection<string> exts)
    {
        foreach (var tagFolder in source.ListActorFolders(Category))          // "actor folder" == tag folder
        {
            var tag = tagFolder.ToLowerInvariant();
            foreach (var fileName in source.ListClips(Category, tagFolder))
            {
                var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
                if (!exts.Contains(ext)) continue;

                var path = source.ClipPath(Category, tagFolder, fileName);
                var opener = source.OpenClip(Category, tagFolder, fileName) ?? (() => File.OpenRead(path));
                into.Add(new BgmClipSource(tag, path, ext, opener));
            }
        }
    }
}
