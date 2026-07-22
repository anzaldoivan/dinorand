using System.Text.Json;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Install;

internal static class OverlayInstaller
{
    private sealed record PendingOverlay(
        string Key, string TargetPath, string BackupPath,
        byte[] LiveBefore, byte[] Pristine, byte[] Replacement, bool BackupExists);

    private sealed record PendingExe(
        string TargetPath, string BackupPath, byte[] LiveBefore, byte[] Pristine,
        ExePatchInstaller.ComposedExePatch Composed, bool BackupExists);

    private const string BackupDirName = GameInstaller.BackupDirName;
    private const string ExeName = GameInstaller.ExeName;
    private const string LooseBackupSubdir = GameInstaller.LooseBackupSubdir;
    private const string ManifestName = "manifest.json";

    private static readonly string[] LooseSubtrees =
    {
        "Sound/VOICE/", "Sound/BGM/", "Speech/", "Data/t_image.imd",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // Deterministic test seam for the bounded rollback path. Thread-local keeps parallel installer tests isolated.
    [ThreadStatic]
    internal static Action<int>? CommitStepProbe;

    private static string GameRoot(string dataDir) => Path.GetDirectoryName(Path.GetFullPath(
        dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;

    private static void EnsureNotDrmProtected(string dataDir) => GameInstaller.EnsureNotDrmProtected(dataDir);
    private static InstallManifest? ReadManifest(string dataDir) => BackupManifestStore.ReadManifest(dataDir);
    private static string HashFile(string path) => BackupManifestStore.HashFile(path);
    private static string ExePath(string dataDir) => GameInstaller.ExePath(dataDir);

    internal static InstallResult Install(string dataDir, string modDir, string? seed = null,
                                        IReadOnlyCollection<string>? onlyFiles = null)
    {
        if (!Directory.Exists(dataDir))
            throw new DirectoryNotFoundException($"Data folder not found: {dataDir}");
        if (!Directory.Exists(modDir))
            throw new DirectoryNotFoundException($"Mod folder not found: {modDir}");
        EnsureNotDrmProtected(dataDir); // refuse a DRM-wrapped (e.g. Steam/Enigma) game before any overlay

        var allowNames = onlyFiles is null
            ? null
            : new HashSet<string>(onlyFiles.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

        var backupDir = Path.Combine(dataDir, BackupDirName);

        // Map of the real Data files by name, matched case-insensitively: the randomizer
        // may emit mixed-case names (e.g. St502.dat) while the originals are lowercase
        // (st502.dat). We always overwrite using the original's actual name.
        var dataFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Directory.EnumerateFiles(dataDir))
            dataFiles.TryAdd(Path.GetFileName(p), p);

        // Carry forward the pristine hashes recorded by any earlier install — a file is backed up (and
        // hashed) exactly once, so re-rolls must preserve, not recompute, the original's hash.
        var existing = ReadManifest(dataDir);
        var hashes = new Dictionary<string, string>(
            existing?.OriginalHashes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

        var pending = new List<PendingOverlay>();

        PendingOverlay PrepareOverlay(string key, string targetPath, string modPath, string backupPath)
        {
            bool backupExists = File.Exists(backupPath);
            byte[] pristine = backupExists
                ? File.ReadAllBytes(backupPath)
                : BackupManifestStore.ReadPristineBytes(targetPath);
            if (backupExists && hashes.TryGetValue(key, out var expected)
                && !string.Equals(BackupManifestStore.HashBytes(pristine), expected,
                    StringComparison.OrdinalIgnoreCase))
                throw new BackupIntegrityException(key, backupPath);
            return new PendingOverlay(key, targetPath, backupPath,
                File.ReadAllBytes(targetPath), pristine, File.ReadAllBytes(modPath), backupExists);
        }

        // No "*.dat" glob: it is case-sensitive on Linux/WSL and would skip DC2's uppercase ST*.DAT
        // (the St502 case-glob bug class — dc1-st502-case-glob-bug).
        foreach (var modPath in Directory.EnumerateFiles(modDir)
                     .Where(p => p.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)))
        {
            var modName = Path.GetFileName(modPath);
            // Scope to the current run's recorded output when an allow-list is supplied: a stale/foreign
            // *.dat left in the working dir must never be installed (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md).
            if (allowNames is not null && !allowNames.Contains(modName))
                continue;
            // Only overlay files that correspond to a real original room file.
            if (!dataFiles.TryGetValue(modName, out var targetPath))
                continue;
            var targetName = Path.GetFileName(targetPath);

            var overlay = PrepareOverlay(targetName, targetPath, modPath,
                Path.Combine(backupDir, targetName));

            // Container-format guard: a room's Gian entry stride (DC1 16-byte vs DC2 32-byte) must never
            // change across an overlay. A flipped stride means the mod file was written in the wrong
            // container format (e.g. a DC1 16-byte rebuild of a DC2 room, from a stale/reused Generate
            // output dir), which the engine misreads into an out-of-range GPU-resource index → a hard
            // crash on room load (docs/decisions/dc2/crash-rcas/DC2-ROOM-CONTAINER-STRIDE-CRASH-RCA.md). The pristine backup is
            // the oracle; skip files that are not Gian containers (nothing to compare).
            var pristinePkg = GianPackage.TryParse(overlay.Pristine);
            if (pristinePkg is not null)
            {
                var modPkg = GianPackage.TryParse(overlay.Replacement);
                if (modPkg is null || modPkg.IsDc2 != pristinePkg.IsDc2)
                    throw new ContainerFormatMismatchException(targetName, pristinePkg.IsDc2);
            }
            pending.Add(overlay);
        }

        // Loose-file overlay: voice banks etc. under known subdirs of modDir → the game ROOT (parent of
        // Data), with root-relative backups under <backup>\loose. Hashes merge into the same manifest, so
        // Restore validates and reverses them exactly like room files (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.3).
        var gameRoot = GameRoot(dataDir);
        var looseBackupRoot = Path.Combine(backupDir, LooseBackupSubdir);
        foreach (var modPath in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(modDir, modPath).Replace('\\', '/');
            if (!LooseSubtrees.Any(s => rel.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                continue;
            // Same allow-list scoping as the room overlay: a stale Speech/voice file left in a reused
            // working dir is never installed when the run recorded its output.
            if (allowNames is not null && !allowNames.Contains(Path.GetFileName(rel)))
                continue;

            var target = Path.Combine(gameRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target))
                continue; // only overlay a real existing bank; never create a stray file

            pending.Add(PrepareOverlay(rel, target, modPath,
                Path.Combine(looseBackupRoot, rel.Replace('/', Path.DirectorySeparatorChar))));
        }

        // The EXE sidecar is part of the same preflight. Compose every compatible request on one pristine
        // buffer now, before any target overlay write; invalid versions/kinds/donors/addresses/build guards
        // therefore fail without changing rooms, loose files, the executable or the manifest.
        var plan = ExePatchPlan.TryRead(modDir);
        PendingExe? pendingExe = null;
        if (plan is not null)
        {
            ExePatchInstaller.ValidatePlanVersion(plan);
            if (plan.Requests.Count > 0)
            {
                var exePath = ExePatchInstaller.ResolveExeForPatch(dataDir);
                var exeBackup = Path.Combine(backupDir, ExeName);
                bool backupExists = File.Exists(exeBackup);
                byte[] pristine = backupExists
                    ? File.ReadAllBytes(exeBackup)
                    : BackupManifestStore.ReadPristineBytes(exePath);
                if (backupExists && hashes.TryGetValue(ExeName, out var expected)
                    && !string.Equals(BackupManifestStore.HashBytes(pristine), expected,
                        StringComparison.OrdinalIgnoreCase))
                    throw new BackupIntegrityException(ExeName, exeBackup);
                var composed = ExePatchInstaller.ComposeExePatchPlan(pristine, plan, modDir, dataDir);
                pendingExe = new PendingExe(exePath, exeBackup, File.ReadAllBytes(exePath),
                    pristine, composed, backupExists);
            }
        }

        // Commit the already-validated plan. The manifest is deliberately last: it never claims success
        // before every overlay and the single composed executable result have landed. Portable filesystems
        // cannot atomically replace this whole set, so retain bounded in-memory live snapshots and roll back
        // every target plus the prior manifest on an unexpected I/O failure.
        var manifestPath = Path.Combine(backupDir, ManifestName);
        byte[]? manifestBefore = File.Exists(manifestPath) ? File.ReadAllBytes(manifestPath) : null;
        var createdBackups = new List<string>();
        try
        {
            Directory.CreateDirectory(backupDir);
            foreach (var overlay in pending)
            {
                if (!overlay.BackupExists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(overlay.BackupPath)!);
                    File.WriteAllBytes(overlay.BackupPath, overlay.Pristine);
                    createdBackups.Add(overlay.BackupPath);
                }
                hashes[overlay.Key] = BackupManifestStore.HashBytes(overlay.Pristine);
            }
            if (pendingExe is not null)
            {
                if (!pendingExe.BackupExists)
                {
                    File.WriteAllBytes(pendingExe.BackupPath, pendingExe.Pristine);
                    createdBackups.Add(pendingExe.BackupPath);
                }
                hashes[ExeName] = BackupManifestStore.HashBytes(pendingExe.Pristine);
            }

            int commitStep = 0;
            foreach (var overlay in pending)
            {
                File.WriteAllBytes(overlay.TargetPath, overlay.Replacement);
                CommitStepProbe?.Invoke(++commitStep);
            }
            if (pendingExe is not null)
            {
                File.WriteAllBytes(pendingExe.TargetPath, pendingExe.Composed.Bytes);
                CommitStepProbe?.Invoke(++commitStep);
            }

            var files = pending.Select(p => p.Key).ToList();
            var manifest = new InstallManifest(seed, DateTime.UtcNow.ToString("o"), files,
                ExePatched: pendingExe is not null,
                ExeRepoints: pendingExe?.Composed.Repoints,
                OriginalHashes: hashes, Applied: true);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts));
            CommitStepProbe?.Invoke(++commitStep);

            int backedUp = pending.Count(p => !p.BackupExists);
            return new InstallResult(backedUp, pending.Count, backupDir);
        }
        catch
        {
            foreach (var overlay in pending)
                File.WriteAllBytes(overlay.TargetPath, overlay.LiveBefore);
            if (pendingExe is not null)
                File.WriteAllBytes(pendingExe.TargetPath, pendingExe.LiveBefore);
            if (manifestBefore is null)
            {
                if (File.Exists(manifestPath)) File.Delete(manifestPath);
            }
            else
                File.WriteAllBytes(manifestPath, manifestBefore);
            foreach (var backup in createdBackups)
                if (File.Exists(backup)) File.Delete(backup);
            throw;
        }
    }

    internal static RestoreResult Restore(string dataDir)
    {
        var backupDir = Path.Combine(dataDir, BackupDirName);
        if (!Directory.Exists(backupDir))
            return new RestoreResult(0);

        var manifest = ReadManifest(dataDir);
        var hashes = manifest?.OriginalHashes;

        var toRestore = Directory.EnumerateFiles(backupDir)
            .Where(p => !string.Equals(Path.GetFileName(p), ManifestName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Loose-file backups (voice banks etc., §12.3) live under <backup>\loose, mirroring the game-root
        // subtree, and are keyed in the manifest by their forward-slash relative path.
        var looseBackupRoot = Path.Combine(backupDir, LooseBackupSubdir);
        var looseBackups = Directory.Exists(looseBackupRoot)
            ? Directory.EnumerateFiles(looseBackupRoot, "*", SearchOption.AllDirectories).ToList()
            : new List<string>();

        // Pass 1: validate every backup we have a recorded hash for, BEFORE touching the live game. A
        // mismatch aborts the whole restore so we never write a half-restored / corrupted set over the game.
        if (hashes is not null)
        {
            foreach (var backupPath in toRestore)
            {
                var name = Path.GetFileName(backupPath);
                if (hashes.TryGetValue(name, out var expected) &&
                    !string.Equals(HashFile(backupPath), expected, StringComparison.OrdinalIgnoreCase))
                    throw new BackupIntegrityException(name, backupPath);
            }
            foreach (var backupPath in looseBackups)
            {
                var rel = Path.GetRelativePath(looseBackupRoot, backupPath).Replace('\\', '/');
                if (hashes.TryGetValue(rel, out var expected) &&
                    !string.Equals(HashFile(backupPath), expected, StringComparison.OrdinalIgnoreCase))
                    throw new BackupIntegrityException(rel, backupPath);
            }
        }

        // Pass 2: copy the validated originals back.
        int restored = 0;
        foreach (var backupPath in toRestore)
        {
            var name = Path.GetFileName(backupPath);
            // The exe belongs in the game root (beside Data\), every other backed-up file is a
            // room .dat that belongs back in Data\ itself.
            var target = string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase)
                ? ExePath(dataDir)
                : Path.Combine(dataDir, name);
            File.Copy(backupPath, target, overwrite: true);
            restored++;
        }

        // Pass 2 (loose): copy validated voice-bank originals back to their game-root subtree.
        if (looseBackups.Count > 0)
        {
            var gameRoot = GameRoot(dataDir);
            foreach (var backupPath in looseBackups)
            {
                var rel = Path.GetRelativePath(looseBackupRoot, backupPath);
                var target = Path.Combine(gameRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backupPath, target, overwrite: true);
                restored++;
            }
        }

        // Keep the backup for reuse; just record that the mod is no longer applied.
        if (manifest is not null)
            File.WriteAllText(Path.Combine(backupDir, ManifestName),
                JsonSerializer.Serialize(manifest with { Applied = false }, JsonOpts));

        return new RestoreResult(restored, ReapplyDc1VertexLiftIfDdrawLifted(dataDir));
    }

    internal static bool ReapplyDc1VertexLiftIfDdrawLifted(string dataDir)
    {
        var exePath = ExePath(dataDir);
        var dllPath = Path.Combine(GameRoot(dataDir), "ddraw.dll");
        if (!File.Exists(exePath) || !File.Exists(dllPath))
            return false;
        if (!DdrawPatcher.IsRebirthVertexTablesExpanded(File.ReadAllBytes(dllPath)))
            return false;
        var exe = File.ReadAllBytes(exePath);
        if (ExePatcher.IsDc1CharacterVertexTablesExpanded(exe))
            return false;
        byte[] lifted;
        try
        {
            lifted = ExePatcher.ExpandDc1CharacterVertexTables(exe); // validates every byte first
        }
        catch (InvalidOperationException)
        {
            return false; // not the liftable stock build — restore it untouched rather than fail
        }
        File.WriteAllBytes(exePath, lifted);
        return true;
    }
}
