using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// The variant/palette fallback for choreography-excluded rooms (gated by
/// <see cref="RandomizerConfig.Dc1CutsceneSafeEnemies"/>, default off). Rooms in the derived
/// choreography census (<see cref="Definitions.GameDefinition.ChoreographyRoomCodes"/>) are skipped
/// by the (model, motion) permute and the cross-species import because a script drives their enemy
/// slots through authored waypoint behaviors (STATIC-SCD-RE cont.49/59). The one lever that is
/// provably orthogonal to choreography is the room's <b>type-2 palette entry</b> (cont.51: the
/// "Blue Raptor" is nothing but a recoloured 512-byte CLUT row; live-witnessed cont.57 row 6), so
/// this pass gives those rooms visual variety instead: copy the species' palette from a seeded
/// random donor room via <see cref="TextureImporter.CopySpeciesPalette"/>.
///
/// <para>Runs after every room-record-mutating pass (its output override freezes the serialized
/// bytes); donors whose palette entry doesn't match (different rect/size) are skipped, and a donor
/// with an identical palette is an intentional no-op ("vanilla tint" stays in the draw pool).</para>
/// </summary>
public sealed class Dc1CutscenePalettePass : IRandomizationPass
{
    public string Name => "dc1-cutscene-palette";

    public bool IsEnabled(RandomizerConfig config)
        => config.RandomizeEnemies && config.Dc1CutsceneSafeEnemies;

    public void Apply(RandomizationContext context)
    {
        var flagged = context.Game.ChoreographyRoomCodes;
        if (flagged.Count == 0) return;

        var rng = context.Seed.RngFor(Name);
        var corpus = context.AllRooms().ToList();
        var spoiler = context.Spoiler.Section("Enemies (DC1 cutscene-room palette tint)", "Room", "Change");

        int tinted = 0, skipped = 0;
        foreach (var room in corpus)
        {
            int code = room.Stage * 0x100 + room.Room;
            if (!flagged.Contains(code)) continue;

            var rec = room.Enemies.FirstOrDefault(e => e.IsRandomizableDino);
            if (rec is null) { skipped++; continue; }

            ushort clut;
            try { clut = TextureImporter.ReadModelTextureCodes(room.RdtBuffer, rec.OriginalModelPtr).Clut; }
            catch (Exception) { skipped++; continue; }

            // Seeded donor draw: every other room placing the same species (cont.51 — the species'
            // CLUT code/rect is room-invariant, so any of them can donate its palette row).
            var donors = corpus.Where(d => !ReferenceEquals(d, room)
                                           && d.Enemies.Any(e => e.IsRandomizableDino && e.Species == rec.Species))
                               .ToList();
            Shuffle(donors, rng);

            var target = context.TryGetRoomOutput(room, out var overridden) ? overridden : room.Write();
            byte[]? patched = null;
            RoomFile? donorUsed = null;
            foreach (var donor in donors)
            {
                try
                {
                    patched = TextureImporter.CopySpeciesPalette(target, donor.Write(), clut);
                    donorUsed = donor;
                    break;
                }
                catch (InvalidOperationException) { /* palette rect/size mismatch — try the next donor */ }
            }
            if (patched is null || donorUsed is null) { skipped++; continue; }

            context.SetRoomOutput(room, patched);
            tinted++;
            spoiler.AddRow(Spoiler.Dc1RoomNames.Describe(code),
                $"{rec.Species} palette (CLUT 0x{clut:X4}, 512 B) replaced with "
                + $"ST{donorUsed.Stage:X}{donorUsed.Room:X2}'s (choreography room — species/permute excluded)");
        }

        context.Log($"[dc1-cutscene-palette] tinted {tinted} choreography room(s), {skipped} skipped");
        spoiler.AddNote($"tinted {tinted} choreography-excluded room(s) via the type-2 palette lever "
            + "(cont.51/57); species swaps and (model, motion) permutes are refused in these rooms");
    }

    private static void Shuffle<T>(List<T> items, Random rng)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
