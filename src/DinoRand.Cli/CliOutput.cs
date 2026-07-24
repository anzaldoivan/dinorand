using DinoRand.ApClient;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Ap;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Voice;

internal static class CliOutput
{
    public static void WriteHelp()
    {
        Console.Write(
            "DinoRand — Dino Crisis 1 / 2 randomizer\r\n" +
            "\r\n" +
            "Usage:\r\n" +
            "  dinorand --install <gameDir> [--game dc1|dc2] [--out <dir>] [--seed <n>]\r\n" +
            "           [--no-items] [--no-enemies] [--dc1-enemy-hp] [--shuffle-keys] [--scatter-key-items]\r\n" +
            "           [--dc1-cutscene-safe] [--dc1-door-skip] [--dc1-fast-forward-cutscenes] [--allow-hidden-spots] [--normalize-pickup-visuals] [--pickup-ground-models] [--no-pickup-ground-models]   (dc1)\r\n" +
            "           [--include-setpiece-enemies] [--include-boss-enemies] [--dc2-allow-water-swaps] [--dc2-emit-d2p]\r\n" +
            "           [--dc2-enemy-mode weighted|fixed] [--dc2-fixed-species <name|0xNN>]\r\n" +
            "           [--dc2-weight <name|0xNN>=<0..15>]...                   (dc2)\r\n" +
            "           [--dc2-character-skin stock|gail|rick|random]           (dc2)\r\n" +
            "           [--dc2-regina-skin stock|gail|rick|random]              (dc2)\r\n" +
            "           [--dc2-raptor-tiers] [--dc2-raptor-weight <0..7>=<0..15>]...\r\n" +
            "           [--dc2-raptor-colour room|mixed] [--dc2-blue-combo <1..20>]   (dc2)\r\n" +
            "           [--dc2-trex-killable] [--dc2-mosa-no-grab] [--dc2-mosa-no-knockback] [--dc2-mosa-tail-to-bite] [--dc2-inostra-spawn-guard]  (dc2)\r\n" +
            "           [--difficulty <0..1>] [--ratio-ammo <0..31>] [--ratio-health <0..31>]\r\n" +
            "           [--ammo-quantity <0..7>] [--weapon-upgrade-chance <0..1>]\r\n" +
            "           [--pre-upgraded-weapon-chance <0..1>]\r\n" +
            "           [--disable-weapons handgun,shotgun,grenade] [--install-to-data]\r\n" +
            "           [--no-spoiler]      (skip the per-seed spoiler file; game files identical either way)\r\n" +
            "  dinorand --install <gameDir> --restore\r\n" +
            "  dinorand --install <gameDir> --verify-backup   (read-only: audit .dinorand_backup for poisoned captures; works for dc1 and dc2)\r\n" +
            "  dinorand --install <gameDir> --swap-species <roomCode> [--species <name>]\r\n" +
            "  dinorand --install <gameDir> --fix-sound <roomCode>\r\n" +
            "  dinorand --install <gameDir> --add-enemy <roomCode> [--species <name>] [--at x,y,z[,rot]] [--kill-flag <n>] [--seed <n>]\r\n" +
            "           [--hp <n>] [--ai-param <n>] [--birth-mode <n>] [--slot <n>] [--activate <behavior>[:<blobHex16>[:<b3>]]]\r\n" +
            "  dinorand --install <gameDir> --add-enemy-at <roomCode>:<rdtOffset> [--species <name>] [--at x,y,z[,rot]] [--kill-flag <n>] [--seed <n>]\r\n" +
            "           [--hp <n>] [--ai-param <n>] [--birth-mode <n>] [--slot <n>] [--activate <behavior>[:<blobHex16>[:<b3>]]]\r\n" +
            "  dinorand --install <gameDir> --copy-enemy-palette <srcRoom>:<dstRoom> [--species <name>]\r\n" +
            "  dinorand --install <gameDir> --exe-patch <targetStage>:<donorStage>\r\n" +
            "  dinorand --install <gameDir> --set-door <room>:<fromDest>:<toDest>\r\n" +
            "  dinorand --install <gameDir> --set-item <room>:<x,z>:<id>\r\n" +
            "  dinorand --install <gameDir> --shuffle-bgm [--seed <n>]\r\n" +
            "  dinorand --install <gameDir> --voice-preview --voice-packs <datapacksDir>\r\n" +
            "           [--bank <stem> | --all-banks] [--voice-actor <name>] [--voice-game <re2|re2r|…>] [--seed <n>]\r\n" +
            "  dinorand --install <gameDir> --shuffle-boxes [--seed <n>]\r\n" +
            "  dinorand --install <gameDir> --reroll-boxes [--seed <n>]\r\n" +
            "  dinorand --install <gameDir> --starting-items \"0x05:1,0x16:30,0x1d:2\" [--starting-weapon 0x09]\r\n" +
            "  dinorand --install <gameDir> --install-to-data --starting-weapon 0x01 [--random-inventory]\r\n" +
            "  dinorand --dc2-edit-door --install <dc2GameDir> [--restore]\r\n" +
            "  dinorand --dc2-edit-door [--in <ST101.DAT>] [--out <dir>]\r\n" +
            "  dinorand --dc2-swap-enemies <roomCode> --install <dc2GameDir> --species <name|0xNN> [--allow-unsafe] [--force]\r\n" +
            "  dinorand --dc2-swap-enemies <roomCode> --install <dc2GameDir> --restore\r\n" +
            "  dinorand --dc2-shuffle-bgm --install <dc2GameDir> [--seed <n>] [--restore]\r\n" +
            "  dinorand --dc2-export-bgm --install <dc2GameDir> --out <dir>\r\n" +
            "  dinorand --dc2-import-bgm --install <dc2GameDir> --bgm-packs <dir> --out <dir> [--seed <n>]\r\n" +
            "  dinorand --dc2-scramble-puzzle-codes --install <dc2GameDir> [--seed <n>] [--restore]\r\n" +
            "  dinorand --dc2-shuffle-circuits --install <dc2GameDir> [--seed <n>] [--restore]\r\n" +
            "  dinorand --dc2-rekey-plate-door --install <dc2GameDir> [--seed <n>] [--restore]\r\n" +
            "  dinorand --dc2-cross-char-weapons --install <dc2GameDir> [--restore]\n" +
            "  dinorand --dc2-randomize-weapons --install <dc2GameDir> [--seed <n>] [--restore]\n" +
            "  dinorand --dc2-door-skip --install <dc2GameDir>\r\n" +
            "  dinorand --ap-connect <host[:port]> --ap-slot <name> [--ap-password <pw>] --install <dc1GameDir> [--out <dir>]\r\n" +
            "\r\n" +
            "Notes:\r\n" +
            "  - --dc2-edit-door retargets Dino Crisis 2's ST101 ST201-door to ST102. With --install it\r\n" +
            "    patches the real ST101.DAT in place under the same backup-and-swap contract as the DC1\r\n" +
            "    room ops (pristine backed up to Data\\.dinorand_backup; undo with --restore). Without\r\n" +
            "    --install it emits a standalone edited ST101.DAT to --out (default mod_dinorand_dc2) for\r\n" +
            "    manual testing. (docs/decisions/dc1/doors/DOOR-RANDOMIZER-PLAN.md.)\r\n" +
            "  - --include-setpiece-enemies / --include-boss-enemies (dc2 enemy rando, both off by\r\n" +
            "    default) widen the cross-species donor pool: setpiece adds the no-damage Triceratops\r\n" +
            "    (0x09) + Giganotosaurus (0x06); boss adds the LAND boss Tyrannosaurus (0x03). Both keep\r\n" +
            "    their own scale/HP, so they make degenerate trash mobs — opt-in only. They compose.\r\n" +
            "  - --dc1-cutscene-safe (dc1 enemy rando, off) cutscene-safe mode: rooms in the derived\r\n" +
            "    choreography census (data/dc1/cutscene-rooms.json flagged tier — a script binds an\r\n" +
            "    enemy slot and drives it through authored waypoints, STATIC-SCD-RE cont.49/59) are\r\n" +
            "    excluded from the in-room permute and from foreign-species imports, and get a seeded\r\n" +
            "    palette tint instead (the cont.51/57 \"Blue Raptor\" lever). Off = byte-identical.\r\n" +
            "  - --dc1-door-skip (dc1, off; EXPERIMENTAL) PATCHES DINO.exe: removes the door-transition\r\n" +
            "    swing so room-to-room transitions are near-instant, keeping the destination background\r\n" +
            "    (STATIC-SCD-RE cont.78). Two reversible .text windows, not seed-encoded, reversed by\r\n" +
            "    --restore; leaves the shared animation stepper untouched. CLOSE the game first.\r\n" +
            "  - --dc1-fast-forward-cutscenes (dc1, off; EXPERIMENTAL / CRASH RISK) PATCHES DINO.exe:\r\n" +
            "    compresses cutscene dead air via a guarded SCD-VM tick multiplier (STATIC-SCD-RE cont.79\r\n" +
            "    v2) — story flags/item grants still commit and dialogue pacing is preserved. One reversible\r\n" +
            "    .text hook + code cave, not seed-encoded, reversed by --restore. CLOSE the game first.\r\n" +
            "  - --dc2-emit-d2p (dc2, off; EXPERIMENTAL) additionally write each randomized room's edits\r\n" +
            "    as a Classic REbirth patch\\ST*.d2p word-patch into <out>\\patch\\ — copy that folder to\r\n" +
            "    rebirth\\patch\\ and the wrapper applies the seed's ROOM edits in memory with Data\\\r\n" +
            "    untouched (levels only: exe levers, watermark, voice and models still need the overlay).\r\n" +
            "    Purely additive to the normal output. docs/parity/NONDESTRUCTIVE-INSTALL-PARITY.md.\r\n" +
            "  - --dc2-allow-water-swaps (dc2 enemy rando, off; EXPERIMENTAL) \"Allow Enemy swaps in the\r\n" +
            "    Water Levels\": lifts the aquatic-room block (ST700/702/703/704 take land donors on their\r\n" +
            "    wave descriptors) AND admits aquatic donors, all wave-only (Mosasaurus at low weight; the\r\n" +
            "    Plesiosaurus boss/grunts also need --include-setpiece-enemies). Off = byte-identical.\r\n" +
            "    docs/decisions/dc2/enemies/DC2-AQUATIC-LAND-UNLOCK-FEASIBILITY.md.\r\n" +
            "  - --dc2-enemy-mode (dc2 enemy rando, default weighted) selects how each room's donor is\r\n" +
            "    picked (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md): weighted biases the per-room pick by the\r\n" +
            "    per-species weight table (curated defaults = today's uniform pick; boss donors rare);\r\n" +
            "    fixed pins ONE donor for every eligible room (an all-T-Rex run). --dc2-fixed-species\r\n" +
            "    names the pin (implies fixed mode; safe LAND donors only) — pinning a boss/setpiece\r\n" +
            "    species doesn't need the include-* toggles. --dc2-weight overrides one species' weight\r\n" +
            "    (repeatable, e.g. --dc2-weight trex=5 --dc2-weight velociraptor=0; 0 excludes the\r\n" +
            "    species; weighted mode only). Per-species room caps (T-Rex 2 / Giga 1) come from\r\n" +
            "    data/dc2/enemy-distribution.json.\r\n" +
            "  - --dc2-raptor-tiers (dc2, off by default) randomizes raptor colour/HP tier variants\r\n" +
            "    (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md): static spawns get per-spawn weighted variants (room-file\r\n" +
            "    edit, non-destructive); wave rooms get the seeded pair-table patch written to the\r\n" +
            "    game's Dino2.exe IN PLACE (.bak backup). --dc2-raptor-weight overrides a variant's\r\n" +
            "    pool weight (repeatable, e.g. --dc2-raptor-weight 5=15 for blue everywhere; 0\r\n" +
            "    excludes). --dc2-raptor-colour (default room): room = one tier per room, colour ==\r\n" +
            "    strength; mixed = room colour is the STRONGEST tier present, other raptors may be\r\n" +
            "    weaker (a room renders only one raptor skin — engine limit). --dc2-blue-combo N\r\n" +
            "    (1..20, default 20 = vanilla) lowers the max-combo hit count that naturally spawns\r\n" +
            "    the blue/super raptor in the next room (exe patch too).\r\n" +
            "  - killable injected T-Rex: patches Dino2.exe IN PLACE (.bak backup) so a randomizer-injected\r\n" +
            "    T-Rex can be killed normally — disables the campaign survival clamp for any T-Rex EXCEPT the\r\n" +
            "    two vanilla boss rooms (ST200, ST903). AUTO-APPLIED whenever the run can spawn a T-Rex\r\n" +
            "    (--include-boss-enemies, or --dc2-fixed-species 0x03). --dc2-trex-killable forces it on even\r\n" +
            "    without those (docs/decisions/dc2/enemies/DC2-TREX-KILLABLE-LEVER-PLAN.md).\r\n" +
            "  - --dc2-character-skin / --dc2-regina-skin (dc2): the protagonist renders as Extra\r\n" +
            "    Crisis Gail or Rick via\r\n" +
            "    their engine-native WP graft files (visual-only; weapons/gameplay unchanged; random =\r\n" +
            "    seed-resolved). The emitted WP*.DAT files need the WP-gate Dino2.exe patch to load —\r\n" +
            "    applied by the UI at install (docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §7-9). This\r\n" +
            "    replaces the withdrawn --swap-player-characters whole-file swap (fire-crash RCA).\r\n" +
            "  - --dc2-swap-enemies <roomCode> converts every eligible hardcoded enemy spawn AND every\r\n" +
            "    native wave-spawn descriptor (K65 — the per-frame respawn system; wave-only rooms like\r\n" +
            "    ST105/ST104 are swappable through it) in one DC2 room (roomCode = the st_id, e.g. 202\r\n" +
            "    or 80A) to the --species donor — the file-edit\r\n" +
            "    replacement for the Cheat Engine cave (no CE needed for a file edit). --species takes a\r\n" +
            "    creature name (velociraptor/oviraptor/allosaurus/inostrancevia/triceratops/…) or a TYPE\r\n" +
            "    literal (0x07). LAND donors only by default; --allow-unsafe forces an aquatic/flyer/\r\n" +
            "    unresolved donor for in-game crash-classification (it may crash on room entry). Patches\r\n" +
            "    ST<code>.DAT in place under the backup-and-swap contract (ST<code>.DAT.bak); --restore\r\n" +
            "    reverts that one room. It refuses set-piece rooms (e.g. ST407 turret) and aquatic-native\r\n" +
            "    rooms (e.g. ST706 Mosasaurus) — the same room-level guards the bulk pass applies — unless\r\n" +
            "    --force is given (distinct from --allow-unsafe, which overrides the donor-habitat guard).\r\n" +
            "    Inherently DC2 (no --game needed). docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md.\r\n" +
            "  - --dc1-enemy-hp (dc1, off by default) overrides each eligible enemy's maxHP per placement:\r\n" +
            "    it writes a seeded, --difficulty-scaled value into the 0x20 spawn record's +6 word (entity\r\n" +
            "    +0x11A), bypassing the game's birth roll. A plain room-file edit (no DINO.exe patch, no\r\n" +
            "    Cheat Engine). Eligible = RaptorHeavy (cat-2) and Pteranodon (cat-7) 0x20 records only —\r\n" +
            "    the only PC birth-inits that KEEP a nonzero preset; Velociraptor and Swarm births overwrite\r\n" +
            "    maxHP with 1000 unconditionally, so they are skipped rather than silently no-op'd\r\n" +
            "    (STATIC-SCD-RE cont.48). Scripted T-Rex and cutscene rooms skipped; 0x59-placed enemies\r\n" +
            "    untouched. Higher --difficulty widens the band upward; HP gates no progression, so seeds\r\n" +
            "    stay beatable. docs/decisions/dc1/spawn/ENEMY-SPAWN-SYSTEM.md \"Gap 4 — REVERSED\".\r\n" +
            "  - --game selects the target (default dc1). dc2 is an early scaffold: it loads the\r\n" +
            "    Dino Crisis 2 (Rebirth) rooms but its passes are no-ops until the room-record\r\n" +
            "    decoders land (docs/parity/BIORAND-REUSE-VALIDATION.md). The DC1-only targeted\r\n" +
            "    operations (--swap-species/--add-enemy/--set-door/--exe-patch/--fix-hitdeath/\r\n" +
            "    --analyze-scripts) and --install-to-data/--restore are not yet supported for dc2.\r\n" +
            "  - Reads your unmodified install; writes randomized copies to the output dir\r\n" +
            "    (default: mod_dinorand). Originals are never modified.\r\n" +
            "  - --install-to-data overlays the generated room files onto the game's Data\r\n" +
            "    folder, backing up the pristine originals to Data\\.dinorand_backup first.\r\n" +
            "  - --restore copies those originals back and removes the backup.\r\n" +
            "  - --shuffle-keys relocates the door-gating key items (Entrance / BG Area /\r\n" +
            "    C.O. Area keys, Key Card Lv A) to new spots, kept provably beatable. It does all\r\n" +
            "    three key-shuffle behaviors together: it also relocates the DDK Input/Code disc\r\n" +
            "    pairs (overlay-requires gated) AND scatters keys into static ammo/health pickups\r\n" +
            "    (not only other door-key spots); discs/items conserved. (The old --scatter-key-items\r\n" +
            "    flag is now redundant — scatter is on by default with --shuffle-keys.)\r\n" +
            "  - DC1 enemy randomization includes FOREIGN-species imports by default: the cat8\r\n" +
            "    Therizinosaurus (stages 1-2) and grounded RaptorHeavy in eligible rooms. With\r\n" +
            "    --install-to-data it applies the EXE patches they need\r\n" +
            "    (cat-slot / hit-reaction / enemy-SE). Stage-scoped + CE-sensitive: CLOSE the game\r\n" +
            "    first (it writes DINO.exe), then relaunch and CE-verify. Reversed by --restore.\r\n" +
            "    --no-enemies disables both normal and foreign-species randomization. The legacy\r\n" +
            "    --exotic-enemies flag is a redundant no-op. T-Rex/flyer/swarm remain gated off.\r\n" +
            "  - --voice-preview (EXPERIMENTAL, docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12) auditions the cutscene\r\n" +
            "    voice swap: it overwrites one labelled Regina bank (or --all-banks) with a CROSS-GAME donor's\r\n" +
            "    line transcoded to DC1's format, so you can launch the game and HEAR it. --voice-packs must\r\n" +
            "    point at the BioRand datapacks (e.g. biorand/datapacks); --voice-actor pins the donor (else\r\n" +
            "    random); --voice-game pins the source game when a donor exists in several (e.g. Kendo in re2\r\n" +
            "    [RE2 classic] vs re2r [remake]); --bank <stem> targets a specific bank (e.g. xa_ep09b). It does NOT patch DINO.exe, so\r\n" +
            "    no relaunch is needed; it writes via the same backup contract, so --restore undoes it. This is\r\n" +
            "    the listen-test BEFORE the production voice pass is ungated (it bypasses the hard gate on purpose).\r\n" +
            "  - Door randomization is Phase 3 and not yet enabled.\r\n" +
            "  - --swap-species imports a foreign species (default largeground; also\r\n" +
            "    raptorheavy|pteranodon|tyrannosaurus|swarm) over the first raptor in one\r\n" +
            "    room, writing it into Data with a backup (undo with --restore). For\r\n" +
            "    in-game testing of the cross-room importer; geometry only (may be mis-coloured).\r\n" +
            "  - --add-enemy INJECTS a new enemy (default raptorheavy) into a room's init\r\n" +
            "    script — for rooms that ship with no placed enemy. --at x,y,z[,rot] sets the\r\n" +
            "    spawn (signed 16-bit; Y is height, 0 = floor); without it the position falls\r\n" +
            "    back to a DOOR ENTRY POSE — a real floor point the game spawns the player on\r\n" +
            "    when entering this room (one is picked at random when several doors lead in;\r\n" +
            "    --seed <n> makes the pick reproducible, default = the room code). If no door\r\n" +
            "    leads into the room, the position is harvested from an existing enemy instead,\r\n" +
            "    so a doorless, enemy-less room still needs --at. Same backup/--restore\r\n" +
            "    contract; geometry only. Spawn validity must be CE-verified.\r\n" +
            "    --kill-flag <n> forces the GetFlag(group-4) \"already-killed\" id; the auto-pick\r\n" +
            "    can't see flags already set in your save, so a collision spawns the enemy\r\n" +
            "    \"already dead\" (no entity) — pass a high/unused id (e.g. 0x60) to rule it out.\r\n" +
            "  - --add-enemy-at <room>:<rdtOffset> is the same as --add-enemy but injects at a\r\n" +
            "    CALLER-CHOSEN RDT offset (hex, from the room's decoded script) instead of the\r\n" +
            "    init script. Activation is PER-CATEGORY (cont.49/57): a cat-2 enemy (raptorheavy)\r\n" +
            "    self-activates and hunts from ANY reached offset — init sub-0 included; a cat-1\r\n" +
            "    enemy (velociraptor) parks in passive state-1 unless a script activates it —\r\n" +
            "    use --activate or an event-sub offset. The offset must be a clean, 4-aligned\r\n" +
            "    opcode boundary; the tool reports which sub it lands in and the category's\r\n" +
            "    activation behavior. The sub must already load the species' resources.\r\n" +
            "  - --add-enemy/--add-enemy-at authored record fields (all default 0 = the proven-neutral\r\n" +
            "    template, STATIC-SCD-RE cont.48/51): --hp <n> presets maxHP (record +6; only cat-2\r\n" +
            "    RaptorHeavy / cat-7 Pteranodon births KEEP it); --ai-param <n> sets the per-entity AI\r\n" +
            "    byte (+3 -> entity +0x2F); --birth-mode <n> selects the birth behavior (+5 low 2 bits:\r\n" +
            "    0=default, 1=behavior 0x19, 2/3=0x1A — cat-2 semantics, CE-gated).\r\n" +
            "  - --activate <behavior>[:<blobHex16>[:<b3>]] (CE-gate lab, cont.49) EMITS the script\r\n" +
            "    activation pair right after the injected record: op 0x22 binding the record's slot +\r\n" +
            "    op 0x3a installing <behavior>. This is what wakes a cat-1 (Velociraptor) placed by init;\r\n" +
            "    cat-2 self-activates without it. blobHex16 = the 0x3a's 8 operand bytes (16 hex chars);\r\n" +
            "    b3 = the 0x3a's byte[3] (semantics unmapped; retail behavior-1 installs carry 0x1F..0x21).\r\n" +
            "    Omitted = both copied together from the room's first native 0x3a, preferring a matching\r\n" +
            "    behavior code (zeros if the room has none).\r\n" +
            "  - --slot <n> forces the injected record's large-entity slot. The auto-pick dedupes only\r\n" +
            "    against parsed 0x20/0x59 records -- it cannot see natively-installed occupants (cont.18's\r\n" +
            "    0102 slot-0 collision); pass a known-free slot when the room has native enemies.\r\n" +
            "  - --copy-enemy-palette <srcRoom>:<dstRoom> copies the --species (default raptorheavy)\r\n" +
            "    palette — the room's type-2 CLUT entry, 512 bytes at the same VRAM rect — from src into\r\n" +
            "    dst, tinting dst's enemies with src's colours (e.g. 511:102 = Blue Raptor into the Mgmt\r\n" +
            "    Office hall; STATIC-SCD-RE cont.51). Room-file only, same backup/--restore contract.\r\n" +
            "  - --set-door <room>:<fromDest>:<toDest> retargets one door (3-hex room codes),\r\n" +
            "    e.g. 103:102:511 sends Management Office's hall door to the Stabilizer\r\n" +
            "    Experiment Room. It carries a valid arrival pose for the destination from an\r\n" +
            "    existing door that already targets it. Same backup/--restore contract; CE-verify\r\n" +
            "    the arrival.\r\n" +
            "  - --set-item <room>:<x,z>:<id> writes one item id into the pickup at position x,z\r\n" +
            "    (the placement quad's first corner, decimal; see a record's +0x04/+0x06). A probe\r\n" +
            "    for validating novel pickups — e.g. 0402:-5888,-11520:0x07 turns the st402 grenade-\r\n" +
            "    gun spot into a Glock 35 to check a pre-upgraded-weapon variant grants correctly\r\n" +
            "    in-game. Same backup/--restore contract; CE/playtest-verify.\r\n" +
            "  - --exe-patch <targetStage>:<donorStage> PATCHES DINO.exe: gives every room in\r\n" +
            "    targetStage the enemy set donorStage's rooms use (the enemy-set index a room\r\n" +
            "    loads is roomId>>8, so this is STAGE-scoped — it changes a whole floor, not one\r\n" +
            "    room). Backs the exe up once to .dinorand_backup (so --restore reverses it too).\r\n" +
            "    CLOSE the game first — Windows locks the running executable. The cross-species\r\n" +
            "    lever; pair with a map-side import for sets whose models a stage lacks. CE-verify.\r\n" +
            "  - --shuffle-bgm PATCHES DINO.exe: shuffles the global BGM (music) catalog so every\r\n" +
            "    track id streams a different file, deterministically from --seed (random if\r\n" +
            "    omitted). The permutation stays within each stream/loop (flags) class, so loop\r\n" +
            "    behaviour stays correct; the music still follows the scene, just plays a different\r\n" +
            "    cue. The catalog is read at game init, so CLOSE the game first and relaunch to hear\r\n" +
            "    it. Backs the exe up once to .dinorand_backup (so --restore reverses it too).\r\n" +
            "  - --bgm-import --bgm-packs <dir> IMPORTS external music into DC1: each Sound/BGM/ slot is\r\n" +
            "    overwritten with a transcoded same-mood donor track drawn from the datapacks under <dir>\r\n" +
            "    (layout <pack>/data/bgm/<tag>/*.ogg|wav; tag = mood folder, 'all' = catch-all). Rides the\r\n" +
            "    main --install run and installs with the seed (reversed by --restore). Distinct from\r\n" +
            "    --shuffle-bgm (own-track exe shuffle) and composes with it. No-op without --bgm-packs.\r\n" +
            "  - --shuffle-boxes PATCHES DINO.exe (EXPERIMENTAL): shuffles emergency-box contents so\r\n" +
            "    every box holds a different (still valid, difficulty-appropriate) loadout, within\r\n" +
            "    each International difficulty block, deterministically from --seed. Records are moved\r\n" +
            "    whole, so contents stay legitimate. Read at load time → CLOSE and relaunch to see it;\r\n" +
            "    backed up to .dinorand_backup (so --restore reverses it).\r\n" +
            "  - --reroll-boxes PATCHES DINO.exe (EXPERIMENTAL): like --shuffle-boxes, but rerolls each\r\n" +
            "    box's items from that difficulty's own box loot pool (weights + amounts taken from the\r\n" +
            "    vanilla boxes — distinct from the map item pool). Mutually exclusive with --shuffle-boxes.\r\n" +
            "  - --random-inventory (with --install-to-data) PATCHES DINO.exe (EXPERIMENTAL): a new game\r\n" +
            "    starts with a randomized supply kit, deterministically from --seed. The Handgun + its 9mm\r\n" +
            "    are always granted, so it stays beatable. Read at new-game → CLOSE/relaunch and start a NEW\r\n" +
            "    GAME to see it; reversed by --restore.\r\n" +
            "  - --starting-items \"id:count,…\" PATCHES DINO.exe (EXPERIMENTAL): set the new-game supply kit\r\n" +
            "    explicitly (ids 0x01..0x23, count 1..255, up to 3 items — the slot budget shared by every\r\n" +
            "    difficulty). The Handgun is still always granted. With --install-to-data it composes with the\r\n" +
            "    weapon choice; standalone it's an EXE-only edit. docs/reference/dc1/items/STARTING-INVENTORY.md.\r\n" +
            "  - --starting-weapon <id> PATCHES DINO.exe (EXPERIMENTAL): change the new-game starting weapon to a\r\n" +
            "    weapon id (0x01..0x0a; e.g. 0x01 Shotgun, 0x09 Grenade Gun). The displaced Handgun is force-placed\r\n" +
            "    into an early, no-key-reachable world pickup (with --install-to-data + item randomization on) so\r\n" +
            "    the seed stays beatable. A weaponless start ('none') is NOT supported yet (the engine re-equips a\r\n" +
            "    default Handgun via an undecoded path). Composes with the supply options; reversed by --restore.\r\n" +
            "    docs/reference/dc1/items/STARTING-INVENTORY.md.\n");
    }
}
