using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests.Refactor;

public sealed class FileFormatCharacterizationTests
{
    [Fact]
    public void DC1_authored_corpus_is_exact_after_real_parse_and_write()
    {
        int executed = 0;
        foreach (string path in Directory.EnumerateFiles(MockRooms.Dc1DataDir(), "*.dat"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            int stage = Convert.ToInt32(stem.Substring(2, 1), 16);
            int room = Convert.ToInt32(stem.Substring(3, 2), 16);
            byte[] original = File.ReadAllBytes(path);
            byte[] written = RoomFile.Read(stage, room, original).Write();
            Assert.Equal(original, written);
            Assert.True(RoomFile.Read(stage, room, written).ParsedCleanly);
            executed++;
        }
        Assert.Equal(12, executed);
    }

    [Fact]
    public void DC2_package_is_actually_repacked_and_canonical_payloads_are_identical()
    {
        byte[] original = SyntheticRoom.Dc2Room(4);
        var parsed = Dc2RoomFile.Read(8, 94, original);
        Assert.Equal(original, parsed.Write());
        var before = GianPackage.TryParse(original);
        Assert.NotNull(before);
        Assert.True(before!.IsDc2);

        int index = before.Entries.Count - 1;
        var entry = before.Entries[index];
        byte[] repacked = PackageRepacker.ReplaceEntryDc2(original, index,
            original.AsSpan(entry.PayloadOffset, checked((int)entry.DeclaredSize)));
        var after = GianPackage.TryParse(repacked);
        Assert.NotNull(after);
        Assert.Equal(before.EntrySize, after!.EntrySize);
        Assert.Equal(before.Entries.Select(e => (e.Type, e.DeclaredSize)),
            after.Entries.Select(e => (e.Type, e.DeclaredSize)));
        for (int i = 0; i < before.Entries.Count; i++)
        {
            var a = before.Entries[i];
            var b = after.Entries[i];
            Assert.Equal(original.AsSpan(a.PayloadOffset, checked((int)a.DeclaredSize)).ToArray(),
                repacked.AsSpan(b.PayloadOffset, checked((int)b.DeclaredSize)).ToArray());
        }
        Assert.Equal(repacked, Dc2RoomFile.Read(8, 94, repacked).Write());
    }
}
