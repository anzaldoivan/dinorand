using System.Security.Cryptography;
using System.Text.Json;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Install;

/// <summary>
/// Non-destructive backup-and-swap overlay installer for Dino Crisis 1.
///
/// The game (DINO.exe behind the Classic REbirth wrapper) has no mod/override path: it
/// opens rooms via hardcoded <c>DATA/STNXX.DAT</c> relative to its working dir, and the
/// wrapper only intercepts its own FMV assets (see <c>docs/reference/dc1/install/LOADER-OVERRIDE.md</c>). So
/// the only way to load randomized rooms is to overlay them into the real <c>Data\</c>
/// folder — done here non-destructively:
///
/// <list type="bullet">
///   <item>Before overwriting a room file, its pristine original is copied into
///   <c>&lt;dataDir&gt;\.dinorand_backup\</c> — but only if a backup of that file doesn't
///   already exist, so re-rolling a new seed never captures an already-randomized file as
///   "pristine".</item>
///   <item><see cref="Restore"/> copies every backed-up file back and deletes the backup
///   folder, leaving <c>Data\</c> byte-identical to the original.</item>
/// </list>
///
/// Only the room files the randomizer emits are touched; the rest of <c>Data\</c> is left
/// alone (the runner writes only <c>st*.dat</c> room files plus <c>log_dinorand.txt</c> /
/// <c>map.dgml</c>, which are skipped here).
/// </summary>
public static class GameInstaller
{
    /// <summary>Backup sub-folder created inside the game's <c>Data\</c> directory.</summary>
    public const string BackupDirName = ".dinorand_backup";

    /// <summary>Tools-style pristine-sibling suffix (<c>&lt;file&gt;.dinorand-bak</c>) written by the
    /// CLI/RE single-file edit flows. <see cref="CapturePristine"/> validates captures against it.</summary>
    public const string SiblingBackupSuffix = ".dinorand-bak";

    /// <summary>The game executable, which lives beside <c>Data\</c> in the game root (the language
    /// dir for the GOG/Steam multi-language layout). The cross-species patch lever lives here, so it
    /// joins the backup/restore surface (docs/decisions/dc1/exe/EXE-PATCH-PER-ROOM-PLAN.md, Decision 3).</summary>
    public const string ExeName = "DINO.exe";

    private const string ManifestName = "manifest.json";

    /// <summary>
    /// Backup sub-folder (under <see cref="BackupDirName"/>) that mirrors the game-root subtree for
    /// <b>loose</b> install files — non-room files a pass emits in subdirectories (voice banks under
    /// <c>Sound\VOICE\</c>; docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.3). Pristine originals are kept here under
    /// their game-root-relative path so <see cref="Restore"/> reverses them to the right place.
    /// </summary>
    public const string LooseBackupSubdir = "loose";

    /// <summary>
    /// Mod-dir subtrees overlaid as loose files into the game <b>root</b> (the parent of <c>Data\</c>),
    /// keyed in the manifest by their forward-slash relative path. Scoped to known audio paths (voice banks,
    /// speech, BGM slots) so an overlay can never clobber an arbitrary game file.
    /// </summary>
    private static readonly string[] LooseSubtrees = { "Sound/VOICE/", "Sound/BGM/", "Speech/" };

    /// <summary>The game root (parent of <c>Data\</c>) — where the exe and <c>Sound\VOICE\</c> live.</summary>
    private static string GameRoot(string dataDir) => Path.GetDirectoryName(Path.GetFullPath(
        dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Overlay the randomized room files from <paramref name="modDir"/> onto
    /// <paramref name="dataDir"/>, backing up pristine originals first. Re-running with a
    /// different seed re-overlays without clobbering the existing pristine backup.
    /// </summary>
    /// <param name="onlyFiles">When non-null, overlay <b>only</b> the room files whose names are in this
    /// set (the current run's recorded output, e.g. <see cref="Dc2.Dc2OutputDirSink.WrittenFiles"/>) —
    /// so a stale or foreign <c>*.dat</c> left in <paramref name="modDir"/> is never installed
    /// (docs/decisions/dc2/install/DC2-INSTALL-INTEGRITY-PLAN.md). Null preserves the legacy overlay-every-matching-<c>.dat</c>
    /// behavior (DC1 CLI, voice). Matched case-insensitively on file name.</param>
    public static InstallResult Install(string dataDir, string modDir, string? seed = null,
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
        Directory.CreateDirectory(backupDir);

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

        int backedUp = 0, overlaid = 0;
        var files = new List<string>();

        foreach (var modPath in Directory.EnumerateFiles(modDir, "*.dat"))
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

            var backupPath = Path.Combine(backupDir, targetName);
            if (!File.Exists(backupPath))
            {
                CapturePristine(targetPath, backupPath); // capture pristine original once (sibling-validated)
                hashes[targetName] = HashFile(backupPath);
                backedUp++;
            }
            else if (hashes.TryGetValue(targetName, out var expected))
            {
                // A backup already exists and we know its pristine hash: validate it is still pristine
                // before overlaying. A mismatch means the backup is not the true original (tampered, or a
                // re-captured already-modded file), so refuse rather than overwrite — Restore must remain trustworthy.
                if (!string.Equals(HashFile(backupPath), expected, StringComparison.OrdinalIgnoreCase))
                    throw new BackupIntegrityException(targetName, backupPath);
            }
            else
            {
                // Legacy backup with no recorded hash (pre-hash install): record it now, best-effort.
                hashes[targetName] = HashFile(backupPath);
            }

            // Container-format guard: a room's Gian entry stride (DC1 16-byte vs DC2 32-byte) must never
            // change across an overlay. A flipped stride means the mod file was written in the wrong
            // container format (e.g. a DC1 16-byte rebuild of a DC2 room, from a stale/reused Generate
            // output dir), which the engine misreads into an out-of-range GPU-resource index → a hard
            // crash on room load (docs/decisions/dc2/crash-rcas/DC2-ROOM-CONTAINER-STRIDE-CRASH-RCA.md). The pristine backup is
            // the oracle; skip files that are not Gian containers (nothing to compare).
            var pristinePkg = GianPackage.TryParse(File.ReadAllBytes(backupPath));
            if (pristinePkg is not null)
            {
                var modPkg = GianPackage.TryParse(File.ReadAllBytes(modPath));
                if (modPkg is null || modPkg.IsDc2 != pristinePkg.IsDc2)
                    throw new ContainerFormatMismatchException(targetName, pristinePkg.IsDc2);
            }

            File.Copy(modPath, targetPath, overwrite: true);
            overlaid++;
            files.Add(targetName);
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

            var backupPath = Path.Combine(looseBackupRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(backupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                CapturePristine(target, backupPath);       // capture pristine original once (sibling-validated)
                hashes[rel] = HashFile(backupPath);
                backedUp++;
            }
            else if (hashes.TryGetValue(rel, out var expected))
            {
                if (!string.Equals(HashFile(backupPath), expected, StringComparison.OrdinalIgnoreCase))
                    throw new BackupIntegrityException(rel, backupPath);
            }
            else
            {
                hashes[rel] = HashFile(backupPath); // legacy backup with no recorded hash; record now
            }

            File.Copy(modPath, target, overwrite: true);
            overlaid++;
            files.Add(rel);
        }

        var manifest = new InstallManifest(seed, DateTime.UtcNow.ToString("o"), files,
            OriginalHashes: hashes, Applied: true);
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        // Apply any EXE-patch plan the runner emitted beside the room files (the cross-species pass declares
        // its cat-slot / hit-reaction / enemy-SE edits there). Each request maps to a PatchExe* method, which
        // backs the exe up once and merges into the manifest just written, so Restore reverses it too.
        var plan = ExePatchPlan.TryRead(modDir);
        if (plan is not null)
            ApplyExePatchPlan(dataDir, plan, modDir, seed);

        return new InstallResult(backedUp, overlaid, backupDir);
    }

    /// <summary>
    /// Apply an <see cref="ExePatchPlan"/> (the runner's <c>exe-patch-plan.json</c> sidecar): dispatch each
    /// typed <see cref="ExePatchRequest"/> to its existing <c>PatchExe*</c> method. <paramref name="donorDir"/>
    /// (the mod dir) is where a cat-8 hit-reaction donor room <c>.dat</c> is read from. Additive + idempotent,
    /// like the individual patches; reversed by <see cref="Restore"/>. See docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md.
    /// </summary>
    public static IReadOnlyList<ExePatchResult> ApplyExePatchPlan(
        string dataDir, ExePatchPlan plan, string donorDir, string? seed = null)
    {
        if (plan.Version != ExePatchPlan.CurrentVersion)
            throw new InvalidOperationException(
                $"exe-patch-plan version {plan.Version} is unsupported (expected {ExePatchPlan.CurrentVersion}).");

        var results = new List<ExePatchResult>();
        foreach (var req in plan.Requests)
        {
            switch (req.Kind)
            {
                case ExePatchKind.CatSlot:
                    results.Add(PatchExeCatSlot(dataDir, StageAiRecordVa(req.Stage), req.Category, req.HandlerVa, seed));
                    break;
                case ExePatchKind.Cat8HitReaction:
                {
                    var donorPath = ResolveDonorPath(donorDir, dataDir, req.DonorRoomFile
                        ?? throw new InvalidOperationException("Cat8HitReaction request has no donor room file."));
                    byte[] donorRdt = RoomFile.ReadFromFile(req.DonorStage, req.DonorRoom, donorPath).RdtBuffer;
                    results.Add(PatchExeCat8HitReaction(dataDir, donorRdt, seed));
                    break;
                }
                case ExePatchKind.RoomEnemySe:
                    results.Add(PatchExeRoomEnemySe(dataDir, req.Stage, req.Room, req.DonorStage, req.DonorRoom, seed));
                    break;
                default:
                    throw new InvalidOperationException($"unknown exe-patch kind {req.Kind}");
            }
        }
        return results;
    }

    /// <summary>VA of the installed AI-handler record for a stage whose category slot is verified free (the
    /// only stages a cat-slot patch may target). Throws for any other stage.</summary>
    private static uint StageAiRecordVa(int stage) => stage switch
    {
        1 => ExePatcher.Stage1AiRecordVa,
        2 => ExePatcher.Stage2AiRecordVa,
        _ => throw new InvalidOperationException(
            $"stage {stage} has no verified-free installed AI record for a cat-slot patch (only 1 and 2).")
    };

    /// <summary>Locate a donor room <c>.dat</c> by name, preferring the mod dir, then the data dir
    /// (case-insensitively) — covers the lowercase-original vs mixed-case-emitted naming.</summary>
    private static string ResolveDonorPath(string donorDir, string dataDir, string fileName)
    {
        var inMod = Path.Combine(donorDir, fileName);
        if (File.Exists(inMod)) return inMod;
        var inData = Path.Combine(dataDir, fileName);
        if (File.Exists(inData)) return inData;
        foreach (var p in Directory.EnumerateFiles(dataDir))
            if (string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase)) return p;
        throw new FileNotFoundException($"cat-8 reaction donor room '{fileName}' not found in mod or data dir.");
    }

    /// <summary>
    /// Back up one pristine original into <c>&lt;dataDir&gt;\.dinorand_backup\</c> (capturing it only
    /// if no backup of that file exists yet) and return the backup path. This is the shared primitive
    /// behind every in-place single-file edit — the DC1 cross-species / door swaps and the DC2 door
    /// edit all funnel through it, so they get one identical, non-compounding backup contract: callers
    /// then read the original FROM the returned backup path and write their edit to the live file, and
    /// <see cref="Restore"/> reverses every such edit at once. Re-running an edit never captures an
    /// already-edited file as "pristine".
    /// </summary>
    public static string BackupOnce(string dataDir, string originalPath)
    {
        EnsureNotDrmProtected(dataDir); // every single-file room edit funnels through here — refuse a DRM exe
        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, Path.GetFileName(originalPath));
        if (!File.Exists(backupPath))
            CapturePristine(originalPath, backupPath); // capture the pristine original once (for Restore)
        return backupPath;
    }

    /// <summary>
    /// True when the live file already differs from its pristine backup — i.e. a previous single-file
    /// edit is present. Because every such edit reads FROM the backup (see <see cref="BackupOnce"/>),
    /// edits do <b>not</b> stack: the next edit silently replaces the prior one (the cont.57
    /// false-handoff RCA — a <c>--copy-enemy-palette</c> after <c>--add-enemy-at</c> dropped the
    /// enemy record). Callers use this to make that replacement explicit to the user.
    /// </summary>
    public static bool HasPriorEdit(string backupPath, string livePath)
    {
        if (!File.Exists(backupPath) || !File.Exists(livePath)) return false;
        if (new FileInfo(backupPath).Length != new FileInfo(livePath).Length) return true;
        return !File.ReadAllBytes(backupPath).AsSpan().SequenceEqual(File.ReadAllBytes(livePath));
    }

    /// <summary>
    /// Read-only bulk audit of every <c>.dinorand_backup</c> capture (rooms + <c>loose\</c> subtree,
    /// excluding the manifest) against the strongest available pristine oracle, to detect poisoned
    /// captures — backups taken from an already-modded file (the ST001 turret-crash recurrence,
    /// docs/decisions/dc2/crash-rcas/DC2-ST001-PRELOAD-STRIP-CRASH-RCA.md, K82). Proof order per file:
    /// manifest pristine hash, then a <c>.dinorand-bak</c> sibling, then the live file (which is
    /// vanilla ground truth only while no install is applied — hence <see cref="BackupVerifyStatus.Suspect"/>,
    /// not <see cref="BackupVerifyStatus.Poisoned"/>, on a live mismatch alone).
    /// </summary>
    public static IReadOnlyList<BackupVerifyResult> VerifyBackups(string dataDir)
    {
        var results = new List<BackupVerifyResult>();
        var backupDir = Path.Combine(dataDir, BackupDirName);
        if (!Directory.Exists(backupDir))
            return results;

        var manifest = ReadManifest(dataDir);
        bool applied = manifest?.Applied == true;
        var hashes = manifest?.OriginalHashes;

        foreach (var backupPath in Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backupDir, backupPath).Replace('\\', '/');
            if (string.Equals(rel, ManifestName, StringComparison.OrdinalIgnoreCase))
                continue;

            bool loose = rel.StartsWith(LooseBackupSubdir + "/", StringComparison.OrdinalIgnoreCase);
            var name = loose ? rel[(LooseBackupSubdir.Length + 1)..] : rel;
            var livePath = loose
                ? Path.Combine(GameRoot(dataDir), name.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(dataDir, name);

            if (hashes is not null && hashes.TryGetValue(name, out var expected)
                && !string.Equals(HashFile(backupPath), expected, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(name, BackupVerifyStatus.Poisoned,
                    "backup does not match the manifest's recorded pristine hash"));
                continue;
            }

            var sibling = livePath + SiblingBackupSuffix;
            if (File.Exists(sibling) && !FilesEqual(backupPath, sibling))
            {
                results.Add(new(name, BackupVerifyStatus.Poisoned,
                    $"backup disagrees with the pristine sibling {Path.GetFileName(sibling)}"));
                continue;
            }

            if (!File.Exists(livePath))
            {
                results.Add(new(name, BackupVerifyStatus.LiveMissing, "no live counterpart for this backup"));
                continue;
            }

            if (FilesEqual(backupPath, livePath))
                results.Add(new(name, BackupVerifyStatus.Ok, "backup matches the live file"));
            else if (applied)
                results.Add(new(name, BackupVerifyStatus.Installed,
                    "live file differs (a randomizer install is applied — expected)"));
            else
                results.Add(new(name, BackupVerifyStatus.Suspect,
                    "backup differs from the live file while no install is applied — "
                    + "a poisoned capture, or an un-manifested in-place edit"));
        }
        return results;
    }

    private static bool FilesEqual(string a, string b) =>
        string.Equals(HashFile(a), HashFile(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Capture a first-time pristine backup of <paramref name="originalPath"/> at
    /// <paramref name="backupPath"/>. If a tools-style <c>&lt;file&gt;.dinorand-bak</c> sibling exists
    /// and differs from the live file, the live file has already been edited OUTSIDE the installer, so
    /// the sibling (captured before that edit) is the pristine original and is copied instead —
    /// capturing the live file would poison the backup and make Restore reinstall the foreign edit
    /// (the ST001 turret-room crash recurrence: docs/decisions/dc2/crash-rcas/DC2-ST001-PRELOAD-STRIP-CRASH-RCA.md, K82).
    /// </summary>
    private static void CapturePristine(string originalPath, string backupPath)
    {
        var sibling = originalPath + SiblingBackupSuffix;
        var source = File.Exists(sibling)
                     && !string.Equals(HashFile(sibling), HashFile(originalPath), StringComparison.OrdinalIgnoreCase)
            ? sibling
            : originalPath;
        File.Copy(source, backupPath);
    }

    /// <summary>
    /// Restore the pristine originals: copy every backed-up file back into <paramref name="dataDir"/>.
    /// The backup folder is <b>kept</b> (never deleted) so it can be reused for future installs; the
    /// manifest is flipped to <c>Applied = false</c> to record that the mod is no longer overlaid.
    /// Idempotent — a no-op when nothing is backed up. Each backup is validated against its recorded
    /// pristine hash <i>before any file is written</i>, so a corrupted backup is refused
    /// (<see cref="BackupIntegrityException"/>) rather than written over the game.
    /// </summary>
    public static RestoreResult Restore(string dataDir)
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

    /// <summary>
    /// The DC1 vertex-ceiling lift is a PAIRED patch ("apply both or neither"): the backup holds the
    /// pre-lift <c>DINO.exe</c>, but the lifted <c>ddraw.dll</c> lives outside the backup contract, so
    /// a plain restore used to leave the DLL writing into the missing <c>.dinovtx</c> section —
    /// write-AV at 0x6FD000 on the next character render
    /// (docs/decisions/dc1/crash-rcas/VTXLIFT-RESTORE-PAIRING-RCA.md). Re-lift the exe whenever the
    /// installed ddraw.dll is lifted and the exe is not. Returns true when the lift was re-applied.
    /// </summary>
    private static bool ReapplyDc1VertexLiftIfDdrawLifted(string dataDir)
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

    /// <summary>Absolute path of <c>DINO.exe</c> for the install whose <c>Data\</c> is
    /// <paramref name="dataDir"/> — its sibling in the game root.</summary>
    public static string ExePath(string dataDir) => Path.Combine(GameRoot(dataDir), ExeName);

    /// <summary>
    /// Refuse to operate on a game whose <c>DINO.exe</c> is wrapped by a DRM protector (e.g. The Enigma
    /// Protector on the Steam build): throws <see cref="DrmProtectedExeException"/>. This is the
    /// authoritative backstop for the whole installer — every overlay and every exe patch funnels through
    /// <see cref="Install"/>, <see cref="BackupOnce"/>, or <see cref="ResolveExeForPatch"/>, each of which
    /// calls this. A missing exe is left to the caller's own not-found handling (we never false-flag it).
    /// See <see cref="ExeProtection"/> for the detection and the rationale (TOS + patch-offset validity).
    /// </summary>
    public static void EnsureNotDrmProtected(string dataDir)
    {
        var exePath = ExePath(dataDir);
        if (!File.Exists(exePath))
            return;
        var detection = ExeProtection.Inspect(exePath);
        if (detection.IsProtected)
            throw new DrmProtectedExeException(exePath, detection);
    }

    /// <summary>Shared patch preamble: resolve <c>DINO.exe</c>, require it to exist, and refuse it if it is
    /// DRM-protected. Every <c>PatchExe*</c> method funnels through here, so none can ever write a protected
    /// executable.</summary>
    private static string ResolveExeForPatch(string dataDir)
    {
        var exePath = ExePath(dataDir);
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"game executable not found beside Data: {exePath}", exePath);
        }
        EnsureNotDrmProtected(dataDir); // TOS + patch-offset validity: never write a DRM-wrapped exe
        return exePath;
    }

    /// <summary>
    /// Patch the installed <c>DINO.exe</c>: for each <see cref="ExeRepoint"/>, repoint the target
    /// stage's enemy-set record (<c>+0x0E</c> setup-fn ptr) to the donor stage's, swapping the target
    /// stage's enemy set to the donor's (docs/decisions/dc1/exe/EXE-PATCH-PER-ROOM-PLAN.md). The pristine exe is backed
    /// up once into the same <c>.dinorand_backup\</c> folder the room overlay uses, and the edit is
    /// always applied <i>from</i> that pristine copy so re-running is non-compounding. The manifest is
    /// updated so <see cref="Restore"/> reverses the exe alongside the rooms.
    ///
    /// <para><b>Stage-scoped:</b> the index a room loads is <c>roomId &gt;&gt; 8</c>, so a repoint of
    /// <c>record[s]</c> changes the enemy set for <i>every</i> room in stage <c>s</c>.</para>
    /// </summary>
    public static ExePatchResult PatchExe(string dataDir, IReadOnlyList<ExeRepoint> repoints, string? seed = null)
    {
        if (repoints.Count == 0)
            throw new ArgumentException("no exe repoints supplied", nameof(repoints));

        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture pristine exe once

        // Always edit from the pristine backup so repeated patches don't compound.
        var bytes = File.ReadAllBytes(exeBackup);

        var applied = new List<string>();
        foreach (var rp in repoints)
        {
            uint donorFn = ExePatcher.ReadSetupFn(bytes, rp.DonorStage);
            uint previous = ExePatcher.RepointSetupFn(bytes, rp.TargetStage, donorFn);
            applied.Add($"stage{rp.TargetStage}<-stage{rp.DonorStage} " +
                        $"(0x{previous:X}->0x{donorFn:X} @ record[{rp.TargetStage}]+0x0E)");
        }

        File.WriteAllBytes(exePath, bytes);

        // Merge into the existing manifest (a room install may have written one already), or create one.
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seed, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        manifest = manifest with { ExePatched = true, ExeRepoints = applied };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, applied);
    }

    /// <summary>
    /// Surgical EXE patch: set one category slot of a stage's installed AI-handler record to a donor
    /// handler (<see cref="ExePatcher.SetRecordCategoryHandler"/>) — the precise cross-species lever for a
    /// category the stage does not natively host (e.g. cat8 Therizinosaurus into stage 2). Unlike
    /// <see cref="PatchExe"/> this changes a single dword, no whole-record swap. Same backup / edit-from-
    /// pristine / manifest contract, so <see cref="Restore"/> reverses it with the room <c>.dat</c>.
    /// </summary>
    public static ExePatchResult PatchExeCatSlot(string dataDir, uint recordVa, int category, uint handlerVa,
                                                 string? seed = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the original exe once (for --restore)

        // Edit the LIVE exe — a cat-slot patch sets one dword to a fixed handler VA, so it is idempotent and
        // ADDITIVE: applying one room's slot must not revert another room's slot (e.g. patching stage-2 cat8
        // for the Theri must leave stage-1 cat3 for an 0102 T-Rex intact). Editing from the pristine backup
        // instead would drop every slot not in that backup. (The whole-record setup-fn repoint of PatchExe,
        // which CAN compound, still edits from pristine.) Editing live also preserves the null-guard for free.
        var bytes = File.ReadAllBytes(exePath);
        uint previous = ExePatcher.SetRecordCategoryHandler(bytes, recordVa, category, handlerVa);
        File.WriteAllBytes(exePath, bytes);

        string entry = $"record@0x{recordVa:X} cat{category} = 0x{handlerVa:X} (was 0x{previous:X} @ +0x{category * 4:X})";
        string slotKey = $"record@0x{recordVa:X} cat{category} ";   // identifies this exact record+slot
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seed, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        // Accumulate active repoints; replace any prior entry for the same slot (re-patching is idempotent).
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(slotKey, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, new[] { entry });
    }

    /// <summary>
    /// Defect-B EXE patch: install the canonical cat-8 hit/death descriptor records into the EXE cave and
    /// repoint the cat-8 descriptor tables to them, so the imported Theri can be shot/killed without the
    /// <c>0x4B0794</c>/<c>0x45dc0e</c> AVs (docs/decisions/dc1/theri/THERI-0102-PLAYABLE-FIX-PLAN.md). The 10 records are
    /// extracted from <paramref name="hostRdt"/> (the decompressed <b>st605</b> RDT — the canonical cat-8
    /// host) via the EXE's still-file-form table entries; the records are <b>pointer-free</b>, so the cave
    /// copy is valid in every room and the patch is cat-8-exclusive (no other enemy is affected).
    ///
    /// <para>Extraction reads the table entries from the <b>pristine backup</b> (where they are still
    /// file-form), then the redirect is applied to the <b>live</b> exe — additive and idempotent, like
    /// <see cref="PatchExeCatSlot"/>, so it composes with the cat8-slot patch and re-runs cleanly.</para>
    /// </summary>
    public static ExePatchResult PatchExeCat8HitDescriptors(string dataDir, byte[] hostRdt, string? seed = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        // Extract the canonical records from the PRISTINE backup — its descriptor tables are still file-form
        // (the redirect, applied only to the live exe, is what overwrites them). This keeps re-runs working.
        byte[] pristine = File.ReadAllBytes(exeBackup);
        byte[] records = Cat8HitDescriptors.Extract(pristine, hostRdt);

        // Apply the redirect to the LIVE exe (additive: preserves the cat8-slot patch + any null-guard).
        byte[] bytes = File.ReadAllBytes(exePath);
        ExePatcher.RedirectCat8HitDescriptors(bytes, records);
        File.WriteAllBytes(exePath, bytes);

        string entry = $"cat8 hit/death descriptors -> cave 0x{ExePatcher.HitDescriptorCaveVa:X} " +
                       $"({ExePatcher.HitDescriptorTotalRecords} records from st605)";
        const string key = "cat8 hit/death descriptors";
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seed, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(key, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, new[] { entry });
    }

    /// <summary>
    /// Corrected defect-B fix (live-verified): cave the cat-8 hit-REACTION descriptor stream from a normal
    /// Theri donor RDT and repoint the index-4/5 reaction table slots to it, so the cross-imported Theri can
    /// be attacked/shot/killed without the walker AV at <c>0x41685B</c>
    /// (docs/decisions/dc1/theri/THERI-0102-PLAYABLE-FIX-PLAN.md Deviations). Supersedes
    /// <see cref="PatchExeCat8HitDescriptors"/> (the refuted st605 descriptor-table model).
    ///
    /// <para><paramref name="donorRdt"/> is the decompressed <b>st603</b> (normal Theri room) RDT — its
    /// offset <see cref="ExePatcher.Cat8ReactionDonorRdtOffset"/> holds the valid reaction descriptor stream
    /// the table baked-points at. Validated as a real (walk-safe) descriptor stream before patching; throws
    /// for a donor whose offset is motion garbage (wrong room). Applied additively to the live exe and logged
    /// in the manifest (idempotent re-apply), reversed byte-identically by <see cref="Restore"/>.</para>
    /// </summary>
    public static ExePatchResult PatchExeCat8HitReaction(string dataDir, byte[] donorRdt, string? seed = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        int off = ExePatcher.Cat8ReactionDonorRdtOffset, len = ExePatcher.Cat8ReactionStreamBytes;
        int recSize = ExePatcher.HitDescriptorRecordSize;
        if (off + len > donorRdt.Length)
            throw new InvalidOperationException(
                $"donor RDT (0x{donorRdt.Length:X} B) is too small for the cat-8 reaction stream at " +
                $"0x{off:X}+0x{len:X} — wrong donor room (need a normal Theri room like st603).");
        // Validate it is a real descriptor stream (state ≤ 7, small walk count), not motion garbage — the
        // st603-vs-st605 distinction that makes the cave faithful and crash-safe.
        for (int i = 0; i < 23; i++)
        {
            int r = off + i * recSize;
            byte state = donorRdt[r + 0x10], cnt = donorRdt[r + 0xe];
            if (state > 7 || cnt > 0x10)
                throw new InvalidOperationException(
                    $"donor RDT offset 0x{r:X} is not a clean cat-8 reaction descriptor (state=0x{state:X2}, " +
                    $"cnt=0x{cnt:X2}) — the donor is not a normal Theri room (st603). Cannot fix the hit reaction.");
        }
        var stream = new byte[len];
        Array.Copy(donorRdt, off, stream, 0, len);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        byte[] bytes = File.ReadAllBytes(exePath);
        uint[] repointed = ExePatcher.RedirectCat8HitReaction(bytes, stream);
        // Universal walker NULL-guard: the single fix that survives the cross-import garbage in ALL reaction
        // descriptor tables (the cat-8 repoint above only covers the cat-8 path; the generic hit handler and
        // other paths read other table sets). Live-verified killable. See ExePatcher.InstallWalkerNullGuard.
        ExePatcher.InstallWalkerNullGuard(bytes);
        // Render-model node guard: the per-frame transform pass crash-safety net (invalid node+0x3C model
        // header; docs/decisions/dc1/crash-rcas/ELEVATOR-060F-HANDGUN-CRASH.md). Both guards travel together.
        ExePatcher.InstallRenderModelGuard(bytes);
        File.WriteAllBytes(exePath, bytes);

        string entry = $"cat8 hit reaction -> cave 0x{ExePatcher.HitDescriptorCaveVa:X} " +
                       $"(0x{len:X} B from st603 RDT 0x{off:X}; {repointed.Length} entries) + walker null-guard " +
                       $"0x{ExePatcher.WalkerVa:X}->0x{ExePatcher.WalkerCaveVa:X} + render-model guard " +
                       $"0x{ExePatcher.RenderTransformHookVa:X}->0x{ExePatcher.RenderGuardCaveVa:X}";
        const string key = "cat8 hit reaction";
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seed, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(key, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, new[] { entry });
    }

    /// <summary>
    /// Install the universal crash-safety EXE guards on their own — the cross-import / cross-stage safety levers
    /// that are not tied to a specific cat-slot or reaction-table repoint. Two benign, universal guards are
    /// applied together (both only convert a crash into a graceful skip, and are identical for every valid
    /// caller):
    /// <list type="bullet">
    /// <item>the defect-B walker NULL-guard (<see cref="ExePatcher.InstallWalkerNullGuard"/>) — the shared
    /// <c>+0x34</c> walker <c>0x416845</c> guarded against a garbage hit/death walk count;</item>
    /// <item>the render-model display-node guard (<see cref="ExePatcher.InstallRenderModelGuard"/>) — the
    /// per-frame transform pass <c>sub_44CED6</c> at <c>0x44D130</c> guarded against an invalid
    /// <c>node+0x3C</c> model header (NULL <i>or</i> an un-relocated PSX file-form pointer; the 060F-elevator /
    /// 0511 cross-stage crash, docs/decisions/dc1/crash-rcas/ELEVATOR-060F-HANDGUN-CRASH.md). Widens the deprecated
    /// <c>tools/scd_re/apply_nullguard.py</c> (<c>==0</c> only).</item>
    /// </list>
    /// Same backup-once / edit-live (additive) / manifest contract as <see cref="PatchExeCatSlot"/>; idempotent
    /// (re-applying is a no-op) and reversed by <see cref="Restore"/>.
    /// </summary>
    public static ExePatchResult PatchExeWalkerNullGuard(string dataDir, string? seed = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        byte[] bytes = File.ReadAllBytes(exePath);
        ExePatcher.InstallWalkerNullGuard(bytes);   // idempotent (no-op if already installed)
        ExePatcher.InstallRenderModelGuard(bytes);  // idempotent; widened render-model node guard
        File.WriteAllBytes(exePath, bytes);

        string entry = $"crash guards: walker 0x{ExePatcher.WalkerVa:X}->0x{ExePatcher.WalkerCaveVa:X}, " +
                       $"render-model 0x{ExePatcher.RenderTransformHookVa:X}->0x{ExePatcher.RenderGuardCaveVa:X}";
        const string key = "crash guards";
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seed, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(key, StringComparison.Ordinal)
                        && !r.StartsWith("walker null-guard", StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, new[] { entry });
    }

    /// <summary>
    /// Enemy-sound EXE patch (docs/reference/dc1/se/ENEMY-SOUND-SYSTEM.md): make a cross-swapped room load the swapped
    /// species' enemy SE instead of its native (e.g. raptor) set. DC1 binds the dino SE set to the room id
    /// via a per-room SE manifest in <c>DINO.exe</c> (room→block directory <c>0x63A470</c>); a swap changes
    /// geometry/motion/AI but not this table, so the room keeps playing its original samples. This copies
    /// the contiguous dino sub-block from a <i>native</i> target-species room (<paramref name="donorStage"/>,
    /// <paramref name="donorRoom"/> — e.g. a Theri room st605) over room (<paramref name="stage"/>,
    /// <paramref name="room"/>)'s dino records and early-terminates, so it loads exactly the target species'
    /// id→WAV set (footprint-faithful to a native room; the SE bank is id-keyed and the AI emits those ids).
    ///
    /// <para>The donor sub-block is read from the same (live) exe — it is never modified by any patch, so
    /// it is locale-correct (uses this exe's own SE-name pointers). Applied additively (it edits only the
    /// swapped room's block) and logged in the manifest; reversed byte-identically by <see cref="Restore"/>.</para>
    /// </summary>
    public static ExePatchResult PatchExeRoomEnemySe(
        string dataDir, int stage, int room, int donorStage, int donorRoom, string? seed = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        byte[] bytes = File.ReadAllBytes(exePath);
        byte[] donorSub = ExePatcher.ExtractRoomDinoSubBlock(bytes, donorStage, donorRoom);
        if (donorSub.Length == 0)
            throw new InvalidOperationException(
                $"donor room st{donorStage}{donorRoom:X2} has no enemy SE records — cannot source the target species' sound set.");
        var res = ExePatcher.RetargetRoomDinoSe(bytes, stage, room, donorSub);
        File.WriteAllBytes(exePath, bytes);

        string entry = $"room enemy SE st{stage}{room:X2} -> donor st{donorStage}{donorRoom:X2} " +
                       $"({res.RecordsWritten}/{res.Capacity} dino records @ block 0x{res.TargetBlockVa:X})";
        string key = $"room enemy SE st{stage}{room:X2}";
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seed, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(key, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, new[] { entry });
    }

    /// <summary>
    /// Music-randomizer EXE patch (docs/reference/dc1/bgm/BGM-SYSTEM.md §4): shuffle the global BGM catalog so every track id
    /// streams a different file, deterministically from <paramref name="seed"/>. DC1 resolves all music through
    /// one id→file catalog in <c>DINO.exe</c> (<see cref="ExePatcher.BgmCatalogBaseVa"/>); it is consumed at
    /// game init (a live memory edit does not reroute — LIVE-VERIFIED), so this is an on-disk patch the next
    /// launch reads. The permutation stays <b>within each <c>flags</c> class</b> (stream/loop behaviour preserved)
    /// and leaves the id-1 version tag + the post-99 SE rows alone (<see cref="ExePatcher.ShuffleBgmCatalog"/>).
    ///
    /// <para>The shuffle is computed from the <b>pristine backup</b> (deterministic + non-compounding: re-running
    /// any seed re-shuffles from the original layout), then only the catalog region is transplanted into the
    /// <b>live</b> exe — so it is additive (composes with any enemy/SE exe patch) and idempotent for a fixed seed.
    /// Logged in the manifest and reversed byte-identically by <see cref="Restore"/>.</para>
    /// </summary>
    public static ExePatchResult PatchExeShuffleBgm(string dataDir, int seed, string? seedLabel = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        // Shuffle from the PRISTINE catalog (non-compounding) → `pristine` now holds the shuffled catalog region.
        byte[] pristine = File.ReadAllBytes(exeBackup);
        var entries = ExePatcher.ShuffleBgmCatalog(pristine, seed);

        // Transplant ONLY the catalog region into the live exe (additive — preserves any other exe patch).
        byte[] bytes = File.ReadAllBytes(exePath);
        int catOff = ExePatcher.VaToFileOffset(ExePatcher.BgmCatalogBaseVa);
        int catLen = ExePatcher.BgmRecordCount * ExePatcher.BgmRecordStride;
        Array.Copy(pristine, catOff, bytes, catOff, catLen);
        File.WriteAllBytes(exePath, bytes);

        int moved = entries.Count(e => e.OldNamePtr != e.NewNamePtr);
        int classes = entries.Select(e => e.Flags).Distinct().Count();
        string entry = $"bgm catalog shuffle (seed {seedLabel ?? seed.ToString()}): {moved}/{entries.Length} " +
                       $"records rerouted within {classes} flags classes @ catalog 0x{ExePatcher.BgmCatalogBaseVa:X}";
        const string key = "bgm catalog shuffle";
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seedLabel, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(key, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, new[] { entry });
    }

    /// <summary>
    /// Scramble the DC1 keypad-code puzzle family (Management Office / Lounge / Computer-Room-gas safes +
    /// the Stabilizer codes) so each lock's accepted 4-digit code is seed-derived, and keep the in-game
    /// document that states the code in sync — the <b>displayed == checked</b> invariant
    /// (<see cref="Dc1PuzzleCodeSync"/>, docs/reference/dc1/puzzle/MGMT-OFFICE-SAFE-PUZZLE-DECODE.md §17–18).
    ///
    /// <para><b>Edition-aware</b> (<see cref="Dc1EditionDetector"/>): the keypad-CHECK table edit is common
    /// (every recompiled DINO.exe copy, both region halves — scrambled on the <b>pristine</b> backup image,
    /// then only the ~0.4&#160;KB table region is transplanted into the live exe, so it is additive w.r.t.
    /// any other exe patch and non-compounding across re-runs), but the DOCUMENT side routes per install:
    /// <c>GogInlineText</c> → repack the three room files (<c>st100/st200/st302.dat</c>) from pristine
    /// backups; <c>RebirthEnglish</c> → rebuild the English diff archive inside <c>ddraw.dll</c>
    /// (<see cref="RebirthTextPatcher"/>, live-confirmed in-game 2026-07-10); <c>RebirthJapanese</c> → skip
    /// with a warning (codes stay stock — no JP text lever yet); <c>Unknown</c> → refuse. The European GOG
    /// executables do not carry the JP-master table and are refused by the stock verification.
    /// Same backup-once + manifest contract as <see cref="PatchExeShuffleBgm"/> — <see cref="Restore"/>
    /// reverses the exe, rooms and ddraw.dll. Codes are read at load time (seen after the next launch).</para>
    /// </summary>
    public static ExePatchResult PatchExeSyncPuzzleCodes(string dataDir, int seed, string? seedLabel = null)
    {
        // Route by install edition: the keypad-CHECK lever (exe table) is build-independent among the
        // JP-master ports, but the DOCUMENT the player reads lives in a different place per edition —
        // inline RDT glyphs (GOG European), REbirth's ddraw.dll diff archive (REbirth-English), or JP
        // room text we cannot rewrite (REbirth-Japanese / plain JP). Never ship displayed != checked.
        var edition = Dc1EditionDetector.Detect(dataDir);
        var exePath = ResolveExeForPatch(dataDir);

        if (edition == Dc1Edition.RebirthJapanese)
        {
            // JP text in the room files needs a JP char-table lever that does not exist yet; scrambling
            // the exe alone would desync displayed != checked, so leave the codes stock — consistent.
            return new ExePatchResult(exePath, string.Empty, new[]
            {
                "puzzle codes SKIPPED: REbirth-Japanese install — the JP document text has no rewrite " +
                "lever yet, so the codes stay stock (displayed == checked either way).",
            });
        }
        if (edition == Dc1Edition.Unknown)
            throw new InvalidOperationException(
                "Puzzle-code scramble refused: could not classify this DC1 install (no REbirth ddraw.dll " +
                "version-lock match and no inline Latin document text in st100.dat). Scrambling blindly " +
                "could leave the document showing a code the keypad rejects (displayed != checked).");

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for Restore)

        bool viaRebirthDll = edition == Dc1Edition.RebirthEnglish;
        byte[]? patchedDll = null;
        string? ddrawPath = null;

        if (viaRebirthDll)
        {
            // Documents live in ddraw.dll's embedded diff archive (REBIRTH-DDRAW-TEXT-STORE-RE.md).
            // Patch from the pristine backup (non-compounding across seeds); written only after the exe
            // scramble also succeeds, so nothing is ever half-applied.
            ddrawPath = Path.Combine(GameRoot(dataDir), "ddraw.dll");
            var ddrawBackup = Path.Combine(backupDir, LooseBackupSubdir, "ddraw.dll");
            if (!File.Exists(ddrawBackup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ddrawBackup)!);
                File.Copy(ddrawPath, ddrawBackup); // pristine once; Restore reverses it to the game root
            }
            patchedDll = RebirthTextPatcher.PatchPuzzleCodeText(
                File.ReadAllBytes(ddrawBackup), row => Dc1PuzzleCodeSync.DeriveRowCode(seed, row));
        }
        else
        {
            // GOG inline-text path: back up each document room once; code runs are read from the
            // PRISTINE backup (non-compounding).
            foreach (var lk in Dc1PuzzleCodeSync.Family)
                if (lk.DocFile is not null && File.Exists(Path.Combine(dataDir, lk.DocFile)))
                    BackupOnce(dataDir, Path.Combine(dataDir, lk.DocFile));
        }

        // Scramble the PRISTINE exe image (verifies the stock JP-master keypad table — throws for the
        // differently-laid-out European executables and never derives from an already-scrambled table),
        // then transplant only the table region into the LIVE exe so the edit stays additive w.r.t.
        // other exe patches (same pristine-source + transplant contract as PatchExeShuffleBgm).
        byte[] pristineExe = File.ReadAllBytes(exeBackup);
        var results = Dc1PuzzleCodeSync.Scramble(
            pristineExe,
            lk =>
            {
                var bak = Path.Combine(backupDir, lk.DocFile!);
                return File.Exists(bak) ? File.ReadAllBytes(bak) : null;
            },
            lk => Dc1PuzzleCodeSync.DeriveRowCode(seed, lk.Row),
            requireDocuments: !viaRebirthDll);

        byte[] exe = File.ReadAllBytes(exePath);
        Dc1PuzzleCodeSync.CopyTableRegion(pristineExe, exe);
        File.WriteAllBytes(exePath, exe);
        var entries = new List<string>();
        if (patchedDll is not null && ddrawPath is not null)
        {
            File.WriteAllBytes(ddrawPath, patchedDll);
            entries.Add("ddraw.dll: REbirth English document text re-synced (diff archive rebuilt)");
        }
        foreach (var r in results)
        {
            string code = string.Concat(r.NewCode);
            if (r is { DocRewritten: true, DocFile: { } f, DocBytes: { } b })
            {
                File.WriteAllBytes(Path.Combine(dataDir, f), b);
                entries.Add($"{r.Lock.Name}: {code} (exe row {r.Lock.Row} + {f})");
            }
            else
            {
                entries.Add($"{r.Lock.Name}: {code} (exe row {r.Lock.Row}" +
                            (viaRebirthDll && r.Lock.DocFile is not null ? " + ddraw diff)" : ")"));
            }
        }

        const string key = "puzzle code sync";
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seedLabel, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(key, StringComparison.Ordinal))
            .Append($"{key} (seed {seedLabel ?? seed.ToString()}): " + string.Join("; ", entries))
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, entries.ToArray());
    }

    /// <summary>
    /// Patch the installed <c>DINO.exe</c> to shuffle emergency-box contents (the box-content randomizer
    /// lever, EXPERIMENTAL): within each International difficulty block the 17 box records are permuted, so
    /// every box holds a different (still valid, difficulty-appropriate) loadout. Same pristine-source +
    /// additive-transplant + manifest contract as <see cref="PatchExeShuffleBgm"/>, so <see cref="Restore"/>
    /// reverses it. Boxes are read at load time, so the change is seen after the next launch.
    /// docs/reference/dc1/items/EMERGENCY-BOX-DATA.md.
    /// </summary>
    public static ExePatchResult PatchExeShuffleBoxes(string dataDir, int seed, string? seedLabel = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        // Shuffle from the PRISTINE table (non-compounding) → `pristine` now holds the shuffled blocks.
        byte[] pristine = File.ReadAllBytes(exeBackup);
        var entries = ExePatcher.ShuffleEmergencyBoxContents(pristine, seed);

        // Transplant ONLY the box-table span into the live exe (additive — preserves any other exe patch).
        // The three International blocks are non-contiguous; copy the whole enclosing span (the untouched
        // Japanese blocks in between are byte-identical in `pristine`, so re-copying them is inert).
        byte[] bytes = File.ReadAllBytes(exePath);
        int spanStart = ExePatcher.VaToFileOffset(ExePatcher.EmergencyBoxBlockEasyVa);
        int spanEnd = ExePatcher.VaToFileOffset(ExePatcher.EmergencyBoxBlockVeryHardVa)
                      + ExePatcher.EmergencyBoxesPerBlock * ExePatcher.EmergencyBoxRecordStride;
        Array.Copy(pristine, spanStart, bytes, spanStart, spanEnd - spanStart);
        File.WriteAllBytes(exePath, bytes);

        int moved = entries.Count(e => e.Slot != e.SourceSlot);
        int blocks = entries.Select(e => e.BlockVa).Distinct().Count();
        string entry = $"emergency-box shuffle (seed {seedLabel ?? seed.ToString()}): {moved}/{entries.Length} " +
                       $"boxes relocated across {blocks} difficulty blocks @ 0x{ExePatcher.EmergencyBoxBlockEasyVa:X}";
        // Keyed on the shared prefix so a later box patch (shuffle OR reroll) replaces this line — both modes
        // rewrite the same span, so only one can be active per install.
        const string key = "emergency-box ";
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seedLabel, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(key, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, new[] { entry });
    }

    /// <summary>
    /// Patch the installed <c>DINO.exe</c> to <b>reroll</b> emergency-box contents (the box-content randomizer's
    /// pool-reroll mode, EXPERIMENTAL): each box's slots are redrawn from that difficulty block's own loot
    /// distribution (<see cref="ExePatcher.RerollEmergencyBoxContents"/>) — box-flavoured and distinct from the
    /// map item pool. Same pristine-source + additive-transplant + manifest contract as
    /// <see cref="PatchExeShuffleBoxes"/> (and the same byte span, so the two box modes are mutually exclusive),
    /// reversed by <see cref="Restore"/>. Seen after the next launch. docs/reference/dc1/items/EMERGENCY-BOX-DATA.md.
    /// </summary>
    public static ExePatchResult PatchExeRerollBoxes(string dataDir, int seed, string? seedLabel = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        // Reroll from the PRISTINE table (non-compounding) → `pristine` now holds the rerolled blocks.
        byte[] pristine = File.ReadAllBytes(exeBackup);
        var entries = ExePatcher.RerollEmergencyBoxContents(pristine, seed);

        // Transplant ONLY the box-table span into the live exe (additive — preserves any other exe patch).
        byte[] bytes = File.ReadAllBytes(exePath);
        int spanStart = ExePatcher.VaToFileOffset(ExePatcher.EmergencyBoxBlockEasyVa);
        int spanEnd = ExePatcher.VaToFileOffset(ExePatcher.EmergencyBoxBlockVeryHardVa)
                      + ExePatcher.EmergencyBoxesPerBlock * ExePatcher.EmergencyBoxRecordStride;
        Array.Copy(pristine, spanStart, bytes, spanStart, spanEnd - spanStart);
        File.WriteAllBytes(exePath, bytes);

        int blocks = entries.Select(e => e.BlockVa).Distinct().Count();
        string entry = $"emergency-box reroll (seed {seedLabel ?? seed.ToString()}): {entries.Length} " +
                       $"boxes rerolled from per-block pools across {blocks} difficulty blocks @ 0x{ExePatcher.EmergencyBoxBlockEasyVa:X}";
        // Keyed so a later box patch (shuffle OR reroll) replaces this line — the two modes share the span.
        const string key = "emergency-box ";
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seedLabel, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(key, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, new[] { entry });
    }

    /// <summary>
    /// RANDOM starting-inventory EXE patch (docs/reference/dc1/items/STARTING-INVENTORY.md, EXPERIMENTAL): redraw every
    /// difficulty block's supply slots from the ammo+health pool, deterministically from <paramref name="seed"/>
    /// (<see cref="ExePatcher.RandomizeStartingInventory"/>). The Handgun (a group-11 flag grant) is left
    /// untouched and slot 0 of each block is forced to 9mm, so the start is always beatable. Same
    /// pristine-source + additive-transplant + manifest contract as <see cref="PatchExeShuffleBoxes"/>, so
    /// <see cref="Restore"/> reverses it. Read at new-game, so seen on the next new game after relaunch.
    /// </summary>
    public static ExePatchResult PatchExeRandomizeInventory(string dataDir, int seed, string? seedLabel = null)
        => PatchExeStartingInventory(dataDir, new StartingInventoryPlan(RandomizeSupply: true), seed, seedLabel);

    /// <summary>
    /// CUSTOM starting-inventory EXE patch (docs/reference/dc1/items/STARTING-INVENTORY.md, EXPERIMENTAL): write the
    /// explicit <paramref name="items"/> <c>(id,count)</c> list into every difficulty block's slots
    /// (<see cref="ExePatcher.SetStartingInventory"/>), preserving the Handgun grant. Same contract as
    /// <see cref="PatchExeRandomizeInventory"/> (shared span, so the two starting-inventory modes are mutually
    /// exclusive per install), reversed by <see cref="Restore"/>.
    /// </summary>
    public static ExePatchResult PatchExeSetInventory(string dataDir, IReadOnlyList<(int Id, int Count)> items, string? seedLabel = null)
        => PatchExeStartingInventory(dataDir, new StartingInventoryPlan(CustomSupply: items), 0, seedLabel);

    /// <summary>
    /// Apply a combined starting-inventory EXE patch — the supply slots (RANDOM or CUSTOM) <b>and</b> the
    /// starting weapon grant — in <b>one</b> operation (docs/reference/dc1/items/STARTING-INVENTORY.md, EXPERIMENTAL). Both
    /// halves are applied to a single pristine-sourced buffer and the whole init span is transplanted once, so
    /// they compose (one install = one starting-inventory state) rather than clobbering each other. Pristine-
    /// source (non-compounding), additive (preserves other exe patches), one manifest line keyed
    /// <c>"starting inventory "</c> (so re-running replaces it), reversed by <see cref="Restore"/>. The weapon
    /// is granted via the group-11 flags (<see cref="ExePatcher.SetStartingWeapon"/>); removing it
    /// (<see cref="StartingInventoryPlan.SetWeapon"/> with <c>WeaponId == null</c>) means the world item pass
    /// must place a reachable weapon — see the item-pass beatability coupling.
    /// </summary>
    public static ExePatchResult PatchExeStartingInventory(
        string dataDir, StartingInventoryPlan plan, int seed, string? seedLabel = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        // Apply BOTH halves to one PRISTINE buffer (non-compounding + composable) before transplanting.
        byte[] pristine = File.ReadAllBytes(exeBackup);
        var parts = new List<string>();
        if (plan.SetWeapon)
        {
            ExePatcher.SetStartingWeapon(pristine, plan.WeaponId);
            parts.Add(plan.WeaponId is { } w ? $"weapon 0x{w:X2}" : "no starting weapon");
        }
        if (plan.CustomSupply is { } items)
        {
            ExePatcher.SetStartingInventory(pristine, items);
            parts.Add("set: " + string.Join(",", items.Select(it => $"0x{it.Id:X2}x{it.Count}")));
        }
        else if (plan.RandomizeSupply)
        {
            var ws = ExePatcher.RandomizeStartingInventory(pristine, seed);
            parts.Add($"randomized (seed {seedLabel ?? seed.ToString()}, {ws.Length} slots)");
        }

        // Transplant ONLY the starting-inventory span into the live exe (additive — preserves any other exe patch).
        byte[] bytes = File.ReadAllBytes(exePath);
        int spanStart = ExePatcher.VaToFileOffset(ExePatcher.StartingInventoryPatchLoVa);
        int spanEnd = ExePatcher.VaToFileOffset(ExePatcher.StartingInventoryPatchHiVa);
        Array.Copy(pristine, spanStart, bytes, spanStart, spanEnd - spanStart);
        File.WriteAllBytes(exePath, bytes);

        string entry = "starting inventory " + (parts.Count == 0 ? "(no change)" : string.Join("; ", parts)) +
                       $" @ 0x{ExePatcher.StartingInventoryInitFnVa:X}";
        // Keyed on the shared prefix so a later starting-inventory patch replaces this line — all modes
        // rewrite the same span, so only one starting-inventory state is active per install.
        const string key = "starting inventory ";
        var manifest = ReadManifest(dataDir) ?? new InstallManifest(seedLabel, DateTime.UtcNow.ToString("o"),
            Array.Empty<string>());
        var repoints = (manifest.ExeRepoints ?? Array.Empty<string>())
            .Where(r => !r.StartsWith(key, StringComparison.Ordinal))
            .Append(entry)
            .ToList();
        manifest = manifest with { ExePatched = true, ExeRepoints = repoints };
        File.WriteAllText(Path.Combine(backupDir, ManifestName),
            JsonSerializer.Serialize(manifest, JsonOpts));

        return new ExePatchResult(exePath, exeBackup, new[] { entry });
    }

    /// <summary>True when a DinoRand overlay is currently <i>applied</i> to this Data folder (the mod is
    /// live). After a <see cref="Restore"/> this is false even though the pristine backup is retained —
    /// use <see cref="HasBackup"/> to test for the backup itself.</summary>
    public static bool IsInstalled(string dataDir) => ReadManifest(dataDir)?.Applied == true;

    /// <summary>True when a pristine backup exists for this Data folder (kept even after a restore, so it
    /// can be reused for future installs).</summary>
    public static bool HasBackup(string dataDir) =>
        Directory.Exists(Path.Combine(dataDir, BackupDirName));

    /// <summary>SHA-256 of a file as an upper-case hex string — the pristine-original fingerprint stored
    /// in the manifest and checked before an overlay or a restore.</summary>
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Resolve the game executable from a path that may be either the <c>DINO.exe</c> itself or a folder
    /// containing it (the same flexibility the UI's game-location box allows). Returns the exe path, or
    /// <c>null</c> if none is found. Used to launch the game ("Play").
    /// </summary>
    public static string? FindGameExe(string pathOrExe, string exeName = ExeName)
    {
        if (string.IsNullOrWhiteSpace(pathOrExe))
            return null;
        try
        {
            if (File.Exists(pathOrExe) &&
                string.Equals(Path.GetFileName(pathOrExe), exeName, StringComparison.OrdinalIgnoreCase))
                return pathOrExe;

            var folder = File.Exists(pathOrExe) ? Path.GetDirectoryName(pathOrExe) : pathOrExe;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return null;

            var direct = Path.Combine(folder, exeName);
            if (File.Exists(direct))
                return direct;
            return Directory.EnumerateFiles(folder, exeName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Read the install manifest, or <c>null</c> if nothing is installed.</summary>
    public static InstallManifest? ReadManifest(string dataDir)
    {
        var manifestPath = Path.Combine(dataDir, BackupDirName, ManifestName);
        if (!File.Exists(manifestPath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<InstallManifest>(
                File.ReadAllText(manifestPath), JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Outcome of an <see cref="GameInstaller.Install"/>.</summary>
public sealed record InstallResult(int BackedUp, int Overlaid, string BackupDir);

/// <summary>Outcome of a <see cref="GameInstaller.Restore"/>. <paramref name="Dc1VertexLiftReapplied"/>
/// is true when the restored stock <c>DINO.exe</c> was re-lifted to match an installed vertex-lifted
/// <c>ddraw.dll</c> (VTXLIFT-RESTORE-PAIRING-RCA).</summary>
public sealed record RestoreResult(int Restored, bool Dc1VertexLiftReapplied = false);

/// <summary>Per-file verdict of <see cref="GameInstaller.VerifyBackups"/>.</summary>
public enum BackupVerifyStatus
{
    /// <summary>Backup matches the live file (and every pristine oracle present).</summary>
    Ok,
    /// <summary>Live file differs but a randomizer install is applied — expected, not judged.</summary>
    Installed,
    /// <summary>Backup differs from the live file while nothing is installed: a poisoned capture,
    /// or an in-place edit that never went through the manifest. Investigate before trusting Restore.</summary>
    Suspect,
    /// <summary>Proven not pristine: the backup contradicts the manifest's recorded hash or a
    /// <c>.dinorand-bak</c> sibling.</summary>
    Poisoned,
    /// <summary>The backup has no live counterpart file.</summary>
    LiveMissing,
}

/// <summary>One <see cref="GameInstaller.VerifyBackups"/> finding. <see cref="Name"/> is the
/// data-dir-relative file name (game-root-relative for loose files, '/'-separated).</summary>
public sealed record BackupVerifyResult(string Name, BackupVerifyStatus Status, string Detail);

/// <summary>One executable patch: give <paramref name="TargetStage"/>'s rooms the enemy set that
/// <paramref name="DonorStage"/>'s rooms use, by repointing the target record's setup-fn ptr.</summary>
public sealed record ExeRepoint(int TargetStage, int DonorStage);

/// <summary>Outcome of a <see cref="GameInstaller.PatchExe"/>: the patched exe, its pristine backup,
/// and a human-readable description of each repoint applied.</summary>
public sealed record ExePatchResult(string ExePath, string BackupPath, IReadOnlyList<string> Repoints);

/// <summary>The combined starting-inventory intent for <see cref="GameInstaller.PatchExeStartingInventory"/>
/// (docs/reference/dc1/items/STARTING-INVENTORY.md). Supply: <see cref="CustomSupply"/> (explicit list) wins over
/// <see cref="RandomizeSupply"/>; both unset ⇒ supply left vanilla. Weapon: set <see cref="SetWeapon"/> to
/// touch the grant — <see cref="WeaponId"/> is the weapon item id (<c>0x01..0x0A</c>) or <c>null</c> for no
/// starting weapon (which the world item pass must then place — beatability coupling).</summary>
public sealed record StartingInventoryPlan(
    bool RandomizeSupply = false,
    IReadOnlyList<(int Id, int Count)>? CustomSupply = null,
    bool SetWeapon = false,
    int? WeaponId = null)
{
    /// <summary>True when the plan changes nothing (no supply mode and no weapon edit) — the installer
    /// can skip it.</summary>
    public bool IsNoOp => !RandomizeSupply && CustomSupply is null && !SetWeapon;
}

/// <summary>What was installed, recorded in <c>.dinorand_backup\manifest.json</c>. <see cref="ExePatched"/>
/// /<see cref="ExeRepoints"/> are set when the executable patch ran, so <see cref="GameInstaller.Restore"/>
/// knows to reverse the exe too. <see cref="OriginalHashes"/> maps each backed-up file name to its pristine
/// SHA-256 (checked before any overlay/restore); <see cref="Applied"/> is true while the mod is overlaid and
/// false after a restore (the backup itself is always retained).</summary>
public sealed record InstallManifest(
    string? Seed, string InstalledUtc, IReadOnlyList<string> Files,
    bool ExePatched = false, IReadOnlyList<string>? ExeRepoints = null,
    IReadOnlyDictionary<string, string>? OriginalHashes = null, bool Applied = true);

/// <summary>Thrown when a backup file's contents no longer match the pristine SHA-256 recorded at backup
/// time — so the installer refuses to overlay onto it, or to restore it over the game, rather than risk an
/// unrecoverable or corrupted Data folder.</summary>
public sealed class BackupIntegrityException : Exception
{
    public BackupIntegrityException(string fileName, string backupPath)
        : base($"Backup integrity check failed for '{fileName}': the backed-up file at '{backupPath}' is " +
               "not the pristine original (hash mismatch). Reinstall the game's original files, then try again.")
    { }
}

/// <summary>Thrown when a room file about to be overlaid has a different Gian entry stride (DC1 16-byte
/// vs DC2 32-byte) than its pristine original — a wrong-container-format rebuild the engine misreads into
/// an out-of-range GPU-resource index, hard-crashing on room load
/// (docs/decisions/dc2/crash-rcas/DC2-ROOM-CONTAINER-STRIDE-CRASH-RCA.md). The installer refuses it, leaving the real file
/// untouched, rather than install an unbootable seed.</summary>
public sealed class ContainerFormatMismatchException : Exception
{
    public ContainerFormatMismatchException(string fileName, bool originalIsDc2)
        : base($"Container-format mismatch for '{fileName}': the pristine original is a " +
               $"{(originalIsDc2 ? "DC2 (32-byte-entry)" : "DC1 (16-byte-entry)")} Gian package but the " +
               "randomized file is not the same format (wrong entry stride). Installing it would crash the " +
               "game on room load — refusing. Re-generate the seed with a clean output directory.")
    { }
}
