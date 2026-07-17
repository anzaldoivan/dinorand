using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// DC1 Lever B (PICKUP-GROUND-MODEL-FEASIBILITY.md "Lever B plan", STATIC-SCD-RE cont.73): give a
/// relocated key/weapon its own donor ground mesh instead of the Lever-A generic panel. For every
/// record <see cref="NormalizePickupVisualsPass"/> marked, if a donor mesh exists for the landed id
/// (<see cref="PickupDonorCatalog"/>): cut the donor's texture UV sub-rect + CLUT row, re-upload
/// them at a free VRAM column of the destination room (intra-page position kept, so UVs stay
/// valid), append the retargeted mesh blob at the end of the destination RDT, and point the
/// record's <see cref="ItemRecord.VisualModelPtr"/> at it. Every failure path (no donor, no free
/// VRAM, RDT ceiling) silently keeps the Lever-A generic panel — never worse than Lever A alone.
///
/// <para>Runs AFTER the enemy passes: they splice script bytes (<c>ScriptInjector.Insert</c>),
/// which would shift an already-appended mesh out from under the stored pointer. Off by default
/// (<see cref="RandomizerConfig.ImportPickupModels"/>, CLI <c>--pickup-ground-models</c>, no GUI
/// wiring): the in-engine render is gated on a human CE / in-game witness.</para>
/// </summary>
public sealed class PickupModelImportPass : IRandomizationPass
{
    public string Name => "Import pickup ground models";

    public bool IsEnabled(RandomizerConfig config) => config.ImportPickupModels;

    public void Apply(RandomizationContext context)
    {
        var game = context.Game;
        var relocatable = new HashSet<int>(game.KeyItemIds);
        relocatable.UnionWith(game.WeaponIds);
        relocatable.UnionWith(game.WeaponPartIds);

        var catalog = PickupDonorCatalog.Build(context.Rooms, relocatable);
        context.Log($"[pickup-models] donor catalog: {catalog.Count} id(s)");

        var spoiler = context.Spoiler.Section("Pickup models imported", "Room", "Item", "Donor room");
        int imported = 0, fellBack = 0;

        foreach (var room in context.Rooms)
        {
            if (room.Script is not { ParsedCleanly: true }) continue;

            // Donor meshes already appended to THIS room (two same-id landings share one import),
            // and the VRAM rects claimed so far (pre-staged by enemy imports + ours).
            var appended = new Dictionary<(int RoomCode, int Offset), uint>();
            var staged = new List<VramRect>(room.StagedTextureRects);

            foreach (var it in room.Items)
            {
                if (!it.NormalizeVisual) continue;                       // not marked by Lever A
                if (!catalog.TryGetValue(it.ItemId, out var donor)) continue; // no donor → generic panel

                if (appended.TryGetValue((donor.RoomCode, donor.MeshOffset), out uint existing))
                {
                    it.VisualModelPtr = existing;
                    imported++;
                    continue;
                }

                // 1. Texture travel: one sub-rect + CLUT per texref, all placed or none.
                var map = new Dictionary<(ushort, ushort), (ushort, ushort)>();
                var entries = new List<PackageRepacker.NewEntry>();
                var placedRects = new List<VramRect>();
                bool ok = true;
                foreach (var tr in donor.Mesh.Texrefs)
                {
                    if (!PickupModelImporter.TryExtractTexture(donor.Room.OriginalBytes, tr, out var cut)
                        || !PickupModelImporter.TryPlace(room.OriginalBytes,
                                                         staged.Concat(placedRects).ToList(), cut!,
                                                         out var placed))
                    {
                        ok = false;
                        break;
                    }
                    map[(tr.Tpage, tr.Clut)] = (placed!.Tpage, placed.Clut);
                    entries.Add(new(GianEntryType.Lzss2, placed.TexRect, Lzss.Compress(cut!.TexPixels)));
                    entries.Add(new(GianEntryType.Palette, placed.ClutRect, cut.ClutPixels));
                    placedRects.Add(placed.TexRect);
                    placedRects.Add(placed.ClutRect);
                }
                if (!ok)
                {
                    fellBack++;
                    context.Log($"[pickup-models] fallback 0x{RoomCode(room):X4} id 0x{it.ItemId:x2}: no VRAM home");
                    continue;                                            // VisualModelPtr stays generic
                }

                // 2. Mesh transfer: rebase for the end-append home, then append (ceiling-checked).
                var blob = PickupMeshFormat.ExtractBlob(donor.Room.RdtBuffer, donor.MeshOffset);
                PickupMeshFormat.RebaseAndRetarget(
                    blob, RoomScript.PsxRdtBase + (uint)room.RdtBuffer.Length, map);
                if (!room.TryAppendPickupMesh(blob, out uint ptr))
                {
                    fellBack++;
                    context.Log($"[pickup-models] fallback 0x{RoomCode(room):X4} id 0x{it.ItemId:x2}: RDT ceiling");
                    continue;
                }
                foreach (var e in entries) room.StageTextureEntry(e);
                staged.AddRange(placedRects);
                appended[(donor.RoomCode, donor.MeshOffset)] = ptr;

                it.VisualModelPtr = ptr;
                imported++;
                spoiler.AddRow(Spoiler.Dc1RoomNames.Describe(RoomCode(room)),
                               Spoiler.Dc1ItemNames.NameOf(it.ItemId),
                               Spoiler.Dc1RoomNames.Describe(donor.RoomCode));
            }
        }

        context.Log($"[pickup-models] imported {imported} donor mesh(es), {fellBack} generic-panel fallback(s)");
        if (imported > 0)
            spoiler.AddNote($"{imported} relocated pickup(s) show their own model (donor mesh + texture imported)");
    }

    private static int RoomCode(RoomFile room) => ((room.Stage & 0xff) << 8) | (room.Room & 0xff);
}
