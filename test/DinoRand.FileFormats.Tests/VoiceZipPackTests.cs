using System.Linq;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Voice;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Phase 4 zip-datapack support (docs/dc1/VOICE-RANDO-PLAN.md §12.8). A donor pack may be a directory
/// tree or a <c>.zip</c> with the same internal layout; <see cref="VoiceDataPack.LoadAll"/> auto-detects
/// both, prefers the folder when a pack exists in both forms, and streams zip clips on demand so they
/// transcode without ever being extracted to disk.
/// </summary>
public class VoiceZipPackTests
{
    private static string DinocrisisZip => Path.Combine(RepoRoot(), "biorand", "datapacks", "dinocrisis.zip");
    private static string DinocrisisFolder => Path.Combine(RepoRoot(), "biorand", "datapacks", "dinocrisis");

    // (a) Zip-only load: a packs-root holding nothing but a copy of dinocrisis.zip yields the Regina corpus.
    [Fact]
    public void LoadAll_ZipOnly_YieldsReginaVoiceCorpus()
    {
        if (!File.Exists(DinocrisisZip)) return; // read-only fixture; skip if absent

        using var temp = new TempDir();
        File.Copy(DinocrisisZip, Path.Combine(temp.Path, "dinocrisis.zip"));

        var clips = VoiceDataPack.LoadAll(temp.Path);

        // 207 Regina cutscene voice clips (the data/voice/regina.dc1 set; hurt clips are a separate kind).
        var voice = clips.Where(c => c.Actor == "regina" && c.Kind != VoiceKind.Hurt).ToList();
        Assert.Equal(207, voice.Count);
        Assert.All(voice, c => Assert.Equal("dc1", c.Game));
        // Zip clips carry the "<zip>!<entry>" identifier and are NOT openable file paths.
        Assert.All(voice, c => Assert.Contains("dinocrisis.zip!data/voice/regina.dc1/", c.Path));
    }

    // LoadAll yields the SAME Regina corpus from the zip as from the folder.
    [Fact]
    public void LoadAll_Zip_MatchesFolderCorpus()
    {
        if (!File.Exists(DinocrisisZip) || !Directory.Exists(DinocrisisFolder)) return;

        using var temp = new TempDir();
        File.Copy(DinocrisisZip, Path.Combine(temp.Path, "dinocrisis.zip"));

        var fromFolder = VoiceDataPack.Load(DinocrisisFolder).Select(Identity).OrderBy(x => x).ToList();
        var fromZip = VoiceDataPack.LoadAll(temp.Path).Select(Identity).OrderBy(x => x).ToList();

        Assert.Equal(fromFolder, fromZip);
    }

    // (b) Folder + zip present under the same root → the folder wins, with no duplicate clips.
    [Fact]
    public void LoadAll_FolderAndZip_PrefersFolder_NoDuplicates()
    {
        if (!File.Exists(DinocrisisZip) || !Directory.Exists(DinocrisisFolder)) return;

        using var temp = new TempDir();
        File.Copy(DinocrisisZip, Path.Combine(temp.Path, "dinocrisis.zip"));
        CopyDir(DinocrisisFolder, Path.Combine(temp.Path, "dinocrisis"));

        var folderOnly = VoiceDataPack.Load(DinocrisisFolder);
        var both = VoiceDataPack.LoadAll(temp.Path);

        // No doubling: the zip was deduped away (same base name "dinocrisis").
        Assert.Equal(folderOnly.Count, both.Count);
        // Every clip came from the folder (a real file path), never the zip identifier.
        Assert.All(both, c => Assert.DoesNotContain(".zip!", c.Path));
    }

    // (c) A Regina .ogg streamed straight out of the zip transcodes to a valid DC1 RIFF.
    [Fact]
    public void ZipStreamedOgg_Transcodes_ToDc1Riff()
    {
        if (!File.Exists(DinocrisisZip)) return;

        using var temp = new TempDir();
        File.Copy(DinocrisisZip, Path.Combine(temp.Path, "dinocrisis.zip"));

        var clip = VoiceDataPack.LoadAll(temp.Path)
            .First(c => c.Path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) && c.Path.Contains(".zip!"));

        var codec = new PcWavCodec();
        byte[] dc1;
        using (var stream = clip.Open())
            dc1 = codec.EncodeForTarget(codec.DecodeToWav(stream, Path.GetExtension(clip.Path)));

        Assert.Equal((byte)'R', dc1[0]);
        Assert.Equal((byte)'I', dc1[1]);
        Assert.Equal((byte)'F', dc1[2]);
        Assert.Equal((byte)'F', dc1[3]);
        Assert.Equal(1, BitConverter.ToUInt16(dc1, 22));                  // mono
        Assert.Equal(22050, BitConverter.ToInt32(dc1, 24));              // 22050 Hz
        Assert.Equal(16, BitConverter.ToUInt16(dc1, 34));                // 16-bit
    }

    // --- helpers ------------------------------------------------------------------------------

    private static string Identity(VoiceClipSource c) =>
        $"{c.Actor}|{c.Game}|{c.Kind}|{c.Index}|{string.Join(",", c.Conditions)}";

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dst));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(src, dst));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dinorand_ziptest_" + Guid.NewGuid());

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(System.IO.Path.Combine(dir, "biorand")))
            dir = System.IO.Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
