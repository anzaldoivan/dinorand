namespace DinoRand.Randomizer.Install;

/// <summary>
/// Coordinates the DC1 room overlay and its optional executable patches while preserving the
/// installer's established ordering and failure boundaries.
/// </summary>
internal static class RandomizationInstallCoordinator
{
    public static RandomizationInstallResult? InstallDc1(
        string dataDir,
        string modDir,
        Seed seed,
        RandomizerConfig config,
        Func<StartingInventoryPlan?> createStartingInventoryPlan,
        Action<string> output,
        Action<IOException> overlayFailure)
    {
        InstallResult installResult;
        try
        {
            installResult = GameInstaller.Install(dataDir, modDir, seed.ToString());
        }
        catch (IOException ex)
        {
            overlayFailure(ex);
            return null;
        }

        var events = new List<RandomizationInstallEvent>();
        void Emit(string message)
        {
            events.Add(new RandomizationInstallEvent(message));
            output(message);
        }

        Emit($"installed to {dataDir}: {installResult.Overlaid} room files overlaid, "
            + $"{installResult.BackedUp} originals backed up");

        // Data-only synthetic installs (and room-only tooling flows) have no executable to patch. Keep
        // the room overlay usable there; a real GOG install still receives the automatic fix below.
        if (File.Exists(GameInstaller.ExePath(dataDir)))
        {
            var itemPickupFix = GameInstaller.PatchExeItemPickupCancelFix(dataDir, seed.ToString());
            foreach (var repoint in itemPickupFix.Repoints)
                Emit($"item pickup: {repoint}");
        }

        if (config.RandomizeEnemies && config.CrossRoomEnemySpecies)
            Emit("exotic enemies: any queued EXE patches were applied; CLOSE/relaunch + CE-verify the swaps.");

        var inventoryPlan = createStartingInventoryPlan();
        if (inventoryPlan is not null)
        {
            var inventory = GameInstaller.PatchExeStartingInventory(
                dataDir, inventoryPlan, seed.Value, seed.ToString());
            foreach (var repoint in inventory.Repoints)
                Emit($"inventory: {repoint}");
            Emit("inventory: EXPERIMENTAL — seen on the NEXT NEW GAME after relaunch "
                + "(a removed start weapon is placed in the early world by the item pass).");
        }

        if (config.Dc1DoorSkip)
        {
            var doorSkip = GameInstaller.PatchExeDoorSkip(dataDir, seed.ToString());
            foreach (var repoint in doorSkip.Repoints)
                Emit($"door skip: {repoint}");
            Emit("door skip: EXPERIMENTAL — door transitions are near-instant on the next launch. CLOSE the game first.");
        }

        if (config.Dc1FastForwardCutscenes)
        {
            var fastForward = GameInstaller.PatchExeFastForwardCutscenes(dataDir, seed.ToString());
            foreach (var repoint in fastForward.Repoints)
                Emit($"fast-forward cutscenes: {repoint}");
            Emit("fast-forward cutscenes: EXPERIMENTAL / CRASH RISK — cutscenes are sped up on the next launch. CLOSE the game first.");
        }

        Emit($"backup: {installResult.BackupDir}  (run with --restore to undo)");
        return new RandomizationInstallResult(installResult, events);
    }
}
