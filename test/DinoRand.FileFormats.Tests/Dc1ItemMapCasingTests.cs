using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the <c>scripts/gen_item_map.py</c> case-sensitivity fix (STATIC-SCD-RE cont.67 /
/// KEY-ITEM-SCATTER-DATA-AUDIT §5). The generator historically resolved a room's <c>itemPriorities</c>
/// key with <c>rid.lower()</c>, which never matches map.json's UPPERCASE hex-letter keys (010C, 030C,
/// …) — so those rooms silently lost their computed Fixed pins. The fix resolves the key
/// case-insensitively, and its single intentional default-output change is <b>030C's An. Darts M
/// (0x13 @ -7700,6900)</b>: it is co-located at one placement quad with the <c>unresolved-trigger</c>
/// ID Card "Communicator" (0x35), so the position pin now protects the shuffleable dart ammo beside it
/// and the item pass leaves it exactly vanilla instead of rerolling it. Real-install-gated
/// (<c>DINORAND_DC1_DIR</c>); CI skips (the <c>gen_item_map.py --check</c> gate guards there).
/// </summary>
public class Dc1ItemMapCasingTests
{
    private static readonly DinoCrisis1 Game = new();
    private const int Room030C = 0x030C;
    private const short DartsX = -7700, DartsZ = 6900;
    private const int AnDartsM = 0x13;

    private static List<RoomFile>? LoadInstall()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return null;
        return refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
    }

    private static int CodeOf(RoomFile r) => ((r.Stage & 0xff) << 8) | (r.Room & 0xff);

    private static (short X, short Z)? Pos(ItemRecord r) =>
        r.Raw is { Length: >= 0x08 }
            ? ((short)(r.Raw[0x04] | r.Raw[0x05] << 8), (short)(r.Raw[0x06] | r.Raw[0x07] << 8))
            : null;

    /// <summary>A room whose 3-nibble code carries a hex letter A–F (010C, 030C, …) — exactly the
    /// rooms the buggy <c>rid.lower()</c> resolution skipped.</summary>
    private static bool IsHexLetterRoom(int code) => code.ToString("X4").Any(c => c > '9');

    // --- Behavioral: the item pass now leaves 030C's An. Darts M exactly vanilla -------------------

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(2024)]
    public void ItemPass_LeavesRoom030CAnDartsM_ExactlyVanilla(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var graph = RoomGraph.Build(rooms, Game.Requirements);
        // 030C must be in the reachable world under full door keys, else the pass never touches it and
        // this test would pass vacuously.
        var reach = KeyItemPlacer.Reachable(graph, Game, Game.StartRoomCode, new HashSet<int>(Game.KeyItemIds));
        Assert.Contains(Room030C, reach);

        var room030c = rooms.Single(r => CodeOf(r) == Room030C);
        var darts = room030c.Items
            .Where(i => i.OriginalItemId == AnDartsM && Pos(i) is var p && p is (DartsX, DartsZ))
            .ToList();
        Assert.NotEmpty(darts); // the vanilla An. Darts M pickup is present at its quad

        var ctx = new RandomizationContext(Game, rooms, graph, new Seed(seed),
                                           new RandomizerConfig(), _ => { }); // default: RandomizeItems + replace
        new ItemRandomizer().Apply(ctx);

        // The pass actually rerolled the reachable world (guards against a vacuous pass no-op).
        Assert.Contains(rooms.SelectMany(r => r.Items), i => i.ItemId != i.OriginalItemId);

        // …but 030C's An. Darts M is Fixed-pinned, so it stayed exactly its vanilla item.
        foreach (var d in darts)
            Assert.Equal(AnDartsM, d.ItemId);
    }

    // --- Structural regression guard: ONLY 030C's An. Darts M changed ------------------------------

    /// <summary>The precise, RNG-free statement of "only one pickup's default output changed": among the
    /// reachable pickups the item pass would otherwise reroll (non-empty, non-key, not in a protected
    /// room), the ones the fix newly stamps <see cref="ItemPriority.Fixed"/> in a hex-letter room are
    /// EXACTLY 030C's An. Darts M. Any other hex-letter shuffleable pickup gaining a pin — the thing the
    /// byte-identity mandate forbids — trips <see cref="Assert.Single{T}"/>.</summary>
    [Fact]
    public void FixedShuffleablePins_InHexLetterRooms_AreExactlyRoom030CAnDartsM()
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var graph = RoomGraph.Build(rooms, Game.Requirements);
        var reach = KeyItemPlacer.Reachable(graph, Game, Game.StartRoomCode, new HashSet<int>(Game.KeyItemIds));

        var pinned = new List<(int Room, int Id, short X, short Z)>();
        foreach (var node in graph.Nodes)
        {
            if (!reach.Contains(node.Code) || !IsHexLetterRoom(node.Code)) continue;
            if (Game.ItemProtectedRoomCodes.Contains(node.Code)) continue; // whole room skipped by the pass
            foreach (var ni in node.Items)
            {
                var r = ni.Record;
                if (r.IsEmptySlot || Game.KeyItemIds.Contains(r.ItemId)) continue; // never rerolled anyway
                if (ni.Priority != ItemPriority.Fixed) continue;
                var (x, z) = Pos(r) ?? default;
                pinned.Add((node.Code, r.OriginalItemId, x, z));
            }
        }

        Assert.Equal((Room030C, AnDartsM, DartsX, DartsZ), Assert.Single(pinned));
    }
}
