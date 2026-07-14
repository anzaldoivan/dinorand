using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Progression-logic tests (Phase 3 cont.13). The door-graph gate map and the flood-fill placer
/// are exercised on small synthetic graphs (no game files), and the whole model is validated on
/// the real install when <c>DINORAND_DC1_DIR</c> is set.
/// </summary>
public class KeyItemPlacerTests
{
    private static readonly DinoCrisis1 Game = new();

    private static DoorRecord Door(int destCode, int type, int lockId = 0) => new()
    {
        TargetStage = (destCode >> 8) & 0xff,
        TargetRoom = destCode & 0xff,
        DoorType = type,
        LockId = lockId,
    };

    private static void Link(RoomGraph g, int from, int to, int type, int lockId = 0)
    {
        var a = g.GetOrAdd((from >> 8) & 0xff, from & 0xff);
        var b = g.GetOrAdd((to >> 8) & 0xff, to & 0xff);
        a.Edges.Add(new RoomEdge(b, Door(to, type, lockId)));
    }

    private static KeyItemPlacer.Spot Spot(int roomCode, int originalId = 0x16) =>
        new(roomCode, new ItemRecord { ItemId = originalId, OriginalItemId = originalId, FileOffset = 0 });

    // --- Door-type -> required-key map (the cont.13 result) -------------------------------------

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x03)]
    [InlineData(0x05)]
    [InlineData(0xfd)]
    [InlineData(0xff)]
    public void DoorsThatAreNotKeyGated_RequireNoKey(int type)
        => Assert.Empty(Game.KeyItemsForDoor(type));

    [Fact]
    public void DirectKeyDoor_TypeByteIsTheItemId()
    {
        Assert.Equal(new[] { 0x2e }, Game.KeyItemsForDoor(0x2e)); // Entrance Key
        Assert.Equal(new[] { 0x30 }, Game.KeyItemsForDoor(0x30)); // BG Area Key
        Assert.Equal(new[] { 0x31 }, Game.KeyItemsForDoor(0x31)); // C.O. Area Key
    }

    [Fact]
    public void KeyCardLadder_HigherCardOpensLowerDoors()
    {
        Assert.Equal(new[] { 0x38, 0x39, 0x3a }, Game.KeyItemsForDoor(6)); // Lv C door: any card
        Assert.Equal(new[] { 0x39, 0x3a }, Game.KeyItemsForDoor(7));       // Lv B door: B or A
        Assert.Equal(new[] { 0x3a }, Game.KeyItemsForDoor(8));             // Lv A door: A only
    }

    // --- Reachable: a key gate blocks until the key is held -------------------------------------

    [Fact]
    public void Reachable_GatedDoorBlocksUntilKeyHeld()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);   // free
        Link(g, 0x0100, 0x0200, 0x2e);   // needs Entrance Key 0x2e

        var without = KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>());
        Assert.Contains(0x0100, without);
        Assert.DoesNotContain(0x0200, without);

        var with = KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x2e });
        Assert.Contains(0x0200, with);
    }

    [Fact]
    public void Reachable_KeyCardLevelIsHierarchical()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x08);   // needs a level-8 card (Lv A 0x3a)

        Assert.DoesNotContain(0x0100, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x39 }));
        Assert.Contains(0x0100, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x3a }));
    }

    // --- Group-9 reader/setter lock protocol (STATIC-SCD-RE.md cont.40) --------------------------
    // A type-1 door READS GetFlag(9,lock); it is crossable only after a same-lock type-2 door has been
    // traversed (which SetFlag(9,lock)s the latch). Type-2 self-latches (always crossable), type-3 is
    // free-to-cross (its producer is a SetFlag in the reader's own source room).

    [Fact]
    public void Reachable_Type1Door_StrandedProducer_IsNotCrossable()
    {
        // 0200 is reachable ONLY through the type-1 door, and its same-lock type-2 producer (0200->0100)
        // sits behind that same locked door → the latch can never be set → 0200 unreachable (softlock).
        // This is the door-rando strand case; a free-edge model wrongly reports 0200 reachable.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);          // free
        Link(g, 0x0100, 0x0200, 0x01, 0x0f);    // type-1 reader, lock 0x0f
        Link(g, 0x0200, 0x0100, 0x02, 0x0f);    // type-2 setter, lock 0x0f — but 0200 is unreachable

        var reach = KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>());
        Assert.Contains(0x0100, reach);
        Assert.DoesNotContain(0x0200, reach);
    }

    [Fact]
    public void Reachable_Type1Door_ReachableProducer_ShortcutOpens()
    {
        // 0200 is reachable another way; crossing its type-2 door (0200->0100) sets lock 0x0f, so the
        // type-1 shortcut 0100->0200 then opens. Everything reachable — the vanilla shortcut case.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);          // free
        Link(g, 0x010d, 0x0200, 0x00);          // independent path to 0200
        Link(g, 0x0100, 0x0200, 0x01, 0x0f);    // type-1 reader, lock 0x0f
        Link(g, 0x0200, 0x0100, 0x02, 0x0f);    // type-2 setter, lock 0x0f

        var reach = KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>());
        Assert.Contains(0x0100, reach);
        Assert.Contains(0x0200, reach);
    }

    [Fact]
    public void Reachable_Type2Door_SelfLatches_AlwaysCrossable()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x02, 0x0f);    // type-2 opens itself on traverse
        Assert.Contains(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>()));
    }

    [Fact]
    public void Reachable_Type3Door_InRoomProducer_IsFree()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x03, 0x0e);    // type-3 reader — producer is in-room, free once reached
        Assert.Contains(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>()));
    }

    /// <summary>
    /// The real 010B->010A case (2026-07-15, user-directed): a type-2 setter can itself carry an
    /// authored <see cref="Requirement"/> (map.json requiresRoom/requires), not just be structurally
    /// "free to cross" as <c>Reachable</c>'s latch pre-pass assumed. 0200 (mirrors 010B) is
    /// independently reachable, same as 0100 (mirrors 010A) — but its type-2 edge to 0100 is gated on
    /// visiting 0900. Without 0900, the setter's own AND-gate is unsatisfied, so the latch it would set
    /// must NOT be considered set, so the reciprocal type-1 reader (0100->0300, a room reachable ONLY
    /// through that shortcut) must stay locked. The bug: the pre-pass added every reachable node's
    /// type-2 <c>LockId</c>s to <c>latches</c> unconditionally ("source room reachable" alone), ignoring
    /// the setter edge's own <see cref="RoomEdge.Requires"/> / destination <see cref="RoomNode.Requires"/>
    /// — wrongly opening 0300 even though the setter itself was never actually traversable.
    /// </summary>
    [Fact]
    public void Reachable_Type2Setter_GatedByRequirement_LatchStaysClosedUntilSatisfied()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);            // start -> 0100, free (independent route, like 010A/0107)
        Link(g, 0x010d, 0x0200, 0x00);            // start -> 0200, free (independent route, like 010B/0108)
        Link(g, 0x0100, 0x0300, 0x01, 0x0f);      // type-1 reader, lock 0x0f — 0300 has NO other route in
        var setter = new RoomEdge(g.GetOrAdd(0x01, 0x00), Door(0x0100, 2, 0x0f))
        { Requires = Requirement.OfRooms(0x0900) };
        g.GetOrAdd(0x02, 0x00).Edges.Add(setter); // 0200 -> 0100, type-2 setter, gated on visiting 0900

        var reach = KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>());
        Assert.Contains(0x0100, reach);
        Assert.Contains(0x0200, reach);           // both endpoints independently reachable regardless
        Assert.DoesNotContain(0x0300, reach);     // but the shortcut must stay locked without 0900
    }

    // --- Verify: an existing placement is/ isn't solvable ---------------------------------------

    [Fact]
    public void Verify_SolvableChain_Succeeds()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);   // goal behind Entrance Key

        // Key sits in a room reachable before the gate.
        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0100] = new[] { 0x2e } };
        var res = KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys);
        Assert.True(res.Success);
    }

    [Fact]
    public void Verify_ReportsSpherePlaythrough()
    {
        // Two-gate chain: sphere 0 reaches 010d+0100 and collects the Entrance Key there; sphere 1
        // opens 0200 and collects the BG Area Key; sphere 2 opens the goal. The recorded SphereSteps
        // must mirror that order (DOCS-AUDIENCE-PLAN.md §5a — the spoiler playthrough projection).
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);   // needs Entrance Key
        Link(g, 0x0200, 0x0300, 0x30);   // needs BG Area Key

        var keys = new Dictionary<int, IReadOnlyList<int>>
        {
            [0x0100] = new[] { 0x2e },
            [0x0200] = new[] { 0x30 },
        };
        var res = KeyItemPlacer.Verify(g, Game, 0x010d, 0x0300, keys);

        Assert.True(res.Success);
        Assert.NotNull(res.Spheres);
        Assert.Equal(3, res.Spheres!.Count);
        Assert.Equal(new[] { 0, 1, 2 }, res.Spheres.Select(s => s.Index));
        Assert.Equal((0x2e, 0x0100), Assert.Single(res.Spheres[0].Collected));
        Assert.Equal((0x30, 0x0200), Assert.Single(res.Spheres[1].Collected));
        Assert.Empty(res.Spheres[2].Collected);                       // final sphere: goal opens, no new keys
        Assert.Equal(2, res.Spheres[0].RoomsReachable);               // 010d + 0100
        Assert.Equal(4, res.Spheres[2].RoomsReachable);               // all rooms
        Assert.True(res.Spheres[1].RoomsReachable == 3);
    }

    [Fact]
    public void Verify_GoalBehindStrandedType1Shortcut_IsRejected()
    {
        // The exact door-rando softlock DoorRandomizer.IsBeatable must catch: a shuffle strands the
        // goal behind a type-1 shortcut whose type-2 producer sits past that same locked door. The
        // beatability gate (Verify → the latch-aware Reachable) must report it unsolvable, so the pass
        // rerolls / falls back to vanilla rather than shipping it.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);          // free
        Link(g, 0x0100, 0x0200, 0x01, 0x0f);    // type-1 reader gates the goal, lock 0x0f
        Link(g, 0x0200, 0x0100, 0x02, 0x0f);    // its type-2 producer is behind the goal → never set

        var res = KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200,
                                       new Dictionary<int, IReadOnlyList<int>>());
        Assert.False(res.Success);
    }

    [Fact]
    public void Verify_KeyLockedBehindItsOwnGate_Fails()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);

        // The only copy of the key is behind the door it opens — unreachable.
        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0200] = new[] { 0x2e } };
        var res = KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys);
        Assert.False(res.Success);
    }

    // --- Strengthened guarantee: every in-world door key must stay collectable, not just the goal ----

    [Fact]
    public void Verify_NonGoalDoorKeyStrandedBehindItsOwnGate_IsRejected()
    {
        // start --free--> goal(0100); 0100 --(Entrance Key 0x2e)--> 0200 side branch. The ONLY Entrance
        // Key sits in 0200, behind the very door it opens. The goal does not need it, so goal-only
        // reachability calls this beatable — but the key is uncollectable. The randomizer guarantee is
        // BioRand's "every item reachable", so this must be rejected (the Entrance-Key-in-0606 class of
        // defect surfaced by the key-shuffle preview).
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);   // free path to the goal
        Link(g, 0x0100, 0x0200, 0x2e);   // optional branch gated by the Entrance Key

        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0200] = new[] { 0x2e } };
        var res = KeyItemPlacer.Verify(g, Game, 0x010d, 0x0100, keys);
        Assert.False(res.Success);
    }

    [Fact]
    public void Verify_NonGoalDoorKeyCollectableElsewhere_StaysBeatable()
    {
        // Same shape, but the Entrance Key sits in the freely-reachable goal room — collectable, so the
        // gated branch is openable. A door key that gates only a non-goal branch must NOT be rejected
        // merely for existing; only an *uncollectable* one is a defect.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);

        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0100] = new[] { 0x2e } };
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0100, keys).Success);
    }

    // --- The guard must cover ALL key items, not only door-TYPE keys (Phase 4 (i)) ------------------
    // A NON-door progression key item (0x2F B1 Room Key) gated by an authored EDGE item-requirement
    // (map.json door `requires`, not a door TYPE byte) is invisible to a door-type-only stranded check —
    // yet BioRand/Archipelago's invariant is "every location reachable". StrandedDoorKeys must generalize
    // to StrandedKeyItems so a non-door key stranded behind its own gate is rejected too.

    [Fact]
    public void Verify_NonDoorKeyItemStrandedBehindItsOwnGate_IsRejected()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);                                  // free path to the goal
        LinkReq(g, 0x010d, 0x0200, 0x00, Requirement.OfItems(0x2f));    // side branch gated by 0x2F (edge item gate)

        // The only copy of 0x2F sits in 0200, behind the very edge its possession opens. The goal (0100)
        // is reachable without it, so goal-only reachability calls this beatable — but 0x2F is an in-world,
        // uncollectable key item. Door-TYPE-only stranding misses it (the edge is type 0); the generalized
        // key-item stranded check must reject it.
        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0200] = new[] { 0x2f } };
        Assert.False(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0100, keys).Success);
    }

    [Fact]
    public void Verify_NonDoorKeyItemCollectableElsewhere_StaysBeatable()
    {
        // Same shape, but 0x2F also sits in the freely-reachable goal room — collectable, so the gated
        // branch opens. Guards against the generalized check over-rejecting a legitimately-placed non-door key.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        LinkReq(g, 0x010d, 0x0200, 0x00, Requirement.OfItems(0x2f));

        var keys = new Dictionary<int, IReadOnlyList<int>>
        {
            [0x0100] = new[] { 0x2f },   // collectable copy in the freely-reachable goal room
            [0x0200] = new[] { 0x2f },   // the gated copy — openable once the free one is held
        };
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0100, keys).Success);
    }

    [Fact]
    public void Verify_DoorKeyInUnreachableArea_IsNotRequired()
    {
        // A duplicate key in a disconnected room (the Operation Wipe-Out copies, e.g. 0A03) is outside
        // the playable world, so it must not be required — beatability is unaffected.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);
        g.GetOrAdd(0x0a, 0x03);          // disconnected room — no edge leads in

        var keys = new Dictionary<int, IReadOnlyList<int>>
        {
            [0x0100] = new[] { 0x2e },   // collectable copy in the playable world
            [0x0a03] = new[] { 0x2e },   // unreachable duplicate — ignored
        };
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0100, keys).Success);
    }

    // --- BG Area Key (0x30) un-pin safety: the self-sealing 0112 backyard topology -----------------
    // Faithful to map.json rooms 010C/0112/010E/0114/010F (GRAPH-LOGIC-PARITY-PLAN §8d/§8l): BOTH apertures
    // into the Backup-Generator passage gate on the BG Area Key 0x30 — the type-0x30 door 0112->010E and
    // the authored requires:[0x30] bypass 0112->0114 (the temp-passage for the self-sealing 0:57 swap). The
    // one-way 0:57 latch itself is not modeled, but since both apertures need 0x30 the region is uniformly
    // 0x30-gated, so a 0x30 stranded inside it is caught whichever aperture is live. This is the safety
    // property un-pinning 0x30 depends on; same mechanism as Verify_KeyLockedBehindItsOwnGate_Fails +
    // StrandedDoorKeys — asserted here on the actual door layout so a regression is unmissable.

    [Fact]
    public void Verify_BgAreaKeyBehindItsOwnSelfSealingPassage_IsRejected()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0112, 0x00);                                 // start -> backyard, free
        Link(g, 0x010d, 0x0606, 0x00);                                 // free path to the goal, off the BG region
        Link(g, 0x0112, 0x010e, 0x30);                                 // aperture A: type-0x30 BG-key door
        LinkReq(g, 0x0112, 0x0114, 0x00, Requirement.OfItems(0x30));   // aperture B: requires:[0x30] bypass
        Link(g, 0x010e, 0x010f, 0x00);                                 // passage -> generator room (interior)
        Link(g, 0x0114, 0x010f, 0x00);

        // 0x30's only copy sits in 010F — inside the region both apertures gate on 0x30. The goal (0606)
        // is reachable without the region, so goal-only reachability would call this beatable; only the
        // stranded-key check rejects it (0x30 is in-world under full keys yet uncollectable).
        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x010f] = new[] { 0x30 } };
        var res = KeyItemPlacer.Verify(g, Game, 0x010d, 0x0606, keys);
        Assert.False(res.Success);
    }

    [Fact]
    public void Verify_BgAreaKeyReachableBeforeItsPassage_StaysBeatable()
    {
        // Same topology, but 0x30 sits in the freely-reachable backyard 0112 (its vanilla-side spot), so it
        // is collectable before either aperture — beatable. Guards against the check over-rejecting a
        // legitimately-placed BG key.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0112, 0x00);
        Link(g, 0x010d, 0x0606, 0x00);
        Link(g, 0x0112, 0x010e, 0x30);
        LinkReq(g, 0x0112, 0x0114, 0x00, Requirement.OfItems(0x30));
        Link(g, 0x010e, 0x010f, 0x00);
        Link(g, 0x0114, 0x010f, 0x00);

        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0112] = new[] { 0x30 } };
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0606, keys).Success);
    }

    // --- Place: produce a solvable placement, or fail if over-constrained ------------------------

    [Fact]
    public void Place_GatedChain_PlacesKeyInReachableSpotAndReachesGoal()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);

        var spots = new[] { Spot(0x010d), Spot(0x0100) };
        var res = new KeyItemPlacer().Place(g, Game, 0x010d, 0x0200, spots,
                                            new[] { 0x2e }, new Seed(7));

        Assert.True(res.Success);
        var (spot, key) = Assert.Single(res.Placements);
        Assert.Equal(0x2e, key);
        // Placed in a room reachable empty-handed (so the gate can actually be opened).
        Assert.Contains(spot.RoomCode, new[] { 0x010d, 0x0100 });

        // The produced placement verifies as solvable.
        var keys = new Dictionary<int, IReadOnlyList<int>> { [spot.RoomCode] = new[] { key } };
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys).Success);
    }

    [Fact]
    public void Place_OverConstrained_OnlySpotBehindTheGate_Fails()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0200, 0x2e);   // goal directly behind the gate

        // The single spot is in the goal room, behind the very door its key must open.
        var spots = new[] { Spot(0x0200) };
        var res = new KeyItemPlacer().Place(g, Game, 0x010d, 0x0200, spots,
                                            new[] { 0x2e }, new Seed(1));
        Assert.False(res.Success);
    }

    [Fact]
    public void Place_CollectsFixedProgressionInLogic_ToReachAKeySpot()
    {
        // R is gated by a non-door progression item 0x2F that sits (fixed) in M; the only spot for the
        // door key 0x2E is in R. Place must COLLECT 0x2F once M is reachable (the §3(b) fix) — not assume
        // it from t=0 — then reach R and seat 0x2E, and the result must Verify. Before the fix Place held
        // nothing here (direct call, no assumedHeld) so R was unreachable and Place FAILED; the symmetric
        // collect makes it succeed AND agree with Verify.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);                                  // start -> M, free
        LinkReq(g, 0x0100, 0x0200, 0x00, Requirement.OfItems(0x2f));    // M -> R, gated by non-door 0x2F
        Link(g, 0x0100, 0x0300, 0x2e);                                  // M -> GOAL, door key 0x2E

        g.GetOrAdd(0x01, 0x00).Items.Add(new NodeItem(                  // fixed 0x2F lives in M
            new ItemRecord { ItemId = 0x2f, OriginalItemId = 0x2f }));

        var spots = new[] { Spot(0x0200) };                            // only door-key spot is in R
        var res = new KeyItemPlacer().Place(g, Game, 0x010d, 0x0300, spots, new[] { 0x2e }, new Seed(11));
        Assert.True(res.Success, string.Join("\n", res.Log));
        var (spot, key) = Assert.Single(res.Placements);
        Assert.Equal(0x2e, key);
        Assert.Equal(0x0200, spot.RoomCode);

        var keys = new Dictionary<int, IReadOnlyList<int>>
        {
            [0x0100] = new[] { 0x2f },   // fixed non-door key in M
            [0x0200] = new[] { 0x2e },   // door key Place seated in R
        };
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0300, keys).Success);
    }

    // --- Composite requirements: edge item gate + room-state gate (graph-logic parity) -----------

    private static void LinkReq(RoomGraph g, int from, int to, int type, Requirement req)
    {
        var a = g.GetOrAdd((from >> 8) & 0xff, from & 0xff);
        var b = g.GetOrAdd((to >> 8) & 0xff, to & 0xff);
        a.Edges.Add(new RoomEdge(b, Door(to, type)) { Requires = req });
    }

    [Fact]
    public void Reachable_EdgeItemRequirement_BlocksUntilAllHeld()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);                                  // free
        LinkReq(g, 0x0100, 0x0200, 0x00, Requirement.OfItems(0x40, 0x41)); // AND of two non-door items

        Assert.DoesNotContain(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x40 }));
        Assert.Contains(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int> { 0x40, 0x41 }));
    }

    [Fact]
    public void Reachable_RoomStateRequirement_ResolvesAtFixpoint()
    {
        // goal's room requires that 0x0100 has been reached; 0x0100 is reachable on a side branch,
        // so the fixpoint flood opens the goal even though its edge is visited before 0x0100.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);     // side branch makes 0x0100 reachable
        Link(g, 0x010d, 0x0200, 0x00);     // edge to goal is free...
        g.GetOrAdd(0x02, 0x00).Requires = Requirement.OfRooms(0x0100); // ...but the room needs 0x0100 visited

        Assert.Contains(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>()));
    }

    [Fact]
    public void Reachable_RoomStateRequirement_BlocksWhenRequiredRoomUnreachable()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0200, 0x00);
        g.GetOrAdd(0x02, 0x00).Requires = Requirement.OfRooms(0x0999); // a room with no path in

        Assert.DoesNotContain(0x0200, KeyItemPlacer.Reachable(g, Game, 0x010d, new HashSet<int>()));
    }

    // --- Item-guarded items: a pickup gated behind a held key ------------------------------------

    private static KeyItemPlacer.Spot GuardedSpot(int roomCode, int id, Requirement guard) =>
        new(roomCode, new ItemRecord { ItemId = id, OriginalItemId = id, FileOffset = 0 }, guard);

    [Fact]
    public void Place_GuardedSpot_IsAvoidedWhenItsGuardCannotBeHeld()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0200, 0x2e); // goal behind Entrance Key

        var guarded = GuardedSpot(0x010d, 0x16, Requirement.OfItems(0x40)); // 0x40 is never placed/held
        var open = Spot(0x010d);
        var res = new KeyItemPlacer().Place(g, Game, 0x010d, 0x0200,
                                            new[] { guarded, open }, new[] { 0x2e }, new Seed(3));

        Assert.True(res.Success);
        var (spot, _) = Assert.Single(res.Placements);
        Assert.Same(open.Record, spot.Record); // never seated into the guarded spot
    }

    [Fact]
    public void Verify_ItemGuardedKey_IsNotCollectableUntilGuardHeld()
    {
        // Room 0x0100 holds the goal key 0x2e, but that pickup is guarded by 0x40, which is nowhere —
        // so the key can never be collected and the goal is unreachable.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        Link(g, 0x0100, 0x0200, 0x2e);
        var node = g.GetOrAdd(0x01, 0x00);
        node.Items.Add(new NodeItem(new ItemRecord { ItemId = 0x2e, OriginalItemId = 0x2e })
        {
            Requires = Requirement.OfItems(0x40),
        });

        var keys = new Dictionary<int, IReadOnlyList<int>> { [0x0100] = new[] { 0x2e } };
        Assert.False(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys).Success);

        // Remove the guard → the key is collectable and the goal is reachable.
        node.Items[0].Requires = Requirement.None;
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys).Success);
    }

    // --- Phase 2 feasibility: Place seats an AND-gated (DDK disc PAIR) relocation progression-safely -
    // The 7 DDK edges gate on BOTH the Input AND the Code disc (map.json `requires` PAIR, AND semantics).
    // These prove the forward-fill placer ALREADY handles a pair gate with no Place change: FrontierKeys
    // surfaces edge.Requires.Items, so both discs are seated into currently-reachable spots before the
    // gate opens (PROGRESSION-KEY-RELOCATION-RESEARCH.md §5-6, feasibility verdict POSSIBLE).

    [Fact]
    public void Place_PairGatedEdge_SeatsBothDiscsBeforeGate_AndVerifies()
    {
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);                                       // start -> M, free
        LinkReq(g, 0x0100, 0x0200, 0x00, Requirement.OfItems(0x62, 0x69));   // M -> goal, needs Input+Code disc
        Link(g, 0x0100, 0x0300, 0x00);                                       // spare reachable room (spot space)

        var spots = new[] { Spot(0x010d), Spot(0x0100), Spot(0x0300) };
        var res = new KeyItemPlacer().Place(g, Game, 0x010d, 0x0200, spots,
                                            new[] { 0x62, 0x69 }, new Seed(7));
        Assert.True(res.Success, string.Join("\n", res.Log));
        Assert.Equal(2, res.Placements.Count);
        // Both discs land in rooms reachable before the gate (so the pair can actually be assembled).
        Assert.All(res.Placements, p => Assert.Contains(p.Spot.RoomCode, new[] { 0x010d, 0x0100, 0x0300 }));

        var keys = res.Placements.GroupBy(p => p.Spot.RoomCode)
            .ToDictionary(grp => grp.Key, grp => (IReadOnlyList<int>)grp.Select(p => p.KeyItem).ToList());
        Assert.True(KeyItemPlacer.Verify(g, Game, 0x010d, 0x0200, keys).Success);
    }

    [Fact]
    public void Place_PairGatedEdge_OneSpotForTwoDiscs_Fails()
    {
        // Negative control: the pair needs BOTH discs, but only ONE reachable spot exists — after seating
        // one disc the gate is still closed and there is nowhere to seat the second → over-constrained.
        var g = new RoomGraph();
        Link(g, 0x010d, 0x0100, 0x00);
        LinkReq(g, 0x0100, 0x0200, 0x00, Requirement.OfItems(0x62, 0x69));

        var spots = new[] { Spot(0x0100) };
        var res = new KeyItemPlacer().Place(g, Game, 0x010d, 0x0200, spots,
                                            new[] { 0x62, 0x69 }, new Seed(1));
        Assert.False(res.Success);
    }

    // --- Real install: the door-graph model is solvable on the shipped game ----------------------

    private static List<RoomFile>? LoadInstall()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        var refs = Game.EnumerateRooms(root);
        if (refs.Count == 0) return null;
        return refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
    }

    private static Dictionary<int, IReadOnlyList<int>> KeysByRoom(IEnumerable<RoomFile> rooms)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var room in rooms)
        {
            int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
            foreach (var item in room.Items)
                if (!item.IsEmptySlot && Game.KeyItemIds.Contains(item.ItemId))
                    (map.TryGetValue(code, out var l) ? l : map[code] = new List<int>()).Add(item.ItemId);
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
    }

    [Fact]
    public void RealInstall_VanillaPlacement_IsBeatable()
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var graph = RoomGraph.Build(rooms);
        var res = KeyItemPlacer.Verify(graph, Game, Game.StartRoomCode, Game.GoalRoomCode,
                                       KeysByRoom(rooms));
        Assert.True(res.Success, string.Join("\n", res.Log));
    }

    /// <summary>
    /// DDK Code Disc H (0x69) vanilla-spawns in 0100 (Locker Room); DDK Input Disc H (0x62) vanilla-spawns
    /// in 0103 (Management Office). DDK Code Disc E (0x6c) vanilla-spawns in 0104 (Strategy Room), which
    /// sits behind the 0102 fenceA laser fence — reachable only after visiting 0202 (Chief's Room,
    /// <c>data/dc1/placement-gates.md</c> "0102 A"), which itself requires holding BOTH DDK Input Disc H
    /// (0x62) AND DDK Code Disc H (0x69) to cross 0203→0202 — either one alone missing already blocks it
    /// (AND-gate). All three discs gate a modeled door edge, so <c>RelocateDdkDiscs</c> legally pools all
    /// of them as relocatable — swapping EITHER 0x62 or 0x69 into 0104 (displacing 0x6c out to the moved
    /// disc's vanilla room) puts that disc behind its own prerequisite: a genuine circular dependency
    /// (user-raised 2026-07-15; the specific rooms first suggested — 0511/0205/050B — turned out unrelated
    /// to this fence on inspection, but 0104 genuinely is; the user separately flagged that both discs of
    /// the pair, not just one, need covering). This locks in that the existing
    /// <see cref="KeyItemPlacer.Verify"/> reachability check already rejects EITHER swap — the sphere-based
    /// safety net that keeps <c>ProgressionPass</c> from ever shipping this seed. No map.json change: the
    /// fenceA gate was already correctly authored (unlike the 010D case above); this is a regression test
    /// for existing engine correctness, not a new gate.
    /// </summary>
    [Theory]
    [InlineData(0x69, 0x0100)] // DDK Code Disc H, vanilla 0100 Locker Room
    [InlineData(0x62, 0x0103)] // DDK Input Disc H, vanilla 0103 Management Office
    public void RealInstall_DdkDiscRelocation_RejectsEitherHDiscBehindItsOwnFenceGate(int disc, int vanillaRoom)
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip
        var graph = RoomGraph.Build(rooms, Game.Requirements);

        var keys = KeysByRoom(rooms).ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value));
        keys[vanillaRoom].Remove(disc);
        keys[vanillaRoom].Add(0x6c);
        keys[0x0104].Remove(0x6c);
        keys[0x0104].Add(disc);
        var dict = keys.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);

        var res = KeyItemPlacer.Verify(graph, Game, Game.StartRoomCode, Game.GoalRoomCode, dict);
        Assert.False(res.Success,
            $"DDK disc 0x{disc:x2} placed behind the 0202 fence it itself unlocks must be rejected as unsolvable");
    }

    /// <summary>
    /// The B3 endgame (and the goal 060d) is reachable ONLY through the heliport, i.e. the Entrance Key
    /// (0x2e) is a hard B3 requirement: 0107→0400 (Entrance Key) → 0401 → the heliport transport / the
    /// heliport-powered elevators are the only real descent. Every other apparent descent is an
    /// intra-facility shortcut that unlocks only after the heliport route:
    ///  • the Large Size Elevator B3 stop 060F→0600 requires the heliport (reqRoom 0401);
    ///  • the 0309 shuttle deep stops 0309→050B / 0309→0604 require Third Energy Control 050B, which is
    ///    itself reachable only via the heliport — so the facility elevator (0107→0113, DDK-N discs) can
    ///    reach B1 but NOT teleport into the B2/B3 hub.
    /// So dropping the Entrance Key must make the goal UNREACHABLE even while every other key (incl. the
    /// DDK-N pair 0x63/0x6a) is held. Dropping only the DDK-N discs must NOT — the heliport + Large
    /// Elevator still descend — which guards against over-gating. See the entrance-key / phantom-descent findings.
    /// </summary>
    [Fact]
    public void RealInstall_DeepFacility_RequiresTheEntranceKey()
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip
        var graph = RoomGraph.Build(rooms, Game.Requirements);

        var all = new HashSet<int>();
        foreach (var rm in rooms)
            if ((rm.Stage & 0xff) != 0x0A)
                foreach (var it in rm.Items)
                    if (!it.IsEmptySlot && Game.KeyItemIds.Contains(it.ItemId)) all.Add(it.ItemId);

        HashSet<int> Without(params int[] drop)
        { var h = new HashSet<int>(all); foreach (var d in drop) h.Remove(d); return h; }
        bool GoalReachable(HashSet<int> held) =>
            KeyItemPlacer.Reachable(graph, Game, Game.StartRoomCode, held).Contains(Game.GoalRoomCode);

        const int entranceKey = 0x2e, ddkInputN = 0x63, ddkCodeN = 0x6a;

        // Vanilla stays beatable, and dropping only the DDK-N discs still descends via the heliport +
        // Large Elevator (guards against over-gating the deep facility):
        Assert.True(GoalReachable(Without()), "all keys held: goal must be reachable");
        Assert.True(GoalReachable(Without(ddkInputN, ddkCodeN)),
            "goal must stay reachable via the heliport + Large Elevator when only the DDK-N discs are missing");

        // THE INVARIANT — the Entrance Key is a hard B3 requirement. Even holding every other key (incl.
        // the DDK-N pair), the goal must be UNREACHABLE without it: the facility elevator + 0309 shuttle
        // reach B1 but the B2/B3 deep stops require 050B, which is heliport-only. RED until the shuttle
        // deep stops are gated.
        Assert.False(GoalReachable(Without(entranceKey)),
            "goal must be UNREACHABLE without the Entrance Key — B3 is heliport-only; the 0309 shuttle deep " +
            "stops (0309→050B / 0309→0604) must not teleport into the B2/B3 hub without the heliport");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    public void RealInstall_ProgressionPass_LogsBeatable(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var log = new List<string>();
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                                           new Seed(seed), new RandomizerConfig(), log.Add);
        new ProgressionPass().Apply(ctx);

        Assert.Contains(log, l => l.Contains("goal") && l.Contains("reachable"));
        Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(2024)]
    public void RealInstall_FloodFillPlacesDoorKeys_ProducesBeatableLayout(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        // Bare door-TYPE graph: a Place PRIMITIVE check (does the flood-fill seat the door keys so the
        // goal is reachable under door-type gating). Overlay-graph shuffle coverage — where the fixed
        // non-door keys must be collected in logic — is the retargeted RealInstall_KeyShuffle_* tests.
        var graph = RoomGraph.Build(rooms);

        // Candidate spots: every real (non-empty) item record, tagged with its room.
        var spots = new List<KeyItemPlacer.Spot>();
        foreach (var room in rooms)
        {
            int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
            foreach (var item in room.Items)
                if (!item.IsEmptySlot)
                    spots.Add(new KeyItemPlacer.Spot(code, item));
        }

        // The keys the door graph actually gates on.
        var doorKeys = new[] { 0x2e, 0x30, 0x31, 0x3a };
        var res = new KeyItemPlacer().Place(graph, Game, Game.StartRoomCode, Game.GoalRoomCode,
                                            spots, doorKeys, new Seed(seed));
        Assert.True(res.Success, string.Join("\n", res.Log));

        // The placement the flood-fill produced is itself beatable.
        var keysByRoom = res.Placements
            .GroupBy(p => p.Spot.RoomCode)
            .ToDictionary(grp => grp.Key, grp => (IReadOnlyList<int>)grp.Select(p => p.KeyItem).ToList());
        Assert.True(KeyItemPlacer.Verify(graph, Game, Game.StartRoomCode, Game.GoalRoomCode, keysByRoom)
                        .Success);
    }

    // --- Key-item shuffle (ProgressionPass behind RandomizerConfig.ShuffleKeyItems) --------------

    private static readonly int[] DoorKeys = { 0x2e, 0x30, 0x31, 0x3a };

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

    /// <summary>A definition that reuses DC1's door-gate map but lets a test pick start/goal codes.</summary>
    private sealed class TestGame : GameDefinition
    {
        private static readonly DinoCrisis1 Inner = new();
        public int Start { get; init; }
        public int Goal { get; init; }
        public override string Id => "test";
        public override string DisplayName => "Test";
        public override string ExecutableName => "test.exe";
        public override IReadOnlySet<int> KeyItemIds => Inner.KeyItemIds;
        public override IReadOnlySet<int> OverlayRelocationKeyIds => Inner.OverlayRelocationKeyIds;
        public override IReadOnlyList<ItemPoolEntry> ItemPool => Inner.ItemPool;
        public override IReadOnlySet<int> ScriptedEnemyRoomCodes => Inner.ScriptedEnemyRoomCodes;
        public override int StartRoomCode => Start;
        public override int GoalRoomCode => Goal;
        public override IReadOnlyCollection<int> KeyItemsForDoor(int doorType) => Inner.KeyItemsForDoor(doorType);
        public override IReadOnlyList<RoomFileRef> EnumerateRooms(string installDir) => Array.Empty<RoomFileRef>();
        public override string? GetDataDir(string installDir) => null;
    }

    [Fact]
    public void ShuffleKeyItems_SyntheticWorld_StaysBeatableAndConservesKeys()
    {
        // start --free--> M ; start --(BG Area 0x30)--> GA ; M --(Entrance 0x2e)--> goal.
        var game = new TestGame { Start = 0x010d, Goal = 0x0121 };
        var rooms = new List<RoomFile>
        {
            Room(0x010d, new[] { (0x0100, 0), (0x0120, 0x30) }, new[] { 0x2e }),
            Room(0x0100, new[] { (0x0121, 0x2e) }, new[] { 0x30 }),
            Room(0x0120, Array.Empty<(int, int)>(), Array.Empty<int>()),
            Room(0x0121, Array.Empty<(int, int)>(), Array.Empty<int>()),
        };

        var log = new List<string>();
        var ctx = new RandomizationContext(game, rooms, RoomGraph.Build(rooms), new Seed(5),
                                           new RandomizerConfig { ShuffleKeyItems = true }, log.Add);
        new ProgressionPass().Apply(ctx);

        Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
        var keys = rooms.SelectMany(r => r.Items).Select(i => i.ItemId)
                        .Where(id => id is 0x2e or 0x30).OrderBy(x => x);
        Assert.Equal(new[] { 0x2e, 0x30 }, keys); // both keys still present, exactly once each
    }

    [Fact]
    public void ShuffleOff_LeavesKeysInTheirVanillaSpots()
    {
        var game = new TestGame { Start = 0x010d, Goal = 0x0121 };
        var rooms = new List<RoomFile>
        {
            Room(0x010d, new[] { (0x0100, 0) }, new[] { 0x2e }),
            Room(0x0100, new[] { (0x0121, 0x2e) }, new[] { 0x30 }),
            Room(0x0121, Array.Empty<(int, int)>(), Array.Empty<int>()),
        };

        var ctx = new RandomizationContext(game, rooms, RoomGraph.Build(rooms), new Seed(5),
                                           new RandomizerConfig(), _ => { }); // ShuffleKeyItems off
        new ProgressionPass().Apply(ctx);

        Assert.Equal(0x2e, rooms[0].Items[0].ItemId);
        Assert.Equal(0x30, rooms[1].Items[0].ItemId);
    }

    private static List<int> DoorKeyMultiset(IEnumerable<RoomFile> rooms) =>
        rooms.SelectMany(r => r.Items)
             .Where(i => !i.IsEmptySlot && DoorKeys.Contains(i.ItemId))
             .Select(i => i.ItemId).OrderBy(x => x).ToList();

    private static Dictionary<int, List<int>> DoorKeyPositions(IEnumerable<RoomFile> rooms)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var room in rooms)
        {
            int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
            foreach (var item in room.Items)
                if (!item.IsEmptySlot && DoorKeys.Contains(item.ItemId))
                    (map.TryGetValue(item.ItemId, out var l) ? l : map[item.ItemId] = new List<int>()).Add(code);
        }
        foreach (var l in map.Values) l.Sort();
        return map;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(123)]
    public void RealInstall_KeyShuffle_StaysBeatableAndConservesKeys(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return;

        var before = DoorKeyMultiset(rooms);
        var log = new List<string>();
        // OVERLAY graph (Game.Requirements) — the graph the runner actually builds
        // (RandomizerRunner.cs) and the one Verify gates on. The bare graph was the §8l blind spot:
        // it never exercised the map.json cross-key gates, so Place↔Verify could disagree unseen.
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                                           new Seed(seed),
                                           new RandomizerConfig { ShuffleKeyItems = true }, log.Add);
        new ProgressionPass().Apply(ctx);

        Assert.Contains(log, l => l.Contains("goal") && l.Contains("reachable"));
        Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
        Assert.Equal(before, DoorKeyMultiset(rooms)); // every key conserved, none created/lost
    }

    /// <summary>
    /// The symmetry guarantee (Phase 4 / GRAPH-LOGIC-PARITY §8l): on the OVERLAY graph, every seed the
    /// shipped key shuffle produces is beatable — i.e. whenever <see cref="KeyItemPlacer.Place"/> returns a
    /// layout, <see cref="KeyItemPlacer.Verify"/> accepts it (or the pass fell back to vanilla, which is
    /// sound). Before the Place-collects-fixed-progression fix this failed ~10/16 seeds (Place held the
    /// non-door items from t=0, seating keys behind gates Verify could not open). Companion:
    /// <see cref="RealInstall_KeyShuffle_ActuallyRelocatesAtLeastOneKey"/> proves it is not just always
    /// reverting to vanilla.
    /// </summary>
    [Fact]
    public void RealInstall_KeyShuffle_OverlayGraph_EverySeedIsPlaceVerifyAgreed()
    {
        if (LoadInstall() is null) return;
        for (int seed = 0; seed < 16; seed++)
        {
            var rooms = LoadInstall()!;
            var before = DoorKeyMultiset(rooms);
            var log = new List<string>();
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                                               new Seed(seed),
                                               new RandomizerConfig { ShuffleKeyItems = true }, log.Add);
            new ProgressionPass().Apply(ctx);

            Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
            Assert.Contains(log, l => l.Contains("goal") && l.Contains("reachable"));
            Assert.Equal(before, DoorKeyMultiset(rooms));
        }
    }

    [Fact]
    public void RealInstall_KeyShuffle_ActuallyRelocatesAtLeastOneKey()
    {
        var vanilla = LoadInstall();
        if (vanilla is null) return;
        var vanillaPos = DoorKeyPositions(vanilla);

        // Across a spread of seeds at least one must move a key off its vanilla spot.
        bool moved = false;
        for (int seed = 0; seed < 30 && !moved; seed++)
        {
            var rooms = LoadInstall()!;
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                                               new Seed(seed),
                                               new RandomizerConfig { ShuffleKeyItems = true }, _ => { });
            new ProgressionPass().Apply(ctx);
            moved = !PositionsEqual(vanillaPos, DoorKeyPositions(rooms));
        }
        Assert.True(moved, "key shuffle never changed any door-key position across 30 seeds");
    }

    private static bool PositionsEqual(Dictionary<int, List<int>> a, Dictionary<int, List<int>> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var l) && l.SequenceEqual(kv.Value));

    // --- Key-item scatter (RandomizerConfig.ShuffleKeyItemsIntoPickups; Phase 5) -----------------------

    private static List<int> AllItemMultiset(IEnumerable<RoomFile> rooms) =>
        rooms.SelectMany(r => r.Items).Where(i => !i.IsEmptySlot).Select(i => i.ItemId).OrderBy(x => x).ToList();

    /// <summary>Acceptance oracle: with scatter ON, BG Area Key 0x30 (a sphere-0 bootstrap key) lands
    /// only in a sphere-0 static spot — which live in rooms 010C / 010D (010C Med. Pak M or 0x30's own
    /// door-key slot, or 010D An. Aid; 0112 has no pickup) — and every seed stays Verify-beatable with the
    /// full item multiset conserved (a scattered key displaces a consumable, never loses/creates one).</summary>
    [Fact]
    public void RealInstall_Scatter_BgKeyLandsOnlyInSphere0StaticSpots_AndConservesItems()
    {
        if (LoadInstall() is null) return;
        for (int seed = 0; seed < 16; seed++)
        {
            var rooms = LoadInstall()!;
            var beforeAll = AllItemMultiset(rooms);
            var beforeDoorKeys = DoorKeyMultiset(rooms);
            var log = new List<string>();
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                new Seed(seed),
                new RandomizerConfig { ShuffleKeyItems = true, ShuffleKeyItemsIntoPickups = true }, log.Add);
            new ProgressionPass().Apply(ctx);

            Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
            Assert.Contains(log, l => l.Contains("goal") && l.Contains("reachable"));
            Assert.Equal(beforeDoorKeys, DoorKeyMultiset(rooms));          // door keys conserved
            Assert.Equal(beforeAll, AllItemMultiset(rooms));              // full item multiset conserved

            var bg = DoorKeyPositions(rooms).GetValueOrDefault(0x30) ?? new List<int>();
            Assert.All(bg, room => Assert.Contains(room, new[] { 0x010C, 0x010D }));
        }
    }

    /// <summary>The feature must actually widen the pool: across seeds, at least once a door key lands in
    /// a former ammo/health slot (a NON-door-key vanilla position) — otherwise scatter is a no-op.</summary>
    [Fact]
    public void RealInstall_Scatter_ActuallyPlacesAKeyInAConsumableSlot()
    {
        var vanilla = LoadInstall();
        if (vanilla is null) return;
        // Vanilla door-key rooms (where a door key normally sits).
        var vanillaKeyRooms = DoorKeyPositions(vanilla).SelectMany(kv => kv.Value).ToHashSet();

        bool scattered = false;
        for (int seed = 0; seed < 40 && !scattered; seed++)
        {
            var rooms = LoadInstall()!;
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                new Seed(seed),
                new RandomizerConfig { ShuffleKeyItems = true, ShuffleKeyItemsIntoPickups = true }, _ => { });
            new ProgressionPass().Apply(ctx);
            // A door key now sitting in a room that never vanilla-held one ⇒ it scattered into a pickup.
            scattered = DoorKeyPositions(rooms).SelectMany(kv => kv.Value).Any(r => !vanillaKeyRooms.Contains(r));
        }
        Assert.True(scattered, "scatter never placed a door key outside a vanilla door-key room across 40 seeds");
    }

    /// <summary>Scatter OFF is byte-identical to the door-key-only shuffle: every produced record state
    /// matches, so the new flag cannot change a door-key-only seed.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(123)]
    public void RealInstall_ScatterOff_IsByteIdenticalToDoorKeyOnlyShuffle(int seed)
    {
        if (LoadInstall() is null) return;

        var a = LoadInstall()!;
        new ProgressionPass().Apply(new RandomizationContext(Game, a, RoomGraph.Build(a, Game.Requirements),
            new Seed(seed), new RandomizerConfig { ShuffleKeyItems = true }, _ => { }));

        var b = LoadInstall()!;
        new ProgressionPass().Apply(new RandomizationContext(Game, b, RoomGraph.Build(b, Game.Requirements),
            new Seed(seed),
            new RandomizerConfig { ShuffleKeyItems = true, ShuffleKeyItemsIntoPickups = false }, _ => { }));

        Assert.Equal(AllItemMultiset(a), AllItemMultiset(b));
        Assert.Equal(DoorKeyPositions(a).ToDictionary(kv => kv.Key, kv => kv.Value),
                     DoorKeyPositions(b).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    // --- DDK disc relocation (RandomizerConfig.RelocateDdkDiscs; PROGRESSION-KEY-RELOCATION-RESEARCH) --
    // The DDK Input/Code discs (0x62–0x6f) gate the 7 disc-pair edges via the map.json overlay `requires`
    // (AND-gates), NOT the door TYPE byte, so they were never in the relocation pool. The opt-in flag adds
    // the overlay-`requires` progression keys to ShuffleDoorKeys; panel keys gate no edge so stay vanilla.

    private static readonly int[] DdkDiscs = Enumerable.Range(0x62, 0x6f - 0x62 + 1).ToArray();

    private static List<int> DdkMultiset(IEnumerable<RoomFile> rooms) =>
        rooms.SelectMany(r => r.Items)
             .Where(i => !i.IsEmptySlot && DdkDiscs.Contains(i.ItemId))
             .Select(i => i.ItemId).OrderBy(x => x).ToList();

    private static Dictionary<int, List<int>> DdkPositions(IEnumerable<RoomFile> rooms)
    {
        var map = new Dictionary<int, List<int>>();
        foreach (var room in rooms)
        {
            int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
            foreach (var item in room.Items)
                if (!item.IsEmptySlot && DdkDiscs.Contains(item.ItemId))
                    (map.TryGetValue(item.ItemId, out var l) ? l : map[item.ItemId] = new List<int>()).Add(code);
        }
        foreach (var l in map.Values) l.Sort();
        return map;
    }

    /// <summary>(i)+(iii) On the OVERLAY graph with the flag on, every seed stays Verify-beatable and every
    /// DDK disc is conserved (the 7 pair-gated edges stay solvable — a stranded pair would log UNSOLVABLE).
    /// Also asserts the full item multiset is conserved (a pure key permutation, no scatter).</summary>
    [Fact]
    public void RealInstall_RelocateDdkDiscs_EverySeedBeatableAndConservesDiscs()
    {
        if (LoadInstall() is null) return;
        for (int seed = 0; seed < 16; seed++)
        {
            var rooms = LoadInstall()!;
            var beforeDiscs = DdkMultiset(rooms);
            var beforeAll = AllItemMultiset(rooms);
            var log = new List<string>();
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                new Seed(seed),
                new RandomizerConfig { ShuffleKeyItems = true, RelocateDdkDiscs = true }, log.Add);
            new ProgressionPass().Apply(ctx);

            Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
            Assert.Contains(log, l => l.Contains("goal") && l.Contains("reachable"));
            Assert.Equal(beforeDiscs, DdkMultiset(rooms));   // every disc conserved
            Assert.Equal(beforeAll, AllItemMultiset(rooms)); // full multiset conserved (no scatter)
        }
    }

    /// <summary>A shuffled progression key must never land in a one-way ending sink
    /// (<see cref="DinoCrisis1.EndingZoneRoomCodes"/> — the hovercraft/port escape dead ends). Such a
    /// placement verifies "beatable" (the reachability engine can't see the no-return property) but is an
    /// uncollectable softlock. Full product config (shuffle + scatter + DDK) over the seed range that
    /// reproduced it: pre-fix, seed 2 seated the Entrance Key in 0x613 Port and ~1 seed in 3 seated some
    /// key there. See the ending-sink findings.</summary>
    [Fact]
    public void RealInstall_KeyShuffle_NeverSeatsKeyInEndingSink()
    {
        if (LoadInstall() is null) return;
        var sinks = Game.EndingZoneRoomCodes;
        Assert.NotEmpty(sinks); // guard against a silently-empty set making this vacuous
        for (int seed = 1; seed <= 50; seed++)
        {
            var rooms = LoadInstall()!;
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                new Seed(seed),
                new RandomizerConfig
                {
                    ShuffleKeyItems = true,
                    ShuffleKeyItemsIntoPickups = true,
                    RelocateDdkDiscs = true,
                }, _ => { });
            new ProgressionPass().Apply(ctx);

            foreach (var room in rooms)
            {
                int code = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
                if (!sinks.Contains(code)) continue;
                foreach (var item in room.Items)
                    // Only a key the shuffle RELOCATED here is a defect (ItemId != OriginalItemId); a
                    // vanilla Fixed key that always sat here — e.g. the optional Plug in 0x612 — ships
                    // beatable and is never moved, so it is not the softlock class.
                    Assert.False(!item.IsEmptySlot && Game.KeyItemIds.Contains(item.ItemId)
                                 && item.ItemId != item.OriginalItemId,
                        $"seed {seed}: relocated key 0x{item.ItemId:X2} seated in ending sink 0x{code:X3}");
            }
        }
    }

    /// <summary>(ii) Place↔Verify agree with DDK discs in the pool: whenever Place returns a layout, Verify
    /// accepts it (or the pass reverted to vanilla, which is sound) — never ships an unbeatable seed.</summary>
    [Fact]
    public void RealInstall_RelocateDdkDiscs_OverlayGraph_EverySeedIsPlaceVerifyAgreed()
    {
        if (LoadInstall() is null) return;
        for (int seed = 0; seed < 16; seed++)
        {
            var rooms = LoadInstall()!;
            var before = DdkMultiset(rooms);
            var log = new List<string>();
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                new Seed(seed),
                new RandomizerConfig { ShuffleKeyItems = true, RelocateDdkDiscs = true }, log.Add);
            new ProgressionPass().Apply(ctx);

            Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
            Assert.Contains(log, l => l.Contains("goal") && l.Contains("reachable"));
            Assert.Equal(before, DdkMultiset(rooms));
        }
    }

    /// <summary>(iv, movement) The flag must actually MOVE a DDK disc off its vanilla spot across seeds —
    /// otherwise the pool was never widened (this is the RED test: fails until ShuffleDoorKeys feeds the
    /// overlay-`requires` discs into the spots/keys/relocating set).</summary>
    [Fact]
    public void RealInstall_RelocateDdkDiscs_ActuallyRelocatesADisc()
    {
        var vanilla = LoadInstall();
        if (vanilla is null) return;
        var vanillaPos = DdkPositions(vanilla);

        bool moved = false;
        for (int seed = 0; seed < 30 && !moved; seed++)
        {
            var rooms = LoadInstall()!;
            var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms, Game.Requirements),
                new Seed(seed),
                new RandomizerConfig { ShuffleKeyItems = true, RelocateDdkDiscs = true }, _ => { });
            new ProgressionPass().Apply(ctx);
            moved = !PositionsEqual(vanillaPos, DdkPositions(rooms));
        }
        Assert.True(moved, "RelocateDdkDiscs never moved any DDK disc off its vanilla spot across 30 seeds");
    }

    /// <summary>(v) Flag OFF is byte-identical to the door-key-only shuffle: the default path cannot change
    /// — DDK discs stay in their vanilla spots and no other record moves differently.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(123)]
    public void RealInstall_RelocateDdkDiscsOff_IsByteIdenticalToDoorKeyOnlyShuffle(int seed)
    {
        if (LoadInstall() is null) return;

        var a = LoadInstall()!;
        new ProgressionPass().Apply(new RandomizationContext(Game, a, RoomGraph.Build(a, Game.Requirements),
            new Seed(seed), new RandomizerConfig { ShuffleKeyItems = true }, _ => { }));

        var b = LoadInstall()!;
        new ProgressionPass().Apply(new RandomizationContext(Game, b, RoomGraph.Build(b, Game.Requirements),
            new Seed(seed),
            new RandomizerConfig { ShuffleKeyItems = true, RelocateDdkDiscs = false }, _ => { }));

        Assert.Equal(AllItemMultiset(a), AllItemMultiset(b));
        Assert.Equal(DdkPositions(a).ToDictionary(kv => kv.Key, kv => kv.Value),
                     DdkPositions(b).ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    /// <summary>Fixed DDK records (0x62 Input H @0103, both 0x6b Code @0406) must NEVER move — they are
    /// pinned in map.json itemPriorities (flag-gated / twin / unresolved-trigger), so relocating them would
    /// risk an unprovable softlock. Their vanilla rooms must still hold them on every seed.</summary>
    [Fact]
    public void RealInstall_RelocateDdkDiscs_FixedDiscsStayVanilla()
    {
        if (LoadInstall() is null) return;
        for (int seed = 0; seed < 16; seed++)
        {
            var rooms = LoadInstall()!;
            new ProgressionPass().Apply(new RandomizationContext(Game, rooms,
                RoomGraph.Build(rooms, Game.Requirements), new Seed(seed),
                new RandomizerConfig { ShuffleKeyItems = true, RelocateDdkDiscs = true }, _ => { }));

            var pos = DdkPositions(rooms);
            Assert.Contains(0x0103, pos.GetValueOrDefault(0x62) ?? new List<int>()); // Input H pinned @0103
            Assert.Contains(0x0406, pos.GetValueOrDefault(0x6b) ?? new List<int>()); // Code disc pinned @0406
        }
    }

    /// <summary>B1 Room Key 0x2F also gates via the overlay <c>requires</c>, but is OUT OF SCOPE for
    /// RelocateDdkDiscs — the flag's band (<see cref="DinoCrisis1.OverlayRelocationKeyIds"/>) is the DDK
    /// discs only, not every overlay-`requires` key. It must stay in its vanilla room 0109 on every seed,
    /// locking the scope decision (PROGRESSION-KEY-RELOCATION-RESEARCH.md §5).</summary>
    [Fact]
    public void RealInstall_RelocateDdkDiscs_B1RoomKeyOutOfScope_StaysVanilla()
    {
        if (LoadInstall() is null) return;
        for (int seed = 0; seed < 16; seed++)
        {
            var rooms = LoadInstall()!;
            new ProgressionPass().Apply(new RandomizationContext(Game, rooms,
                RoomGraph.Build(rooms, Game.Requirements), new Seed(seed),
                new RandomizerConfig { ShuffleKeyItems = true, RelocateDdkDiscs = true }, _ => { }));
            int b1 = rooms.Where(r => (((r.Stage & 0xff) << 8) | (r.Room & 0xff)) == 0x0109)
                          .SelectMany(r => r.Items).Count(i => i.ItemId == 0x2f);
            Assert.True(b1 >= 1, $"B1 Room Key 0x2F left room 0109 (seed {seed}) — should be out of scope");
        }
    }

    // --- Endgame escape + elevator forge-chain gates (re-audit 2026-07-14; DC1-ELEVATOR-ID-CARD-GATE) ---
    //
    // The prior §8o/§8p item gates (0x36/0x3B on the elevator, 0x3B/0x4C/0x4D on the escape doors) were
    // REFUTED by static native tracing: the elevator floor-select is a native panel whose card reader's
    // output flags (0:55/0:56) are read by nothing, and the Third Energy overload sets no SCD flag the
    // escape doors read (both native/CE-only). So the goal-lock is the pre-existing Key Card A endgame
    // lock, and the elevator is gated the communication-room way — on the ROOM that forges the card /
    // restores power (a code-anchored requiresRoom producer, not a refuted item gate).

    /// <summary>
    /// Option 1 (re-audit): the only offline-verifiable goal-critical door key is Key Card Lv. A (0x3a) —
    /// removing it blocks the goal. The Kirk card 0x3B / Stabilizer 0x4C / Initializer 0x4D are NOT modeled
    /// gates (the overload is native, not an SCD/door flag), so removing any of them still reaches the goal.
    /// Guards against re-adding the guide-only §8o/§8p gates that native tracing refuted.
    /// </summary>
    [Fact]
    public void RealInstall_EndgameEscape_OnlyKeyCardAIsGoalCritical()
    {
        var rooms = LoadInstall();
        if (rooms is null) return;
        var graph = RoomGraph.Build(rooms, Game.Requirements);
        var all = new HashSet<int>(Game.KeyItemIds);

        var minusA = new HashSet<int>(all); minusA.Remove(0x3a);
        Assert.DoesNotContain(Game.GoalRoomCode,
            KeyItemPlacer.Reachable(graph, Game, Game.StartRoomCode, minusA));

        foreach (var notAGate in new[] { 0x3B, 0x4C, 0x4D }) // native/CE-only — not a modeled door gate
        {
            var minus = new HashSet<int>(all); minus.Remove(notAGate);
            Assert.Contains(Game.GoalRoomCode,
                KeyItemPlacer.Reachable(graph, Game, Game.StartRoomCode, minus));
        }
    }

    /// <summary>
    /// The facility elevator (0113) descent is gated the communication-room way — on the forge ROOM, not
    /// the refuted per-floor card item: B1 (0309) needs the F.C. Device 0x41 AND the Researcher rewrite room
    /// 010B; B2 (050B) and B3 (0604) need the endgame Kirk rewrite room 0506. The big/cargo elevator
    /// (0405→060F→030C/0600) needs the generator-power room 030B. All are reachability-inert (the lab keeps
    /// non-elevator entrances) but the free-bridge EDGE is removed — so this guards the edge requirement
    /// directly. The inter-floor edges are deliberately left to their door-record logic (gating them breaks
    /// the vanilla key ordering — measured).
    /// </summary>
    [Fact]
    public void RealInstall_ElevatorDescent_GatedByForgeAndPowerRooms()
    {
        var rooms = LoadInstall();
        if (rooms is null) return;
        var graph = RoomGraph.Build(rooms, Game.Requirements);

        AssertEdgeRequires(graph, 0x0113, 0x0309, items: new[] { 0x41 }, rooms: new[] { 0x010B });
        AssertEdgeRequires(graph, 0x0113, 0x050B, items: System.Array.Empty<int>(), rooms: new[] { 0x0506 });
        AssertEdgeRequires(graph, 0x0113, 0x0604, items: System.Array.Empty<int>(), rooms: new[] { 0x0506 });
        AssertEdgeRequires(graph, 0x0405, 0x060F, items: System.Array.Empty<int>(), rooms: new[] { 0x030B });
        AssertEdgeRequires(graph, 0x060F, 0x030C, items: System.Array.Empty<int>(), rooms: new[] { 0x030B });
        // 060F→0600 (Large Elevator B3 stop) also requires the heliport waypoint 0401: 030B alone is
        // reachable from the start (B1 Room Key), which left a phantom descent into the whole B3 endgame
        // that bypassed the Entrance Key. See RealInstall_DeepFacility_RequiresARealSurfaceDescent.
        AssertEdgeRequires(graph, 0x060F, 0x0600, items: System.Array.Empty<int>(), rooms: new[] { 0x030B, 0x0401 });
    }

    /// <summary>
    /// The 0309 "Hall B1" shuttle-car partition (STATIC-SCD-RE cont.64 §E / REGION-SCHEMA-PLAN §2): the
    /// generator-stairs / B1 side (west {0307,030A,030D}) is fenced off on foot from the boarding/elevator
    /// side (shuttle {0306,0113,050B,0604}). Without this split the room is atomic and the graph FLATTENS —
    /// descending to B1 by the stairs (010D→030B, BG Room B1 Key) freely reaches the elevator hall and the
    /// deep B2/B3 hub, bypassing the whole ~6-stage heliport/large-elevator progression (the free-bridge
    /// phantom, GRAPH-LOGIC-PARITY §8k). Asserts, on the real map.json: (a) 0309 IS node-split, and (b)
    /// holding ONLY the two descent keys (BG Room B1 Key 0x2F + BG Area Key 0x30) reaches B1 but NOT the
    /// B2/B3 shuttle side — you can only reach beyond B2/B3 through the heliport route. RED if the map.json
    /// 0309 nodeSplit is missing (the data was lost once; this guards it from vanishing again).
    /// </summary>
    [Fact]
    public void RealInstall_HallB1ShuttleSplit_GeneratorStairsCannotReachB2B3Hub()
    {
        var rooms = LoadInstall();
        if (rooms is null) return;
        var graph = RoomGraph.Build(rooms, Game.Requirements);

        // (a) 0309 is a real sub-region node-split, not an atomic room.
        var hub = graph.Nodes.Where(n => n.Code == 0x0309).ToList();
        Assert.Equal(2, hub.Count);

        // (b) With only the descent keys, the generator-stairs B1 side is reachable but the fenced-off
        // shuttle/elevator side and the deep B2/B3 hub are NOT (they need the heliport/large-elevator
        // progression). Atomic 0309 would wrongly reach them all via the free shuttle crossing.
        var descentKeys = new HashSet<int> { 0x2F, 0x30 };
        var reach = KeyItemPlacer.Reachable(graph, Game, Game.StartRoomCode, descentKeys);

        Assert.Contains(0x030B, reach);   // Backup Generator Room B1 — reached by the stairs
        Assert.Contains(0x0309, reach);   // Hall B1 (west side)
        Assert.Contains(0x0307, reach);   // Medical Room Hallway (west side)
        foreach (var beyond in new[] { 0x0113, 0x050B, 0x0604, 0x0506, 0x0509, 0x0508 })
            Assert.DoesNotContain(beyond, reach); // elevator hall + B2/B3 hub — fenced off, heliport-only
    }

    // Every edge src->dest must carry the required held items and visited rooms — no un-stamped bypass.
    private static void AssertEdgeRequires(RoomGraph graph, int src, int dest, int[] items, int[] rooms)
    {
        var edges = graph.Nodes.Where(n => n.Code == src)
            .SelectMany(n => n.Edges).Where(e => e.Door.OriginalTargetCode == dest).ToList();
        Assert.NotEmpty(edges);
        foreach (var e in edges)
        {
            foreach (var it in items) Assert.Contains(it, e.Requires.Items ?? System.Array.Empty<int>());
            foreach (var rm in rooms) Assert.Contains(rm, e.Requires.RoomsVisited ?? System.Array.Empty<int>());
        }
    }

    /// <summary>
    /// The self-seal invariant (Phase 4 (ii)): across a seed sweep with the key shuffle (and DDK-disc
    /// relocation) on, no key item is ever placed in a playable-world room that requires that same key to
    /// reach — i.e. never past its own dominating gate. Out-of-world Operation Wipe-Out duplicates (stage
    /// <c>0x0A</c>, unreachable even with every key) are excluded exactly as
    /// <see cref="KeyItemPlacer.Verify"/>'s StrandedKeyItems does. Standing guard on the whole overlay,
    /// including the re-audited elevator forge-chain + power gates.
    /// </summary>
    [Fact]
    public void RealInstall_KeyShuffle_NoKeyPlacedPastItsOwnDominatingGate()
    {
        if (LoadInstall() is null) return;
        for (int seed = 0; seed < 16; seed++)
        {
            var rooms = LoadInstall()!;
            new ProgressionPass().Apply(new RandomizationContext(Game, rooms,
                RoomGraph.Build(rooms, Game.Requirements), new Seed(seed),
                new RandomizerConfig { ShuffleKeyItems = true, RelocateDdkDiscs = true }, _ => { }));

            var graph = RoomGraph.Build(rooms, Game.Requirements);
            var pos = KeysByRoom(rooms);
            var all = new HashSet<int>(pos.Values.SelectMany(v => v));
            var world = KeyItemPlacer.Reachable(graph, Game, Game.StartRoomCode, all); // playable world

            foreach (var (room, keys) in pos)
            {
                if (!world.Contains(room)) continue;                 // out-of-world duplicate — never required
                foreach (var k in keys)
                {
                    var minusK = new HashSet<int>(all); minusK.Remove(k);
                    Assert.True(
                        KeyItemPlacer.Reachable(graph, Game, Game.StartRoomCode, minusK).Contains(room),
                        $"seed {seed}: key 0x{k:X2} sits in room 0x{room:X4}, which is unreachable without 0x{k:X2}");
                }
            }
        }
    }

    // --- Synthetic pair-gate (runs on CI, no game files) -----------------------------------------------

    /// <summary>Stamp one edge (src→dest) with an item AND-requirement, so a synthetic graph can express a
    /// DDK-style disc PAIR gate the raw door records cannot.</summary>
    private sealed class EdgeRequiresOverlay : IRequirementOverlay
    {
        private readonly int _src, _dest;
        private readonly Requirement _req;
        public EdgeRequiresOverlay(int src, int dest, Requirement req) { _src = src; _dest = dest; _req = req; }
        public void ApplyTo(RoomGraph graph)
        {
            foreach (var node in graph.Nodes)
                if (node.Code == _src)
                    foreach (var edge in node.Edges)
                        if (edge.Target.Code == _dest)
                            edge.Requires = _req;
        }
    }

    /// <summary>(iv) A synthetic pair-gated relocation via the full ProgressionPass path: the goal is gated
    /// by [Input 0x62 AND Code 0x69], both discs relocatable in reachable rooms. With the flag on the pass
    /// must relocate the pair, stay beatable, and conserve both discs (or revert to vanilla — also sound).
    /// Runs without game files, so CI exercises the pair path too.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(42)]
    [InlineData(2024)]
    public void RelocateDdkDiscs_SyntheticPairGate_RelocatesPairStaysBeatableAndConserves(int seed)
    {
        var game = new TestGame { Start = 0x010d, Goal = 0x0200 };
        var rooms = new List<RoomFile>
        {
            Room(0x010d, new[] { (0x0100, 0) }, new[] { 0x62 }),   // start, free -> M; holds Input disc
            Room(0x0100, new[] { (0x0200, 0) }, new[] { 0x69 }),   // M -> goal (overlay gates it); holds Code disc
            Room(0x0200, Array.Empty<(int, int)>(), Array.Empty<int>()),
        };
        var overlay = new EdgeRequiresOverlay(0x0100, 0x0200, Requirement.OfItems(0x62, 0x69));

        var before = DdkMultiset(rooms);
        var log = new List<string>();
        var ctx = new RandomizationContext(game, rooms, RoomGraph.Build(rooms, overlay), new Seed(seed),
            new RandomizerConfig { ShuffleKeyItems = true, RelocateDdkDiscs = true }, log.Add);
        new ProgressionPass().Apply(ctx);

        Assert.DoesNotContain(log, l => l.Contains("UNSOLVABLE") || l.Contains("WARNING"));
        Assert.Contains(log, l => l.Contains("goal") && l.Contains("reachable"));
        Assert.Equal(before, DdkMultiset(rooms));                  // both discs conserved
        // The pair was admitted to the pool (not "no door keys present"): the shuffle log names the relocation.
        Assert.Contains(log, l => l.Contains("relocated") && l.Contains("door key"));
    }

    /// <summary>Flag OFF on the same synthetic world leaves both discs in their vanilla rooms — the pair
    /// gates via the overlay, not a door TYPE, so the door-key-only shuffle never touches them.</summary>
    [Fact]
    public void RelocateDdkDiscsOff_SyntheticPairGate_LeavesDiscsVanilla()
    {
        var game = new TestGame { Start = 0x010d, Goal = 0x0200 };
        var rooms = new List<RoomFile>
        {
            Room(0x010d, new[] { (0x0100, 0) }, new[] { 0x62 }),
            Room(0x0100, new[] { (0x0200, 0) }, new[] { 0x69 }),
            Room(0x0200, Array.Empty<(int, int)>(), Array.Empty<int>()),
        };
        var overlay = new EdgeRequiresOverlay(0x0100, 0x0200, Requirement.OfItems(0x62, 0x69));

        var ctx = new RandomizationContext(game, rooms, RoomGraph.Build(rooms, overlay), new Seed(5),
            new RandomizerConfig { ShuffleKeyItems = true }, _ => { }); // RelocateDdkDiscs off
        new ProgressionPass().Apply(ctx);

        Assert.Equal(0x62, rooms[0].Items[0].ItemId); // Input disc still in 010d
        Assert.Equal(0x69, rooms[1].Items[0].ItemId); // Code disc still in 0100
    }
}
