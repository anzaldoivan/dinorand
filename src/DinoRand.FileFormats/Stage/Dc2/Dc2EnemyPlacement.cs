namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// A single Dino Crisis 2 enemy spawn, as decoded from the room's <b>slot-5 SCD program</b>.
///
/// <para><b>UPDATED (2026-06-27, K47–K50).</b> The earlier "DC2 has no in-room spawn record" framing
/// is superseded: enemy placement <b>is</b> in the room file — the slot-5 spawn opcode <c>0x1a</c>
/// reads <see cref="Type"/>/<see cref="X"/>/<see cref="Y"/>/<see cref="Z"/>/<see cref="Slot"/> from a
/// block filled by <c>op 0x05</c> push-immediates, and the <b>mode-0 literal</b> operands are
/// room-file editable (docs/reference/dc2/spawn/ENEMY-SPAWNER-RE.md). The write primitive is
/// <see cref="Dc2SpawnEditor"/>; the authoritative per-operand blob offsets are pinned in
/// <c>data/dc2/spawn-graph.json</c> (tools/dc2_re/edit_spawn.py). A separate EXE-patch seam
/// (ctor model-base / vtable) remains the route for non-literal operands and for TYPE-0x10 generic
/// spawns whose model base is a global (docs/reference/dc2/spawn/EXE-SPAWN-SYSTEM.md) — deferred (D4).</para>
///
/// <para><see cref="Type"/> is the spawn TYPE byte (block+0x20 → ctor table; 1:1 with species only
/// for the hardcoded TYPEs, <c>0x10</c> = generic). Not yet populated by <see cref="Dc2RoomFile"/>
/// (its slot-5 parse is pending); consumers currently read the spawn graph directly.</para>
/// </summary>
public sealed class Dc2EnemyPlacement
{
    /// <summary>Spawn TYPE byte (slot-5 block+0x20) — the per-type ctor / species class
    /// (docs/reference/dc2/spawn/ENEMY-SPAWNER-RE.md §5). 1:1 with species for hardcoded TYPEs only.</summary>
    public int Type { get; set; }

    /// <summary>Actor slot index (slot-5 block+0x24) — the live actor-pool slot the spawn fills.</summary>
    public int Slot { get; set; }

    public short X { get; set; }
    public short Y { get; set; }
    public short Z { get; set; }

    /// <summary>Byte offset of this spawn's opcode inside the decompressed room blob (set by the
    /// reader). Per-operand editable offsets live in <c>data/dc2/spawn-graph.json</c>.</summary>
    public int FileOffset { get; init; }
}
