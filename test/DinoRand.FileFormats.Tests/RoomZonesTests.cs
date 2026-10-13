using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>Camera-zone and floor-zone tables of the DC1 room buffer (STATIC-SCD-RE cont.99).</summary>
public class RoomZonesTests
{
    private const uint Base = RoomScript.PsxRdtBase;

    private static void W16(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }
    private static void W32(byte[] b, int at, uint v) { W16(b, at, (int)v); W16(b, at + 2, (int)(v >> 16)); }

    /// <summary>A buffer holding only the seven header pointers, a camera table of two zones at 0x40
    /// and a floor table of one zone after it.</summary>
    private static byte[] Synthetic()
    {
        var b = new byte[0x40 + 4 + 2 * 68 + 4 + 20];
        int cam = 0x40, floor = cam + 4 + 2 * 68;
        W32(b, 3 * 4, Base + (uint)cam);
        W32(b, 4 * 4, Base + (uint)floor);
        W32(b, 6 * 4, Base + (uint)floor);

        W32(b, cam, 2);
        for (int i = 0; i < 2; i++)
        {
            int rec = cam + 4 + i * 68;
            b[rec + 2] = (byte)(i + 1);          // camera id
            b[rec + 3] = (byte)(i * 3);          // floor group
            for (int k = 0; k < 8; k++) W16(b, rec + 4 + k * 2, -100 * (i + 1) + k);
        }

        b[floor] = 1; b[floor + 3] = 0x24;       // count byte; a real room has junk in the padding
        for (int k = 0; k < 8; k++) W16(b, floor + 4 + k * 2, 1000 + k);
        b[floor + 4 + 16] = 2;                   // level
        b[floor + 4 + 17] = 5;                   // step
        return b;
    }

    [Fact]
    public void ReadCameraZones_DecodesIdFloorGroupAndQuad()
    {
        var zones = RoomZones.ReadCameraZones(Synthetic());

        Assert.Equal(2, zones.Count);
        Assert.Equal(1, zones[0].CameraId);
        Assert.Equal(2, zones[1].CameraId);
        Assert.Equal(3, zones[1].FloorGroup);
        Assert.Equal(new ZoneQuad(-100, -99, -98, -97, -96, -95, -94, -93), zones[0].Area);
    }

    [Fact]
    public void ReadFloorZones_ReadsAByteCount_IgnoringThePadding()
    {
        var zone = Assert.Single(RoomZones.ReadFloorZones(Synthetic()));

        Assert.Equal(new ZoneQuad(1000, 1001, 1002, 1003, 1004, 1005, 1006, 1007), zone.Area);
        Assert.Equal(2, zone.Level);
        Assert.Equal(5, zone.Step);
    }

    [Fact]
    public void Readers_ReturnEmpty_WhenThePointersDoNotFit()
    {
        Assert.Empty(RoomZones.ReadCameraZones(new byte[8]));
        Assert.Empty(RoomZones.ReadFloorZones(new byte[64]));          // null pointers
        var truncated = Synthetic()[..0x60];                           // table runs off the end
        Assert.Empty(RoomZones.ReadCameraZones(truncated));
    }

    /// <summary>On the real install the two tables tile the buffer exactly: the camera table ends at
    /// pointer 4 in every room, the floor table at pointer 5 in all but st60e.</summary>
    [Fact]
    public void RealInstall_TablesEndExactlyAtTheNextTable()
    {
        var files = RoomFileRoundTripTests.RoomFiles().Select(r => (string)r[0]).ToList();
        if (files.Count < 100) return;   // synthetic corpus has no zone tables

        int cameraFits = 0, floorFits = 0;
        var floorMisfits = new List<string>();
        foreach (var path in files)
        {
            var rdt = RoomFile.Read(0, 0, File.ReadAllBytes(path)).RdtBuffer;
            Assert.True(RoomZones.TryPointer(rdt, 3, out int p3));
            Assert.True(RoomZones.TryPointer(rdt, 4, out int p4));
            Assert.True(RoomZones.TryPointer(rdt, 5, out int p5));
            Assert.True(RoomZones.TryPointer(rdt, 6, out int p6));

            var cams = RoomZones.ReadCameraZones(rdt);
            if (p3 + 4 + cams.Count * 68 == p4) cameraFits++;

            if (p6 + 4 + RoomZones.ReadFloorZones(rdt).Count * 20 == p5) floorFits++;
            else floorMisfits.Add(Path.GetFileNameWithoutExtension(path).ToLowerInvariant());
        }

        Assert.Equal(files.Count, cameraFits);
        Assert.Equal(new[] { "st60e" }, floorMisfits);
        Assert.Equal(files.Count - 1, floorFits);
    }
}
