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
        new Dc2Species(0x06, "E40", 0x0063f500, "Giganotosaurus", Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Known,     Dc2Habitat.Land, IsSetpiece: true), // live ✓ (boss-scale); boss→setpiece 2026-07-09 (opt-in only, grouped with Triceratops)
        new Dc2Species(0x07, "E90", 0x0063d000, "Oviraptor",      Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Known,     Dc2Habitat.Land),    // live ✓
        new Dc2Species(0x08, "E60", 0x00638000, "Allosaurus",     Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Known,     Dc2Habitat.Land),
        new Dc2Species(0x0d, "E60", 0x00638000, "Allosaurus",     Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Suspected, Dc2Habitat.Land),    // variant ctor
        new Dc2Species(0x09, "E70", 0x00650000, "Triceratops",      Dc2BaseClass.Dedicated,    IsBoss: false, Confidence.Known,     Dc2Habitat.Land, IsSetpiece: true), // RESOLVED LIVE 2026-06-30 (CE cave ST202): header 434/62/20@0x650000, HP 5333, no crash ⇒ LAND; setpiece (no-damage) ⇒ opt-in donor only

        // --- Shared 0x640000 group (mutually exclusive; not v1 donors). Aquatic set CLOSED LIVE 2026-07-08
        //     (K68/K66/K70): 0x03=E10 T-Rex (land), 0x05=E30 Plesiosaurus BOSS, 0x0a=E80 Mosasaurus,
        //     0x0b/0x0c=E31/E32 Plesiosaurus grunt — all aquatic. The stale K61 "0x05=Mosasaurus/E30" and
        //     "0x0a/0x0b/0x0c unresolved" mappings are corrected. Aquatic species run on land WITHOUT
        //     crashing only via the op-0x4f wave / op-0x23 preload path (K72), so they are wave-only donors
        //     gated behind Dc2AllowWaterLevelEnemySwaps (Mosasaurus a low-weight regular donor; the
        //     Plesiosaurus boss/grunts additionally IsSetpiece = setpiece opt-in). ---
        new Dc2Species(0x0e, "E50", 0x00640000, "Inostrancevia",  Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Known,     Dc2Habitat.Land),    // live ✓
        new Dc2Species(0x03, "E10", 0x00640000, "Tyrannosaurus",  Dc2BaseClass.Shared640000, IsBoss: true,  Confidence.Known,     Dc2Habitat.Land),    // live ✓ (boss-scale)
        new Dc2Species(0x05, "E30", 0x00640000, "Plesiosaurus (boss form)", Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Known, Dc2Habitat.Aquatic, IsSetpiece: true), // K68/K70 live: BOSS form, HP 20000, oversized ⇒ setpiece opt-in + wave-only
        new Dc2Species(0x04, "E20", 0x00640000, "Pteranodon",     Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Known,     Dc2Habitat.Flyer),   // RESOLVED LIVE 2026-06-30 (CE cave ST202): header 358/58/18@0x640000=E20, HP 2400, NO crash ⇒ FLYER (crash-safe but not a land donor)
        new Dc2Species(0x0a, "E80", 0x00640000, "Mosasaurus",     Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Known,     Dc2Habitat.Aquatic), // K68 live: E80 Mosasaurus, HP 2800, cleanest aquatic ⇒ low-weight regular donor (wave-only, water-flag-gated)
        new Dc2Species(0x0b, "E31", 0x00640000, "Plesiosaurus (regular/grunt form)", Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Known, Dc2Habitat.Aquatic, IsSetpiece: true), // K68 live: E31 grunt, HP 3000, inert on land ⇒ setpiece opt-in + wave-only
        new Dc2Species(0x0c, "E32", 0x00640000, "Plesiosaurus (regular/grunt form)", Dc2BaseClass.Shared640000, IsBoss: false, Confidence.Known, Dc2Habitat.Aquatic, IsSetpiece: true), // K68: E32 = 0x0c sub-variant of E31 ⇒ setpiece opt-in + wave-only
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

    /// <summary>Aquatic/non-land habitat — the crash-on-land set (K69). Water-level rooms native to one of
    /// these are protected unless <c>Dc2AllowWaterLevelEnemySwaps</c> lifts the block; a donor with this
    /// habitat may only be placed on the wave/preload path (never op-0x1a — K62b crash).</summary>
    public static bool IsWaterHabitat(Dc2Habitat h) => h is Dc2Habitat.Aquatic or Dc2Habitat.NonLand;

    /// <summary>True iff <paramref name="type"/>'s native species is water (aquatic/non-land) — the
    /// water-flag-gated half of <see cref="IsNonLandNativeType"/> (flyers stay blocked regardless).</summary>
    public static bool IsWaterNativeType(int type) => ForType(type) is { } s && IsWaterHabitat(s.Habitat);

    /// <summary>True iff <paramref name="type"/>'s native species is a <see cref="Dc2Habitat.Flyer"/> — the
    /// half of the non-land skip the water flag never lifts (land replacement spawns outside the hitbox).</summary>
    public static bool IsFlyerNativeType(int type) => ForType(type)?.Habitat is Dc2Habitat.Flyer;

    /// <summary>The Plesiosaurus GRUNT native types (0x0b=E31 / 0x0c=E32). Their native rooms
    /// (ST001/600/601/604) have invisible colliders that let a swapped donor attack the player THROUGH
    /// walls (playtest 2026-07-12), so — exactly like <see cref="IsFlyerNativeType"/> — they are unfit
    /// swap targets whatever the water flag says. Split out from the swap-SAFE Mosasaurus (0x0a) wave
    /// rooms (ST700/702/703/704), which the water flag legitimately opens. The Plesiosaurus BOSS (0x05)
    /// is generic-delivered (TYPE-0x10), never a native wave/spawn type, so it never reaches here.</summary>
    public static bool IsPlesiosaurusGruntNativeType(int type) => type is 0x0b or 0x0c;

    /// <summary>Native types whose room the wave planner leaves vanilla UNCONDITIONALLY — the water flag
    /// never lifts it: <see cref="IsFlyerNativeType"/> (donor spawns outside the hitbox) and
    /// <see cref="IsPlesiosaurusGruntNativeType"/> (donor attacks through the room's invisible colliders).
    /// The remaining water natives (Mosasaurus 0x0a) stay flag-gated via <see cref="IsWaterNativeType"/>.</summary>
    public static bool IsUnconditionalSkipNativeType(int type) =>
        IsFlyerNativeType(type) || IsPlesiosaurusGruntNativeType(type);

    /// <summary>True iff <paramref name="donor"/> is an aquatic/non-land species — crash-safe as a land
    /// spawn ONLY on the op-0x4f wave / op-0x23 preload path (K72), never an op-0x1a record (K62b).</summary>
    public static bool IsWaterDonor(Dc2Species donor) => IsWaterHabitat(donor.Habitat);

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
    /// <summary><paramref name="allowWater"/> <c>true</c> (the experimental
    /// <c>Dc2AllowWaterLevelEnemySwaps</c>) additionally admits AQUATIC species — Mosasaurus as a regular
    /// donor, the Plesiosaurus boss/grunts only with <paramref name="includeSetpiece"/> — all wave-only per
    /// the planner's placement gate. Default <c>false</c> keeps the pool LAND-only (byte-identical).</summary>
    public static IReadOnlyList<Dc2Species> DonorPool(bool includeSetpiece, bool includeBoss = false, bool allowWater = false) => All
        .Where(s => IsPoolMember(s, includeSetpiece, includeBoss, allowWater))
        .ToArray();

    /// <summary>True iff <paramref name="type"/> would be in <see cref="DonorPool"/> under these
    /// toggles — the SAME predicate the pool is built from, exposed so consumers (e.g. the app's
    /// weight-slider visibility, docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D7) can't drift from it.</summary>
    public static bool IsDonorPoolMember(int type, bool includeSetpiece, bool includeBoss, bool allowWater = false) =>
        ForType(type) is { } s && IsPoolMember(s, includeSetpiece, includeBoss, allowWater);

    private static bool IsPoolMember(Dc2Species s, bool includeSetpiece, bool includeBoss, bool allowWater) =>
        s.Confidence == Confidence.Known
        && (s.Habitat == Dc2Habitat.Land || (allowWater && IsWaterHabitat(s.Habitat)))
        && (!s.IsSetpiece || includeSetpiece)
        && (!s.IsBoss || includeBoss)
        && !IsCrashProneDonorType(s.Type);

    /// <summary>Species hard-excluded as DONORS in EVERY flag combination (independent of habitat/water flag),
    /// because their behavior crashes when run outside their native scene. Currently just E32 (TYPE 0x0c):
    /// crash RCA 2026-07-17 (DC2 dump 13-22-55, Dino2.exe) — a live E32 grunt swapped onto land AV'd reading
    /// NULL+0x40 at 0x00432061, its per-tick spawner AI (sub 0x431fe0, from state handler 0x430404)
    /// dereferencing a NULL spawn-anchor at actor+0x210 that only its native aquatic init populates. This is a
    /// BEHAVIOR fault, not a load/residency one, so the wave-preload path does NOT prevent it.
    /// Scoped to 0x0c ONLY: its sibling E31 (0x0b) is separately live-confirmed NOT to crash (user evidence
    /// 2026-07-17), so it stays wave-eligible. If another grunt/aquatic donor is later found to crash, add its
    /// TYPE here. See docs/reference/dc2/_registries/EXE-SYMBOLS.md.</summary>
    public static bool IsCrashProneDonorType(int type) => type == 0x0c;
}
