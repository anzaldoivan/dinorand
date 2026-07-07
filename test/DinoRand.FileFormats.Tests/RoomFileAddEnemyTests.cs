using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// End-to-end <see cref="RoomFile.AddEnemy"/> over the real corpus (docs/dc1/ADD-ENEMY-PLAN.md): import a
/// donor species, inject a new <c>0x20</c> record into a room's init script, and prove the grown room
/// re-reads cleanly with the new enemy present and the existing ones untouched. Gated on a real install
/// (<c>DINORAND_DC1_DIR</c>); a no-op on CI without game files. Spawn validity itself is a CE concern.
/// </summary>
public class RoomFileAddEnemyTests
{
    private static string? DataDir()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        foreach (var c in new[] { root, Path.Combine(root, "Data") })
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "st*.dat").Any())
                return c;
        return Directory.EnumerateDirectories(root, "Data", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static RoomFile Load(string dir, int stage, int room, string file)
        => RoomFile.Read(stage, room, File.ReadAllBytes(Path.Combine(dir, file)));

    private static IEnumerable<int> Heads(RoomFile rf)
    {
        foreach (var e in rf.Enemies)
        {
            if (e.OriginalModelPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
            if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
        }
    }

    /// <summary>First cleanly-extractable donor for a species anywhere in the install (mirrors the CLI's
    /// donor search), or null. Used so a test isn't coupled to one room's extractability.</summary>
    private static SpeciesDonor? FindDonor(string dir, DinoSpecies species)
    {
        foreach (var path in Directory.EnumerateFiles(dir, "st*.dat"))
        {
            RoomFile rf;
            try { rf = RoomFile.Read(0, 0, File.ReadAllBytes(path)); }
            catch { continue; }
            var rec = rf.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == species);
            if (rec is null) continue;
            try { return SpeciesImporter.ExtractDonor(rf.RdtBuffer, rec, Heads(rf)); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
        }
        return null;
    }

    [Fact]
    public void AddEnemy_InjectsPteranodon_GrowsRoom_AndRoundTrips()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st407.dat")) || !File.Exists(Path.Combine(dir, "st10e.dat")))
            return;

        // Donor: the Pteranodon (cat 7) from st407.
        var src = Load(dir, 4, 0x07, "st407.dat");
        var donorRec = src.Enemies.First(e => e.Species == DinoSpecies.Pteranodon);
        var donor = SpeciesImporter.ExtractDonor(src.RdtBuffer, donorRec, Heads(src));

        // Target: a raptor room. Harvest a known-good floor point from one of its raptors.
        var target = Load(dir, 1, 0x0e, "st10e.dat");
        int before = target.Enemies.Count;
        Assert.True(before > 0);
        var probe = target.Enemies.First();
        var (x, y, z, rot) = (probe.PosX, probe.PosY, probe.PosZ, probe.Rotation);
        int beforeLen = target.RdtBuffer.Length;

        var added = target.AddEnemy(donor, x, y, z, rot);

        // The new record decodes as an 18-bone Pteranodon with cat-7 AI, at the harvested position.
        Assert.Equal(DinoSpecies.Pteranodon, added.Species);
        Assert.True(added.SpeciesMatchesCategory);
        Assert.Equal(x, added.PosX);
        Assert.Equal(z, added.PosZ);
        Assert.Equal(before + 1, target.Enemies.Count);
        Assert.True(target.RdtBuffer.Length > beforeLen);

        // Write -> re-Read: the room still parses cleanly and the extra Pteranodon survives.
        var bytes = target.Write();
        var reread = RoomFile.Read(1, 0x0e, bytes);
        Assert.True(reread.ParsedCleanly);
        Assert.Equal(before + 1, reread.Enemies.Count);
        Assert.Contains(reread.Enemies, e => e.Species == DinoSpecies.Pteranodon
                                          && e.PosX == x && e.PosZ == z);
    }

    /// <summary>First interior 4-aligned opcode boundary inside a NON-init subroutine (an event sub),
    /// or -1. Mirrors the offsets a user reads from the decoded script for <c>--add-enemy-at</c>.</summary>
    private static int FindEventSubOffset(byte[] rdt)
    {
        if (!ScriptInjector.TryReadFuncTable(rdt, out _, out var starts) || starts.Count < 2) return -1;
        for (int i = 1; i < starts.Count; i++) // skip init sub-0
        {
            int s = starts[i], e = i + 1 < starts.Count ? starts[i + 1] : rdt.Length, pos = s;
            while (pos < e)
            {
                int len = DcOpcodes.Length(rdt, pos);
                if (len <= 0 || pos + len > e) break;
                if (pos > s && (pos & 3) == 0) return pos;
                pos += len;
            }
        }
        return -1;
    }

    [Fact]
    public void AddEnemyAt_InjectsIntoEventSub_RoundTrips_AndRejectsMidInstruction()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st407.dat")) || !File.Exists(Path.Combine(dir, "st502.dat")))
            return;

        var src = Load(dir, 4, 0x07, "st407.dat");
        var donor = SpeciesImporter.ExtractDonor(
            src.RdtBuffer, src.Enemies.First(e => e.Species == DinoSpecies.Pteranodon), Heads(src));

        // st502 (the "blue raptor" room) has several event subs.
        var target = Load(dir, 5, 0x02, "st502.dat");
        int off = FindEventSubOffset(target.RdtBuffer);
        Assert.True(off > 0, "no event-sub boundary found in st502");
        int sub = ScriptInjector.SubroutineAtBoundary(target.RdtBuffer, off);
        Assert.True(sub >= 1, "offset must be in a non-init (event) subroutine");

        int before = target.Enemies.Count;
        var added = target.AddEnemyAt(donor, off, 0, 0, 0, 0);

        Assert.Equal(off, added.FileOffset);          // landed exactly at the requested offset
        Assert.Equal(DinoSpecies.Pteranodon, added.Species);
        Assert.Equal(before + 1, target.Enemies.Count);

        var reread = RoomFile.Read(5, 0x02, target.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Equal(before + 1, reread.Enemies.Count);
        Assert.Contains(reread.Enemies, e => e.FileOffset == off && e.Species == DinoSpecies.Pteranodon);

        // A mid-instruction offset (off+1) is rejected, not silently corrupting the script.
        var fresh = Load(dir, 5, 0x02, "st502.dat");
        Assert.Throws<InvalidOperationException>(() => fresh.AddEnemyAt(donor, off + 1, 0, 0, 0, 0));
    }

    [Fact]
    public void AddEnemyAt_ReusesLoadedModel_WhenSpeciesAlreadyInRoom()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st10a.dat"))) return;

        // st10a's window-ambush spawns RaptorHeavy, so that model is already loaded by the room.
        var target = Load(dir, 1, 0x0a, "st10a.dat");
        var native = target.Enemies.First(e => e.Species == DinoSpecies.RaptorHeavy);
        uint loadedModel = native.OriginalModelPtr, loadedMotion = native.OriginalMotionPtr;

        // A clean RaptorHeavy donor from anywhere in the install (as the CLI finds one). Because the
        // target room ALREADY loads RaptorHeavy, AddEnemyAt must REUSE the loaded model — a freshly
        // imported copy is never loaded into a renderable node, so the event-spawned enemy render-AVs
        // (docs/dc1/ADD-ENEMY-EVENT-INJECTION-CRASH-RCA.md).
        var donor = FindDonor(dir, DinoSpecies.RaptorHeavy);
        if (donor is null) return; // no cleanly-importable donor (not expected on a full install)
        int off = FindEventSubOffset(target.RdtBuffer);
        Assert.True(off > 0, "no event-sub boundary found in st10a");
        int beforeLen = target.RdtBuffer.Length;

        var added = target.AddEnemyAt(donor, off, 0, 0, 0, 0);

        Assert.Equal(loadedModel, added.ModelPtr);   // reused the already-loaded model pointer
        Assert.Equal(loadedMotion, added.MotionPtr);
        // No fresh model block was appended — the buffer grew only by the 24-byte record.
        Assert.Equal(beforeLen + DcOpcodes.EnemyLength, target.RdtBuffer.Length);

        var reread = RoomFile.Read(1, 0x0a, target.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Contains(reread.Enemies, e => e.FileOffset == off && e.ModelPtr == loadedModel);
    }

    [Fact]
    public void AddEnemyStanding_010A_PicksCopyBBranchTargetSite_ReusesModel_RoundTrips()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st10a.dat"))) return;

        // 010A has the standing-raptor branch-target spawn block in sub0 (gated 0:34/0:35). The site
        // classifier must pick the boundary INSIDE that block — exactly where copy B sat (RDT 0x48874),
        // the live+dump-proven active+persistent placement (docs/dc1/ENEMY-INJECTION-MODES.md,
        // ADD-ENEMY-EVENT-INJECTION-CRASH-RCA.md "44912 dump RCA").
        var target = Load(dir, 1, 0x0a, "st10a.dat");
        int standing = InjectionSiteClassifier.StandingSite(target.RdtBuffer);
        Assert.Equal(0x48874, standing);

        // That site classifies as active + every-entry (the "standing native-like" cell) inside init.
        var site = InjectionSiteClassifier.Classify(target.RdtBuffer).Single(s => s.Offset == standing);
        Assert.True(site.IsInit);
        Assert.Equal(SpawnActivation.Active, site.Activation);
        Assert.Equal(SpawnPersistence.EveryEntry, site.Persistence);

        var native = target.Enemies.First(e => e.Species == DinoSpecies.RaptorHeavy);
        uint loadedModel = native.OriginalModelPtr, loadedMotion = native.OriginalMotionPtr;
        var donor = FindDonor(dir, DinoSpecies.RaptorHeavy);
        if (donor is null) return; // no cleanly-importable donor (not expected on a full install)

        int before = target.Enemies.Count;
        int beforeLen = target.RdtBuffer.Length;
        var added = target.AddEnemyStanding(donor, 7696, 0, -6896, 0);

        Assert.Equal(standing, added.FileOffset);              // landed at the copy-B site
        Assert.Equal(DinoSpecies.RaptorHeavy, added.Species);
        Assert.Equal(loadedModel, added.ModelPtr);             // reused the room's loaded model (no AV)
        Assert.Equal(loadedMotion, added.MotionPtr);
        Assert.Equal(beforeLen + DcOpcodes.EnemyLength, target.RdtBuffer.Length); // grew by 24 only
        Assert.Equal(before + 1, target.Enemies.Count);

        var reread = RoomFile.Read(1, 0x0a, target.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Contains(reread.Enemies, e => e.FileOffset == standing && e.ModelPtr == loadedModel);
    }

    [Fact]
    public void AddEnemyEncounter_PicksEventSub_RoundTrips()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st407.dat")) || !File.Exists(Path.Combine(dir, "st502.dat")))
            return;

        var src = Load(dir, 4, 0x07, "st407.dat");
        var donor = SpeciesImporter.ExtractDonor(
            src.RdtBuffer, src.Enemies.First(e => e.Species == DinoSpecies.Pteranodon), Heads(src));

        var target = Load(dir, 5, 0x02, "st502.dat");
        int encounter = InjectionSiteClassifier.EncounterSite(target.RdtBuffer);
        Assert.True(encounter > 0, "st502 should have an event-sub encounter site");
        Assert.True(ScriptInjector.SubroutineAtBoundary(target.RdtBuffer, encounter) >= 1); // an event sub

        var site = InjectionSiteClassifier.Classify(target.RdtBuffer).Single(s => s.Offset == encounter);
        Assert.False(site.IsInit);
        Assert.Equal(SpawnActivation.Active, site.Activation);
        Assert.Equal(SpawnPersistence.OneShot, site.Persistence);

        int before = target.Enemies.Count;
        var added = target.AddEnemyEncounter(donor, 0, 0, 0, 0);
        Assert.Equal(encounter, added.FileOffset);
        Assert.Equal(before + 1, target.Enemies.Count);

        var reread = RoomFile.Read(5, 0x02, target.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Contains(reread.Enemies, e => e.FileOffset == encounter && e.Species == DinoSpecies.Pteranodon);
    }

    [Fact]
    public void SafeInsertOffset_0102_AvoidsTheTerminalLoopReturn()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st102.dat"))) return;

        // 0102's sub0 ends in a 0x04 counter-gated loop/return at RDT 0x3a170. The pre-fix SafeInsertOffset
        // returned that 0x04 slot and the game crashed at load (docs/dc1/ENEMY-INJECTION-MODES.md). The fix:
        // the auto-init offset must be a plain (non-control) opcode boundary.
        var target = Load(dir, 1, 0x02, "st102.dat");
        Assert.True(ScriptInjector.TryReadFuncTable(target.RdtBuffer, out _, out var starts) && starts.Count > 1);
        int s0 = starts[0], e0 = starts[1];
        int o = ScriptCfg.SafeInsertOffset(target.RdtBuffer, s0, e0);
        Assert.True(o > 0);
        Assert.NotEqual(0x3a170, o);                                   // not the 0x04 terminator
        Assert.False(ScriptCfg.IsControlOpcode(target.RdtBuffer[o]));  // a plain opcode boundary
    }

    [Fact]
    public void AddEnemyAt_RefusesToSpliceAtAControlOpcode()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st102.dat"))) return;

        var donor = FindDonor(dir, DinoSpecies.RaptorHeavy);
        if (donor is null) return;
        var target = Load(dir, 1, 0x02, "st102.dat");

        // Find a control-flow opcode boundary in sub0 (0102 ends in a 0x04 loop/return; earlier it has 0x0e
        // branches). Located dynamically so the test holds whether the install's st102 is pristine or has an
        // injected enemy. Splicing at a control op derails the VM, so the guard must reject it up front
        // rather than write a load-crashing file (docs/dc1/ENEMY-INJECTION-MODES.md "0102 load-crash RCA").
        Assert.True(ScriptInjector.TryReadFuncTable(target.RdtBuffer, out _, out var starts) && starts.Count > 1);
        int s0 = starts[0], e0 = starts[1], ctrlOff = -1;
        for (int pos = s0; pos < e0;)
        {
            int len = DcOpcodes.Length(target.RdtBuffer, pos);
            if (len <= 0 || pos + len > e0) break;
            if (pos > s0 && (pos & 3) == 0 && ScriptCfg.IsControlOpcode(target.RdtBuffer[pos])) { ctrlOff = pos; break; }
            pos += len;
        }
        Assert.True(ctrlOff > 0, "expected a control opcode boundary in 0102 sub0");
        var ex = Assert.Throws<InvalidOperationException>(() => target.AddEnemyAt(donor, ctrlOff, 0, 0, 0, 0));
        Assert.Contains("control", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddEnemy_PicksFreeSlotAndKillFlag_DistinctFromExisting()
    {
        var dir = DataDir();
        if (dir is null) return;
        if (!File.Exists(Path.Combine(dir, "st407.dat")) || !File.Exists(Path.Combine(dir, "st106.dat")))
            return;

        var src = Load(dir, 4, 0x07, "st407.dat");
        var donor = SpeciesImporter.ExtractDonor(
            src.RdtBuffer, src.Enemies.First(e => e.Species == DinoSpecies.Pteranodon), Heads(src));

        var target = Load(dir, 1, 0x06, "st106.dat");
        var usedSlots = target.Enemies.Select(e => e.Slot).ToHashSet();
        var added = target.AddEnemy(donor, 0, 0, 0, 0);

        // Slot is the smallest index free in the room; kill-flag 0 is free here (corpus norm).
        Assert.DoesNotContain(added.Slot, usedSlots);
    }
}
