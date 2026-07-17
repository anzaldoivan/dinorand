using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The puzzle-code install step, file-level (<c>ApplyToFile</c>) on a synthetic canonical exe
/// (<see cref="Dc2ElevatorCodePatchTests.MakeExe"/>). Pins the outcome branches, seed determinism,
/// and the backup-once contract — the mirror of <see cref="Dc2ShopShuffleInstallerTests"/>.
/// </summary>
public class Dc2PuzzleCodeScrambleInstallerTests
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
        var dir = Directory.CreateTempSubdirectory("pzcinst").FullName;
        try
        {
            Assert.Equal(Dc2PuzzleCodeOutcome.NotFound,
                Dc2PuzzleCodeScrambleInstaller.ApplyToFile(Path.Combine(dir, "Dino2.exe"), seed: 1));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UnrecognizedBuild_LeavesFileUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("pzcinst").FullName;
        try
        {
            var wrongLen = WriteExe(dir, new byte[100]);
            Assert.Equal(Dc2PuzzleCodeOutcome.UnrecognizedVersion,
                Dc2PuzzleCodeScrambleInstaller.ApplyToFile(wrongLen, seed: 1));

            var zeroed = new byte[Dc2WpGatePatch.ExpectedLength]; // right length, no c7 44 24 pins
            var path = WriteExe(dir, zeroed);
            Assert.Equal(Dc2PuzzleCodeOutcome.UnrecognizedVersion,
                Dc2PuzzleCodeScrambleInstaller.ApplyToFile(path, seed: 1));
            Assert.Equal(zeroed, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Apply_ScramblesDeterministically_AndRestoreRoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory("pzcinst").FullName;
        try
        {
            var pristine = Dc2ElevatorCodePatchTests.MakeExe();
            var path = WriteExe(dir, pristine);

            Assert.Equal(Dc2PuzzleCodeOutcome.Applied,
                Dc2PuzzleCodeScrambleInstaller.ApplyToFile(path, seed: 1234));
            var run1 = File.ReadAllBytes(path);
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));   // pristine captured

            // same seed on a fresh pristine exe ⇒ identical output (non-compounding, deterministic)
            var path2 = Path.Combine(dir, "Dino2b.exe");
            File.WriteAllBytes(path2, pristine);
            Dc2PuzzleCodeScrambleInstaller.ApplyToFile(path2, seed: 1234);
            Assert.Equal(run1, File.ReadAllBytes(path2));

            // re-run with another seed over the scrambled exe: .bak stays pristine
            Dc2PuzzleCodeScrambleInstaller.ApplyToFile(path, seed: 99);
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            // restore ⇒ vanilla candidates back, byte-identical to pristine
            Assert.Equal(Dc2PuzzleCodeOutcome.Restored,
                Dc2PuzzleCodeScrambleInstaller.ApplyToFile(path, seed: 0, restore: true));
            Assert.Equal(pristine, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Install-gated round trip on a COPY of the real exe (apply → assert scrambled bytes →
    /// restore → assert pristine). Never touches the install itself.</summary>
    [Fact]
    public void RealExeCopy_ApplyRestore_RoundTripsByteIdentical()
    {
        var dataDir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dataDir)) return;
        var realExe = Path.Combine(dataDir, "..", "Dino2.exe");
        if (!File.Exists(realExe)) return;
        var pristine = File.ReadAllBytes(realExe);
        try { Dc2ElevatorCodePatch.Validate(pristine); } catch (InvalidOperationException) { return; }
        if (!Dc2ElevatorCodePatch.IsCanonical(pristine)) return; // install already scrambled → skip

        var dir = Directory.CreateTempSubdirectory("pzcreal").FullName;
        try
        {
            var path = WriteExe(dir, pristine);
            Assert.Equal(Dc2PuzzleCodeOutcome.Applied,
                Dc2PuzzleCodeScrambleInstaller.ApplyToFile(path, seed: 4242));
            var scrambled = File.ReadAllBytes(path);
            Assert.False(Dc2ElevatorCodePatch.IsCanonical(scrambled));
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            Assert.Equal(Dc2PuzzleCodeOutcome.Restored,
                Dc2PuzzleCodeScrambleInstaller.ApplyToFile(path, seed: 0, restore: true));
            Assert.Equal(pristine, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
