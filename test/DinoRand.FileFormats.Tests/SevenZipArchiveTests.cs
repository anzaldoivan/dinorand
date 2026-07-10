using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DinoRand.FileFormats.SevenZip;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// CI-durable coverage for the minimal 7z reader/splice-writer: a tiny hand-authored Copy-codec
/// archive (literal bytes, no game content) round-trips through <see cref="SevenZipArchive.Read"/> →
/// <see cref="SevenZipArchive.RebuildWithReplacedStreams"/> → <see cref="SevenZipArchive.Read"/>.
/// Unlike the real-DLL tests this never skips, so the container logic stays covered on a clean clone.
/// The fixture (and its CRCs) is built with an independent bitwise CRC-32, cross-validating the
/// production table-driven <c>Crc32</c> the reader verifies against.
/// </summary>
public class SevenZipArchiveTests
{
    /// <summary>Independent bitwise CRC-32 (reflected, poly 0xEDB88320) — deliberately NOT the
    /// production implementation.</summary>
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Hand-emit a Copy-codec, non-solid, plain-header 7z (the exact shape the splice-writer produces).
    /// All lengths/counts must stay &lt; 128 so every 7z variable-length number is a single byte.
    /// </summary>
    private static byte[] BuildTinyCopy7z(params (string Name, byte[] Content)[] files)
    {
        var pack = new List<byte>();
        foreach (var f in files) pack.AddRange(f.Content);

        var h = new List<byte>
        {
            0x01,                       // kHeader
            0x04,                       // kMainStreamsInfo
            0x06, 0x00,                 // kPackInfo, packPos = 0
            (byte)files.Length,         // numPackStreams
            0x09,                       // kSize
        };
        foreach (var f in files) h.Add((byte)f.Content.Length);
        h.Add(0x00);                    // end PackInfo
        h.Add(0x07);                    // kUnpackInfo
        h.Add(0x0B);                    // kFolder
        h.Add((byte)files.Length);
        h.Add(0x00);                    // folders inline (not external)
        foreach (var f in files) { h.Add(0x01); h.Add(0x01); h.Add(0x00); } // 1 coder: flags idSize=1, id 00 = Copy
        h.Add(0x0C);                    // kCodersUnpackSize
        foreach (var f in files) h.Add((byte)f.Content.Length);
        h.Add(0x00);                    // end UnpackInfo
        h.Add(0x08);                    // kSubStreamsInfo
        h.Add(0x0A); h.Add(0x01);       // kCRC, all defined
        foreach (var f in files) h.AddRange(BitConverter.GetBytes(Crc(f.Content)));
        h.Add(0x00);                    // end SubStreamsInfo
        h.Add(0x00);                    // end MainStreamsInfo

        var names = new List<byte> { 0x00 }; // names inline (not external)
        foreach (var f in files) { names.AddRange(Encoding.Unicode.GetBytes(f.Name)); names.Add(0); names.Add(0); }
        h.Add(0x05);                    // kFilesInfo
        h.Add((byte)files.Length);
        h.Add(0x11);                    // kName property
        h.Add((byte)names.Count);
        h.AddRange(names);
        h.Add(0x00);                    // end FilesInfo
        h.Add(0x00);                    // end Header

        var startHeader = new List<byte>();
        startHeader.AddRange(BitConverter.GetBytes((ulong)pack.Count)); // NextHeaderOffset
        startHeader.AddRange(BitConverter.GetBytes((ulong)h.Count));    // NextHeaderSize
        startHeader.AddRange(BitConverter.GetBytes(Crc(h.ToArray())));  // NextHeaderCRC

        var blob = new List<byte> { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 };
        blob.AddRange(BitConverter.GetBytes(Crc(startHeader.ToArray())));
        blob.AddRange(startHeader);
        blob.AddRange(pack);
        blob.AddRange(h);
        return blob.ToArray();
    }

    private static readonly (string Name, byte[] Content)[] Fixture =
    {
        (@"diff\a.bin", Encoding.ASCII.GetBytes("stock digit run 0375 lives here")),
        ("b.bin", new byte[] { 0x00, 0xFF, 0x10, 0x20, 0x30 }),
        (@"diff\c.bin", Encoding.ASCII.GetBytes("untouched")),
    };

    [Fact]
    public void Read_TinyCopyArchive_ParsesNamesStreamsAndCrcVerifiedContent()
    {
        var arc = SevenZipArchive.Read(BuildTinyCopy7z(Fixture));
        Assert.Equal(3, arc.Streams.Count);
        Assert.Equal(3, arc.StreamIndexByName.Count);
        foreach (var f in Fixture) // ExtractFile CRC-checks with the production Crc32 vs our bitwise CRCs
            Assert.Equal(f.Content, arc.ExtractFile(f.Name.Replace('\\', '/')));
    }

    [Fact]
    public void Rebuild_ReplacesOneStream_PreservesTheRest_AndRereads()
    {
        var arc = SevenZipArchive.Read(BuildTinyCopy7z(Fixture));
        byte[] replacement = Encoding.ASCII.GetBytes("scrambled digit run 6767 now (different length too)");
        int idx = arc.StreamIndexByName["diff/a.bin"];

        var rebuilt = SevenZipArchive.Read(arc.RebuildWithReplacedStreams(
            new Dictionary<int, byte[]> { [idx] = replacement }));

        Assert.Equal(replacement, rebuilt.ExtractFile("diff/a.bin"));
        Assert.Equal(Fixture[1].Content, rebuilt.ExtractFile("b.bin"));
        Assert.Equal(Fixture[2].Content, rebuilt.ExtractFile("diff/c.bin"));
        Assert.Equal(3, rebuilt.StreamIndexByName.Count); // FilesInfo carried through verbatim
    }

    [Fact]
    public void Rebuild_NoReplacements_RoundTripsEveryStream()
    {
        var arc = SevenZipArchive.Read(BuildTinyCopy7z(Fixture));
        var rebuilt = SevenZipArchive.Read(arc.RebuildWithReplacedStreams(new Dictionary<int, byte[]>()));
        foreach (var f in Fixture)
            Assert.Equal(f.Content, rebuilt.ExtractFile(f.Name.Replace('\\', '/')));
    }

    [Fact]
    public void Read_RefusesCorruptArchives()
    {
        byte[] good = BuildTinyCopy7z(Fixture);

        var badSig = (byte[])good.Clone();
        badSig[0] = 0x00;
        Assert.Throws<InvalidDataException>(() => SevenZipArchive.Read(badSig));

        var badHeaderCrc = (byte[])good.Clone();
        badHeaderCrc[^1] ^= 0xFF; // flip a header byte -> NextHeaderCRC mismatch
        Assert.Throws<InvalidDataException>(() => SevenZipArchive.Read(badHeaderCrc));

        var badContent = (byte[])good.Clone();
        badContent[0x20] ^= 0xFF; // flip a packed byte -> stream CRC mismatch on extract
        var arc = SevenZipArchive.Read(badContent);
        Assert.Throws<InvalidDataException>(() => arc.ExtractStream(0));
    }
}
