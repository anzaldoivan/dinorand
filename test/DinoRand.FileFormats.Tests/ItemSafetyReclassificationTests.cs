using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Maps;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// BioRand-style item-safety refinement (ITEM-RANDO-PLAN.md / docs/dc1/TRIGGER-DECODE.md): stop
/// blanket-pinning every flag-gated pickup <see cref="ItemPriority.Fixed"/>. A non-key item gated by a
/// <i>story</i> event flag (its gate controls only <i>when</i> it appears, not progression) is safe to
/// reroll in place, while relocation twins (the same id in &gt;1 record of a room) must share a single
/// assignment so the key shuffle can never desync them. Runtime-armed <c>0xff</c> slots, unresolved
/// triggers, and the escape-loadout block stay protected.
///
/// <para>These are the Phase-1 guarantees: the reclassification + link assertions are RED until the
/// generator/consumer changes land; the "stays protected" assertions are regression guards that must
/// remain GREEN throughout.</para>
/// </summary>
public class ItemSafetyReclassificationTests
{
    private static readonly DinoCrisis1 Game = new();

    // A record carrying a placement quad whose first corner is (x,z) — the position the item-priority
    // overlay matches on (X@+0x04, Z@+0x06). Id is irrelevant to the position match.
    private static ItemRecord QuadItem(int id, short x, short z)
    {
        var raw = new byte[44];
        raw[0x04] = (byte)x; raw[0x05] = (byte)(x >> 8);
        raw[0x06] = (byte)z; raw[0x07] = (byte)(z >> 8);
        return new ItemRecord { ItemId = id, OriginalItemId = id, Amount = 1, FileOffset = 0, Raw = raw };
    }

    /// <summary>The priority the <i>shipped</i> map.json overlay stamps onto a pickup at (x,z) in the
    /// given room — drives the reclassification + protection assertions against real data.</summary>
    private static ItemPriority ShippedPriorityAt(int roomCode, short x, short z)
    {
        var room = new RoomFile((roomCode >> 8) & 0xff, roomCode & 0xff);
        room.Items.Add(QuadItem(0x10, x, z));
        var g = RoomGraph.Build(new List<RoomFile> { room }, MapRequirements.LoadDefault());
        return g.Nodes.Single(n => n.Code == roomCode).Items.Single().Priority;
    }

    // --- Reclassification: non-key, story-flag-gated pickups become shuffleable (RED today) ---------

    [Fact]
    public void ShippedMap_HandgunSlides_0204_AreShuffleable_NotFixed()
    {
        // 0204 Lounge "Handgun Slides" (0x0e) is a weapon-part upgrade gated by event flag 2:11. The
        // gate controls only WHEN it appears, not progression, and the id is not a key item
        // (0x2b..0x6f), so it is safe to reroll in place — the slot's gate stays intact.
        Assert.Equal(ItemPriority.Normal, ShippedPriorityAt(0x0204, 2000, -3000));
    }

    [Fact]
    public void ShippedMap_ShotgunParts_0503_AreShuffleable_NotFixed()
    {
        // 0503 "Shotgun Parts" (0x0b), event-flag 2:11, non-key upgrade → shuffleable.
        Assert.Equal(ItemPriority.Normal, ShippedPriorityAt(0x0503, -10240, 4480));
    }

    [Fact]
    public void ShippedMap_ShotgunParts_0511_AreShuffleable_NotFixed()
    {
        // 0511 is the relocation room for the same Shotgun Parts (0x0b) — likewise shuffleable.
        Assert.Equal(ItemPriority.Normal, ShippedPriorityAt(0x0511, -10240, 4480));
    }

    // --- Protection guards: truly-immovable pickups stay Fixed (GREEN now, must stay GREEN) ---------

    [Fact]
    public void ShippedMap_RuntimeArmedSlot_0204_StaysFixed()
    {
        // 0xff runtime-armed slot — shuffle is a no-op / native fill; must never be collected.
        Assert.Equal(ItemPriority.Fixed, ShippedPriorityAt(0x0204, -3900, 4300));
    }

    [Fact]
    public void ShippedMap_UnresolvedTrigger_0503_StaysFixed()
    {
        // 0503 "Secret Disc" sits behind an unresolved trigger — it may never appear, so a needed item
        // placed here could softlock. Stays protected.
        Assert.Equal(ItemPriority.Fixed, ShippedPriorityAt(0x0503, -1792, 1792));
    }

    [Fact]
    public void ShippedMap_EscapeLoadoutBlock_0612_StaysFixed()
    {
        // The 11:10 escape-loadout block grant (Grenade Gun + Plug + Grenade Bullets) in the protected
        // finale rooms stays vanilla unless explicitly linked.
        Assert.Equal(ItemPriority.Fixed, ShippedPriorityAt(0x0612, -896, 0));
    }

    // --- Link: relocation twins share one assignment under the key shuffle (RED today) --------------

    /// <summary>A definition that reuses DC1's door-gate map but lets the test pick start/goal codes
    /// (clean-room mirror of the helper in <see cref="KeyItemPlacerTests"/>).</summary>
    private sealed class TestGame : GameDefinition
    {
        private static readonly DinoCrisis1 Inner = new();
        public int Start { get; init; }
        public int Goal { get; init; }
        public override string Id => "test";
        public override string DisplayName => "Test";
        public override string ExecutableName => "test.exe";
        public override IReadOnlySet<int> KeyItemIds => Inner.KeyItemIds;
        public override IReadOnlyList<ItemPoolEntry> ItemPool => Inner.ItemPool;
        public override IReadOnlySet<int> ScriptedEnemyRoomCodes => Inner.ScriptedEnemyRoomCodes;
        public override int StartRoomCode => Start;
        public override int GoalRoomCode => Goal;
        public override IReadOnlyCollection<int> KeyItemsForDoor(int doorType) => Inner.KeyItemsForDoor(doorType);
        public override IReadOnlyList<RoomFileRef> EnumerateRooms(string installDir) => Array.Empty<RoomFileRef>();
        public override string? GetDataDir(string installDir) => null;
    }

    private static RoomFile Room(int code, (int dest, int type)[] doors, int[] keys)
    {
        var room = new RoomFile((code >> 8) & 0xff, code & 0xff);
        foreach (var (dest, type) in doors)
            room.Doors.Add(new DoorRecord
            {
                TargetStage = (dest >> 8) & 0xff,
                TargetRoom = dest & 0xff,
                DoorType = type,
            });
        foreach (var k in keys)
            room.Items.Add(new ItemRecord { ItemId = k, OriginalItemId = k, Amount = 1, FileOffset = 0 });
        return room;
    }

    private static int Code(RoomFile r) => ((r.Stage & 0xff) << 8) | (r.Room & 0xff);

    /// <summary>
    /// A synthetic world whose room 0x0100 holds the Entrance Key (0x2e) in THREE records — a
    /// relocation twin — plus a second door key (BG Area Key 0x30) elsewhere, giving the key shuffle
    /// enough freedom to scatter a foreign key into one of the twin records (the desync this refinement
    /// must prevent). Returns the rooms (post-shuffle) and the progression log.
    /// </summary>
    private static (List<RoomFile> rooms, List<string> log) RunTwinKeyShuffle(int seed)
    {
        var game = new TestGame { Start = 0x010d, Goal = 0x0121 };
        var rooms = new List<RoomFile>
        {
            Room(0x010d, new[] { (0x0100, 0), (0x0120, 0x30) }, new[] { 0x30 }),
            Room(0x0100, new[] { (0x0121, 0x2e) }, new[] { 0x2e, 0x2e, 0x2e }), // twin ×3
            Room(0x0120, Array.Empty<(int, int)>(), Array.Empty<int>()),
            Room(0x0121, Array.Empty<(int, int)>(), Array.Empty<int>()),
        };

        // The itemLinks overlay binds the three Entrance-Key records in 0x0100 as one relocation twin
        // — exercising the full parse → stamp → honor pipeline (MapRequirements → NodeItem.Link →
        // ProgressionPass), exactly as the shipped map.json does for 0303 B1 Key Chip etc.
        const string overlayJson = """
        { "rooms": { "0100": { "itemLinks": [ { "id": "2e" } ] } } }
        """;
        var graph = RoomGraph.Build(rooms, MapRequirements.Parse(overlayJson));

        var log = new List<string>();
        var ctx = new RandomizationContext(game, rooms, graph, new Seed(seed),
                                           new RandomizerConfig { ShuffleKeyItems = true }, log.Add);
        new ProgressionPass().Apply(ctx);
        return (rooms, log);
    }

    [Fact]
    public void KeyShuffle_TwinKeyRecords_AlwaysStayInSync()
    {
        // After the shuffle, the three records that were originally the Entrance Key in room 0x0100
        // must all still hold the SAME id (one shared assignment) — never a split where one became a
        // foreign key. Today the records shuffle independently, so some seed desyncs.
        for (int seed = 0; seed < 50; seed++)
        {
            var (rooms, _) = RunTwinKeyShuffle(seed);
            var ids = rooms.Single(r => Code(r) == 0x0100).Items
                           .Where(i => i.OriginalItemId == 0x2e)
                           .Select(i => i.ItemId).Distinct().ToList();
            Assert.True(ids.Count == 1,
                $"twin Entrance-Key records desynced under key shuffle (seed {seed}): " +
                string.Join(",", ids.Select(x => $"0x{x:x}")));
        }
    }

    [Fact]
    public void KeyShuffle_TwinKeyWorld_StaysBeatable_AndConservesKeysLogically()
    {
        // Regression guard: linking the twin must not break solvability or lose a key. A relocation
        // twin is ONE logical slot (its 3 records mirror one assignment), so the conserved invariant is
        // the LOGICAL key set — both door keys stay obtainable, one per logical location — not the
        // physical record multiset (which the mirror collapses by design).
        for (int seed = 0; seed < 50; seed++)
        {
            var (rooms, log) = RunTwinKeyShuffle(seed);
            Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));

            int twinKey = rooms.Single(r => Code(r) == 0x0100).Items
                               .Where(i => i.OriginalItemId == 0x2e).Select(i => i.ItemId).Distinct().Single();
            int startKey = rooms.Single(r => Code(r) == 0x010d).Items.Single().ItemId;
            Assert.Equal(new[] { 0x2e, 0x30 }, new[] { twinKey, startKey }.OrderBy(x => x).ToArray());
        }
    }
}
