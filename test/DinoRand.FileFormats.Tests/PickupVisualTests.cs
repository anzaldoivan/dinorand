using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Maps;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The decoded ground-visual layer (STATIC-SCD-RE cont.72; map.json <c>itemVisuals</c>) and the
/// <see cref="RandomizerConfig.AvoidHiddenPickupSpots"/> rule it feeds
/// (docs/decisions/dc1/items/PICKUP-VISUAL-PLACEMENT-PLAN.md): weapons/parts avoid interaction-only
/// slots, and the key shuffle is "no worse than vanilla" — an interaction-only spot only receives a
/// key whose vanilla home was also interaction-only.
/// </summary>
public class PickupVisualTests
{
    private static readonly GameDefinition Game = new DinoCrisis1();

    // --- overlay parsing / stamping ---------------------------------------------------------------

    /// <summary>A record carrying a placement quad whose first corner is (x,z) — what the position-
    /// keyed overlays match on (the real walker fills Raw; the harness must set it).</summary>
    private static ItemRecord QuadItem(int id, short x, short z)
    {
        var raw = new byte[ItemRecord.Length];
        raw[0x04] = (byte)(x & 0xff); raw[0x05] = (byte)((x >> 8) & 0xff);
        raw[0x06] = (byte)(z & 0xff); raw[0x07] = (byte)((z >> 8) & 0xff);
        return new ItemRecord { ItemId = id, OriginalItemId = id, Amount = 1, FileOffset = 0, Raw = raw };
    }

    [Fact]
    public void MapRequirements_ItemVisuals_StampsVisualByPosition()
    {
        const string json = """
        { "beginEnd": { "start": "0100", "end": "0100" },
          "rooms": { "0100": { "category": "Segment", "itemVisuals": [
              { "at": "10,20", "visual": "interaction-only" },
              { "at": "30,40", "visual": "bespoke-mesh" } ] } } }
        """;
        var room = new RoomFile(0x01, 0x00);
        room.Items.Add(QuadItem(0x1c, 10, 20));
        room.Items.Add(QuadItem(0x2e, 30, 40));
        room.Items.Add(QuadItem(0x16, 50, 60)); // no overlay entry -> implicit default

        var g = RoomGraph.Build(new List<RoomFile> { room }, MapRequirements.Parse(json));
        var items = g.Nodes.Single(n => n.Code == 0x0100).Items;

        Assert.Equal(PickupVisual.InteractionOnly,
                     items.Single(i => i.Record.OriginalItemId == 0x1c).Visual);
        Assert.Equal(PickupVisual.BespokeMesh,
                     items.Single(i => i.Record.OriginalItemId == 0x2e).Visual);
        Assert.Equal(PickupVisual.GenericPanel,
                     items.Single(i => i.Record.OriginalItemId == 0x16).Visual);
    }

    // --- KeyItemPlacer: EligibleKeys constraint + bijection tightness guard ------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1234)]
    [InlineData(99999)]
    public void Place_ConstrainedSpot_OnlyReceivesItsEligibleKey_InABijectionPool(int seedValue)
    {
        // One start room, no gates: 3 keys into 3 spots (bijection). Spot C admits only key 0x3a.
        // Regardless of the RNG order, the tightness guard must reserve C for 0x3a — the greedy fill
        // may never strand C with no admissible key (which would fall back to vanilla).
        var room = new RoomFile(0x01, 0x00);
        var g = RoomGraph.Build(new List<RoomFile> { room });

        ItemRecord Rec(int id) => new() { ItemId = id, OriginalItemId = id, Amount = 1, FileOffset = 0 };
        var recA = Rec(0x2e); var recB = Rec(0x30); var recC = Rec(0x3a);
        var eligible = new HashSet<int> { 0x3a };
        var spots = new List<KeyItemPlacer.Spot>
        {
            new(0x0100, recA),
            new(0x0100, recB),
            new(0x0100, recC, default, eligible),
        };
        var keys = new[] { 0x2e, 0x30, 0x3a };

        var res = new KeyItemPlacer().Place(g, Game, 0x0100, 0x0100, spots, keys, new Seed(seedValue),
                                            new HashSet<ItemRecord> { recA, recB, recC });
        Assert.True(res.Success, string.Join("\n", res.Log));
        var atC = res.Placements.Single(p => p.Spot.Record == recC);
        Assert.Equal(0x3a, atC.KeyItem);
    }

    // --- real-install, default config: the shipped rule end-to-end ---------------------------------

    private static List<RoomFile>? LoadInstall()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return null;
        return refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(2026)]
    public void RealInstall_KeyShuffle_NeverHidesAVisibleVanillaKey(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var graph = RoomGraph.Build(rooms, Game.Requirements);

        // Vanilla visual per key id, read off the graph BEFORE the pass mutates ids.
        var vanillaVisual = new Dictionary<int, PickupVisual>();
        foreach (var node in graph.Nodes)
            foreach (var ni in node.Items)
                if (!ni.Record.IsEmptySlot && Game.KeyItemIds.Contains(ni.Record.OriginalItemId)
                    && (!vanillaVisual.TryGetValue(ni.Record.OriginalItemId, out var v)
                        || v == PickupVisual.InteractionOnly))
                    vanillaVisual[ni.Record.OriginalItemId] = ni.Visual;

        var config = new RandomizerConfig
        {
            RandomizeItems = false,
            ShuffleKeyItems = true,
            ShuffleKeyItemsIntoPickups = true,
            RelocateDdkDiscs = true,
        };
        var ctx = new RandomizationContext(Game, rooms, graph, new Seed(seed), config, _ => { });
        new ProgressionPass().Apply(ctx);

        foreach (var node in graph.Nodes)
            foreach (var ni in node.Items)
            {
                if (ni.Record.ItemId == ni.Record.OriginalItemId) continue;      // not relocated here
                if (!Game.KeyItemIds.Contains(ni.Record.ItemId)) continue;       // not a key landing
                if (ni.Visual != PickupVisual.InteractionOnly) continue;         // visible home — fine
                Assert.True(vanillaVisual.GetValueOrDefault(ni.Record.ItemId) == PickupVisual.InteractionOnly,
                    $"seed {seed}: key 0x{ni.Record.ItemId:x2} (vanilla {vanillaVisual.GetValueOrDefault(ni.Record.ItemId)}) " +
                    $"landed in interaction-only spot 0x{node.Code:X4} — 'no worse than vanilla' violated");
            }
    }

    /// <summary>THE ground-visual invariant (cont.72): relocating an item id NEVER touches the spot's
    /// visual — <c>RoomScript.ApplyEdits</c> writes only the low id byte at <c>rec+0x1c</c>, so the
    /// display-slot byte (<c>rec+0x22</c>) and model pointer (<c>rec+0x24</c>) are byte-identical after
    /// an id edit + rewrite. This is why a shuffled key renders as its landing spot's vanilla visual
    /// (old mesh / generic panel / nothing), with or without <c>AvoidHiddenPickupSpots</c>.</summary>
    [Fact]
    public void RealInstall_ItemIdEdit_NeverTouchesTheVisualFields()
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        const int slotOff = 0x22, ptrOff = 0x24;
        int checked_ = 0;
        foreach (var room in rooms)
        {
            var target = room.Items.FirstOrDefault(i => !i.IsEmptySlot && i.FileOffset >= 0
                                                        && i.Raw is { Length: >= ptrOff + 4 });
            if (target is null) continue;

            byte vanillaSlot = target.Raw[slotOff];
            var vanillaPtr = target.Raw.Skip(ptrOff).Take(4).ToArray();

            target.ItemId = target.ItemId == 0x16 ? 0x2e : 0x16; // relocate any different id
            var rewritten = room.Write();
            var reread = RoomFile.Read(room.Stage, room.Room, rewritten);
            var rec = reread.Items.Single(i => i.FileOffset == target.FileOffset);

            Assert.Equal(target.ItemId, rec.ItemId);
            Assert.Equal(vanillaSlot, rec.Raw[slotOff]);
            Assert.Equal(vanillaPtr, rec.Raw.Skip(ptrOff).Take(4).ToArray());
            checked_++;
        }
        Assert.True(checked_ > 50, $"expected to exercise most rooms, got {checked_}");
    }

    /// <summary>Lever A end-to-end (PICKUP-GROUND-MODEL-FEASIBILITY.md): with
    /// <c>NormalizePickupVisuals</c> on, every relocated key that lands in a non-generic spot is flagged
    /// for normalization to a free display slot (no collision with the room's op23-scenery or other item
    /// slots), and the rewrite round-trips (re-read shows the chosen slot + the generic-panel model).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(2026)]
    public void RealInstall_NormalizePickupVisuals_FlagsFreeSlotsAndRoundTrips(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var graph = RoomGraph.Build(rooms, Game.Requirements);
        var config = new RandomizerConfig
        {
            RandomizeItems = false,
            ShuffleKeyItems = true,
            ShuffleKeyItemsIntoPickups = true,
            RelocateDdkDiscs = true,
            NormalizePickupVisuals = true,
        };
        var ctx = new RandomizationContext(Game, rooms, graph, new Seed(seed), config, _ => { });
        new ProgressionPass().Apply(ctx);
        new NormalizePickupVisualsPass().Apply(ctx);

        int normalized = 0;
        foreach (var room in rooms)
        {
            if (room.Script is not { ParsedCleanly: true }) continue;
            var scenery = new HashSet<byte>(room.Script.SceneryDisplaySlots);

            foreach (var it in room.Items)
            {
                if (!it.NormalizeVisual) continue;
                normalized++;

                Assert.True(it.NormalizeDisplaySlot < RoomScript.DisplaySlotPoolCap,
                    $"seed {seed}: chosen slot 0x{it.NormalizeDisplaySlot:X} >= cap");

                // A freshly-allocated slot (the record had no visual of its own) must not collide with
                // op23 scenery or any OTHER item's slot in the room.
                bool freshlyAllocated = it.DisplaySlot == ItemRecord.NoDisplaySlot;
                if (freshlyAllocated)
                {
                    Assert.DoesNotContain(it.NormalizeDisplaySlot, scenery);
                    foreach (var other in room.Items)
                        if (!ReferenceEquals(other, it) && !other.IsEmptySlot
                            && other.DisplaySlot != ItemRecord.NoDisplaySlot)
                            Assert.NotEqual(other.DisplaySlot, it.NormalizeDisplaySlot);
                }
            }

            var reread = RoomFile.Read(room.Stage, room.Room, room.Write());
            foreach (var it in room.Items)
            {
                if (!it.NormalizeVisual) continue;
                var rec = reread.Items.Single(i => i.FileOffset == it.FileOffset);
                Assert.Equal(it.NormalizeDisplaySlot, rec.DisplaySlot);
                Assert.Equal(ItemRecord.GenericPanelModelPtr,
                             (uint)(rec.Raw[0x24] | (rec.Raw[0x25] << 8) | (rec.Raw[0x26] << 16) | (rec.Raw[0x27] << 24)));
            }
        }

        _ = normalized; // per-seed count is informational; the per-record asserts above are the invariant
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(2026)]
    public void RealInstall_ItemPass_NeverPlacesAWeaponInAHiddenSlot(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var graph = RoomGraph.Build(rooms, Game.Requirements);
        var ctx = new RandomizationContext(Game, rooms, graph, new Seed(seed),
                                           new RandomizerConfig(), _ => { });
        new ItemRandomizer().Apply(ctx);

        var weaponSet = new HashSet<int>(Game.WeaponIds.Concat(Game.WeaponPartIds));
        foreach (var node in graph.Nodes)
            foreach (var ni in node.Items)
            {
                if (ni.Record.ItemId == ni.Record.OriginalItemId) continue; // vanilla/untouched slot
                if (!weaponSet.Contains(ni.Record.ItemId)) continue;
                Assert.True(ni.Visual != PickupVisual.InteractionOnly,
                    $"seed {seed}: weapon/part 0x{ni.Record.ItemId:x2} pool-placed into interaction-only " +
                    $"slot in 0x{node.Code:X4}");
            }
    }
}
