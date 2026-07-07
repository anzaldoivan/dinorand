using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2.Passes;

/// <summary>
/// DC2 raptor tier randomization pass (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §4). Rewrites, per room:
/// <list type="bullet">
/// <item>each static op-0x1a raptor spawn's VARIANT literal (block+0x08 → <c>[actor+0x10C]</c>,
/// the tier nibble: skin .TEX + HP class together) with an independent weighted draw;</item>
/// <item>each raptor wave descriptor's <c>desc+4</c> byte (pair-table index jitter — the exact
/// weighted wave pool is the install-time pair-table exe patch, <see cref="Dc2RaptorTierInstaller"/>).</item>
/// </list>
/// Runs AFTER <see cref="Dc2EnemyRandomizer"/> on the context's working bytes and re-reads each
/// target's CURRENT type first, so spawns/waves converted away from raptor are skipped (their
/// variant operand then belongs to another category's .TEX clamp).
/// </summary>
public sealed class Dc2RaptorTierRandomizer : IDc2RandomizationPass
{
    public string Name => "DC2 Raptor Tiers";

    public bool IsEnabled(RandomizerConfig config) => config.Dc2RandomizeRaptorTiers;

    public void Apply(Dc2RandomizationContext context)
    {
        var graph = Dc2SpawnGraph.LoadEmbedded();
        var waves = Dc2WaveTable.LoadEmbedded();
        var tiers = Dc2RaptorTierTable.LoadEmbedded();
        var weights = tiers.EffectiveWeights(context.Config.Dc2RaptorTierWeights);

        var spoiler = context.Spoiler.Section("Raptor tiers (DC2)",
            "Room", "Static variants", "Wave desc bytes");
        spoiler.AddNote("weights: " + string.Join(", ",
            tiers.Rows.Select(r => $"V{r.Variant}={weights[r.Variant]}")));

        int roomsChanged = 0, staticEdits = 0, waveEdits = 0;
        foreach (var room in context.Rooms.OrderBy(r => r.Stage).ThenBy(r => r.Room))
        {
            var roomKey = Dc2SpawnGraph.RoomKey(room.Stage, room.Room);
            if (Dc2RoomExclusions.IsExcluded(roomKey)) continue; // same set-piece rule as the enemy pass

            var spawns = graph.ForRoom(roomKey) ?? Array.Empty<Dc2SpawnRecord>();
            var wave = waves.ForRoom(roomKey);
            var bytes = context.CurrentBytes(room);

            // Static targets: literal-TYPE spawns whose CURRENT type is still the raptor and whose
            // VARIANT operand is a literal word we can rewrite.
            var staticVariants = spawns
                .Where(s => s.TypeMode == 0 && s.VariantMode == 0 && s.VariantValueOff >= 0
                         && Dc2SpawnEditor.ReadOperandFromPackage(bytes, s.TypeValueOff)
                            == Dc2RaptorTierPlanner.RaptorType)
                .Select(s => (s.VariantValueOff,
                              Dc2SpawnEditor.ReadOperandFromPackage(bytes, s.VariantValueOff)))
                .ToList();

            // Wave targets: descriptors whose CURRENT species byte is still the raptor.
            var waveOffs = (wave?.Descriptors ?? (IReadOnlyList<Dc2WaveDescriptor>)Array.Empty<Dc2WaveDescriptor>())
                .Where(d => d.VariantOff >= 0
                         && Dc2SpawnEditor.ReadByteFromPackage(bytes, d.TypeOff)
                            == Dc2RaptorTierPlanner.RaptorType)
                .Select(d => d.VariantOff)
                .ToList();
            if (staticVariants.Count == 0 && waveOffs.Count == 0) continue;

            var rng = context.Seed.RngFor($"{Name}:{room.Stage:X}{room.Room:X2}");
            var plan = Dc2RaptorTierPlanner.PlanRoom(staticVariants, waveOffs, rng, weights);
            if (plan.IsEmpty) continue; // all weights 0 ⇒ nothing to write

            context.EmitRoom(room, Dc2SpawnEditor.ApplyEdits(bytes,
                plan.WordEdits.Select(w => (w.ValueOff, w.Variant)),
                plan.ByteEdits.Select(b => (b.Offset, b.Value))));

            roomsChanged++;
            staticEdits += plan.WordEdits.Count;
            waveEdits += plan.ByteEdits.Count;
            spoiler.AddRow($"ST{roomKey}",
                plan.WordEdits.Count == 0 ? "—"
                    : string.Join(" ", plan.WordEdits.Select(w => $"V{w.Variant & 0xF}")),
                plan.ByteEdits.Count == 0 ? "—"
                    : string.Join(" ", plan.ByteEdits.Select(b => b.Value.ToString())));
        }

        context.Log($"[dc2-raptor-tiers] {staticEdits} static variant(s) + {waveEdits} wave desc byte(s) "
            + $"across {roomsChanged} room(s); wave pool itself = install-time pair-table patch.");
        spoiler.AddNote($"{roomsChanged} room(s) changed; wave tier pool applied at install "
            + "(Dino2.exe pair-table patch) + blue-raptor combo threshold "
            + $"{context.Config.Dc2BlueRaptorComboThreshold}");
    }
}
