using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>The backup-and-swap output sink: backs the vanilla file up exactly once, then overwrites
/// in place (docs/dc2/CROSS-SPECIES-RANDO-PLAN.md).</summary>
public class Dc2BackupSwapSinkTests
{
    [Fact]
    public void EmitTo_BacksUpOriginalThenWrites()
    {
        var dir = Directory.CreateTempSubdirectory("dc2sink").FullName;
        try
        {
            var path = Path.Combine(dir, "ST407.DAT");
            var original = new byte[] { 1, 2, 3, 4 };
            var randomized = new byte[] { 9, 8, 7 };
            File.WriteAllBytes(path, original);

            Dc2BackupSwapSink.EmitTo(path, randomized);

            Assert.Equal(randomized, File.ReadAllBytes(path));                  // in place
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));           // vanilla preserved
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EmitTo_IsIdempotent_ReRunKeepsVanillaBackup()
    {
        var dir = Directory.CreateTempSubdirectory("dc2sink").FullName;
        try
        {
            var path = Path.Combine(dir, "ST407.DAT");
            var original = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(path, original);

            Dc2BackupSwapSink.EmitTo(path, new byte[] { 9, 8, 7 });   // run 1
            Dc2BackupSwapSink.EmitTo(path, new byte[] { 5, 5 });      // run 2 (over a randomized file)

            // the .bak must still be the *vanilla* original, never the run-1 output.
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
            Assert.Equal(new byte[] { 5, 5 }, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
