using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The BGM-shuffle install step, file-level (<c>ApplyToFiles</c>) on the synthetic music-table exe
/// (<see cref="Dc2MusicTablePatchTests.MakeExe"/>) plus a temp Data dir of synthetic MS_ containers.
/// Pins: the outcome branches, the like-class grouping sourced from on-disk containers (slots whose
/// file is missing or unparseable must never move), and backup-once.
/// </summary>
public class Dc2BgmShuffleInstallerTests
{
    private static string WriteExe(string dir, byte[] bytes)
    {
        var path = Path.Combine(dir, "Dino2.exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Write a parseable music container for <paramref name="name"/> whose track-index set
    /// is <paramref name="trackIndexes"/> — the like-class key.</summary>
    private static void WriteContainer(string dataDir, string name, params int[] trackIndexes)
    {
        var tracks = trackIndexes.Select(i => new Dc2MusicTrack(i, 0u, new byte[10])).ToList();
        File.WriteAllBytes(Path.Combine(dataDir, name), Dc2MusicContainer.WriteTracks(tracks));
    }

    private static string[] SliceNames(byte[] exe)
        => Enumerable.Range(Dc2MusicTablePatch.MusicFirstSlot, Dc2MusicTablePatch.MusicSlotCount)
                     .Select(s => Dc2MusicTablePatch.ReadName(exe, s)!).ToArray();

    [Fact]
    public void MissingExe_ReturnsNotFound()
    {
        var dir = Directory.CreateTempSubdirectory("bgminst").FullName;
        try
        {
            Assert.Equal(Dc2BgmShuffleOutcome.NotFound,
                Dc2BgmShuffleInstaller.ApplyToFiles(Path.Combine(dir, "Dino2.exe"), dir, seed: 1));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UnrecognizedBuild_LeavesFileUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("bgminst").FullName;
        try
        {
            var zeroed = new byte[Dc2WpGatePatch.ExpectedLength]; // right length, no name table
            var path = WriteExe(dir, zeroed);
            Assert.Equal(Dc2BgmShuffleOutcome.UnrecognizedVersion,
                Dc2BgmShuffleInstaller.ApplyToFiles(path, dir, seed: 1));
            Assert.Equal(zeroed, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NoContainersOnDisk_NothingMoves()
    {
        // Every slot's file is absent ⇒ no like-class members ⇒ Applied but byte-order unchanged.
        var dir = Directory.CreateTempSubdirectory("bgminst").FullName;
        try
        {
            var pristine = Dc2MusicTablePatchTests.MakeExe();
            var path = WriteExe(dir, pristine);
            var dataDir = Directory.CreateDirectory(Path.Combine(dir, "Data")).FullName;

            Assert.Equal(Dc2BgmShuffleOutcome.Applied,
                Dc2BgmShuffleInstaller.ApplyToFiles(path, dataDir, seed: 1234));
            Assert.Equal(Dc2MusicTablePatch.CanonicalNames, SliceNames(File.ReadAllBytes(path)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Shuffle_MovesOnlyWithinLikeClass_AndRestoreRoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("bgminst").FullName;
        try
        {
            var pristine = Dc2MusicTablePatchTests.MakeExe();
            var path = WriteExe(dir, pristine);
            var dataDir = Directory.CreateDirectory(Path.Combine(dir, "Data")).FullName;

            // one 3-member class {0,1,2}; one lone class {5}; MS_0101 stays absent (never moves)
            WriteContainer(dataDir, "MS_0001.DAT", 0, 1, 2);
            WriteContainer(dataDir, "MS_0002.DAT", 0, 1, 2);
            WriteContainer(dataDir, "MS_0003.DAT", 0, 1, 2);
            WriteContainer(dataDir, "ME_0300.DAT", 5);

            Assert.Equal(Dc2BgmShuffleOutcome.Applied,
                Dc2BgmShuffleInstaller.ApplyToFiles(path, dataDir, seed: 7));
            var names = SliceNames(File.ReadAllBytes(path));
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            var canonical = Dc2MusicTablePatch.CanonicalNames;
            var classNames = new[] { "MS_0001.DAT", "MS_0002.DAT", "MS_0003.DAT" };
            for (int k = 0; k < names.Length; k++)
            {
                if (classNames.Contains(canonical[k], StringComparer.OrdinalIgnoreCase))
                    Assert.Contains(names[k], classNames);   // moved only within its class
                else
                    Assert.Equal(canonical[k], names[k]);    // unclassed/missing slots never move
            }
            // the slice is still a permutation of the canonical set
            Assert.Equal(canonical.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));

            Assert.Equal(Dc2BgmShuffleOutcome.Restored,
                Dc2BgmShuffleInstaller.ApplyToFiles(path, dataDir, seed: 0, restore: true));
            Assert.Equal(canonical, SliceNames(File.ReadAllBytes(path)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
