using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Logic;

namespace DinoRand.Randomizer.Graph;

public sealed class RoomNode
{
    public RoomNode(int stage, int room, int regionIndex = 0, string? regionName = null)
    {
        Stage = stage;
        Room = room;
        RegionIndex = regionIndex;
        RegionName = regionName;
    }

    public int Stage { get; }
    public int Room { get; }

    /// <summary>Sub-room region index (REGION-SCHEMA-PLAN.md §2). <b>0 = the primary region</b> (or an
    /// atomic room's only node), so <see cref="NodeCode"/> == <see cref="Code"/> and every un-split room
    /// behaves byte-identically. A room with an intra-room, entry-direction partition (e.g. the 0309
    /// shuttle) splits into one node per region, indices 0..N.</summary>
    public int RegionIndex { get; }

    /// <summary>The region's authored name from <c>map.json</c> (e.g. <c>"west"</c>/<c>"shuttle"</c>), or
    /// <c>null</c> for an atomic room's single node. Diagnostic only.</summary>
    public string? RegionName { get; }

    public string Id => RegionIndex == 0 ? $"ST{Stage}:R{Room}" : $"ST{Stage}:R{Room}.{RegionName ?? RegionIndex.ToString()}";

    /// <summary>The room as an <c>SSRR</c> code (<c>stage&lt;&lt;8 | room</c>), matching
    /// <see cref="DoorRecord.TargetCode"/> and the room codes used as keys in
    /// <c>data/dc1/map.json</c> / <c>room-data.json</c>. Shared by every region node of a split room.</summary>
    public int Code => ((Stage & 0xff) << 8) | (Room & 0xff);

    /// <summary>The unique graph-node identity: <c>(RegionIndex &lt;&lt; 16) | Code</c>
    /// (REGION-SCHEMA-PLAN.md §2). For the primary region (index 0) this equals <see cref="Code"/>, so the
    /// reachability layer stays byte-identical for un-split rooms; the room is recovered anywhere with
    /// <c>NodeCode &amp; 0xFFFF</c>.</summary>
    public int NodeCode => (RegionIndex << 16) | Code;

    public List<RoomEdge> Edges { get; } = new();

    /// <summary>The item pickups physically in this room — the <i>same</i> <see cref="ItemRecord"/>
    /// instances the passes mutate, so an edit propagates without a copy (plan §4.2). Populated by
    /// <see cref="RoomGraph.Build"/>; door-target-only nodes (no room file) stay empty.</summary>
    public List<NodeItem> Items { get; } = new();

    /// <summary>Room-state gate for <i>entering</i> this room (the map.json <c>requiresRoom</c> case),
    /// AND-applied on top of every incoming edge's own gate. <see cref="Requirement.None"/> by
    /// default.</summary>
    public Requirement Requires { get; set; } = Requirement.None;
}

/// <summary>Randomization priority of a pickup (BioRand's <c>ItemPriority</c> subset, docs/decisions/cross/ITEM-RANDO-PLAN.md
/// §7.1). <see cref="Normal"/> participates fully; <see cref="Fixed"/> stays exactly vanilla (never
/// rerolled or pool-placed) — a per-item form of <see cref="Definitions.GameDefinition.ItemProtectedRoomCodes"/>.
/// Progression eligibility and hidden-spot policy are represented separately by the shared pickup
/// contract and key-placement planner.</summary>
public enum ItemPriority
{
    Normal,
    Fixed,
}

/// <summary>The pickup's decoded GROUND VISUAL class (STATIC-SCD-RE cont.72: record field pair
/// <c>rec+0x22</c> display-node slot / <c>rec+0x24</c> model pointer — never derived from the item
/// id). <see cref="GenericPanel"/> = the shared globally-resident blinking-panel model every ground
/// consumable shows (the implicit default; not emitted to map.json). <see cref="BespokeMesh"/> = a
/// room-local mesh of the VANILLA item (a relocated id still renders the old item's model).
/// <see cref="InteractionOnly"/> = no visual at all — collectable only by examining the spot.</summary>
public enum PickupVisual
{
    GenericPanel,
    BespokeMesh,
    InteractionOnly,
}

/// <summary>An item pickup attached to a <see cref="RoomNode"/>: the live record plus its optional
/// guard (the map.json item <c>requires</c> case — a pickup reachable only once the guard is held).</summary>
public sealed record NodeItem(ItemRecord Record, int RoomCode = 0)
{
    public PhysicalPickupId PhysicalId => new(RoomCode, Record.FileOffset);

    /// <summary>Explicit logical group identity. Null means this physical record is a singleton.</summary>
    public LogicalPickupId? LogicalId { get; set; }

    public PickupSourceClass Source { get; set; } = PickupSourceClass.Unknown;
    public bool Excluded { get; set; }
    public PickupPlacementClass AllowedPlacementClass { get; set; } = PickupPlacementClass.Ordinary;

    /// <summary>Immutable snapshot consumed by planners and output descriptions.</summary>
    public PickupLocationContract Location => PickupLocationContract.Snapshot(this);

    public Requirement Requires { get; set; } = Requirement.None;

    /// <summary>Randomization priority, stamped from the map.json item overlay (default
    /// <see cref="ItemPriority.Normal"/>). <see cref="ItemPriority.Fixed"/> ⇒ the item pass leaves it vanilla.</summary>
    public ItemPriority Priority { get; set; } = ItemPriority.Normal;

    /// <summary>True when this pickup is a legal key-item <b>scatter</b> target — a static ammo/health
    /// slot a shuffled door key may land in — stamped from the map.json <c>scatterTargets</c> overlay
    /// (default <c>false</c>). Consulted ONLY by the opt-in key-item scatter
    /// (<see cref="RandomizerConfig.ShuffleKeyItemsIntoPickups"/>); never read on the default path, so it
    /// cannot change flag-off output. docs/decisions/dc1/items/KEY-ITEM-SCATTER-DATA-AUDIT.md.</summary>
    public bool IsScatterTarget { get; set; }

    /// <summary>Relocation-twin link key, stamped from the map.json <c>itemLinks</c> overlay (default
    /// <c>null</c> = unlinked). Records in the same room sharing a non-null <see cref="Link"/> are the
    /// same physical pickup duplicated across script states (the game relocates one logical item across
    /// several records), so they must take ONE shared assignment — the key shuffle / item pass mirror
    /// the group's canonical record onto the rest, never desyncing or duplicating it. Membership is
    /// authored by stable record offsets; equal ids or coordinates never imply a link.</summary>
    public string? Link { get; set; }

    /// <summary>Ground-visual class of this spot, stamped from the map.json <c>itemVisuals</c> overlay
    /// (default <see cref="PickupVisual.GenericPanel"/> — the overlay emits only non-default classes).
    /// Consulted by the passes when <see cref="RandomizerConfig.AvoidHiddenPickupSpots"/> is on: weapons
    /// and parts avoid <see cref="PickupVisual.InteractionOnly"/> slots, and an interaction-only spot may
    /// only receive a key whose vanilla home was also interaction-only ("no worse than vanilla").</summary>
    public PickupVisual Visual { get; set; } = PickupVisual.GenericPanel;
}

/// <summary>A directed door from one room to another. <see cref="Requires"/> is the composite
/// item-AND gate authored on this door edge (the map.json door <c>requires</c> / puzzle-gate case),
/// AND-applied on top of the door-type key-set OR gate.</summary>
public sealed record RoomEdge(RoomNode Target, DoorRecord Door)
{
    public Requirement Requires { get; set; } = Requirement.None;
}
