using DinoRand.FileFormats.Stage.Dc2;
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

    [Fact]
    public void EmitFile_WithoutDataDir_Throws()
    {
        var sink = new Dc2BackupSwapSink(); // no dataDir
        Assert.Throws<InvalidOperationException>(() => sink.EmitFile("WEP_P0.DAT", new byte[] { 1 }));
    }

    [Fact]
    public void EmitFile_WithDataDir_BacksUpAndSwaps()
    {
        var dir = Directory.CreateTempSubdirectory("dc2sink").FullName;
        try
        {
            var original = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(Path.Combine(dir, "WEP_P0.DAT"), original);

            new Dc2BackupSwapSink(dir).EmitFile("WEP_P0.DAT", new byte[] { 9 });

            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(dir, "WEP_P0.DAT")));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(dir, "WEP_P0.DAT.bak")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Emit_InMemoryRoomWithoutSourcePath_Throws()
    {
        var room = Dc2RoomFile.Read(1, 1, SyntheticRoom.Dc2Room(0));
        Assert.Throws<InvalidOperationException>(() => new Dc2BackupSwapSink().Emit(room, new byte[] { 1 }));
    }

    [Fact]
    public void Emit_RejectsContainerStrideChange_TheRoomLoadCrashClass()
    {
        // A DC2 32-byte-entry room emitted as a DC1 16-byte-entry package is the container-stride
        // corruption that crashes the game on room load — the guard must refuse it BEFORE any write.
        var dir = Directory.CreateTempSubdirectory("dc2sink").FullName;
        try
        {
            var path = Path.Combine(dir, "ST101.DAT");
            File.WriteAllBytes(path, SyntheticRoom.Dc2Room(0));
            var room = Dc2RoomFile.ReadFromFile(1, 1, path);

            var dc1Stride = SyntheticRoom.Dc1Room(
                Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(),
                Array.Empty<SyntheticRoom.Enemy>());
            Assert.Throws<InvalidOperationException>(() => new Dc2BackupSwapSink().Emit(room, dc1Stride));

            Assert.False(File.Exists(path + ".bak"));                        // nothing was written
            Assert.Equal(room.OriginalBytes, File.ReadAllBytes(path));       // install untouched
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Emit_StridePreservingBytes_SwapInPlaceWithBackup()
    {
        var dir = Directory.CreateTempSubdirectory("dc2sink").FullName;
        try
        {
            var path = Path.Combine(dir, "ST102.DAT");
            var original = SyntheticRoom.Dc2Room(1);
            File.WriteAllBytes(path, original);
            var room = Dc2RoomFile.ReadFromFile(1, 2, path);

            // a legitimate edit: same room repacked through the DC2 writer
            var edited = Dc2SpawnEditor.WriteByte(original, 10, 0x42);
            new Dc2BackupSwapSink().Emit(room, edited);

            Assert.Equal(edited, File.ReadAllBytes(path));
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
