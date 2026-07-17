using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The circuit-shuffle install step, directory-level (<c>ApplyToDataDir</c>) on synthetic room
/// packages (<see cref="Dc2CircuitPatchTests.MakePackage"/>). Pins the outcome branches, seed
/// determinism, the backup-and-swap contract (<c>ST*.DAT.bak</c>), and the
/// validate-both-before-writing-either rule.
/// </summary>
public class Dc2CircuitShuffleInstallerTests
{
    private static string MakeDataDir(out Dictionary<string, byte[]> pristine)
    {
        var dir = Directory.CreateTempSubdirectory("circinst").FullName;
        pristine = new Dictionary<string, byte[]>();
        foreach (var spec in Dc2CircuitPatch.Rooms)
        {
            var bytes = Dc2CircuitPatchTests.MakePackage(spec);
            File.WriteAllBytes(Path.Combine(dir, spec.FileName), bytes);
            pristine[spec.FileName] = bytes;
        }
        return dir;
    }

    [Fact]
    public void MissingRoom_ReturnsNotFound_WritesNothing()
    {
        var dir = Directory.CreateTempSubdirectory("circinst").FullName;
        try
        {
            Assert.Equal(Dc2CircuitShuffleOutcome.NotFound,
                Dc2CircuitShuffleInstaller.ApplyToDataDir(dir, seed: 1));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Apply_ShufflesDeterministically_AndRestoreRoundTrips()
    {
        var dir = MakeDataDir(out var pristine);
        try
        {
            Assert.Equal(Dc2CircuitShuffleOutcome.Applied,
                Dc2CircuitShuffleInstaller.ApplyToDataDir(dir, seed: 1234));
            foreach (var spec in Dc2CircuitPatch.Rooms)
            {
                var path = Path.Combine(dir, spec.FileName);
                Assert.Equal(pristine[spec.FileName], File.ReadAllBytes(path + ".bak")); // pristine captured
                Assert.NotEqual(pristine[spec.FileName], File.ReadAllBytes(path));       // room rewritten
            }
            var run1 = Dc2CircuitPatch.Rooms.ToDictionary(s => s.FileName,
                s => File.ReadAllBytes(Path.Combine(dir, s.FileName)));

            // re-run with ANOTHER seed: .bak stays pristine and the result equals a fresh
            // apply of that seed (computed from the pristine bytes — non-compounding)
            Assert.Equal(Dc2CircuitShuffleOutcome.Applied,
                Dc2CircuitShuffleInstaller.ApplyToDataDir(dir, seed: 99));
            var dir2 = MakeDataDir(out _);
            try
            {
                Dc2CircuitShuffleInstaller.ApplyToDataDir(dir2, seed: 99);
                foreach (var spec in Dc2CircuitPatch.Rooms)
                {
                    var path = Path.Combine(dir, spec.FileName);
                    Assert.Equal(pristine[spec.FileName], File.ReadAllBytes(path + ".bak"));
                    Assert.Equal(File.ReadAllBytes(Path.Combine(dir2, spec.FileName)), File.ReadAllBytes(path));
                }
            }
            finally { Directory.Delete(dir2, recursive: true); }

            // same seed reproduces the first run byte-for-byte
            Dc2CircuitShuffleInstaller.ApplyToDataDir(dir, seed: 1234);
            foreach (var spec in Dc2CircuitPatch.Rooms)
                Assert.Equal(run1[spec.FileName], File.ReadAllBytes(Path.Combine(dir, spec.FileName)));

            // restore ⇒ byte-identical rooms, backups removed
            Assert.Equal(Dc2CircuitShuffleOutcome.Restored,
                Dc2CircuitShuffleInstaller.ApplyToDataDir(dir, seed: 0, restore: true));
            foreach (var spec in Dc2CircuitPatch.Rooms)
            {
                var path = Path.Combine(dir, spec.FileName);
                Assert.Equal(pristine[spec.FileName], File.ReadAllBytes(path));
                Assert.False(File.Exists(path + ".bak"));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TamperedRoom_ReturnsUnrecognizedContent_WritesNeitherRoom()
    {
        var dir = MakeDataDir(out var pristine);
        try
        {
            // corrupt ST402's blink run (the SECOND room validated) — the FIRST room must not be
            // written either, per the validate-both-before-writing-either rule
            var spec402 = Dc2CircuitPatch.Rooms.Single(r => r.FileName == "ST402.DAT");
            var blob = MakeTamperedBlob(spec402);
            File.WriteAllBytes(Path.Combine(dir, spec402.FileName),
                SyntheticRoom.Package(GianPackage.Dc2EntrySize,
                    (GianEntryType.Lzss0, DinoRand.FileFormats.Compression.Lzss.Compress(blob)),
                    (GianEntryType.Data, new byte[16])));
            var tampered = File.ReadAllBytes(Path.Combine(dir, spec402.FileName));

            Assert.Equal(Dc2CircuitShuffleOutcome.UnrecognizedContent,
                Dc2CircuitShuffleInstaller.ApplyToDataDir(dir, seed: 1));
            Assert.Equal(pristine["ST607.DAT"], File.ReadAllBytes(Path.Combine(dir, "ST607.DAT")));
            Assert.Equal(tampered, File.ReadAllBytes(Path.Combine(dir, spec402.FileName)));
            Assert.Empty(Directory.GetFiles(dir, "*.bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static byte[] MakeTamperedBlob(Dc2CircuitPatch.RoomSpec spec)
    {
        var blob = Dc2CircuitPatchTests.MakeBlob(spec);
        var offs = Dc2CircuitPatch.LocateBlinkIdOffsets(blob, spec, spec.Routines[0]);
        blob[offs[0]] = 42;
        return blob;
    }

    /// <summary>Install-gated round trip on COPIES of the real ST607.DAT/ST402.DAT (apply → assert
    /// bytes → restore → assert pristine). Never touches the install itself.</summary>
    [Fact]
    public void RealRoomCopies_ApplyRestore_RoundTripByteIdentical()
    {
        var dataDir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dataDir) || !Directory.Exists(dataDir)) return;

        var dir = Directory.CreateTempSubdirectory("circreal").FullName;
        try
        {
            var pristine = new Dictionary<string, byte[]>();
            foreach (var spec in Dc2CircuitPatch.Rooms)
            {
                var src = Path.Combine(dataDir, spec.FileName);
                if (!File.Exists(src)) return;
                // prefer the .bak (vanilla) if the install is currently shuffled
                var bak = src + ".bak";
                var bytes = File.ReadAllBytes(File.Exists(bak) ? bak : src);
                pristine[spec.FileName] = bytes;
                File.WriteAllBytes(Path.Combine(dir, spec.FileName), bytes);
            }

            Assert.Equal(Dc2CircuitShuffleOutcome.Applied,
                Dc2CircuitShuffleInstaller.ApplyToDataDir(dir, seed: 4242));
            foreach (var spec in Dc2CircuitPatch.Rooms)
            {
                var path = Path.Combine(dir, spec.FileName);
                Assert.Equal(pristine[spec.FileName], File.ReadAllBytes(path + ".bak"));
                // the rewritten room still parses, and its blink ids honour the constraints
                var pristineBlob = Dc2DoorEditor.DecompressScdBlob(pristine[spec.FileName]);
                var newBlob = Dc2DoorEditor.DecompressScdBlob(File.ReadAllBytes(path));
                foreach (var routine in spec.Routines)
                {
                    var offs = Dc2CircuitPatch.LocateBlinkIdOffsets(pristineBlob, spec, routine);
                    var ids = offs.Select(o => newBlob[o] | (newBlob[o + 1] << 8)).ToArray();
                    Assert.All(ids, id => Assert.Contains(id, spec.BoxIds));
                    Assert.All(spec.BoxIds, id => Assert.Contains(id, ids));
                    for (int i = 1; i < ids.Length; i++)
                        Assert.NotEqual(ids[i - 1], ids[i]);
                }
            }

            Assert.Equal(Dc2CircuitShuffleOutcome.Restored,
                Dc2CircuitShuffleInstaller.ApplyToDataDir(dir, seed: 0, restore: true));
            foreach (var spec in Dc2CircuitPatch.Rooms)
            {
                var path = Path.Combine(dir, spec.FileName);
                Assert.Equal(pristine[spec.FileName], File.ReadAllBytes(path));
                Assert.False(File.Exists(path + ".bak"));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
