using System.Security.Cryptography;
using DinoRand.App;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Runner-level spoiler tests against the real in-repo installs (docs/SPOILER-LOG-PLAN.md §4):
/// the ZERO-IMPACT regression (same seed with/without the spoiler ⇒ byte-identical outputs) and
/// the spoiler-matches-reality checks (enemy table == donor tally; debug block round-trips via
/// AppSeed.TryParse). No-ops on machines without the game data (the repo-walk pattern of
/// RoomFileRoundTripTests).
/// </summary>
public class SpoilerRunnerTests
{
    [Fact]
    public void Dc1_EmittingTheSpoiler_ChangesNoOutputByte()
    {
        if (Dc1Install() is not { } install) return;
        var seed = new Seed(424242);
        var config = new RandomizerConfig { ShuffleKeyItems = true };

        using var t = new TempDirs();
        new RandomizerRunner(new DinoCrisis1()).Run(install, t.A, seed, config, emitSpoiler: false);
        new RandomizerRunner(new DinoCrisis1()).Run(install, t.B, seed, config, emitSpoiler: true);

        Assert.False(File.Exists(Path.Combine(t.A, SpoilerLogBuilder.FileName)));
        Assert.True(File.Exists(Path.Combine(t.B, SpoilerLogBuilder.FileName)));
        AssertIdenticalExceptSpoiler(t.A, t.B);
    }

    [Fact]
    public void Dc1_Spoiler_HasItemAndKeySections_AndRoundTrippingSeedString()
    {
        if (Dc1Install() is not { } install) return;
        var seed = new Seed(424242);
        var config = new RandomizerConfig { ShuffleKeyItems = true };

        using var t = new TempDirs();
        var result = new RandomizerRunner(new DinoCrisis1()).Run(install, t.A, seed, config);
        var md = File.ReadAllText(Path.Combine(t.A, SpoilerLogBuilder.FileName));

        // Debug block: the DINO- string reproduces exactly this run.
        var seedString = ExtractSeedString(md);
        Assert.True(AppSeed.TryParse(seedString, out var parsed));
        Assert.Equal(seed.Value, parsed.Seed.Value);
        Assert.True(parsed.Config.ShuffleKeyItems);

        Assert.Contains("## Items (DC1)", md);
        Assert.Contains("## Key items (DC1)", md);

        // Key-item rows match the pass's own relocation count.
        int relocated = ParseCount(result.Log, "[keyshuffle] relocated ", " door keys");
        Assert.Equal(relocated, CountTableRows(md, "## Key items (DC1)"));
    }

    [Fact]
    public void Dc1_DisabledPasses_ProduceNoSections()
    {
        if (Dc1Install() is not { } install) return;
        var config = new RandomizerConfig
        {
            RandomizeItems = false, RandomizeEnemies = false,
            ShuffleKeyItems = false, EnsureBeatable = true,
        };
        using var t = new TempDirs();
        new RandomizerRunner(new DinoCrisis1()).Run(install, t.A, new Seed(1), config);
        var md = File.ReadAllText(Path.Combine(t.A, SpoilerLogBuilder.FileName));

        Assert.DoesNotContain("## Items (DC1)", md);
        Assert.DoesNotContain("## Enemies (DC1", md);
        Assert.DoesNotContain("## Key items (DC1)", md);
    }

    [Fact]
    public void Dc2_EmittingTheSpoiler_ChangesNoOutputByte()
    {
        if (Dc2Install() is not { } install) return;
        var seed = new Seed(1001);
        var config = new RandomizerConfig();

        using var t = new TempDirs();
        new Dc2RandomizerRunner(new DinoCrisis2()).Run(install, t.A, seed, config, emitSpoiler: false);
        new Dc2RandomizerRunner(new DinoCrisis2()).Run(install, t.B, seed, config, emitSpoiler: true);

        Assert.False(File.Exists(Path.Combine(t.A, SpoilerLogBuilder.FileName)));
        Assert.True(File.Exists(Path.Combine(t.B, SpoilerLogBuilder.FileName)));
        AssertIdenticalExceptSpoiler(t.A, t.B);
    }

    [Fact]
    public void Dc2_EnemyTable_MatchesTheDonorTallyExactly_WeightedMode()
    {
        if (Dc2Install() is not { } install) return;
        using var t = new TempDirs();
        var result = new Dc2RandomizerRunner(new DinoCrisis2())
            .Run(install, t.A, new Seed(1001), new RandomizerConfig());
        var md = File.ReadAllText(Path.Combine(t.A, SpoilerLogBuilder.FileName));

        Assert.Contains("mode: weighted", md);
        var tally = ParseTally(result.Log);
        Assert.Equal(tally.Values.Sum(), CountTableRows(md, "## Enemies (DC2 cross-species)"));
        foreach (var (species, count) in tally)
            Assert.Equal(count, TableRows(md, "## Enemies (DC2 cross-species)")
                                .Count(r => r[2].Contains(species)));
    }

    [Fact]
    public void Dc2_FixedTRexSeed_EveryChangedRoomShowsTyrannosaurus()
    {
        if (Dc2Install() is not { } install) return;
        var config = new RandomizerConfig
        {
            Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed,
            Dc2FixedSpeciesType = 0x03,
        };
        using var t = new TempDirs();
        new Dc2RandomizerRunner(new DinoCrisis2()).Run(install, t.A, new Seed(2001), config);
        var md = File.ReadAllText(Path.Combine(t.A, SpoilerLogBuilder.FileName));

        Assert.Contains("mode: fixed: Tyrannosaurus", md);
        var rows = TableRows(md, "## Enemies (DC2 cross-species)");
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("Tyrannosaurus", r[2]));
        // Skipped/protected rooms are summarized, not silent (plan §4).
        Assert.Contains("set-piece", md);
        Assert.Contains("aquatic", md);
    }

    // --- helpers -------------------------------------------------------------------------------

    private static string? Dc1Install()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var install = Path.Combine(root, "english");
        return new DinoCrisis1().GetDataDir(install) is not null ? install : null;
    }

    private static string? Dc2Install()
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var install = Path.Combine(root, "4249140_DinoCrisis2");
        return new DinoCrisis2().GetDataDir(install) is not null ? install : null;
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "english")) ||
                File.Exists(Path.Combine(dir.FullName, "DinoRand.sln")))
                return dir.FullName;
        return null;
    }

    private static void AssertIdenticalExceptSpoiler(string dirA, string dirB)
    {
        static Dictionary<string, string> Hashes(string dir) =>
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals(SpoilerLogBuilder.FileName, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    f => Path.GetRelativePath(dir, f).Replace('\\', '/'),
                    f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))),
                    StringComparer.OrdinalIgnoreCase);
        var a = Hashes(dirA);
        var b = Hashes(dirB);
        Assert.Equal(a.Keys.OrderBy(k => k), b.Keys.OrderBy(k => k));
        foreach (var (name, hash) in a)
            Assert.True(hash == b[name], $"output file '{name}' differs when the spoiler is emitted");
    }

    private static string ExtractSeedString(string md)
    {
        var line = md.Split('\n').First(l => l.Contains("DINO-"));
        int start = line.IndexOf("DINO-", StringComparison.Ordinal);
        int end = start;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '-' or '_')) end++;
        return line[start..end];
    }

    /// <summary>Rows of the first GFM table after the given heading (cells trimmed, no header).</summary>
    private static List<string[]> TableRows(string md, string heading)
    {
        var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int i = lines.FindIndex(l => l.StartsWith(heading, StringComparison.Ordinal));
        Assert.True(i >= 0, $"heading '{heading}' not found");
        var rows = new List<string[]>();
        bool inTable = false;
        for (int j = i + 1; j < lines.Count; j++)
        {
            var l = lines[j];
            if (l.StartsWith("## ", StringComparison.Ordinal)) break;
            if (!l.StartsWith("|", StringComparison.Ordinal)) { if (inTable) break; continue; }
            if (!inTable) { inTable = true; continue; }              // header row
            if (l.Contains("---")) continue;                          // separator row
            rows.Add(l.Trim('|').Split('|').Select(c => c.Trim()).ToArray());
        }
        return rows;
    }

    private static int CountTableRows(string md, string heading) => TableRows(md, heading).Count;

    private static int ParseCount(IReadOnlyList<string> log, string prefix, string suffix)
    {
        var line = log.First(l => l.Contains(prefix));
        int s = line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        int e = line.IndexOf(suffix, s, StringComparison.Ordinal);
        return int.Parse(line[s..e]);
    }

    private static Dictionary<string, int> ParseTally(IReadOnlyList<string> log)
    {
        const string marker = "donor tally: ";
        var line = log.First(l => l.Contains(marker));
        return line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]
            .Split('/')
            .Select(p => p.Split('×'))
            .ToDictionary(p => p[0].Trim(), p => int.Parse(p[1]));
    }

    private sealed class TempDirs : IDisposable
    {
        public string A { get; } = MakeTemp();
        public string B { get; } = MakeTemp();
        private static string MakeTemp()
        {
            var d = Path.Combine(Path.GetTempPath(), "dinorand-spoiler-" + Guid.NewGuid().ToString("N"));
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
