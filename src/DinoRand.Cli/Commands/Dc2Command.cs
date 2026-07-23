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

internal sealed partial class CliApplication
{
    private int? TryRunDc2Command()
    {
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

        return null;
    }

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

        // Compatibility flag name: --dc2-mosa-tail-to-bite now cancels the user-labelled tail-looking
        // selector-6 missed-grab continuation in non-native land rooms. Native aquatic rooms
        // (ST700/702/703/704) replay the displaced instructions; successful-contact substates are untouched.
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

        // Mosasaurus missed-grab continuation cancellation exe lever — same in-place, .bak-protected contract.
        // Auto-applied whenever the run can inject a Mosasaurus (water-level swaps / fixed-mosa pin), or forced
        // on by the compatibility flag --dc2-mosa-tail-to-bite. Harmless no-op when no Mosasaurus is placed.
        if (Dc2MosaTailRedirectInstaller.WantedFor(dc2Config))
        {
            try
            {
                var outcome = Dc2MosaTailRedirectInstaller.Apply(installDir, restore: false, Console.WriteLine);
                Console.WriteLine($"mosa-missed-grab-cancel exe patch: {outcome}");
            }
            catch (IOException)
            {
                Console.Error.WriteLine("mosa-missed-grab-cancel exe patch NOT applied: Dino2.exe is locked — close the game and re-run.");
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
}
