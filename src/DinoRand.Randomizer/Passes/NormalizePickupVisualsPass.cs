using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Graph;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// DC1 Lever A (PICKUP-GROUND-MODEL-FEASIBILITY.md, "Design correction" 2026-07-17). Runs after
/// progression / item relocation and makes every relocated spot's ground visual represent the LANDED
/// item: a bespoke-mesh spot stops showing the old item's model no matter what landed; a hidden spot
/// gets a panel for key/weapon landings (consumables stay vanilla-invisible); a generic-panel spot is
/// marked only when Lever B will upgrade the landed id to its own donor model.
///
/// <para>The visual is a per-record field pair (<see cref="ItemRecord.DisplaySlotOffset"/> pool slot /
/// <see cref="ItemRecord.ModelPtrOffset"/> model pointer), never id-derived (STATIC-SCD-RE cont.72), so
/// normalization is a byte edit like the id. A bespoke-mesh spot already owns a display node, so it reuses
/// its slot and only repoints the model. An interaction-only spot has no node, so a <b>free</b> pool slot
/// is allocated, avoiding every op23-scenery and item slot already in the room, capped fail-closed at
/// <see cref="RoomScript.DisplaySlotPoolCap"/> (the pool's true capacity is CE-unmeasured); a room with no
/// free slot is left untouched — still vanilla-invisible, no worse than today. Records carrying the
/// deferred-visual flag (<c>rec+0x28≠0</c>) are skipped (their draw bit is cleared regardless).</para>
///
/// <para>Off by default (<see cref="RandomizerConfig.NormalizePickupVisuals"/>): the in-engine render is
/// gated on a human CE / in-game witness — static work proves byte structure, not rendering.</para>
/// </summary>
public sealed class NormalizePickupVisualsPass : IRandomizationPass
{
    public string Name => "Normalize pickup visuals";

    // Lever B builds on the Lever-A marking (its failure paths ARE the generic panel), so it
    // implies this pass even when NormalizePickupVisuals itself is off.
    public bool IsEnabled(RandomizerConfig config)
        => config.NormalizePickupVisuals || config.ImportPickupModels;

    // rec+0x28 (dword): nonzero clears the display node's draw bit (deferred visual, role uncertain,
    // cont.72). Normalizing the model would not render, so such a record is never normalized.
    private const int DeferredVisualOffset = 0x28;

    public void Apply(RandomizationContext context)
    {
        var game = context.Game;
        var relocatable = new HashSet<int>(game.KeyItemIds);
        relocatable.UnionWith(game.WeaponIds);
        relocatable.UnionWith(game.WeaponPartIds);

        // Ids Lever B will upgrade to their own donor model (design correction 2026-07-17): such a
        // record is marked even on a generic-panel spot — the import pass repoints it; its fail-closed
        // fallback rewrites the same generic values, byte-identical to not marking.
        var donorIds = context.Config.ImportPickupModels
            ? (IReadOnlySet<int>)PickupDonorCatalog.Build(context.Rooms, relocatable).Keys.ToHashSet()
            : new HashSet<int>();

        // Ground-visual class per record lives on the graph (map.json itemVisuals overlay).
        var visualOf = new Dictionary<ItemRecord, PickupVisual>();
        foreach (var node in context.Graph.Nodes)
            foreach (var ni in node.Items)
                visualOf[ni.Record] = ni.Visual;

        var spoiler = context.Spoiler.Section("Pickup visuals normalized", "Room", "Item", "Shown as");
        int normalized = 0, skipped = 0;

        foreach (var room in context.Rooms)
        {
            if (room.Script is not { ParsedCleanly: true }) continue;

            // The display-node pool slots this room already uses: op23 scenery + every item's own slot.
            var occupied = new HashSet<byte>(room.Script.SceneryDisplaySlots);
            foreach (var it in room.Items)
                if (!it.IsEmptySlot && it.DisplaySlot != ItemRecord.NoDisplaySlot)
                    occupied.Add(it.DisplaySlot);

            foreach (var it in room.Items)
            {
                if (it.IsEmptySlot || it.FileOffset < 0) continue;
                if (it.ItemId == it.OriginalItemId) continue;              // not relocated here
                var visual = visualOf.GetValueOrDefault(it, PickupVisual.GenericPanel);
                // The visual must represent the LANDED item (design correction 2026-07-17):
                // - bespoke spot: the old item's mesh is wrong for anything that landed, key or not;
                // - hidden spot: keys/weapons get a panel, consumables stay vanilla-invisible;
                // - generic spot: touched only when Lever B will upgrade the landed id to its own model.
                bool mark = visual switch
                {
                    PickupVisual.BespokeMesh => true,
                    PickupVisual.InteractionOnly => relocatable.Contains(it.ItemId),
                    _ => donorIds.Contains(it.ItemId),
                };
                if (!mark) continue;

                if (it.Raw.Length > DeferredVisualOffset + 3
                    && (it.Raw[DeferredVisualOffset] | it.Raw[DeferredVisualOffset + 1]
                        | it.Raw[DeferredVisualOffset + 2] | it.Raw[DeferredVisualOffset + 3]) != 0)
                {
                    skipped++;
                    context.Log($"[normalize-visuals] skip 0x{RoomCode(room):X4} id 0x{it.ItemId:x2}: deferred-visual (rec+0x28≠0)");
                    continue;
                }

                byte slot;
                if (it.DisplaySlot != ItemRecord.NoDisplaySlot)
                {
                    slot = it.DisplaySlot;   // reuse the existing node, just repoint the model
                }
                else
                {
                    var free = FirstFreeSlot(occupied);
                    if (free is null)
                    {
                        skipped++;
                        context.Log($"[normalize-visuals] skip 0x{RoomCode(room):X4} id 0x{it.ItemId:x2}: " +
                                    $"no free display slot below cap 0x{RoomScript.DisplaySlotPoolCap:X}");
                        continue;
                    }
                    slot = free.Value;
                    occupied.Add(slot);      // a second normalization in this room must not reuse it
                }

                it.NormalizeVisual = true;
                it.NormalizeDisplaySlot = slot;
                normalized++;
                // Donor-marked records are reported by the "Pickup models imported" section instead —
                // a "generic pickup" row here would be wrong for them (rare fail-closed import
                // fallbacks are logged, not spoilered).
                if (!donorIds.Contains(it.ItemId))
                    spoiler.AddRow(Spoiler.Dc1RoomNames.Describe(RoomCode(room)),
                                   Spoiler.Dc1ItemNames.NameOf(it.ItemId), "generic pickup");
            }
        }

        context.Log($"[normalize-visuals] normalized {normalized} pickup visual(s), skipped {skipped}");
        if (normalized > 0)
            spoiler.AddNote($"rewrote {normalized} mismatched pickup(s) to the generic pickup panel");
    }

    /// <summary>Lowest pool index in <c>[0, cap)</c> not already occupied, or null (fail-closed).</summary>
    private static byte? FirstFreeSlot(HashSet<byte> occupied)
    {
        for (byte i = 0; i < RoomScript.DisplaySlotPoolCap; i++)
            if (!occupied.Contains(i)) return i;
        return null;
    }

    private static int RoomCode(RoomFile room) => ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
}
