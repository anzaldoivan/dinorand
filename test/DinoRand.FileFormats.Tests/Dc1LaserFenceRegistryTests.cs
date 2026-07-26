using System.Text.Json;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Source-data contract for <c>data/dc1/laser-fences.json</c>. The registry is the authored census from
/// the DC1 fence state-driver decode (placement-gates.md / STATIC-SCD-RE cont.62); it must not grow a
/// speculative 0105 prerequisite or silently lose a shared-flag controller.
/// </summary>
public sealed class Dc1LaserFenceRegistryTests
{
    private static JsonElement Fences()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DinoRand.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "data", "dc1", "laser-fences.json");
        Assert.True(File.Exists(path), $"registry not found: {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("fences").Clone();
    }

    private static JsonElement Fence(JsonElement fences, string id)
    {
        foreach (var fence in fences.EnumerateArray())
            if (fence.GetProperty("id").GetString() == id) return fence;
        Assert.Fail($"fence {id} absent from laser-fences.json");
        return default;
    }

    private static string[] Strings(JsonElement fence, string property)
        => fence.GetProperty(property).EnumerateArray().Select(v => v.GetString()!).ToArray();

    private static void AssertPrerequisites(
        JsonElement fence,
        string[] items,
        params (string Kind, string Item, string Room, string Flag)[] actions)
    {
        Assert.Equal(items, Strings(fence, "requiredItems"));
        var actual = fence.GetProperty("requiredActions").EnumerateArray().ToArray();
        Assert.Equal(actions.Length, actual.Length);
        for (int i = 0; i < actions.Length; i++)
        {
            Assert.Equal(actions[i].Kind, actual[i].GetProperty("kind").GetString());
            Assert.Equal(actions[i].Item, actual[i].GetProperty("item").GetString());
            Assert.Equal(actions[i].Room, actual[i].GetProperty("room").GetString());
            Assert.Equal(actions[i].Flag, actual[i].GetProperty("flag").GetString());
        }
    }

    [Fact]
    public void Registry_ContainsAllFourteenControllers_AndThirteenUniqueStateFlags()
    {
        var fences = Fences();
        Assert.Equal(14, fences.GetArrayLength());
        Assert.Equal(14, fences.EnumerateArray().Select(f => f.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(13, fences.EnumerateArray().Select(f => f.GetProperty("stateFlag").GetString()).Distinct().Count());
        Assert.Equal(11, fences.EnumerateArray().Select(f => f.GetProperty("room").GetString()).Distinct().Count());

        var expected = new Dictionary<string, (string Room, int Instance, string State, string Enable)>
        {
            ["0102A"] = ("0102", 12, "0:12", "0:240"), ["0102B"] = ("0102", 13, "0:13", "0:241"),
            ["0108"] = ("0108", 32, "0:32", "0:243"), ["010A"] = ("010A", 36, "0:36", "0:244"),
            ["0301A"] = ("0301", 78, "0:78", "0:245"), ["0301B"] = ("0301", 79, "0:79", "0:246"),
            ["0306A"] = ("0306", 98, "0:98", "0:247"), ["0306B"] = ("0306", 99, "0:99", "0:248"),
            ["030A"] = ("030A", 112, "0:112", "0:249"), ["030D"] = ("030D", 112, "0:112", "0:249"),
            ["0500"] = ("0500", 177, "0:177", "0:250"), ["0502"] = ("0502", 173, "0:173", "0:251"),
            ["0606"] = ("0606", 147, "0:147", "0:252"), ["0608"] = ("0608", 149, "0:149", "0:253")
        };
        foreach (var entry in fences.EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString()!;
            Assert.True(expected.TryGetValue(id, out var e), $"unexpected fence controller {id}");
            Assert.Equal(e.Room, entry.GetProperty("room").GetString());
            Assert.Equal(e.Instance, entry.GetProperty("instance").GetInt32());
            Assert.Equal(e.State, entry.GetProperty("stateFlag").GetString());
            Assert.Equal(e.Enable, entry.GetProperty("enableFlag").GetString());
        }
    }

    [Fact]
    public void Registry_RecordsDirectNormalEnableRequirements_AndDistinctPrerequisites()
    {
        var fences = Fences();

        Assert.Equal(new[] { "0106", "0202", "0107" }, Strings(Fence(fences, "0102A"), "requiredRooms"));
        Assert.Equal(new[] { "0106" }, Strings(Fence(fences, "0102B"), "requiredRooms"));
        Assert.Equal(new[] { "0106", "0202", "0107" }, Strings(Fence(fences, "0108"), "requiredRooms"));
        Assert.Equal(new[] { "0106", "0202", "0107" }, Strings(Fence(fences, "010A"), "requiredRooms"));
        Assert.Equal(new[] { "0406" }, Strings(Fence(fences, "030A"), "requiredRooms"));
        Assert.Equal(new[] { "0406" }, Strings(Fence(fences, "030D"), "requiredRooms"));
        foreach (var id in new[] { "0502", "0606", "0608" })
            Assert.Equal(new[] { "0505", "0608" }, Strings(Fence(fences, id), "requiredRooms"));

        foreach (var id in new[] { "0301A", "0301B", "0306A", "0306B", "0500" })
            Assert.Empty(Strings(Fence(fences, id), "requiredRooms"));

        Assert.DoesNotContain("0105", Strings(Fence(fences, "0102A"), "requiredRooms"));
        var ddkN = ("take", "0x63", "0202", "7:85");
        var expected = new Dictionary<string, (string[] Items, (string, string, string, string)[] Actions)>
        {
            ["0102A"] = (Array.Empty<string>(), new[] { ddkN }),
            ["0102B"] = (Array.Empty<string>(), Array.Empty<(string, string, string, string)>()),
            ["0108"] = (Array.Empty<string>(), new[] { ddkN }),
            ["010A"] = (Array.Empty<string>(), new[] { ddkN }),
            ["0301A"] = (Array.Empty<string>(), Array.Empty<(string, string, string, string)>()),
            ["0301B"] = (Array.Empty<string>(), Array.Empty<(string, string, string, string)>()),
            ["0306A"] = (Array.Empty<string>(), Array.Empty<(string, string, string, string)>()),
            ["0306B"] = (Array.Empty<string>(), Array.Empty<(string, string, string, string)>()),
            ["030A"] = (Array.Empty<string>(), Array.Empty<(string, string, string, string)>()),
            ["030D"] = (Array.Empty<string>(), Array.Empty<(string, string, string, string)>()),
            ["0500"] = (Array.Empty<string>(), Array.Empty<(string, string, string, string)>()),
            ["0502"] = (new[] { "0x38" }, Array.Empty<(string, string, string, string)>()),
            ["0606"] = (new[] { "0x38" }, Array.Empty<(string, string, string, string)>()),
            ["0608"] = (new[] { "0x38" }, Array.Empty<(string, string, string, string)>()),
        };

        Assert.Equal(14, expected.Count);
        foreach (var (id, prerequisites) in expected)
        {
            var actions = prerequisites.Actions
                .Select(a => (a.Item1, a.Item2, a.Item3, a.Item4))
                .ToArray();
            AssertPrerequisites(Fence(fences, id), prerequisites.Items, actions);
        }
    }
}
