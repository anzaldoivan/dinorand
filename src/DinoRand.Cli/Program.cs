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

var args0 = Environment.GetCommandLineArgs();
var argv = args0.Skip(1).ToArray();

if (argv.Length == 0 || argv.Contains("--help") || argv.Contains("-h"))
{
    Console.WriteLine("""
        DinoRand — Dino Crisis 1 / 2 randomizer

        Usage:
          dinorand --install <gameDir> [--game dc1|dc2] [--out <dir>] [--seed <n>]
                   [--no-items] [--no-enemies] [--dc1-enemy-hp] [--shuffle-keys] [--scatter-key-items] [--exotic-enemies]
                   [--dc1-cutscene-safe] [--dc1-door-skip] [--dc1-fast-forward-cutscenes] [--allow-hidden-spots] [--normalize-pickup-visuals] [--no-pickup-ground-models]   (dc1)
                   [--include-setpiece-enemies] [--include-boss-enemies] [--dc2-allow-water-swaps] [--dc2-emit-d2p]
                   [--dc2-enemy-mode weighted|fixed] [--dc2-fixed-species <name|0xNN>]
                   [--dc2-weight <name|0xNN>=<0..15>]...                   (dc2)
                   [--dc2-character-skin stock|gail|rick|random]           (dc2)
                   [--dc2-regina-skin stock|gail|rick|random]              (dc2)
                   [--dc2-raptor-tiers] [--dc2-raptor-weight <0..7>=<0..15>]...
                   [--dc2-raptor-colour room|mixed] [--dc2-blue-combo <1..20>]   (dc2)
                   [--dc2-trex-killable] [--dc2-mosa-no-grab] [--dc2-mosa-no-knockback] [--dc2-mosa-tail-to-bite] [--dc2-inostra-spawn-guard]  (dc2)
                   [--difficulty <0..1>] [--ratio-ammo <0..31>] [--ratio-health <0..31>]
                   [--ammo-quantity <0..7>] [--weapon-upgrade-chance <0..1>]
                   [--pre-upgraded-weapon-chance <0..1>]
                   [--disable-weapons handgun,shotgun,grenade] [--install-to-data]
                   [--no-spoiler]      (skip SPOILER.md; game files identical either way)
          dinorand --install <gameDir> --restore
          dinorand --install <gameDir> --verify-backup   (read-only: audit .dinorand_backup for poisoned captures; works for dc1 and dc2)
          dinorand --install <gameDir> --swap-species <roomCode> [--species <name>]
          dinorand --install <gameDir> --fix-sound <roomCode>
          dinorand --install <gameDir> --add-enemy <roomCode> [--species <name>] [--at x,y,z[,rot]] [--kill-flag <n>] [--seed <n>]
                   [--hp <n>] [--ai-param <n>] [--birth-mode <n>] [--slot <n>] [--activate <behavior>[:<blobHex16>[:<b3>]]]
          dinorand --install <gameDir> --add-enemy-at <roomCode>:<rdtOffset> [--species <name>] [--at x,y,z[,rot]] [--kill-flag <n>] [--seed <n>]
                   [--hp <n>] [--ai-param <n>] [--birth-mode <n>] [--slot <n>] [--activate <behavior>[:<blobHex16>[:<b3>]]]
          dinorand --install <gameDir> --copy-enemy-palette <srcRoom>:<dstRoom> [--species <name>]
          dinorand --install <gameDir> --exe-patch <targetStage>:<donorStage>
          dinorand --install <gameDir> --set-door <room>:<fromDest>:<toDest>
          dinorand --install <gameDir> --set-item <room>:<x,z>:<id>
          dinorand --install <gameDir> --shuffle-bgm [--seed <n>]
          dinorand --install <gameDir> --voice-preview --voice-packs <datapacksDir>
                   [--bank <stem> | --all-banks] [--voice-actor <name>] [--voice-game <re2|re2r|…>] [--seed <n>]
          dinorand --install <gameDir> --shuffle-boxes [--seed <n>]
          dinorand --install <gameDir> --reroll-boxes [--seed <n>]
          dinorand --install <gameDir> --starting-items "0x05:1,0x16:30,0x1d:2" [--starting-weapon 0x09]
          dinorand --install <gameDir> --install-to-data --starting-weapon 0x01 [--random-inventory]
          dinorand --dc2-edit-door --install <dc2GameDir> [--restore]
          dinorand --dc2-edit-door [--in <ST101.DAT>] [--out <dir>]
          dinorand --dc2-swap-enemies <roomCode> --install <dc2GameDir> --species <name|0xNN> [--allow-unsafe] [--force]
          dinorand --dc2-swap-enemies <roomCode> --install <dc2GameDir> --restore
          dinorand --dc2-shuffle-bgm --install <dc2GameDir> [--seed <n>] [--restore]
          dinorand --dc2-export-bgm --install <dc2GameDir> --out <dir>
          dinorand --dc2-import-bgm --install <dc2GameDir> --bgm-packs <dir> --out <dir> [--seed <n>]
          dinorand --dc2-scramble-puzzle-codes --install <dc2GameDir> [--seed <n>] [--restore]
          dinorand --dc2-shuffle-circuits --install <dc2GameDir> [--seed <n>] [--restore]
          dinorand --dc2-rekey-plate-door --install <dc2GameDir> [--seed <n>] [--restore]
          dinorand --dc2-cross-char-weapons --install <dc2GameDir> [--restore]
          dinorand --dc2-randomize-weapons --install <dc2GameDir> [--seed <n>] [--restore]
          dinorand --dc2-door-skip --install <dc2GameDir>
          dinorand --ap-connect <host[:port]> --ap-slot <name> [--ap-password <pw>] --install <dc1GameDir> [--out <dir>]

        Notes:
          - --dc2-edit-door retargets Dino Crisis 2's ST101 ST201-door to ST102. With --install it
            patches the real ST101.DAT in place under the same backup-and-swap contract as the DC1
            room ops (pristine backed up to Data\.dinorand_backup; undo with --restore). Without
            --install it emits a standalone edited ST101.DAT to --out (default mod_dinorand_dc2) for
            manual testing. (docs/decisions/dc1/doors/DOOR-RANDOMIZER-PLAN.md.)
          - --include-setpiece-enemies / --include-boss-enemies (dc2 enemy rando, both off by
            default) widen the cross-species donor pool: setpiece adds the no-damage Triceratops
            (0x09) + Giganotosaurus (0x06); boss adds the LAND boss Tyrannosaurus (0x03). Both keep
            their own scale/HP, so they make degenerate trash mobs — opt-in only. They compose.
          - --dc1-cutscene-safe (dc1 enemy rando, off) cutscene-safe mode: rooms in the derived
            choreography census (data/dc1/cutscene-rooms.json flagged tier — a script binds an
            enemy slot and drives it through authored waypoints, STATIC-SCD-RE cont.49/59) are
            excluded from the in-room permute and from --exotic-enemies imports, and get a seeded
            palette tint instead (the cont.51/57 "Blue Raptor" lever). Off = byte-identical.
          - --dc1-door-skip (dc1, off; EXPERIMENTAL) PATCHES DINO.exe: removes the door-transition
            swing so room-to-room transitions are near-instant, keeping the destination background
            (STATIC-SCD-RE cont.78). Two reversible .text windows, not seed-encoded, reversed by
            --restore; leaves the shared animation stepper untouched. CLOSE the game first.
          - --dc1-fast-forward-cutscenes (dc1, off; EXPERIMENTAL / CRASH RISK) PATCHES DINO.exe:
            compresses cutscene dead air via a guarded SCD-VM tick multiplier (STATIC-SCD-RE cont.79
            v2) — story flags/item grants still commit and dialogue pacing is preserved. One reversible
            .text hook + code cave, not seed-encoded, reversed by --restore. CLOSE the game first.
          - --dc2-emit-d2p (dc2, off; EXPERIMENTAL) additionally write each randomized room's edits
            as a Classic REbirth patch\ST*.d2p word-patch into <out>\patch\ — copy that folder to
            rebirth\patch\ and the wrapper applies the seed's ROOM edits in memory with Data\
            untouched (levels only: exe levers, watermark, voice and models still need the overlay).
            Purely additive to the normal output. docs/parity/NONDESTRUCTIVE-INSTALL-PARITY.md.
          - --dc2-allow-water-swaps (dc2 enemy rando, off; EXPERIMENTAL) "Allow Enemy swaps in the
            Water Levels": lifts the aquatic-room block (ST700/702/703/704 take land donors on their
            wave descriptors) AND admits aquatic donors, all wave-only (Mosasaurus at low weight; the
            Plesiosaurus boss/grunts also need --include-setpiece-enemies). Off = byte-identical.
            docs/decisions/dc2/enemies/DC2-AQUATIC-LAND-UNLOCK-FEASIBILITY.md.
          - --dc2-enemy-mode (dc2 enemy rando, default weighted) selects how each room's donor is
            picked (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md): weighted biases the per-room pick by the
            per-species weight table (curated defaults = today's uniform pick; boss donors rare);
            fixed pins ONE donor for every eligible room (an all-T-Rex run). --dc2-fixed-species
            names the pin (implies fixed mode; safe LAND donors only) — pinning a boss/setpiece
            species doesn't need the include-* toggles. --dc2-weight overrides one species' weight
            (repeatable, e.g. --dc2-weight trex=5 --dc2-weight velociraptor=0; 0 excludes the
            species; weighted mode only). Per-species room caps (T-Rex 2 / Giga 1) come from
            data/dc2/enemy-distribution.json.
          - --dc2-raptor-tiers (dc2, off by default) randomizes raptor colour/HP tier variants
            (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md): static spawns get per-spawn weighted variants (room-file
            edit, non-destructive); wave rooms get the seeded pair-table patch written to the
            game's Dino2.exe IN PLACE (.bak backup). --dc2-raptor-weight overrides a variant's
            pool weight (repeatable, e.g. --dc2-raptor-weight 5=15 for blue everywhere; 0
            excludes). --dc2-raptor-colour (default room): room = one tier per room, colour ==
            strength; mixed = room colour is the STRONGEST tier present, other raptors may be
            weaker (a room renders only one raptor skin — engine limit). --dc2-blue-combo N
            (1..20, default 20 = vanilla) lowers the max-combo hit count that naturally spawns
            the blue/super raptor in the next room (exe patch too).
          - killable injected T-Rex: patches Dino2.exe IN PLACE (.bak backup) so a randomizer-injected
            T-Rex can be killed normally — disables the campaign survival clamp for any T-Rex EXCEPT the
            two vanilla boss rooms (ST200, ST903). AUTO-APPLIED whenever the run can spawn a T-Rex
            (--include-boss-enemies, or --dc2-fixed-species 0x03). --dc2-trex-killable forces it on even
            without those (docs/decisions/dc2/enemies/DC2-TREX-KILLABLE-LEVER-PLAN.md).
          - --dc2-character-skin / --dc2-regina-skin (dc2): the protagonist renders as Extra
            Crisis Gail or Rick via
            their engine-native WP graft files (visual-only; weapons/gameplay unchanged; random =
            seed-resolved). The emitted WP*.DAT files need the WP-gate Dino2.exe patch to load —
            applied by the UI at install (docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §7-9). This
            replaces the withdrawn --swap-player-characters whole-file swap (fire-crash RCA).
          - --dc2-swap-enemies <roomCode> converts every eligible hardcoded enemy spawn AND every
            native wave-spawn descriptor (K65 — the per-frame respawn system; wave-only rooms like
            ST105/ST104 are swappable through it) in one DC2 room (roomCode = the st_id, e.g. 202
            or 80A) to the --species donor — the file-edit
            replacement for the Cheat Engine cave (no CE needed for a file edit). --species takes a
            creature name (velociraptor/oviraptor/allosaurus/inostrancevia/triceratops/…) or a TYPE
            literal (0x07). LAND donors only by default; --allow-unsafe forces an aquatic/flyer/
            unresolved donor for in-game crash-classification (it may crash on room entry). Patches
            ST<code>.DAT in place under the backup-and-swap contract (ST<code>.DAT.bak); --restore
            reverts that one room. It refuses set-piece rooms (e.g. ST407 turret) and aquatic-native
            rooms (e.g. ST706 Mosasaurus) — the same room-level guards the bulk pass applies — unless
            --force is given (distinct from --allow-unsafe, which overrides the donor-habitat guard).
            Inherently DC2 (no --game needed). docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md.
          - --dc1-enemy-hp (dc1, off by default) overrides each eligible enemy's maxHP per placement:
            it writes a seeded, --difficulty-scaled value into the 0x20 spawn record's +6 word (entity
            +0x11A), bypassing the game's birth roll. A plain room-file edit (no DINO.exe patch, no
            Cheat Engine). Eligible = RaptorHeavy (cat-2) and Pteranodon (cat-7) 0x20 records only —
            the only PC birth-inits that KEEP a nonzero preset; Velociraptor and Swarm births overwrite
            maxHP with 1000 unconditionally, so they are skipped rather than silently no-op'd
            (STATIC-SCD-RE cont.48). Scripted T-Rex and cutscene rooms skipped; 0x59-placed enemies
            untouched. Higher --difficulty widens the band upward; HP gates no progression, so seeds
            stay beatable. docs/decisions/dc1/spawn/ENEMY-SPAWN-SYSTEM.md "Gap 4 — REVERSED".
          - --game selects the target (default dc1). dc2 is an early scaffold: it loads the
            Dino Crisis 2 (Rebirth) rooms but its passes are no-ops until the room-record
            decoders land (docs/parity/BIORAND-REUSE-VALIDATION.md). The DC1-only targeted
            operations (--swap-species/--add-enemy/--set-door/--exe-patch/--fix-hitdeath/
            --analyze-scripts) and --install-to-data/--restore are not yet supported for dc2.
          - Reads your unmodified install; writes randomized copies to the output dir
            (default: mod_dinorand). Originals are never modified.
          - --install-to-data overlays the generated room files onto the game's Data
            folder, backing up the pristine originals to Data\.dinorand_backup first.
          - --restore copies those originals back and removes the backup.
          - --shuffle-keys relocates the door-gating key items (Entrance / BG Area /
            C.O. Area keys, Key Card Lv A) to new spots, kept provably beatable. It does all
            three key-shuffle behaviors together: it also relocates the DDK Input/Code disc
            pairs (overlay-requires gated) AND scatters keys into static ammo/health pickups
            (not only other door-key spots); discs/items conserved. (The old --scatter-key-items
            flag is now redundant — scatter is on by default with --shuffle-keys.)
          - --exotic-enemies (EXPERIMENTAL, off by default) imports FOREIGN species into
            eligible rooms — the cat8 Therizinosaurus (stages 1-2) and the grounded
            RaptorHeavy — and, with --install-to-data, applies the EXE patches they need
            (cat-slot / hit-reaction / enemy-SE). Stage-scoped + CE-sensitive: CLOSE the game
            first (it writes DINO.exe), then relaunch and CE-verify. Reversed by --restore.
            T-Rex/flyer/swarm are registered but gated off (docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md).
          - --voice-preview (EXPERIMENTAL, docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12) auditions the cutscene
            voice swap: it overwrites one labelled Regina bank (or --all-banks) with a CROSS-GAME donor's
            line transcoded to DC1's format, so you can launch the game and HEAR it. --voice-packs must
            point at the BioRand datapacks (e.g. biorand/datapacks); --voice-actor pins the donor (else
            random); --voice-game pins the source game when a donor exists in several (e.g. Kendo in re2
            [RE2 classic] vs re2r [remake]); --bank <stem> targets a specific bank (e.g. xa_ep09b). It does NOT patch DINO.exe, so
            no relaunch is needed; it writes via the same backup contract, so --restore undoes it. This is
            the listen-test BEFORE the production voice pass is ungated (it bypasses the hard gate on purpose).
          - Door randomization is Phase 3 and not yet enabled.
          - --swap-species imports a foreign species (default largeground; also
            raptorheavy|pteranodon|tyrannosaurus|swarm) over the first raptor in one
            room, writing it into Data with a backup (undo with --restore). For
            in-game testing of the cross-room importer; geometry only (may be mis-coloured).
          - --add-enemy INJECTS a new enemy (default raptorheavy) into a room's init
            script — for rooms that ship with no placed enemy. --at x,y,z[,rot] sets the
            spawn (signed 16-bit; Y is height, 0 = floor); without it the position falls
            back to a DOOR ENTRY POSE — a real floor point the game spawns the player on
            when entering this room (one is picked at random when several doors lead in;
            --seed <n> makes the pick reproducible, default = the room code). If no door
            leads into the room, the position is harvested from an existing enemy instead,
            so a doorless, enemy-less room still needs --at. Same backup/--restore
            contract; geometry only. Spawn validity must be CE-verified.
            --kill-flag <n> forces the GetFlag(group-4) "already-killed" id; the auto-pick
            can't see flags already set in your save, so a collision spawns the enemy
            "already dead" (no entity) — pass a high/unused id (e.g. 0x60) to rule it out.
          - --add-enemy-at <room>:<rdtOffset> is the same as --add-enemy but injects at a
            CALLER-CHOSEN RDT offset (hex, from the room's decoded script) instead of the
            init script. Activation is PER-CATEGORY (cont.49/57): a cat-2 enemy (raptorheavy)
            self-activates and hunts from ANY reached offset — init sub-0 included; a cat-1
            enemy (velociraptor) parks in passive state-1 unless a script activates it —
            use --activate or an event-sub offset. The offset must be a clean, 4-aligned
            opcode boundary; the tool reports which sub it lands in and the category's
            activation behavior. The sub must already load the species' resources.
          - --add-enemy/--add-enemy-at authored record fields (all default 0 = the proven-neutral
            template, STATIC-SCD-RE cont.48/51): --hp <n> presets maxHP (record +6; only cat-2
            RaptorHeavy / cat-7 Pteranodon births KEEP it); --ai-param <n> sets the per-entity AI
            byte (+3 -> entity +0x2F); --birth-mode <n> selects the birth behavior (+5 low 2 bits:
            0=default, 1=behavior 0x19, 2/3=0x1A — cat-2 semantics, CE-gated).
          - --activate <behavior>[:<blobHex16>[:<b3>]] (CE-gate lab, cont.49) EMITS the script
            activation pair right after the injected record: op 0x22 binding the record's slot +
            op 0x3a installing <behavior>. This is what wakes a cat-1 (Velociraptor) placed by init;
            cat-2 self-activates without it. blobHex16 = the 0x3a's 8 operand bytes (16 hex chars);
            b3 = the 0x3a's byte[3] (semantics unmapped; retail behavior-1 installs carry 0x1F..0x21).
            Omitted = both copied together from the room's first native 0x3a, preferring a matching
            behavior code (zeros if the room has none).
          - --slot <n> forces the injected record's large-entity slot. The auto-pick dedupes only
            against parsed 0x20/0x59 records -- it cannot see natively-installed occupants (cont.18's
            0102 slot-0 collision); pass a known-free slot when the room has native enemies.
          - --copy-enemy-palette <srcRoom>:<dstRoom> copies the --species (default raptorheavy)
            palette — the room's type-2 CLUT entry, 512 bytes at the same VRAM rect — from src into
            dst, tinting dst's enemies with src's colours (e.g. 511:102 = Blue Raptor into the Mgmt
            Office hall; STATIC-SCD-RE cont.51). Room-file only, same backup/--restore contract.
          - --set-door <room>:<fromDest>:<toDest> retargets one door (3-hex room codes),
            e.g. 103:102:511 sends Management Office's hall door to the Stabilizer
            Experiment Room. It carries a valid arrival pose for the destination from an
            existing door that already targets it. Same backup/--restore contract; CE-verify
            the arrival.
          - --set-item <room>:<x,z>:<id> writes one item id into the pickup at position x,z
            (the placement quad's first corner, decimal; see a record's +0x04/+0x06). A probe
            for validating novel pickups — e.g. 0402:-5888,-11520:0x07 turns the st402 grenade-
            gun spot into a Glock 35 to check a pre-upgraded-weapon variant grants correctly
            in-game. Same backup/--restore contract; CE/playtest-verify.
          - --exe-patch <targetStage>:<donorStage> PATCHES DINO.exe: gives every room in
            targetStage the enemy set donorStage's rooms use (the enemy-set index a room
            loads is roomId>>8, so this is STAGE-scoped — it changes a whole floor, not one
            room). Backs the exe up once to .dinorand_backup (so --restore reverses it too).
            CLOSE the game first — Windows locks the running executable. The cross-species
            lever; pair with a map-side import for sets whose models a stage lacks. CE-verify.
          - --shuffle-bgm PATCHES DINO.exe: shuffles the global BGM (music) catalog so every
            track id streams a different file, deterministically from --seed (random if
            omitted). The permutation stays within each stream/loop (flags) class, so loop
            behaviour stays correct; the music still follows the scene, just plays a different
            cue. The catalog is read at game init, so CLOSE the game first and relaunch to hear
            it. Backs the exe up once to .dinorand_backup (so --restore reverses it too).
          - --bgm-import --bgm-packs <dir> IMPORTS external music into DC1: each Sound/BGM/ slot is
            overwritten with a transcoded same-mood donor track drawn from the datapacks under <dir>
            (layout <pack>/data/bgm/<tag>/*.ogg|wav; tag = mood folder, 'all' = catch-all). Rides the
            main --install run and installs with the seed (reversed by --restore). Distinct from
            --shuffle-bgm (own-track exe shuffle) and composes with it. No-op without --bgm-packs.
          - --shuffle-boxes PATCHES DINO.exe (EXPERIMENTAL): shuffles emergency-box contents so
            every box holds a different (still valid, difficulty-appropriate) loadout, within
            each International difficulty block, deterministically from --seed. Records are moved
            whole, so contents stay legitimate. Read at load time → CLOSE and relaunch to see it;
            backed up to .dinorand_backup (so --restore reverses it).
          - --reroll-boxes PATCHES DINO.exe (EXPERIMENTAL): like --shuffle-boxes, but rerolls each
            box's items from that difficulty's own box loot pool (weights + amounts taken from the
            vanilla boxes — distinct from the map item pool). Mutually exclusive with --shuffle-boxes.
          - --random-inventory (with --install-to-data) PATCHES DINO.exe (EXPERIMENTAL): a new game
            starts with a randomized supply kit, deterministically from --seed. The Handgun + its 9mm
            are always granted, so it stays beatable. Read at new-game → CLOSE/relaunch and start a NEW
            GAME to see it; reversed by --restore.
          - --starting-items "id:count,…" PATCHES DINO.exe (EXPERIMENTAL): set the new-game supply kit
            explicitly (ids 0x01..0x23, count 1..255, up to 3 items — the slot budget shared by every
            difficulty). The Handgun is still always granted. With --install-to-data it composes with the
            weapon choice; standalone it's an EXE-only edit. docs/reference/dc1/items/STARTING-INVENTORY.md.
          - --starting-weapon <id> PATCHES DINO.exe (EXPERIMENTAL): change the new-game starting weapon to a
            weapon id (0x01..0x0a; e.g. 0x01 Shotgun, 0x09 Grenade Gun). The displaced Handgun is force-placed
            into an early, no-key-reachable world pickup (with --install-to-data + item randomization on) so
            the seed stays beatable. A weaponless start ('none') is NOT supported yet (the engine re-equips a
            default Handgun via an undecoded path). Composes with the supply options; reversed by --restore.
            docs/reference/dc1/items/STARTING-INVENTORY.md.
        """);
    return 0;
}

// DC2 door-edit (docs/decisions/dc1/doors/DOOR-RANDOMIZER-PLAN.md): retarget ST101's ST201 door to ST102.
//   --dc2-edit-door --install <gameDir>           : patch the real ST101.DAT in place, backing the
//                                                   pristine original up via the shared GameInstaller
//                                                   backup contract (undo with --restore).
//   --dc2-edit-door --install <gameDir> --restore : undo the in-place patch (GameInstaller.Restore).
//   --dc2-edit-door [--in <ST101.DAT>] [--out <d>]: standalone emit to a scratch folder (no install).
if (argv.Contains("--dc2-edit-door"))
    return EditDc2Door(GetOpt("--install"), GetOpt("--in"), GetOpt("--out"), argv.Contains("--restore"));

// DC2 single-room enemy swap (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md): convert one room's hardcoded enemy
// spawns to a chosen donor species — the file-edit replacement for the CE cave (.claude/skills/dc2-enemy-
// inject). LAND-only by default; --allow-unsafe forces a non-land/unresolved donor for crash-classification.
// --restore reverts the room from its ST*.DAT.bak. Inherently DC2 (no --game needed), like --dc2-edit-door.
if (GetOpt("--dc2-swap-enemies") is { } swapEnemiesRoom)
    return SwapDc2Enemies(GetOpt("--install"), swapEnemiesRoom, GetOpt("--species"),
                          argv.Contains("--allow-unsafe"), argv.Contains("--force"), argv.Contains("--restore"));

// DC2 BGM shuffle (docs/decisions/dc2/audio/DC2-BGM-RANDO-PLAN.md — live-witnessed 2026-07-05, I3 gate PASSED):
// permute Dino2.exe's music-file pointer table within like-classes (same track-index set).
// --restore rewrites the slice to canonical order (leaves other exe patches intact).
if (argv.Contains("--dc2-shuffle-bgm"))
    return ShuffleDc2Bgm(GetOpt("--install"), GetOpt("--seed"), argv.Contains("--restore"));

// DC2 music EXPORT/IMPORT (docs/decisions/dc2/audio/DC2-BGM-IMPORT-FEASIBILITY.md): the container payload is
// standard MP3, so --dc2-export-bgm unpacks every Data/M[SEF]_*.DAT track to a playable .mp3 under --out, and
// --dc2-import-bgm rebuilds those containers from a BioRand-layout donor pack (.mp3 verbatim; .ogg/.wav via
// ffmpeg) into --out for the user to copy back into Data\. Both are read-only on the install.
if (argv.Contains("--dc2-export-bgm"))
    return ExportDc2Bgm(GetOpt("--install"), GetOpt("--out"));
if (argv.Contains("--dc2-import-bgm"))
    return ImportDc2Bgm(GetOpt("--install"), GetOpt("--bgm-packs"), GetOpt("--out"), GetOpt("--seed"));

if (argv.Contains("--dc2-randomize-weapons")
    && (argv.Contains("--dc2-shared-weapons") || argv.Contains("--dc2-shared-weapons-subs-only")))
{
    Console.Error.WriteLine("error: --dc2-randomize-weapons is incompatible with --dc2-shared-weapons.");
    return 1;
}

// Randomized ownership must own the graft prerequisite and run after any shop/start-weapon edits.
// Dispatch it before the standalone shop/cross/shared handlers so combined redundant flags cannot
// apply the prerequisite twice.
if (argv.Contains("--dc2-randomize-weapons"))
    return RandomizeDc2Weapons(GetOpt("--install"), GetOpt("--seed"), argv.Contains("--restore"),
        shuffleShop: argv.Contains("--dc2-shuffle-shop"));

// DC2 shop shuffle (docs/decisions/dc2/shop/DC2-SHOP-RANDO-PLAN.md I1+I2; I3 live witness PENDING): permute the
// Dino2.exe shop economy — retail prices (master table 0x71DCB8) and stock-unlock bitmasks
// (catalog 0x704260+id*12+0xA) among the 11 for-sale ids. Reversible: pristine .bak plus a
// table-only --restore (Dc2ShopTablePatch.RestoreCanonical) that leaves other patches alone.
if (argv.Contains("--dc2-shuffle-shop"))
    return ShuffleDc2Shop(GetOpt("--install"), GetOpt("--seed"), argv.Contains("--restore"));

// DC2 elevator puzzle-code scramble (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §3, K108): write 8
// distinct seed-derived 4-digit codes (digits 0–5) into the setup fn's imm32 candidate table.
// Displayed==checked is automatic (single runtime copy at scene_mgr+0x1204) — no document work.
// Reversible: pristine .bak plus a slot-only --restore (Dc2ElevatorCodePatch.RestoreCanonical).
if (argv.Contains("--dc2-scramble-puzzle-codes"))
    return ScrambleDc2PuzzleCodes(GetOpt("--install"), GetOpt("--seed"), argv.Contains("--restore"));

// DC2 stungun-circuit shuffle (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 2, K110): rewrite the
// scripted blink box-id sequences of ST607 routines 7/8 and ST402 routines 23/24 with seed-derived
// orders (same length, every box at least once, no adjacent repeats; terminators untouched).
// Room-file edit under the backup-and-swap contract (ST*.DAT.bak); --restore reverts both rooms.
if (argv.Contains("--dc2-shuffle-circuits"))
    return ShuffleDc2Circuits(GetOpt("--install"), GetOpt("--seed"), argv.Contains("--restore"));

// DC2 Key-Plate terminal re-key (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 4, K118): permute
// ST205's SAT-9 routing so a seed-chosen plate colour is accepted, and recolour the blue slot panel to
// match. Room-file edit under the ST205.DAT.bak backup-and-swap contract; --restore reverts.
if (argv.Contains("--dc2-rekey-plate-door"))
    return RekeyDc2PlateDoor(GetOpt("--install"), GetOpt("--seed"), argv.Contains("--restore"));

// DC2 cross-character weapons (DC2-CROSS-CHAR-WEAPON-MODEL-SWAP.md): let Regina and Dylan wield each
// other's weapons while still rendering with their own body. Builds eight WEP_P grafts from the user's
// own Data files + repoints the NULL 0x71B230 catalog slots; --restore reverts exe and deletes them.
if (argv.Contains("--dc2-cross-char-weapons"))
    return Dc2CrossCharWeapons(GetOpt("--install"), argv.Contains("--restore"));

// DC2 character-shared weapons (DC2-NATIVE-WEAPON-SHARING-DECODE.md, K125): grant both owner bits in
// the 0x704260 item catalog so Regina and Dylan SHARE weapons instead of owning one set each. SUB
// weapons are bits only; MAIN weapons delegate to the graft installer (a MAIN with a NULL 0x71B230
// slot crashes). --dc2-shared-weapons-subs-only restricts it to the graft-free subset.
if (argv.Contains("--dc2-shared-weapons") || argv.Contains("--dc2-shared-weapons-subs-only"))
    return Dc2SharedWeapons(GetOpt("--install"), argv.Contains("--restore"),
                            includeMain: !argv.Contains("--dc2-shared-weapons-subs-only"));

// DC2 REbirth DoorSkip passthrough (CUTSCENE-SKIP-FEASIBILITY.md §5, K115): set DoorSkip = 1 in the
// game root's config.ini [DLL] section — the wrapper DLL's own door-transition skip. A user-config
// write, not a game edit; undo by setting the key back to 0 in config.ini (or the REbirth launcher).
if (argv.Contains("--dc2-door-skip"))
    return EnableDc2DoorSkip(GetOpt("--install"));

static int Dc2SharedWeapons(string? installDir, bool restore, bool includeMain)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-shared-weapons requires --install <dc2GameDir>.");
        return 1;
    }
    var outcome = Dc2SharedWeaponInstaller.Apply(installDir, restore, Console.WriteLine, includeMain);
    switch (outcome)
    {
        case Dc2SharedWeaponOutcome.Applied:
            Console.WriteLine($"undo    : dinorand --dc2-shared-weapons --install \"{installDir}\" --restore");
            Console.WriteLine(includeMain
                ? "note    : every main and sub weapon is now usable — and shop-purchasable — by BOTH characters."
                : "note    : Machete and Large Stungun are now usable — and shop-purchasable — by BOTH characters.");
            Console.WriteLine("note    : cosmetic — a shared weapon keeps its original owner's inventory icon on both characters.");
            return 0;
        case Dc2SharedWeaponOutcome.AlreadyApplied:
        case Dc2SharedWeaponOutcome.Restored:
            return 0;
        default:
            return 1;
    }
}

// DC2 starting main-weapon lever (docs/decisions/dc2/loadout/DC2-STARTING-LOADOUT-PLAN.md I2; I3 human gate PENDING):
// patch the new-game bootstrap equip immediates' weapon-id bytes (Dylan file 0x50D69, Regina 0x50E9D).
// Subweapon bytes are never touched — Machete/Large Stun Gun stay by construction.
//   --dc2-randomize-start-weapon [--seed N]     : random pick from each character's own band
//   --dc2-start-weapon dylan=<id>[,regina=<id>] : explicit ids (hex 0x.. or decimal; partial ok)
//   either form + --restore                     : revert both bytes to canonical
//   Only owned mains are offered (catalog 0x704260: MAIN flag + the character's ownership bit);
//   a SUB / other-character main / fire-empty main (0x04,0x07) bricks the weapon menu (div-0) and
//   is refused; --allow-unsafe installs one anyway (investigation only).
//   --dc2-add-and-equip also installs the weapon-ring div-0 zero-guard (Dc2WeaponRingGuardPatch),
//   which unlocks each character's FULL band (SUBs, other-char mains, the fire-empty Grenade Gun)
//   as safe picks — no --allow-unsafe needed.
if (argv.Contains("--dc2-randomize-start-weapon") || GetOpt("--dc2-start-weapon") is not null)
    return SetDc2StartWeapon(GetOpt("--install"), GetOpt("--seed"), GetOpt("--dc2-start-weapon"),
                             argv.Contains("--restore"), argv.Contains("--allow-unsafe"),
                             argv.Contains("--dc2-add-and-equip"));

// Archipelago runtime client, DC1 v1 (docs/decisions/cross/AP-CLIENT-PLAN.md): connect to an AP
// server, patch AP's fill into the local install (loop-closing, D5), then poll the running
// DINO.exe — pickups → LocationChecks, ReceivedItems → grants, goal room → CLIENT_GOAL.
// Long-running; Ctrl-C disconnects cleanly. The poll half needs the WINDOWS host (WSL cannot
// attach to a Windows process).
if (GetOpt("--ap-connect") is { } apHostPort)
    return ApConnectDc1(apHostPort, GetOpt("--ap-slot"), GetOpt("--ap-password"),
                        GetOpt("--install"), GetOpt("--out"));

string? install = GetOpt("--install");
if (install is null)
{
    Console.Error.WriteLine("error: --install <gameDir> is required (see --help)");
    return 1;
}

// TOS / DRM guard: refuse to operate on a game whose DINO.exe is wrapped by a protector (e.g. The Enigma
// Protector on the Steam release). This covers every subcommand at once — none runs past this point on a
// protected install. See ExeProtection / docs/reference/dc1/drm/STEAM-ENIGMA-DRM.md.
if (GuardNotDrmProtected(install) is { } drmExit)
    return drmExit;

// Game selector (default dc1). DC2 is wired as a parallel stack (Dc2RandomizerRunner / DinoCrisis2)
// so DC1 stays byte-for-byte unchanged — see docs/parity/BIORAND-REUSE-VALIDATION.md Q3.
// Read-only poisoned-backup audit (K82): compare every .dinorand_backup capture against the
// manifest hash / .dinorand-bak sibling / live file. Game-agnostic (runs before the --game split);
// exit 1 when anything is Poisoned/Suspect.
if (argv.Contains("--verify-backup"))
{
    // Both game defs may claim a Data dir under the same root (their room patterns overlap);
    // audit the one that actually holds a backup, falling back to the first hit.
    var candidates = new[] { new DinoCrisis1().GetDataDir(install), new DinoCrisis2().GetDataDir(install) }
        .Where(d => d is not null).Distinct().ToList();
    var dataDir = candidates.FirstOrDefault(d => GameInstaller.HasBackup(d!)) ?? candidates.FirstOrDefault();
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {install}");
        return 1;
    }
    var findings = GameInstaller.VerifyBackups(dataDir);
    if (findings.Count == 0)
    {
        Console.WriteLine($"no DinoRand backup found in {dataDir} — nothing to verify");
        return 0;
    }
    foreach (var f in findings.OrderByDescending(f => f.Status))
        Console.WriteLine($"{f.Status,-11} {f.Name}  ({f.Detail})");
    int bad = findings.Count(f => f.Status is BackupVerifyStatus.Poisoned or BackupVerifyStatus.Suspect);
    Console.WriteLine(bad == 0
        ? $"OK: {findings.Count} backup(s) verified, none poisoned"
        : $"FAIL: {bad} of {findings.Count} backup(s) poisoned or suspect");
    return bad == 0 ? 0 : 1;
}

string gameId = (GetOpt("--game") ?? "dc1").ToLowerInvariant();
if (gameId is not ("dc1" or "dc2"))
{
    Console.Error.WriteLine($"error: --game must be 'dc1' or 'dc2' (got '{gameId}')");
    return 1;
}

if (gameId == "dc2")
    return RunDc2(install);

if (argv.Contains("--analyze-scripts"))
    return AnalyzeScripts(install);

// Targeted single-room cross-room species swap (a manual in-game test of the importer,
// docs/decisions/dc1/enemies/CROSS-ROOM-SPECIES-PLAN.md Step 6). Backs the room up to the same .dinorand_backup
// folder --restore uses, then imports one foreign species over the room's first raptor.
if (GetOpt("--swap-species") is { } swapRoom)
{
    // --max-donor <bytes>: DEBUG/test override — force the Theri clip-strip to this donor budget even when
    // the room would fit unstripped (used to reproduce 0102's exact strip in a native-Theri room, e.g. st612).
    int? maxDonorOverride = GetOpt("--max-donor") is { } md && int.TryParse(md, out int mdv) ? mdv : null;
    return SwapSpecies(install, swapRoom, GetOpt("--species") ?? "largeground", maxDonorOverride);
}

// Targeted single-room ADD-enemy (inject a brand-new 0x20 record into a room that has none —
// the "enemy in a safe room" move, docs/decisions/dc1/enemies/ADD-ENEMY-PLAN.md). Same backup/restore contract as
// --swap-species. Spawn position from --at x,y,z[,rot], else a door entry pose, else harvested.
//
// --add-enemy-at <room>:<rdtOffset> injects at a CALLER-SPECIFIED RDT offset instead of the init
// script — for EVENT-sub targeting, where an injected enemy spawns active/hunting rather than inert
// (docs/decisions/dc1/spawn/ADD-ENEMY-EVENT-INJECTION-CRASH-RCA.md). Offset is hex, from the room's decoded script.
if (GetOpt("--add-enemy-at") is { } addAtSpec)
{
    var parts = addAtSpec.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length != 2)
    {
        Console.Error.WriteLine($"error: --add-enemy-at expects <room>:<rdtOffset> (e.g. 10a:0x49000), got '{addAtSpec}'");
        return 1;
    }
    return AddEnemy(install, parts[0], GetOpt("--species") ?? "raptorheavy", GetOpt("--at"),
                    GetOpt("--kill-flag"), GetOpt("--seed"), parts[1],
                    GetOpt("--hp"), GetOpt("--ai-param"), GetOpt("--birth-mode"), GetOpt("--activate"), GetOpt("--slot"));
}
if (GetOpt("--add-enemy") is { } addRoom)
    return AddEnemy(install, addRoom, GetOpt("--species") ?? "raptorheavy", GetOpt("--at"), GetOpt("--kill-flag"), GetOpt("--seed"),
                    null, GetOpt("--hp"), GetOpt("--ai-param"), GetOpt("--birth-mode"), GetOpt("--activate"), GetOpt("--slot"));

// Palette-copy lab (STATIC-SCD-RE cont.51): copy one species' type-2 CLUT entry (512 B, same VRAM
// rect) from a donor room into a target room — the "tint an enemy" lever (Blue Raptor = st511's
// recoloured palette row). Room-file only; same backup/--restore contract as the other room ops.
if (GetOpt("--copy-enemy-palette") is { } palSpec)
    return CopyEnemyPalette(install, palSpec, GetOpt("--species") ?? "raptorheavy");

// Targeted door retarget (docs/decisions/dc1/doors/DOOR-RANDOMIZER-PLAN.md): change one door's destination. spec is
// room:fromDest:toDest as 3-hex room codes, e.g. 103:102:511 = in Management Office (0103) change the
// door that leads to the hall (0102) so it goes to the Stabilizer Experiment Room (0511). Carries a
// valid arrival pose for the destination from an existing door that already targets it (so you land
// at a real doorway, not a wall). Same backup/--restore contract as the room ops.
if (GetOpt("--set-door") is { } setDoorSpec)
    return SetDoor(install, setDoorSpec);

if (GetOpt("--set-item") is { } setItemSpec)
    return SetItem(install, setItemSpec);

if (GetOpt("--set-enemy-hp") is { } setEnemyHpSpec)
    return SetEnemyHp(install, setEnemyHpSpec);

// Targeted EXE patch (docs/decisions/dc1/exe/EXE-PATCH-PER-ROOM-PLAN.md): repoint one stage's enemy-set record to
// another stage's, swapping that whole stage's enemy set. The enemy-set index a room loads is
// roomId>>8, so this is STAGE-scoped. Same backup/--restore contract as the room ops, but on
// DINO.exe — the game must be CLOSED (Windows locks the running image).
if (GetOpt("--exe-patch") is { } exeArg)
    return PatchExeOp(install, exeArg);

// Defect-B fix (docs/decisions/dc1/theri/THERI-0102-DEFECT-B-HITDEATH-RCA.md): install the canonical cat8 hit/death
// descriptor records into an EXE cave and repoint the cat8 descriptor tables, so an imported Theri can be
// SHOT/KILLED without the 0x4B0794/0x45dc0e AVs. Standalone, idempotent application of the same patch the
// Theri swap runs; the game must be CLOSED (Windows locks the running image).
if (argv.Contains("--fix-hitdeath"))
    return FixHitDeath(install);

// Standalone enemy-SOUND fix (docs/reference/dc1/se/ENEMY-SOUND-SYSTEM.md): retarget a Theri-swapped room's per-room SE
// manifest block to the Theri (j_*) set so it stops playing raptor sounds. EXE-only + idempotent (no .dat
// touch), so it fixes an existing swap install without re-importing. Same backup/--restore contract; game CLOSED.
if (GetOpt("--fix-sound") is { } fixSoundRoom)
    return FixSound(install, fixSoundRoom);

// Music randomizer (docs/reference/dc1/bgm/BGM-SYSTEM.md §4): shuffle the global BGM catalog in DINO.exe so every track id
// streams a different file (deterministic from --seed, within each stream/loop flags class). EXE-only + the
// catalog is read at init, so the game must be CLOSED and relaunched. Same backup/--restore contract.
if (argv.Contains("--shuffle-bgm"))
    return ShuffleBgm(install, GetOpt("--seed"));

// Puzzle-code randomizer (docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md §17): scramble the shared keypad-code
// family (Mgmt Office / Lounge / Computer-Room-gas safes + Stabilizer codes) from --seed, and rewrite the
// in-game documents that state the code so displayed == checked. EXE table + st100/st200/st302 edits; read
// at load, so the game must be CLOSED and relaunched. Same backup/--restore contract.
if (argv.Contains("--scramble-puzzle-codes"))
    return ScramblePuzzleCodes(install, GetOpt("--seed"));

// Voice swap PREVIEW (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.5 step 4): install one (or --all-banks) swapped
// Regina voice bank using a CROSS-GAME donor so you can launch the game and hear it. This is the dev
// validation path — it bypasses the hard-gated VoiceRandomizer pass (the gate stays closed) and writes
// via the loose-file backup contract, so --restore reverses it. No exe edit (no relaunch needed).
if (argv.Contains("--voice-preview"))
    return VoicePreview(install, GetOpt("--voice-packs"), GetOpt("--bank"),
        GetOpt("--voice-actor"), argv.Contains("--all-banks"), GetOpt("--seed"), GetOpt("--voice-game"));

if (argv.Contains("--shuffle-boxes") && argv.Contains("--reroll-boxes"))
{
    Console.Error.WriteLine("error: --shuffle-boxes and --reroll-boxes are mutually exclusive (both rewrite the box table).");
    return 1;
}
if (argv.Contains("--shuffle-boxes"))
    return ShuffleBoxes(install, GetOpt("--seed"));

if (argv.Contains("--reroll-boxes"))
    return RerollBoxes(install, GetOpt("--seed"));

// Standalone starting-inventory EXE patch (no room overlay). When combined with --install-to-data the
// full flow (below) handles it instead, so the item pass can place a removed weapon in the world.
if (!argv.Contains("--install-to-data") && (GetOpt("--starting-items") is not null || GetOpt("--starting-weapon") is not null))
    return SetStartingItems(install, GetOpt("--starting-items"), GetOpt("--starting-weapon"));

if (argv.Contains("--restore"))
{
    var dataDir = new DinoCrisis1().GetDataDir(install);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {install}");
        return 1;
    }
    var restore = GameInstaller.Restore(dataDir);
    Console.WriteLine(restore.Restored > 0
        ? $"restored {restore.Restored} original room files in {dataDir}"
        : $"nothing to restore (no DinoRand backup found in {dataDir})");
    if (restore.Dc1VertexLiftReapplied)
        Console.WriteLine("re-applied the DC1 vertex-ceiling lift to DINO.exe (ddraw.dll is lifted; "
            + "a stock exe would crash at 0x6FD000 — VTXLIFT-RESTORE-PAIRING-RCA)");
    return 0;
}

string outDir = GetOpt("--out") ?? "mod_dinorand";
var seed = GetOpt("--seed") is { } s ? Seed.Parse(s) : Seed.Random();

var config = new RandomizerConfig
{
    RandomizeItems = !argv.Contains("--no-items"),
    RandomizeEnemies = !argv.Contains("--no-enemies"),
    // DC1 per-placement enemy maxHP override (off by default). Writes a seeded, --difficulty-scaled HP into
    // eligible 0x20 spawns' +6 word (entity +0x11A), bypassing the {750,850,1000} roll — plain SCD file edit.
    // docs/decisions/dc1/spawn/ENEMY-SPAWN-SYSTEM.md "Gap 4 — REVERSED".
    RandomizeEnemyHp = argv.Contains("--dc1-enemy-hp"),
    // DC2 cross-species: include the no-damage setpiece Triceratops (E70/0x09, K62) in the donor pool.
    // Off by default (degenerate trash mob); opt-in. docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md.
    IncludeDc2SetpieceEnemies = argv.Contains("--include-setpiece-enemies"),
    // DC2 cross-species: include the boss donors (T-Rex 0x03, K61). Off by default (degenerate trash
    // mob); opt-in. docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md.
    IncludeDc2BossEnemies = argv.Contains("--include-boss-enemies"),
    // DC2 EXPERIMENTAL (off): allow enemy swaps in the water levels — lifts the aquatic-room block and
    // admits aquatic (wave-only) donors (K72). Default off = byte-identical. docs/decisions/dc2/enemies/DC2-AQUATIC-LAND-UNLOCK-FEASIBILITY.md.
    Dc2AllowWaterLevelEnemySwaps = argv.Contains("--dc2-allow-water-swaps"),
    // DC1 (off): cutscene-safe enemy rando — choreography-census rooms (data/dc1/cutscene-rooms.json
    // flagged tier, STATIC-SCD-RE cont.49/59) refuse permutes/species imports and get the palette-tint
    // fallback instead. Off = byte-identical. Not seed-encoded.
    Dc1CutsceneSafeEnemies = argv.Contains("--dc1-cutscene-safe"),
    Dc1DoorSkip = argv.Contains("--dc1-door-skip"),
    Dc1FastForwardCutscenes = argv.Contains("--dc1-fast-forward-cutscenes"),
    ShuffleKeyItems = argv.Contains("--shuffle-keys"),
    // DC1: key-item scatter (a door key may also land in a static ammo/health pickup, not only another
    // door-key spot) rides on --shuffle-keys by default, like the DDK relocation below — the product does
    // all three key-shuffle behaviors together. Progression-safe (symmetric Place), items conserved. The
    // legacy --scatter-key-items flag is now a redundant no-op. docs/decisions/dc1/items/KEY-ITEM-SCATTER-DATA-AUDIT.md.
    ShuffleKeyItemsIntoPickups = argv.Contains("--shuffle-keys"),
    // DC1: DDK Input/Code disc relocation (0x62–0x6f, overlay-`requires` PAIR-gated, not door-TYPE) rides on
    // --shuffle-keys by default — the product does all three key-shuffle behaviors together. Progression-safe
    // (pair-aware Place), discs conserved. The RandomizerConfig.RelocateDdkDiscs flag stays independent so
    // tests can exercise it in isolation. docs/decisions/dc1/items/PROGRESSION-KEY-RELOCATION-RESEARCH.md.
    RelocateDdkDiscs = argv.Contains("--shuffle-keys"),
    // DC1 (ON by default): keep important items visible — weapons/parts avoid interaction-only (no ground
    // visual) spots, and the key shuffle applies "no worse than vanilla" (an interaction-only spot only
    // receives a key whose vanilla home was interaction-only). Decoded map.json itemVisuals layer
    // (STATIC-SCD-RE cont.72). --allow-hidden-spots restores the old anything-anywhere behaviour.
    AvoidHiddenPickupSpots = !argv.Contains("--allow-hidden-spots"),
    // DC1 Lever A (OFF by default — in-game render not yet human-witnessed): when a relocated key/weapon lands
    // in a spot whose ground visual doesn't match (interaction-only = invisible, or bespoke = shows the old
    // item), rewrite that spot to the generic pickup panel. docs/decisions/dc1/items/PICKUP-GROUND-MODEL-FEASIBILITY.md.
    NormalizePickupVisuals = argv.Contains("--normalize-pickup-visuals"),
    // DC1 (OFF by default — pilot rooms not yet human-witnessed): shorten whitelisted cutscene brackets
    // to their side effects (in-place script rewrite, cont.74). docs/decisions/cross/CUTSCENE-SKIP-FEASIBILITY.md §9.3.
    ShortenCutscenes = argv.Contains("--dc1-shorten-cutscenes"),
    // Default ON for the in-game witness session; --no-pickup-ground-models is the off-switch.
    ImportPickupModels = !argv.Contains("--no-pickup-ground-models"),
    // Experimental: import foreign species (cat8 Theri + grounded RaptorHeavy) into eligible rooms and queue
    // the EXE patches they need. Off unless asked; with --install-to-data it patches DINO.exe (game must be
    // CLOSED). docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md.
    CrossRoomEnemySpecies = argv.Contains("--exotic-enemies"),
    // Experimental: with --install-to-data, patch DINO.exe so a new game starts with a randomized supply
    // kit (the Handgun + ammo are always granted, so it stays beatable). docs/reference/dc1/items/STARTING-INVENTORY.md.
    RandomizeStartingInventory = argv.Contains("--random-inventory"),
    // Phase 4 voice rando (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §13). Now LIVE: --voice swaps the cutscene cast
    // (Regina/Rick/Gail/Kirk) and installs the banks with the seed (reversed by --restore). Needs
    // --voice-packs; --voice-cross-game widens the donor pool beyond DC1's own cast.
    RandomizeVoices = argv.Contains("--voice"),
    IncludeCrossGameVoices = argv.Contains("--voice-cross-game"),
    VoicePacksRoot = GetOpt("--voice-packs"),
    // External BGM import (docs/decisions/cross/BGM-RANDO-PLAN.md): --bgm-import overwrites Sound/BGM/ slots
    // with transcoded same-tag donor tracks from --bgm-packs. Distinct from --shuffle-bgm (own-track exe
    // shuffle); the two compose. A no-op without --bgm-packs.
    RandomizeBgm = argv.Contains("--bgm-import"),
    BgmPacksRoot = GetOpt("--bgm-packs"),
};
if (GetOpt("--difficulty") is { } d && double.TryParse(d, out var diff))
    config.EnemyDifficulty = Math.Clamp(diff, 0, 1);

// The App picks donors per character (with a "Default = keep own voice" option); the CLI has no such UI,
// so --voice means "randomize the whole swappable cast" — set every member to a random donor.
if (config.RandomizeVoices)
    config.VoiceDonors = DinoRand.Randomizer.Voice.VoiceSwapPlanner.SwappableCast
        .ToDictionary(a => a.ToString().ToLowerInvariant(), _ => "random", StringComparer.OrdinalIgnoreCase);

// Item-distribution dials (docs/decisions/cross/ITEM-RATIO-ZERO-PLAN.md). Clamped to range; a 0/0 ammo+health pair is
// normalized to the default 16/16 mix with a warning (it would otherwise mean "no consumables at all",
// which the engine only tolerates as a legacy fallback). 0/0 is bit-identical to 16/16.
if (GetOpt("--ratio-ammo") is { } ra && byte.TryParse(ra, out var ammoRatio))
    config.RatioAmmo = Math.Min((byte)31, ammoRatio);
if (GetOpt("--ratio-health") is { } rh && byte.TryParse(rh, out var healthRatio))
    config.RatioHealth = Math.Min((byte)31, healthRatio);
if (GetOpt("--ammo-quantity") is { } aq && byte.TryParse(aq, out var ammoQty))
    config.AmmoQuantity = Math.Min((byte)7, ammoQty);
if (config.NormalizeRatios())
    Console.Error.WriteLine("warning: --ratio-ammo and --ratio-health can't both be 0; using the default 16/16 mix.");
if (GetOpt("--weapon-upgrade-chance") is { } wuc && double.TryParse(wuc, out var upg))
    config.WeaponUpgradeChance = Math.Clamp(upg, 0, 1);
// EXPERIMENTAL (§7.3): place found base weapons already-upgraded with this probability. The variant
// ids are never vanilla pickups, so this is unvalidated — off (0) unless explicitly set.
if (GetOpt("--pre-upgraded-weapon-chance") is { } puc && double.TryParse(puc, out var pre))
    config.PreUpgradedWeaponChance = Math.Clamp(pre, 0, 1);

// Per-family weapon toggles (§7.4): clear the named families from the pool (e.g. --disable-weapons
// shotgun,grenade). Tokens match a family's first display word (handgun/shotgun/grenade); unknown
// tokens warn and are ignored. Default keeps every family (byte-identical).
if (GetOpt("--disable-weapons") is { } dw)
{
    var families = new DinoCrisis1().WeaponFamilies;
    foreach (var token in dw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var match = families.FirstOrDefault(f =>
            f.Name.Split(' ')[0].Equals(token, StringComparison.OrdinalIgnoreCase));
        if (match.Flag == WeaponFamily.None)
            Console.Error.WriteLine($"warning: --disable-weapons: unknown weapon family '{token}' (try handgun/shotgun/grenade)");
        else
            config.EnabledWeaponFamilies &= ~match.Flag;
    }
}

// Starting weapon override (docs/reference/dc1/items/STARTING-INVENTORY.md): `--starting-weapon <id>` changes which weapon
// Regina begins with. Feeds config.StartingWeapons so the item pass force-places the displaced Handgun into
// a reachable early spot (beatability); the EXE grant is applied in the --install-to-data block. A weaponless
// start ('none') is rejected — not deliverable yet (the engine re-equips a default Handgun).
if (GetOpt("--starting-weapon") is { } swArg)
{
    if (swArg.Equals("none", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("error: --starting-weapon none (weaponless start) is not supported yet — the engine "
            + "re-equips a default Handgun via an undecoded path, so it can't be reliably removed. "
            + "Pick a weapon id (e.g. 0x01 Shotgun, 0x09 Grenade Gun) instead.");
        return 1;
    }
    if (ParseItemInt(swArg) is { } wid)
        config.StartingWeapons = new[] { wid };
    else
    {
        Console.Error.WriteLine($"error: --starting-weapon: bad value '{swArg}' (expected a weapon id like 0x01)");
        return 1;
    }
}

var runner = new RandomizerRunner(new DinoCrisis1());
// --no-spoiler suppresses SPOILER.md only (docs/decisions/cross/SPOILER-LOG-PLAN.md); every game file is
// byte-identical either way (the spoiler is a pure post-write projection).
bool emitSpoiler = !argv.Contains("--no-spoiler");
var result = runner.Run(install, outDir, seed, config, emitSpoiler);

Console.WriteLine($"seed {seed} → {result.RoomsWritten} room files, {result.RoomCount} rooms");
Console.WriteLine($"output: {Path.GetFullPath(result.OutputDir)}");
if (emitSpoiler)
    Console.WriteLine($"spoiler: {Path.GetFullPath(Path.Combine(result.OutputDir, DinoRand.Randomizer.Spoiler.SpoilerLogBuilder.FileName))} "
        + "(debug block on top is spoiler-free; --no-spoiler to skip)");

if (argv.Contains("--install-to-data"))
{
    var dataDir = new DinoCrisis1().GetDataDir(install);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {install}");
        return 1;
    }
    InstallResult ir;
    try { ir = GameInstaller.Install(dataDir, outDir, seed.ToString()); }
    catch (IOException ex)
    {
        // Only the exe-patch step (e.g. --exotic-enemies' cat-slot patch) writes DINO.exe, which Windows locks
        // while the game runs; the room overlay itself does not. Surface the close-the-game hint.
        Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
        Console.Error.WriteLine("hint : --exotic-enemies patches DINO.exe — CLOSE the game and re-run (the room overlay is idempotent).");
        return 1;
    }
    Console.WriteLine($"installed to {dataDir}: {ir.Overlaid} room files overlaid, " +
                      $"{ir.BackedUp} originals backed up");
    if (config.CrossRoomEnemySpecies)
        Console.WriteLine("exotic enemies: any queued EXE patches were applied; CLOSE/relaunch + CE-verify the swaps.");
    // Combined starting-inventory EXE patch (weapon + supply), composed into one operation so the halves
    // don't clobber each other. CUSTOM supply (--starting-items) wins over RANDOM (--random-inventory); the
    // weapon grant follows config.StartingWeapons (already fed to the item pass above). Additive, seeded,
    // reversed by --restore.
    var customSupply = GetOpt("--starting-items") is { } itemsSpecInstall ? ParseStartingItems(itemsSpecInstall) : null;
    bool weaponOverride = config.StartingWeapons is not null;
    if (config.RandomizeStartingInventory || customSupply is not null || weaponOverride)
    {
        var plan = new StartingInventoryPlan(
            RandomizeSupply: config.RandomizeStartingInventory && customSupply is null,
            CustomSupply: customSupply,
            SetWeapon: weaponOverride,
            WeaponId: config.StartingWeapons is { Count: >= 1 } sws ? sws.First() : (int?)null);
        var iv = GameInstaller.PatchExeStartingInventory(dataDir, plan, seed.Value, seed.ToString());
        foreach (var r in iv.Repoints) Console.WriteLine($"inventory: {r}");
        Console.WriteLine("inventory: EXPERIMENTAL — seen on the NEXT NEW GAME after relaunch "
            + "(a removed start weapon is placed in the early world by the item pass).");
    }
    // Door skip (experimental, DC1): reversible DINO.exe patch that removes the door-transition swing while
    // keeping the destination background commit (cont.78). Install-time, not seed-encoded; reversed by --restore.
    if (config.Dc1DoorSkip)
    {
        var ds = GameInstaller.PatchExeDoorSkip(dataDir, seed.ToString());
        foreach (var r in ds.Repoints) Console.WriteLine($"door skip: {r}");
        Console.WriteLine("door skip: EXPERIMENTAL — door transitions are near-instant on the next launch. CLOSE the game first.");
    }
    // Fast-forward cutscenes (experimental/crash risk, DC1): reversible DINO.exe patch that compresses cutscene
    // dead air via a guarded SCD-VM tick multiplier (cont.79 v2). Install-time, not seed-encoded; reversed by --restore.
    if (config.Dc1FastForwardCutscenes)
    {
        var ff = GameInstaller.PatchExeFastForwardCutscenes(dataDir, seed.ToString());
        foreach (var r in ff.Repoints) Console.WriteLine($"fast-forward cutscenes: {r}");
        Console.WriteLine("fast-forward cutscenes: EXPERIMENTAL / CRASH RISK — cutscenes are sped up on the next launch. CLOSE the game first.");
    }
    Console.WriteLine($"backup: {ir.BackupDir}  (run with --restore to undo)");
}
return 0;

string? GetOpt(string name)
{
    int i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

// Parse a hex (0x..) or decimal integer token; null if malformed.
// cont.57 false-handoff RCA: every single-file edit rebuilds the file FROM the pristine backup, so
// edits do NOT stack — a second edit silently replaced the first. Make the replacement explicit.
static string BackupOnceWarned(string dataDir, string originalPath)
{
    var backupPath = GameInstaller.BackupOnce(dataDir, originalPath);
    if (GameInstaller.HasPriorEdit(backupPath, originalPath))
        Console.WriteLine($"warning : {Path.GetFileName(originalPath)} already differs from its pristine "
            + "backup — single-file edits rebuild FROM the backup and do NOT stack, so this edit "
            + "REPLACES the previous edit(s) on this file (one witness = one deploy).");
    return backupPath;
}

static int? ParseItemInt(string t) =>
    t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? (int.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : null)
        : (int.TryParse(t, out var d) ? d : (int?)null);

// Parse a "id:count,id:count,…" starting-inventory spec into (id,count) pairs; throws on a bad token.
static List<(int Id, int Count)> ParseStartingItems(string spec)
{
    var items = new List<(int, int)>();
    foreach (var tok in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var parts = tok.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || ParseItemInt(parts[0]) is not { } id || ParseItemInt(parts[1]) is not { } count)
            throw new FormatException($"bad --starting-items token '{tok}' (expected id:count, e.g. 0x16:30)");
        items.Add((id, count));
    }
    return items;
}

// Locate the install's DINO.exe and refuse to proceed if it is DRM-protected (Steam/Enigma build).
// Returns null when the game is clean (or no exe is present — handled by each op's own not-found path),
// or a non-zero exit code after printing the reason. Kept here so every subcommand is gated in one place.
int? GuardNotDrmProtected(string gameDir)
{
    if (!Directory.Exists(gameDir))
        return null; // a bad path is reported by the operation itself
    string? exePath;
    try
    {
        exePath = Directory.EnumerateFiles(gameDir, GameInstaller.ExeName, SearchOption.AllDirectories)
            .FirstOrDefault();
    }
    catch { return null; }
    if (exePath is null)
        return null; // no DINO.exe under the folder — let the op surface its own error
    var detection = ExeProtection.Inspect(exePath);
    if (!detection.IsProtected)
        return null;
    Console.Error.WriteLine($"error: refusing to modify a DRM-protected game.");
    Console.Error.WriteLine($"       {detection.Detail}");
    Console.Error.WriteLine($"       exe: {exePath}");
    return 3;
}

// Retarget ST101's ST201 door (dest word at blob offset 0xA7F2) from ST201 (stage 2, room 1) to
// ST102 (stage 1, room 2). With --install, patches the real ST101.DAT IN PLACE through the same
// GameInstaller backup-and-swap contract DC1's room swaps use (pristine backed up to .dinorand_backup,
// edit applied to the live file, reversed by --restore). Without --install, emits a standalone copy to
// a scratch folder. Reuses Dc2DoorEditor (GianPackage/Lzss) end to end.
static int EditDc2Door(string? installDir, string? inPath, string? outDir, bool restore)
{
    const int st201DoorOffset = 0xA7F2;   // data/dc2/door-graph.json rooms["101"].doors[0].dest_push_off

    // In-place mode: locate the DC2 Data folder (rebirth/Data) and patch ST101.DAT there.
    if (installDir is not null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            Console.Error.WriteLine($"error: could not locate a DC2 Data folder under {installDir} "
                + "(looked for rebirth/Data, Data, english/Data containing ST*.DAT).");
            return 1;
        }

        if (restore)
        {
            var r = GameInstaller.Restore(dataDir);
            Console.WriteLine(r.Restored > 0
                ? $"restored {r.Restored} original file(s) in {dataDir}"
                : $"nothing to restore (no DinoRand backup found in {dataDir})");
            return 0;
        }

        var targetPath = Directory.EnumerateFiles(dataDir, "ST101.DAT").FirstOrDefault();
        if (targetPath is null)
        {
            Console.Error.WriteLine($"error: ST101.DAT not found in {dataDir}");
            return 1;
        }

        // Back the pristine original up once (shared GameInstaller contract) and ALWAYS edit FROM that
        // pristine copy, so re-running is non-compounding and --restore reverses it.
        var backupPath = BackupOnceWarned(dataDir, targetPath);
        if (!TryEdit(File.ReadAllBytes(backupPath), st201DoorOffset, out var before, out var after, out var edited))
            return 1;
        File.WriteAllBytes(targetPath, edited);

        Console.WriteLine($"patched : {targetPath}  (in place)");
        Console.WriteLine($"door    : ST101 ST201-door @ blob 0x{st201DoorOffset:X}");
        Console.WriteLine($"before  : {before}  (bytes {before.Stage:X2} {before.Room:X2})");
        Console.WriteLine($"after   : {after}  (bytes {after.Stage:X2} {after.Room:X2})");
        Console.WriteLine($"backup  : {backupPath}");
        Console.WriteLine($"undo    : dinorand --dc2-edit-door --install \"{installDir}\" --restore");
        Console.WriteLine($"verify  : launch the game, walk the ST101->ST201 door, confirm it now arrives in ST102.");
        return 0;
    }

    // Standalone emit mode: read READ-ONLY, write an edited copy to a scratch folder.
    inPath ??= Path.Combine("4249140_DinoCrisis2", "rebirth", "Data", "ST101.DAT");
    outDir ??= "mod_dinorand_dc2";
    if (!File.Exists(inPath))
    {
        Console.Error.WriteLine($"error: ST101.DAT not found at '{inPath}' (pass --in <path>, or --install <gameDir>)");
        return 1;
    }

    if (!TryEdit(File.ReadAllBytes(inPath), st201DoorOffset, out var b, out var a, out var bytes))
        return 1;
    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, "ST101.DAT");
    File.WriteAllBytes(outPath, bytes);

    Console.WriteLine($"read    : {Path.GetFullPath(inPath)}  (READ-ONLY)");
    Console.WriteLine($"door    : ST101 ST201-door @ blob 0x{st201DoorOffset:X}");
    Console.WriteLine($"before  : {b}  (bytes {b.Stage:X2} {b.Room:X2})");
    Console.WriteLine($"after   : {a}  (bytes {a.Stage:X2} {a.Room:X2})");
    Console.WriteLine($"wrote   : {Path.GetFullPath(outPath)}");
    Console.WriteLine("verify  : back up your install, copy this ST101.DAT over Data\\ST101.DAT, load the "
        + "game, walk the ST101->ST201 door, and confirm it now arrives in ST102.");
    return 0;

    // Read the current dest, rewrite it to ST102, and read it back from the repacked package. Returns false
    // (after logging) if the bytes are not a valid DC2 package with the expected door.
    static bool TryEdit(byte[] package, int destOffset,
                        out Dc2DoorEditor.DoorDest before, out Dc2DoorEditor.DoorDest after, out byte[] edited)
    {
        before = default; after = default; edited = Array.Empty<byte>();
        try
        {
            before = Dc2DoorEditor.ReadDestinationFromPackage(package, destOffset);
            edited = Dc2DoorEditor.WriteDestination(package, destOffset, newStage: 1, newRoom: 2);
            after = Dc2DoorEditor.ReadDestinationFromPackage(edited, destOffset);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: door edit failed: {ex.Message}");
            return false;
        }
    }
}

// Dino Crisis 2 entry point. A thin driver over the DC2 parallel stack (DinoCrisis2 +
// Dc2RandomizerRunner). The randomization passes are no-ops until the DC2 spawn/door/item record
// decoders land (docs/reference/dc2/_registries/KNOWLEDGE-AND-QUESTIONS.md OPEN #1/#2/#5), so this loads the rooms, runs
// the (stub) passes, and reports status. DC1-only operations are rejected with a clear message.
// "stock" (or the character's own name) = no swap; parse is case-insensitive.
static Dc2CharacterSkin ParseSkin(string? arg) => arg?.ToLowerInvariant() switch
{
    null or "stock" or "dylan" or "regina" => Dc2CharacterSkin.Stock,
    "gail" => Dc2CharacterSkin.Gail,
    "rick" => Dc2CharacterSkin.Rick,
    "random" => Dc2CharacterSkin.Random,
    _ => throw new ArgumentException($"unknown character skin '{arg}' (use stock|gail|rick|random)"),
};

int RunDc2(string installDir)
{
    string[] dc1Only =
    {
        "--swap-species", "--add-enemy", "--add-enemy-at", "--copy-enemy-palette", "--set-door", "--set-item", "--exe-patch",
        "--fix-hitdeath", "--fix-sound", "--shuffle-bgm", "--shuffle-boxes", "--reroll-boxes", "--starting-items", "--starting-weapon", "--analyze-scripts", "--install-to-data", "--restore",
    };
    if (dc1Only.FirstOrDefault(o => argv.Contains(o)) is { } op)
    {
        Console.Error.WriteLine($"error: {op} is a Dino Crisis 1 operation; not supported with --game dc2 yet.");
        return 1;
    }

    var dc2Game = new DinoCrisis2();
    if (dc2Game.GetDataDir(installDir) is null)
    {
        Console.Error.WriteLine($"error: could not locate a DC2 Data folder under {installDir} "
            + "(looked for rebirth/Data, Data, english/Data containing ST*.DAT).");
        return 1;
    }

    string dc2Out = GetOpt("--out") ?? "mod_dinorand_dc2";
    var dc2Seed = GetOpt("--seed") is { } s ? Seed.Parse(s) : Seed.Random();
    var dc2Config = new RandomizerConfig
    {
        RandomizeItems = !argv.Contains("--no-items"),
        RandomizeEnemies = !argv.Contains("--no-enemies"),
        // Opt-in: add the no-damage setpiece Triceratops (E70/0x09, K62) to the cross-species donor pool.
        IncludeDc2SetpieceEnemies = argv.Contains("--include-setpiece-enemies"),
        // Opt-in: add the boss donors (T-Rex 0x03, K61) to the cross-species donor pool.
        IncludeDc2BossEnemies = argv.Contains("--include-boss-enemies"),
        // EXPERIMENTAL (off): allow enemy swaps in the water levels — lifts the aquatic-room block and admits
        // aquatic (wave-only) donors (K72). docs/decisions/dc2/enemies/DC2-AQUATIC-LAND-UNLOCK-FEASIBILITY.md.
        Dc2AllowWaterLevelEnemySwaps = argv.Contains("--dc2-allow-water-swaps"),
        // Character-skin swap: Dylan renders as Gail/Rick (docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §7-9).
        Dc2CharacterSkin = ParseSkin(GetOpt("--dc2-character-skin")),
        Dc2ReginaSkin = ParseSkin(GetOpt("--dc2-regina-skin")),
        // Additive CR patch\ST*.d2p sidecar output (off; docs/parity/NONDESTRUCTIVE-INSTALL-PARITY.md).
        Dc2EmitD2pPatches = argv.Contains("--dc2-emit-d2p"),
    };
    if (GetOpt("--difficulty") is { } d && double.TryParse(d, out var diff))
        dc2Config.EnemyDifficulty = Math.Clamp(diff, 0, 1);

    // Donor distribution (docs/decisions/dc2/enemies/ENEMY-DISTRIBUTION-PLAN.md D7): --dc2-fixed-species implies fixed
    // mode; --dc2-enemy-mode fixed without a species is an error; --dc2-weight overrides are
    // weighted-mode only. The pin must be a safe LAND donor — the bulk path has no --allow-unsafe.
    if (GetOpt("--dc2-fixed-species") is { } fixedSpec)
    {
        var pin = Dc2RoomEnemySwap.ResolveDonor(fixedSpec);
        if (pin is null || !Dc2RoomEnemySwap.IsSafeLandDonor(pin) || pin.Confidence != Confidence.Known)
        {
            Console.Error.WriteLine($"error: --dc2-fixed-species: '{fixedSpec}' is not a safe LAND donor. "
                + "Use velociraptor/oviraptor/allosaurus/inostrancevia/triceratops/tyrannosaurus/"
                + "giganotosaurus or a TYPE literal like 0x03.");
            return 1;
        }
        dc2Config.Dc2EnemyMode = Dc2EnemyDistributionMode.Fixed;
        dc2Config.Dc2FixedSpeciesType = pin.Type;
    }
    if (GetOpt("--dc2-enemy-mode") is { } mode)
    {
        switch (mode.ToLowerInvariant())
        {
            case "weighted":
                dc2Config.Dc2EnemyMode = Dc2EnemyDistributionMode.Weighted;
                dc2Config.Dc2FixedSpeciesType = null;
                break;
            case "fixed" when dc2Config.Dc2FixedSpeciesType is not null:
                break; // already set via --dc2-fixed-species
            case "fixed":
                Console.Error.WriteLine("error: --dc2-enemy-mode fixed requires --dc2-fixed-species <name|0xNN>.");
                return 1;
            default:
                Console.Error.WriteLine($"error: --dc2-enemy-mode must be weighted or fixed, got '{mode}'.");
                return 1;
        }
    }
    var weightOverrides = new Dictionary<int, byte>();
    for (int i = 0; i < argv.Length; i++)
    {
        if (argv[i] != "--dc2-weight") continue;
        var spec = i + 1 < argv.Length ? argv[++i] : null;
        var parts = spec?.Split('=', 2);
        var species = parts is { Length: 2 } ? Dc2RoomEnemySwap.ResolveDonor(parts[0]) : null;
        if (species is null || !byte.TryParse(parts![1], out var w) || w > Dc2DonorPicker.MaxWeight)
        {
            Console.Error.WriteLine($"error: --dc2-weight expects <name|0xNN>=<0..{Dc2DonorPicker.MaxWeight}>, "
                + $"got '{spec}'.");
            return 1;
        }
        weightOverrides[species.Type] = w;
    }
    if (weightOverrides.Count > 0)
        dc2Config.Dc2SpeciesWeights = weightOverrides;

    // Raptor tiers (docs/reference/dc2/enemies/RAPTOR-TIER-RE.md §4): --dc2-raptor-tiers enables the pass;
    // --dc2-raptor-weight <variant>=<0..15> (repeatable) overrides the per-variant pool weights;
    // --dc2-blue-combo <1..20> sets the natural blue/super-raptor combo threshold (20 = vanilla).
    dc2Config.Dc2RandomizeRaptorTiers = argv.Contains("--dc2-raptor-tiers");
    var tierOverrides = new Dictionary<int, byte>();
    for (int i = 0; i < argv.Length; i++)
    {
        if (argv[i] != "--dc2-raptor-weight") continue;
        var spec = i + 1 < argv.Length ? argv[++i] : null;
        var parts = spec?.Split('=', 2);
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], out var variant)
            || variant < 0 || variant > Dc2RaptorTierTable.MaxVariant
            || !byte.TryParse(parts[1], out var tw) || tw > Dc2DonorPicker.MaxWeight)
        {
            Console.Error.WriteLine($"error: --dc2-raptor-weight expects <0..{Dc2RaptorTierTable.MaxVariant}>"
                + $"=<0..{Dc2DonorPicker.MaxWeight}>, got '{spec}'.");
            return 1;
        }
        tierOverrides[variant] = tw;
    }
    if (tierOverrides.Count > 0)
        dc2Config.Dc2RaptorTierWeights = tierOverrides;
    if (GetOpt("--dc2-raptor-colour") is { } colourMode)
    {
        switch (colourMode.ToLowerInvariant())
        {
            case "room": dc2Config.Dc2RaptorColourMode = Dc2RaptorColourMode.RoomTier; break;
            case "mixed": dc2Config.Dc2RaptorColourMode = Dc2RaptorColourMode.MixedTiers; break;
            default:
                Console.Error.WriteLine($"error: --dc2-raptor-colour must be room or mixed, got '{colourMode}'.");
                return 1;
        }
    }
    if (GetOpt("--dc2-blue-combo") is { } combo)
    {
        if (!int.TryParse(combo, out var threshold)
            || threshold < DinoRand.FileFormats.Exe.Dc2RaptorPatch.MinComboThreshold
            || threshold > DinoRand.FileFormats.Exe.Dc2RaptorPatch.MaxComboThreshold)
        {
            Console.Error.WriteLine("error: --dc2-blue-combo expects a hit count from 1 to 20 (20 = vanilla).");
            return 1;
        }
        dc2Config.Dc2BlueRaptorComboThreshold = threshold;
    }

    // Killable injected T-Rex (docs/decisions/dc2/enemies/DC2-TREX-KILLABLE-LEVER-PLAN.md): --dc2-trex-killable patches
    // Dino2.exe so a randomized T-Rex dies normally, EXCEPT in the two vanilla boss rooms (ST200/ST903).
    dc2Config.Dc2MakeTrexKillable = argv.Contains("--dc2-trex-killable");

    // In-bounds injected Mosasaurus grab (docs/decisions/dc2/enemies/DC2-MOSA-GRAB-SUPPRESS-PLAN.md):
    // --dc2-mosa-no-grab patches Dino2.exe so an injected E80 Mosasaurus's grab no longer launches the
    // player out of bounds in land rooms, EXCEPT in the native aquatic rooms (ST700/702/703/704).
    dc2Config.Dc2SuppressMosaGrab = argv.Contains("--dc2-mosa-no-grab");

    // In-bounds injected Mosasaurus knockback (DC2-MOSA-GRAB-SUPPRESS-PLAN.md §8.5, K105): a SEPARATE OOB
    // channel from the grab — --dc2-mosa-no-knockback patches Dino2.exe so an injected E80 Mosasaurus's
    // tail/proximity knockback can no longer fling the player out of bounds in land rooms, EXCEPT in the
    // native aquatic rooms (ST700/702/703/704).
    dc2Config.Dc2SuppressMosaKnockback = argv.Contains("--dc2-mosa-no-knockback");

    // Behavior-layer OOB fix (DC2-MOSA-GRAB-SUPPRESS-PLAN.md §9, K106): rather than gate shared movement,
    // --dc2-mosa-tail-to-bite patches Dino2.exe so an injected E80 Mosasaurus does its narrow bite instead
    // of the OOB-causing wide-turn tail strike in land rooms, EXCEPT the native aquatic rooms
    // (ST700/702/703/704). Changes WHICH behavior the mosa runs; touches no player-movement code.
    dc2Config.Dc2RedirectMosaTail = argv.Contains("--dc2-mosa-tail-to-bite");

    // Killable injected Triceratops (RCA §7b): --dc2-triceratops-killable remaps E70's out-of-range
    // death animation index (8 -> 7) in Dino2.exe so an injected Triceratops dies with a real
    // animation instead of crashing.
    dc2Config.Dc2MakeTriceratopsKillable = argv.Contains("--dc2-triceratops-killable");

    // Inostra spawn-descriptor NULL guard: --dc2-inostra-spawn-guard forces on the Dino2.exe emitter
    // NULL-cursor guard so an injected Inostrancevia (E50, a DEFAULT donor) doesn't crash the
    // emergence emitter. Auto-applied below whenever a run can inject E50.
    dc2Config.Dc2MakeInostraSpawnSafe = argv.Contains("--dc2-inostra-spawn-guard");

    bool dc2EmitSpoiler = !argv.Contains("--no-spoiler");
    var dc2Result = new Dc2RandomizerRunner(dc2Game).Run(installDir, dc2Out, dc2Seed, dc2Config,
                                                         dc2EmitSpoiler);
    Console.WriteLine($"seed {dc2Seed} → {dc2Result.RoomCount} DC2 rooms loaded, "
        + $"{dc2Result.RoomsWritten} randomized → {Path.GetFullPath(dc2Result.OutputDir)}");
    if (dc2EmitSpoiler)
        Console.WriteLine($"spoiler: {Path.GetFullPath(Path.Combine(dc2Result.OutputDir, DinoRand.Randomizer.Spoiler.SpoilerLogBuilder.FileName))} "
            + "(debug block on top is spoiler-free; --no-spoiler to skip)");
    foreach (var line in dc2Result.Log)
        Console.WriteLine($"  {line}");

    // Raptor tier exe levers (wave pair table + blue-raptor combo threshold) patch the game's
    // Dino2.exe IN PLACE (backup-protected .bak, same contract as --dc2-shuffle-bgm) — the room
    // files above are still non-destructive output.
    if (dc2Config.Dc2RandomizeRaptorTiers
        || dc2Config.Dc2BlueRaptorComboThreshold != DinoRand.FileFormats.Exe.Dc2RaptorPatch.VanillaComboThreshold)
    {
        try
        {
            var outcome = Dc2RaptorTierInstaller.Apply(installDir, dc2Seed, dc2Config, restore: false, Console.WriteLine);
            Console.WriteLine($"raptor tier exe patch: {outcome}");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("raptor tier exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
        }
    }

    // Killable injected T-Rex exe lever (hook + code cave) — same in-place, .bak-protected contract.
    // Auto-applied whenever the run can spawn a T-Rex (boss enemies / fixed-T-Rex pin), or forced on
    // by --dc2-trex-killable. Harmless no-op when no T-Rex is actually placed.
    if (Dc2TrexKillableInstaller.WantedFor(dc2Config))
    {
        try
        {
            var outcome = Dc2TrexKillableInstaller.Apply(installDir, restore: false, Console.WriteLine);
            Console.WriteLine($"killable-T-Rex exe patch: {outcome}");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("killable-T-Rex exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
        }
    }

    // In-bounds Mosasaurus grab exe lever (hooks + code caves) — same in-place, .bak-protected contract.
    // Auto-applied whenever the run can inject a Mosasaurus (water-level swaps / fixed-mosa pin), or forced
    // on by --dc2-mosa-no-grab. Harmless no-op when no Mosasaurus is actually placed.
    if (Dc2MosaGrabSuppressInstaller.WantedFor(dc2Config))
    {
        try
        {
            var outcome = Dc2MosaGrabSuppressInstaller.Apply(installDir, restore: false, Console.WriteLine);
            Console.WriteLine($"mosa-no-grab exe patch: {outcome}");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("mosa-no-grab exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
        }
    }

    // In-bounds Mosasaurus knockback exe lever (hook + code cave) — same in-place, .bak-protected contract.
    // Auto-applied whenever the run can inject a Mosasaurus (water-level swaps / fixed-mosa pin), or forced
    // on by --dc2-mosa-no-knockback. Harmless no-op when no Mosasaurus is actually placed.
    if (Dc2MosaKnockbackSuppressInstaller.WantedFor(dc2Config))
    {
        try
        {
            var outcome = Dc2MosaKnockbackSuppressInstaller.Apply(installDir, restore: false, Console.WriteLine);
            Console.WriteLine($"mosa-no-knockback exe patch: {outcome}");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("mosa-no-knockback exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
        }
    }

    // Behavior-layer Mosasaurus tail-redirect exe lever (hook + code cave at the attack-pattern hub) — same
    // in-place, .bak-protected contract. Auto-applied whenever the run can inject a Mosasaurus (water-level
    // swaps / fixed-mosa pin), or forced on by --dc2-mosa-tail-to-bite. Harmless no-op when no Mosasaurus is
    // actually placed.
    if (Dc2MosaTailRedirectInstaller.WantedFor(dc2Config))
    {
        try
        {
            var outcome = Dc2MosaTailRedirectInstaller.Apply(installDir, restore: false, Console.WriteLine);
            Console.WriteLine($"mosa-tail-to-bite exe patch: {outcome}");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("mosa-tail-to-bite exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
        }
    }

    // Killable injected Triceratops exe lever (single-byte death-anim remap) — same in-place,
    // .bak-protected contract. Auto-applied whenever the run can inject a Triceratops (setpiece
    // enemies / fixed-Triceratops pin), or forced on by --dc2-triceratops-killable. Harmless no-op
    // when no Triceratops is actually placed (RCA §7b).
    if (Dc2TriceratopsKillableInstaller.WantedFor(dc2Config))
    {
        try
        {
            var outcome = Dc2TriceratopsKillableInstaller.Apply(installDir, restore: false, Console.WriteLine);
            Console.WriteLine($"killable-Triceratops exe patch: {outcome}");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("killable-Triceratops exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
        }
    }

    // Inostra spawn-descriptor NULL guard exe lever (hook + code cave) — same in-place, .bak-protected
    // contract. The guard patches a SHARED emitter tick driver (0x4131d0, ~10 actor-class vtables) and is
    // byte-identical when the emitter is armed, so it auto-applies for ANY cross-species run (any injected
    // donor can drive the emitter into its un-armed NULL-cursor crash), or forced on by
    // --dc2-inostra-spawn-guard. Harmless no-op otherwise (fires only on the un-armed tick).
    if (Dc2InostraSpawnGuardInstaller.WantedFor(dc2Config))
    {
        try
        {
            var outcome = Dc2InostraSpawnGuardInstaller.Apply(installDir, restore: false, Console.WriteLine);
            Console.WriteLine($"inostra-spawn-guard exe patch: {outcome}");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("inostra-spawn-guard exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
        }
    }

    Console.WriteLine("note: DC2 enemy randomization (cross-species swap) is LIVE and written "
        + "non-destructively to the output dir; item/door passes are still no-ops pending their record "
        + "decodes. Overlay onto the game with the DinoRand app (Install), or copy the files onto "
        + "rebirth\\Data yourself. docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md.");
    return 0;
}

// DC2 single-room FORCED enemy swap: convert every eligible hardcoded enemy spawn in one room to a chosen
// donor species, in place, under the backup-and-swap contract (ST*.DAT.bak; --restore reverts). This is the
// file-edit twin of the CE cave (.claude/skills/dc2-enemy-inject) — no Cheat Engine needed for a file edit.
// LAND-only by default; --allow-unsafe forces an aquatic/flyer/unresolved donor for in-game crash-classification.
static int SwapDc2Enemies(string? installDir, string roomCode, string? species, bool allowUnsafe, bool force, bool restore)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-swap-enemies requires --install <dc2GameDir>.");
        return 1;
    }
    var dataDir = new DinoCrisis2().GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a DC2 Data folder under {installDir} "
            + "(looked for rebirth/Data, Data, english/Data containing ST*.DAT).");
        return 1;
    }

    string code = roomCode.Trim().ToUpperInvariant();   // st_id key, e.g. "202", "80A" (also the file stem)
    var targetPath = Directory.EnumerateFiles(dataDir, $"ST{code}.DAT").FirstOrDefault();
    if (targetPath is null)
    {
        Console.Error.WriteLine($"error: ST{code}.DAT not found in {dataDir} (room code is the st_id, e.g. 202 or 80A).");
        return 1;
    }
    string backup = targetPath + Dc2BackupSwapSink.BackupSuffix;

    // --restore: revert this one room from its sink backup (distinct from GameInstaller's .dinorand_backup).
    if (restore)
    {
        if (!File.Exists(backup))
        {
            Console.WriteLine($"nothing to restore (no {Path.GetFileName(backup)} next to {Path.GetFileName(targetPath)}).");
            return 0;
        }
        File.Copy(backup, targetPath, overwrite: true);
        File.Delete(backup);
        Console.WriteLine($"restored: {targetPath}  (from .bak; removed the backup)");
        return 0;
    }

    if (species is null)
    {
        Console.Error.WriteLine("error: --species <name|0xNN> is required "
            + "(e.g. --species oviraptor or --species 0x07).");
        return 1;
    }
    var donor = Dc2RoomEnemySwap.ResolveDonor(species);
    if (donor is null)
    {
        Console.Error.WriteLine($"error: --species: unknown donor '{species}'. Use a creature name "
            + "(velociraptor/oviraptor/allosaurus/inostrancevia/triceratops/tyrannosaurus/giganotosaurus/"
            + "pteranodon/mosasaurus) or a TYPE literal like 0x07.");
        return 1;
    }

    // LAND-only guard. Aquatic crashes; flyer is crash-safe but its ground AI is unverified; unresolved is
    // unknown — all need --allow-unsafe (the skill's crash-classification path). docs/reference/dc2/enemies/CROSS-SPECIES-SWAP-RE.md.
    if (!Dc2RoomEnemySwap.IsSafeLandDonor(donor) && !allowUnsafe)
    {
        string why = donor.Habitat switch
        {
            Dc2Habitat.Aquatic => "is AQUATIC — crashes as a land spawn",
            Dc2Habitat.NonLand => "is NON-LAND (aquatic or flyer, unresolved) — crashes as a land spawn",
            Dc2Habitat.Flyer   => "is a FLYER — crash-safe, but its ground-combat AI is unverified",
            _                  => "has UNRESOLVED habitat — may crash",
        };
        Console.Error.WriteLine($"error: '{donor.Creature}' (0x{donor.Type:X2}) {why}.");
        Console.Error.WriteLine("       Re-run with --allow-unsafe to force it (for in-game crash-classification only).");
        return 1;
    }

    // Both enemy-creation paths (K65): op-0x1a spawn literals (spawn-graph.json) and native wave
    // descriptors (wave-descriptors.json). A wave-only room (ST105/ST104-class) has no — or only
    // generic — spawn-graph entries and is swapped purely through its descriptors.
    var spawns = Dc2SpawnGraph.LoadEmbedded().ForRoom(code) ?? Array.Empty<Dc2SpawnRecord>();
    var wave = Dc2WaveTable.LoadEmbedded().ForRoom(code);
    if (spawns.Count == 0 && wave is null)
    {
        Console.Error.WriteLine($"error: room {code} has no enemy spawns and arms no waves "
            + "(neither data/dc2/spawn-graph.json nor wave-descriptors.json knows it).");
        return 1;
    }

    // Room-level guards (parity with the bulk Dc2EnemyRandomizer pass): a set-piece room's enemies are
    // scripted (a swap breaks the sequence), and an aquatic-native room's intended enemy shouldn't be
    // converted to a land donor. Refuse both by default; --force overrides (distinct from the donor-habitat
    // --allow-unsafe). docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md.
    bool setpieceRoom = Dc2RoomExclusions.IsExcluded(code);
    bool aquaticRoom = Dc2RoomEnemySwap.IsAquaticNativeRoom(spawns, code, wave);
    if ((setpieceRoom || aquaticRoom) && !force)
    {
        if (setpieceRoom)
            Console.Error.WriteLine($"error: ST{code} is a set-piece room (its enemies are scripted) — a swap may break it.");
        if (aquaticRoom)
            Console.Error.WriteLine($"error: ST{code} is non-land-native (hosts an aquatic/non-land species) — swapping its intended enemy is wrong.");
        Console.Error.WriteLine("       Re-run with --force to edit it anyway.");
        return 1;
    }
    if (force && setpieceRoom)
        Console.Error.WriteLine($"warn : ST{code} is a set-piece room — forcing (--force; the scripted sequence may break).");
    if (force && aquaticRoom)
        Console.Error.WriteLine($"warn : ST{code} is non-land-native — forcing (--force; the swap is cosmetically wrong).");

    var plan = Dc2RoomEnemySwap.Plan(spawns, wave, donor.Type);
    if (plan.IsEmpty)
    {
        Console.Error.WriteLine($"error: ST{code} has no editable hardcoded enemy spawn and no wave "
            + "descriptor (its spawns are all generic 0x10 / non-literal). Nothing to swap.");
        return 1;
    }

    if (!Dc2RoomEnemySwap.IsSafeLandDonor(donor))
        Console.Error.WriteLine($"warn : 0x{donor.Type:X2} ({donor.Creature}) is {donor.Habitat} — may crash. "
            + "Forcing (--allow-unsafe).");

    // Read PRISTINE bytes (from .bak if present, else the live vanilla file) so re-runs are non-compounding,
    // then apply the combined plan in one repack and emit through the backup-once sink.
    var pristine = File.Exists(backup) ? backup : targetPath;
    var bytes = Dc2SpawnEditor.ApplyEdits(File.ReadAllBytes(pristine),
        plan.WordEdits.Select(w => (w.ValueOff, (short)w.NewType)),
        plan.ByteEdits.Select(b => (b.Offset, b.NewValue)));
    Dc2BackupSwapSink.EmitTo(targetPath, bytes);

    var typeConversions = plan.WordEdits.Where(e => e.OldType != -1).ToList();
    var fromSummary = string.Join(", ", typeConversions
        .GroupBy(e => e.OldType)
        .Select(g => $"{g.Count()}× {Dc2SpeciesTable.ForType(g.Key)?.Creature ?? (g.Key == 0x10 ? "generic ambush" : "?")} (0x{g.Key:X2})"));
    int waveDescs = wave?.Descriptors.Count ?? 0;
    Console.WriteLine($"ST{code}: swapped {typeConversions.Count} spawn(s) + {waveDescs} wave descriptor(s) "
        + $"-> {donor.Creature} (0x{donor.Type:X2}, {donor.EFile})");
    if (fromSummary.Length > 0) Console.WriteLine($"from    : {fromSummary}");
    if (waveDescs > 0)
        Console.WriteLine($"waves   : native TYPE(s) "
            + string.Join("/", wave!.Descriptors.Select(d => $"0x{d.NativeType:X2}").Distinct())
            + $" -> 0x{donor.Type:X2} (per-frame respawns follow the donor; K65)");
    Console.WriteLine($"wrote   : {targetPath}  (in place)");
    Console.WriteLine($"backup  : {backup}");
    Console.WriteLine($"undo    : dinorand --dc2-swap-enemies {code} --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : launch the game, enter ST{code}, and confirm the {donor.Creature} spawns "
        + "(no crash = land/flyer; visual species ID; a crash on entry = aquatic/unloadable).");

    // A Triceratops (E70) is a setpiece model whose standard death path requests an out-of-range
    // animation clip → crash on kill (RCA §7b). Apply the killability lever so the injected donor dies
    // with a real animation instead of crashing. In-place, .bak-protected; reverse via
    //   dinorand --dc2-swap-enemies <room> --install <dir> --restore   (rooms) and Dino2.exe.bak (exe).
    if (donor.Type == 0x09)
    {
        try
        {
            var outcome = Dc2TriceratopsKillableInstaller.Apply(installDir, restore: false, Console.WriteLine);
            Console.WriteLine($"killable-Triceratops exe patch: {outcome}");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("killable-Triceratops exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
        }
    }

    // An Inostrancevia (E50) can be driven into the PSX-recompiled emergence/burst emitter, which
    // dereferences its spawn-descriptor cursor with no NULL check → crash in a room that armed no
    // descriptor list for it (witnessed live in ST902; RCA). Apply the emitter NULL-guard so it spawns
    // nothing instead of crashing. In-place, .bak-protected; reverse via
    //   dinorand --dc2-swap-enemies <room> --install <dir> --restore   (rooms) and Dino2.exe.bak (exe).
    if (donor.Type == 0x0e)
    {
        try
        {
            var outcome = Dc2InostraSpawnGuardInstaller.Apply(installDir, restore: false, Console.WriteLine);
            Console.WriteLine($"inostra-spawn-guard exe patch: {outcome}");
        }
        catch (IOException)
        {
            Console.Error.WriteLine("inostra-spawn-guard exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
        }
    }
    return 0;
}

// DC2 BGM shuffle (docs/decisions/dc2/audio/DC2-BGM-RANDO-PLAN.md I2; I3 gate PASSED 2026-07-05 — live RPM witness
// id 171 -> rerouted ME_2200 consumed on ST203 entry + human ear confirm): permute the Dino2.exe
// music-file pointer table (slots 150..217 -> ME_/MF_/MS_*.DAT) within like-classes so every
// (file-id, track-index) request stays resolvable. Reversible: pristine .bak
// plus a slice-only --restore (Dc2MusicTablePatch.RestoreCanonical) that leaves other patches alone.
static int ShuffleDc2Bgm(string? installDir, string? seedArg, bool restore)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-shuffle-bgm requires --install <dc2GameDir>.");
        return 1;
    }
    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();
    var outcome = Dc2BgmShuffleInstaller.Apply(installDir, seed.Value, restore, Console.WriteLine);
    switch (outcome)
    {
        case Dc2BgmShuffleOutcome.Applied:
            Console.WriteLine($"seed    : {seed}");
            Console.WriteLine($"undo    : dinorand --dc2-shuffle-bgm --install \"{installDir}\" --restore");
            Console.WriteLine("note    : relaunch the game to hear it; the table is read on stream open.");
            return 0;
        case Dc2BgmShuffleOutcome.Restored:
            return 0;
        default:
            return 1;
    }
}

// DC2 music export: unpack every container's tracks to .mp3 under --out (default bgm-export/dc2/_all).
static int ExportDc2Bgm(string? installDir, string? outDir)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-export-bgm requires --install <dc2GameDir>.");
        return 1;
    }
    outDir ??= Path.Combine("bgm-export", "dc2", "_all");
    int n = Dc2BgmExportInstaller.Export(installDir, outDir, Console.WriteLine);
    if (n == 0) { Console.Error.WriteLine("no music exported (no DC2 Data dir or no M[SEF]_*.DAT)."); return 1; }
    Console.WriteLine("next    : play/sort the .mp3s, drop favourites into <pack>/data/bgm/<tag>/, then --dc2-import-bgm.");
    return 0;
}

// DC2 music import: rebuild containers from a donor pack into --out (the user copies them into Data\).
static int ImportDc2Bgm(string? installDir, string? packsRoot, string? outDir, string? seedArg)
{
    if (installDir is null || packsRoot is null || outDir is null)
    {
        Console.Error.WriteLine("error: --dc2-import-bgm requires --install <dc2GameDir> --bgm-packs <dir> --out <dir>.");
        return 1;
    }
    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();
    int n = Dc2BgmImportInstaller.Import(installDir, packsRoot, outDir, seed.Value, Console.WriteLine);
    if (n == 0) { Console.Error.WriteLine("no containers written (no DC2 Data dir, empty pack, or ffmpeg missing for non-mp3 donors)."); return 1; }
    Console.WriteLine($"seed    : {seed}");
    return 0;
}

// DC2 shop shuffle (docs/decisions/dc2/shop/DC2-SHOP-RANDO-PLAN.md I1+I2; I3 live witness PENDING): permute the
// retail prices and stock-unlock bitmasks among the 11 for-sale shop ids. Both permutations are
// computed from the canonical tables (never compounds). Reversible: pristine .bak plus a
// table-only --restore (Dc2ShopTablePatch.RestoreCanonical) that leaves other exe patches alone.
static int ShuffleDc2Shop(string? installDir, string? seedArg, bool restore)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-shuffle-shop requires --install <dc2GameDir>.");
        return 1;
    }
    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();
    var outcome = Dc2ShopShuffleInstaller.Apply(installDir, seed.Value, restore, Console.WriteLine);
    switch (outcome)
    {
        case Dc2ShopShuffleOutcome.Applied:
            Console.WriteLine($"seed    : {seed}");
            Console.WriteLine($"undo    : dinorand --dc2-shuffle-shop --install \"{installDir}\" --restore");
            Console.WriteLine("note    : prices show in the shop after a relaunch; difficulty still halves/doubles them.");
            return 0;
        case Dc2ShopShuffleOutcome.Restored:
            return 0;
        default:
            return 1;
    }
}

static int RandomizeDc2Weapons(string? installDir, string? seedArg, bool restore, bool shuffleShop)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-randomize-weapons requires --install <dc2GameDir>.");
        return 1;
    }
    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();
    if (shuffleShop)
    {
        var shop = Dc2ShopShuffleInstaller.Apply(installDir, seed.Value, restore, Console.WriteLine,
            shuffleCatalogMasks: false);
        if (shop is Dc2ShopShuffleOutcome.NotFound or Dc2ShopShuffleOutcome.UnrecognizedVersion)
            return 1;
    }
    var outcome = Dc2RandomizedWeaponInstaller.Apply(installDir, seed, restore, Console.WriteLine);
    switch (outcome)
    {
        case Dc2RandomizedWeaponOutcome.Applied:
            Console.WriteLine($"seed    : {seed}");
            Console.WriteLine($"undo    : dinorand --dc2-randomize-weapons --install \"{installDir}\" --restore");
            Console.WriteLine("note    : randomized weapons retain the original owner's inventory icon.");
            return 0;
        case Dc2RandomizedWeaponOutcome.Restored:
            return 0;
        default:
            return 1;
    }
}

// DC2 REbirth DoorSkip passthrough: one ini key in config.ini [DLL] (K115).
static int EnableDc2DoorSkip(string? installDir)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-door-skip requires --install <dc2GameDir>.");
        return 1;
    }
    if (!Dc2DoorSkipInstaller.Apply(installDir, Console.WriteLine)) return 1;
    Console.WriteLine("undo    : set DoorSkip = 0 in config.ini [DLL] (or via the REbirth launcher).");
    return 0;
}

// DC2 elevator puzzle-code scramble (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §3, K108): rewrite the
// 8 candidate imm32s in the keypad setup fn with distinct seed-derived 4-digit codes (digits 0–5).
// The in-game "Elevator Security Code" file renders from the same runtime field the check reads, so
// displayed==checked survives any rewrite. Reversible: pristine .bak plus a slot-only --restore.
static int ScrambleDc2PuzzleCodes(string? installDir, string? seedArg, bool restore)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-scramble-puzzle-codes requires --install <dc2GameDir>.");
        return 1;
    }
    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();
    var outcome = Dc2PuzzleCodeScrambleInstaller.Apply(installDir, seed.Value, restore, Console.WriteLine);
    switch (outcome)
    {
        case Dc2PuzzleCodeOutcome.Applied:
            Console.WriteLine($"seed    : {seed}");
            Console.WriteLine($"undo    : dinorand --dc2-scramble-puzzle-codes --install \"{installDir}\" --restore");
            Console.WriteLine("note    : relaunch the game; the rolled code shows in the in-game Elevator Security Code file.");
            return 0;
        case Dc2PuzzleCodeOutcome.Restored:
            return 0;
        default:
            return 1;
    }
}

// DC2 stungun-circuit shuffle (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 2, K110): rewrite the
// blink box-id literals of ST607 routines 7/8 + ST402 routines 23/24 in place (lengths, cadence and
// terminators untouched). Room-file edit under the ST*.DAT.bak backup-and-swap contract; --restore
// reverts both rooms and removes the backups. Both rooms are pin-validated before either is written.
static int ShuffleDc2Circuits(string? installDir, string? seedArg, bool restore)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-shuffle-circuits requires --install <dc2GameDir>.");
        return 1;
    }
    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();
    var outcome = Dc2CircuitShuffleInstaller.Apply(installDir, seed.Value, restore, Console.WriteLine);
    switch (outcome)
    {
        case Dc2CircuitShuffleOutcome.Applied:
            Console.WriteLine($"seed    : {seed}");
            Console.WriteLine($"undo    : dinorand --dc2-shuffle-circuits --install \"{installDir}\" --restore");
            Console.WriteLine("note    : re-enter the generator (ST607) / silo bridge (ST402) circuit puzzles to see the new blink order.");
            return 0;
        case Dc2CircuitShuffleOutcome.Restored:
            return 0;
        default:
            return 1;
    }
}

// DC2 Key-Plate terminal re-key (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §4 item 4, K118): permute
// ST205 routine[2]'s SAT-9 routing so a seed-chosen plate is the correct one, and recolour the blue
// slot panel (entry 13) to match. Room-file edit under the ST205.DAT.bak backup-and-swap contract;
// --restore reverts. Byte-identical when the seed picks the vanilla blue plate.
static int RekeyDc2PlateDoor(string? installDir, string? seedArg, bool restore)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-rekey-plate-door requires --install <dc2GameDir>.");
        return 1;
    }
    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();
    var outcome = Dc2PlateKeyInstaller.Apply(installDir, seed.Value, restore, Console.WriteLine);
    switch (outcome)
    {
        case Dc2PlateKeyOutcome.Applied:
            Console.WriteLine($"seed    : {seed}");
            Console.WriteLine($"undo    : dinorand --dc2-rekey-plate-door --install \"{installDir}\" --restore");
            Console.WriteLine("note    : at the ST205 Regina terminal, use the newly-correct plate colour (shown above); a different plate prints \"not the correct one for this terminal\".");
            return 0;
        case Dc2PlateKeyOutcome.Restored:
            return 0;
        default:
            return 1;
    }
}

static int Dc2CrossCharWeapons(string? installDir, bool restore)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-cross-char-weapons requires --install <dc2GameDir>.");
        return 1;
    }
    var outcome = Dc2CrossCharWeaponInstaller.Apply(installDir, restore, Console.WriteLine);
    switch (outcome)
    {
        case Dc2CrossCharWeaponOutcome.Applied:
            Console.WriteLine($"undo    : dinorand --dc2-cross-char-weapons --install \"{installDir}\" --restore");
            Console.WriteLine("note    : each character now carries the other's weapons once owned, rendered on their own body.");
            foreach (var p in Dc2CrossCharWeaponPatch.Pairs.Where(p => !p.HeadGraftSafe))
                Console.WriteLine($"warn    : {p.Target} + weapon 0x{p.WeaponId:X2} ({p.OwnerFile}) is built but NOT yet witnessed in-game.");
            return 0;
        case Dc2CrossCharWeaponOutcome.AlreadyApplied:
        case Dc2CrossCharWeaponOutcome.Restored:
            return 0;
        default:
            return 1;
    }
}

// DC2 starting main-weapon lever (docs/decisions/dc2/loadout/DC2-STARTING-LOADOUT-PLAN.md I2; I3 human gate PENDING —
// each pooled id still needs an in-game new-game witness). Patches only the weapon-id (lo) bytes of
// the bootstrap equip words; subweapon bytes untouched. Reversible: pristine .bak plus a two-byte
// --restore (Dc2StartingLoadoutPatch.RestoreCanonical) that leaves other exe patches alone.
static int SetDc2StartWeapon(string? installDir, string? seedArg, string? spec, bool restore, bool allowUnsafe = false,
                             bool addAndEquip = false)
{
    if (installDir is null)
    {
        Console.Error.WriteLine("error: --dc2-randomize-start-weapon/--dc2-start-weapon requires --install <dc2GameDir>.");
        return 1;
    }

    byte? dylanId = null, reginaId = null;
    if (spec is not null && !restore)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            byte id;
            try
            {
                id = kv.Length == 2 ? Convert.ToByte(kv[1], kv[1].StartsWith("0x") ? 16 : 10) : throw new FormatException();
            }
            catch (Exception)
            {
                Console.Error.WriteLine($"error: bad --dc2-start-weapon part '{part}' (expected dylan=<id> or regina=<id>).");
                return 1;
            }
            switch (kv[0].ToLowerInvariant())
            {
                case "dylan": dylanId = id; break;
                case "regina": reginaId = id; break;
                default:
                    Console.Error.WriteLine($"error: unknown character '{kv[0]}' (dylan or regina).");
                    return 1;
            }
        }
        if (dylanId is null && reginaId is null)
        {
            Console.Error.WriteLine("error: --dc2-start-weapon needs at least dylan=<id> or regina=<id>.");
            return 1;
        }
        // Explicit form: an unnamed character stays canonical rather than getting a random roll.
        dylanId ??= Dc2StartingLoadoutPatch.DylanCanonicalId;
        reginaId ??= Dc2StartingLoadoutPatch.ReginaCanonicalId;
    }

    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();
    Dc2BgmShuffleOutcome outcome;
    try
    {
        outcome = Dc2StartingLoadoutInstaller.Apply(installDir, seed.Value, dylanId, reginaId, restore, Console.WriteLine, allowUnsafe, addAndEquip);
    }
    catch (ArgumentOutOfRangeException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        if (!allowUnsafe && !addAndEquip && ex.ParamName is "dylanId" or "reginaId")
            Console.Error.WriteLine("hint : pass --dc2-add-and-equip to unlock the full band safely (installs the "
                                    + "weapon-ring div-0 guard), or --allow-unsafe to force it unguarded (investigation only).");
        return 1;
    }
    switch (outcome)
    {
        case Dc2BgmShuffleOutcome.Applied:
            if (dylanId is null || reginaId is null) Console.WriteLine($"seed    : {seed}");
            Console.WriteLine($"undo    : dinorand --dc2-randomize-start-weapon --install \"{installDir}\" --restore");
            Console.WriteLine("note    : takes effect on NEW GAME only (bootstrap immediates); saves keep their loadout.");
            return 0;
        case Dc2BgmShuffleOutcome.Restored:
            return 0;
        default:
            return 1;
    }
}

// Replace a room's raptor with a Therizinosaurus: the entangled cat8 donor needs the multi-range closure
// geometry import + its texture carried in (so it wears its own skin, not the room's) + a surgical EXE
// cat8 patch (so the spawn dispatches to the real Theri AI handler 0x56BFA0, not stage-2's free stub). One
// reversible command (docs/decisions/dc1/theri/THERI-0203-SWAP-PLAN.md). Stage-2 only for now (the cat8 record slot is
// verified free + located only for stage 2).
static int SwapTherizinosaurus(string installDir, string dataDir, int code, int stage, int room,
                               RoomFileRef targetRef, IReadOnlyList<RoomFileRef> refs, int? maxDonorOverride = null)
{
    // The Theri's VRAM identity (data/dc1/enemy-textures.json cat8): X=640 texture column, palette row 511.
    ushort[] tpages = { 0x8a, 0x9a };
    const ushort clut = 0x7ff0;

    // The surgical EXE patch needs the target stage's installed AI-record VA, with cat8 verified free.
    // Stage 1 (cat8 NULL) and stage 2 (cat8 stub) are confirmed; stage 6 hosts cat8 natively (no patch).
    uint? recordVa = stage switch
    {
        1 => ExePatcher.Stage1AiRecordVa,
        2 => ExePatcher.Stage2AiRecordVa,
        _ => null,
    };
    if (recordVa is null && stage != 6)
    {
        Console.Error.WriteLine($"error: Theri swap supports stage 1 and 2 (cat8 AI-record located + verified "
            + $"free), or stage 6 (cat8 native, no patch); got ST{code:X3}.");
        return 1;
    }

    // Donor: a stage-6 Theri room, preferring the catalog donor st603. Needs BOTH a closure-extractable
    // cat8 geometry record AND the Theri texture uploaded at (640,0)+(768,511).
    SpeciesDonorMulti? donor = null;
    byte[]? donorFile = null;
    byte[]? donorRdt = null;          // kept so an over-ceiling room can re-extract a clip-stripped donor
    EnemyRecord? donorRec = null;
    string? donorName = null;
    foreach (var rref in refs.OrderByDescending(r => r.Stage == 6 && r.Room == 0x03))
    {
        RoomFile rf;
        try { rf = RoomFile.ReadFromFile(rref.Stage, rref.Room, rref.Path); }
        catch { continue; }
        var rec = rf.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == DinoSpecies.Therizinosaurus);
        if (rec is null) continue;
        try
        {
            var d = SpeciesImporter.ExtractDonorClosure(rf.RdtBuffer, rec);
            var bytes = File.ReadAllBytes(rref.Path);
            _ = TextureImporter.ExtractSpeciesTexture(bytes, tpages, clut); // donor must upload the Theri skin
            donor = d; donorFile = bytes; donorRdt = rf.RdtBuffer; donorRec = rec; donorName = Path.GetFileName(rref.Path);
            break;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
    }
    if (donor is null || donorFile is null || donorRdt is null || donorRec is null)
    {
        Console.Error.WriteLine("error: no closure-extractable Therizinosaurus donor with a (640,0) texture found in the install");
        return 1;
    }

    // Back up pristine + read the target from the pristine copy (re-runnable / non-compounding).
    var backupPath = BackupOnceWarned(dataDir, targetRef.Path);

    var target = RoomFile.Read(stage, room, File.ReadAllBytes(backupPath));
    var victim = target.Enemies.FirstOrDefault(e => e.IsRandomizableDino);
    if (victim is null)
    {
        Console.Error.WriteLine($"error: ST{code:X3} has no randomizable enemy to replace.");
        return 1;
    }
    int idx = target.Enemies.IndexOf(victim);
    var was = victim.Species;

    // 1. Geometry (multi-range closure import; appends the donor, growing the RDT). Track B: the effective
    //    append ceiling is the RESIDENT-POOL FLOOR (RDT 0x5D000), NOT the looser work-struct 0x60000 — a grown
    //    RDT past 0x5D000 overwrites the heap-resident persistent door/prop models, which is defect A (0102
    //    entry render AV) and, per the live convergent hypothesis (THERI-0102-PLAYABLE-FIX-PLAN "LIVE SESSION"),
    //    also defect B (the same overrun corrupts the shared death/anim object). So we AUTO clip-strip the donor
    //    to fit ≤ the floor (region-reuse overlay/split stays DEAD — CE-proven, THERI-RDT-MEMORY-LIBERATION-PLAN).
    //    Refuse only if the max eligible strip (MaxClipStripDropCount) still can't clear the floor.
    int vanillaLen = target.RdtBuffer.Length;
    int maxDonor = SpeciesImporter.ResidentPoolFloor - ((vanillaLen + 3) & ~3);
    if (maxDonorOverride is { } mdo && mdo < maxDonor)
    {
        maxDonor = mdo;   // --max-donor: tighter budget for RE (forces a deeper strip).
        Console.WriteLine($"[--max-donor] forcing donor budget {maxDonor} B (RE override, below the resident-pool floor).");
    }
    int[] TheriProtectedClips = { 1, 12, 13, 15, 19, 48 };   // live st605 used set + likely-death (see plan)

    // One reusable fit check (EnemyImportBudget) decides the RDT budget + clip-strip — replacing the inline
    // maxDonor/strip/refusal logic. The Theri stages its texture via the fixed-column overwrite/append path
    // below (step 2), NOT VRAM relocation, so the helper's VRAM half is skipped (null texture); this is the
    // RDT-budget half only. Behaviour is byte-identical: the prepared block set imports through ImportSpecies.
    var fit = EnemyImportBudget.Evaluate(donorRdt, donorRec.OriginalModelPtr, donorRec.OriginalMotionPtr,
                                         vanillaLen, maxDonor, SpeciesImporter.MaxClipStripDropCount,
                                         TheriProtectedClips, ReadOnlySpan<byte>.Empty, donorTexture: null,
                                         out var geoBlocks);
    if (!fit.Fits)
    {
        if (fit.Limiting == ImportFitConstraint.ClipStripBudget)
            // Quality-bounded eligibility: needs more than the conservative pre-capture drop budget, so we
            // never ship a visibly stripped Theri. Raised after the live used-clip capture.
            Console.Error.WriteLine($"error: ST{code:X3}+Theri needs {fit.DroppedClips} clips dropped ({fit.DroppedBytes} B) "
                + $"to fit under the resident-pool floor, over the quality-bounded limit of "
                + $"{SpeciesImporter.MaxClipStripDropCount}. Pick a smaller room, or raise the limit after the "
                + "live used-clip capture. Nothing was written.");
        else
        {
            // RdtBudget: the max eligible strip cannot bring the grown RDT ≤ the resident-pool floor → Track B
            // can't enable this room. Refuse; ship nothing.
            Console.Error.WriteLine($"error: ST{code:X3} is too big for the Theri even after clip-strip: {fit.Reason}");
            Console.Error.WriteLine("hint : nothing was written.");
        }
        return 1;
    }
    int droppedClips = fit.DroppedClips, droppedBytes = fit.DroppedBytes;
    var geoDonor = donor with { Blocks = geoBlocks! };
    target.ImportSpecies(geoDonor, idx);

    // Final headroom assert — the (possibly stripped) append must land ≤ the resident-pool floor (the effective
    // ceiling for a pool-sharing species); never ship over it. EngineRoomRdtCeiling remains the hard upper bound.
    if (target.RdtBuffer.Length > SpeciesImporter.ResidentPoolFloor)
    {
        int over = target.RdtBuffer.Length - SpeciesImporter.ResidentPoolFloor;
        Console.Error.WriteLine($"error: ST{code:X3}+Theri RDT = {target.RdtBuffer.Length} B exceeds the resident-pool "
            + $"floor {SpeciesImporter.ResidentPoolFloor} B by {over} B. Nothing was written.");
        return 1;
    }

    byte[] geo = target.Write();

    // 2. Texture: overwrite in place if the room already uploads the Theri's column (e.g. 0203), else ADD it
    //    as new VRAM entries (small rooms). Texture lives in VRAM, so the append does not affect the RDT ceiling.
    byte[] final;
    try
    {
        // Per-resource import: overwrite whichever of {texture page, palette} the room already uploads,
        // append whichever it lacks (handles 0102's palette-present / texture-absent layout).
        final = TextureImporter.ImportSpeciesTexture(geo, donorFile, tpages, clut);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: texture import failed: {ex.Message}");
        return 1;
    }
    File.WriteAllBytes(targetRef.Path, final);

    // 3. Surgical EXE patch: write the target stage's cat8 slot = the Theri handler so the spawn dispatches
    //    to a real AI handler. Stage 6 hosts cat8 natively, so it needs no patch.
    ExePatchResult? exeRes = null;
    string? reactionNote = null;
    string? soundNote = null;
    if (recordVa is { } rva)
    {
        try
        {
            exeRes = GameInstaller.PatchExeCatSlot(dataDir, rva, 8, ExePatcher.TheriCat8HandlerVa);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
            Console.Error.WriteLine("hint : the room file was written, but the EXE is locked — CLOSE the game and re-run (the .dat step is idempotent), or run --exe-patch separately.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: exe patch failed: {ex.Message}");
            return 1;
        }

        // 3b. Defect-B fix (live-verified 2026-06-22): cave the cat8 hit-REACTION descriptor stream from the
        //     normal-Theri donor (st603 RDT 0x3C0E0) and repoint the idx4/5 reaction tables, so the appended
        //     Theri can be attacked/shot/killed without the walker AV at 0x41685B (the descriptor the engine
        //     reads at the EXE-baked offset is appended garbage in the imported room). Cat8-exclusive; uses the
        //     FULL un-stripped donorRdt. Best-effort: geometry/texture/slot are already written and this re-runs
        //     cleanly, so a failure warns (attack stays crashy) rather than aborting. Supersedes the refuted
        //     Architecture-A descriptor-table redirect (GameInstaller.PatchExeCat8HitDescriptors et al., left
        //     unreferenced). See docs/decisions/dc1/theri/THERI-0102-PLAYABLE-FIX-PLAN.md "Deviations".
        try
        {
            var rr = GameInstaller.PatchExeCat8HitReaction(dataDir, donorRdt);
            reactionNote = rr.Repoints.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warn : cat8 hit-reaction patch failed ({ex.Message}); the Theri spawns + "
                + "animates but will CRASH when it attacks / is shot. Re-run after closing the game.");
        }

        // 3c. Enemy-sound fix (docs/reference/dc1/se/ENEMY-SOUND-SYSTEM.md): DC1 binds the loaded dino SE set to the room
        //     id via a per-room SE manifest in DINO.exe, so a swapped room keeps playing its native (raptor)
        //     samples — every swapped enemy plays VELOCIRAPTOR sounds. Copy a native Theri room's (st605)
        //     dino SE sub-block over this room's so it loads the Theri (se\dino\j_*) set at the ids the cat8
        //     AI emits. Best-effort, like the reaction patch: a failure leaves wrong sound, not a broken swap.
        try
        {
            var se = GameInstaller.PatchExeRoomEnemySe(dataDir, stage, room, donorStage: 6, donorRoom: 5);
            soundNote = se.Repoints.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warn : enemy-sound patch failed ({ex.Message}); the Theri will play "
                + "raptor sounds (cosmetic). Re-run after closing the game.");
        }
    }

    string placement = droppedClips > 0
        ? $"append+clip-strip: dropped {droppedClips} clips, {droppedBytes} B"
        : "append";
    Console.WriteLine($"ST{code:X3} slot{victim.Slot}: {was} -> Therizinosaurus  (cat8; multi-range geometry [{placement}] + texture)");
    Console.WriteLine($"donor   : {donorName} (geometry closure + (640,0) texture / (768,511) palette)");
    Console.WriteLine($"wrote   : {targetRef.Path}");
    if (exeRes is { } er)
    {
        Console.WriteLine($"exe     : {er.ExePath}");
        foreach (var r in er.Repoints) Console.WriteLine($"exe slot: {r}");
        if (reactionNote is not null) Console.WriteLine($"exe rxn : {reactionNote}");
        if (soundNote is not null) Console.WriteLine($"exe snd : {soundNote}");
    }
    else
        Console.WriteLine($"exe     : (none — stage {stage} hosts cat8 natively)");
    Console.WriteLine($"backup  : {backupPath}");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : CLOSE then relaunch; enter ST{code:X3} and confirm the Theri spawns, animates and is killable (CE gate).");
    return 0;
}

// Replace a room's raptor with a Tyrannosaurus (cat3 20-bone boss rig): closure geometry import + its
// (640,0) texture carried in + a surgical EXE cat3 patch (stage 1's installed AI-record has a NULL cat3
// slot, so an imported T-Rex would dispatch a null handler). One reversible command
// (docs/decisions/dc1/models/TREX-INTO-0102-FEASIBILITY.md). Stage 1 (cat3 NULL, patched) or stage 6 (cat3 native, no patch).
static int SwapTyrannosaurus(string installDir, string dataDir, int code, int stage, int room,
                             RoomFileRef targetRef, IReadOnlyList<RoomFileRef> refs)
{
    // The T-Rex boss rig's VRAM identity (data/dc1/enemy-textures.json cat3): X=640 column, palette row 510.
    ushort[] tpages = { 0x8a, 0x9a };
    const ushort clut = 0x7fb0;

    // The surgical EXE patch needs the target stage's installed AI-record VA, with cat3 verified free.
    // Stage 1's cat3 slot is NULL (verified from DINO.exe); stage 6 hosts cat3 natively (no patch).
    uint? recordVa = stage switch
    {
        1 => ExePatcher.Stage1AiRecordVa,
        _ => null,
    };
    if (recordVa is null && stage != 6)
    {
        Console.Error.WriteLine($"error: Tyrannosaurus swap supports stage 1 (cat3 AI-record located + "
            + $"verified NULL) or stage 6 (cat3 native, no patch); got ST{code:X3}.");
        return 1;
    }

    // Donor: a T-Rex room, preferring the catalog donor st60a (a single 0x20 cat-3 entity). Needs BOTH a
    // closure-extractable cat3/20-bone geometry record AND the T-Rex texture uploaded at (640,0)+(768,510).
    SpeciesDonorMulti? donor = null;
    byte[]? donorFile = null;
    string? donorName = null;
    foreach (var rref in refs.OrderByDescending(r => r.Stage == 6 && r.Room == 0x0a))
    {
        RoomFile rf;
        try { rf = RoomFile.ReadFromFile(rref.Stage, rref.Room, rref.Path); }
        catch { continue; }
        var rec = rf.Enemies.FirstOrDefault(e => e.Category == 3 && e.SpeciesBoneCount == 20);
        if (rec is null) continue;
        try
        {
            var d = SpeciesImporter.ExtractDonorClosure(rf.RdtBuffer, rec);
            var bytes = File.ReadAllBytes(rref.Path);
            _ = TextureImporter.ExtractSpeciesTexture(bytes, tpages, clut); // donor must upload the T-Rex skin
            donor = d; donorFile = bytes; donorName = Path.GetFileName(rref.Path);
            break;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
    }
    if (donor is null || donorFile is null)
    {
        Console.Error.WriteLine("error: no closure-extractable Tyrannosaurus donor with a (640,0) texture found in the install");
        return 1;
    }

    // Back up pristine + read the target from the pristine copy (re-runnable / non-compounding).
    var backupPath = BackupOnceWarned(dataDir, targetRef.Path);

    var target = RoomFile.Read(stage, room, File.ReadAllBytes(backupPath));
    var victim = target.Enemies.FirstOrDefault(e => e.IsRandomizableDino);
    if (victim is null)
    {
        Console.Error.WriteLine($"error: ST{code:X3} has no randomizable enemy to replace.");
        return 1;
    }
    int idx = target.Enemies.IndexOf(victim);
    var was = victim.Species;

    // 1. Geometry (multi-range closure import; the T-Rex boss closure is a single clean range). The append
    //    grows the RDT; refuse if it would overflow the engine room buffer (0x60000) — that overrun crashes
    //    the model-setup walk (docs/decisions/dc1/theri/THERI-0203-SWAP-PLAN.md). 0102 fits with ~40 KB to spare.
    int appendLen = ((target.RdtBuffer.Length + 3) & ~3) + donor.Blocks.Bytes.Length;
    if (appendLen > SpeciesImporter.EngineRoomRdtCeiling)
    {
        int over = appendLen - SpeciesImporter.EngineRoomRdtCeiling;
        Console.Error.WriteLine($"error: ST{code:X3}+T-Rex RDT = {appendLen} B exceeds the engine ceiling "
            + $"{SpeciesImporter.EngineRoomRdtCeiling} B by {over} B — it would overflow and crash in-game. "
            + "Pick a smaller room. Nothing was written.");
        return 1;
    }
    target.ImportSpecies(donor, idx);
    byte[] geo = target.Write();

    // 2. Texture: 0102 does not upload the X=640 column, so ADD it as new VRAM entries (texture lives in
    //    VRAM, so it does not affect the RDT ceiling). Overwrite in place if the room already has it.
    bool hasSlot = TextureImporter.HasSpeciesTextureSlot(geo, tpages, clut);
    byte[] final;
    try
    {
        final = hasSlot
            ? TextureImporter.OverwriteSpeciesTextureInPlace(geo, donorFile, tpages, clut)
            : TextureImporter.AppendSpeciesTexture(geo, donorFile, tpages, clut);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: texture import failed: {ex.Message}");
        return 1;
    }
    File.WriteAllBytes(targetRef.Path, final);

    // 3. Surgical EXE patch: write the target stage's cat3 slot = the T-Rex boss handler so the spawn
    //    dispatches to a real AI handler. Stage 6 hosts cat3 natively, so it needs no patch.
    ExePatchResult? exeRes = null;
    if (recordVa is { } rva)
    {
        try
        {
            exeRes = GameInstaller.PatchExeCatSlot(dataDir, rva, 3, ExePatcher.TrexCat3HandlerVa);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
            Console.Error.WriteLine("hint : the room file was written, but the EXE is locked — CLOSE the game and re-run (the .dat step is idempotent), or run --exe-patch separately.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: exe patch failed: {ex.Message}");
            return 1;
        }
    }

    Console.WriteLine($"ST{code:X3} slot{victim.Slot}: {was} -> Tyrannosaurus  (cat3 boss rig; multi-range geometry [append] + texture [{(hasSlot ? "overwrote" : "added X=640")}])");
    Console.WriteLine($"donor   : {donorName} (geometry closure {donor.Blocks.Bytes.Length} B + (640,0) texture / (768,510) palette)");
    Console.WriteLine($"wrote   : {targetRef.Path}  (RDT {target.RdtBuffer.Length} B / ceiling {SpeciesImporter.EngineRoomRdtCeiling} B)");
    if (exeRes is { } er)
    {
        Console.WriteLine($"exe     : {er.ExePath}");
        foreach (var r in er.Repoints) Console.WriteLine($"exe slot: {r}");
    }
    else
        Console.WriteLine($"exe     : (none — stage {stage} hosts cat3 natively)");
    Console.WriteLine($"backup  : {backupPath}");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : CLOSE then relaunch; enter ST{code:X3} and confirm the T-Rex spawns, animates and is killable (CE gate).");
    return 0;
}

// Replace a room's raptor with a Swarm (cat5 7-bone small-dino / compy): geometry import + its texture
// RELOCATED into a free VRAM region + a surgical EXE cat5 patch + the universal walker null-guard + a
// per-room enemy-SE retarget. One reversible command. The swarm shares the raptor's EXACT VRAM — tpage
// 0x8b @ (704,0) and CLUT 0x7ff0 @ (768,511) — with a smaller 64x128 rect, so a fixed-column overwrite
// (the Theri/T-Rex path) would mismatch / clobber the raptor and the swarm would wear the raptor skin;
// the RELOCATE import (RoomFile.ImportSpeciesTextured → free column + free palette row + RewriteModelCodes)
// gives it its own skin in fresh VRAM. Stage 1/2 (cat5 verified free, patched) or stage 3 (cat5 native,
// no patch). (docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md; swarm cat5 handler 0x5C8116 in EXE-SYMBOLS.md.)
static int SwapSwarm(string installDir, string dataDir, int code, int stage, int room,
                     RoomFileRef targetRef, IReadOnlyList<RoomFileRef> refs)
{
    // The surgical EXE patch needs the target stage's installed AI-record VA, with cat5 verified free.
    // Stage 1 & 2's cat5 slots are NULL (verified from DINO.exe); stage 3 hosts cat5 natively (no patch).
    uint? recordVa = stage switch
    {
        1 => ExePatcher.Stage1AiRecordVa,
        2 => ExePatcher.Stage2AiRecordVa,
        _ => null,
    };
    if (recordVa is null && stage != 3)
    {
        Console.Error.WriteLine($"error: swarm swap supports stage 1 or 2 (cat5 AI-record located + verified "
            + $"free) or stage 3 (cat5 native, no patch); got ST{code:X3}.");
        return 1;
    }

    // Donor: a swarm room, preferring st307 (stage 3 room 7 — 4 cat5 instances sharing one model). Needs a
    // clean cat5/7-bone geometry record AND its texture present (tpage 0x8b @ (704,0) + CLUT 0x7ff0 @ (768,511)).
    SpeciesDonor? donor = null;
    string? donorName = null;
    int donorStage = 3, donorRoom = 7;
    foreach (var rref in refs.OrderByDescending(r => r.Stage == 3 && r.Room == 0x07))
    {
        RoomFile rf;
        try { rf = RoomFile.ReadFromFile(rref.Stage, rref.Room, rref.Path); }
        catch { continue; }
        var rec = rf.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == DinoSpecies.Swarm);
        if (rec is null) continue;
        try
        {
            donor = SpeciesImporter.ExtractDonor(rf.RdtBuffer, rec, Heads(rf))
                with { Texture = SpeciesImporter.TryExtractTexture(rf.OriginalBytes, rf.RdtBuffer, rec.OriginalModelPtr) };
            donorName = Path.GetFileName(rref.Path);
            donorStage = rref.Stage; donorRoom = rref.Room;
            break;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
    }
    if (donor is null)
    {
        Console.Error.WriteLine("error: no clean cat5 swarm donor found in the install");
        return 1;
    }
    if (donor.Texture is null)
    {
        Console.Error.WriteLine("error: swarm donor texture (tpage 0x8b / CLUT 0x7ff0) could not be resolved; "
            + "the swarm would wear the wrong skin. Nothing was written.");
        return 1;
    }

    // Back up pristine + read the target from the pristine copy (re-runnable / non-compounding).
    var backupPath = BackupOnceWarned(dataDir, targetRef.Path);

    var target = RoomFile.Read(stage, room, File.ReadAllBytes(backupPath));
    var victim = target.Enemies.FirstOrDefault(e => e.IsRandomizableDino);
    if (victim is null)
    {
        Console.Error.WriteLine($"error: ST{code:X3} has no randomizable enemy to replace.");
        return 1;
    }
    int idx = target.Enemies.IndexOf(victim);
    var was = victim.Species;

    // 0. The swarm is a COORDINATED GROUP: a lone import NULL-AVs in the swarm AI because the engine never
    //    forms the pack (docs/decisions/dc1/spawn/SWARM-0102-GROUP-SPAWN-PLAN.md). Native rooms place 4 like-model cat-5
    //    members; we mirror that — member 0 over the victim, +3 members sharing the one closure. The 3 extra
    //    members need valid floor coords in the target room: a door's entry pose INTO this room is a
    //    guaranteed floor point (where the engine safely places the player), clustered to keep them on floor.
    var groupBase = FindRoomFloorBase(refs, code);
    var extraMembers = SwarmGroupOffsets(groupBase);
    // The native swarm's op58 record: the engine runs it to ALLOCATE the shared type-0x17 coordination effect
    // that every member's +0x1C8 links to (op58 handler 0x429AD2 → byte[slot+1]=byte[record+2]=0x17;
    // live-RE'd in 040B 2026-06-24). Prepended before the member records so the effect exists when they spawn.
    byte[] swarmCoordinationRecord = { 0x58, 0x00, 0x17, 0x02, 0x00, 0x00, 0x00, 0x00 };

    // 1. Geometry + texture: import the small swarm geometry over the victim and RELOCATE the donor texture to
    //    a free VRAM column + palette row (so the swarm samples its OWN texels, not the raptor's shared (704,0)
    //    column / (768,511) palette). Texture lives in VRAM, off the RDT ceiling. Refuse if no free VRAM region
    //    (geometry-only would render mis-coloured = the "raptor skin" trap). Member 0 over the victim + the
    //    3 group members are placed in one operation (they share the single imported closure).
    var tex = target.ImportSpeciesGroupTextured(donor, idx, extraMembers, swarmCoordinationRecord);
    if (tex.Outcome != RoomFile.TextureImportOutcome.Relocated)
    {
        Console.Error.WriteLine($"error: ST{code:X3} has no free VRAM region for the swarm texture; it would "
            + "wear the raptor skin. Pick a room with free VRAM. Nothing was written.");
        return 1;
    }
    if (target.RdtBuffer.Length > SpeciesImporter.ResidentPoolFloor)
    {
        Console.Error.WriteLine($"error: ST{code:X3}+swarm RDT = {target.RdtBuffer.Length} B exceeds the "
            + $"resident-pool floor {SpeciesImporter.ResidentPoolFloor} B. Nothing was written.");
        return 1;
    }
    File.WriteAllBytes(targetRef.Path, target.Write());

    // 2. Surgical EXE patches (stage 1/2 only; stage 3 hosts cat5 natively):
    //    (a) cat5 slot = the swarm handler so the spawn dispatches to a real AI handler;
    //    (b) the universal walker null-guard (cross-import hit/death reaction crash-safety);
    //    (c) per-room enemy-SE retarget so the swarm plays its own (compy) sounds, not raptor sounds.
    ExePatchResult? exeRes = null;
    string? guardNote = null, soundNote = null, effectNote = null;
    // Stage-1/2 effect-dispatch table (loaded into [0x6D3CC4]); its type-0x17 slot is garbage in a non-swarm
    // stage, so the per-frame driver wild-calls when it processes the swarm coordination effect (crash dump
    // 22032). Patch in the real handler, just like the cat5 AI slot.
    uint? effectTableVa = stage switch
    {
        1 => ExePatcher.Stage1EffectTableVa,
        2 => ExePatcher.Stage2EffectTableVa,
        _ => null,
    };
    if (recordVa is { } rva)
    {
        try
        {
            exeRes = GameInstaller.PatchExeCatSlot(dataDir, rva, 5, ExePatcher.SwarmCat5HandlerVa);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
            Console.Error.WriteLine("hint : the room file was written, but the EXE is locked — CLOSE the game and re-run (the .dat step is idempotent).");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: exe patch failed: {ex.Message}");
            return 1;
        }

        // Swarm coordination-effect handler: install the type-0x17 effect handler into this stage's effect
        // table so the per-frame driver dispatches the swarm's coordination effect instead of wild-calling.
        if (effectTableVa is { } etv)
        {
            try
            {
                effectNote = GameInstaller.PatchExeCatSlot(dataDir, etv, ExePatcher.SwarmEffectType,
                    ExePatcher.SwarmEffectHandlerVa).Repoints.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"warn : swarm effect-handler patch failed ({ex.Message}); the swarm "
                    + "will CRASH on entry (effect dispatch). Re-run after closing the game.");
            }
        }

        // Universal walker null-guard: cross-import hit/death reactions can feed the shared +0x34 walker
        // 0x416845 a garbage walk count (AV 0x41685B). Benign for valid callers. Best-effort.
        try { guardNote = GameInstaller.PatchExeWalkerNullGuard(dataDir).Repoints.FirstOrDefault(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warn : walker null-guard failed ({ex.Message}); the swarm spawns + "
                + "animates but may CRASH when hit/killed. Re-run after closing the game.");
        }

        // Enemy-sound: bind st<code> to the swarm donor's (compy) SE sub-block so it stops playing raptor SE.
        try { soundNote = GameInstaller.PatchExeRoomEnemySe(dataDir, stage, room, donorStage, donorRoom).Repoints.FirstOrDefault(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warn : enemy-sound patch failed ({ex.Message}); the swarm will play "
                + "raptor sounds (cosmetic). Re-run after closing the game.");
        }
    }

    Console.WriteLine($"ST{code:X3} slot{victim.Slot}: {was} -> Swarm  (cat5; geometry import + texture relocated to {tex.TextureRect}, palette {tex.PaletteRect})");
    Console.WriteLine($"group   : {1 + extraMembers.Count} cat5 members sharing one model closure (member0 over the victim; "
        + $"{extraMembers.Count} added near {groupBase} so the engine forms the pack)");
    Console.WriteLine($"donor   : {donorName} (geometry + (704,0) texture / (768,511) palette)");
    Console.WriteLine($"wrote   : {targetRef.Path}  (RDT {target.RdtBuffer.Length} B / floor {SpeciesImporter.ResidentPoolFloor} B)");
    if (exeRes is { } er)
    {
        Console.WriteLine($"exe     : {er.ExePath}");
        foreach (var r in er.Repoints) Console.WriteLine($"exe slot: {r}");
        if (effectNote is not null) Console.WriteLine($"exe eff : {effectNote}");
        if (guardNote is not null) Console.WriteLine($"exe grd : {guardNote}");
        if (soundNote is not null) Console.WriteLine($"exe snd : {soundNote}");
    }
    else
        Console.WriteLine($"exe     : (none — stage {stage} hosts cat5 natively)");
    Console.WriteLine($"backup  : {backupPath}");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : LAB TOOL — the swarm spawns (4 members), links its coordination effect, renders and");
    Console.WriteLine($"          animates for a few seconds, then CRASHES (a behavior SCD task desyncs into the relocated");
    Console.WriteLine($"          heap closure — docs/decisions/dc1/spawn/SWARM-0102-GROUP-SPAWN-PLAN.md). NOT shippable; --restore to revert.");
    return 0;

    static IEnumerable<int> Heads(RoomFile rf)
    {
        foreach (var e in rf.Enemies)
        {
            if (e.OriginalModelPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
            if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
        }
    }
}

// A guaranteed-valid floor point in the target room: the entry pose of any door anywhere that leads INTO
// it (where the engine safely places an arriving player — real floor, not a wall). Same source SetDoor uses
// for an arrival pose. Falls back to (0,0,0) if no inbound door is found (caller still clusters around it).
static (short X, short Y, short Z) FindRoomFloorBase(IReadOnlyList<RoomFileRef> refs, int roomCode)
{
    foreach (var rref in refs)
    {
        RoomFile rf;
        try { rf = RoomFile.ReadFromFile(rref.Stage, rref.Room, rref.Path); }
        catch { continue; }
        var door = rf.Doors.FirstOrDefault(
            d => ((d.OriginalTargetStage << 8) | d.OriginalTargetRoom) == roomCode);
        if (door is not null)
            return (door.OriginalEntryX, door.OriginalEntryY, door.OriginalEntryZ);
    }
    return (0, 0, 0);
}

// The 3 extra swarm members (member 0 is the imported victim). A tight cluster around a known-good floor
// point keeps them on valid floor (room geometry is unknown here); the swarm AI disperses them into the
// pack on spawn. Native swarms spread ~1200 units; we stay tighter (±~400) to avoid clipping into walls
// near the door, accepting a CE/visual refinement of the coords if the live test shows a bad spot.
static IReadOnlyList<(short X, short Y, short Z, short Rotation)> SwarmGroupOffsets((short X, short Y, short Z) b)
{
    const short d = 400;
    return new (short, short, short, short)[]
    {
        ((short)(b.X + d), b.Y, b.Z,             0),
        (b.X,              b.Y, (short)(b.Z + d), 0),
        ((short)(b.X + d), b.Y, (short)(b.Z + d), 0),
    };
}

// Import one foreign species over the first randomizable raptor in a single room, writing the
// edited room file directly into the game's Data folder (backed up first to .dinorand_backup so
// --restore undoes it). For an isolated in-game test of the cross-room importer.
static int SwapSpecies(string installDir, string roomArg, string speciesName, int? maxDonorOverride = null)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }
    if (!TryParseSpecies(speciesName, out var species))
    {
        Console.Error.WriteLine($"error: unknown species '{speciesName}' " +
            "(largeground | raptorheavy | pteranodon | tyrannosaurus | swarm | velociraptor | therizinosaurus)");
        return 1;
    }

    int code;
    try { code = Convert.ToInt32(roomArg.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16); }
    catch { Console.Error.WriteLine($"error: bad room code '{roomArg}' (e.g. 106)"); return 1; }
    int stage = code >> 8, room = code & 0xff;

    var refs = game.EnumerateRooms(installDir).ToList();
    var targetRef = refs.FirstOrDefault(r => r.Stage == stage && r.Room == room);
    if (targetRef is null)
    {
        Console.Error.WriteLine($"error: room ST{code:X3} not found under {dataDir}");
        return 1;
    }

    // The Therizinosaurus (cat8) is an entangled multi-range donor AND needs its texture carried +
    // a surgical EXE cat8 patch — its own path (docs/decisions/dc1/theri/THERI-0203-SWAP-PLAN.md).
    if (species == DinoSpecies.Therizinosaurus)
        return SwapTherizinosaurus(installDir, dataDir, code, stage, room, targetRef, refs, maxDonorOverride);

    // The Tyrannosaurus (cat3 boss rig) is set-piece-excluded from the generic donor search
    // (EnemyRecord.IsRandomizableDino), so it has its own path: closure geometry from a T-Rex room +
    // its (640,0) texture carried in + a surgical EXE cat3 patch (docs/decisions/dc1/models/TREX-INTO-0102-FEASIBILITY.md).
    if (species == DinoSpecies.Tyrannosaurus)
        return SwapTyrannosaurus(installDir, dataDir, code, stage, room, targetRef, refs);

    // The Swarm (cat5) is a cross-category import: like the Theri/T-Rex it needs a surgical EXE cat5 patch
    // (stages 1/2 have a NULL cat5 slot) + the walker null-guard + an enemy-SE retarget, and its texture must
    // be RELOCATED (it shares the raptor's exact VRAM column/row), so it has its own path.
    if (species == DinoSpecies.Swarm)
        return SwapSwarm(installDir, dataDir, code, stage, room, targetRef, refs);

    // Find a clean donor for the requested species anywhere in the game (carrying its texture).
    SpeciesDonor? donor = null;
    foreach (var rref in refs)
    {
        var rf = RoomFile.ReadFromFile(rref.Stage, rref.Room, rref.Path);
        var rec = rf.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == species);
        if (rec is null) continue;
        try
        {
            donor = SpeciesImporter.ExtractDonor(rf.RdtBuffer, rec, Heads(rf))
                with { Texture = SpeciesImporter.TryExtractTexture(rf.OriginalBytes, rf.RdtBuffer, rec.OriginalModelPtr) };
            break;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
    }
    if (donor is null)
    {
        Console.Error.WriteLine($"error: no clean donor for {species} found in the install");
        return 1;
    }

    // Back up the pristine original (once) to the shared backup folder. Always edit FROM the pristine
    // copy so re-running with a different species starts clean (no compounding imports).
    var backupPath = BackupOnceWarned(dataDir, targetRef.Path);

    var target = RoomFile.Read(stage, room, File.ReadAllBytes(backupPath));
    var victim = target.Enemies.FirstOrDefault(e => e.IsRandomizableDino);
    if (victim is null)
    {
        Console.Error.WriteLine($"error: ST{code:X3} has no randomizable enemy to replace " +
            "(its raptors may be event-spawned, not in the init script)");
        return 1;
    }
    int idx = target.Enemies.IndexOf(victim);
    var was = victim.Species;

    var result = target.ImportSpeciesTextured(donor, idx);
    File.WriteAllBytes(targetRef.Path, target.Write());

    string texNote = result.Outcome switch
    {
        RoomFile.TextureImportOutcome.Relocated =>
            $"texture relocated to {result.TextureRect}, palette {result.PaletteRect}",
        RoomFile.TextureImportOutcome.ReclaimedVictim =>
            $"texture placed over the victim's own column {result.TextureRect}, palette {result.PaletteRect} (VRAM was full; victim slot reclaimed)",
        _ => donor.Texture is null
            ? "geometry only — donor texture unresolved (may be mis-coloured)"
            : "geometry only — no free VRAM region in target (may be mis-coloured)",
    };
    Console.WriteLine($"ST{code:X3} slot{victim.Slot}: {was} -> {species}  (RDT grew, model+motion imported; {texNote})");
    Console.WriteLine($"wrote   : {targetRef.Path}");
    Console.WriteLine($"backup  : {backupPath}");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    return 0;

    static IEnumerable<int> Heads(RoomFile rf)
    {
        foreach (var e in rf.Enemies)
        {
            if (e.OriginalModelPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
            if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
        }
    }
}

// Inject a brand-new enemy into a room that has none, writing the edited file straight into Data
// (backed up to .dinorand_backup so --restore undoes it). Mirrors SwapSpecies. Spawn position comes
// from --at x,y,z[,rot]; if omitted, it is harvested from an existing enemy in the room (a known-good
// floor point) — a truly-empty room therefore requires --at. CE is the real validator of the spot.
static int AddEnemy(string installDir, string roomArg, string speciesName, string? atArg, string? killFlagArg = null, string? seedArg = null, string? offsetArg = null,
                    string? hpArg = null, string? aiParamArg = null, string? birthModeArg = null, string? activateArg = null, string? slotArg = null)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }
    if (!TryParseSpecies(speciesName, out var species))
    {
        Console.Error.WriteLine($"error: unknown species '{speciesName}' " +
            "(raptorheavy | pteranodon | swarm | velociraptor; largeground/trex are not yet importable)");
        return 1;
    }

    int code;
    try { code = Convert.ToInt32(roomArg.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16); }
    catch { Console.Error.WriteLine($"error: bad room code '{roomArg}' (e.g. 107)"); return 1; }
    int stage = code >> 8, room = code & 0xff;

    // --add-enemy-at: a caller-specified RDT injection offset (event-sub targeting). Hex, from the
    // room's decoded script. null = the default init-script auto-placement (AddEnemy).
    int? injectOffset = null;
    if (offsetArg is not null)
    {
        try { injectOffset = Convert.ToInt32(offsetArg.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16); }
        catch { Console.Error.WriteLine($"error: bad rdt offset '{offsetArg}' (hex, e.g. 0x49000)"); return 1; }
    }

    var refs = game.EnumerateRooms(installDir).ToList();
    var targetRef = refs.FirstOrDefault(r => r.Stage == stage && r.Room == room);
    if (targetRef is null)
    {
        Console.Error.WriteLine($"error: room ST{code:X3} not found under {dataDir}");
        return 1;
    }

    // Donor for the requested species, found anywhere in the install (carrying its texture).
    SpeciesDonor? donor = null;
    foreach (var rref in refs)
    {
        var rf = RoomFile.ReadFromFile(rref.Stage, rref.Room, rref.Path);
        var rec = rf.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == species);
        if (rec is null) continue;
        try
        {
            donor = SpeciesImporter.ExtractDonor(rf.RdtBuffer, rec, Heads(rf))
                with { Texture = SpeciesImporter.TryExtractTexture(rf.OriginalBytes, rf.RdtBuffer, rec.OriginalModelPtr) };
            break;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
    }
    if (donor is null)
    {
        Console.Error.WriteLine($"error: no cleanly-importable donor for {species} found in the install");
        return 1;
    }

    // Edit from the pristine backup (re-runnable / non-compounding).
    var backupPath = BackupOnceWarned(dataDir, targetRef.Path);
    var target = RoomFile.Read(stage, room, File.ReadAllBytes(backupPath));

    // Spawn position: --at x,y,z[,rot], else harvest from an existing enemy in the room.
    short x, y, z, rot;
    if (atArg is not null)
    {
        var p = atArg.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length is < 3 or > 4
            || !short.TryParse(p[0], out x) || !short.TryParse(p[1], out y) || !short.TryParse(p[2], out z))
        {
            Console.Error.WriteLine($"error: --at must be x,y,z[,rot] (signed 16-bit), got '{atArg}'");
            return 1;
        }
        rot = p.Length == 4 && short.TryParse(p[3], out var r) ? r : (short)0;
    }
    else
    {
        // Fallback when --at is omitted: a door entry pose — a guaranteed-walkable floor point the game
        // spawns the player on when entering this room (door records carry the destination arrival pose;
        // docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.12/24). Better than a trigger-zone centroid or a harvested
        // coordinate because the engine itself materialises the player there. Pick one at random (seeded,
        // so a re-run is reproducible) when several doors lead in.
        var poses = DoorEntryPoses.IntoRoom(AllDoors(), code);
        if (poses.Count > 0)
        {
            int seed = code; // deterministic per-room default; override with --seed
            if (seedArg is not null && !int.TryParse(seedArg, out seed))
            {
                Console.Error.WriteLine($"error: --seed must be an integer, got '{seedArg}'");
                return 1;
            }
            var pick = poses[new Random(seed).Next(poses.Count)];
            (x, y, z, rot) = (pick.X, pick.Y, pick.Z, pick.Rotation);
            Console.WriteLine($"note    : no --at given; spawning at a door entry pose ({x},{y},{z}) rot {rot} "
                + $"({poses.Count} arrival point(s) into ST{code:X3}; picked with seed {seed})");
        }
        else
        {
            // No door leads into this room (event-only / start room): last resort, harvest a known-good
            // floor point from an existing enemy in the room.
            var src = target.Enemies.FirstOrDefault();
            if (src is null)
            {
                Console.Error.WriteLine($"error: ST{code:X3} has no door leading in and no existing enemy to "
                    + "harvest a position from; pass --at x,y,z[,rot] (e.g. a coordinate read in Cheat Engine)");
                return 1;
            }
            (x, y, z, rot) = (src.PosX, src.PosY, src.PosZ, src.Rotation);
        }
    }

    // Kill flag (GetFlag group-4 "already-killed" id). The auto-pick only dedupes against other
    // enemies in THIS room — it can't see flags already SET in the player's save, so a low default
    // can collide with a previously-killed enemy's flag and the new enemy spawns "already dead"
    // (no entity). --kill-flag <n> forces a high/known-clear id to rule that out (CE-verified gap).
    byte? killFlag = null;
    if (killFlagArg is not null)
    {
        bool ok = killFlagArg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(killFlagArg.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var kf)
            : byte.TryParse(killFlagArg, out kf);
        if (!ok) { Console.Error.WriteLine($"error: --kill-flag must be a byte 0..255, got '{killFlagArg}'"); return 1; }
        killFlag = kf;
    }

    // Authored record fields (+6 maxHP / +3 AI param / +5 birth mode, cont.48/51) and the optional
    // op-0x22 + op-0x3a activation-pair emission (cont.49). All default to the zero template.
    static bool ParseByte(string s, out byte v) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? byte.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out v)
        : byte.TryParse(s, out v);
    var authoring = new EnemyAuthoring();
    if (hpArg is not null)
    {
        if (!ushort.TryParse(hpArg, out var hp) || hp == 0)
        { Console.Error.WriteLine($"error: --hp must be 1..65535, got '{hpArg}'"); return 1; }
        authoring = authoring with { MaxHp = hp };
        if (species is not (DinoSpecies.RaptorHeavy or DinoSpecies.Pteranodon))
            Console.WriteLine($"warning : --hp on a {species} is a provable no-op — only cat-2/cat-7 births "
                + "keep a preset (STATIC-SCD-RE cont.48); the byte is written anyway for the record.");
    }
    if (aiParamArg is not null)
    {
        if (!ParseByte(aiParamArg, out var ap))
        { Console.Error.WriteLine($"error: --ai-param must be a byte 0..255, got '{aiParamArg}'"); return 1; }
        authoring = authoring with { AiParam = ap };
    }
    if (birthModeArg is not null)
    {
        if (!ParseByte(birthModeArg, out var bm))
        { Console.Error.WriteLine($"error: --birth-mode must be a byte 0..255 (low 2 bits select), got '{birthModeArg}'"); return 1; }
        authoring = authoring with { BirthMode = bm };
    }
    if (activateArg is not null)
    {
        var ap = activateArg.Split(':', StringSplitOptions.TrimEntries);
        if (ap.Length > 3 || !ParseByte(ap[0], out var behavior))
        { Console.Error.WriteLine($"error: --activate must be <behavior>[:<blobHex16>[:<b3>]], got '{activateArg}'"); return 1; }
        byte[]? blob = null;
        byte? b3 = null;
        if (ap.Length >= 2 && ap[1].Length > 0)
        {
            if (ap[1].Length != 16)
            { Console.Error.WriteLine($"error: --activate blob must be 16 hex chars (8 bytes), got '{ap[1]}'"); return 1; }
            try { blob = Convert.FromHexString(ap[1]); }
            catch (FormatException) { Console.Error.WriteLine($"error: --activate blob is not valid hex: '{ap[1]}'"); return 1; }
        }
        if (ap.Length == 3)
        {
            if (!ParseByte(ap[2], out var b3v))
            { Console.Error.WriteLine($"error: --activate b3 must be a byte, got '{ap[2]}'"); return 1; }
            b3 = b3v;
        }
        authoring = authoring with { ActivateBehavior = behavior, ActivateBlob = blob, ActivateB3 = b3 };
    }

    // For --add-enemy-at, validate the offset up front and resolve which subroutine it lands in, so the
    // user can confirm it's an EVENT sub (the activation path) and not the inert init sub-0.
    int targetSub = -1;
    if (injectOffset is int io0)
    {
        if ((io0 & 3) != 0)
        {
            Console.Error.WriteLine($"error: rdt offset 0x{io0:x} must be 4-aligned");
            return 1;
        }
        targetSub = ScriptInjector.SubroutineAtBoundary(target.RdtBuffer, io0);
        if (targetSub < 0)
        {
            Console.Error.WriteLine($"error: rdt offset 0x{io0:x} is not a clean opcode boundary in any "
                + $"subroutine of ST{code:X3} — pick an offset from the room's decoded script "
                + "(e.g. tools/scd_re/spawn_catalog.py). Injecting there would split an instruction.");
            return 1;
        }
        // Per-category activation truth (cont.49, live-witnessed cont.57 row 1 — the old blanket
        // "init sub-0 => INERT" warning is retired): cat-2 behavior-0 self-drives; cat-1 state-1
        // is passive until a script (or --activate's emitted 0x22/0x3a pair) installs a behavior.
        if (targetSub == 0)
            Console.WriteLine(donor.Category switch
            {
                2 => $"note    : 0x{io0:x} is in the INIT sub-0 — a category-2 enemy ({species}) "
                     + "SELF-ACTIVATES and hunts from any reached offset (behavior-0 default, cont.57).",
                1 => $"warning : 0x{io0:x} is in the INIT sub-0 — a category-1 enemy ({species}) PARKS "
                     + "in passive state-1 until a script installs its behavior (cont.49). Pass "
                     + "--activate (emits the op-0x22/0x3a pair) or use an event-sub offset.",
                _ => $"warning : 0x{io0:x} is in the INIT sub-0 — category-{donor.Category} init "
                     + "activation is UNVERIFIED (only cat-2 self-activation and cat-1 parking are "
                     + "decoded, cont.49/57); prefer an event-sub offset or --activate.",
            });
    }

    // --slot: explicit large-entity slot. The auto-pick only dedupes against parsed 0x20/0x59 records
    // -- it cannot see natively-installed occupants (cont.18's 0102 slot-0 collision); pass a known-free
    // slot when the room has native enemies.
    byte? slot = null;
    if (slotArg is not null)
    {
        if (!ParseByte(slotArg, out var sl))
        { Console.Error.WriteLine($"error: --slot must be a byte 0..255, got '{slotArg}'"); return 1; }
        slot = sl;
    }

    EnemyRecord added;
    RoomFile.TexturedImportResult texResult;
    try
    {
        (added, texResult) = injectOffset is int io
            ? target.AddEnemyAtTextured(donor, io, x, y, z, rot, slot: slot, killFlag: killFlag, authoring: authoring)
            : target.AddEnemyTextured(donor, x, y, z, rot, slot: slot, killFlag: killFlag, authoring: authoring);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: could not inject into ST{code:X3}: {ex.Message}");
        return 1;
    }
    File.WriteAllBytes(targetRef.Path, target.Write());

    string modelNote = texResult.Outcome == RoomFile.TextureImportOutcome.Reused
        ? "reused the room's already-loaded model (renderable; texture already resident)"
        : "model+motion imported; " + (texResult.Outcome == RoomFile.TextureImportOutcome.Relocated
            ? $"texture relocated to {texResult.TextureRect}, palette {texResult.PaletteRect}"
            : donor.Texture is null
                ? "geometry only — donor texture unresolved (may be mis-coloured)"
                : "geometry only — no free VRAM region in target (may be mis-coloured)");
    Console.WriteLine($"ST{code:X3}: added {species} at ({x},{y},{z}) rot {rot}  "
        + $"slot {added.Slot}, kill-flag {added.KillFlag}  ({modelNote})");
    if (authoring != default)
        Console.WriteLine($"authored: maxHP {authoring.MaxHp} (+6), ai-param {authoring.AiParam} (+3), "
            + $"birth-mode {authoring.BirthMode} (+5)"
            + (authoring.ActivateBehavior is byte ab
                ? $", activation pair EMITTED (op 0x22 slot {added.Slot} + op 0x3a behavior 0x{ab:x2}, "
                  + (authoring.ActivateBlob is null ? "blob copied from the room's native 0x3a / zeros)" : "explicit blob)")
                : ""));
    if (injectOffset is int io3)
        Console.WriteLine("        injected at RDT 0x" + $"{io3:x} in "
            + (targetSub == 0
                ? donor.Category switch
                {
                    2 => "init sub-0 (cat-2: self-activates)",
                    1 => "init sub-0 (cat-1: parks unless --activate)",
                    _ => $"init sub-0 (cat-{donor.Category}: activation unverified)",
                }
                : $"event sub{targetSub} (activation path)")
            + " — confirm the sub loads this species' resources before it runs.");
    Console.WriteLine($"wrote   : {targetRef.Path}");
    Console.WriteLine($"backup  : {backupPath}");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine("verify  : load the room in-game / Cheat Engine — confirm it spawns on the floor, "
        + "animates, takes damage and dies without crashing.");
    return 0;

    static IEnumerable<int> Heads(RoomFile rf)
    {
        foreach (var e in rf.Enemies)
        {
            if (e.OriginalModelPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
            if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
        }
    }

    // Every door in the install, for the entry-pose position fallback (a door's pose is the arrival
    // point in its DESTINATION room, so doors into the target room give that room's spawn points).
    IEnumerable<DoorRecord> AllDoors()
    {
        foreach (var r in refs)
        {
            RoomFile rf;
            try { rf = RoomFile.ReadFromFile(r.Stage, r.Room, r.Path); }
            catch { continue; }
            foreach (var d in rf.Doors) yield return d;
        }
    }
}

// Change one door's destination, writing the edited room straight into Data (backed up to
// .dinorand_backup so --restore undoes it). spec = "room:fromDest:toDest" (3-hex room codes). Copies a
// valid arrival pose for the destination from an existing door that already targets it, so the player
// lands at a real doorway. The entry-pose offsets are CE-decoded (DoorPoseLayout.IsDecoded); arrival
// should still be CE-verified.
static int SetDoor(string installDir, string spec)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    var parts = spec.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length != 3 || !TryRoomCode(parts[0], out int code)
        || !TryRoomCode(parts[1], out int fromDest) || !TryRoomCode(parts[2], out int toDest))
    {
        Console.Error.WriteLine($"error: --set-door expects room:fromDest:toDest as room codes (e.g. 103:102:511), got '{spec}'");
        return 1;
    }
    int stage = code >> 8, room = code & 0xff;

    var refs = game.EnumerateRooms(installDir).ToList();
    var targetRef = refs.FirstOrDefault(r => r.Stage == stage && r.Room == room);
    if (targetRef is null)
    {
        Console.Error.WriteLine($"error: room ST{code:X3} not found under {dataDir}");
        return 1;
    }

    // Find a donor door anywhere that already targets toDest, to copy a valid arrival pose for it
    // (where the engine places a player arriving in that room — a real floor point, not a wall).
    DoorRecord? donor = null;
    foreach (var rref in refs)
    {
        var rf = RoomFile.ReadFromFile(rref.Stage, rref.Room, rref.Path);
        donor = rf.Doors.FirstOrDefault(d => ((d.OriginalTargetStage << 8) | d.OriginalTargetRoom) == toDest);
        if (donor is not null) break;
    }

    // Edit from the pristine backup (re-runnable / non-compounding).
    var backupPath = BackupOnceWarned(dataDir, targetRef.Path);
    var target = RoomFile.Read(stage, room, File.ReadAllBytes(backupPath));

    var doors = target.Doors
        .Where(d => ((d.OriginalTargetStage << 8) | d.OriginalTargetRoom) == fromDest).ToList();
    if (doors.Count == 0)
    {
        var present = target.Doors
            .Select(d => $"ST{((d.OriginalTargetStage << 8) | d.OriginalTargetRoom):X3}").Distinct();
        Console.Error.WriteLine($"error: ST{code:X3} has no door leading to ST{fromDest:X3}. "
            + $"Its doors target: {string.Join(", ", present)}");
        return 1;
    }

    foreach (var d in doors)
    {
        d.TargetStage = (toDest >> 8) & 0xff;
        d.TargetRoom = toDest & 0xff;
        if (donor is not null)
        {
            d.EntryX = donor.OriginalEntryX; d.EntryY = donor.OriginalEntryY;
            d.EntryZ = donor.OriginalEntryZ; d.EntryD = donor.OriginalEntryD;
        }
    }
    File.WriteAllBytes(targetRef.Path, target.Write());

    Console.WriteLine($"ST{code:X3}: {doors.Count} door(s) ST{fromDest:X3} -> ST{toDest:X3}");
    Console.WriteLine(donor is not null
        ? $"arrival pose: carried from a door targeting ST{toDest:X3} "
          + $"(X={donor.OriginalEntryX} Y={donor.OriginalEntryY} Z={donor.OriginalEntryZ} D={donor.OriginalEntryD})"
        : $"arrival pose: NONE found targeting ST{toDest:X3} — kept the original pose; you may arrive at a "
          + "wrong spot. CE-verify and, if needed, decode a good coord.");
    Console.WriteLine($"wrote   : {targetRef.Path}");
    Console.WriteLine($"backup  : {backupPath}");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : walk that door in-game; confirm you arrive in ST{toDest:X3} on the floor (not stuck).");
    return 0;

    static bool TryRoomCode(string s, out int code)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out code);
    }
}

// Write one item id into the pickup at a given position — a probe for validating novel pickups (e.g.
// a pre-upgraded weapon variant the game never places itself, docs/decisions/cross/ITEM-RANDO-PLAN.md §7.3). spec is
// "<room>:<x,z>:<id>" (room = 3-hex SSRR; x,z = the placement quad's first corner, decimal; id = hex
// item id). Edits from the pristine backup (re-runnable), so --restore undoes it.
static int SetItem(string installDir, string spec)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    var parts = spec.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length != 3)
    {
        Console.Error.WriteLine($"error: --set-item expects room:x,z:id (e.g. 402:-5888,-11520:0x07), got '{spec}'");
        return 1;
    }
    var xz = parts[1].Split(',', StringSplitOptions.TrimEntries);
    if (!TryHex(parts[0], out int code) || xz.Length != 2
        || !short.TryParse(xz[0], out short x) || !short.TryParse(xz[1], out short z)
        || !TryHex(parts[2], out int newId))
    {
        Console.Error.WriteLine($"error: --set-item expects room:x,z:id (e.g. 402:-5888,-11520:0x07), got '{spec}'");
        return 1;
    }
    int stage = code >> 8, room = code & 0xff;

    var targetRef = game.EnumerateRooms(installDir).FirstOrDefault(r => r.Stage == stage && r.Room == room);
    if (targetRef is null)
    {
        Console.Error.WriteLine($"error: room ST{code:X3} not found under {dataDir}");
        return 1;
    }

    // Edit from the pristine backup (re-runnable / non-compounding).
    var backupPath = BackupOnceWarned(dataDir, targetRef.Path);
    var target = RoomFile.Read(stage, room, File.ReadAllBytes(backupPath));

    // Match the pickup by its placement quad's first corner (X@+0x04, Z@+0x06).
    var hit = target.Items.FirstOrDefault(i => i.Raw.Length >= 0x08
        && BitConverter.ToInt16(i.Raw, 0x04) == x && BitConverter.ToInt16(i.Raw, 0x06) == z);
    if (hit is null)
    {
        var present = target.Items
            .Where(i => i.Raw.Length >= 0x08)
            .Select(i => $"(0x{i.OriginalItemId:X2}@{BitConverter.ToInt16(i.Raw, 0x04)},{BitConverter.ToInt16(i.Raw, 0x06)})");
        Console.Error.WriteLine($"error: ST{code:X3} has no pickup at {x},{z}. Its pickups: {string.Join(" ", present)}");
        return 1;
    }

    int oldId = hit.OriginalItemId;
    hit.ItemId = newId;
    hit.Amount = Math.Max(1, hit.Amount);
    File.WriteAllBytes(targetRef.Path, target.Write());

    Console.WriteLine($"ST{code:X3} @ {x},{z}: item 0x{oldId:X2} -> 0x{newId:X2}");
    Console.WriteLine($"wrote   : {targetRef.Path}");
    Console.WriteLine($"backup  : {backupPath}");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : pick it up in-game; confirm item 0x{newId:X2} is granted/equips correctly.");
    return 0;

    static bool TryHex(string s, out int val)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out val);
    }
}

// Preset maxHP (+6) on every eligible 0x20 dino in ONE room — the CE-verification knob for the DC1-G2 HP
// lever (docs/decisions/dc1/spawn/ENEMY-SPAWN-SYSTEM.md "Gap 4 — REVERSED"). spec is "<room>[:<hp>]" (room =
// 3-hex SSRR, e.g. 102; hp = decimal 1..65535). With NO hp it just LISTS the room's enemies (dry inspect,
// no write). Edits from the pristine backup so it's re-runnable and --restore undoes it. Same 0x20-only /
// IsRandomizableDino eligibility as the enemy pass; 0x59 records are never touched (their +6 is a pointer).
static int SetEnemyHp(string installDir, string spec)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    var parts = spec.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length is < 1 or > 2 || !TryHex(parts[0], out int code))
    {
        Console.Error.WriteLine($"error: --set-enemy-hp expects room[:hp] (e.g. 102:3400 or 102 to list), got '{spec}'");
        return 1;
    }
    int? hp = null;
    if (parts.Length == 2)
    {
        if (!int.TryParse(parts[1], out int h) || h < 1 || h > 65535)
        {
            Console.Error.WriteLine($"error: hp must be a decimal 1..65535 (word), got '{parts[1]}'");
            return 1;
        }
        hp = h;
    }
    int stage = code >> 8, room = code & 0xff;

    var targetRef = game.EnumerateRooms(installDir).FirstOrDefault(r => r.Stage == stage && r.Room == room);
    if (targetRef is null)
    {
        Console.Error.WriteLine($"error: room ST{code:X3} not found under {dataDir}");
        return 1;
    }

    // Read from the pristine backup so a patch is re-runnable (non-compounding) and --restore reverts it;
    // a dry inspect reads the live file (no backup created).
    var backupPath = hp is null ? targetRef.Path : BackupOnceWarned(dataDir, targetRef.Path);
    var target = RoomFile.Read(stage, room, File.ReadAllBytes(backupPath));

    var eligible = target.Enemies
        .Where(e => e.Opcode == DcOpcodes.Enemy && e.IsRandomizableDino && e.IsHpPresettable).ToList();
    Console.WriteLine($"ST{code:X3}: {target.Enemies.Count} enemy record(s), {eligible.Count} eligible 0x20 dino(s):");
    foreach (var e in target.Enemies)
    {
        string tag = e.Opcode != DcOpcodes.Enemy || !e.IsRandomizableDino ? ""
            : e.IsHpPresettable ? "  [eligible]"
            : "  [birth-frozen: this species' init overwrites maxHP — preset would be a no-op]";
        Console.WriteLine($"  op=0x{e.Opcode:X2} cat={e.Category} species={e.Species} bones={e.SpeciesBoneCount} "
            + $"model=0x{e.OriginalModelPtr:X8} motion=0x{e.OriginalMotionPtr:X8} maxHp(+6)={e.MaxHp} @0x{e.FileOffset:X}"
            + tag);
        Console.WriteLine($"    raw: {BitConverter.ToString(e.Raw)}");
    }

    if (hp is null)
    {
        Console.WriteLine("dry inspect (no hp given) — nothing written. Re-run as --set-enemy-hp <room>:<hp> to patch.");
        return 0;
    }
    if (eligible.Count == 0)
    {
        Console.Error.WriteLine($"error: ST{code:X3} has no eligible 0x20 dino to patch "
            + "(0x59-placed enemies and birth-frozen species [Velociraptor/Swarm] are excluded).");
        return 1;
    }

    foreach (var e in eligible) e.MaxHp = (ushort)hp.Value;
    File.WriteAllBytes(targetRef.Path, target.Write());

    Console.WriteLine($"set maxHP(+6) = {hp} on {eligible.Count} record(s) (was rolling {{850,1000,750}}; the cat-2/cat-7 birth keeps a nonzero preset)");
    Console.WriteLine($"wrote   : {targetRef.Path}");
    Console.WriteLine($"backup  : {backupPath}");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : in CE, read the spawned dino's entity +0x11A (maxHP) — expect {hp}; "
        + $"it should take ~{hp.Value / 850.0:0.#}× the vanilla hits to drop.");
    return 0;

    static bool TryHex(string s, out int val)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out val);
    }
}

// Palette-copy lab (STATIC-SCD-RE cont.51): copy the --species type-2 CLUT entry (raw 16bpp, 512 B,
// matched by the model's CLUT VRAM rect) from srcRoom into dstRoom — the file-only "tint an enemy"
// lever (Blue Raptor = st511's recoloured row). The CLUT code is read from dst's own model (falling
// back to src's) so the copy targets exactly the entry the species samples. Backup contract on dst.
static int CopyEnemyPalette(string installDir, string spec, string speciesName)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }
    if (!TryParseSpecies(speciesName, out var species))
    {
        Console.Error.WriteLine($"error: unknown species '{speciesName}'");
        return 1;
    }
    var parts = spec.Split(':', StringSplitOptions.TrimEntries);
    int srcCode = 0, dstCode = 0;
    bool ok = parts.Length == 2;
    if (ok) { try { srcCode = Convert.ToInt32(parts[0], 16); dstCode = Convert.ToInt32(parts[1], 16); } catch { ok = false; } }
    if (!ok)
    {
        Console.Error.WriteLine($"error: --copy-enemy-palette expects <srcRoom>:<dstRoom> (e.g. 511:102), got '{spec}'");
        return 1;
    }

    var refs = game.EnumerateRooms(installDir).ToList();
    var srcRef = refs.FirstOrDefault(r => r.Stage == srcCode >> 8 && r.Room == (srcCode & 0xff));
    var dstRef = refs.FirstOrDefault(r => r.Stage == dstCode >> 8 && r.Room == (dstCode & 0xff));
    if (srcRef is null || dstRef is null)
    {
        Console.Error.WriteLine($"error: room ST{(srcRef is null ? srcCode : dstCode):X3} not found under {dataDir}");
        return 1;
    }

    // The species' CLUT code, read from the model the rooms actually render: prefer dst's own record
    // (that is the raptor being tinted), fall back to src's. Same CLUT across rooms per cont.51.
    var srcRoom = RoomFile.ReadFromFile(srcCode >> 8, srcCode & 0xff, srcRef.Path);
    var backupPath = BackupOnceWarned(dataDir, dstRef.Path);
    var dstBytes = File.ReadAllBytes(backupPath);
    var dstRoom = RoomFile.Read(dstCode >> 8, dstCode & 0xff, dstBytes);
    var rec = dstRoom.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == species)
           ?? srcRoom.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == species);
    if (rec is null)
    {
        Console.Error.WriteLine($"error: neither ST{dstCode:X3} nor ST{srcCode:X3} places a {species} — no model to read the CLUT from");
        return 1;
    }
    var host = dstRoom.Enemies.Contains(rec) ? dstRoom : srcRoom;
    ushort clut = TextureImporter.ReadModelTextureCodes(host.RdtBuffer, rec.OriginalModelPtr).Clut;

    byte[] patched;
    try { patched = TextureImporter.CopySpeciesPalette(dstBytes, File.ReadAllBytes(srcRef.Path), clut); }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
    File.WriteAllBytes(dstRef.Path, patched);

    var (clX, clY) = TextureImporter.ClutOrigin(clut);
    Console.WriteLine($"ST{dstCode:X3}: {species} palette (CLUT 0x{clut:X4} @ VRAM {clX},{clY}, 512 B) replaced with ST{srcCode:X3}'s");
    Console.WriteLine($"wrote   : {dstRef.Path}");
    Console.WriteLine($"backup  : {backupPath}");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine("verify  : re-enter the room in-game — the species renders in the donor room's colours; nothing else changes.");
    return 0;
}

// Repoint a stage's enemy-set record to another stage's, patching DINO.exe in place (backed up once
// to .dinorand_backup so --restore undoes it). spec is "<targetStage>:<donorStage>" (decimal or hex,
// e.g. 2:1 = give every stage-2 room the stage-1 basic-raptor set). Stage-scoped. CE is the validator.
// Standalone defect-B fix: extract the canonical cat8 hit/death descriptors from st605 and patch the EXE
// cave + descriptor tables (GameInstaller.PatchExeCat8HitDescriptors). Idempotent; reversed by --restore.
static int FixHitDeath(string installDir)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    // The canonical cat8 host is st605 (st603/st612 are too small to hold the high-band death descriptors).
    var st605 = Directory.EnumerateFiles(dataDir, "*.dat")
        .FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), "st605", StringComparison.OrdinalIgnoreCase));
    if (st605 is null)
    {
        Console.Error.WriteLine($"error: st605.dat (the canonical cat8 host) not found in {dataDir}; cannot extract the descriptors.");
        return 1;
    }

    byte[] hostRdt;
    try { hostRdt = RoomFile.ReadFromFile(6, 5, st605).RdtBuffer; }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: could not read st605 RDT ({st605}): {ex.Message}");
        return 1;
    }

    ExePatchResult res;
    try { res = GameInstaller.PatchExeCat8HitDescriptors(dataDir, hostRdt); }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
        Console.Error.WriteLine("hint : is the game still running? Windows locks the executable — close it and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: hit/death descriptor patch failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"patched : {res.ExePath}");
    Console.WriteLine($"backup  : {res.BackupPath}");
    foreach (var r in res.Repoints) Console.WriteLine($"desc    : {r}");
    Console.WriteLine("effect  : the cat8 Theri's hit/death reactions now resolve to canonical st605 descriptors "
        + "in every room (cat8-exclusive; no other enemy affected).");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : CLOSE then relaunch; shoot + kill the Theri and confirm no 0x4B0794/0x45dc0e crash.");
    return 0;
}

// Standalone enemy-SOUND fix (docs/reference/dc1/se/ENEMY-SOUND-SYSTEM.md): make a Theri-swapped room load the Theri
// (se\dino\j_*) SE set instead of the room's native raptor set, so the swapped enemy stops playing raptor
// sounds. EXE-only + idempotent (it does NOT touch the room .dat), so it can be applied to an existing swap
// install without re-importing geometry. Donor = the native cat8 host st605. The game must be CLOSED.
static int FixSound(string installDir, string roomArg)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    int code;
    try { code = Convert.ToInt32(roomArg.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16); }
    catch { Console.Error.WriteLine($"error: bad room code '{roomArg}' (e.g. 102)"); return 1; }
    int stage = code >> 8, room = code & 0xff;
    if (stage == 6)
    {
        Console.WriteLine($"note: stage 6 hosts the Theri natively (already loads j_* SE); nothing to fix for ST{code:X3}.");
        return 0;
    }

    ExePatchResult res;
    try { res = GameInstaller.PatchExeRoomEnemySe(dataDir, stage, room, donorStage: 6, donorRoom: 5); }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
        Console.Error.WriteLine("hint : is the game still running? Windows locks the executable — close it and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: enemy-sound patch failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"patched : {res.ExePath}");
    Console.WriteLine($"backup  : {res.BackupPath}");
    foreach (var r in res.Repoints) Console.WriteLine($"snd     : {r}");
    Console.WriteLine($"effect  : ST{code:X3} now loads the Therizinosaurus (se\\dino\\j_*) SE set; its footsteps/"
        + "roar/bite/death play Theri samples instead of raptor.");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : CLOSE then relaunch; enter ST{code:X3} and listen.");
    return 0;
}

// Shuffle the global BGM catalog in DINO.exe (the music randomizer; docs/reference/dc1/bgm/BGM-SYSTEM.md §4). EXE-only +
// idempotent for a fixed seed (it does NOT touch any room .dat), deterministic, reversed by --restore. The
// catalog is consumed at game init (a live edit does nothing — LIVE-VERIFIED), so the game must be CLOSED and
// then RELAUNCHED to hear the new mapping.
// Voice swap PREVIEW / dev harness (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.5 step 4). Installs one (default) or
// every (--all-banks) labelled Regina voice bank, overwritten with a CROSS-GAME donor's clip transcoded to
// DC1's format, via the loose-file backup contract (undo: --restore). Deliberately bypasses the gated
// VoiceRandomizer pass so the gate (VoiceManifestLayout.IsDecoded) stays closed; this is the listen-test
// before flipping it. A DC1-only donor would be Regina→Regina (inaudible), so a cross-game pack is required.
static int VoicePreview(string installDir, string? packsRoot, string? bankStem, string? donorActorArg,
    bool allBanks, string? seedArg, string? gameFilter)
{
    var dataDir = new DinoCrisis1().GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }
    if (packsRoot is null || !Directory.Exists(packsRoot))
    {
        Console.Error.WriteLine("error: --voice-preview needs --voice-packs <dir> pointing at the BioRand "
            + "datapacks (e.g. biorand/datapacks). A DC1-only donor would be Regina→Regina (inaudible).");
        return 1;
    }

    var crossGame = VoiceDataPack.LoadAll(packsRoot).Where(c => !c.IsNativeDc1).ToList();
    if (crossGame.Count == 0)
    {
        Console.Error.WriteLine($"error: no cross-game donor clips under {packsRoot} (expected "
            + "datapacks/<game>/data/voice/<actor>.<game>/…). Without a non-DC1 donor the swap is inaudible.");
        return 1;
    }

    // Optional source-game filter (e.g. --voice-game re2 = RE2 classic; re2r = the remake) so a donor that
    // appears in several games (e.g. Kendo in re2 + re2r) is pinned to one performance.
    if (gameFilter is { } gf)
    {
        var picked = crossGame.Where(c => string.Equals(c.Game, gf, StringComparison.OrdinalIgnoreCase)).ToList();
        if (picked.Count == 0)
        {
            var games = string.Join(", ", crossGame.Select(c => c.Game).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g));
            Console.Error.WriteLine($"error: --voice-game '{gf}' has no donor clips under {packsRoot}. Available games: {games}");
            return 1;
        }
        crossGame = picked;
    }

    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();
    var rng = seed.RngFor("voice-preview");

    // Pick the donor actor: pinned via --voice-actor, else a random cross-game actor.
    string donor;
    if (donorActorArg is { } da)
    {
        donor = da.ToLowerInvariant();
        if (!crossGame.Any(c => string.Equals(c.Actor, donor, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"error: --voice-actor '{da}' has no cross-game donor clips under {packsRoot}.");
            return 1;
        }
    }
    else
    {
        var actors = crossGame.Select(c => c.Actor).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.Ordinal).ToList();
        donor = actors[rng.Next(actors.Count)];
    }

    // Resolve targets + transcode plan via the shared preview service (one tested path with the App).
    IReadOnlyList<VoiceWrite> writes;
    try
    {
        writes = DinoRand.Randomizer.Voice.VoicePreview.Plan(
            crossGame, donor, allBanks, bankStem, rng, Dc1VoiceManifest.LoadDefault());
    }
    catch (VoicePreviewException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }

    // Transcode each donor into the bank's DC1 8-bit format and stage it under a temp mod dir.
    var modDir = Path.Combine(Path.GetTempPath(), "dinorand_voice_preview_" + seed);
    DinoRand.Randomizer.Voice.VoicePreview.Transcode(writes, modDir, new PcWavCodec());

    InstallResult ir;
    try { ir = GameInstaller.Install(dataDir, modDir, seed.ToString()); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: install failed: {ex.Message}");
        return 1;
    }

    var donorGames = string.Join("+", writes.Select(w => w.Donor.Game).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g));
    Console.WriteLine($"voice preview installed to {dataDir}");
    Console.WriteLine($"donor   : Regina → {donor} ({donorGames})");
    Console.WriteLine($"banks   : {ir.Overlaid} overlaid, {ir.BackedUp} backed up");
    foreach (var w in writes.Take(10))
        Console.WriteLine($"  {w.TargetBankPath}  ←  {Path.GetFileName(w.Donor.Path)}");
    if (writes.Count > 10) Console.WriteLine($"  … and {writes.Count - 10} more");
    Console.WriteLine($"seed    : {seed}");
    Console.WriteLine("verify  : LAUNCH the game and reach the cutscene(s) above — Regina's line should play in "
        + "the donor's voice. No exe edit, so no relaunch needed; --all-banks makes it easiest to hear.");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    return 0;
}

static int ShuffleBgm(string installDir, string? seedArg)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();

    ExePatchResult res;
    try { res = GameInstaller.PatchExeShuffleBgm(dataDir, seed.Value, seed.ToString()); }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
        Console.Error.WriteLine("hint : is the game still running? Windows locks the executable — close it and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: bgm shuffle failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"patched : {res.ExePath}");
    Console.WriteLine($"backup  : {res.BackupPath}");
    Console.WriteLine($"seed    : {seed}");
    foreach (var r in res.Repoints) Console.WriteLine($"bgm     : {r}");
    Console.WriteLine("effect  : every BGM id now streams a different file (within its stream/loop class); the "
        + "music still follows the scene but plays a new cue.");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine("verify  : CLOSE then RELAUNCH the game (the catalog is read at init) and listen as you "
        + "change rooms.");
    return 0;
}

static int ScramblePuzzleCodes(string installDir, string? seedArg)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();

    ExePatchResult res;
    try { res = GameInstaller.PatchExeSyncPuzzleCodes(dataDir, seed.Value, seed.ToString()); }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"error: could not write puzzle-code files: {ex.Message}");
        Console.Error.WriteLine("hint : is the game still running? Windows locks the executable — close it and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: puzzle-code scramble failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"patched : {res.ExePath}");
    Console.WriteLine($"backup  : {res.BackupPath}");
    Console.WriteLine($"seed    : {seed}");
    foreach (var r in res.Repoints) Console.WriteLine($"code    : {r}");
    Console.WriteLine("effect  : each keypad safe now accepts a new seed-derived code; the document that states "
        + "the code (where one exists) is rewritten to match.");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine("verify  : CLOSE then RELAUNCH the game (codes are read at load) and try a safe.");
    return 0;
}

static int ShuffleBoxes(string installDir, string? seedArg)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();

    ExePatchResult res;
    try { res = GameInstaller.PatchExeShuffleBoxes(dataDir, seed.Value, seed.ToString()); }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
        Console.Error.WriteLine("hint : is the game still running? Windows locks the executable — close it and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: box shuffle failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"patched : {res.ExePath}");
    Console.WriteLine($"backup  : {res.BackupPath}");
    Console.WriteLine($"seed    : {seed}");
    foreach (var r in res.Repoints) Console.WriteLine($"boxes   : {r}");
    Console.WriteLine("effect  : EXPERIMENTAL — each emergency box now holds a different (valid, "
        + "difficulty-appropriate) loadout; plug costs and box locations are unchanged.");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine("verify  : CLOSE then RELAUNCH the game (box contents are read at load) and open a box.");
    return 0;
}

static int RerollBoxes(string installDir, string? seedArg)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    var seed = seedArg is { } s ? Seed.Parse(s) : Seed.Random();

    ExePatchResult res;
    try { res = GameInstaller.PatchExeRerollBoxes(dataDir, seed.Value, seed.ToString()); }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
        Console.Error.WriteLine("hint : is the game still running? Windows locks the executable — close it and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: box reroll failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"patched : {res.ExePath}");
    Console.WriteLine($"backup  : {res.BackupPath}");
    Console.WriteLine($"seed    : {seed}");
    foreach (var r in res.Repoints) Console.WriteLine($"boxes   : {r}");
    Console.WriteLine("effect  : EXPERIMENTAL — each emergency box is rerolled from its difficulty's own box "
        + "loot pool (distinct from map drops); plug costs and box locations are unchanged.");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine("verify  : CLOSE then RELAUNCH the game (box contents are read at load) and open a box.");
    return 0;
}

static int SetStartingItems(string installDir, string? spec, string? weaponArg)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    // Custom supply list (optional): "id:count,…", ids hex 0x.. or decimal. Range/fit checked by the patcher.
    List<(int Id, int Count)>? items = null;
    if (spec is not null)
    {
        items = new List<(int, int)>();
        foreach (var tok in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = tok.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !TryParseInt(parts[0], out var id) || !TryParseInt(parts[1], out var count))
            {
                Console.Error.WriteLine($"error: --starting-items: bad token '{tok}' (expected id:count, e.g. 0x16:30)");
                return 1;
            }
            items.Add((id, count));
        }
    }

    // Starting weapon (optional): an id, or "none". Standalone (no room overlay), so 'none' can't place a
    // replacement in the world — warn that beatability needs the full `--install-to-data` flow.
    bool setWeapon = false; int? weaponId = null;
    if (weaponArg is not null)
    {
        setWeapon = true;
        if (weaponArg.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("error: --starting-weapon none (weaponless start) is not supported yet — the engine "
                + "re-equips a default Handgun via an undecoded path. Pick a weapon id (e.g. 0x01) instead.");
            return 1;
        }
        if (TryParseInt(weaponArg, out var wid)) weaponId = wid;
        else { Console.Error.WriteLine($"error: --starting-weapon: bad value '{weaponArg}' (id like 0x01)"); return 1; }
    }

    var plan = new StartingInventoryPlan(CustomSupply: items, SetWeapon: setWeapon, WeaponId: weaponId);

    ExePatchResult res;
    try { res = GameInstaller.PatchExeStartingInventory(dataDir, plan, 0); }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
        Console.Error.WriteLine("hint : is the game still running? Windows locks the executable — close it and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: set starting inventory failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"patched : {res.ExePath}");
    Console.WriteLine($"backup  : {res.BackupPath}");
    foreach (var r in res.Repoints) Console.WriteLine($"items   : {r}");
    Console.WriteLine("effect  : EXPERIMENTAL — the new-game supply kit / starting weapon are set as requested. "
        + "Unused supply slots are emptied.");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine("verify  : CLOSE then RELAUNCH the game and start a NEW GAME.");
    return 0;

    static bool TryParseInt(string t, out int v) =>
        t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out v)
            : int.TryParse(t, out v);
}

static int PatchExeOp(string installDir, string spec)
{
    var game = new DinoCrisis1();
    var dataDir = game.GetDataDir(installDir);
    if (dataDir is null)
    {
        Console.Error.WriteLine($"error: could not locate a Data folder under {installDir}");
        return 1;
    }

    var parts = spec.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length != 2 || !TryParseStage(parts[0], out int target) || !TryParseStage(parts[1], out int donor))
    {
        Console.Error.WriteLine($"error: --exe-patch expects <targetStage>:<donorStage> (e.g. 2:1), got '{spec}'");
        return 1;
    }

    var exePath = GameInstaller.ExePath(dataDir);
    if (!File.Exists(exePath))
    {
        Console.Error.WriteLine($"error: {GameInstaller.ExeName} not found beside Data ({exePath})");
        return 1;
    }

    ExePatchResult res;
    try { res = GameInstaller.PatchExe(dataDir, new[] { new ExeRepoint(target, donor) }); }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
        Console.Error.WriteLine("hint : is the game still running? Windows locks the executable — close it and retry.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: exe patch failed: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"patched : {res.ExePath}");
    Console.WriteLine($"backup  : {res.BackupPath}");
    foreach (var r in res.Repoints) Console.WriteLine($"repoint : {r}");
    Console.WriteLine($"effect  : every stage-{target} room now loads stage-{donor}'s enemy set (stage-scoped).");
    Console.WriteLine($"undo    : dinorand --install \"{installDir}\" --restore");
    Console.WriteLine($"verify  : CLOSE then relaunch the game; in a stage-{target} room confirm [0x6DE990] "
        + "rebuilds non-null and enemies spawn, animate and die without crashing.");
    return 0;

    static bool TryParseStage(string s, out int stage)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out stage)
            : int.TryParse(s, out stage);
    }
}

static bool TryParseSpecies(string name, out DinoSpecies species)
{
    species = name.ToLowerInvariant().Replace("-", "").Replace("_", "") switch
    {
        "raptorheavy" or "heavy" or "heavyraptor" => DinoSpecies.RaptorHeavy,
        "pteranodon" or "flyer" or "ptera" => DinoSpecies.Pteranodon,
        // "largeground"/"large"/"ground" were the 20-bone class now known to be the Tyrannosaurus (cont.23).
        "tyrannosaurus" or "trex" or "rex" or "tyrant" or "largeground" or "large" or "ground"
            => DinoSpecies.Tyrannosaurus,
        "swarm" => DinoSpecies.Swarm,
        "velociraptor" or "raptor" => DinoSpecies.Velociraptor,
        "therizinosaurus" or "theri" or "theriz" or "scythe" => DinoSpecies.Therizinosaurus,
        _ => DinoSpecies.Unknown,
    };
    return species != DinoSpecies.Unknown;
}

// Read-only diagnostic: reports how many rooms parse as packages, how many script
// segments walk cleanly with the current (incomplete) opcode table, and the item-id
// spread. Drives opcode-table discovery and verifies the non-destructive guarantee.
static int AnalyzeScripts(string installDir)
{
    var game = new DinoCrisis1();
    var refs = game.EnumerateRooms(installDir).ToList();
    if (refs.Count == 0)
    {
        Console.Error.WriteLine($"no room files found under {installDir}");
        return 1;
    }

    int pkgOk = 0, cleanWalk = 0, byteExact = 0, totalItems = 0, totalDoors = 0, lockedDoors = 0;
    var lastEntryTypes = new SortedDictionary<GianEntryType, int>();
    var itemIds = new SortedDictionary<int, int>();

    foreach (var r in refs)
    {
        var bytes = File.ReadAllBytes(r.Path);
        var room = RoomFile.Read(r.Stage, r.Room, bytes);

        if (room.Package is { } pkg)
        {
            pkgOk++;
            if (pkg.RoomDataEntry is { } e)
                lastEntryTypes[e.Type] = lastEntryTypes.GetValueOrDefault(e.Type) + 1;
        }
        if (room.ParsedCleanly) cleanWalk++;
        if (room.Write().AsSpan().SequenceEqual(bytes)) byteExact++;

        foreach (var it in room.Items)
        {
            totalItems++;
            itemIds[it.ItemId] = itemIds.GetValueOrDefault(it.ItemId) + 1;
        }
        foreach (var d in room.Doors)
        {
            totalDoors++;
            if (d.LockId != 0) lockedDoors++;
        }
    }

    Console.WriteLine($"rooms scanned       : {refs.Count}");
    Console.WriteLine($"parsed as package   : {pkgOk}/{refs.Count}");
    Console.WriteLine($"script walked clean : {cleanWalk}/{refs.Count}  (opcode table is incomplete — see DcOpcodes)");
    Console.WriteLine($"byte-exact write    : {byteExact}/{refs.Count}  (non-destructive guarantee)");
    Console.WriteLine($"item records found  : {totalItems}");
    Console.WriteLine($"door records found  : {totalDoors}  ({lockedDoors} gated)");
    Console.WriteLine($"last-entry types    : {string.Join(", ", lastEntryTypes.Select(kv => $"{kv.Key}={kv.Value}"))}");
    if (itemIds.Count > 0)
        Console.WriteLine($"item id histogram   : {string.Join(", ", itemIds.Select(kv => $"0x{kv.Key:X2}×{kv.Value}"))}");
    return 0;
}

// ---------------------------------------------------------------------------------------------
// Archipelago runtime client, DC1 v1 (docs/decisions/cross/AP-CLIENT-PLAN.md).
// Flow: connect/login → slot_data placements → patch rooms (ApPlacementInstaller) → overlay
// install (GameInstaller) → attach DINO.exe → 4 Hz poll loop until Ctrl-C.
// ---------------------------------------------------------------------------------------------
// The flow itself lives in Dc1ApRunner (DinoRand.Randomizer/Ap) so this command and the Avalonia
// connect tab run ONE implementation; this binder only supplies the console sinks and Ctrl-C.
static int ApConnectDc1(string hostPort, string? slot, string? password, string? install, string? outDirArg)
{
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    return Dc1ApRunner.Run(hostPort, slot, password, install, outDirArg,
        Console.WriteLine, Console.Error.WriteLine, stop.Token);
}
