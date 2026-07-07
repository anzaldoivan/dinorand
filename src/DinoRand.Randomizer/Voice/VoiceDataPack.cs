using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Voice;

/// <summary>
/// Loads a BioRand-layout datapack into a flat donor pool (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §5).
/// Mirrors BioRand's on-disk layout verbatim (D2) so its already-ripped corpora drop in unmodified:
/// <code>
///   &lt;packRoot&gt;/data/voice/&lt;actor&gt;.&lt;game&gt;/&lt;index&gt;_&lt;kind&gt;-&lt;cond&gt;.{ogg,wav}
///   &lt;packRoot&gt;/data/hurt /&lt;actor&gt;.&lt;game&gt;/&lt;n&gt;.{ogg,wav}            (all VoiceKind.Hurt)
/// </code>
/// A pack lives either as a directory tree or as a <c>.zip</c> with the same internal layout
/// (<see cref="IVoicePackSource"/>; plan §12.8), so BioRand's distributed zips drop in without
/// unpacking. A new donor game is a new folder/zip, not new code. Reading donor metadata never decodes
/// audio — the filename is the manifest (<see cref="VoiceFileName"/>) — so the loader is byte-safe and
/// testable.
/// </summary>
public static class VoiceDataPack
{
    private static readonly string[] AudioExtensions = { ".ogg", ".wav" };
    private static readonly string[] Categories = { "voice", "hurt" };

    /// <summary>
    /// Load every pack directly under <paramref name="packsRoot"/> into one combined donor pool
    /// (plan §12.1 — cross-game donors). Each immediate subdirectory is a folder-pack and each
    /// <c>*.zip</c> is a zip-pack (plan §12.8). When a pack exists as both <c>&lt;name&gt;/</c> and
    /// <c>&lt;name&gt;.zip</c> the folder is preferred and the zip skipped (dedupe by base name,
    /// case-insensitive). Returns empty if the root is missing. The cross-game vs DC1-only choice is
    /// applied later by the planner via <see cref="VoiceClipSource.IsNativeDc1"/>, so the loader stays
    /// game-agnostic.
    /// </summary>
    public static IReadOnlyList<VoiceClipSource> LoadAll(string packsRoot)
    {
        var clips = new List<VoiceClipSource>();
        if (string.IsNullOrWhiteSpace(packsRoot) || !Directory.Exists(packsRoot)) return clips;

        foreach (var source in OpenPacks(packsRoot))
            using (source)
                LoadPack(source, clips);
        return clips;
    }

    /// <summary>
    /// Walk one folder pack at <paramref name="packRoot"/> (a directory containing <c>data/voice</c>
    /// and/or <c>data/hurt</c>) and return every donor clip. The actor identity is the open
    /// <c>&lt;actor&gt;</c> folder name (string), so cross-game casts load too (plan §12.1). Returns an
    /// empty list if the pack has no <c>data/</c> dir.
    /// </summary>
    public static IReadOnlyList<VoiceClipSource> Load(string packRoot)
    {
        var clips = new List<VoiceClipSource>();
        using var source = new FolderPackSource(packRoot);
        LoadPack(source, clips);
        return clips;
    }

    /// <summary>
    /// The distinct donor actors discovered across every pack under <paramref name="packsRoot"/> as
    /// <c>(Actor, Game)</c> pairs, WITHOUT decoding any audio — only folder/zip entry <i>names</i> are
    /// read, so it stays fast even on a multi-hundred-MB zip (plan §12.8). Drives the App's
    /// donor-character dropdown. Empty when the root is missing.
    /// </summary>
    public static IReadOnlyList<(string Actor, string Game)> ListActors(string packsRoot)
    {
        var result = new List<(string Actor, string Game)>();
        if (string.IsNullOrWhiteSpace(packsRoot) || !Directory.Exists(packsRoot)) return result;

        var seen = new HashSet<(string, string)>();
        foreach (var source in OpenPacks(packsRoot))
            using (source)
                foreach (var category in Categories)
                    foreach (var folder in source.ListActorFolders(category))
                    {
                        if (!TrySplitActorFolder(folder, out var actor, out var game)) continue;
                        if (seen.Add((actor, game))) result.Add((actor, game));
                    }
        return result;
    }

    /// <summary>
    /// Build the deduped set of pack sources under <paramref name="packsRoot"/>: folder packs first
    /// (each subdir), then any <c>*.zip</c> whose base name was not already claimed by a folder. Prefers
    /// the folder when both forms of a pack are present.
    /// </summary>
    private static IEnumerable<IVoicePackSource> OpenPacks(string packsRoot)
    {
        var sources = new List<IVoicePackSource>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.EnumerateDirectories(packsRoot))
            if (taken.Add(Path.GetFileName(dir)))
                sources.Add(new FolderPackSource(dir));

        foreach (var zip in Directory.EnumerateFiles(packsRoot, "*.zip"))
            if (taken.Add(Path.GetFileNameWithoutExtension(zip)))  // a same-named folder wins ⇒ skip the zip
                sources.Add(new ZipPackSource(zip));

        return sources;
    }

    private static void LoadPack(IVoicePackSource source, List<VoiceClipSource> into)
    {
        LoadCategory(source, "voice", forcedKind: null, into);
        LoadCategory(source, "hurt", forcedKind: VoiceKind.Hurt, into);
    }

    /// <summary>Load every <c>&lt;actor&gt;.&lt;game&gt;</c> folder under one category of a pack source.</summary>
    private static void LoadCategory(
        IVoicePackSource source, string category, VoiceKind? forcedKind, List<VoiceClipSource> into)
    {
        foreach (var folder in source.ListActorFolders(category))
        {
            if (!TrySplitActorFolder(folder, out var actorName, out var game)) continue;

            foreach (var fileName in source.ListClips(category, folder))
            {
                if (!AudioExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant())) continue;
                var parsed = VoiceFileName.Parse(Path.GetFileNameWithoutExtension(fileName));
                var clip = new VoiceClipSource(
                    actorName, game, forcedKind ?? parsed.Kind, parsed.Conditions, parsed.Index,
                    source.ClipPath(category, folder, fileName));

                var opener = source.OpenClip(category, folder, fileName);
                into.Add(opener is null ? clip : clip with { Open = opener });
            }
        }
    }

    /// <summary>Split a <c>&lt;actor&gt;.&lt;game&gt;</c> folder name (actor lower-cased). False if malformed.</summary>
    private static bool TrySplitActorFolder(string folder, out string actor, out string game)
    {
        actor = game = "";
        var dot = folder.LastIndexOf('.');
        if (dot <= 0 || dot >= folder.Length - 1) return false; // need "<actor>.<game>"
        actor = folder[..dot].ToLowerInvariant();               // open donor identity (any game's cast)
        game = folder[(dot + 1)..];
        return true;
    }

    /// <summary>Map a datapack actor-folder name to the DC1 cast enum (case-insensitive).</summary>
    public static bool TryParseActor(string name, out VoiceActor actor) =>
        Enum.TryParse(name, ignoreCase: true, out actor) && actor != VoiceActor.Unknown;
}
