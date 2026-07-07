using System.Globalization;
using System.Text.Json;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the emergency-box catalog in <c>data/dc1/emergency-boxes.json</c> to
/// <see cref="DinoCrisis1.EmergencyBoxes"/> / <see cref="DinoCrisis1.PlugItemId"/>, and checks the
/// catalog's internal integrity (item ids ↔ names vs <c>items.json</c> allItems). Structure (room +
/// plug cost) is the engine contract; contents are FAQ-sourced reference (ITEM-RANDO-PLAN.md §7.4).
/// </summary>
public class EmergencyBoxTests
{
    private sealed record ContentRow(string id, string name, int count);
    private sealed record Contents(ContentRow[] easy, ContentRow[] normal, ContentRow[] hard, ContentRow[] veryHard);
    private sealed record BoxRow(string id, string name, string room, string? color, int plugs, Contents contents);
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

        // The JSON catalog and the mirrored DinoCrisis1.EmergencyBoxes must agree on (room, plug cost),
        // in the same order — so the two never drift.
        var expected = file.boxes.Select(b => (ParseId(b.room), b.plugs)).ToArray();
        var actual = game.EmergencyBoxes.Select(b => (b.RoomCode, b.PlugCost)).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EmergencyBoxCatalog_HasExpectedShape()
    {
        var file = LoadJson<BoxesFile>("emergency-boxes.json");
        // 17 boxes total (per the source FAQ). 12 carry a colour: the 8 reds (4 FAQ-labelled dual-box
        // reds + 4 unlabelled red singles identified from the EXE block ordering) + 3 labelled greens +
        // 1 labelled yellow. The remaining 5 single-box rooms are an unresolved green/yellow split (null).
        Assert.Equal(17, file.boxes.Length);
        Assert.Equal(8, file.boxes.Count(b => b.color == "red"));
        Assert.Equal(12, file.boxes.Count(b => b.color is not null));
        // Plug counts are positive and the total demand is the known vanilla figure.
        Assert.All(file.boxes, b => Assert.InRange(b.plugs, 1, 3));
        Assert.Equal(26, file.boxes.Sum(b => b.plugs));
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
