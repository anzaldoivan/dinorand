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
    private int? TryRunDc1Command(string install)
    {
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

        return null;
    }

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
}
