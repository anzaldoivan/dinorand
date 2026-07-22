using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The plate-key re-key install step, directory-level (<c>ApplyToDataDir</c>) on a synthetic ST205
/// package (<see cref="Dc2PlateKeyPatchTests.MakePackage"/>). Pins the outcome branches, seed
/// determinism, the backup-and-swap contract (<c>ST205.DAT.bak</c>), and the restore round-trip
/// (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 4, K118).
/// </summary>
public class Dc2PlateKeyInstallerTests
{
    private static string MakeDataDir(out byte[] pristine)
    {
        var dir = Directory.CreateTempSubdirectory("plateinst").FullName;
        pristine = Dc2PlateKeyPatchTests.MakePackage();
        File.WriteAllBytes(Path.Combine(dir, "ST205.DAT"), pristine);
        return dir;
    }

    [Fact]
    public void MissingRoom_ReturnsNotFound_WritesNothing()
    {
        var dir = Directory.CreateTempSubdirectory("plateinst").FullName;
        try
        {
            Assert.Equal(Dc2PlateKeyOutcome.NotFound, Dc2PlateKeyInstaller.ApplyToDataDir(dir, seed: 1));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Apply_ReKeysDeterministically_AndRestoreRoundTrips()
    {
        var dir = MakeDataDir(out var pristine);
        try
        {
            var path = Path.Combine(dir, "ST205.DAT");

            Assert.Equal(Dc2PlateKeyOutcome.Applied, Dc2PlateKeyInstaller.ApplyToDataDir(dir, seed: 4242));
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak")); // pristine captured
            var run1 = File.ReadAllBytes(path);

            // same seed reproduces byte-for-byte, computed from the pristine .bak (non-compounding)
            Dc2PlateKeyInstaller.ApplyToDataDir(dir, seed: 4242);
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));
            Assert.Equal(run1, File.ReadAllBytes(path));

            // restore ⇒ byte-identical room, backup removed
            Assert.Equal(Dc2PlateKeyOutcome.Restored, Dc2PlateKeyInstaller.ApplyToDataDir(dir, seed: 0, restore: true));
            Assert.Equal(pristine, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TamperedRoom_ReturnsUnrecognizedContent_WritesNoBackup()
    {
        var dir = Directory.CreateTempSubdirectory("plateinst").FullName;
        try
        {
            // a package whose script blob is not the recognized vanilla ST205 signature
            var blob = Dc2PlateKeyPatchTests.MakeBlob();
            var rec = Dc2PlateKeyPatch.LocateRecords(blob).First(r => r.RoutineIndex == Dc2PlateKeyPatch.Terminal2AcceptRoutine);
            blob[rec.RoutineIdxOffset] = 99;
            var entries = new List<(GianEntryType, byte[])> { (GianEntryType.Lzss0, Compression.Lzss.Compress(blob)) };
            for (int i = 1; i < Dc2PlateKeyPatch.PaletteEntryIndex; i++) entries.Add((GianEntryType.Data, new byte[] { 1, 2, 3, 4 }));
            entries.Add((GianEntryType.Palette, Dc2PlateKeyPatchTests.MakePalette()));
            var tampered = SyntheticRoom.Package(GianPackage.Dc2EntrySize, entries.ToArray());
            File.WriteAllBytes(Path.Combine(dir, "ST205.DAT"), tampered);

            Assert.Equal(Dc2PlateKeyOutcome.UnrecognizedContent, Dc2PlateKeyInstaller.ApplyToDataDir(dir, seed: 1));
            Assert.Equal(tampered, File.ReadAllBytes(Path.Combine(dir, "ST205.DAT")));
            Assert.Empty(Directory.GetFiles(dir, "*.bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Install-gated round trip on a COPY of the real ST205.DAT (apply → assert bytes →
    /// restore → assert pristine). Never touches the install itself.</summary>
    [Fact]
    public void RealRoomCopy_ApplyRestore_RoundTripByteIdentical()
    {
        var dataDir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir)) return;
        var srcRoom = Path.Combine(dataDir, "ST205.DAT");
        var bak = srcRoom + ".bak";
        var srcPath = File.Exists(bak) ? bak : srcRoom;
        if (!File.Exists(srcPath)) return;

        var dir = Directory.CreateTempSubdirectory("platereal").FullName;
        try
        {
            var pristine = File.ReadAllBytes(srcPath);
            var path = Path.Combine(dir, "ST205.DAT");
            File.WriteAllBytes(path, pristine);

            Assert.Equal(Dc2PlateKeyOutcome.Applied, Dc2PlateKeyInstaller.ApplyToDataDir(dir, seed: 4242));
            Assert.Equal(pristine, File.ReadAllBytes(path + ".bak"));

            Assert.Equal(Dc2PlateKeyOutcome.Restored, Dc2PlateKeyInstaller.ApplyToDataDir(dir, seed: 0, restore: true));
            Assert.Equal(pristine, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
