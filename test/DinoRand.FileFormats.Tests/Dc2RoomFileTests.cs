using DinoRand.FileFormats.Stage.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>Dc2RoomFile's byte-exact round-trip contract — the invariant every DC2 pass and sink
/// relies on: what was read is exactly what an unedited Write() emits, and only file-backed rooms
/// carry a SourcePath for the backup-swap sink.</summary>
public class Dc2RoomFileTests
{
    [Fact]
    public void Read_CapturesBytes_AndWriteIsByteExact()
    {
        var bytes = SyntheticRoom.Dc2Room(2);
        var room = Dc2RoomFile.Read(4, 7, bytes);

        Assert.Equal(4, room.Stage);
        Assert.Equal(7, room.Room);
        Assert.Equal(bytes, room.OriginalBytes);
        Assert.Equal(bytes, room.Write());   // byte-exact no-op round-trip
        Assert.Null(room.SourcePath);        // in-memory room: no on-disk identity
        Assert.Empty(room.Enemies);
    }

    [Fact]
    public void Read_CopiesInput_NotAliasesIt()
    {
        var bytes = SyntheticRoom.Dc2Room(0);
        var room = Dc2RoomFile.Read(1, 1, bytes);
        bytes[0] ^= 0xFF; // caller mutates its buffer afterwards
        Assert.NotEqual(bytes[0], room.OriginalBytes[0]);
    }

    [Fact]
    public void ReadFromFile_SetsSourcePath()
    {
        var dir = Directory.CreateTempSubdirectory("dc2room").FullName;
        try
        {
            var path = Path.Combine(dir, "ST407.DAT");
            var bytes = SyntheticRoom.Dc2Room(5);
            File.WriteAllBytes(path, bytes);

            var room = Dc2RoomFile.ReadFromFile(4, 7, path);
            Assert.Equal(path, room.SourcePath);
            Assert.Equal(bytes, room.Write());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ToString_FormatsStId()
    {
        Assert.Equal("ST407", Dc2RoomFile.Read(4, 7, new byte[1]).ToString());
        Assert.Equal("ST112", Dc2RoomFile.Read(1, 12, new byte[1]).ToString());
    }
}
