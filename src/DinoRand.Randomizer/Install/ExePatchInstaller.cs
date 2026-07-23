using System.Text.Json;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Dc1.Exe;

namespace DinoRand.Randomizer.Install;

internal static class ExePatchInstaller
{
    internal sealed record ComposedExePatch(byte[] Bytes, IReadOnlyList<string> Repoints, int RequestCount);

    private const string BackupDirName = GameInstaller.BackupDirName;
    private const string ExeName = GameInstaller.ExeName;
    private const string LooseBackupSubdir = GameInstaller.LooseBackupSubdir;
    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string GameRoot(string dataDir) => Path.GetDirectoryName(Path.GetFullPath(
        dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
    private static string ExePath(string dataDir) => GameInstaller.ExePath(dataDir);
    private static void EnsureNotDrmProtected(string dataDir) => GameInstaller.EnsureNotDrmProtected(dataDir);
    private static string BackupOnce(string dataDir, string originalPath) =>
        BackupManifestStore.BackupOnce(dataDir, originalPath);
    private static InstallManifest? ReadManifest(string dataDir) => BackupManifestStore.ReadManifest(dataDir);

    internal static IReadOnlyList<ExePatchResult> ApplyExePatchPlan(
        string dataDir, ExePatchPlan plan, string donorDir, string? seed = null)
    {
        ValidatePlanVersion(plan);
        if (plan.Requests.Count == 0) return Array.Empty<ExePatchResult>();

        var exePath = ResolveExeForPatch(dataDir);
        var backupDir = Path.Combine(dataDir, BackupDirName);
        var exeBackup = Path.Combine(backupDir, ExeName);
        var existing = ReadManifest(dataDir);
        byte[] pristine = File.Exists(exeBackup)
            ? File.ReadAllBytes(exeBackup)
            : BackupManifestStore.ReadPristineBytes(exePath);
        if (File.Exists(exeBackup)
            && existing?.OriginalHashes?.TryGetValue(ExeName, out var expected) == true
            && !string.Equals(BackupManifestStore.HashBytes(pristine), expected, StringComparison.OrdinalIgnoreCase))
            throw new BackupIntegrityException(ExeName, exeBackup);

        var composed = ComposeExePatchPlan(pristine, plan, donorDir, dataDir);
        if (composed.RequestCount == 0) return Array.Empty<ExePatchResult>();

        Directory.CreateDirectory(backupDir);
        if (!File.Exists(exeBackup))
            File.WriteAllBytes(exeBackup, pristine);
        File.WriteAllBytes(exePath, composed.Bytes);

        var hashes = new Dictionary<string, string>(
            existing?.OriginalHashes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        {
            [ExeName] = BackupManifestStore.HashBytes(pristine),
        };
        var manifest = (existing ?? new InstallManifest(seed, DateTime.UtcNow.ToString("o"), Array.Empty<string>()))
            with
            {
                Seed = seed ?? existing?.Seed,
                ExePatched = true,
                ExeRepoints = composed.Repoints,
                OriginalHashes = hashes,
                Applied = true,
            };
        File.WriteAllText(Path.Combine(backupDir, ManifestName), JsonSerializer.Serialize(manifest, JsonOpts));
        return new[] { new ExePatchResult(exePath, exeBackup, composed.Repoints) };
    }

    internal static ComposedExePatch ComposeExePatchPlan(
        byte[] pristine, ExePatchPlan plan, string donorDir, string dataDir)
    {
        ValidatePlanVersion(plan);

        var byTarget = new Dictionary<string, ExePatchRequest>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in plan.Requests)
        {
            string key = req.Kind switch
            {
                ExePatchKind.CatSlot => $"cat-slot:{req.Stage}:{req.Category}",
                ExePatchKind.Cat8HitReaction => "cat8-hit-reaction",
                ExePatchKind.RoomEnemySe => $"room-se:{req.Stage}:{req.Room}",
                _ => throw new InvalidOperationException($"unknown exe-patch kind {req.Kind}"),
            };
            if (byTarget.TryGetValue(key, out var previous) && previous != req)
                throw new InvalidOperationException(
                    $"incompatible exe-patch requests target {key}: {previous} conflicts with {req}");
            byTarget[key] = req;
        }

        var bytes = (byte[])pristine.Clone();
        var entries = new List<string>();
        foreach (var req in byTarget.Values
                     .OrderBy(r => r.Kind).ThenBy(r => r.Stage).ThenBy(r => r.Room)
                     .ThenBy(r => r.Category).ThenBy(r => r.DonorStage).ThenBy(r => r.DonorRoom))
        {
            switch (req.Kind)
            {
                case ExePatchKind.CatSlot:
                {
                    uint recordVa = StageAiRecordVa(req.Stage);
                    uint previous = ExePatcher.SetRecordCategoryHandler(bytes, recordVa, req.Category, req.HandlerVa);
                    entries.Add($"record@0x{recordVa:X} cat{req.Category} = 0x{req.HandlerVa:X} " +
                                $"(was 0x{previous:X} @ +0x{req.Category * 4:X})");
                    break;
                }
                case ExePatchKind.Cat8HitReaction:
                {
                    var donorPath = ResolveDonorPath(donorDir, dataDir, req.DonorRoomFile
                        ?? throw new InvalidOperationException("Cat8HitReaction request has no donor room file."));
                    byte[] donorRdt = RoomFile.ReadFromFile(req.DonorStage, req.DonorRoom, donorPath).RdtBuffer;
                    entries.Add(ApplyCat8HitReaction(bytes, donorRdt));
                    break;
                }
                case ExePatchKind.RoomEnemySe:
                {
                    byte[] donorSub = ExePatcher.ExtractRoomDinoSubBlock(bytes, req.DonorStage, req.DonorRoom);
                    if (donorSub.Length == 0)
                        throw new InvalidOperationException(
                            $"donor room st{req.DonorStage}{req.DonorRoom:X2} has no enemy SE records — " +
                            "cannot source the target species' sound set.");
                    var result = ExePatcher.RetargetRoomDinoSe(bytes, req.Stage, req.Room, donorSub);
                    entries.Add($"room enemy SE st{req.Stage}{req.Room:X2} -> donor " +
                                $"st{req.DonorStage}{req.DonorRoom:X2} ({result.RecordsWritten}/{result.Capacity} " +
                                $"dino records @ block 0x{result.TargetBlockVa:X})");
                    break;
                }
            }
        }
        return new ComposedExePatch(bytes, entries, byTarget.Count);
    }

    internal static void ValidatePlanVersion(ExePatchPlan plan)
    {
        if (plan.Version != ExePatchPlan.CurrentVersion)
            throw new InvalidOperationException(
                $"exe-patch-plan version {plan.Version} is unsupported (expected {ExePatchPlan.CurrentVersion}).");
    }

    private static string ApplyCat8HitReaction(byte[] bytes, byte[] donorRdt)
    {
        int off = ExePatcher.Cat8ReactionDonorRdtOffset, len = ExePatcher.Cat8ReactionStreamBytes;
        int recSize = ExePatcher.HitDescriptorRecordSize;
        if (off + len > donorRdt.Length)
            throw new InvalidOperationException(
                $"donor RDT (0x{donorRdt.Length:X} B) is too small for the cat-8 reaction stream at " +
                $"0x{off:X}+0x{len:X} — wrong donor room (need a normal Theri room like st603).");
        for (int i = 0; i < 23; i++)
        {
            int r = off + i * recSize;
            byte state = donorRdt[r + 0x10], count = donorRdt[r + 0xe];
            if (state > 7 || count > 0x10)
                throw new InvalidOperationException(
                    $"donor RDT offset 0x{r:X} is not a clean cat-8 reaction descriptor " +
                    $"(state=0x{state:X2}, cnt=0x{count:X2}) — the donor is not a normal Theri room (st603).");
        }
        var stream = donorRdt.AsSpan(off, len).ToArray();
        uint[] repointed = ExePatcher.RedirectCat8HitReaction(bytes, stream);
        ExePatcher.InstallWalkerNullGuard(bytes);
        ExePatcher.InstallRenderModelGuard(bytes);
        return $"cat8 hit reaction -> cave 0x{ExePatcher.HitDescriptorCaveVa:X} " +
               $"(0x{len:X} B from st603 RDT 0x{off:X}; {repointed.Length} entries) + walker null-guard " +
               $"0x{ExePatcher.WalkerVa:X}->0x{ExePatcher.WalkerCaveVa:X} + render-model guard " +
               $"0x{ExePatcher.RenderTransformHookVa:X}->0x{ExePatcher.RenderGuardCaveVa:X}";
    }

    internal static uint StageAiRecordVa(int stage) => stage switch
    {
        1 => ExePatcher.Stage1AiRecordVa,
        2 => ExePatcher.Stage2AiRecordVa,
        _ => throw new InvalidOperationException(
            $"stage {stage} has no verified-free installed AI record for a cat-slot patch (only 1 and 2).")
    };

    internal static string ResolveDonorPath(string donorDir, string dataDir, string fileName)
    {
        var inMod = Path.Combine(donorDir, fileName);
        if (File.Exists(inMod)) return inMod;
        var inData = Path.Combine(dataDir, fileName);
        if (File.Exists(inData)) return inData;
        foreach (var p in Directory.EnumerateFiles(dataDir))
            if (string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase)) return p;
        throw new FileNotFoundException($"cat-8 reaction donor room '{fileName}' not found in mod or data dir.");
    }

    internal static string ResolveExeForPatch(string dataDir)
    {
        var exePath = ExePath(dataDir);
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"game executable not found beside Data: {exePath}", exePath);
        }
        EnsureNotDrmProtected(dataDir); // TOS + patch-offset validity: never write a DRM-wrapped exe
        return exePath;
    }

    internal static ExePatchResult PatchExe(string dataDir, IReadOnlyList<ExeRepoint> repoints, string? seed = null)
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

    internal static ExePatchResult PatchExeCatSlot(string dataDir, uint recordVa, int category, uint handlerVa,
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

    internal static ExePatchResult PatchExeCat8HitDescriptors(string dataDir, byte[] hostRdt, string? seed = null)
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

    internal static ExePatchResult PatchExeCat8HitReaction(string dataDir, byte[] donorRdt, string? seed = null)
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

    internal static ExePatchResult PatchExeWalkerNullGuard(string dataDir, string? seed = null)
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

    internal static ExePatchResult PatchExeItemPickupCancelFix(string dataDir, string? seed = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        // The fix is a small additive hook/cave. Edit the live image so it composes with other
        // already-installed EXE patches; the one-time backup still gives Restore a pristine source.
        byte[] bytes = File.ReadAllBytes(exePath);
        ExePatcher.InstallItemPickupCancelFix(bytes);
        File.WriteAllBytes(exePath, bytes);

        string entry = $"item pickup cancel/failure close: 0x{ExePatcher.ItemPickupSessionCloseVa:X}->" +
                       $"0x{ExePatcher.ItemPickupCancelCaveVa:X} clears " +
                       $"{ExePatcher.ItemPickupPendingFlagGroup}:0x{ExePatcher.ItemPickupPendingFlagIndex:X2}";
        const string key = "item pickup cancel/failure close";
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

    internal static ExePatchResult PatchExeRoomEnemySe(
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

    internal static ExePatchResult PatchExeShuffleBgm(string dataDir, int seed, string? seedLabel = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        // Shuffle from the PRISTINE catalog (non-compounding) → `pristine` now holds the shuffled catalog region.
        byte[] pristine = File.ReadAllBytes(exeBackup);
        var patchPlan = Dc1ExePatchPlanner.PlanBgmCatalog(pristine, seed);
        var entries = ExePatcher.ApplyBgmCatalogPlan(pristine, patchPlan.BgmSourceIds!);

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

    internal static ExePatchResult PatchExeDoorSkip(string dataDir, string? seedLabel = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        byte[] bytes = File.ReadAllBytes(exePath);
        ExePatcher.ApplyDoorSkip(bytes); // idempotent + guarded against an unexpected build
        File.WriteAllBytes(exePath, bytes);

        string entry = $"door skip (experimental): state-1 leaf-sweep skipped @0x{ExePatcher.DoorSkipSwingVa:X}, " +
                       $"bg hold {ExePatcher.DoorHoldPristine}->{ExePatcher.DoorHoldPatched} frames @0x{ExePatcher.DoorHoldGateVa:X}";
        const string key = "door skip";
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

    internal static ExePatchResult PatchExeFastForwardCutscenes(string dataDir, string? seedLabel = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        byte[] bytes = File.ReadAllBytes(exePath);
        ExePatcher.ApplyCutsceneFastForward(bytes); // idempotent + guarded against an unexpected build
        File.WriteAllBytes(exePath, bytes);

        string entry = $"fast-forward cutscenes (experimental): SCD-VM tick multiplier hook @0x{ExePatcher.CutsceneFfHookVa:X} " +
                       $"-> cave @0x{ExePatcher.CutsceneFfCaveVa:X}";
        const string key = "fast-forward cutscenes";
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

    internal static ExePatchResult PatchExeSyncPuzzleCodes(string dataDir, int seed, string? seedLabel = null)
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

    internal static ExePatchResult PatchExeShuffleBoxes(string dataDir, int seed, string? seedLabel = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        // Shuffle from the PRISTINE table (non-compounding) → `pristine` now holds the shuffled blocks.
        byte[] pristine = File.ReadAllBytes(exeBackup);
        var patchPlan = Dc1ExePatchPlanner.PlanEmergencyBoxShuffle(seed);
        var entries = ExePatcher.ApplyEmergencyBoxShufflePlan(
            pristine, patchPlan.EmergencyBoxSourceSlots!);

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

    internal static ExePatchResult PatchExeRerollBoxes(string dataDir, int seed, string? seedLabel = null)
    {
        var exePath = ResolveExeForPatch(dataDir);

        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var exeBackup = Path.Combine(backupDir, ExeName);
        if (!File.Exists(exeBackup))
            File.Copy(exePath, exeBackup); // capture the pristine exe once (for --restore)

        // Reroll from the PRISTINE table (non-compounding) → `pristine` now holds the rerolled blocks.
        byte[] pristine = File.ReadAllBytes(exeBackup);
        var patchPlan = Dc1ExePatchPlanner.PlanEmergencyBoxReroll(pristine, seed);
        var entries = ExePatcher.ApplyEmergencyBoxRerollPlan(pristine, patchPlan.EmergencyBoxRecords!);

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

    internal static ExePatchResult PatchExeStartingInventory(
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
            ExePatcher.ValidateStartingInventory(pristine);
            var patchPlan = Dc1ExePatchPlanner.PlanStartingInventory(seed);
            var ws = ExePatcher.ApplyStartingInventoryPlan(pristine, patchPlan.StartingInventoryBlocks!);
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
}
