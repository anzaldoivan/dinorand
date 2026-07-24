using System.Security.Cryptography;
using System.Text;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests.Refactor;

public sealed class RunnerArtifactCharacterizationTests
{
    [Fact]
    public void DC1_runner_artifact_names_and_canonical_contents_replay_exactly()
    {
        string root = Directory.CreateTempSubdirectory("dinorand-w1-art-dc1-").FullName;
        try
        {
            string install = SyntheticInputs.CreateDc1Install(root);
            string a = Path.Combine(root, "a");
            string b = Path.Combine(root, "b");
            var config = new RandomizerConfig();
            var runner = new RandomizerRunner(new DinoCrisis1());
            runner.Run(install, a, new Seed(12345), config);
            runner.Run(install, b, new Seed(12345), config);
            var spoilerFileName = SpoilerLogBuilder.FileNameFor(
                SeedString.Encode(new Seed(12345), config));

            var expected = Directory.EnumerateFiles(Path.Combine(install, "Data"), "*.dat")
                .Select(Path.GetFileName).Concat(new[] { "log_dinorand.txt", "map.dgml", spoilerFileName })
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, Names(a));
            Assert.Equal(Snapshot(a, root), Snapshot(b, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DC2_runner_nonempty_binary_inventory_and_spoiler_replay_exactly()
    {
        string root = Directory.CreateTempSubdirectory("dinorand-w1-art-dc2-").FullName;
        try
        {
            var config = new RandomizerConfig();
            string install = SyntheticInputs.CreateDc2Install(root, config);
            string a = Path.Combine(root, "a");
            string b = Path.Combine(root, "b");
            var runner = new Dc2RandomizerRunner(new DinoCrisis2());
            var first = runner.Run(install, a, new Seed(1001), config);
            var second = runner.Run(install, b, new Seed(1001), config);
            Assert.Equal(5, first.RoomCount);
            Assert.True(first.RoomsWritten > 0);
            Assert.Contains("ST104.DAT", first.WrittenFiles, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Names(a), name => name.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(first.WrittenFiles.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Append(SpoilerLogBuilder.FileNameFor(SeedString.Encode(new Seed(1001), config)))
                    .OrderBy(x => x, StringComparer.Ordinal),
                Names(a));
            Assert.Equal(Snapshot(a, root), Snapshot(b, root));
            Assert.Equal(first.RoomCount, second.RoomCount);
            Assert.Equal(first.RoomsWritten, second.RoomsWritten);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string[] Names(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static string[] Snapshot(string dir, string tempRoot) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .OrderBy(p => Path.GetRelativePath(dir, p), StringComparer.Ordinal).Select(path =>
        {
            string relative = Path.GetRelativePath(dir, path).Replace('\\', '/');
            bool text = Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".dgml";
            byte[] bytes = text ? Encoding.UTF8.GetBytes(SeedDifferentialTests.CanonicalText(File.ReadAllText(path), dir)) : File.ReadAllBytes(path);
            return $"{relative}|{bytes.Length}|{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
        }).ToArray();
}
