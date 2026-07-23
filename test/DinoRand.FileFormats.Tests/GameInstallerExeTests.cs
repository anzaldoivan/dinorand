using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Tests for the executable backup/patch/restore contract added in
/// <c>docs/dc1/EXE-PATCH-PER-ROOM-PLAN.md</c> Decision 3: <see cref="GameInstaller.PatchExe"/> repoints a
/// stage's enemy-set record, backs the exe up once, and <see cref="GameInstaller.Restore"/> reverses
/// it — putting <c>DINO.exe</c> back in the game root (beside <c>Data\</c>), not into <c>Data\</c>.
/// Runs entirely on a synthetic game tree in a temp dir; no real install needed.
/// </summary>
public class GameInstallerExeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dinorand_test_" + Guid.NewGuid().ToString("N"));
    private string DataDir => Path.Combine(_root, "Data");
    private string ExePath => Path.Combine(_root, GameInstaller.ExeName);

    public GameInstallerExeTests()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllBytes(ExePath, BuildSyntheticExe());
        File.WriteAllBytes(Path.Combine(DataDir, "st101.dat"), new byte[] { 1, 2, 3, 4 });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>An exe large enough to cover the file-backed window, seeded with the two known
    /// per-stage setup-fn pointers and the 10 cat-8 hit/death descriptor table entries (file-form,
    /// pointing into <see cref="SyntheticHostRdt"/>) at their verified offsets.</summary>
    private static byte[] BuildSyntheticExe()
    {
        var exe = new byte[ExePatcher.FileBackedRvaHi];
        ExePatcher.WriteUInt32AtVa(exe, ExePatcher.SetupFnFieldVa(1), ExePatcher.SetupFnBasicRaptor);
        ExePatcher.WriteUInt32AtVa(exe, ExePatcher.SetupFnFieldVa(2), ExePatcher.SetupFnStage2);
        for (int r = 0; r < ExePatcher.HitDescriptorTotalRecords; r++)
        {
            uint entryVa = r < ExePatcher.HitDescriptorIndexCount
                ? ExePatcher.Cat8HitTable17Va + (uint)r * 4
                : ExePatcher.Cat8HitTable15Va + (uint)(r - ExePatcher.HitDescriptorIndexCount) * 4;
            ExePatcher.WriteUInt32AtVa(exe, entryVa, Cat8HitDescriptors.RdtPsxBase + RecordRdtOffset(r));
        }
        return exe;
    }

    private static uint RecordRdtOffset(int r) => 0x100u + (uint)r * 0x20u;
    private static byte RecordByte(int r, int b) => (byte)(0x40 + r * ExePatcher.HitDescriptorRecordSize + b);

    /// <summary>A host RDT carrying recognizable bytes at the offsets the seeded table entries point to.</summary>
    private static byte[] SyntheticHostRdt()
    {
        var rdt = new byte[0x1000];
        for (int r = 0; r < ExePatcher.HitDescriptorTotalRecords; r++)
            for (int b = 0; b < ExePatcher.HitDescriptorRecordSize; b++)
                rdt[RecordRdtOffset(r) + b] = RecordByte(r, b);
        return rdt;
    }

    /// <summary>The 10 records concatenated, in table17[0..4]+table15[0..4] order.</summary>
    private static byte[] ExpectedRecords()
    {
        var recs = new byte[ExePatcher.HitDescriptorTotalRecords * ExePatcher.HitDescriptorRecordSize];
        for (int r = 0; r < ExePatcher.HitDescriptorTotalRecords; r++)
            for (int b = 0; b < ExePatcher.HitDescriptorRecordSize; b++)
                recs[r * ExePatcher.HitDescriptorRecordSize + b] = RecordByte(r, b);
        return recs;
    }

    [Fact]
    public void ExePath_ResolvesBesideData()
        => Assert.Equal(ExePath, GameInstaller.ExePath(DataDir));

    [Fact]
    public void PatchExe_RepointsTargetToDonor_AndBacksUpOnce()
    {
        var result = GameInstaller.PatchExe(DataDir, new[] { new ExeRepoint(2, 1) });

        // The pristine exe was captured, and stage 2's record now points at stage 1's setup fn.
        Assert.True(File.Exists(result.BackupPath));
        var patched = File.ReadAllBytes(ExePath);
        Assert.Equal(ExePatcher.SetupFnBasicRaptor, ExePatcher.ReadSetupFn(patched, 2));
        // The backup is still pristine (stage 2's original fn).
        var backup = File.ReadAllBytes(result.BackupPath);
        Assert.Equal(ExePatcher.SetupFnStage2, ExePatcher.ReadSetupFn(backup, 2));

        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.NotNull(manifest);
        Assert.True(manifest!.ExePatched);
        Assert.NotNull(manifest.ExeRepoints);
        Assert.Single(manifest.ExeRepoints!);
    }

    [Fact]
    public void PatchExe_IsNonCompounding_EditsFromPristine()
    {
        // Patch twice with different repoints; the second must start from the pristine backup, not the
        // already-patched exe, so re-running never compounds.
        GameInstaller.PatchExe(DataDir, new[] { new ExeRepoint(2, 1) });
        GameInstaller.PatchExe(DataDir, new[] { new ExeRepoint(1, 2) });

        var exe = File.ReadAllBytes(ExePath);
        // Second run patched stage 1 from pristine; stage 2 is back to its pristine value.
        Assert.Equal(ExePatcher.SetupFnStage2, ExePatcher.ReadSetupFn(exe, 1));
        Assert.Equal(ExePatcher.SetupFnStage2, ExePatcher.ReadSetupFn(exe, 2));
    }

    /// <summary>Write the full stock JP-master keypad table (all rows, both region halves) into the
    /// synthetic exe — <see cref="DinoRand.FileFormats.Stage.Dc1PuzzleCodeSync.VerifyStockKeypadTable"/>
    /// requires it before any scramble.</summary>
    private static void WriteStockKeypadTable(byte[] exe)
    {
        foreach (var lk in DinoRand.FileFormats.Stage.Dc1PuzzleCodeSync.Family)
            foreach (int copy in MgmtOfficeSafeCode.JpRow0FileOffsets)
                foreach (bool us in new[] { false, true })
                {
                    int fo = MgmtOfficeSafeCode.RowFileOffset(copy, lk.Row, us);
                    int[] stock = us ? lk.UsDigits : lk.OriginalDigits;
                    for (int i = 0; i < MgmtOfficeSafeCode.DigitCount; i++)
                        exe[fo + i] = MgmtOfficeSafeCode.EncodeDigit(stock[i]);
                }
    }

    [Fact]
    public void PatchExeSyncPuzzleCodes_WritesSeedCodes_AndRestoreReverses()
    {
        // Needs an inline-text (GOG European) install for the document rooms; the temp tree then
        // classifies GogInlineText and routes through the RDT document lever.
        string? srcData = Dc1EditionDetectorTests.FindInlineTextDataDir();
        if (srcData is null) return; // no inline-text install in this checkout — skip silently

        // Seed the synthetic exe's code table with the full shipped stock codes.
        var exe0 = File.ReadAllBytes(ExePath);
        WriteStockKeypadTable(exe0);
        File.WriteAllBytes(ExePath, exe0);

        var docFiles = new List<string>();
        foreach (var lk in DinoRand.FileFormats.Stage.Dc1PuzzleCodeSync.Family)
        {
            if (lk.DocFile is null) continue;
            var src = Path.Combine(srcData, lk.DocFile);
            if (File.Exists(src)) { File.Copy(src, Path.Combine(DataDir, lk.DocFile)); docFiles.Add(lk.DocFile); }
        }

        var pristineExe = File.ReadAllBytes(ExePath);
        var pristineDocs = docFiles.ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(DataDir, f)));

        var res = GameInstaller.PatchExeSyncPuzzleCodes(DataDir, seed: 777, seedLabel: "777");
        Assert.Equal(6, res.Repoints.Count);

        // Each exe row now decodes to the seed-derived code (both region halves).
        var exe = File.ReadAllBytes(ExePath);
        foreach (var lk in DinoRand.FileFormats.Stage.Dc1PuzzleCodeSync.Family)
        {
            int[] want = DinoRand.FileFormats.Stage.Dc1PuzzleCodeSync.DeriveRowCode(777, lk.Row);
            Assert.Equal(want, MgmtOfficeSafeCode.ReadRow(exe, lk.Row, us: false));
            Assert.Equal(want, MgmtOfficeSafeCode.ReadRow(exe, lk.Row, us: true));
        }

        // Any real document rooms present were rewritten (and differ from pristine).
        foreach (var f in docFiles)
            Assert.NotEqual(pristineDocs[f], File.ReadAllBytes(Path.Combine(DataDir, f)));

        // Manifest records the sync.
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.Contains(manifest!.ExeRepoints!, r => r.StartsWith("puzzle code sync", StringComparison.Ordinal));

        // Restore reverses the exe AND every document, byte-identical.
        GameInstaller.Restore(DataDir);
        Assert.Equal(pristineExe, File.ReadAllBytes(ExePath));
        foreach (var f in docFiles)
            Assert.Equal(pristineDocs[f], File.ReadAllBytes(Path.Combine(DataDir, f)));
    }

    private static string? FindGameFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, relative);
            if (File.Exists(p)) return p;
            if (File.Exists(Path.Combine(dir.FullName, "DinoRand.sln"))) break;
        }
        return null;
    }

    /// <summary>The real REbirth ddraw.dll (version-lock verified), or null to skip.</summary>
    private static string? FindRebirthDdraw()
    {
        string? data = Dc1EditionDetectorTests.FindGameDir("english");
        if (data is null) return null;
        string p = Path.Combine(Path.GetDirectoryName(data)!, "ddraw.dll");
        return Dc1EditionDetector.IsRebirthDdraw(p) ? p : null;
    }

    /// <summary>Unknown edition (no REbirth DLL, no inline Latin text) must refuse loudly — an EXE-only
    /// scramble would leave the document showing a code the keypad rejects.</summary>
    [Fact]
    public void PatchExeSyncPuzzleCodes_UnknownEdition_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GameInstaller.PatchExeSyncPuzzleCodes(DataDir, seed: 1));
        Assert.Contains("classify", ex.Message);
        // and nothing was written
        Assert.False(File.Exists(Path.Combine(DataDir, GameInstaller.BackupDirName, GameInstaller.ExeName)));
    }

    /// <summary>REbirth-Japanese: the JP document text has no rewrite lever, so the whole scramble is
    /// skipped (codes stay stock — displayed == checked trivially). Real-DLL; skips if absent.</summary>
    [Fact]
    public void PatchExeSyncPuzzleCodes_RebirthJapanese_SkipsWithoutWriting()
    {
        string? ddraw = FindRebirthDdraw();
        if (ddraw is null) return;
        File.Copy(ddraw, Path.Combine(_root, "ddraw.dll"));
        File.WriteAllText(Path.Combine(_root, "config.ini"), "[DLL]\nJapaneseEnable = 1\n");

        byte[] exeBefore = File.ReadAllBytes(ExePath);
        var res = GameInstaller.PatchExeSyncPuzzleCodes(DataDir, seed: 1);
        Assert.Contains(res.Repoints, r => r.Contains("SKIPPED", StringComparison.Ordinal));
        Assert.Equal(exeBefore, File.ReadAllBytes(ExePath)); // untouched
    }

    /// <summary>REbirth-English end-to-end: exe rows scrambled AND ddraw.dll's embedded diff archive
    /// rebuilt with the same codes; Restore reverses both byte-identical. Real-DLL; skips if absent.</summary>
    [Fact]
    public void PatchExeSyncPuzzleCodes_RebirthEnglish_PatchesDdrawAndRestores()
    {
        string? ddraw = FindRebirthDdraw();
        if (ddraw is null) return;
        string liveDdraw = Path.Combine(_root, "ddraw.dll");
        File.Copy(ddraw, liveDdraw);
        File.WriteAllText(Path.Combine(_root, "config.ini"), "[DLL]\nJapaneseEnable = 0\n");

        var exe0 = File.ReadAllBytes(ExePath);
        WriteStockKeypadTable(exe0);
        File.WriteAllBytes(ExePath, exe0);
        byte[] ddraw0 = File.ReadAllBytes(liveDdraw);

        var res = GameInstaller.PatchExeSyncPuzzleCodes(DataDir, seed: 777);
        Assert.Contains(res.Repoints, r => r.Contains("ddraw.dll", StringComparison.Ordinal));

        // exe rows hold the seed codes (both halves), and the DLL carries the text patch.
        var exe = File.ReadAllBytes(ExePath);
        foreach (var lk in DinoRand.FileFormats.Stage.Dc1PuzzleCodeSync.Family)
        {
            int[] want = DinoRand.FileFormats.Stage.Dc1PuzzleCodeSync.DeriveRowCode(777, lk.Row);
            Assert.Equal(want, MgmtOfficeSafeCode.ReadRow(exe, lk.Row, us: false));
            Assert.Equal(want, MgmtOfficeSafeCode.ReadRow(exe, lk.Row, us: true));
        }
        Assert.True(RebirthTextPatcher.IsPuzzleCodeTextPatched(File.ReadAllBytes(liveDdraw)));

        // Re-running with another seed re-derives from the pristine backup (non-compounding).
        GameInstaller.PatchExeSyncPuzzleCodes(DataDir, seed: 778);
        Assert.True(RebirthTextPatcher.IsPuzzleCodeTextPatched(File.ReadAllBytes(liveDdraw)));

        // Restore puts the pristine ddraw.dll (loose backup) and exe back, byte-identical.
        GameInstaller.Restore(DataDir);
        Assert.Equal(exe0, File.ReadAllBytes(ExePath));
        Assert.Equal(ddraw0, File.ReadAllBytes(liveDdraw));
    }

    [Fact]
    public void Restore_PutsExeBackInGameRoot_ByteIdentical()
    {
        var pristine = File.ReadAllBytes(ExePath);
        GameInstaller.PatchExe(DataDir, new[] { new ExeRepoint(2, 1) });
        Assert.NotEqual(pristine, File.ReadAllBytes(ExePath)); // changed

        var restore = GameInstaller.Restore(DataDir);

        Assert.True(restore.Restored >= 1);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));            // exe restored, byte-identical
        Assert.False(File.Exists(Path.Combine(DataDir, GameInstaller.ExeName))); // never dumped into Data\
        Assert.True(Directory.Exists(Path.Combine(DataDir, GameInstaller.BackupDirName))); // backup is kept (never deleted) for reuse
    }

    [Fact]
    public void PatchExe_MergesWithExistingRoomManifest()
    {
        // A prior room install writes a manifest with Files; PatchExe must preserve it.
        var modDir = Path.Combine(_root, "mod");
        Directory.CreateDirectory(modDir);
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 9, 9, 9, 9 });
        GameInstaller.Install(DataDir, modDir, seed: "123");

        GameInstaller.PatchExe(DataDir, new[] { new ExeRepoint(2, 1) });

        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.NotNull(manifest);
        Assert.True(manifest!.ExePatched);
        Assert.Contains("st101.dat", manifest.Files);   // room install record preserved
        Assert.Equal("123", manifest.Seed);
    }

    [Fact]
    public void PatchExe_EmptyRepoints_Throws()
        => Assert.Throws<ArgumentException>(() => GameInstaller.PatchExe(DataDir, Array.Empty<ExeRepoint>()));

    // ---- cat-8 hit/death descriptor patch (defect B) ----

    [Fact]
    public void PatchExeCat8HitDescriptors_WritesCave_RepointsTables_RecordsManifest()
    {
        var result = GameInstaller.PatchExeCat8HitDescriptors(DataDir, SyntheticHostRdt());

        Assert.True(File.Exists(result.BackupPath)); // pristine captured
        var exe = File.ReadAllBytes(ExePath);

        // The cave holds the 10 extracted records, and both tables point at consecutive cave records.
        int caveOff = ExePatcher.VaToFileOffset(ExePatcher.HitDescriptorCaveVa);
        Assert.Equal(ExpectedRecords(), exe.Skip(caveOff).Take(ExpectedRecords().Length).ToArray());
        for (int r = 0; r < ExePatcher.HitDescriptorTotalRecords; r++)
        {
            uint entryVa = r < ExePatcher.HitDescriptorIndexCount
                ? ExePatcher.Cat8HitTable17Va + (uint)r * 4
                : ExePatcher.Cat8HitTable15Va + (uint)(r - ExePatcher.HitDescriptorIndexCount) * 4;
            uint caveVa = ExePatcher.HitDescriptorCaveVa + (uint)r * (uint)ExePatcher.HitDescriptorRecordSize;
            Assert.Equal(caveVa, ExePatcher.ReadUInt32AtVa(exe, entryVa));
        }

        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.NotNull(manifest);
        Assert.True(manifest!.ExePatched);
        Assert.Contains(manifest.ExeRepoints!, e => e.StartsWith("cat8 hit/death descriptors"));
    }

    [Fact]
    public void PatchExeCat8HitDescriptors_IsIdempotent_ReExtractsFromPristineBackup()
    {
        // First run repoints the live tables to the cave (non-file-form). A second run must still succeed
        // by extracting from the PRISTINE backup, and produce a byte-identical exe + a single manifest entry.
        GameInstaller.PatchExeCat8HitDescriptors(DataDir, SyntheticHostRdt());
        var afterFirst = File.ReadAllBytes(ExePath);

        GameInstaller.PatchExeCat8HitDescriptors(DataDir, SyntheticHostRdt());
        Assert.Equal(afterFirst, File.ReadAllBytes(ExePath));

        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.Single(manifest!.ExeRepoints!, e => e.StartsWith("cat8 hit/death descriptors"));
    }

    [Fact]
    public void PatchExeCat8HitDescriptors_ComposesWithCatSlotPatch()
    {
        // The slot patch and the descriptor patch touch disjoint regions; applying both must preserve each.
        GameInstaller.PatchExeCatSlot(DataDir, ExePatcher.Stage1AiRecordVa, 8, ExePatcher.TheriCat8HandlerVa);
        GameInstaller.PatchExeCat8HitDescriptors(DataDir, SyntheticHostRdt());

        var exe = File.ReadAllBytes(ExePath);
        Assert.Equal(ExePatcher.TheriCat8HandlerVa,
            ExePatcher.ReadUInt32AtVa(exe, ExePatcher.Stage1AiRecordVa + 8 * 4)); // slot intact
        Assert.Equal(ExePatcher.HitDescriptorCaveVa,
            ExePatcher.ReadUInt32AtVa(exe, ExePatcher.Cat8HitTable17Va)); // descriptor repoint intact
    }

    [Fact]
    public void PatchExeCat8HitDescriptors_Restore_ReversesToByteIdentical()
    {
        var pristine = File.ReadAllBytes(ExePath);
        GameInstaller.PatchExeCat8HitDescriptors(DataDir, SyntheticHostRdt());
        Assert.NotEqual(pristine, File.ReadAllBytes(ExePath));

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
    }

    // ---- corrected defect-B fix: cat-8 hit-REACTION stream patch (live-verified) ----

    /// <summary>A donor RDT with a valid (walk-safe) reaction descriptor stream at
    /// <see cref="ExePatcher.Cat8ReactionDonorRdtOffset"/> — state ≤ 7, walk-count 0, like native st603.</summary>
    private static byte[] SyntheticDonorRdt()
    {
        int off = ExePatcher.Cat8ReactionDonorRdtOffset, len = ExePatcher.Cat8ReactionStreamBytes;
        var rdt = new byte[off + len + 0x100];
        for (int i = 0; i < len; i++)
        {
            int rec = i / ExePatcher.HitDescriptorRecordSize, b = i % ExePatcher.HitDescriptorRecordSize;
            byte v = (byte)(0x30 + ((rec * 7 + b) % 0x50));        // recognizable, non-zero
            if (b == 0x0e) v = 0;                                  // walk-count 0 (crash-safe)
            if (b == 0x10) v = (byte)(rec % 2 == 0 ? 3 : 5);       // state ≤ 7 (valid)
            rdt[off + i] = v;
        }
        return rdt;
    }

    [Fact]
    public void PatchExeCat8HitReaction_WritesCave_RepointsAllSeededTableEntries_RecordsManifest()
    {
        var donor = SyntheticDonorRdt();
        var result = GameInstaller.PatchExeCat8HitReaction(DataDir, donor);

        Assert.True(File.Exists(result.BackupPath));
        var exe = File.ReadAllBytes(ExePath);

        // Cave holds the donor's 0x3C0E0 stream verbatim.
        int caveOff = ExePatcher.VaToFileOffset(ExePatcher.HitDescriptorCaveVa);
        var expected = donor.Skip(ExePatcher.Cat8ReactionDonorRdtOffset).Take(ExePatcher.Cat8ReactionStreamBytes).ToArray();
        Assert.Equal(expected, exe.Skip(caveOff).Take(expected.Length).ToArray());

        // Every file-form descriptor entry across the whole table region now points at the cave; the synthetic
        // exe seeded the 10 cat-8 descriptor entries (0x6639FC[0..4] + 0x663A10[0..4]) at the verified offsets.
        for (uint va = ExePatcher.Cat8ReactionTableLoVa; va < ExePatcher.Cat8ReactionTableHiVa; va += 4)
        {
            uint v = ExePatcher.ReadUInt32AtVa(exe, va);
            Assert.True(v == ExePatcher.HitDescriptorCaveVa || v == 0, // repointed, or untouched zero-slack
                $"entry 0x{va:X} = 0x{v:X8} unexpectedly not cave/zero");
        }
        Assert.Equal(ExePatcher.HitDescriptorCaveVa, ExePatcher.ReadUInt32AtVa(exe, ExePatcher.Cat8HitTable17Va));
        Assert.Equal(ExePatcher.HitDescriptorCaveVa, ExePatcher.ReadUInt32AtVa(exe, ExePatcher.Cat8HitTable15Va));

        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.True(manifest!.ExePatched);
        Assert.Contains(manifest.ExeRepoints!, e => e.StartsWith("cat8 hit reaction"));
    }

    [Fact]
    public void PatchExeCat8HitReaction_ComposesWithCatSlotPatch_AndIsIdempotent()
    {
        GameInstaller.PatchExeCatSlot(DataDir, ExePatcher.Stage1AiRecordVa, 8, ExePatcher.TheriCat8HandlerVa);
        GameInstaller.PatchExeCat8HitReaction(DataDir, SyntheticDonorRdt());
        var afterFirst = File.ReadAllBytes(ExePath);
        GameInstaller.PatchExeCat8HitReaction(DataDir, SyntheticDonorRdt());

        Assert.Equal(afterFirst, File.ReadAllBytes(ExePath)); // idempotent re-apply
        Assert.Equal(ExePatcher.TheriCat8HandlerVa,
            ExePatcher.ReadUInt32AtVa(afterFirst, ExePatcher.Stage1AiRecordVa + 8 * 4)); // slot intact
        Assert.Single(GameInstaller.ReadManifest(DataDir)!.ExeRepoints!, e => e.StartsWith("cat8 hit reaction"));
    }

    [Fact]
    public void PatchExeCat8HitReaction_Restore_ReversesToByteIdentical()
    {
        var pristine = File.ReadAllBytes(ExePath);
        GameInstaller.PatchExeCat8HitReaction(DataDir, SyntheticDonorRdt());
        Assert.NotEqual(pristine, File.ReadAllBytes(ExePath));

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void PatchExeCat8HitReaction_RejectsGarbageDonor()
    {
        // A donor whose 0x3C0E0 offset is motion garbage (state > 7) is refused — the st603-vs-st605 guard.
        var donor = SyntheticDonorRdt();
        donor[ExePatcher.Cat8ReactionDonorRdtOffset + 0x10] = 0xF9; // record 0 state out of range
        Assert.Throws<InvalidOperationException>(() => GameInstaller.PatchExeCat8HitReaction(DataDir, donor));
    }

    [Fact]
    public void PatchExeCat8HitReaction_RejectsTooSmallDonor()
        => Assert.Throws<InvalidOperationException>(
            () => GameInstaller.PatchExeCat8HitReaction(DataDir, new byte[0x1000]));

    // ---- standalone walker null-guard (cross-species swarm path) ----

    [Fact]
    public void PatchExeWalkerNullGuard_InstallsJmpAndCave_BacksUpOnce_RecordsManifest()
    {
        var result = GameInstaller.PatchExeWalkerNullGuard(DataDir);

        Assert.True(File.Exists(result.BackupPath)); // pristine captured once
        var exe = File.ReadAllBytes(ExePath);
        // The walker site now jmp's (E9) to the cave, and the cave is non-zero (the 37-byte stub).
        Assert.Equal(0xE9, exe[ExePatcher.VaToFileOffset(ExePatcher.WalkerVa)]);
        int caveOff = ExePatcher.VaToFileOffset(ExePatcher.WalkerCaveVa);
        Assert.Equal(0x55, exe[caveOff]); // push ebp — first byte of the guarded reimplementation
        // The render-model guard is co-installed: hook site jmp's (E9) and the cave starts with `mov ecx,[ebp-0x10]`.
        Assert.Equal(0xE9, exe[ExePatcher.VaToFileOffset(ExePatcher.RenderTransformHookVa)]);
        Assert.Equal(0x8B, exe[ExePatcher.VaToFileOffset(ExePatcher.RenderGuardCaveVa)]);

        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.True(manifest!.ExePatched);
        Assert.Contains(manifest.ExeRepoints!, e => e.StartsWith("crash guards"));
    }

    [Fact]
    public void PatchExeWalkerNullGuard_IsIdempotent_AndComposesWithCatSlotPatch()
    {
        // The swarm path applies a cat5 slot patch then the walker guard; both must hold and re-apply cleanly.
        GameInstaller.PatchExeCatSlot(DataDir, ExePatcher.Stage1AiRecordVa, 5, ExePatcher.SwarmCat5HandlerVa);
        GameInstaller.PatchExeWalkerNullGuard(DataDir);
        var afterFirst = File.ReadAllBytes(ExePath);
        GameInstaller.PatchExeWalkerNullGuard(DataDir);

        Assert.Equal(afterFirst, File.ReadAllBytes(ExePath)); // idempotent re-apply
        Assert.Equal(ExePatcher.SwarmCat5HandlerVa,
            ExePatcher.ReadUInt32AtVa(afterFirst, ExePatcher.Stage1AiRecordVa + 5 * 4)); // cat5 slot intact
        Assert.Single(GameInstaller.ReadManifest(DataDir)!.ExeRepoints!, e => e.StartsWith("crash guards"));
    }

    [Fact]
    public void PatchExeWalkerNullGuard_Restore_ReversesToByteIdentical()
    {
        var pristine = File.ReadAllBytes(ExePath);
        GameInstaller.PatchExeWalkerNullGuard(DataDir);
        Assert.NotEqual(pristine, File.ReadAllBytes(ExePath));

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void PatchExeItemPickupCancelFix_InstallsAndRestoresByteIdentically()
    {
        var pristine = File.ReadAllBytes(ExePath);
        new byte[] { 0x55, 0x8B, 0xEC, 0x6A, 0x00 }
            .CopyTo(pristine, ExePatcher.VaToFileOffset(ExePatcher.ItemPickupSessionCloseVa));
        File.WriteAllBytes(ExePath, pristine);

        var result = GameInstaller.PatchExeItemPickupCancelFix(DataDir, seed: "pickup-test");

        Assert.True(File.Exists(result.BackupPath));
        var patched = File.ReadAllBytes(ExePath);
        Assert.True(ExePatcher.IsItemPickupCancelFixApplied(patched));
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.Contains(manifest!.ExeRepoints!,
            entry => entry.StartsWith("item pickup cancel/failure close", StringComparison.Ordinal));

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstallDc1_ItemPickupCloseFix_IsInstalledByDefault(bool randomizeItems)
    {
        int hook = ExePatcher.VaToFileOffset(ExePatcher.ItemPickupSessionCloseVa);
        var pristine = File.ReadAllBytes(ExePath);
        new byte[] { 0x55, 0x8B, 0xEC, 0x6A, 0x00 }.CopyTo(pristine, hook);
        File.WriteAllBytes(ExePath, pristine);

        string modDir = Path.Combine(_root, "mod");
        Directory.CreateDirectory(modDir);
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 4, 3, 2, 1 });

        var result = RandomizationInstallCoordinator.InstallDc1(
            DataDir, modDir, new Seed(123),
            new RandomizerConfig { RandomizeItems = randomizeItems },
            () => null, _ => { }, ex => throw ex);

        Assert.NotNull(result);
        Assert.True(ExePatcher.IsItemPickupCancelFixApplied(File.ReadAllBytes(ExePath)));
    }

    // ---- BGM catalog shuffle (the music randomizer; docs/dc1/BGM-SYSTEM.md §4) ----

    private static uint BgmFlagsFor(int id) => (id % 3) switch { 0 => 0x2Au, 1 => 0x08u, _ => 0x0Au };
    private static uint BgmNameVa(int id) => 0x00640000u + (uint)id * 0x20u;

    private static void WriteCStringAtVa(byte[] exe, uint va, string s)
    {
        int off = ExePatcher.VaToFileOffset(va);
        var b = System.Text.Encoding.ASCII.GetBytes(s);
        b.CopyTo(exe, off);
        exe[off + b.Length] = 0;
    }

    /// <summary>Seed a valid BGM catalog into <paramref name="exe"/> (ids 2..99 across 3 flags classes), keeping
    /// the rest of the synthetic exe (setup-fn ptrs, descriptor tables) intact so additivity can be checked.</summary>
    private static void SeedBgmCatalog(byte[] exe)
    {
        for (int id = ExePatcher.BgmFirstShuffledId; id <= ExePatcher.BgmRecordCount; id++)
        {
            uint rec = ExePatcher.BgmRecordVa(id);
            WriteCStringAtVa(exe, BgmNameVa(id), $@"bgm\trk_{id:D2}");
            ExePatcher.WriteUInt32AtVa(exe, rec + 0, BgmNameVa(id));
            ExePatcher.WriteUInt32AtVa(exe, rec + 4, (uint)(id * 7 + 1));
            ExePatcher.WriteUInt32AtVa(exe, rec + 8, (uint)id);
            ExePatcher.WriteUInt32AtVa(exe, rec + 0xC, BgmFlagsFor(id));
        }
    }

    private byte[] WriteCatalogExe()
    {
        var exe = BuildSyntheticExe();
        SeedBgmCatalog(exe);
        File.WriteAllBytes(ExePath, exe);
        return exe;
    }

    [Fact]
    public void PatchExeShuffleBgm_ShufflesCatalog_BacksUpOnce_RecordsManifest()
    {
        var pristine = WriteCatalogExe();

        var res = GameInstaller.PatchExeShuffleBgm(DataDir, seed: 12345, seedLabel: "12345");

        Assert.True(File.Exists(res.BackupPath));
        Assert.Equal(pristine, File.ReadAllBytes(res.BackupPath)); // backup is pristine
        var exe = File.ReadAllBytes(ExePath);
        Assert.NotEqual(pristine, exe);                            // catalog changed

        // Every shuffled record kept its flags + id and stayed in-class (name still a seeded bgm\ pointer).
        for (int id = ExePatcher.BgmFirstShuffledId; id <= ExePatcher.BgmRecordCount; id++)
        {
            Assert.Equal(BgmFlagsFor(id), ExePatcher.ReadUInt32AtVa(exe, ExePatcher.BgmRecordVa(id) + 0xC));
            Assert.Equal((uint)id, ExePatcher.ReadUInt32AtVa(exe, ExePatcher.BgmRecordVa(id) + 8));
        }
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.NotNull(manifest);
        Assert.True(manifest!.ExePatched);
        Assert.Contains(manifest.ExeRepoints!, e => e.StartsWith("bgm catalog shuffle"));
    }

    [Fact]
    public void PatchExeShuffleBgm_IsIdempotentForSeed()
    {
        WriteCatalogExe();
        GameInstaller.PatchExeShuffleBgm(DataDir, 7, "7");
        var afterFirst = File.ReadAllBytes(ExePath);
        GameInstaller.PatchExeShuffleBgm(DataDir, 7, "7"); // same seed, re-applied
        Assert.Equal(afterFirst, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void PatchExeShuffleBgm_IsNonCompounding_EditsFromPristine()
    {
        var pristine = WriteCatalogExe();
        GameInstaller.PatchExeShuffleBgm(DataDir, 1, "1");
        GameInstaller.PatchExeShuffleBgm(DataDir, 2, "2"); // second seed must start from pristine, not seed-1 output

        // Result must equal a direct seed-2 shuffle of the pristine catalog.
        var expected = (byte[])pristine.Clone();
        ExePatcher.ShuffleBgmCatalog(expected, 2);
        Assert.Equal(expected, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void PatchExeShuffleBgm_IsAdditive_PreservesAnEnemyExePatch()
    {
        var pristine = WriteCatalogExe();
        // A prior enemy patch (cat-slot) must survive a later bgm shuffle (disjoint regions).
        GameInstaller.PatchExeCatSlot(DataDir, ExePatcher.Stage2AiRecordVa, 8, ExePatcher.TheriCat8HandlerVa);
        GameInstaller.PatchExeShuffleBgm(DataDir, 3, "3");

        var exe = File.ReadAllBytes(ExePath);
        Assert.Equal(ExePatcher.TheriCat8HandlerVa,
            ExePatcher.ReadUInt32AtVa(exe, ExePatcher.Stage2AiRecordVa + 8 * 4)); // enemy patch intact
        // …and the bgm catalog region really did change.
        int catOff = ExePatcher.VaToFileOffset(ExePatcher.BgmCatalogBaseVa);
        int catLen = ExePatcher.BgmRecordCount * ExePatcher.BgmRecordStride;
        Assert.NotEqual(pristine.Skip(catOff).Take(catLen), exe.Skip(catOff).Take(catLen));
    }

    [Fact]
    public void PatchExeShuffleBgm_Restore_ReversesByteIdentical()
    {
        var pristine = WriteCatalogExe();
        GameInstaller.PatchExeShuffleBgm(DataDir, 55, "55");
        Assert.NotEqual(pristine, File.ReadAllBytes(ExePath));

        GameInstaller.Restore(DataDir);

        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
        Assert.True(Directory.Exists(Path.Combine(DataDir, GameInstaller.BackupDirName))); // backup is kept (never deleted) for reuse
    }

    /// <summary>Gated real-exe test: set <c>DINORAND_DINO_EXE</c> to a real <c>DINO.exe</c> to validate the
    /// shuffle against the shipped catalog (id fields, bgm\ names, in-class permutation) and that Restore
    /// reverses it byte-identically. Skipped (early return) when the env var is unset, so CI stays self-contained.</summary>
    [Fact]
    public void PatchExeShuffleBgm_RealExe_ValidatesAndReverses()
    {
        var realExe = Environment.GetEnvironmentVariable("DINORAND_DINO_EXE");
        if (string.IsNullOrEmpty(realExe) || !File.Exists(realExe))
            return; // gated: no real exe supplied

        // Lay out a temp game tree (DINO.exe beside Data\) using the real exe.
        File.Copy(realExe, ExePath, overwrite: true);
        var pristine = File.ReadAllBytes(ExePath);

        var res = GameInstaller.PatchExeShuffleBgm(DataDir, seed: 1, seedLabel: "1");
        var patched = File.ReadAllBytes(ExePath);
        Assert.NotEqual(pristine, patched); // the real catalog moved

        // flags + id fields are untouched; the names are all still bgm\ pointers in the same class.
        for (int id = ExePatcher.BgmFirstShuffledId; id <= ExePatcher.BgmRecordCount; id++)
        {
            Assert.Equal(ExePatcher.ReadUInt32AtVa(pristine, ExePatcher.BgmRecordVa(id) + 0xC),
                         ExePatcher.ReadUInt32AtVa(patched, ExePatcher.BgmRecordVa(id) + 0xC));
            Assert.Equal((uint)id, ExePatcher.ReadUInt32AtVa(patched, ExePatcher.BgmRecordVa(id) + 8));
            var nm = ExePatcher.ReadCStringAtVa(patched, ExePatcher.ReadUInt32AtVa(patched, ExePatcher.BgmRecordVa(id) + 0));
            Assert.NotNull(nm);
            Assert.StartsWith(@"bgm\", nm);
        }
        Assert.NotEmpty(res.Repoints);

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
    }

    // ---- Emergency-box content shuffle (docs/dc1/EMERGENCY-BOX-DATA.md) ----

    /// <summary>Seed the three International box blocks with valid, distinguishable 21-byte records
    /// (<c>[0x0A][unique id, unique count][pad]</c>) so the shuffle/restore contract is checkable.</summary>
    private void SeedBoxTable(byte[] exe)
    {
        for (int blk = 0; blk < ExePatcher.EmergencyBoxBlockVas.Length; blk++)
        {
            int blockOff = ExePatcher.VaToFileOffset(ExePatcher.EmergencyBoxBlockVas[blk]);
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
            {
                int off = blockOff + i * ExePatcher.EmergencyBoxRecordStride;
                exe[off] = ExePatcher.EmergencyBoxSlotMarker;
                exe[off + 1] = (byte)(0x10 + i);
                exe[off + 2] = (byte)(1 + blk * 17 + i);
            }
        }
    }

    private byte[] WriteBoxTableExe()
    {
        var exe = BuildSyntheticExe();
        SeedBoxTable(exe);
        File.WriteAllBytes(ExePath, exe);
        return exe;
    }

    [Fact]
    public void PatchExeShuffleBoxes_ShufflesBoxes_BacksUpOnce_RecordsManifest()
    {
        var pristine = WriteBoxTableExe();

        var res = GameInstaller.PatchExeShuffleBoxes(DataDir, seed: 12345, seedLabel: "12345");

        Assert.True(File.Exists(res.BackupPath));
        Assert.Equal(pristine, File.ReadAllBytes(res.BackupPath)); // backup is pristine
        var exe = File.ReadAllBytes(ExePath);
        Assert.NotEqual(pristine, exe);                            // box table changed

        // Each block still holds the same multiset of records (a pure permutation, nothing invented/lost).
        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
        {
            int blockOff = ExePatcher.VaToFileOffset(va);
            string Recs(byte[] b) => string.Join("|", Enumerable.Range(0, ExePatcher.EmergencyBoxesPerBlock)
                .Select(i => Convert.ToHexString(b, blockOff + i * ExePatcher.EmergencyBoxRecordStride, ExePatcher.EmergencyBoxRecordStride))
                .OrderBy(x => x));
            Assert.Equal(Recs(pristine), Recs(exe));
        }
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.True(manifest!.ExePatched);
        Assert.Contains(manifest.ExeRepoints!, e => e.StartsWith("emergency-box shuffle"));
    }

    [Fact]
    public void PatchExeShuffleBoxes_IsNonCompounding_EditsFromPristine()
    {
        var pristine = WriteBoxTableExe();
        GameInstaller.PatchExeShuffleBoxes(DataDir, 1, "1");
        GameInstaller.PatchExeShuffleBoxes(DataDir, 2, "2"); // must start from pristine, not seed-1 output

        var expected = (byte[])pristine.Clone();
        ExePatcher.ShuffleEmergencyBoxContents(expected, 2);
        Assert.Equal(expected, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void PatchExeShuffleBoxes_Restore_ReversesByteIdentical()
    {
        var pristine = WriteBoxTableExe();
        GameInstaller.PatchExeShuffleBoxes(DataDir, 55, "55");
        Assert.NotEqual(pristine, File.ReadAllBytes(ExePath));

        GameInstaller.Restore(DataDir);

        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
        Assert.True(Directory.Exists(Path.Combine(DataDir, GameInstaller.BackupDirName))); // backup is kept (never deleted) for reuse
    }

    /// <summary>Gated real-exe test (set <c>DINORAND_DINO_EXE</c>): confirms the box-block offset constants
    /// point at real 0x0A-led 17-record blocks in the shipped exe, that the shuffle preserves each block's
    /// record multiset, and that Restore reverses it. Skipped when the env var is unset.</summary>
    [Fact]
    public void PatchExeShuffleBoxes_RealExe_ValidatesOffsets_AndReverses()
    {
        var realExe = Environment.GetEnvironmentVariable("DINORAND_DINO_EXE");
        if (string.IsNullOrEmpty(realExe) || !File.Exists(realExe))
            return; // gated: no real exe supplied

        File.Copy(realExe, ExePath, overwrite: true);
        var pristine = File.ReadAllBytes(ExePath);

        // Every record in each International block must lead with the 10-slot marker (offsets are correct).
        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
        {
            int blockOff = ExePatcher.VaToFileOffset(va);
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
                Assert.Equal(ExePatcher.EmergencyBoxSlotMarker, pristine[blockOff + i * ExePatcher.EmergencyBoxRecordStride]);
        }

        var res = GameInstaller.PatchExeShuffleBoxes(DataDir, seed: 1, seedLabel: "1");
        var patched = File.ReadAllBytes(ExePath);
        Assert.NotEqual(pristine, patched);
        Assert.NotEmpty(res.Repoints);

        // Per-block multiset preserved on the real table.
        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
        {
            int blockOff = ExePatcher.VaToFileOffset(va);
            string Recs(byte[] b) => string.Join("|", Enumerable.Range(0, ExePatcher.EmergencyBoxesPerBlock)
                .Select(i => Convert.ToHexString(b, blockOff + i * ExePatcher.EmergencyBoxRecordStride, ExePatcher.EmergencyBoxRecordStride))
                .OrderBy(x => x));
            Assert.Equal(Recs(pristine), Recs(patched));
        }

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void PatchExeRerollBoxes_RerollsBoxes_BacksUpOnce_RecordsManifest()
    {
        var pristine = WriteBoxTableExe();

        var res = GameInstaller.PatchExeRerollBoxes(DataDir, seed: 909, seedLabel: "909");

        Assert.True(File.Exists(res.BackupPath));
        Assert.Equal(pristine, File.ReadAllBytes(res.BackupPath)); // backup is pristine
        Assert.NotEqual(pristine, File.ReadAllBytes(ExePath));      // box table changed
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.True(manifest!.ExePatched);
        Assert.Contains(manifest.ExeRepoints!, e => e.StartsWith("emergency-box reroll"));
    }

    [Fact]
    public void PatchExeRerollBoxes_IsNonCompounding_EditsFromPristine()
    {
        var pristine = WriteBoxTableExe();
        GameInstaller.PatchExeRerollBoxes(DataDir, 1, "1");
        GameInstaller.PatchExeRerollBoxes(DataDir, 2, "2"); // must start from pristine, not seed-1 output

        var expected = (byte[])pristine.Clone();
        ExePatcher.RerollEmergencyBoxContents(expected, 2);
        Assert.Equal(expected, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void PatchExeRerollBoxes_Restore_ReversesByteIdentical()
    {
        var pristine = WriteBoxTableExe();
        GameInstaller.PatchExeRerollBoxes(DataDir, 55, "55");
        Assert.NotEqual(pristine, File.ReadAllBytes(ExePath));

        GameInstaller.Restore(DataDir);

        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
        Assert.True(Directory.Exists(Path.Combine(DataDir, GameInstaller.BackupDirName))); // backup is kept (never deleted) for reuse
    }

    [Fact]
    public void BoxModes_AreMutuallyExclusive_LastBoxPatchWins()
    {
        WriteBoxTableExe();
        GameInstaller.PatchExeShuffleBoxes(DataDir, 1, "1");
        GameInstaller.PatchExeRerollBoxes(DataDir, 2, "2"); // both rewrite the same span

        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.DoesNotContain(manifest!.ExeRepoints!, e => e.StartsWith("emergency-box shuffle"));
        Assert.Contains(manifest.ExeRepoints!, e => e.StartsWith("emergency-box reroll"));
    }

    // ---- Starting inventory (the new-game starting-inventory randomizer lever) ----------------------

    private byte[] WriteInventoryExe()
    {
        var exe = BuildSyntheticExe();
        // Supply slot stores.
        foreach (var block in ExePatcher.StartingInventoryBlocks)
            foreach (var slot in block.Slots)
            {
                uint disp = ExePatcher.InventorySlotIdBaseDisp + (uint)slot.Slot * 4;
                WriteSlotStore(exe, slot.IdImmVa, disp, 0x16);
                WriteSlotStore(exe, slot.QtyImmVa, disp + 1, 0x10);
            }
        // Weapon-grant pushes (push 1 / push <weaponId>) so SetStartingWeapon's validation passes.
        foreach (var block in ExePatcher.StartingWeaponGrantBlocks)
            foreach (var site in block.Sites)
            {
                ExePatcher.WriteUInt8AtVa(exe, site.ValImmVa - 1, 0x6A);
                ExePatcher.WriteUInt8AtVa(exe, site.ValImmVa, 1);
                ExePatcher.WriteUInt8AtVa(exe, site.IdxImmVa - 1, 0x6A);
                ExePatcher.WriteUInt8AtVa(exe, site.IdxImmVa, site.VanillaWeaponId);
            }
        File.WriteAllBytes(ExePath, exe);
        return exe;
    }

    private static void WriteSlotStore(byte[] exe, uint immVa, uint disp, byte imm)
    {
        uint instr = immVa - 6;
        ExePatcher.WriteUInt8AtVa(exe, instr, 0xC6);
        ExePatcher.WriteUInt8AtVa(exe, instr + 1, 0x80);
        ExePatcher.WriteUInt32AtVa(exe, instr + 2, disp);
        ExePatcher.WriteUInt8AtVa(exe, immVa, imm);
    }

    [Fact]
    public void PatchExeRandomizeInventory_BacksUpOnce_RecordsManifest_GrantsAmmo()
    {
        var pristine = WriteInventoryExe();

        var res = GameInstaller.PatchExeRandomizeInventory(DataDir, seed: 31337, seedLabel: "31337");

        Assert.True(File.Exists(res.BackupPath));
        Assert.Equal(pristine, File.ReadAllBytes(res.BackupPath)); // backup is pristine
        var exe = File.ReadAllBytes(ExePath);
        Assert.NotEqual(pristine, exe);
        // Slot 0 of every block is 9mm (the Handgun's ammo) — beatability.
        foreach (var block in ExePatcher.StartingInventoryBlocks)
            Assert.Equal(ExePatcher.StartingInvHandgunAmmoId, ExePatcher.ReadUInt8AtVa(exe, block.Slots[0].IdImmVa));
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.True(manifest!.ExePatched);
        Assert.Contains(manifest.ExeRepoints!, e => e.StartsWith("starting inventory randomized"));
    }

    [Fact]
    public void PatchExeRandomizeInventory_IsNonCompounding_EditsFromPristine()
    {
        var pristine = WriteInventoryExe();
        GameInstaller.PatchExeRandomizeInventory(DataDir, 1, "1");
        GameInstaller.PatchExeRandomizeInventory(DataDir, 2, "2"); // must start from pristine, not seed-1 output

        var expected = (byte[])pristine.Clone();
        ExePatcher.RandomizeStartingInventory(expected, 2);
        Assert.Equal(expected, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void PatchExeSetInventory_WritesList_AndRecordsManifest()
    {
        WriteInventoryExe();
        var items = new List<(int, int)> { (0x05, 1), (0x16, 30), (0x1D, 2) };
        GameInstaller.PatchExeSetInventory(DataDir, items);

        var exe = File.ReadAllBytes(ExePath);
        foreach (var block in ExePatcher.StartingInventoryBlocks)
            for (int i = 0; i < items.Count; i++)
            {
                Assert.Equal((byte)items[i].Item1, ExePatcher.ReadUInt8AtVa(exe, block.Slots[i].IdImmVa));
                Assert.Equal((byte)items[i].Item2, ExePatcher.ReadUInt8AtVa(exe, block.Slots[i].QtyImmVa));
            }
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.Contains(manifest!.ExeRepoints!, e => e.StartsWith("starting inventory set"));
    }

    [Fact]
    public void PatchExeStartingInventory_Restore_ReversesByteIdentical()
    {
        var pristine = WriteInventoryExe();
        GameInstaller.PatchExeRandomizeInventory(DataDir, 55, "55");
        Assert.NotEqual(pristine, File.ReadAllBytes(ExePath));

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
        Assert.True(Directory.Exists(Path.Combine(DataDir, GameInstaller.BackupDirName))); // backup is kept (never deleted) for reuse
    }

    [Fact]
    public void StartingInventoryModes_AreMutuallyExclusive_LastWins()
    {
        WriteInventoryExe();
        GameInstaller.PatchExeRandomizeInventory(DataDir, 1, "1");
        GameInstaller.PatchExeSetInventory(DataDir, new List<(int, int)> { (0x16, 30) }); // shares the span

        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.DoesNotContain(manifest!.ExeRepoints!, e => e.StartsWith("starting inventory randomized"));
        Assert.Contains(manifest.ExeRepoints!, e => e.StartsWith("starting inventory set"));
    }

    [Fact]
    public void PatchExeStartingInventory_SetWeapon_RewritesGrants_AndRecordsManifest()
    {
        WriteInventoryExe();
        GameInstaller.PatchExeStartingInventory(DataDir, new StartingInventoryPlan(SetWeapon: true, WeaponId: 0x01), 0);

        var exe = File.ReadAllBytes(ExePath);
        foreach (var block in ExePatcher.StartingWeaponGrantBlocks)
            for (int i = 0; i < block.Sites.Length; i++)
                if (i == 0) Assert.Equal(0x01, ExePatcher.ReadUInt8AtVa(exe, block.Sites[i].IdxImmVa));
                else Assert.Equal(0, ExePatcher.ReadUInt8AtVa(exe, block.Sites[i].ValImmVa)); // extras disabled
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.Contains(manifest!.ExeRepoints!, e => e.StartsWith("starting inventory") && e.Contains("weapon 0x01"));
    }

    [Fact]
    public void PatchExeStartingInventory_WeaponlessStart_IsRejected()
    {
        WriteInventoryExe();
        // A weaponless start ("None") is not supported yet — the install must refuse rather than silently
        // leave Regina armed (the engine re-equips a default Handgun via an undecoded path).
        Assert.Throws<ArgumentException>(() =>
            GameInstaller.PatchExeStartingInventory(DataDir, new StartingInventoryPlan(SetWeapon: true, WeaponId: null), 0));
    }

    [Fact]
    public void PatchExeStartingInventory_WeaponAndSupply_Compose_InOnePatch()
    {
        var pristine = WriteInventoryExe();
        // RANDOM supply + swap the weapon (to Shotgun 0x01), together.
        GameInstaller.PatchExeStartingInventory(DataDir,
            new StartingInventoryPlan(RandomizeSupply: true, SetWeapon: true, WeaponId: 0x01), seed: 7, seedLabel: "7");

        var exe = File.ReadAllBytes(ExePath);
        // Weapon set to Shotgun (first grant idx=0x01, extras disabled) AND supply rerolled (9mm in slot 0) —
        // both halves applied at once.
        foreach (var block in ExePatcher.StartingWeaponGrantBlocks)
        {
            Assert.Equal(0x01, ExePatcher.ReadUInt8AtVa(exe, block.Sites[0].IdxImmVa));
            for (int i = 1; i < block.Sites.Length; i++)
                Assert.Equal(0, ExePatcher.ReadUInt8AtVa(exe, block.Sites[i].ValImmVa));
        }
        foreach (var block in ExePatcher.StartingInventoryBlocks)
            Assert.Equal(ExePatcher.StartingInvHandgunAmmoId, ExePatcher.ReadUInt8AtVa(exe, block.Slots[0].IdImmVa));
        Assert.NotEqual(pristine, exe);

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath)); // one span, reversed byte-identically
    }

    /// <summary>Gated real-exe test (set <c>DINORAND_DINO_EXE</c>): the weapon-grant <c>SetFlag(11,…)</c> sites
    /// land on real `push 1; push idx` pairs in the shipped exe (idx a weapon id), setting a weapon rewrites the
    /// first grant + disables extras, and Restore reverses it. Skipped when the env var is unset.</summary>
    [Fact]
    public void PatchExeStartingWeapon_RealExe_ValidatesGrants_AndReverses()
    {
        var realExe = Environment.GetEnvironmentVariable("DINORAND_DINO_EXE");
        if (string.IsNullOrEmpty(realExe) || !File.Exists(realExe))
            return; // gated

        File.Copy(realExe, ExePath, overwrite: true);
        var pristine = File.ReadAllBytes(ExePath);
        ExePatcher.ValidateStartingWeaponGrants(pristine); // the grant offsets are correct on the shipped build
        // Vanilla: 11:5 (Handgun) is the only weapon every difficulty grants.
        foreach (var block in ExePatcher.StartingWeaponGrantBlocks)
            Assert.Contains(block.Sites, s => ExePatcher.ReadUInt8AtVa(pristine, s.IdxImmVa) == 0x05);

        GameInstaller.PatchExeStartingInventory(DataDir, new StartingInventoryPlan(SetWeapon: true, WeaponId: 0x09), 0);
        var patched = File.ReadAllBytes(ExePath);
        foreach (var block in ExePatcher.StartingWeaponGrantBlocks)
        {
            Assert.Equal(0x09, ExePatcher.ReadUInt8AtVa(patched, block.Sites[0].IdxImmVa));
            for (int i = 1; i < block.Sites.Length; i++)
                Assert.Equal(0, ExePatcher.ReadUInt8AtVa(patched, block.Sites[i].ValImmVa));
        }

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
    }

    /// <summary>Gated real-exe test (set <c>DINORAND_DINO_EXE</c>): the starting-inventory slot offsets land
    /// on real `C6 80 …` stores in the shipped exe, RANDOM grants 9mm in slot 0 of every block and writes only
    /// valid supply ids/counts, and Restore reverses it byte-identically. Skipped when the env var is unset.</summary>
    [Fact]
    public void PatchExeRandomizeInventory_RealExe_ValidatesOffsets_AndReverses()
    {
        var realExe = Environment.GetEnvironmentVariable("DINORAND_DINO_EXE");
        if (string.IsNullOrEmpty(realExe) || !File.Exists(realExe))
            return; // gated

        File.Copy(realExe, ExePath, overwrite: true);
        var pristine = File.ReadAllBytes(ExePath);
        ExePatcher.ValidateStartingInventory(pristine); // the offsets are correct on the shipped build

        var res = GameInstaller.PatchExeRandomizeInventory(DataDir, seed: 1, seedLabel: "1");
        var patched = File.ReadAllBytes(ExePath);
        Assert.NotEqual(pristine, patched);
        Assert.NotEmpty(res.Repoints);

        foreach (var block in ExePatcher.StartingInventoryBlocks)
        {
            Assert.Equal(ExePatcher.StartingInvHandgunAmmoId, ExePatcher.ReadUInt8AtVa(patched, block.Slots[0].IdImmVa));
            foreach (var slot in block.Slots)
            {
                Assert.InRange(ExePatcher.ReadUInt8AtVa(patched, slot.IdImmVa),
                    ExePatcher.StartingInvFirstSupplyId, ExePatcher.StartingInvLastItemId);
                Assert.InRange(ExePatcher.ReadUInt8AtVa(patched, slot.QtyImmVa), (byte)1, (byte)255);
            }
        }

        // Only the declared patch span changed.
        int lo = ExePatcher.VaToFileOffset(ExePatcher.StartingInventoryPatchLoVa);
        int hi = ExePatcher.VaToFileOffset(ExePatcher.StartingInventoryPatchHiVa);
        for (int i = 0; i < pristine.Length; i++)
            if (i < lo || i >= hi)
                Assert.Equal(pristine[i], patched[i]);

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
    }

    /// <summary>Gated real-exe test (set <c>DINORAND_DINO_EXE</c>): the decoded vanilla starting inventory
    /// matches the documented kit (docs/dc1/STARTING-INVENTORY.md) — every difficulty block's slot 2/0/0/1
    /// holds 9mm, and the diff0 ammo counts are the property-table full mags. Skipped when unset.</summary>
    [Fact]
    public void StartingInventory_RealExe_DecodesVanillaKit()
    {
        var realExe = Environment.GetEnvironmentVariable("DINORAND_DINO_EXE");
        if (string.IsNullOrEmpty(realExe) || !File.Exists(realExe))
            return; // gated

        var exe = File.ReadAllBytes(realExe);
        byte Id(string block, int slot) =>
            ExePatcher.ReadUInt8AtVa(exe, ExePatcher.StartingInventoryBlocks.First(b => b.Name == block).Slots.First(s => s.Slot == slot).IdImmVa);
        byte Qty(string block, int slot) =>
            ExePatcher.ReadUInt8AtVa(exe, ExePatcher.StartingInventoryBlocks.First(b => b.Name == block).Slots.First(s => s.Slot == slot).QtyImmVa);

        // 9mm (the Handgun's ammo) is present in every difficulty block.
        Assert.Equal(0x16, Id("diff0", 2));
        Assert.Equal(0x16, Id("diff1", 0));
        Assert.Equal(0x16, Id("diff2", 1));
        Assert.Equal(0x16, Id("default", 1));
        // diff0 ammo counts are full mags (9mm=34, Slag=10, Grenade=6).
        Assert.Equal(34, Qty("diff0", 2));
        Assert.Equal(10, Qty("diff0", 3));
        Assert.Equal(6, Qty("diff0", 4));
    }

    /// <summary>Gated real-exe test (set <c>DINORAND_DINO_EXE</c>): reroll on the shipped exe keeps every box
    /// record valid — 0x0A marker, ids in the box-item range, and each (id, amount) is one the pristine block
    /// actually ships (drawn from that block's own pool) — and Restore reverses it. Skipped when unset.</summary>
    [Fact]
    public void PatchExeRerollBoxes_RealExe_KeepsValidContents_AndReverses()
    {
        var realExe = Environment.GetEnvironmentVariable("DINORAND_DINO_EXE");
        if (string.IsNullOrEmpty(realExe) || !File.Exists(realExe))
            return; // gated

        File.Copy(realExe, ExePath, overwrite: true);
        var pristine = File.ReadAllBytes(ExePath);

        // Per-block set of valid (id, amount) pairs the pristine table ships (the legal reroll outputs).
        var legal = new Dictionary<uint, HashSet<(byte, byte)>>();
        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
        {
            var set = new HashSet<(byte, byte)>();
            int blockOff = ExePatcher.VaToFileOffset(va);
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
                for (int k = 0; k < 10; k++)
                {
                    byte id = pristine[blockOff + i * ExePatcher.EmergencyBoxRecordStride + 1 + 2 * k];
                    if (id < ExePatcher.EmergencyBoxFirstItemId || id > ExePatcher.EmergencyBoxLastItemId) break;
                    set.Add((id, pristine[blockOff + i * ExePatcher.EmergencyBoxRecordStride + 2 + 2 * k]));
                }
            legal[va] = set;
        }

        GameInstaller.PatchExeRerollBoxes(DataDir, seed: 1, seedLabel: "1");
        var patched = File.ReadAllBytes(ExePath);
        Assert.NotEqual(pristine, patched);

        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
        {
            int blockOff = ExePatcher.VaToFileOffset(va);
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
            {
                int rec = blockOff + i * ExePatcher.EmergencyBoxRecordStride;
                Assert.Equal(ExePatcher.EmergencyBoxSlotMarker, patched[rec]);
                for (int k = 0; k < 10; k++)
                {
                    byte id = patched[rec + 1 + 2 * k];
                    if (id < ExePatcher.EmergencyBoxFirstItemId || id > ExePatcher.EmergencyBoxLastItemId) break;
                    Assert.Contains((id, patched[rec + 2 + 2 * k]), legal[va]); // a real, in-block (id,amount)
                }
            }
        }

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath));
    }

    // ---- EXE-patch plan applier (cross-species pass plumbing; docs/dc1/CROSS-SPECIES-PASS-PLAN.md) ----

    [Fact]
    public void ApplyExePatchPlan_CatSlot_PatchesSlot_AndRecordsManifest()
    {
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.CatSlot(2, 8, ExePatcher.TheriCat8HandlerVa),
        });

        var results = GameInstaller.ApplyExePatchPlan(DataDir, plan, donorDir: _root);

        Assert.Single(results);
        var exe = File.ReadAllBytes(ExePath);
        Assert.Equal(ExePatcher.TheriCat8HandlerVa,
            ExePatcher.ReadUInt32AtVa(exe, ExePatcher.Stage2AiRecordVa + 8 * 4));
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.True(manifest!.ExePatched);
    }

    [Fact]
    public void ApplyExePatchPlan_RejectsUnsupportedVersion()
    {
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion + 99, Array.Empty<ExePatchRequest>());
        Assert.Throws<InvalidOperationException>(() => GameInstaller.ApplyExePatchPlan(DataDir, plan, _root));
    }

    [Fact]
    public void Install_AppliesSidecarExePatchPlan_AndRestoreReverses()
    {
        var pristine = File.ReadAllBytes(ExePath);

        // A mod dir with a room overlay + an exe-patch-plan.json sidecar (one cat-slot patch).
        var modDir = Path.Combine(_root, "mod");
        Directory.CreateDirectory(modDir);
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 7, 7, 7, 7 });
        new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.CatSlot(2, 8, ExePatcher.TheriCat8HandlerVa),
        }).Write(modDir);

        GameInstaller.Install(DataDir, modDir, seed: "5");

        var exe = File.ReadAllBytes(ExePath);
        Assert.Equal(ExePatcher.TheriCat8HandlerVa,
            ExePatcher.ReadUInt32AtVa(exe, ExePatcher.Stage2AiRecordVa + 8 * 4)); // sidecar applied
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.True(manifest!.ExePatched);

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristine, File.ReadAllBytes(ExePath)); // exe back to pristine
        Assert.True(Directory.Exists(Path.Combine(DataDir, GameInstaller.BackupDirName))); // backup is kept (never deleted) for reuse
    }

    [Fact]
    public void Install_WithoutSidecar_DoesNotPatchExe()
    {
        var pristine = File.ReadAllBytes(ExePath);
        var modDir = Path.Combine(_root, "mod2");
        Directory.CreateDirectory(modDir);
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 8, 8, 8, 8 });

        GameInstaller.Install(DataDir, modDir, seed: "6");

        Assert.Equal(pristine, File.ReadAllBytes(ExePath)); // no plan ⇒ exe untouched
        var manifest = GameInstaller.ReadManifest(DataDir);
        Assert.False(manifest!.ExePatched);
    }

    [Fact]
    public void Install_InvalidNthExeRequest_LeavesPriorRoomLooseExeAndManifestUnchanged()
    {
        var modDir = Path.Combine(_root, "transaction-mod");
        Directory.CreateDirectory(modDir);
        var liveVoiceDir = Path.Combine(_root, "Sound", "VOICE");
        var modVoiceDir = Path.Combine(modDir, "Sound", "VOICE");
        Directory.CreateDirectory(liveVoiceDir);
        Directory.CreateDirectory(modVoiceDir);
        var liveVoice = Path.Combine(liveVoiceDir, "0001.dat");
        var modVoice = Path.Combine(modVoiceDir, "0001.dat");
        File.WriteAllBytes(liveVoice, new byte[] { 1, 1, 1 });
        File.WriteAllBytes(modVoice, new byte[] { 5, 5, 5 });
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 6, 6, 6, 6 });
        GameInstaller.Install(DataDir, modDir, seed: "installed-seed");

        var roomBefore = File.ReadAllBytes(Path.Combine(DataDir, "st101.dat"));
        var looseBefore = File.ReadAllBytes(liveVoice);
        var exeBefore = File.ReadAllBytes(ExePath);
        var manifestPath = Path.Combine(DataDir, GameInstaller.BackupDirName, "manifest.json");
        var manifestBefore = File.ReadAllBytes(manifestPath);

        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 7, 7, 7, 7 });
        File.WriteAllBytes(modVoice, new byte[] { 8, 8, 8 });
        new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.CatSlot(2, 8, ExePatcher.TheriCat8HandlerVa),
            ExePatchRequest.CatSlot(3, 8, ExePatcher.TheriCat8HandlerVa), // stage 3 is not verified free
        }).Write(modDir);

        Assert.Throws<InvalidOperationException>(() =>
            GameInstaller.Install(DataDir, modDir, seed: "rejected-seed"));

        Assert.Equal(roomBefore, File.ReadAllBytes(Path.Combine(DataDir, "st101.dat")));
        Assert.Equal(looseBefore, File.ReadAllBytes(liveVoice));
        Assert.Equal(exeBefore, File.ReadAllBytes(ExePath));
        Assert.Equal(manifestBefore, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void Install_MissingExeDonor_PreflightLeavesOverlayAndManifestUntouched()
    {
        var modDir = Path.Combine(_root, "missing-donor-mod");
        Directory.CreateDirectory(modDir);
        var roomPath = Path.Combine(DataDir, "st101.dat");
        var roomBefore = File.ReadAllBytes(roomPath);
        var exeBefore = File.ReadAllBytes(ExePath);
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 9, 9, 9, 9 });
        new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.Cat8HitReaction(6, 3, "missing-st603.dat"),
        }).Write(modDir);

        Assert.Throws<FileNotFoundException>(() => GameInstaller.Install(DataDir, modDir, seed: "missing"));

        Assert.Equal(roomBefore, File.ReadAllBytes(roomPath));
        Assert.Equal(exeBefore, File.ReadAllBytes(ExePath));
        Assert.False(File.Exists(Path.Combine(DataDir, GameInstaller.BackupDirName, "manifest.json")));
    }

    [Fact]
    public void Install_ConflictingComposedRequests_AreRefusedBeforeMutation()
    {
        var modDir = Path.Combine(_root, "conflict-mod");
        Directory.CreateDirectory(modDir);
        var roomPath = Path.Combine(DataDir, "st101.dat");
        var roomBefore = File.ReadAllBytes(roomPath);
        var exeBefore = File.ReadAllBytes(ExePath);
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 9, 9, 9, 9 });
        new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.CatSlot(2, 8, ExePatcher.TheriCat8HandlerVa),
            ExePatchRequest.CatSlot(2, 8, ExePatcher.SetupFnBasicRaptor),
        }).Write(modDir);

        Assert.Throws<InvalidOperationException>(() => GameInstaller.Install(DataDir, modDir, seed: "conflict"));

        Assert.Equal(roomBefore, File.ReadAllBytes(roomPath));
        Assert.Equal(exeBefore, File.ReadAllBytes(ExePath));
        Assert.False(File.Exists(Path.Combine(DataDir, GameInstaller.BackupDirName, "manifest.json")));
    }

    [Fact]
    public void Install_CommitIoFailure_RollsBackEveryTargetAndNewBackup()
    {
        var roomPath = Path.Combine(DataDir, "st101.dat");
        var roomBefore = File.ReadAllBytes(roomPath);
        var exeBefore = File.ReadAllBytes(ExePath);
        var liveVoiceDir = Path.Combine(_root, "Sound", "VOICE");
        Directory.CreateDirectory(liveVoiceDir);
        var liveVoice = Path.Combine(liveVoiceDir, "0001.dat");
        File.WriteAllBytes(liveVoice, new byte[] { 1, 1, 1 });

        var modDir = Path.Combine(_root, "rollback-mod");
        var modVoiceDir = Path.Combine(modDir, "Sound", "VOICE");
        Directory.CreateDirectory(modVoiceDir);
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 9, 9, 9, 9 });
        File.WriteAllBytes(Path.Combine(modVoiceDir, "0001.dat"), new byte[] { 8, 8, 8 });
        new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.CatSlot(2, 8, ExePatcher.TheriCat8HandlerVa),
        }).Write(modDir);

        OverlayInstaller.CommitStepProbe = step =>
        {
            if (step == 1) throw new IOException("injected commit failure");
        };
        try
        {
            Assert.Throws<IOException>(() => GameInstaller.Install(DataDir, modDir, seed: "rollback"));
        }
        finally
        {
            OverlayInstaller.CommitStepProbe = null;
        }

        Assert.Equal(roomBefore, File.ReadAllBytes(roomPath));
        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(liveVoice));
        Assert.Equal(exeBefore, File.ReadAllBytes(ExePath));
        var backupDir = Path.Combine(DataDir, GameInstaller.BackupDirName);
        Assert.False(File.Exists(Path.Combine(backupDir, "st101.dat")));
        Assert.False(File.Exists(Path.Combine(backupDir, GameInstaller.ExeName)));
        Assert.False(File.Exists(Path.Combine(backupDir, "loose", "Sound", "VOICE", "0001.dat")));
        Assert.False(File.Exists(Path.Combine(backupDir, "manifest.json")));
    }

    [Fact]
    public void Install_ComposesMultipleCompatibleRequests_Idempotently_AndRestoreReverses()
    {
        var pristineExe = File.ReadAllBytes(ExePath);
        var pristineRoom = File.ReadAllBytes(Path.Combine(DataDir, "st101.dat"));
        var modDir = Path.Combine(_root, "composed-mod");
        Directory.CreateDirectory(modDir);
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), new byte[] { 4, 3, 2, 1 });
        new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.CatSlot(1, 8, ExePatcher.TheriCat8HandlerVa),
            ExePatchRequest.CatSlot(2, 8, ExePatcher.TheriCat8HandlerVa),
        }).Write(modDir);

        GameInstaller.Install(DataDir, modDir, seed: "first");
        var first = File.ReadAllBytes(ExePath);
        Assert.Equal(ExePatcher.TheriCat8HandlerVa,
            ExePatcher.ReadUInt32AtVa(first, ExePatcher.Stage1AiRecordVa + 8 * 4));
        Assert.Equal(ExePatcher.TheriCat8HandlerVa,
            ExePatcher.ReadUInt32AtVa(first, ExePatcher.Stage2AiRecordVa + 8 * 4));

        GameInstaller.Install(DataDir, modDir, seed: "second");
        Assert.Equal(first, File.ReadAllBytes(ExePath));
        Assert.Equal(pristineExe,
            File.ReadAllBytes(Path.Combine(DataDir, GameInstaller.BackupDirName, GameInstaller.ExeName)));
        Assert.Equal("second", GameInstaller.ReadManifest(DataDir)!.Seed);

        GameInstaller.Restore(DataDir);
        Assert.Equal(pristineExe, File.ReadAllBytes(ExePath));
        Assert.Equal(pristineRoom, File.ReadAllBytes(Path.Combine(DataDir, "st101.dat")));
    }
}
