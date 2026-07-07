using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2.Passes;

/// <summary>
/// DC2 enemy randomization pass. <b>⚠ STUB / no-op.</b> Borrows the BioRand <c>EnemyRandomiser</c>
/// loop *shape* (MIT, © Ted John; <c>ref/classic/IntelOrca.Biohazard.BioRand/EnemyRandomiser.cs:234</c>):
/// filter what can host enemies → weight by difficulty → assign a <see cref="Dc2SelectableEnemy"/>.
///
/// <para><b>The first write seam IS the room file (D4, K47–K50).</b> Enemy placement is a slot-5 SCD
/// program: spawn op <c>0x1a</c> reads TYPE / X / Y / Z / SLOT from <b>mode-0 literal</b> operands that
/// are room-file editable. The write primitive is shipped — <see cref="Dc2SpawnEditor"/> over the
/// per-operand offsets in <c>data/dc2/spawn-graph.json</c> (tools/dc2_re/edit_spawn.py) — gated by the
/// T8 round-trip harness. The heavier <b>EXE-level</b> seam (patch a ctor's <c>PUSH &lt;model base&gt;</c>
/// / repoint a vtable, a future <c>Dc2ExeEnemyPatcher</c>) is <b>deferred</b>; it is only needed for
/// non-literal operands and the generic TYPE-0x10 spawn whose model base is a global
/// (docs/reference/dc2/spawn/EXE-SPAWN-SYSTEM.md). See docs/decisions/dc2/RANDO-ROADMAP-PLAN.md Phase 1.</para>
///
/// <para>What still gates a *cross-species* TYPE swap: the per-room <b>resident <c>E*.DAT</c> set</b>
/// (a TYPE may only be swapped to a species whose model is loaded in that room) — runtime capture T3d,
/// partial (7/89). Provably-safe edits available now without T3d: <b>position permutation among a
/// room's same-species spawns</b> (all positions are known-valid; species/count unchanged). The output
/// sink (backup-and-swap <c>ST*.DAT</c> vs a CR <c>.d2p</c> override, T7) is the open integration
/// decision; this stub stays a no-op until that lands.</para>
/// </summary>
public sealed class Dc2EnemyRandomizer : IDc2RandomizationPass
{
    public string Name => "DC2 Enemy Randomizer";

    public bool IsEnabled(RandomizerConfig config) => config.RandomizeEnemies;

    public void Apply(Dc2RandomizationContext context)
    {
        // Cross-species swap (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md, K59/K60): per room, plan the
        // hardcoded-spawn → dedicated-base-donor TYPE edits, rewrite the slot-5 TYPE literals, and
        // emit via the (backup-swap) sink. The op-0x1a handler self-loads the donor category at room
        // load, so a TYPE edit performs a real species swap — no EXE patch, no resident-set gating.
        //
        // K65 (docs/decisions/dc2/spawn/ST105-REAL-SPAWNER-PLAN.md, Gates W1/W2 live-PASSED): rooms also — often
        // exclusively — create enemies via the NATIVE wave spawner, armed by op-0x4f with a
        // descriptor baked in the room blob. Its species byte (desc+1) is edited to the same donor,
        // and any dormant generic ambush spawn tied to the replaced E-file is normalized to the
        // donor too, so the whole room is species-pure across both creation paths.
        var graph = Dc2SpawnGraph.LoadEmbedded();
        var waves = Dc2WaveTable.LoadEmbedded();
        var distribution = Dc2EnemyDistribution.LoadEmbedded();

        // Donor selection policy (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md): FIXED pins one donor for
        // every eligible room (the pin IS the boss/setpiece opt-in, so the full safe pool applies);
        // WEIGHTED biases the per-room pick by the curated/overridden weight table. Setpiece donors
        // (the no-damage Triceratops, K62) and boss donors (T-Rex/Giganotosaurus, K61) stay opt-in
        // pool members off by default (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md).
        bool fixedMode = context.Config.Dc2EnemyMode == Dc2EnemyDistributionMode.Fixed;

        // Spoiler section (docs/decisions/cross/SPOILER-LOG-PLAN.md §4): one row per changed room, recorded at the
        // moment the plan is applied — never re-derived. Created up-front so skip summaries appear
        // even when nothing changes.
        var spoiler = context.Spoiler.Section("Enemies (DC2 cross-species)",
            "Room", "Vanilla species", "New species", "Edits");

        if (fixedMode)
        {
            var pin = context.Config.Dc2FixedSpeciesType is int t ? Dc2SpeciesTable.ForType(t) : null;
            if (pin is null || !Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true)
                    .Any(d => d.Type == pin.Type))
            {
                context.Log("[dc2-enemy] fixed mode: Dc2FixedSpeciesType is missing or not a safe "
                    + "LAND donor — pass skipped (no rooms changed).");
                spoiler.AddNote("mode: fixed — invalid/unsafe pinned species, pass skipped (no rooms changed)");
                return;
            }
            spoiler.AddNote($"mode: fixed: {pin.Creature} (every eligible room converts to this donor)");
        }
        else
        {
            spoiler.AddNote("mode: weighted (per-room donor drawn from the weight table)"
                + (context.Config.Dc2SpeciesWeights is { Count: > 0 } w
                    ? $"; weight overrides: {string.Join(", ", w.OrderBy(kv => kv.Key).Select(kv => $"{Dc2SpeciesTable.ForType(kv.Key)?.Creature ?? $"0x{kv.Key:X2}"}={kv.Value}"))}"
                    : "; curated default weights"));
        }
        var donorPool = fixedMode
            ? Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true)
            : Dc2SpeciesTable.DonorPool(
                context.Config.IncludeDc2SetpieceEnemies, context.Config.IncludeDc2BossEnemies);
        var picker = fixedMode
            ? Dc2DonorPicker.Fixed(context.Config.Dc2FixedSpeciesType!.Value)
            : Dc2DonorPicker.Weighted(distribution.EffectiveWeights(context.Config.Dc2SpeciesWeights));

        // Per-species room caps (weighted mode only; plan D4): a mutable remaining-rooms ledger,
        // consumed in (stage, room)-sorted order — cap state makes iteration order observable, and
        // directory enumeration order is not a determinism guarantee.
        var capsLeft = fixedMode ? null : distribution.RoomCaps.ToDictionary(kv => kv.Key, kv => kv.Value);
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int roomsChanged = 0, spawnsChanged = 0, roomsSkipped = 0, roomsExcluded = 0, roomsAquatic = 0;

        foreach (var room in context.Rooms.OrderBy(r => r.Stage).ThenBy(r => r.Room))
        {
            var roomKey = Dc2SpawnGraph.RoomKey(room.Stage, room.Room);

            // Set-piece rooms (e.g. ST407's turret sequence) are scripted around their specific enemies;
            // swapping species would break them — leave them vanilla (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md).
            if (Dc2RoomExclusions.IsExcluded(roomKey)) { roomsExcluded++; continue; }

            // Explicit aquatic rooms whose enemy is generic-delivered (e.g. ST704's Mosasaurus via TYPE-0x10)
            // are invisible to PlanRoom's habitat skip — protect them by st_id so their intended aquatic
            // enemy is never converted to a land donor (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md).
            if (Dc2AquaticRooms.Contains(roomKey)) { roomsAquatic++; continue; }

            // A wave-only room (e.g. ST104: zero op-0x1a spawns, raptors purely from waves) has no
            // spawn-graph entry — plan with an empty spawn list rather than skipping it.
            var spawns = graph.ForRoom(roomKey) ?? Array.Empty<Dc2SpawnRecord>();
            var wave = waves.ForRoom(roomKey);
            if (spawns.Count == 0 && wave is null) continue;

            // Cap filter BEFORE the pick: an exhausted species simply isn't offered, so there is
            // no re-roll and no silent fallback (plan D4).
            var roomPool = capsLeft is null
                ? donorPool
                : donorPool.Where(d => !capsLeft.TryGetValue(d.Type, out var left) || left > 0).ToArray();

            var rng = context.Seed.RngFor($"{Name}:{room.Stage:X}{room.Room:X2}"); // per-room determinism
            var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, wave, rng, roomPool, picker);
            if (plan.IsEmpty) { roomsSkipped++; continue; }

            if (plan.DonorType is int donorType)
            {
                if (capsLeft is not null && capsLeft.ContainsKey(donorType))
                    capsLeft[donorType]--;
                var creature = Dc2SpeciesTable.ForType(donorType)?.Creature ?? $"0x{donorType:X2}";
                tally[creature] = tally.GetValueOrDefault(creature) + 1;

                // Spoiler row: vanilla = every species this room natively spawns via either
                // creation path (the op-0x1a ctor literals + the wave descriptors' native types).
                var vanillaTypes = spawns
                    .Where(s => s.TypeMode == 0 && Dc2SpeciesTable.IsEnemyCtorType(s.Type))
                    .Select(s => s.Type)
                    .Concat(wave?.Descriptors.Select(d => d.NativeType) ?? Enumerable.Empty<int>())
                    .Distinct().OrderBy(t => t)
                    .Select(t => Dc2SpeciesTable.ForType(t)?.Creature ?? $"0x{t:X2}");
                int spawnEdits = plan.WordEdits.Count(w => w.OldType != -1);
                int waveEdits = plan.ByteEdits.Count(b => b.NewValue != 0);
                spoiler.AddRow($"ST{roomKey}", string.Join(" / ", vanillaTypes), creature,
                               $"{spawnEdits} spawn(s), {waveEdits} wave descriptor(s)");
            }

            var bytes = Dc2SpawnEditor.ApplyEdits(context.CurrentBytes(room),
                plan.WordEdits.Select(w => (w.ValueOff, (short)w.NewType)),
                plan.ByteEdits.Select(b => (b.Offset, b.NewValue)));

            context.EmitRoom(room, bytes);
            roomsChanged++;
            spawnsChanged += plan.WordEdits.Count(w => w.OldType != -1) // TYPE conversions…
                + plan.ByteEdits.Count(b => b.NewValue != 0);           // …+ wave descriptors
        }

        string mode = fixedMode
            ? $"fixed={Dc2SpeciesTable.ForType(context.Config.Dc2FixedSpeciesType!.Value)!.Creature}"
            : "weighted";
        context.Log(
            $"[dc2-enemy] cross-species swap ({mode}): {spawnsChanged} spawn/wave edits across {roomsChanged} rooms " +
            $"(donors: {string.Join("/", donorPool.Select(d => d.Creature))}); " +
            $"{roomsSkipped} eligible rooms left unchanged (no valid distinct donor); " +
            $"{roomsExcluded} set-piece rooms excluded; {roomsAquatic} aquatic rooms protected.");
        if (tally.Count > 0)
            context.Log($"[dc2-enemy] donor tally: {string.Join("/", tally.Select(kv => $"{kv.Key}×{kv.Value}"))}");

        // Skipped/protected rooms get a summary, never silence (plan §4).
        spoiler.AddNote($"donor pool: {string.Join(" / ", donorPool.Select(d => d.Creature))}");
        spoiler.AddNote($"{roomsChanged} room(s) changed; {roomsSkipped} eligible room(s) left vanilla "
            + $"(no valid distinct donor); {roomsExcluded} set-piece room(s) excluded; "
            + $"{roomsAquatic} aquatic room(s) protected");
        if (tally.Count > 0)
            spoiler.AddNote($"donor tally: {string.Join(" / ", tally.Select(kv => $"{kv.Key}×{kv.Value}"))}");
    }
}
