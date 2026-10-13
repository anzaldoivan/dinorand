using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>The read-only room dump behind <c>--dump-room</c>: package entries, decoded records and a
/// named instruction listing.</summary>
public class RoomDumpTests
{
    private static string Dump(byte[] file)
    {
        var writer = new StringWriter();
        RoomDump.Write(writer, RoomFile.Read(1, 2, file));
        return writer.ToString();
    }

    [Fact]
    public void Dump_ListsEntries_Records_AndNamedInstructions()
    {
        var text = Dump(SyntheticRoom.Dc1RoomWithTrailingEntries(
            new[] { new SyntheticRoom.Item(0x16, 3) },
            new[] { new SyntheticRoom.Door(2, 0x0d, 0, 0) },
            new[] { new SyntheticRoom.Enemy(DcOpcodes.Enemy, 2, 21), new SyntheticRoom.Enemy(DcOpcodes.Enemy2, 8, 22) }));

        Assert.Contains("room 0102", text);
        Assert.Contains("5 entries", text);
        Assert.Contains("load 0x80100000  <- room data", text);
        Assert.Contains("load 0x801FE9C0", text);
        Assert.Contains("doors: 1", text);
        Assert.Contains("-> 020D", text);
        Assert.Contains("items: 1", text);
        Assert.Contains("id 0x16 x3", text);
        Assert.Contains("enemies: 2", text);
        Assert.Contains("Velociraptor", text);
        Assert.Contains("Therizinosaurus", text);
        // instruction listing: proven names, with the raw bytes
        Assert.Contains("ENEMY ", text);
        Assert.Contains("ENEMY2 ", text);
        Assert.Contains("AOT ", text);
        Assert.Matches(@"(?m)^\s+0x[0-9A-F]{5}  20  ENEMY\s+20 ", text);
    }

    [Fact]
    public void Dump_PrintsOpXX_ForAnOpcodeWithoutAProvenName()
    {
        // one subroutine holding a single 0x62 (length-only opcode, 8 bytes)
        var rdt = new byte[0x24 + 4 + 8];
        rdt[0x14] = 0x24; rdt[0x15] = 0x00; rdt[0x16] = 0x10; rdt[0x17] = 0x80;   // table @ 0x24
        rdt[0x24] = 4;                                                              // one entry
        rdt[0x28] = 0x62;
        var file = SyntheticRoom.Dc1PackageWithLoad(
            (GianEntryType.Texture, new byte[] { 1, 2, 3, 4 }, 0),
            (GianEntryType.Data, rdt, RoomScript.PsxRdtBase));

        Assert.Matches(@"(?m)^\s+0x00028  62  OP_62\s+62 00 00 00 00 00 00 00", Dump(file));
    }

    [Fact]
    public void Dump_OfAnUnparsedFile_SaysSo()
    {
        var writer = new StringWriter();
        RoomDump.Write(writer, RoomFile.ReadOpaque(7, 1, new byte[16]));
        Assert.Contains("not a recognised room package", writer.ToString());
    }
}
