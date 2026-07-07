namespace DinoRand.Randomizer.Dc2;

/// <summary>A spawn the planner reasons over — the projection of one <c>data/dc2/spawn-graph.json</c>
/// op-0x1a spawn: its TYPE value + operand mode + the blob byte-offset to rewrite, and its actor slot.
/// <paramref name="TypeMode"/> 0 = literal (editable); non-zero (var/block/global) = not editable.
/// VARIANT = the block+0x08 operand (raptor tier / .TEX nibble → <c>[actor+0x10C]</c>,
/// docs/reference/dc2/enemies/RAPTOR-TIER-RE.md); -1 = spawn has no such push (or pre-VARIANT graph).</summary>
public sealed record Dc2SpawnRecord(
    int Type, int TypeMode, int TypeValueOff, int Slot,
    int Variant = -1, int VariantMode = -1, int VariantValueOff = -1);

/// <summary>A planned TYPE-literal edit: write <paramref name="NewType"/> at blob offset
/// <paramref name="ValueOff"/> (via <c>Dc2SpawnEditor.WriteOperand</c>).</summary>
public sealed record Dc2SpawnTypeEdit(int ValueOff, int OldType, int NewType);

/// <summary>A planned single-byte blob edit (via <c>Dc2SpawnEditor.WriteByte</c>): a wave
/// descriptor's species byte (<c>desc+1</c>, K65) or an op-0x05 push MODE byte
/// (<c>push+1</c>, the mode-6 → mode-0 half of <c>Dc2GenericSpawnNormalize</c>).</summary>
public sealed record Dc2ByteEdit(int Offset, byte NewValue);

/// <summary>A room's combined edit plan across BOTH enemy-creation paths (K65): op-0x1a
/// TYPE-literal word edits + wave-descriptor/normalization byte+word edits. Empty lists = leave
/// the room vanilla. <paramref name="DonorType"/> is the room's single donor TYPE (null when the
/// plan is empty) — the pass's cap ledger and donor tally read it
/// (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D4/D7). Apply in one round trip via
/// <c>Dc2SpawnEditor.ApplyEdits</c>.</summary>
public sealed record Dc2RoomPlan(
    IReadOnlyList<Dc2SpawnTypeEdit> WordEdits,
    IReadOnlyList<Dc2ByteEdit> ByteEdits,
    int? DonorType = null)
{
    public static readonly Dc2RoomPlan Empty =
        new(Array.Empty<Dc2SpawnTypeEdit>(), Array.Empty<Dc2ByteEdit>());
    public bool IsEmpty => WordEdits.Count == 0 && ByteEdits.Count == 0;
}

/// <summary>
/// Pure (DOM-free, file-free) per-room planner for the v1 cross-species swap
/// (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md). Decides, for one room's spawns + a seeded RNG, which
/// TYPE-literal edits to make. Policy: <b>per-room single donor</b>, dedicated-base donor
/// (<see cref="Dc2SpeciesTable.DefaultDonors"/>), donor ≠ any species native to the room (the ST202
/// collision-avoidance rule). Eligible source = a <b>literal</b> TYPE that is a species-hardcoded
/// enemy ctor (<see cref="Dc2SpeciesTable.IsEnemyCtorType"/>); generic 0x10 / non-enemy spawns are
/// left untouched.
/// </summary>
public static class Dc2CrossSpeciesPlanner
{
    /// <summary>The generic enemy spawn TYPE (model from the runtime global <c>block+0x0c</c>, not a
    /// self-loaded category) — its residency is opaque to static analysis, so it gates the shared-base
    /// donor budget (docs/reference/dc2/enemies/CROSS-SPECIES-SWAP-RE.md §2).</summary>
    private const int GenericSpawnType = 0x10;

    /// <summary>Plan the TYPE edits for one room. Returns an empty list when the room has no eligible
    /// spawn or no valid distinct donor (then the room is left unchanged). <paramref name="picker"/>
    /// selects the donor among the room's VALID donors (weighted/fixed,
    /// docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md); <c>null</c> keeps the legacy uniform pick. A picker
    /// that declines (all valid donors weight-0 / pin not valid here) leaves the room unchanged —
    /// never a uniform fallback.</summary>
    public static IReadOnlyList<Dc2SpawnTypeEdit> PlanRoom(
        IReadOnlyList<Dc2SpawnRecord> spawns,
        Random rng,
        IReadOnlyList<Dc2Species>? donorPool = null,
        Dc2DonorPicker? picker = null)
    {
        donorPool ??= Dc2SpeciesTable.DefaultDonors;

        // Non-land-native room skip (v2): if the room natively hosts a confirmed non-land species —
        // Aquatic (e.g. ST706's Mosasaurus 0x05, or ST700's 0x0a), the unresolved-but-live-non-land
        // NonLand (0x0b/0x0c), or the Flyer 0x04 (land replacement spawns outside the level hitbox,
        // live 2026-07-04) — leave the WHOLE room unchanged. Converting its intended non-land enemy to
        // a land donor is wrong, and a co-resident aquatic scene is a crash risk. Detected by the species'
        // Habitat (data-driven via Dc2SpeciesTable), so it extends to any future non-land species. Checks
        // every spawn (even non-literal), since the non-land native may be gate-driven.
        if (spawns.Any(s => Dc2SpeciesTable.IsNonLandNativeType(s.Type)))
            return Array.Empty<Dc2SpawnTypeEdit>();

        // Eligible source = a LITERAL TYPE (mode 0, so the operand is a rewritable word) that is a
        // species-hardcoded enemy ctor. Generic 0x10 (model from a global) and non-enemy TYPEs fall out.
        var eligible = spawns
            .Where(s => s.TypeMode == 0 && Dc2SpeciesTable.IsEnemyCtorType(s.Type))
            .ToList();
        if (eligible.Count == 0) return Array.Empty<Dc2SpawnTypeEdit>();

        // ≤1-resident-0x640000 budget (v2): a generic-0x10 spawn loads its model from a runtime global
        // we can't see statically, which could already occupy the shared 0x640000 base. A shared-base
        // donor would then be a SECOND 0x640000 category (mutually exclusive ⇒ wrong model / crash), so
        // exclude shared-base donors from any room that has a generic-0x10 spawn. With no generic-0x10
        // present, per-room single-donor guarantees the donor is the room's sole enemy category.
        bool hasGenericSpawn = spawns.Any(s => s.Type == GenericSpawnType);

        // Per-room single donor, chosen ≠ every species the room already spawns. Picking a donor the
        // room natively hosts would duplicate a (TYPE) across its shared SLOT/wave set — the ST202
        // collision that produced "no visible enemies". A donor absent from the room cannot collide.
        var nativeTypes = eligible.Select(s => s.Type).ToHashSet();
        var validDonors = donorPool
            .Where(d => !nativeTypes.Contains(d.Type)
                     && !(hasGenericSpawn && d.BaseClass == Dc2BaseClass.Shared640000))
            .ToList();
        if (validDonors.Count == 0) return Array.Empty<Dc2SpawnTypeEdit>();

        var donor = picker is null
            ? validDonors[rng.Next(validDonors.Count)]
            : picker.Pick(validDonors, rng);
        if (donor is null) return Array.Empty<Dc2SpawnTypeEdit>();

        // Convert every eligible spawn to the donor TYPE; SLOT/X/Y/Z keep their (valid, distinct)
        // values. For a hardcoded TYPE the spawn's own LoadEnemyCategory(donor) makes it resident and
        // the donor ctor sets the model base (MODEL_BASE block+0xc is 0) — a clean native-class spawn.
        return eligible
            .Select(s => new Dc2SpawnTypeEdit(s.TypeValueOff, s.Type, donor.Type))
            .ToArray();
    }

    /// <summary>
    /// Plan a room across BOTH enemy-creation paths (K65): the op-0x1a hardcoded TYPE literals
    /// (the shipped K59/K61 lever) AND the native wave-spawn descriptors (the live-validated
    /// Gates-W1/W2 lever), with ONE donor for everything so the room stays species-pure.
    /// <paramref name="wave"/> null ⇒ identical to <see cref="PlanRoom"/> (legacy path).
    ///
    /// <para>Wave rooms additionally get every generic TYPE-0x10 creature spawn NORMALIZED to the
    /// donor (<c>Dc2GenericSpawnNormalize</c>, K64): the descriptor swap removes the native E-file's
    /// residency, so a dormant ambush record left referencing it (e.g. ST105's zone-3 raptors →
    /// <c>0x633000</c>) would bind an unloaded base when triggered. Normalizing converts it to the
    /// self-loading hardcoded form — TYPE word → donor, MODEL_BASE (and a mode-6 HP) push → literal 0.</para>
    /// </summary>
    public static Dc2RoomPlan PlanRoomWithWaves(
        IReadOnlyList<Dc2SpawnRecord> spawns,
        Dc2WaveRoom? wave,
        Random rng,
        IReadOnlyList<Dc2Species>? donorPool = null,
        Dc2DonorPicker? picker = null)
    {
        if (wave is null || wave.Descriptors.Count == 0)
        {
            // Every legacy-path word edit converts TO the room's single donor, so the plan's
            // DonorType is readable off the first edit.
            var legacy = PlanRoom(spawns, rng, donorPool, picker);
            return new Dc2RoomPlan(legacy, Array.Empty<Dc2ByteEdit>(),
                                   legacy.Count > 0 ? legacy[0].NewType : null);
        }

        donorPool ??= Dc2SpeciesTable.DefaultDonors;

        // Non-land-native skip, extended to the wave path: a room whose WAVES are natively
        // aquatic/non-land (ST001/600/601/604/700/702/703/704) keeps them — a land donor there is
        // wrong (and its spawn points assume a non-land scene). Same rule as the spawn-based skip.
        if (wave.Descriptors.Any(d => Dc2SpeciesTable.IsNonLandNativeType(d.NativeType)) ||
            spawns.Any(s => Dc2SpeciesTable.IsNonLandNativeType(s.Type)))
            return Dc2RoomPlan.Empty;

        // ONE donor across both paths, distinct from every species the room natively hosts
        // (hardcoded spawn TYPEs + wave native TYPEs) — the ST202 collision-avoidance rule.
        // Shared-0x640000 donors are safe here: the wave descriptor(s) and every normalized spawn
        // load the SAME donor category, and the generic creature spawns that made the legacy
        // budget rule necessary are exactly the ones being normalized.
        var eligible = spawns
            .Where(s => s.TypeMode == 0 && Dc2SpeciesTable.IsEnemyCtorType(s.Type))
            .ToList();
        var nativeTypes = eligible.Select(s => s.Type)
            .Concat(wave.Descriptors.Select(d => d.NativeType))
            .ToHashSet();
        var validDonors = donorPool.Where(d => !nativeTypes.Contains(d.Type)).ToList();
        if (validDonors.Count == 0) return Dc2RoomPlan.Empty;
        var donor = picker is null
            ? validDonors[rng.Next(validDonors.Count)]
            : picker.Pick(validDonors, rng);
        if (donor is null) return Dc2RoomPlan.Empty;

        var words = new List<Dc2SpawnTypeEdit>();
        var bytes = new List<Dc2ByteEdit>();
        EmitWaveEdits(wave, donor.Type, words, bytes);

        // The hardcoded op-0x1a literals, same as the legacy path but with the shared donor.
        // A normalized generic spawn is not in `eligible` (its graph TYPE is 0x10, filtered out),
        // so the two edit sets never overlap.
        foreach (var s in eligible)
            words.Add(new Dc2SpawnTypeEdit(s.TypeValueOff, s.Type, donor.Type));

        return new Dc2RoomPlan(words, bytes, donor.Type);
    }

    /// <summary>Emit the wave-path edits for one donor: (1) every descriptor's species byte
    /// (<c>desc+1</c> — armed AND preload-only records, keeping op-0x23 residency coherent);
    /// (2) <c>Dc2GenericSpawnNormalize</c> for every dormant generic creature spawn (TYPE word →
    /// donor; MODEL_BASE and a mode-6 HP push → mode-0 literal 0). Shared by the seeded planner
    /// and the forced single-room swap (<see cref="Dc2RoomEnemySwap"/>).</summary>
    internal static void EmitWaveEdits(
        Dc2WaveRoom wave, int donorType,
        List<Dc2SpawnTypeEdit> words, List<Dc2ByteEdit> bytes)
    {
        foreach (var d in wave.Descriptors)
            bytes.Add(new Dc2ByteEdit(d.TypeOff, (byte)donorType));

        foreach (var g in wave.GenericCreatureSpawns)
        {
            words.Add(new Dc2SpawnTypeEdit(g.TypePushOff + 2, GenericSpawnType, donorType));
            bytes.Add(new Dc2ByteEdit(g.MbPushOff + 1, 0));          // MODEL_BASE mode 6 → 0
            words.Add(new Dc2SpawnTypeEdit(g.MbPushOff + 2, -1, 0)); //   value → literal 0
            if (g.HpPushMode == 6)
            {
                bytes.Add(new Dc2ByteEdit(g.HpPushOff + 1, 0));          // HP mode 6 → 0
                words.Add(new Dc2SpawnTypeEdit(g.HpPushOff + 2, -1, 0)); //   value → literal 0
            }
        }
    }
}
