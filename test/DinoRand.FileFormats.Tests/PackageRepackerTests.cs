using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// <see cref="PackageRepacker"/>'s refusal branches and the DC2 replace round-trip. The happy DC1
/// insert path is exercised by the texture-import tests; what's pinned here is every guard whose
/// failure would let a malformed package be written into a room file silently.
/// </summary>
public class PackageRepackerTests
{
    private static PackageRepacker.NewEntry Entry(byte fill, int len = 8)
    {
        var payload = new byte[len];
        Array.Fill(payload, fill);
        return new PackageRepacker.NewEntry(GianEntryType.Texture, new VramRect(1, 2, 3, 4), payload);
    }

    // ---- InsertEntriesBeforeRdt ------------------------------------------------------------------

    [Fact]
    public void Insert_EmptyInserts_ReturnsIdenticalCopy()
    {
        var package = SyntheticRoom.Dc1Room(
            Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(), Array.Empty<SyntheticRoom.Enemy>());
        var result = PackageRepacker.InsertEntriesBeforeRdt(package, Array.Empty<PackageRepacker.NewEntry>());
        Assert.Equal(package, result);
        Assert.NotSame(package, result); // a copy, never the caller's buffer
    }

    [Fact]
    public void Insert_NonGianInput_Throws()
    {
        // Too short for a header, and a header-sized all-zero buffer (its first entry has size 0).
        Assert.Throws<InvalidOperationException>(
            () => PackageRepacker.InsertEntriesBeforeRdt(new byte[100], new[] { Entry(1) }));
        Assert.Throws<InvalidOperationException>(
            () => PackageRepacker.InsertEntriesBeforeRdt(new byte[4096], new[] { Entry(1) }));
    }

    [Fact]
    public void Insert_OverflowingTheEntryTable_Throws()
    {
        var package = SyntheticRoom.Dc1Room(
            Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(), Array.Empty<SyntheticRoom.Enemy>());
        // 2 existing + 127 inserts = 129 > the 128-slot (2048/16) table.
        var tooMany = Enumerable.Range(0, 127).Select(i => Entry((byte)i)).ToArray();
        Assert.Throws<InvalidOperationException>(() => PackageRepacker.InsertEntriesBeforeRdt(package, tooMany));
    }

    [Fact]
    public void Insert_PlacesEntriesBeforeRdt_AndKeepsRdtLast()
    {
        var package = SyntheticRoom.Dc1Room(
            new[] { new SyntheticRoom.Item(0x10, 1) }, Array.Empty<SyntheticRoom.Door>(),
            Array.Empty<SyntheticRoom.Enemy>());
        var before = GianPackage.TryParse(package)!;
        var rdtPayload = package.AsSpan(before.Entries[^1].PayloadOffset, (int)before.Entries[^1].DeclaredSize).ToArray();

        var result = PackageRepacker.InsertEntriesBeforeRdt(package, new[] { Entry(0xAA, 12), Entry(0xBB, 5) });

        var after = GianPackage.TryParse(result)!;
        Assert.Equal(before.Entries.Count + 2, after.Entries.Count);
        // inserts sit immediately before the last entry, in order, payloads intact
        Assert.Equal(0xAA, result[after.Entries[^3].PayloadOffset]);
        Assert.Equal(12u, after.Entries[^3].DeclaredSize);
        Assert.Equal(0xBB, result[after.Entries[^2].PayloadOffset]);
        // the RDT is still last and byte-identical
        Assert.Equal(rdtPayload,
            result.AsSpan(after.Entries[^1].PayloadOffset, (int)after.Entries[^1].DeclaredSize).ToArray());
    }

    // ---- ReplaceEntryDc2 -------------------------------------------------------------------------

    [Fact]
    public void ReplaceDc2_NonGianInput_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => PackageRepacker.ReplaceEntryDc2(new byte[100], GianEntryType.Data, new byte[4]));
    }

    [Fact]
    public void ReplaceDc2_Dc1Package_Throws()
    {
        var dc1 = SyntheticRoom.Dc1Room(
            Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(), Array.Empty<SyntheticRoom.Enemy>());
        Assert.Throws<InvalidDataException>(
            () => PackageRepacker.ReplaceEntryDc2(dc1, GianEntryType.Data, new byte[4]));
    }

    [Fact]
    public void ReplaceDc2_ZeroOrMultipleMatches_Throws()
    {
        var room = SyntheticRoom.Dc2Room(0); // one Lzss0 + one Data entry
        Assert.Throws<InvalidDataException>(
            () => PackageRepacker.ReplaceEntryDc2(room, GianEntryType.Texture, new byte[4])); // 0 matches

        var twoLzss = SyntheticRoom.Package(GianPackage.Dc2EntrySize,
            (GianEntryType.Lzss0, Lzss.Compress(new byte[64])),
            (GianEntryType.Lzss0, Lzss.Compress(new byte[64])));
        Assert.Throws<InvalidDataException>(
            () => PackageRepacker.ReplaceEntryDc2(twoLzss, GianEntryType.Lzss0, new byte[4])); // 2 matches
    }

    [Fact]
    public void ReplaceDc2_DifferentSizePayload_RelaysSectorsAndReparses()
    {
        var room = SyntheticRoom.Dc2Room(3);
        var originalBlob = Lzss.Decompress(GetPayload(room, GianEntryType.Lzss0));

        // Replace the Data tail with a larger payload — sizes may differ; offsets are re-derived.
        var newTail = Enumerable.Range(0, 3000).Select(i => (byte)(i & 0xff)).ToArray();
        var result = PackageRepacker.ReplaceEntryDc2(room, GianEntryType.Data, newTail);

        var pkg = GianPackage.TryParse(result)!;
        Assert.True(pkg.IsDc2); // stride preserved — the container-format corruption guard's invariant
        Assert.Equal(newTail, GetPayload(result, GianEntryType.Data));
        // the untouched LZSS0 entry still decompresses to the same blob
        Assert.Equal(originalBlob, Lzss.Decompress(GetPayload(result, GianEntryType.Lzss0)));
    }

    private static byte[] GetPayload(byte[] package, GianEntryType type)
    {
        var pkg = GianPackage.TryParse(package)!;
        var e = pkg.Entries.Single(e => e.Type == type);
        return package.AsSpan(e.PayloadOffset, (int)e.DeclaredSize).ToArray();
    }
}
