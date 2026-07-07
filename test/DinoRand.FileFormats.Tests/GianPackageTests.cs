using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Integration test over the real DC1 install (gated on <c>DINORAND_DC1_DIR</c>, no-ops
/// without it). Asserts every room file parses as a <see cref="GianPackage"/> and exposes
/// an in-bounds room-data segment — the structural pre-req for record walking. The Python
/// prototype validated 145/145; this freezes that result in CI.
/// </summary>
public class GianPackageTests
{
    // ---- Synthetic-data unit tests (no game files needed) ----

    /// <summary>Builds a minimal DC1 (16-byte entry) package with the given typed payloads.</summary>
    private static byte[] BuildDc1Package(params (GianEntryType type, byte[] payload)[] entries)
    {
        static int Align(int v) => (v + 2047) & ~2047;

        int total = GianPackage.HeaderSize + entries.Sum(e => Align(e.payload.Length));
        var buf = new byte[total];

        int pos = GianPackage.HeaderSize;
        for (int i = 0; i < entries.Length; i++)
        {
            int hdr = i * GianPackage.Dc1EntrySize;
            WriteU32(buf, hdr, (uint)entries[i].type);
            WriteU32(buf, hdr + 4, (uint)entries[i].payload.Length);
            entries[i].payload.CopyTo(buf, pos);
            pos += Align(entries[i].payload.Length);
        }
        return buf;
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    [Fact]
    public void TryParse_Dc1_LocatesEntriesAndLastAsRoomData()
    {
        var roomData = new byte[] { 0x83, 0x00, 0x20, 0x00, 0x83, 0x00, 0x40, 0x00, 0x62, 0x02 };
        var pkg = GianPackage.TryParse(BuildDc1Package(
            (GianEntryType.Texture, new byte[100]),
            (GianEntryType.Palette, new byte[16]),
            (GianEntryType.Unknown, roomData)));

        Assert.NotNull(pkg);
        Assert.False(pkg!.IsDc2);
        Assert.Equal(16, pkg.EntrySize);
        Assert.Equal(3, pkg.Entries.Count);

        var rdt = pkg.RoomDataEntry;
        Assert.NotNull(rdt);
        Assert.Equal(GianEntryType.Unknown, rdt!.Value.Type);
        Assert.Equal((uint)roomData.Length, rdt.Value.DeclaredSize);
        // First payload starts right after the 2048 header; second is sector-aligned after it.
        Assert.Equal(GianPackage.HeaderSize, pkg.Entries[0].PayloadOffset);
        Assert.Equal(GianPackage.HeaderSize + 2048, pkg.Entries[1].PayloadOffset);
    }

    [Fact]
    public void TryParse_StopsAtUnknownEntryType()
    {
        var buf = BuildDc1Package((GianEntryType.Data, new byte[8]));
        // Corrupt the *second* (unused) entry slot to an out-of-range type with a size,
        // then confirm the parser stops at entry 1 and keeps only the first.
        WriteU32(buf, GianPackage.Dc1EntrySize, 0x99);
        WriteU32(buf, GianPackage.Dc1EntrySize + 4, 32);

        var pkg = GianPackage.TryParse(buf);
        Assert.NotNull(pkg);
        Assert.Single(pkg!.Entries);
    }

    [Fact]
    public void TryParse_RejectsTooShortInput()
        => Assert.Null(GianPackage.TryParse(new byte[100]));

    [Theory]
    [MemberData(nameof(RoomFileRoundTripTests.RoomFiles), MemberType = typeof(RoomFileRoundTripTests))]
    public void Room_ParsesAsPackage_WithInBoundsRoomData(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var pkg = GianPackage.TryParse(bytes);

        Assert.NotNull(pkg);
        Assert.False(pkg!.IsDc2, "DC1 rooms must use the 16-byte entry layout.");
        Assert.NotEmpty(pkg.Entries);

        var rdt = pkg.RoomDataEntry;
        Assert.NotNull(rdt);
        Assert.True(rdt!.Value.PayloadOffset >= GianPackage.HeaderSize);
        Assert.True(rdt.Value.PayloadOffset + (long)rdt.Value.DeclaredSize <= bytes.Length,
            $"room-data segment runs past EOF in {Path.GetFileName(path)}");
    }
}
