namespace DinoRand.Randomizer.Dc2;

/// <summary>Where a species' model loads in RAM — decides whether two species can be resident at
/// once (docs/reference/dc2/enemies/CROSS-SPECIES-SWAP-RE.md §2, EXE table <c>0x703520</c>).</summary>
public enum Dc2BaseClass
{
    /// <summary>Own dedicated RAM base (E00 0x633000, E40 0x63f500, E90 0x63d000, E60 0x638000,
    /// E70 0x650000) — always coexists with the other dedicated species.</summary>
    Dedicated,

    /// <summary>Shares base <c>0x640000</c> with the rest of the big-dino group
    /// (E10/E20/E30/E31/E32/E50/E80/EA0) — only ONE can be resident in a room at a time.</summary>
    Shared640000,
}

/// <summary>Where a creature can spawn. <see cref="Aquatic"/> creatures CRASH when forced into a land
/// room — their ctor/AI assumes an aquatic scene (live-confirmed: Mosasaurus crashed ST202,
/// 2026-06-30). Only <see cref="Land"/> creatures are valid cross-room donors.</summary>
public enum Dc2Habitat
{
    Land,
    /// <summary>Underwater creature (Mosasaurus, Plesiosaurus) — scene-gated; crashes as a land spawn.</summary>
    Aquatic,
    /// <summary>Flyer (Pteranodon, E20/TYPE 0x04). <b>Crash-SAFE</b> as a land spawn — live-confirmed it
    /// spawns without crashing (ST202, K62c), unlike <see cref="Aquatic"/>. Still excluded from the donor
    /// pool: its ground-combat AI/pathing is unverified, so a flyer-as-trash-mob could behave oddly.</summary>
    Flyer,
    /// <summary>Live-confirmed <b>NON-LAND</b> (crashes as a land spawn, ST202 2026-06-30) but the
    /// aquatic-vs-flyer split is still unresolved (TYPE 0x0b/0x0c). Treated as skip-worthy exactly like
    /// <see cref="Aquatic"/> — never a donor, and a room natively hosting it is left unchanged — the
    /// conservative choice until a live capture in its native stage resolves it (2026-06-30 decision).</summary>
    NonLand,
    Unknown,
}

/// <summary>
/// One DC2 enemy <b>species ctor TYPE</b> — the slot-5 spawn op-0x1a TYPE byte (<c>block+0x20</c>)
/// that indexes the per-type ctor table <c>0x731D98</c>; each ctor loads its model base
/// (docs/reference/dc2/enemies/CROSS-SPECIES-SWAP-RE.md, K49). Only the species-hardcoded TYPEs are listed; the
/// generic <c>0x10</c> (model from a runtime global) and non-enemy <c>0x00/0x01/0x11+</c> TYPEs are
/// deliberately absent, so <see cref="Dc2SpeciesTable.IsEnemyCtorType"/> == "appears here".
/// </summary>
public sealed record Dc2Species(
    int Type,
    string EFile,
    uint ModelBase,
    string Creature,
    Dc2BaseClass BaseClass,
    bool IsBoss,
    Confidence Confidence,
    Dc2Habitat Habitat = Dc2Habitat.Unknown,
    /// <summary>A non-threatening SETPIECE enemy (deals no damage; appears in a scripted set-piece, e.g.
    /// the Triceratops). LAND and crash-safe as a donor, but excluded from the default donor pool because
    /// it makes a dull/identity-breaking trash mob — opt-in only via <see cref="Dc2SpeciesTable.DonorPool"/>.</summary>
    bool IsSetpiece = false);

/// <summary>
/// The DC2 species registry — TYPE → <see cref="Dc2Species"/>, sourced from
/// <c>data/dc2/enemies.json</c> <c>mapping.type_ctor_map</c> (K49) + the live captures
/// (K43 E00=Velociraptor, K44 E90=Oviraptor/E60=Allosaurus, K46 E40=Giganotosaurus, this session's
/// live ST202 Oviraptor swap). <b>This replaces the stale <c>Dc2EnemyHelper</c> creature map</b>
/// (which predated the captures: Ovi=E00, Raptor=E30/31/32 — both retracted). Locked to the JSON by
/// <c>Dc2SpeciesTableTests</c>. Adding/locking a creature is a row edit, not new code.
/// </summary>
public static class Dc2SpeciesTable
{
    public static IReadOnlyList<Dc2Species> All { get; } = new[]
    {
        // --- Dedicated bases (coexist). LIVE-swap-confirmed donors flagged (ST202, 2026-06-30). ---
        new Dc2Species(0x02, "E00", 0x00633000, "Velociraptor",   Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Known,     Dc2Habitat.Land),
        new Dc2Species(0x06, "E40", 0x0063f500, "Giganotosaurus", Dc2BaseClass.Dedicated,    IsBoss: true,  Confidence.Known,     Dc2Habitat.Land),    // live ✓ (boss-scale)
        new Dc2Species(0x07, "E90", 0x0063d000, "Oviraptor",      Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Known,     Dc2Habitat.Land),    // live ✓
        new Dc2Species(0x08, "E60", 0x00638000, "Allosaurus",     Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Known,     Dc2Habitat.Land),
        new Dc2Species(0x0d, "E60", 0x00638000, "Allosaurus",     Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Suspected, Dc2Habitat.Land),    // variant ctor
        new Dc2Species(0x09, "E70", 0x00650000, "Triceratops",      Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Known,     Dc2Habitat.Land, IsSetpiece: true), // RESOLVED LIVE 2026-06-30 (CE cave ST202): header 434/62/20@0x650000, HP 5333, no crash ⇒ LAND; setpiece (no-damage) ⇒ opt-in donor only

        // --- Shared 0x640000 group (mutually exclusive; not v1 donors). 0x03/0x05 RESOLVED by the live
        //     swap (2026-06-30): 0x03=E10 T-Rex worked, 0x05=E30 Mosasaurus CRASHED (aquatic) — the static
        //     "both E10 by HP 10000" guess is corrected. ---
        new Dc2Species(0x0e, "E50", 0x00640000, "Inostrancevia",  Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Known,     Dc2Habitat.Land),    // live ✓
        new Dc2Species(0x03, "E10", 0x00640000, "Tyrannosaurus",  Dc2BaseClass.Shared640000, IsBoss: true,  Confidence.Known,     Dc2Habitat.Land),    // live ✓ (boss-scale)
        new Dc2Species(0x05, "E30", 0x00640000, "Mosasaurus",     Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Known,     Dc2Habitat.Aquatic), // live ✗ CRASH (aquatic)
        new Dc2Species(0x04, "E20", 0x00640000, "Pteranodon",     Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Known,     Dc2Habitat.Flyer),   // RESOLVED LIVE 2026-06-30 (CE cave ST202): header 358/58/18@0x640000=E20, HP 2400, NO crash ⇒ FLYER (crash-safe but not a land donor)
        new Dc2Species(0x0a, "?",   0x00640000, "shared (unresolved)", Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Open,  Dc2Habitat.Aquatic), // habitat KNOWN: live CRASH + maintainer in-game ID = AQUATIC (ST202, K62b; native host ST700); creature/E-slot still open
        new Dc2Species(0x0b, "?",   0x00640000, "shared (unresolved)", Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Open,  Dc2Habitat.NonLand), // habitat KNOWN: live CRASH = NON-LAND (ST202, K62b); aquatic-vs-flyer unresolved ⇒ skip-worthy (conservative)
        new Dc2Species(0x0c, "?",   0x00640000, "shared (unresolved)", Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Open,  Dc2Habitat.NonLand), // habitat KNOWN: live CRASH = NON-LAND (ST202, K62b); aquatic-vs-flyer unresolved ⇒ skip-worthy (conservative)
    };

    private static readonly IReadOnlyDictionary<int, Dc2Species> ByType =
        All.ToDictionary(s => s.Type);

    /// <summary>The species for a spawn TYPE, or <c>null</c> if <paramref name="type"/> is not a
    /// species-hardcoded enemy ctor (generic 0x10, player/partner, effects/items).</summary>
    public static Dc2Species? ForType(int type) => ByType.GetValueOrDefault(type);

    /// <summary>True iff <paramref name="type"/> is a species-hardcoded enemy ctor TYPE — i.e. its
    /// model loads from the spawn's own <c>LoadEnemyCategory</c> and a TYPE-literal edit swaps it
    /// cleanly (the v1 eligibility predicate).</summary>
    public static bool IsEnemyCtorType(int type) => ByType.ContainsKey(type);

    /// <summary>True iff <paramref name="type"/> is a species whose NATIVE room must not be a swap
    /// target: <see cref="Dc2Habitat.Aquatic"/> / <see cref="Dc2Habitat.NonLand"/> (a land donor there
    /// is wrong, co-resident aquatic scene = crash risk) or <see cref="Dc2Habitat.Flyer"/> (a land
    /// replacement spawns outside the level hitbox — unreachable, live-verified 2026-07-04). The single
    /// shared predicate behind every non-land room skip (planner + <c>Dc2RoomEnemySwap</c>).</summary>
    public static bool IsNonLandNativeType(int type) =>
        ForType(type)?.Habitat is Dc2Habitat.Aquatic or Dc2Habitat.NonLand or Dc2Habitat.Flyer;

    /// <summary>Donor pool: <b>non-boss, live-confirmed LAND</b> species — read as normal enemies and
    /// don't crash. v1 was dedicated-only; <b>v2 (2026-06-30) broadened it to include shared-base LAND
    /// donors</b> (so Inostrancevia <c>0x0e</c> joins Velociraptor <c>0x02</c>, Oviraptor <c>0x07</c>,
    /// Allosaurus <c>0x08</c>). The shared-base members are kept safe at use-time by the planner's
    /// <b>≤1-resident-0x640000 budget guard</b> (a shared donor is excluded from any room with a
    /// generic-0x10 spawn, whose model could occupy 0x640000 — docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md).
    /// Excludes the bosses (Giganotosaurus <c>0x06</c>, T-Rex <c>0x03</c> — behind a boss-donor pool),
    /// the unresolved E70/shared <c>0x04/0x0a/0x0b/0x0c</c>, the variant Allo ctor, and — critically —
    /// <b>aquatic creatures</b> (Mosasaurus): forcing one into a land room CRASHES the game
    /// (live-confirmed ST202, 2026-06-30; their ctor/AI assumes an aquatic scene).</summary>
    public static IReadOnlyList<Dc2Species> DefaultDonors { get; } = All
        .Where(s => !s.IsBoss && !s.IsSetpiece && s.Confidence == Confidence.Known && s.Habitat == Dc2Habitat.Land)
        .ToArray();

    /// <summary>The cross-species donor pool for a run. Both opt-ins broaden the live-validated base pool
    /// (LAND + <c>Known</c> only — never aquatic/flyer/unresolved):
    /// <list type="bullet">
    /// <item><paramref name="includeSetpiece"/> <c>true</c> ⇒ also allow LAND <b>setpiece</b> species (the
    /// no-damage Triceratops <c>0x09</c>), via <c>RandomizerConfig.IncludeDc2SetpieceEnemies</c>.</item>
    /// <item><paramref name="includeBoss"/> <c>true</c> ⇒ also allow LAND <b>boss</b> species (T-Rex
    /// <c>0x03</c>, Giganotosaurus <c>0x06</c>) — both live-proven LAND but degenerate as trash mobs, via
    /// <c>RandomizerConfig.IncludeDc2BossEnemies</c>.</item>
    /// </list>
    /// Both <c>false</c> (default) ⇒ exactly <see cref="DefaultDonors"/>. Wired in
    /// <see cref="Passes.Dc2EnemyRandomizer"/>; the planner's ≤1-shared-0x640000 budget guard keeps the
    /// shared-base members (Inostrancevia, T-Rex) safe at use-time.</summary>
    public static IReadOnlyList<Dc2Species> DonorPool(bool includeSetpiece, bool includeBoss = false) => All
        .Where(s => IsPoolMember(s, includeSetpiece, includeBoss))
        .ToArray();

    /// <summary>True iff <paramref name="type"/> would be in <see cref="DonorPool"/> under these
    /// toggles — the SAME predicate the pool is built from, exposed so consumers (e.g. the app's
    /// weight-slider visibility, docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D7) can't drift from it.</summary>
    public static bool IsDonorPoolMember(int type, bool includeSetpiece, bool includeBoss) =>
        ForType(type) is { } s && IsPoolMember(s, includeSetpiece, includeBoss);

    private static bool IsPoolMember(Dc2Species s, bool includeSetpiece, bool includeBoss) =>
        s.Confidence == Confidence.Known && s.Habitat == Dc2Habitat.Land
        && (!s.IsSetpiece || includeSetpiece)
        && (!s.IsBoss || includeBoss);
}
