using System.Globalization;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// Pure planner for the single-room <b>FORCED</b> enemy swap behind the CLI op
/// <c>--dc2-swap-enemies &lt;roomCode&gt; --species &lt;name|0xNN&gt;</c> — the file-edit replacement for
/// the CE cave the <c>dc2-enemy-inject</c> skill used (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md).
///
/// <para>Unlike <see cref="Dc2CrossSpeciesPlanner"/> (the production randomizer: a random non-native
/// donor, collision-avoidance, aquatic-room skip), this converts <b>every</b> eligible hardcoded enemy
/// spawn in one room to <b>one caller-chosen</b> donor TYPE. Habitat is enforced by the CLI via
/// <see cref="IsSafeLandDonor"/> + an <c>--allow-unsafe</c> escape hatch (so the skill can crash-classify
/// unresolved / aquatic / flyer types in-game). RNG-free / file-free.</para>
/// </summary>
public static class Dc2RoomEnemySwap
{
    /// <summary>
    /// Resolve a <c>--species</c> spec to its <see cref="Dc2Species"/>, or <c>null</c> if it is not a
    /// known enemy ctor TYPE. Accepts a creature name (case/space/punctuation-insensitive, e.g.
    /// <c>"oviraptor"</c>, <c>"T-Rex"</c>) or a TYPE literal (hex <c>"0x07"</c> or decimal <c>"7"</c>).
    /// </summary>
    public static Dc2Species? ResolveDonor(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        spec = spec.Trim();

        // TYPE literal first (0xNN hex, else bare decimal) — mirrors Program.cs ParseItemInt.
        if (TryParseType(spec, out int type))
            return Dc2SpeciesTable.ForType(type);

        // Creature name (normalize away case + non-alphanumerics so "T-Rex" / "t rex" both match).
        var norm = Normalize(spec);
        return Dc2SpeciesTable.All.FirstOrDefault(s => Normalize(s.Creature) == norm);
    }

    /// <summary>A donor is safe (allowed without <c>--allow-unsafe</c>) iff it is a <b>LAND</b> species —
    /// the live-validated land-only guard. <see cref="Dc2Habitat.Aquatic"/> and <see cref="Dc2Habitat.NonLand"/>
    /// crash as a land spawn, <see cref="Dc2Habitat.Flyer"/> is crash-safe but excluded, and
    /// <see cref="Dc2Habitat.Unknown"/> is unresolved — all need the explicit escape hatch.</summary>
    public static bool IsSafeLandDonor(Dc2Species donor) => donor.Habitat == Dc2Habitat.Land;

    /// <summary>True iff the room natively hosts a confirmed <b>non-land</b> species — <see cref="Dc2Habitat.Aquatic"/>
    /// (e.g. ST706's Mosasaurus <c>0x05</c>, ST700's <c>0x0a</c>), the unresolved-but-live-non-land
    /// <see cref="Dc2Habitat.NonLand"/> (<c>0x0b</c>/<c>0x0c</c>), or the <see cref="Dc2Habitat.Flyer"/>
    /// (<c>0x04</c>, whose land replacement spawns outside the level hitbox) — swapping its intended
    /// non-land enemy to a land donor is wrong (and a co-resident aquatic scene is a crash risk). Mirrors
    /// <see cref="Dc2CrossSpeciesPlanner"/>'s non-land-room skip: checks <b>every</b> spawn (even non-literal),
    /// since the non-land native may be gate-driven. The CLI refuses such a room unless <c>--force</c> is passed.</summary>
    public static bool IsAquaticNativeRoom(IReadOnlyList<Dc2SpawnRecord> spawns) =>
        spawns.Any(s => Dc2SpeciesTable.IsNonLandNativeType(s.Type));

    /// <summary>Room-key-aware overload: the room is aquatic-native if its spawns host a hardcoded non-land
    /// species (<see cref="IsAquaticNativeRoom(IReadOnlyList{Dc2SpawnRecord})"/>) <b>or</b> its <c>st_id</c>
    /// is in the explicit <see cref="Dc2AquaticRooms"/> list — the underwater rooms (e.g. ST704) whose
    /// aquatic enemy is delivered via the generic TYPE-0x10 path and so is invisible to the habitat check.
    /// The CLI uses this so it refuses those rooms too (unless <c>--force</c>).</summary>
    public static bool IsAquaticNativeRoom(IReadOnlyList<Dc2SpawnRecord> spawns, string roomKey) =>
        IsAquaticNativeRoom(spawns) || Dc2AquaticRooms.Contains(roomKey);

    /// <summary>The eligible source spawns in a room: <b>literal</b> (<c>mode 0</c>) species-hardcoded
    /// enemy ctors — the spawns whose TYPE a <c>Dc2SpawnEditor.WriteOperand</c> edit cleanly swaps.
    /// Generic <c>0x10</c> (model from a runtime global), non-literal operands, and non-enemy TYPEs fall
    /// out (matching <see cref="Dc2CrossSpeciesPlanner"/>'s eligibility predicate).</summary>
    public static IReadOnlyList<Dc2SpawnRecord> EligibleSpawns(IReadOnlyList<Dc2SpawnRecord> spawns) =>
        spawns.Where(s => s.TypeMode == 0 && Dc2SpeciesTable.IsEnemyCtorType(s.Type)).ToArray();

    /// <summary>Plan the TYPE edits to convert every eligible spawn (<see cref="EligibleSpawns"/>) to
    /// <paramref name="donorType"/>. Empty when the room has no editable hardcoded enemy spawn.</summary>
    public static IReadOnlyList<Dc2SpawnTypeEdit> Plan(IReadOnlyList<Dc2SpawnRecord> spawns, int donorType) =>
        EligibleSpawns(spawns)
            .Select(s => new Dc2SpawnTypeEdit(s.TypeValueOff, s.Type, donorType))
            .ToArray();

    /// <summary>FORCED combined plan across BOTH enemy-creation paths (K65): every eligible hardcoded
    /// op-0x1a spawn AND every wave descriptor (+ ambush normalization) → <paramref name="donorType"/>.
    /// The wave lever is what makes wave-only rooms (ST105/ST104-class, previously "nothing to swap")
    /// swappable — live-validated Gates W1/W2 (docs/decisions/dc2/spawn/ST105-REAL-SPAWNER-PLAN.md).</summary>
    public static Dc2RoomPlan Plan(IReadOnlyList<Dc2SpawnRecord> spawns, Dc2WaveRoom? wave, int donorType)
    {
        var words = new List<Dc2SpawnTypeEdit>(Plan(spawns, donorType));
        var bytes = new List<Dc2ByteEdit>();
        if (wave is not null)
            Dc2CrossSpeciesPlanner.EmitWaveEdits(wave, donorType, words, bytes);
        return new Dc2RoomPlan(words, bytes);
    }

    /// <summary>Wave-aware overload: the room is also non-land-native when its WAVES natively spawn an
    /// aquatic/non-land species (TYPE 0x0a/0x0b/0x0c rooms: ST001/600/601/604/700/702/703/704) — the
    /// data-driven form of what <see cref="Dc2AquaticRooms"/> could only pin by hand (ST704).</summary>
    public static bool IsAquaticNativeRoom(
        IReadOnlyList<Dc2SpawnRecord> spawns, string roomKey, Dc2WaveRoom? wave) =>
        IsAquaticNativeRoom(spawns, roomKey) ||
        (wave?.Descriptors.Any(d => Dc2SpeciesTable.IsNonLandNativeType(d.NativeType)) ?? false);

    private static bool TryParseType(string s, out int value)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
