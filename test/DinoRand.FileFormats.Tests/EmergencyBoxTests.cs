using System.Globalization;
using System.Text.Json;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the emergency-box catalog in <c>data/dc1/emergency-boxes.json</c> to
/// <see cref="DinoCrisis1.EmergencyBoxes"/> / <see cref="DinoCrisis1.PlugItemId"/>, and checks the
/// catalog's internal integrity (item ids ↔ names vs <c>items.json</c> allItems). Structure (box id, room,
/// plug cost) comes from the room scripts' box records; contents are the exe table, record = box id
/// (EMERGENCY-BOX-DATA.md).
/// </summary>
public class EmergencyBoxTests
{
    private sealed record ContentRow(string id, string name, int count);
    private sealed record Contents(ContentRow[] easy, ContentRow[] normal, ContentRow[] hard, ContentRow[] veryHard);
    private sealed record BoxRow(string id, int boxId, string name, string room, string? color, int plugs, Contents contents);
    private sealed record BoxesFile(string plugItemId, BoxRow[] boxes);

    private sealed record ItemRow(string id, string name, string? category);
    private sealed record ItemsFile(ItemRow[] allItems);

    private static int ParseId(string hex) =>
        int.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static T LoadJson<T>(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "dc1", file);
        Assert.True(File.Exists(path), $"{file} not found at {path}");
        var obj = JsonSerializer.Deserialize<T>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(obj);
        return obj!;
    }

    [Fact]
    public void EmergencyBoxes_MatchDinoCrisis1()
    {
        var file = LoadJson<BoxesFile>("emergency-boxes.json");
        Assert.NotNull(file.boxes);
        var game = new DinoCrisis1();

        Assert.Equal(ParseId(file.plugItemId), game.PlugItemId);

        // The JSON catalog and the mirrored DinoCrisis1.EmergencyBoxes must agree on (box id, room, plug
        // cost, colour), in the same order — so the two never drift.
        var expected = file.boxes.Select(b => (b.boxId, ParseId(b.room), b.plugs, b.color)).ToArray();
        var actual = game.EmergencyBoxes
            .Select(b => (b.BoxId, b.RoomCode, b.PlugCost, (string?)b.Colour.ToString().ToLowerInvariant())).ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>The box records read from every English room script (0x28 subtype 8, byte +0x1C = id,
    /// +0x1D = cost): id, room and vanilla cost.</summary>
    public static readonly (int Id, int Room, int Cost)[] VanillaBoxes =
    {
        (0, 0x0113, 2), (1, 0x0205, 1), (2, 0x0306, 2), (3, 0x0502, 2), (4, 0x050C, 2), (5, 0x0602, 3),
        (6, 0x0608, 3), (7, 0x0610, 1), (8, 0x0105, 1), (9, 0x0301, 2), (10, 0x0502, 1), (11, 0x050C, 1),
        (12, 0x0608, 1), (13, 0x0104, 1), (14, 0x0602, 1), (15, 0x060D, 1), (16, 0x0612, 1),
    };

    [Fact]
    public void EmergencyBoxCatalog_MatchesTheRoomScripts()
    {
        var game = new DinoCrisis1();
        Assert.Equal(VanillaBoxes, game.EmergencyBoxes.Select(b => (b.BoxId, b.RoomCode, b.PlugCost)).ToArray());
        // Colour comes from the id: 8 red (0-7), 5 green (8-12), 4 yellow (13-16).
        Assert.Equal(8, game.EmergencyBoxes.Count(b => b.Colour == EmergencyBoxColour.Red));
        Assert.Equal(5, game.EmergencyBoxes.Count(b => b.Colour == EmergencyBoxColour.Green));
        Assert.Equal(4, game.EmergencyBoxes.Count(b => b.Colour == EmergencyBoxColour.Yellow));
        Assert.All(game.EmergencyBoxes, b => Assert.InRange(b.PlugCost, 1, 3));
        Assert.Equal(26, game.EmergencyBoxes.Sum(b => b.PlugCost));
    }

    /// <summary>The 26 box records of the English install, at their RDT offsets: the 17 base rooms plus the
    /// stage A/B/C revisit copies, which carry the same id and cost.</summary>
    private static readonly (int Stage, int Room, int Offset, int Id)[] VanillaRecords =
    {
        (0x1, 0x13, 0x102A0, 0), (0xA, 0x13, 0x101A0, 0), (0x2, 0x05, 0xC394, 1), (0x3, 0x06, 0x45FD8, 2),
        (0xB, 0x06, 0x45FD4, 2), (0x5, 0x02, 0x47FB8, 3), (0x5, 0x0C, 0x43488, 4), (0x6, 0x02, 0x16A0C, 5),
        (0xC, 0x02, 0x110C4, 5), (0x6, 0x08, 0x3FBD8, 6), (0xC, 0x08, 0x3FBA0, 6), (0x6, 0x10, 0x222D8, 7),
        (0x1, 0x05, 0x429BC, 8), (0xA, 0x05, 0x42944, 8), (0x3, 0x01, 0x3C590, 9), (0xB, 0x01, 0x3C538, 9),
        (0x5, 0x02, 0x47FD8, 10), (0x5, 0x0C, 0x434A8, 11), (0x6, 0x08, 0x3FBB8, 12), (0xC, 0x08, 0x3FB80, 12),
        (0x1, 0x04, 0x348B0, 13), (0xA, 0x04, 0x347EC, 13), (0x6, 0x02, 0x169EC, 14), (0xC, 0x02, 0x110A4, 14),
        (0x6, 0x0D, 0x2C3C8, 15), (0x6, 0x12, 0x22A40, 16),
    };

    /// <summary>Gated on <c>DINORAND_DC1_DIR</c>: the room scripts hold exactly the 26 box records above, with
    /// the catalogue's cost for each id.</summary>
    [Fact]
    public void RealInstall_BoxRecordsMatchTheCatalogue()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return;
        var game = new DinoCrisis1();
        var refs = game.EnumerateRooms(root);
        if (refs.Count == 0) return;

        var found = new List<(int, int, int, int)>();
        foreach (var r in refs)
        {
            var room = RoomFile.ReadFromFile(r.Stage, r.Room, r.Path);
            foreach (var box in room.Script?.EmergencyBoxes ?? [])
            {
                found.Add((r.Stage, r.Room, box.FileOffset, box.BoxId));
                Assert.Equal(game.EmergencyBoxes[box.BoxId].PlugCost, box.PlugCost);
            }
        }
        Assert.Equal(VanillaRecords.OrderBy(x => x).ToArray(), found.OrderBy(x => x).ToArray());
    }

    /// <summary>Gated on <c>DINORAND_DINO_EXE</c>: box id N's catalogue contents are exe contents record N, in
    /// slot order, in each International block (which settles "record index = box id").</summary>
    [Fact]
    public void RealExe_ContentsRecordIndexIsTheBoxId()
    {
        var path = Environment.GetEnvironmentVariable("DINORAND_DINO_EXE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var exe = File.ReadAllBytes(path);
        var file = LoadJson<BoxesFile>("emergency-boxes.json");
        var blocks = new (uint Va, Func<Contents, ContentRow[]> Tier)[]
        {
            (ExePatcher.EmergencyBoxBlockEasyVa, c => c.easy), (ExePatcher.EmergencyBoxBlockEasyVa, c => c.normal),
            (ExePatcher.EmergencyBoxBlockHardVa, c => c.hard), (ExePatcher.EmergencyBoxBlockVeryHardVa, c => c.veryHard),
        };
        foreach (var box in file.boxes)
            foreach (var (va, tier) in blocks)
            {
                int rec = ExePatcher.VaToFileOffset(va) + box.boxId * ExePatcher.EmergencyBoxRecordStride;
                var pairs = Enumerable.Range(0, 10).Select(k => (id: (int)exe[rec + 1 + 2 * k], count: (int)exe[rec + 2 + 2 * k]))
                    .TakeWhile(p => p.id != 0).ToArray();
                Assert.Equal(tier(box.contents).Select(e => (ParseId(e.id), e.count)).ToArray(), pairs);
            }
    }

    [Fact]
    public void EmergencyBoxContents_EveryItem_MatchesCanonicalIdAndName()
    {
        var file = LoadJson<BoxesFile>("emergency-boxes.json");
        var byId = LoadJson<ItemsFile>("items.json").allItems.ToDictionary(r => ParseId(r.id), r => r.name);

        foreach (var box in file.boxes)
            foreach (var tier in new[] { box.contents.easy, box.contents.normal, box.contents.hard, box.contents.veryHard })
            {
                Assert.NotNull(tier);
                foreach (var e in tier)
                {
                    Assert.True(byId.TryGetValue(ParseId(e.id), out var name),
                        $"box '{box.id}' references id {e.id} not in items.json allItems");
                    Assert.Equal(name, e.name);
                    Assert.True(e.count >= 1, $"box '{box.id}' item {e.id} has non-positive count {e.count}");
                }
            }
    }
}
