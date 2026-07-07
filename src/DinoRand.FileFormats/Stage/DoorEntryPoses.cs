using System.Collections.Generic;
using System.Linq;

namespace DinoRand.FileFormats.Stage;

/// <summary>A pose a door drops the player at on arrival into a room: position + facing.</summary>
public readonly record struct EntryPose(short X, short Y, short Z, short Rotation);

/// <summary>
/// Door entry poses — the positions/facings at which the game spawns the player when they walk through a
/// door into a room. Each is stored in the <i>source</i> door record (the door you leave through) as the
/// destination-room arrival pose (<see cref="DoorRecord.EntryX"/>…<see cref="DoorRecord.EntryD"/>; decoded
/// + CE-validated, docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.12/24 — the live entry-pose global <c>0x6D3E6C</c>
/// matches the door record byte-for-byte). Because the game materialises the player on these points, they
/// are guaranteed-walkable floor coordinates — the natural fallback spawn location for an injected enemy
/// when no explicit position is supplied.
/// </summary>
public static class DoorEntryPoses
{
    /// <summary>
    /// The <b>distinct</b> entry poses at which the player arrives in <paramref name="targetCode"/>
    /// (<c>stage&lt;&lt;8 | room</c>), gathered from every door — in any room — whose destination is that
    /// room. Distinct so paired/reciprocal doors that share an arrival point don't bias a random pick.
    /// Empty when no door leads into the room (e.g. an event-only or start room) — the caller should then
    /// fall back to another position source.
    /// </summary>
    public static IReadOnlyList<EntryPose> IntoRoom(IEnumerable<DoorRecord> allDoors, int targetCode) =>
        allDoors
            .Where(d => d.TargetCode == targetCode)
            .Select(d => new EntryPose(d.EntryX, d.EntryY, d.EntryZ, d.EntryD))
            .Distinct()
            .ToList();
}
