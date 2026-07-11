using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The shop-shuffle install step, file-level (<c>ApplyToFile</c>) on a synthetic canonical exe
/// (<see cref="Dc2ShopTablePatchTests.MakeExe"/>). Pins the outcome branches, seed determinism,
/// and the backup-once contract.
/// </summary>
public class Dc2ShopShuffleInstallerTests
{
    private static string WriteExe(string dir, byte[] bytes)
    {
        var path = Path.Combine(dir, "Dino2.exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void MissingExe_ReturnsNotFound()
    {
        var dir = Directory.CreateTempSubdirectory("shopinst").FullName;
        try
        {
            Assert.Equal(Dc2ShopShuffleOutcome.NotFound,
                Dc2ShopShuffleInstaller.ApplyToFile(Path.Combine(dir, "Dino2.exe"), seed: 1));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UnrecognizedBuild_LeavesFileUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("shopinst").FullName;
        try
        {
            // Right shape but wrong length AND right length but zeroed tables — both refused.
            var wrongLen = WriteExe(dir, new byte[100]);
            Assert.Equal(Dc2ShopShuffleOutcome.UnrecognizedVersion,
                Dc2ShopShuffleInstaller.ApplyToFile(wrongLen, seed: 1));

            var zeroed = new byte[Dc2WpGatePatch.ExpectedLength];
            var path = WriteExe(dir, zeroed);
            Assert.Equal(Dc2ShopShuffleOutcome.UnrecognizedVersion,
                Dc2ShopShuffleInstaller.ApplyToFile(path, seed: 1));
            Assert.Equal(zeroed, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Apply_ShufflesDeterministically_AndRestoreRoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("shopinst").FullName;
        try
        {
            var pristine = Dc2ShopTablePatchTests.MakeExe();
            var path = WriteExe(dir, pristine);

            Assert.Equal(Dc2ShopShuffleOutcome.Applied,
                Dc2ShopShuffleInstaller.ApplyToFile(path, seed: 1234));
            var run1 = File.ReadAllBytes(path);
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));   // pristine captured

            // prices are still a permutation of retail — never invented values
            var prices = Dc2ShopTablePatch.ForSaleIds.Select(id => Dc2ShopTablePatch.ReadPrice(run1, id));
            Assert.Equal(Dc2ShopTablePatch.CanonicalPrices.Order(), prices.Order());

            // same seed on a fresh pristine exe ⇒ identical output (non-compounding, deterministic)
            var path2 = Path.Combine(dir, "Dino2b.exe");
            File.WriteAllBytes(path2, pristine);
            Dc2ShopShuffleInstaller.ApplyToFile(path2, seed: 1234);
            Assert.Equal(run1, File.ReadAllBytes(path2));

            // re-run with another seed over the shuffled exe: .bak stays pristine
            Dc2ShopShuffleInstaller.ApplyToFile(path, seed: 99);
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            // restore ⇒ canonical retail values
            Assert.Equal(Dc2ShopShuffleOutcome.Restored,
                Dc2ShopShuffleInstaller.ApplyToFile(path, seed: 0, restore: true));
            Assert.True(Dc2ShopTablePatch.IsCanonical(File.ReadAllBytes(path)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
