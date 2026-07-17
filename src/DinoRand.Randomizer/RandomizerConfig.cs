using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer;

/// <summary>
/// User-facing options. Combined with a <see cref="Seed"/>, this fully determines a
/// run. Kept flat and serializable so configs can be shared as text.
/// </summary>
public sealed class RandomizerConfig
{
    public bool RandomizeItems { get; set; } = true;
    public bool RandomizeEnemies { get; set; } = true;
    public bool RandomizeStartingInventory { get; set; } = false;

    /// <summary>
    /// DC1 only (off by default). Per-placement enemy maxHP override
    /// (docs/decisions/dc1/spawn/ENEMY-SPAWN-SYSTEM.md "Gap 4 — REVERSED", DC1-G2): writes a seeded,
    /// <see cref="EnemyDifficulty"/>-scaled HP into each eligible <c>0x20</c> spawn record's <c>+6</c>
    /// word (entity <c>+0x11A</c>), bypassing the engine's <c>{750,850,1000}</c> roll. A plain SCD file
    /// edit — no EXE/DDRAW/CE. Only positively-decoded dinosaurs, scripted/cutscene rooms excluded, so
    /// beatability is unaffected (HP gates nothing). Seed-encoded (byte 4 bit 6). CLI <c>--dc1-enemy-hp</c>.
    /// <see cref="Passes.EnemyHpRandomizer"/>.
    /// </summary>
    public bool RandomizeEnemyHp { get; set; } = false;

    /// <summary>
    /// DC2 only (on by default). Apply the reversible <c>ddraw.dll</c> MotionTrail over-brightening fix
    /// to the Classic REbirth wrapper when running Dino Crisis 2
    /// (<see cref="Dc2.Dc2MotionTrailInstaller"/> / <see cref="FileFormats.Exe.Dc2DdrawTrailPatch"/>).
    /// Idempotent and backed up (<c>ddraw.dll.bak</c>); a non-matching wrapper build is left untouched.
    /// Has no effect on DC1.
    /// </summary>
    public bool FixDc2MotionTrail { get; set; } = true;

    /// <summary>
    /// DC2 only (off by default). Include non-threatening <b>setpiece</b> enemies (the Triceratops,
    /// E70/TYPE&#160;0x09 — a no-damage scripted set-piece dino) in the cross-species donor pool. They are
    /// LAND and crash-safe (live-confirmed ST202, K62), but make a degenerate trash mob, so they are
    /// excluded unless this opt-in flag is set (CLI <c>--include-setpiece-enemies</c>). Selects
    /// <see cref="Dc2.Dc2SpeciesTable.DonorPool"/>(<c>includeSetpiece</c>) in
    /// <see cref="Dc2.Passes.Dc2EnemyRandomizer"/>; no effect on DC1.
    /// </summary>
    public bool IncludeDc2SetpieceEnemies { get; set; } = false;

    /// <summary>
    /// DC2 only (off by default). Include <b>boss</b> enemies (Tyrannosaurus E10/TYPE&#160;0x03 and
    /// Giganotosaurus E40/TYPE&#160;0x06) in the cross-species donor pool. Both are LAND and live-proven
    /// crash-safe (ST202, K61), but keep their boss-scale models/HP, so they make a degenerate trash mob —
    /// excluded unless this opt-in flag is set (CLI <c>--include-boss-enemies</c>). Adds the boss species to
    /// <see cref="Dc2.Dc2SpeciesTable.DonorPool"/>(<c>includeBoss</c>) in
    /// <see cref="Dc2.Passes.Dc2EnemyRandomizer"/>; composes with <see cref="IncludeDc2SetpieceEnemies"/>;
    /// no effect on DC1.
    /// </summary>
    public bool IncludeDc2BossEnemies { get; set; } = false;

    /// <summary>
    /// DC2 only (EXPERIMENTAL, off by default). "Allow Enemy swaps in the Water Levels" — lifts the
    /// aquatic-room protection and admits aquatic donors, all gated to the crash-safe op-0x4f wave /
    /// op-0x23 preload path (K72, docs/decisions/dc2/enemies/DC2-AQUATIC-LAND-UNLOCK-FEASIBILITY.md). ON:
    /// (a) the Mosasaurus wave rooms (ST700/702/703/704, <see cref="Dc2.Dc2AquaticRooms"/>) become
    /// swappable — LAND donors placed on their wave descriptors (live-proven safe); (b) aquatic species
    /// become eligible donors (wave-only per the planner's placement gate) — Mosasaurus enters the default
    /// weighted pool at low weight, the Plesiosaurus boss/grunts additionally need
    /// <see cref="IncludeDc2SetpieceEnemies"/>. OFF (default): aquatic rooms hard-blocked AND no aquatic
    /// species used as donors — byte-identical to before the feature. Threads into
    /// <see cref="Dc2.Dc2SpeciesTable.DonorPool"/> + <see cref="Dc2.Dc2CrossSpeciesPlanner"/> +
    /// <see cref="Dc2.Passes.Dc2EnemyRandomizer"/>. CLI <c>--dc2-allow-water-swaps</c>; no effect on DC1.
    /// </summary>
    public bool Dc2AllowWaterLevelEnemySwaps { get; set; } = false;

    /// <summary>
    /// DC2 only. How the enemy pass picks each room's donor species
    /// (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md): <see cref="Dc2.Dc2EnemyDistributionMode.Weighted"/>
    /// (default — per-species weights bias the per-room pick; curated defaults reproduce the legacy
    /// uniform pick exactly) or <see cref="Dc2.Dc2EnemyDistributionMode.Fixed"/> (one pinned donor,
    /// <see cref="Dc2FixedSpeciesType"/>, for every eligible room). Seed-encoded (AppSeed byte 11).
    /// CLI <c>--dc2-enemy-mode</c> / <c>--dc2-fixed-species</c>; no effect on DC1.
    /// </summary>
    public Dc2.Dc2EnemyDistributionMode Dc2EnemyMode { get; set; } = Dc2.Dc2EnemyDistributionMode.Weighted;

    /// <summary>
    /// DC2 only. The pinned donor TYPE for <see cref="Dc2.Dc2EnemyDistributionMode.Fixed"/> mode
    /// (e.g. <c>0x03</c> = an all-T-Rex run). Must be a Known+LAND species
    /// (<see cref="Dc2.Dc2SpeciesTable.DonorPool"/>(<c>true</c>, <c>true</c>) member — a pin IS the
    /// boss/setpiece opt-in); per-room safety guards still apply, so rooms where the pin is invalid
    /// stay vanilla. Ignored in Weighted mode.
    /// </summary>
    public int? Dc2FixedSpeciesType { get; set; }

    /// <summary>
    /// DC2 only. Per-species weight overrides (TYPE → 0–15) for
    /// <see cref="Dc2.Dc2EnemyDistributionMode.Weighted"/> mode; <c>null</c> (default) = the curated
    /// registry defaults (<c>data/dc2/enemy-distribution.json</c>). Weight 0 excludes the species
    /// from the draw (a room whose every valid donor is weight-0 stays vanilla — never a uniform
    /// fallback). Overlaid onto the defaults by <see cref="Dc2.Dc2EnemyDistribution.EffectiveWeights"/>;
    /// weights bias the pick among pool MEMBERS — membership still comes from the boss/setpiece
    /// toggles. Seed-encoded (AppSeed bytes 12–15). CLI <c>--dc2-weight name=n</c> (repeatable).
    /// </summary>
    public IReadOnlyDictionary<int, byte>? Dc2SpeciesWeights { get; set; }

    /// <summary>
    /// DC2 only (off by default). Randomize raptor tier variants (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §4):
    /// static op-0x1a raptor spawns get per-spawn weighted variant draws (room-file edit); wave
    /// rooms get desc+4 jitter plus the seeded wave pair-table exe patch. Independent of
    /// <see cref="RandomizeEnemies"/> (spawns converted away from raptor by the enemy pass are
    /// skipped). CLI <c>--dc2-raptor-tiers</c>.
    /// </summary>
    public bool Dc2RandomizeRaptorTiers { get; set; } = false;

    /// <summary>
    /// DC2 only (off by default). Randomize/customize the starting main weapon
    /// (docs/decisions/dc2/loadout/DC2-STARTING-LOADOUT-PLAN.md): patches the new-game bootstrap equip immediates'
    /// weapon-id bytes in <c>Dino2.exe</c> (subweapon bytes never touched — Machete/Large Stun Gun
    /// always kept). Seed-encoded (byte 22). CLI <c>--dc2-randomize-start-weapon</c>.
    /// </summary>
    public bool Dc2RandomizeStartWeapon { get; set; } = false;

    /// <summary>DC2 only. Explicit Dylan starting main-weapon id (must be in
    /// <see cref="FileFormats.Exe.Dc2StartingLoadoutPatch.DylanWeaponIds"/>); <c>null</c> = random
    /// from the band. Ignored unless <see cref="Dc2RandomizeStartWeapon"/>.</summary>
    public byte? Dc2DylanStartWeaponId { get; set; }

    /// <summary>DC2 only. Explicit Regina starting main-weapon id (band
    /// <see cref="FileFormats.Exe.Dc2StartingLoadoutPatch.ReginaWeaponIds"/>); <c>null</c> = random.
    /// Ignored unless <see cref="Dc2RandomizeStartWeapon"/>.</summary>
    public byte? Dc2ReginaStartWeaponId { get; set; }

    /// <summary>DC2 only (off by default). Add-and-equip mode for the starting weapon: also installs
    /// the weapon-ring div-0 zero-guard (<see cref="FileFormats.Exe.Dc2WeaponRingGuardPatch"/>), which
    /// makes every id in each character's band a safe pick (SUBs, other-character mains, the fire-empty
    /// Grenade Gun) instead of only the owned-MAIN subset. Seed-encoded (byte 22 bit 6). Ignored unless
    /// <see cref="Dc2RandomizeStartWeapon"/>. DC2-WEAPON-SYSTEM-INVESTIGATION.md §3.</summary>
    public bool Dc2AddAndEquipStartWeapon { get; set; } = false;

    /// <summary>
    /// DC2 only (off by default). Shuffle the shop economy (docs/decisions/dc2/shop/DC2-SHOP-RANDO-PLAN.md):
    /// permutes the 11 for-sale items' retail prices and stock-unlock shop-level bitmasks inside
    /// <c>Dino2.exe</c> (reversible <c>.bak</c>-backed exe patch, rebirth build only). CLI
    /// <c>--dc2-shuffle-shop</c>.
    /// </summary>
    public bool Dc2ShuffleShop { get; set; } = false;

    /// <summary>
    /// DC2 only (off by default). Scramble the sub-level elevator security-code candidates
    /// (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §3, K108): writes 8 distinct seed-derived
    /// 4-digit codes (digits 0–5) into the setup function's imm32 table inside <c>Dino2.exe</c>.
    /// Displayed==checked is automatic (single runtime copy). Reversible <c>.bak</c>-backed exe
    /// patch, rebirth build only. CLI <c>--dc2-scramble-puzzle-codes</c>.
    /// </summary>
    public bool Dc2ScramblePuzzleCodes { get; set; } = false;

    /// <summary>
    /// DC2 only (off by default). Shuffle the stungun-circuit blink choreography
    /// (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 2, K110): rewrites the scripted blink
    /// box-id sequences of ST607 (generator stabilizer) and ST402 (Missile Silo bridge) with
    /// seed-derived orders — same length, every box at least once, no adjacent repeats. Room-file
    /// edit (backup-and-swap). CLI <c>--dc2-shuffle-circuits</c>.
    /// </summary>
    public bool Dc2ShuffleCircuits { get; set; } = false;

    /// <summary>
    /// DC2 only. The GUI's single "Randomize Puzzles" flag over the two puzzle subflags
    /// (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md): setting it drives BOTH
    /// <see cref="Dc2ScramblePuzzleCodes"/> and <see cref="Dc2ShuffleCircuits"/>; reading it ORs
    /// them, so a config carrying only one subflag (a standalone CLI lever / a legacy seed bit)
    /// still presents as puzzles-randomized. Not itself seed-encoded — it rides the subflag bits
    /// (byte 16 bits 3/4). Same master/subflag pattern as ShuffleKeyItems → RelocateDdkDiscs.
    /// </summary>
    public bool Dc2RandomizePuzzles
    {
        get => Dc2ScramblePuzzleCodes || Dc2ShuffleCircuits;
        set { Dc2ScramblePuzzleCodes = value; Dc2ShuffleCircuits = value; }
    }

    /// <summary>
    /// DC2 only (<b>off by default</b>). Enable Classic REbirth's own door-transition skip by
    /// writing <c>DoorSkip = 1</c> into the game root's <c>config.ini</c> <c>[DLL]</c> section
    /// (<see cref="Dc2.Dc2DoorSkipInstaller"/> — K115). A user-config passthrough, not a game edit:
    /// never seed-encoded, restore = flip the ini key back. CLI <c>--dc2-door-skip</c>.
    /// </summary>
    public bool Dc2DoorSkip { get; set; } = false;

    /// <summary>
    /// DC1 only (<b>off by default — pilot rooms not yet human-witnessed in game</b>). Shorten
    /// cutscenes to their side effects: rewrite each whitelisted script-authored cutscene bracket
    /// (<c>SetFlag(2,2,1)…SetFlag(2,2,0)</c>, STATIC-SCD-RE cont.74) in place so every flag write
    /// and item record still executes while the choreography is jumped over
    /// (<see cref="FileFormats.Stage.CutsceneShortener"/>). Conservative whitelist — brackets with
    /// gosub/task-spawn/goto-sub or branch hazards are left untouched (65 of 171 corpus brackets
    /// qualify). CLI <c>--dc1-shorten-cutscenes</c>. docs/decisions/cross/CUTSCENE-SKIP-FEASIBILITY.md §9.3.
    /// </summary>
    public bool ShortenCutscenes { get; set; } = false;

    /// <summary>
    /// DC2 only. Additionally emit each touched room's edits as a Classic REbirth
    /// <c>patch\ST*.d2p</c> word-patch into <c>&lt;out&gt;\patch\</c>
    /// (<see cref="FileFormats.Stage.Dc2.Dc2D2pWriter"/> — the wrapper's decoded non-destructive
    /// override, docs/parity/NONDESTRUCTIVE-INSTALL-PARITY.md). Purely additive: the room files and
    /// every other output byte are unchanged by this flag. An install/output-shape option like
    /// <see cref="FixDc2MotionTrail"/>, NOT seed-encoded. CLI <c>--dc2-emit-d2p</c>.
    /// </summary>
    public bool Dc2EmitD2pPatches { get; set; } = false;

    /// <summary>
    /// DC2 only. Per-variant weight overrides (variant 0–7 → 0–15) for the raptor tier draw;
    /// <c>null</c> = the registry defaults (<c>data/dc2/raptor-tiers.json</c>: common tiers 4, the
    /// variant-5 blue/super raptor 1). Weight 0 excludes a variant. Overlaid by
    /// <see cref="Dc2.Dc2RaptorTierTable.EffectiveWeights"/>. CLI <c>--dc2-raptor-weight v=n</c>.
    /// </summary>
    public IReadOnlyDictionary<int, byte>? Dc2RaptorTierWeights { get; set; }

    /// <summary>
    /// DC2 only. How raptor colour relates to stats per room (RAPTOR-TIER-RE.md §4b):
    /// <see cref="Dc2.Dc2RaptorColourMode.RoomTier"/> (default — one variant per room, colour ==
    /// strength) or <see cref="Dc2.Dc2RaptorColourMode.MixedTiers"/> (colour = strongest tier in
    /// the room, others may be weaker). Seed-encoded (byte 17 bit 5). CLI <c>--dc2-raptor-colour</c>.
    /// </summary>
    public Dc2.Dc2RaptorColourMode Dc2RaptorColourMode { get; set; } = Dc2.Dc2RaptorColourMode.RoomTier;

    /// <summary>
    /// DC2 only. The "Blue Raptor Spawn Condition": the max-combo hit count that arms the natural
    /// variant-5 super-raptor for the next room (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §3). 1–20; 20 = vanilla
    /// (imm8 0x13 at VA 0x41E5A6, <see cref="FileFormats.Exe.Dc2RaptorPatch.ApplyComboThreshold"/>,
    /// applied at install). CLI <c>--dc2-blue-combo</c>.
    /// </summary>
    public int Dc2BlueRaptorComboThreshold { get; set; } = FileFormats.Exe.Dc2RaptorPatch.VanillaComboThreshold;

    /// <summary>
    /// DC2 only (off by default). Make a randomizer-injected T-Rex killable: patches Dino2.exe with a
    /// hook + code cave that forces the actor phase to Extra-Crisis (2), disabling the campaign
    /// survival clamp, for any E10 whose current room is NOT a vanilla T-Rex boss room (ST200/ST903).
    /// Applied in place at install (backup-protected .bak); the two scripted bosses are left untouched.
    /// docs/decisions/dc2/enemies/DC2-TREX-KILLABLE-LEVER-PLAN.md. CLI <c>--dc2-trex-killable</c>.
    /// </summary>
    public bool Dc2MakeTrexKillable { get; set; }

    /// <summary>
    /// DC2 only (off by default). Keep a randomizer-injected E80 Mosasaurus's grab IN-BOUNDS in land
    /// rooms: patches Dino2.exe with hooks + code caves that skip only the player-Y write of the mosa's
    /// grab (states 19/20) for any TYPE-0x0a actor whose current room is NOT a native Mosasaurus room
    /// (ST700/702/703/704). The horizontal grab is preserved; only the out-of-bounds vertical launch is
    /// removed, and the four native aquatic encounters are left byte-identical. Applied in place at
    /// install (backup-protected .bak). docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md.
    /// CLI <c>--dc2-mosa-no-grab</c>.
    /// </summary>
    public bool Dc2SuppressMosaGrab { get; set; }

    /// <summary>
    /// DC2 only (off by default). Stop a randomizer-injected E80 Mosasaurus from knocking the player OUT
    /// OF BOUNDS in land rooms: patches Dino2.exe with a hook + code cave that skips the shared knockback
    /// applicator (VA 0x452D84) only when the shoved actor is the player, the attacker is a TYPE-0x0a
    /// Mosasaurus, and the room is NOT a native Mosasaurus room (ST700/702/703/704). Enemy↔enemy
    /// separation, non-aquatic knockback, and the four native aquatic encounters are left byte-identical.
    /// Applied in place at install (backup-protected .bak). Distinct from the grab lever
    /// (<see cref="Dc2SuppressMosaGrab"/>) — a separate OOB channel, decoded live 2026-07-13 (K105).
    /// docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md §8.5. CLI <c>--dc2-mosa-no-knockback</c>.
    /// </summary>
    public bool Dc2SuppressMosaKnockback { get; set; }

    /// <summary>
    /// DC2 only (off by default). Stop a randomizer-injected E80 Mosasaurus from flinging the player OUT OF
    /// BOUNDS in land rooms by changing WHICH behavior it runs: patches Dino2.exe with a hook + code cave at
    /// the E80 attack-pattern hub (state-1 <c>0x43fc80</c>) that, for a TYPE-0x0a actor outside the native
    /// Mosasaurus rooms (ST700/702/703/704), redirects the wide-turn tail strike (attack-pattern
    /// <c>byte[esi+0x55]==2</c>) to the narrow bite (pattern 0), so the mosa never performs the OOB move.
    /// Touches no player-movement code; the four native aquatic encounters stay byte-identical. Applied in
    /// place at install (backup-protected .bak). Distinct from the grab (<see cref="Dc2SuppressMosaGrab"/>)
    /// and knockback (<see cref="Dc2SuppressMosaKnockback"/>) levers — the behavior-layer approach, K106.
    /// docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md §9. CLI <c>--dc2-mosa-tail-to-bite</c>.
    /// </summary>
    public bool Dc2RedirectMosaTail { get; set; }

    /// <summary>
    /// DC2 only (off by default). Make a randomizer-injected Triceratops killable without crashing:
    /// patches Dino2.exe to remap E70's out-of-range death animation index (8 → 7) so the setpiece
    /// model's death path binds a valid clip instead of reading past its package table (which crashes).
    /// Applied in place at install (backup-protected .bak). The remapped instruction only ever runs in
    /// E70's own death handler, so it is a no-op for every other actor.
    /// docs/decisions/dc2/crash-rcas/DC2-ST001-TRICERATOPS-WAVE-DEDICATED-BASE-CRASH-RCA.md §7b.
    /// CLI <c>--dc2-triceratops-killable</c>.
    /// </summary>
    public bool Dc2MakeTriceratopsKillable { get; set; }

    /// <summary>
    /// DC2 only (off by default). Stop a randomizer-injected Inostrancevia (E50, TYPE 0x0e — a DEFAULT
    /// donor) from crashing when the PSX-recompiled emergence/burst emitter runs in a room that armed no
    /// spawn-descriptor list for it: patches Dino2.exe with a NULL-cursor guard on the shared emitter's
    /// tick driver (hook 0x4131d5) so it skips the tick instead of dereferencing the NULL cursor. Applied in
    /// place at install (backup-protected .bak). The guard is species-agnostic (the driver 0x4131d0 is a
    /// shared component, ~10 actor-class vtables) and byte-identical whenever the emitter is armed, so it is
    /// a no-op for every normal spawn. docs/decisions/dc2/crash-rcas/DC2-INOSTRA-SPAWN-DESCRIPTOR-NULL-RCA.md.
    /// CLI <c>--dc2-inostra-spawn-guard</c>; auto-applied for ANY DC2 cross-species run (zero-cost net).
    /// </summary>
    public bool Dc2MakeInostraSpawnSafe { get; set; }

    /// <summary>
    /// DC2 only (Dylan = stock, default). Which character main-game Dylan renders as: his six
    /// per-weapon <c>WP&lt;n&gt;A.DAT</c> slots serve Gail's or Rick's Extra Crisis graft files (the
    /// engine-native re-skin, visual-only — weapon behavior untouched), paired with the WP-gate exe
    /// patch at install (docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §7–9). Consumed by
    /// <see cref="Dc2.Passes.Dc2PlayerModelSwap"/> (CLI <c>--dc2-character-skin</c>); seed-encoded
    /// (byte 11 bits 6–7); <see cref="Dc2.Passes.Dc2CharacterSkin.Random"/> resolves from the seed.
    /// This replaces the withdrawn whole-file Regina ↔ Dylan swap
    /// (docs/decisions/dc2/models/DC2-PLAYER-SWAP-FIRE-CRASH-RCA.md).
    /// </summary>
    public Dc2.Passes.Dc2CharacterSkin Dc2CharacterSkin { get; set; } = Dc2.Passes.Dc2CharacterSkin.Stock;

    /// <summary>DC2 only (Stock = Regina, default). Which character Regina renders as — same
    /// mechanism over her WP row (cross-rig graft, in-game verified; cutscene skin-paste is known
    /// cosmetic TECH DEBT). Seed-encoded (byte 16 bits 0–1); CLI <c>--dc2-regina-skin</c>.</summary>
    public Dc2.Passes.Dc2CharacterSkin Dc2ReginaSkin { get; set; } = Dc2.Passes.Dc2CharacterSkin.Stock;

    /// <summary>
    /// The weapon item ids Regina begins a new game with. <c>null</c> (default) ⇒ use the game's vanilla
    /// set (<see cref="GameDefinition.StartingWeaponIds"/> = the Handgun for DC1). A non-null set overrides
    /// it (e.g. <c>{0x01}</c> = start with the Shotgun instead). <b>The empty set (a weaponless start) is
    /// not deliverable yet</b> — the EXE patch only clears the group-11 owned-flag, but the engine re-equips
    /// a default Handgun through an as-yet-undecoded path (confirmed in-game), so the EXE lever
    /// (<see cref="Install.GameInstaller.PatchExeStartingInventory"/>) rejects a weaponless request. This is
    /// an <b>install-time override</b>, not part
    /// of the shared seed string (a free-form set doesn't fit the byte budget), so it doesn't transfer
    /// between machines via a seed — set it the same way on each. The EXE patch grants exactly this set
    /// (<see cref="Install.GameInstaller.PatchExeStartingInventory"/>), and the world item pass force-places
    /// any vanilla starting weapon this set <i>removes</i> into an early, no-key-reachable spot so the seed
    /// stays beatable. docs/reference/dc1/items/STARTING-INVENTORY.md.
    /// </summary>
    public IReadOnlyCollection<int>? StartingWeapons { get; set; }

    /// <summary>Phase 3. Validate the goal room stays reachable under the door-graph key logic
    /// (<see cref="Logic.KeyItemPlacer"/>) before running content shuffles. On by default.</summary>
    public bool EnsureBeatable { get; set; } = true;

    /// <summary>Phase 3. Relocate the door-gating key items (Entrance/BG Area/C.O. Area keys, Key
    /// Card Lv A) among their spots via the progression flood-fill, keeping every seed beatable.
    /// Off by default — when off, key items stay in their vanilla, flag-stable spots.</summary>
    public bool ShuffleKeyItems { get; set; } = false;

    /// <summary>
    /// DC1 (off by default). Widen the <see cref="ShuffleKeyItems"/> pool so a relocated door key may
    /// also land in a <b>static ammo/health pickup</b>, not only another door-key spot — the map.json
    /// <c>scatterTargets</c> slots (<see cref="Graph.NodeItem.IsScatterTarget"/>), which exclude every
    /// runtime-armed / flag-gated / unresolved-trigger / relocation-twin slot so a key is never seated
    /// behind a missable condition (docs/decisions/dc1/items/KEY-ITEM-SCATTER-DATA-AUDIT.md). Needs
    /// <see cref="ShuffleKeyItems"/> on. Placement stays progression-safe (the symmetric
    /// <see cref="Logic.KeyItemPlacer.Place"/>); displaced consumables are conserved. Off ⇒ byte-identical
    /// to the door-key-only shuffle. CLI <c>--scatter-key-items</c>.
    /// </summary>
    public bool ShuffleKeyItemsIntoPickups { get; set; } = false;

    /// <summary>
    /// DC1. Widen the <see cref="ShuffleKeyItems"/> pool with the in-scope progression keys that gate via
    /// the map.json overlay <c>requires</c> (edge item AND-gates) rather than the door TYPE byte — the
    /// <b>DDK Input/Code disc pairs</b> (<see cref="Definitions.GameDefinition.OverlayRelocationKeyIds"/> =
    /// ids 0x62–0x6f), the 7 disc-pair-gated edges. The band excludes B1 Room Key 0x2F (also a requires
    /// gate, out of scope) and panel keys (which gate an unmodeled container, no edge); Fixed/twin/co-located
    /// records stay excluded by the same machinery as door-TYPE keys. Placement stays progression-safe (the
    /// pair-aware <see cref="Logic.KeyItemPlacer.Place"/>) and every disc is conserved.
    /// <para><b>Product default: on whenever key items are shuffled.</b> The GUI and CLI config-build set
    /// <c>RelocateDdkDiscs = ShuffleKeyItems</c> (no separate GUI toggle) — so with the key shuffle off this
    /// is off and byte-identical to before. The flag stays independent (default false) so tests can exercise
    /// DDK relocation in isolation. Not seed-encoded (byte 4 is full; it is derived from the shuffle bit).</para>
    /// docs/decisions/dc1/items/PROGRESSION-KEY-RELOCATION-RESEARCH.md.
    /// </summary>
    public bool RelocateDdkDiscs { get; set; } = false;

    /// <summary>
    /// DC1 (<b>on by default</b>). Keep important items out of HIDDEN pickup spots, using the decoded
    /// ground-visual class (map.json <c>itemVisuals</c> → <see cref="Graph.NodeItem.Visual"/>;
    /// STATIC-SCD-RE cont.72 — 59 of the 154 vanilla pickups are interaction-only: no ground visual,
    /// collectable only by examining the spot). Two effects: (1) the item pass pool-places weapons and
    /// weapon parts only into spots with a visual; (2) the key shuffle applies "no worse than vanilla" —
    /// an interaction-only spot may only receive a key whose own vanilla home was interaction-only, so a
    /// visible key never becomes invisible (and a hidden key may become visible). Consumable fill is
    /// unaffected (vanilla hides consumables too). Turning this OFF restores the previous behaviour.
    /// CLI <c>--allow-hidden-spots</c> (disables). docs/decisions/dc1/items/PICKUP-VISUAL-PLACEMENT-PLAN.md.
    /// </summary>
    public bool AvoidHiddenPickupSpots { get; set; } = true;

    /// <summary>
    /// DC1 Lever A (<b>off by default — in-game render not yet human-witnessed</b>). When a relocated key
    /// or weapon lands in a spot whose ground visual does not match it — an interaction-only spot (no
    /// visual) or a bespoke-mesh spot (shows the OLD item's model) — rewrite that spot's ground visual to
    /// the shared generic pickup panel (display slot + model <c>0x80180000</c>), so what the player sees
    /// matches the randomizer. Fail-closed: an interaction-only spot needs a free display-node pool slot
    /// (<see cref="Stage.RoomScript.DisplaySlotPoolCap"/>); a room with none free is left untouched (still
    /// vanilla-invisible, no worse than today). Records with a deferred-visual flag (<c>rec+0x28≠0</c>) are
    /// skipped. Independent of <see cref="AvoidHiddenPickupSpots"/>. CLI <c>--normalize-pickup-visuals</c>.
    /// docs/decisions/dc1/items/PICKUP-GROUND-MODEL-FEASIBILITY.md (Lever A).
    /// </summary>
    public bool NormalizePickupVisuals { get; set; } = false;

    /// <summary>
    /// DC1 Lever B (<b>ON by default for the in-game witness session — the render is not yet
    /// human-confirmed; disable with the flag off if a seed misbehaves</b>). On top of Lever A: a
    /// relocated key/weapon whose id has a vanilla bespoke ground mesh anywhere in the game gets that
    /// donor mesh imported into its destination room (mesh appended to the RDT, texture UV sub-rect +
    /// CLUT re-uploaded at a free VRAM column, texrefs rewritten), so the pickup shows its OWN model
    /// instead of the generic panel. Ids without a donor and every failure path (no free VRAM, RDT
    /// ceiling) fall back to the Lever-A generic panel — implies the Lever-A marking even when
    /// <see cref="NormalizePickupVisuals"/> is off. CLI <c>--pickup-ground-models</c> (redundant while
    /// the default is on). docs/decisions/dc1/items/PICKUP-GROUND-MODEL-FEASIBILITY.md ("Lever B
    /// plan"), STATIC-SCD-RE cont.73.
    /// </summary>
    public bool ImportPickupModels { get; set; } = true;

    /// <summary>Phase 3. Off until the door-graph pass lands.</summary>
    public bool RandomizeDoors { get; set; } = false;

    /// <summary>
    /// Phase 4 (experimental, off by default). Master toggle for cutscene voice randomization — the DC1
    /// port of BioRand's voice rando (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md). When on, the swappable cast
    /// (<see cref="Voice.VoiceSwapPlanner.SwappableCast"/> = Regina/Rick/Gail/Kirk) each speaks as a donor
    /// actor: a per-character pin from <see cref="VoiceDonors"/> when set, else a random donor per seed.
    /// The swap is <b>installed with the seed</b> (loose voice banks under <c>Sound\VOICE\</c>, via the
    /// <c>GameInstaller</c> backup contract — reversed by Restore). Off by default; needs
    /// <see cref="VoicePacksRoot"/> to have a donor source. Model swap is deferred (plan §9).
    /// </summary>
    public bool RandomizeVoices { get; set; } = false;

    /// <summary>
    /// Phase 4. Per-character donor <b>pins</b>: target actor name (lower-case, e.g. <c>"regina"</c>) →
    /// donor actor name (open, possibly cross-game, e.g. <c>"claire"</c>). A target absent from the map (or
    /// mapped to a blank/<c>"random"</c> value) draws a random eligible donor each seed. Only honoured when
    /// <see cref="RandomizeVoices"/> is on; only targets in <see cref="Voice.VoiceSwapPlanner.SwappableCast"/>
    /// apply. Set by the App's per-character dropdowns (docs/decisions/dc1/voice/VOICE-UI-PLAN.md). Not part of the shared
    /// seed string (a free-form map doesn't fit the byte budget) — persisted as an App setting instead.
    /// </summary>
    public IReadOnlyDictionary<string, string>? VoiceDonors { get; set; }

    /// <summary>
    /// Phase 4. Allow donor voice clips from <i>other games</i>' datapacks (RE1/2/3, …) into the pool
    /// (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §5.2). <c>false</c> (default) keeps the pool DC1-cast-only; <c>true</c>
    /// lets any character speak in another game's actor's voice. Independent of the toggles above.
    /// </summary>
    public bool IncludeCrossGameVoices { get; set; } = false;

    /// <summary>
    /// Draw "DINORAND V&lt;version&gt; / SEED &lt;n&gt;" into the title screen(s) — DC1
    /// <c>Data\t_image.imd</c>, DC2 <c>TITLE.DAT</c>/<c>TITLE2.DAT</c> (BioRand parity;
    /// docs/decisions/cross/SEED-WATERMARK-PLAN.md). Always on with any seeded randomization —
    /// no GUI/CLI toggle; this bool exists so tests can isolate other passes' output
    /// (the <see cref="RelocateDdkDiscs"/> pattern). Reversed by <c>--restore</c>.
    /// </summary>
    public bool TitleWatermark { get; set; } = true;

    /// <summary>
    /// Phase 4. Filesystem root holding the BioRand-layout donor datapacks (each subdir a pack;
    /// docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.1). The voice emission loads donor clips from here via
    /// <see cref="Voice.VoiceDataPack.LoadAll"/>. <c>null</c> (default) ⇒ no donor source, so the gated
    /// pass produces nothing even when its toggles are on. Set by the App/CLI (§12.5).
    /// </summary>
    public string? VoicePacksRoot { get; set; }

    /// <summary>
    /// Import external, mood-tagged music into the DC1 BGM slots (docs/decisions/cross/BGM-RANDO-PLAN.md).
    /// When on, <see cref="Bgm.BgmRandomizer"/> draws same-tag donor tracks from <see cref="BgmPacksRoot"/>,
    /// transcodes them to each <c>Sound/BGM/</c> slot's RIFF format, and installs them with the seed via the
    /// <c>GameInstaller</c> loose-file backup contract (reversed by Restore). Off by default; a no-op without a
    /// <see cref="BgmPacksRoot"/> that holds music. Independent of the existing <c>--shuffle-bgm</c> exe lever
    /// (which permutes the game's own tracks and needs no assets).
    /// </summary>
    public bool RandomizeBgm { get; set; } = false;

    /// <summary>
    /// Filesystem root holding BGM datapacks for import, each pack laid out <c>&lt;pack&gt;/data/bgm/&lt;tag&gt;/*.{ogg,wav}</c>
    /// (folder = mood tag; docs/decisions/cross/BGM-RANDO-PLAN.md). Loaded via <see cref="Bgm.BgmDataPack.LoadAll"/>.
    /// <c>null</c> (default) ⇒ no donor source, so <see cref="RandomizeBgm"/> produces nothing.
    /// </summary>
    public string? BgmPacksRoot { get; set; }

    /// <summary>
    /// Phase 2 (experimental, off by default). Import a <i>foreign</i> dinosaur species into a room
    /// that did not ship with it, by copying its model+motion into the room's RDT and repointing one
    /// enemy record (docs/decisions/dc1/enemies/CROSS-ROOM-SPECIES-PLAN.md, increment 1: geometry only — imported species may
    /// render mis-coloured until textures are imported). Conservative: grounded species only, one per
    /// room, scripted/cutscene rooms excluded. Not yet CE-validated, hence off and experimental.
    /// </summary>
    public bool CrossRoomEnemySpecies { get; set; } = false;

    /// <summary>
    /// Opt-in (default off; DC1 only). Cutscene-safe enemy randomization: rooms in the derived
    /// choreography census (<c>data/dc1/cutscene-rooms.json</c> <c>flagged</c> tier — a script
    /// binds an enemy slot and installs a scripted waypoint/completion-flag behavior on it,
    /// STATIC-SCD-RE cont.49/59) are excluded from the in-room (model, motion) permute and from
    /// cross-species imports, and receive the orthogonal <b>palette-tint</b> fallback instead
    /// (a seeded donor room's type-2 CLUT entry — the cont.51/57 "Blue Raptor" lever). Off keeps
    /// every existing seed byte-identical. Like <see cref="Dc2AllowWaterLevelEnemySwaps"/>, this
    /// flag is NOT seed-encoded.
    /// </summary>
    public bool Dc1CutsceneSafeEnemies { get; set; } = false;

    /// <summary>
    /// Opt-in (default off). Allow the installer to patch <c>DINO.exe</c> (back it up first; reversed
    /// by <c>--restore</c>). The exe holds the per-stage enemy-set record table, so this is the only
    /// lever for <i>cross-species</i> changes a room never natively hosts — a patch is <b>stage-scoped</b>
    /// (<c>index = roomId &gt;&gt; 8</c>). Off keeps the file-only backup contract intact for everyone
    /// not opting in. The concrete repoints are supplied at install time (CLI <c>--exe-patch</c>);
    /// see docs/decisions/dc1/exe/EXE-PATCH-PER-ROOM-PLAN.md and docs/decisions/dc1/enemies/REGION-INDEX-MAP.md.
    /// </summary>
    public bool PatchExe { get; set; } = false;

    /// <summary>0 = light, 1 = heavy. Scales enemy difficulty/density.</summary>
    public double EnemyDifficulty { get; set; } = 0.5;

    /// <summary>
    /// Phase 1. Item-pool mode. <c>true</c> (default) = replace every non-key pickup with a
    /// weighted-random item from the pool (the category ratios below bias the draw). <c>false</c> =
    /// keep the game's existing non-key items but shuffle which spot each lands in. Mirrors BioRand's
    /// "custom item pool" toggle. <see cref="Passes.ItemRandomizer"/>.
    /// </summary>
    public bool ReplaceItemPool { get; set; } = true;

    /// <summary>
    /// Phase 1. Whether replace-mode pool-places the game's weapons + parts into reachable spots
    /// (BioRand-style, with linked ammo). <c>true</c> (default) keeps weapons in the seed; <c>false</c>
    /// leaves weapon/part pickups as their vanilla item (never overwritten with consumables either way
    /// — that clobber is fixed). <see cref="Passes.ItemRandomizer"/>, docs/decisions/cross/ITEM-RANDO-PLAN.md.
    /// </summary>
    public bool RandomizeWeapons { get; set; } = true;

    /// <summary>
    /// Phase 1. Relative bias for Ammo (<see cref="Definitions.ItemCategory.Ammo"/>) pickups, 0–31.
    /// The <i>per-item</i> weights inside a category are always the pool's intrinsic ones; this dial
    /// only tilts the Ammo-vs-Health balance. Equal Ammo/Health ratios therefore reproduce the pool's
    /// implied split exactly (uniform scaling), which is the default. As a belt-and-suspenders, when
    /// <b>both</b> ratios are 0 (e.g. a legacy seed) the randomizer also falls back to intrinsic
    /// weights. Replace-mode only.
    /// </summary>
    public byte RatioAmmo { get; set; } = 16;

    /// <summary>Phase 1. Relative bias for Health pickups, 0–31. See <see cref="RatioAmmo"/>.</summary>
    public byte RatioHealth { get; set; } = 16;

    /// <summary>
    /// Phase 1. Average-quantity dial for ammo stacks, 0–7. 0 (default) keeps each pickup's vanilla
    /// amount; higher values scale ammo stack sizes <i>up</i> (×<c>1 + 0.5·n</c>). Paired with
    /// <see cref="AmmoReduction"/> (the <i>down</i> side) to form one signed dial in the UI. Honoured by
    /// <see cref="Passes.ItemRandomizer"/>.
    /// </summary>
    public byte AmmoQuantity { get; set; }

    /// <summary>
    /// Phase 1. The <i>reduction</i> side of the ammo average-quantity dial, 0–7. 0 (default) keeps the
    /// vanilla amount; higher values scale ammo stack sizes <i>down</i> (×<c>1 / (1 + 0.5·n)</c>, floored
    /// at 1). Mutually exclusive with <see cref="AmmoQuantity"/> by construction (the UI sets one or the
    /// other), so the net effect is a signed level <c>AmmoQuantity − AmmoReduction</c> where 0 is vanilla.
    /// Kept as a separate field (rather than widening <see cref="AmmoQuantity"/>) so the "more" side and
    /// every existing seed stay byte-identical. Honoured by <see cref="Passes.ItemRandomizer"/>.
    /// </summary>
    public byte AmmoReduction { get; set; }

    /// <summary>
    /// Phase 1 (§7.3). Probability [0,1] that a weapon-upgrade <i>part</i> is placed, when its base
    /// weapon is in the seed. <c>1.0</c> (default) keeps every vanilla upgrade part (current behavior,
    /// byte-identical — the roll is skipped, consuming no RNG); lower values make upgrades a
    /// sometimes-reward. Parts whose base weapon is absent are never placed regardless. Replace-mode
    /// only. <see cref="Passes.ItemRandomizer"/>.
    /// </summary>
    public double WeaponUpgradeChance { get; set; } = 1.0;

    /// <summary>
    /// Phase 1 (§7.3), EXPERIMENTAL — default <c>0</c> (off). Probability [0,1] that a found base weapon
    /// is placed already-upgraded — its variant id (e.g. Glock 35 <c>0x07</c>, SPAS12 <c>0x02</c>)
    /// instead of the base + a loose part. These variant ids are <b>never</b> vanilla world pickups, so a
    /// direct variant pickup is unvalidated; keep off until CE/playtest confirms the game grants it
    /// correctly. <c>0</c> consumes no RNG (byte-identical default). <see cref="Passes.ItemRandomizer"/>.
    /// </summary>
    public double PreUpgradedWeaponChance { get; set; } = 0.0;

    /// <summary>
    /// Phase 1 (§7.4). The weapon <i>families</i> allowed into the pool, as a flag set
    /// (<see cref="WeaponFamily"/>). <see cref="WeaponFamily.All"/> (default) places every family — the
    /// current behavior, <b>byte-identical</b> (the family filter passes everything, consuming no RNG).
    /// Clearing a family drops its base weapons, custom variants, and upgrade parts from pool placement;
    /// those pickup slots fall through to consumable fill and that family's ammo is no longer linked.
    /// The starting Handgun is always granted regardless, so 9mm stays linked even with Handgun cleared.
    /// Replace-mode + <see cref="RandomizeWeapons"/> only. <see cref="Passes.ItemRandomizer"/>.
    /// </summary>
    public WeaponFamily EnabledWeaponFamilies { get; set; } = WeaponFamily.All;

    /// <summary>
    /// Input-boundary clamp (docs/decisions/cross/ITEM-RATIO-ZERO-PLAN.md). A freshly-authored <c>0/0</c> ratio is invalid:
    /// it would mean "no ammo and no health", which the engine only tolerates as a legacy fallback
    /// (old 6-byte seeds decode to <c>0/0</c>). Since <c>0/0</c> is bit-identical to <c>16/16</c>, this
    /// normalizes it to the default mix. Call at input boundaries (CLI parse, UI load); the engine's
    /// own fallback is independent and unaffected. Returns <c>true</c> if it changed anything.
    /// </summary>
    public bool NormalizeRatios()
    {
        if (RatioAmmo != 0 || RatioHealth != 0)
            return false;
        RatioAmmo = 16;
        RatioHealth = 16;
        return true;
    }
}
