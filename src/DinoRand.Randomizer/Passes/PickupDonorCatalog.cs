using System.Buffers.Binary;
using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Passes;

/// <summary>One vanilla bespoke ground mesh usable as the donor for an item id.</summary>
public sealed record PickupDonor(RoomFile Room, int RoomCode, int MeshOffset, PickupMesh Mesh);

/// <summary>
/// Runtime-derived id→donor-mesh map for Lever B (PICKUP-GROUND-MODEL-FEASIBILITY.md "Lever B plan",
/// decision: no data file — the rooms are already parsed, so the map cannot go stale). A mesh is a
/// valid donor for id X when a vanilla record with <see cref="ItemRecord.OriginalItemId"/> X points
/// at it (shared "pile" meshes therefore serve their sharers — vanilla-faithful); preference is the
/// donor with the fewest co-pointing distinct ids (a solo mesh beats the pile), then deterministic
/// (room code, offset) order. A mesh that fails <see cref="PickupMeshFormat.TryParse"/> — including
/// one whose offset went stale under a script insertion, caught by the header self-pointer check —
/// disqualifies that donor, fail-closed.
/// </summary>
public static class PickupDonorCatalog
{
    public static IReadOnlyDictionary<int, PickupDonor> Build(
        IReadOnlyList<RoomFile> rooms, IReadOnlySet<int> relocatableIds)
    {
        // Candidate meshes: every vanilla record with a room-local (bespoke) ground-model pointer.
        // Vanilla state comes from ItemRecord.Raw, so earlier id relocation doesn't disturb the map.
        var candidates = new List<(int Id, RoomFile Room, int RoomCode, int Offset, int CoIds)>();
        foreach (var room in rooms)
        {
            if (room.Script is not { ParsedCleanly: true }) continue;
            int roomCode = ((room.Stage & 0xff) << 8) | (room.Room & 0xff);

            var idsByOffset = new Dictionary<int, HashSet<int>>();
            foreach (var it in room.Items)
            {
                if (it.IsEmptySlot || it.Raw.Length < ItemRecord.ModelPtrOffset + 4) continue;
                if (it.Raw[ItemRecord.DisplaySlotOffset] == ItemRecord.NoDisplaySlot) continue;
                uint ptr = BinaryPrimitives.ReadUInt32LittleEndian(
                    it.Raw.AsSpan(ItemRecord.ModelPtrOffset, 4));
                if (ptr is 0 or ItemRecord.GenericPanelModelPtr) continue;
                long off = ptr - RoomScript.PsxRdtBase;
                if (off < 0 || off >= room.RdtBuffer.Length) continue;
                idsByOffset.TryAdd((int)off, new HashSet<int>());
                idsByOffset[(int)off].Add(it.OriginalItemId);
            }

            foreach (var (off, ids) in idsByOffset)
                foreach (int id in ids)
                    if (relocatableIds.Contains(id))
                        candidates.Add((id, room, roomCode, off, ids.Count));
        }

        var catalog = new Dictionary<int, PickupDonor>();
        foreach (var group in candidates.GroupBy(c => c.Id))
            foreach (var c in group.OrderBy(c => c.CoIds).ThenBy(c => c.RoomCode).ThenBy(c => c.Offset))
                if (PickupMeshFormat.TryParse(c.Room.RdtBuffer, c.Offset, out var mesh))
                {
                    catalog[group.Key] = new PickupDonor(c.Room, c.RoomCode, c.Offset, mesh!);
                    break;
                }
        return catalog;
    }
}
