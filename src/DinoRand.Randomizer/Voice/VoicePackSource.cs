using System.IO.Compression;

namespace DinoRand.Randomizer.Voice;

/// <summary>
/// A read-only donor-pack source, mirroring BioRand's <c>DataManager.IArea</c> (ref/classic
/// <c>DataManager.cs</c>): list the <c>&lt;actor&gt;.&lt;game&gt;</c> folders under a category
/// (<c>voice</c>/<c>hurt</c>), list the clip file names in one of those folders, and resolve a clip's
/// readable identifier + on-demand stream opener. Two implementations (<see cref="FolderPackSource"/>,
/// <see cref="ZipPackSource"/>) let a pack live as a directory tree or a <c>.zip</c> interchangeably
/// (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.8). Zip entries are streamed on demand — never extracted to disk,
/// never read whole into memory.
/// </summary>
internal interface IVoicePackSource : IDisposable
{
    /// <summary>The <c>&lt;actor&gt;.&lt;game&gt;</c> folder names under <c>data/&lt;category&gt;/</c>.</summary>
    IReadOnlyList<string> ListActorFolders(string category);

    /// <summary>The clip file names (with extension) directly under <c>data/&lt;category&gt;/&lt;actorFolder&gt;/</c>.</summary>
    IReadOnlyList<string> ListClips(string category, string actorFolder);

    /// <summary>A human-readable identifier for the clip (e.g. an absolute path, or
    /// <c>dinocrisis.zip!data/voice/regina.dc1/regina001.ogg</c> for a zip entry).</summary>
    string ClipPath(string category, string actorFolder, string fileName);

    /// <summary>An on-demand stream opener for the clip, or <c>null</c> to use the record's default
    /// (<c>File.OpenRead(Path)</c>) — folder packs return <c>null</c> since their <see cref="ClipPath"/>
    /// is already an openable path.</summary>
    Func<Stream>? OpenClip(string category, string actorFolder, string fileName);
}

/// <summary>A donor pack laid out as a directory tree (<c>&lt;packRoot&gt;/data/voice|hurt/…</c>).</summary>
internal sealed class FolderPackSource(string packRoot) : IVoicePackSource
{
    public void Dispose() { }

    private string CategoryDir(string category) => Path.Combine(packRoot, "data", category);

    public IReadOnlyList<string> ListActorFolders(string category)
    {
        var dir = CategoryDir(category);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(dir).Select(Path.GetFileName).ToList()!;
    }

    public IReadOnlyList<string> ListClips(string category, string actorFolder)
    {
        var dir = Path.Combine(CategoryDir(category), actorFolder);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir).Select(Path.GetFileName).ToList()!;
    }

    public string ClipPath(string category, string actorFolder, string fileName) =>
        Path.Combine(CategoryDir(category), actorFolder, fileName);

    // The path IS openable, so let VoiceClipSource.Open default to File.OpenRead(Path).
    public Func<Stream>? OpenClip(string category, string actorFolder, string fileName) => null;
}

/// <summary>
/// A donor pack packaged as a <c>.zip</c> whose root is the pack root (<c>data/voice|hurt/…</c>).
/// Entry names are read from the archive's central directory only (fast even on a multi-hundred-MB zip);
/// a clip is materialised lazily, one entry at a time, into a seekable in-memory stream when actually
/// decoded — the whole archive is never read into memory and nothing is written to disk.
/// </summary>
internal sealed class ZipPackSource : IVoicePackSource
{
    private readonly string _zipPath;
    private readonly ZipArchive _zip;

    public ZipPackSource(string zipPath)
    {
        _zipPath = zipPath;
        _zip = ZipFile.OpenRead(zipPath);
    }

    public void Dispose() => _zip.Dispose();

    private static string Prefix(string category) => $"data/{category}/";

    public IReadOnlyList<string> ListActorFolders(string category)
    {
        var prefix = Prefix(category);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var entry in _zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = name[prefix.Length..];
            int slash = rest.IndexOf('/');
            if (slash <= 0) continue;                 // need "<actorFolder>/<file>"
            var folder = rest[..slash];
            if (seen.Add(folder)) result.Add(folder);
        }
        return result;
    }

    public IReadOnlyList<string> ListClips(string category, string actorFolder)
    {
        var prefix = Prefix(category) + actorFolder + "/";
        var result = new List<string>();
        foreach (var entry in _zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = name[prefix.Length..];
            if (rest.Length == 0 || rest.Contains('/')) continue; // a file directly in the folder
            result.Add(rest);
        }
        return result;
    }

    public string ClipPath(string category, string actorFolder, string fileName) =>
        $"{Path.GetFileName(_zipPath)}!{Prefix(category)}{actorFolder}/{fileName}";

    public Func<Stream>? OpenClip(string category, string actorFolder, string fileName)
    {
        var zipPath = _zipPath;
        var entryName = $"{Prefix(category)}{actorFolder}/{fileName}";
        return () => OpenZipEntry(zipPath, entryName);
    }

    /// <summary>
    /// Re-open the archive (reads only the central directory) and copy the single requested entry into a
    /// seekable <see cref="MemoryStream"/>. Decoupled from this source's lifetime, so the returned
    /// <see cref="VoiceClipSource"/> opener stays valid after the pack source is disposed.
    /// </summary>
    private static Stream OpenZipEntry(string zipPath, string entryName)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(entryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Zip entry not found: {entryName} in {zipPath}");

        var ms = new MemoryStream();
        using (var s = entry.Open())
            s.CopyTo(ms);
        ms.Position = 0;
        return ms;
    }
}
