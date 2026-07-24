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
using DinoRand.Randomizer.Spoiler;
using DinoRand.Randomizer.Voice;

internal sealed partial class CliApplication
{
    private int RunRandomizeCommand(string install)
    {
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
            // Default OFF: explicitly opt in to the crash-risk donor-model importer. Keep the legacy
            // --no-pickup-ground-models spelling as a hard off switch for existing scripts.
            ImportPickupModels = argv.Contains("--pickup-ground-models")
                              && !argv.Contains("--no-pickup-ground-models"),
            // DC1 enemy randomization includes foreign species (cat8 Theri + grounded RaptorHeavy) by default and
            // queues the EXE patches they need. --no-enemies disables both passes; the old --exotic-enemies spelling
            // remains a harmless compatibility no-op. With --install-to-data, CLOSE the game before DINO.exe patches.
            CrossRoomEnemySpecies = !argv.Contains("--no-enemies"),
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
        // --no-spoiler suppresses the per-seed spoiler file only (docs/decisions/cross/SPOILER-LOG-PLAN.md); every game file is
        // byte-identical either way (the spoiler is a pure post-write projection).
        bool emitSpoiler = !argv.Contains("--no-spoiler");
        var result = runner.Run(install, outDir, seed, config, emitSpoiler);
        var spoilerFileName = SpoilerLogBuilder.FileNameFor(SeedString.Encode(seed, config));

        Console.WriteLine($"seed {seed} → {result.RoomsWritten} room files, {result.RoomCount} rooms");
        Console.WriteLine($"output: {Path.GetFullPath(result.OutputDir)}");
        if (emitSpoiler)
            Console.WriteLine($"spoiler: {Path.GetFullPath(Path.Combine(result.OutputDir, spoilerFileName))} "
                + "(debug block on top is spoiler-free; --no-spoiler to skip)");

        if (argv.Contains("--install-to-data"))
        {
            var dataDir = new DinoCrisis1().GetDataDir(install);
            if (dataDir is null)
            {
                Console.Error.WriteLine($"error: could not locate a Data folder under {install}");
                return 1;
            }
            var installResult = RandomizationInstallCoordinator.InstallDc1(
                dataDir,
                outDir,
                seed,
                config,
                createStartingInventoryPlan: () =>
                {
                    // Combined starting-inventory EXE patch (weapon + supply), composed into one operation so the halves
                    // don't clobber each other. CUSTOM supply (--starting-items) wins over RANDOM (--random-inventory); the
                    // weapon grant follows config.StartingWeapons (already fed to the item pass above). Additive, seeded,
                    // reversed by --restore.
                    var customSupply = GetOpt("--starting-items") is { } itemsSpecInstall
                        ? ParseStartingItems(itemsSpecInstall)
                        : null;
                    bool weaponOverride = config.StartingWeapons is not null;
                    return config.RandomizeStartingInventory || customSupply is not null || weaponOverride
                        ? new StartingInventoryPlan(
                            RandomizeSupply: config.RandomizeStartingInventory && customSupply is null,
                            CustomSupply: customSupply,
                            SetWeapon: weaponOverride,
                            WeaponId: config.StartingWeapons is { Count: >= 1 } sws ? sws.First() : (int?)null)
                        : null;
                },
                output: Console.WriteLine,
                overlayFailure: ex =>
                {
                    // Only the exe-patch step (e.g. enemy randomization's cat-slot patch) writes DINO.exe, which Windows locks
                    // while the game runs; the room overlay itself does not. Surface the close-the-game hint.
                    Console.Error.WriteLine($"error: could not write {GameInstaller.ExeName}: {ex.Message}");
                    Console.Error.WriteLine("hint : DC1 enemy randomization may patch DINO.exe — CLOSE the game and re-run (the room overlay is idempotent).");
                });
            if (installResult is null)
                return 1;
        }
        return 0;
    }
}
