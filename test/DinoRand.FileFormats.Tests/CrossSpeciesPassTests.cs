using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The integrated cross-species enemy pass (docs/dc1/CROSS-SPECIES-PASS-PLAN.md). Headless unit tests pin the
/// registry, the pure placement planner (stage-scoping), the EXE-patch sidecar, and that the runner/installer
/// thread the plan; a gated real-install test proves the pass places the Theri end-to-end and queues its EXE
/// patches.
/// </summary>
public class CrossSpeciesPassTests
{
    // ---- Registry ----

    [Fact]
    public void Catalog_EnablesOnlyProvenSpecies()
    {
        var enabled = ExoticSpeciesCatalog.Enabled.Select(d => d.Species).ToHashSet();
        Assert.Equal(new[] { DinoSpecies.Therizinosaurus, DinoSpecies.RaptorHeavy }.ToHashSet(), enabled);
    }

    [Fact]
    public void Catalog_Theri_IsCat8_FreeInStages1And2()
    {
        var theri = ExoticSpeciesCatalog.For(DinoSpecies.Therizinosaurus)!;
        Assert.True(theri.Enabled);
        Assert.Equal(8, theri.Category);
        Assert.True(theri.NeedsCatSlotPatch);
        Assert.True(theri.AllowsStage(1));
        Assert.True(theri.AllowsStage(2));
        Assert.False(theri.AllowsStage(3)); // stage 3's cat8 slot is not verified free
    }

    [Fact]
    public void Catalog_RaptorHeavy_NeedsNoPatch_AnyStage()
    {
        var rh = ExoticSpeciesCatalog.For(DinoSpecies.RaptorHeavy)!;
        Assert.True(rh.Enabled);
        Assert.False(rh.NeedsCatSlotPatch);
        Assert.True(rh.AllowsStage(7)); // no stage constraint when no slot patch
    }

    [Theory]
    [InlineData(DinoSpecies.Tyrannosaurus)]
    [InlineData(DinoSpecies.Pteranodon)]
    [InlineData(DinoSpecies.Swarm)]
    public void Catalog_GatedSpecies_AreDisabled_WithABlocker(DinoSpecies species)
    {
        var def = ExoticSpeciesCatalog.For(species)!;
        Assert.False(def.Enabled);
        Assert.False(string.IsNullOrEmpty(def.Blocker));
    }

    // ---- Pure placement planner (stage-scoping) ----

    private static RoomCandidate Cand(int stage, int room, bool victim = true, params DinoSpecies[] present)
        => new(stage, room, victim, present.Length == 0 ? new[] { DinoSpecies.Velociraptor } : present);

    [Fact]
    public void Planner_PlacesTheri_OnlyInFreeStages()
    {
        var donors = new[] { DinoSpecies.Therizinosaurus };
        // Stage 1 (free) → placed; stage 3 (not free) → nothing, since Theri is the only donor.
        var s1 = CrossSpeciesPlanner.Plan(new[] { Cand(1, 2) }, donors, chance: 1.0, new Random(1));
        var s3 = CrossSpeciesPlanner.Plan(new[] { Cand(3, 1) }, donors, chance: 1.0, new Random(1));
        Assert.Single(s1);
        Assert.Equal(DinoSpecies.Therizinosaurus, s1[0].Species);
        Assert.Empty(s3);
    }

    [Fact]
    public void Planner_RaptorHeavy_PlaceableAnyStage()
    {
        var p = CrossSpeciesPlanner.Plan(new[] { Cand(3, 1) }, new[] { DinoSpecies.RaptorHeavy }, 1.0, new Random(1));
        Assert.Single(p);
        Assert.Equal(DinoSpecies.RaptorHeavy, p[0].Species);
    }

    [Fact]
    public void Planner_SkipsRoomsWithoutAVictim()
    {
        var p = CrossSpeciesPlanner.Plan(new[] { Cand(1, 2, victim: false) },
            new[] { DinoSpecies.Therizinosaurus }, 1.0, new Random(1));
        Assert.Empty(p);
    }

    [Fact]
    public void Planner_NeverPlacesASpeciesARoomAlreadyHas()
    {
        var p = CrossSpeciesPlanner.Plan(
            new[] { Cand(1, 2, true, DinoSpecies.Velociraptor, DinoSpecies.Therizinosaurus) },
            new[] { DinoSpecies.Therizinosaurus }, 1.0, new Random(1));
        Assert.Empty(p); // Theri already present; no other donor → nothing
    }

    [Fact]
    public void Planner_DisabledSpecies_NeverPlaced_EvenIfDonorClaimedAvailable()
    {
        // T-Rex is gated off; even if a donor is "available" the planner only considers enabled species.
        var p = CrossSpeciesPlanner.Plan(new[] { Cand(1, 2) },
            new[] { DinoSpecies.Tyrannosaurus }, 1.0, new Random(1));
        Assert.Empty(p);
    }

    [Fact]
    public void Planner_ChanceZero_PlacesNothing()
    {
        var p = CrossSpeciesPlanner.Plan(new[] { Cand(1, 2) },
            new[] { DinoSpecies.Therizinosaurus }, chance: 0.0, new Random(1));
        Assert.Empty(p);
    }

    [Fact]
    public void Planner_OneCatSlotSpeciesPerStage_TheriRecursConsistently()
    {
        // Multiple free-stage rooms all get the same cat-slot species (one stage-global handler).
        var cands = new[] { Cand(1, 1), Cand(1, 2), Cand(1, 3) };
        var p = CrossSpeciesPlanner.Plan(cands, new[] { DinoSpecies.Therizinosaurus }, 1.0, new Random(7));
        Assert.Equal(3, p.Count);
        Assert.All(p, x => Assert.Equal(DinoSpecies.Therizinosaurus, x.Species));
    }

    [Fact]
    public void Planner_IsDeterministicForSeed()
    {
        var cands = new[] { Cand(1, 1), Cand(2, 1), Cand(3, 1), Cand(1, 2) };
        var donors = new[] { DinoSpecies.Therizinosaurus, DinoSpecies.RaptorHeavy };
        var a = CrossSpeciesPlanner.Plan(cands, donors, 0.6, new Random(99));
        var b = CrossSpeciesPlanner.Plan(cands, donors, 0.6, new Random(99));
        Assert.Equal(a, b);
    }

    // ---- EXE-patch sidecar plan ----

    [Fact]
    public void ExePatchPlan_JsonRoundTrips()
    {
        var plan = new ExePatchPlan(ExePatchPlan.CurrentVersion, new[]
        {
            ExePatchRequest.CatSlot(2, 8, 0x0056BFA0u),
            ExePatchRequest.Cat8HitReaction(6, 3, "st603.dat"),
            ExePatchRequest.RoomEnemySe(2, 3, 6, 5),
        });

        var rt = ExePatchPlan.FromJson(plan.ToJson());

        Assert.Equal(plan.Version, rt.Version);
        Assert.Equal(plan.Requests, rt.Requests); // ExePatchRequest is a value record
    }

    [Fact]
    public void ExePatchRequest_Factories_SetTheRightFields()
    {
        var cat = ExePatchRequest.CatSlot(1, 3, 0x578BA8u);
        Assert.Equal(ExePatchKind.CatSlot, cat.Kind);
        Assert.Equal((1, 3, 0x578BA8u), (cat.Stage, cat.Category, cat.HandlerVa));

        var se = ExePatchRequest.RoomEnemySe(2, 3, 6, 5);
        Assert.Equal(ExePatchKind.RoomEnemySe, se.Kind);
        Assert.Equal((2, 3, 6, 5), (se.Stage, se.Room, se.DonorStage, se.DonorRoom));

        var rx = ExePatchRequest.Cat8HitReaction(6, 3, "st603.dat");
        Assert.Equal(ExePatchKind.Cat8HitReaction, rx.Kind);
        Assert.Equal((6, 3, "st603.dat"), (rx.DonorStage, rx.DonorRoom, rx.DonorRoomFile));
    }

    // ---- Pass gating ----

    [Fact]
    public void Pass_RidesTheEnemyRandomizerByDefault()
    {
        var pass = new CrossSpeciesEnemyPass();

        Assert.True(new RandomizerConfig().CrossRoomEnemySpecies);
        Assert.True(pass.IsEnabled(new RandomizerConfig()));
        Assert.False(pass.IsEnabled(new RandomizerConfig { RandomizeEnemies = false }));
        Assert.False(pass.IsEnabled(new RandomizerConfig { CrossRoomEnemySpecies = false }));
    }

    [Fact]
    public void Pass_NoDonors_PlacesNothing_QueuesNoPatches()
    {
        // A fake importer with no available donors: the pass must no-op.
        var pass = new CrossSpeciesEnemyPass(_ => new FakeImporter(donors: Array.Empty<DinoSpecies>()));
        var ctx = NewContext(Array.Empty<RoomFile>());
        pass.Apply(ctx);
        Assert.Empty(ctx.ExePatchRequests);
    }

    private static RandomizationContext NewContext(IReadOnlyList<RoomFile> rooms)
        => new(new DinoCrisis1(), rooms, RoomGraph.Build(rooms), new Seed(1),
               new RandomizerConfig { CrossRoomEnemySpecies = true }, _ => { });

    private sealed class FakeImporter : ICrossSpeciesImporter
    {
        public FakeImporter(IReadOnlyCollection<DinoSpecies> donors) => AvailableDonors = donors;
        public IReadOnlyCollection<DinoSpecies> AvailableDonors { get; }
        public bool TryImport(RandomizationContext context, RoomFile room, int victimIdx, ExoticSpeciesDef def,
                              out IReadOnlyList<ExePatchRequest> patches, out string note)
        { patches = Array.Empty<ExePatchRequest>(); note = "fake"; return true; }
    }

    // ---- Gated real-install end-to-end ----

    private static readonly DinoCrisis1 Game = new();

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static List<RoomFile>? LoadInstall()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        var refs = Game.EnumerateRooms(root);
        return refs.Count == 0 ? null : refs.Select(r => RoomFile.ReadFromFile(r.Stage, r.Room, r.Path)).ToList();
    }

    /// <summary>Gated real-corpus proof for the swarm→0102 path: the cat5 swarm donor (st307) imports over a
    /// stage-1 raptor (st102) with its texture RELOCATED to a free VRAM region (its own skin, not the shared
    /// raptor (704,0)/(768,511)), and the edited room reparses cleanly carrying the swarm. Validates the room
    /// transform the CLI <c>--swap-species 102 --species swarm</c> performs (the EXE cat5/walker/SE patches are
    /// covered by ExePatcher/GameInstaller tests). Skipped when no game files are present.</summary>
    [Fact]
    public void RealCorpus_SwarmImportsIntoStage1Room_WithItsOwnRelocatedTexture()
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var donorRoom = rooms.FirstOrDefault(r => r.Stage == 3 && r.Room == 0x07); // st307 — 4 cat5 swarm
        var target = rooms.FirstOrDefault(r => r.Stage == 1 && r.Room == 0x02);    // st102 — raptor victim
        if (donorRoom is null || target is null) return; // partial corpus — skip

        var rec = donorRoom.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == DinoSpecies.Swarm);
        Assert.NotNull(rec); // st307 hosts the cat5 swarm

        IEnumerable<int> heads(RoomFile rf)
        {
            foreach (var e in rf.Enemies)
            {
                if (e.OriginalModelPtr >= SpeciesImporter.PsxBase) yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
                if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase) yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
            }
        }

        var donor = SpeciesImporter.ExtractDonor(donorRoom.RdtBuffer, rec!, heads(donorRoom))
            with { Texture = SpeciesImporter.TryExtractTexture(donorRoom.OriginalBytes, donorRoom.RdtBuffer, rec!.OriginalModelPtr) };
        Assert.NotNull(donor.Texture); // the swarm skin (tpage 0x8b / CLUT 0x7ff0) resolves in st307

        // Import over the target's raptor, into a fresh parse so the corpus copy stays clean.
        var work = RoomFile.Read(target.Stage, target.Room, target.OriginalBytes);
        int idx = work.Enemies.IndexOf(work.Enemies.First(e => e.IsRandomizableDino));
        var tex = work.ImportSpeciesTextured(donor, idx);

        // The texture must RELOCATE (own skin in free VRAM), not fall back to geometry-only (raptor skin).
        Assert.Equal(RoomFile.TextureImportOutcome.Relocated, tex.Outcome);
        Assert.NotNull(tex.TextureRect);
        Assert.NotNull(tex.PaletteRect);
        // Relocated off the raptor's shared (704,0) texture column / (768,511) palette row.
        Assert.NotEqual(704, tex.TextureRect!.Value.X);
        Assert.NotEqual(511, tex.PaletteRect!.Value.Y);
        // Small swarm geometry stays well under the resident-pool floor (no clip-strip needed).
        Assert.True(work.RdtBuffer.Length <= SpeciesImporter.ResidentPoolFloor);

        // The edited room reparses cleanly and now carries the cat5 swarm.
        var reread = RoomFile.Read(target.Stage, target.Room, work.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Contains(reread.Enemies, e => e.Species == DinoSpecies.Swarm && e.SpeciesMatchesCategory);
    }

    /// <summary>Gated real-corpus proof for the swarm GROUP placement (docs/dc1/SWARM-0102-GROUP-SPAWN-PLAN.md
    /// Architecture B): <see cref="RoomFile.ImportSpeciesGroupTextured"/> imports the cat5 closure ONCE over the
    /// st102 raptor and adds 3 more members, so st102 ends with 4 cat5 swarm placements that all share the SAME
    /// model+motion pointer (the precondition for the engine to form the pack), the RDT grows by only the
    /// records (not 4 closure copies), and the room round-trips. Skipped without game files.</summary>
    [Fact]
    public void RealCorpus_SwarmGroupImport_PlacesFourMembersSharingOneClosure()
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var donorRoom = rooms.FirstOrDefault(r => r.Stage == 3 && r.Room == 0x07); // st307 — 4 cat5 swarm
        var target = rooms.FirstOrDefault(r => r.Stage == 1 && r.Room == 0x02);    // st102 — raptor victim
        if (donorRoom is null || target is null) return; // partial corpus — skip

        var rec = donorRoom.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == DinoSpecies.Swarm);
        Assert.NotNull(rec);

        IEnumerable<int> heads(RoomFile rf)
        {
            foreach (var e in rf.Enemies)
            {
                if (e.OriginalModelPtr >= SpeciesImporter.PsxBase) yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
                if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase) yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
            }
        }

        var donor = SpeciesImporter.ExtractDonor(donorRoom.RdtBuffer, rec!, heads(donorRoom))
            with { Texture = SpeciesImporter.TryExtractTexture(donorRoom.OriginalBytes, donorRoom.RdtBuffer, rec!.OriginalModelPtr) };
        Assert.NotNull(donor.Texture);

        var work = RoomFile.Read(target.Stage, target.Room, target.OriginalBytes);
        int idx = work.Enemies.IndexOf(work.Enemies.First(e => e.IsRandomizableDino));
        Assert.Equal(0, work.Enemies.Count(e => e.Species == DinoSpecies.Swarm)); // st102 starts raptor, no swarm

        var extra = new (short, short, short, short)[] { (3360, 0, 560, 0), (3760, 0, 560, 0), (3360, 0, 960, 0) };
        // The swarm's coordination-effect op58 (creates the shared type-0x17 effect the members link to).
        byte[] coord = { 0x58, 0x00, 0x17, 0x02, 0x00, 0x00, 0x00, 0x00 };
        var tex = work.ImportSpeciesGroupTextured(donor, idx, extra, coord);

        Assert.Equal(RoomFile.TextureImportOutcome.Relocated, tex.Outcome);
        // The whole group must stay under the resident-pool floor — one closure + records, not 4 closures.
        Assert.True(work.RdtBuffer.Length <= SpeciesImporter.ResidentPoolFloor);
        // The op58 type-0x17 coordination record is present in the RDT (5-byte signature 58 00 17 02 00).
        var sig = new byte[] { 0x58, 0x00, 0x17, 0x02, 0x00 };
        Assert.True(IndexOf(work.RdtBuffer, sig) >= 0, "swarm coordination op58 (type 0x17) not found in RDT");

        var reread = RoomFile.Read(target.Stage, target.Room, work.Write());
        Assert.True(reread.ParsedCleanly);

        // 4 cat5 swarm members (member0 converted from the victim + 3 added), all sharing ONE model+motion ptr.
        var swarm = reread.Enemies.Where(e => e.Species == DinoSpecies.Swarm && e.SpeciesMatchesCategory).ToList();
        var dump = string.Join(" | ", reread.Enemies.Select(e =>
            $"op{e.Opcode:x2} slot{e.Slot} cat{e.Category} {e.Species} match={e.SpeciesMatchesCategory} model={e.ModelPtr:x8} pos=({e.PosX},{e.PosZ})"));
        Assert.True(4 == swarm.Count, $"expected 4 swarm, got {swarm.Count}. enemies[{reread.Enemies.Count}]: {dump}");
        Assert.Single(swarm.Select(e => e.ModelPtr).Distinct());   // one shared model closure
        Assert.Single(swarm.Select(e => e.MotionPtr).Distinct());  // one shared motion closure
        // The 3 added members landed at the requested cluster coords.
        Assert.Contains(swarm, e => e.PosX == 3760 && e.PosZ == 560);
        Assert.Contains(swarm, e => e.PosX == 3360 && e.PosZ == 960);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(1337)]
    public void RealInstall_PlacesExoticAndQueuesPatches_AndEditedRoomsRoundTrip(int seed)
    {
        var rooms = LoadInstall();
        if (rooms is null) return; // no game files (CI) — skip

        var before = rooms.ToDictionary(r => r, r => r.Write());

        var log = new List<string>();
        var ctx = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(seed),
            // Max difficulty raises the placement chance so a placement is reliably attempted.
            new RandomizerConfig { CrossRoomEnemySpecies = true, EnemyDifficulty = 1.0 }, log.Add);

        new CrossSpeciesEnemyPass().Apply(ctx);

        Assert.Contains(log, l => l.Contains("Velociraptor ->"));

        // Any room placed via the output override (the Theri's textured bytes) must reparse cleanly and carry
        // the imported species.
        foreach (var room in rooms)
        {
            if (!ctx.TryGetRoomOutput(room, out var bytes)) continue;
            var reread = RoomFile.Read(room.Stage, room.Room, bytes);
            Assert.True(reread.ParsedCleanly, $"{room} override did not reparse");
        }

        // RaptorHeavy rooms commit atomically into the shared RoomFile. Every changed grounded room must
        // stay within the resident-pool budget and its final imported model must resolve to a texture upload;
        // GeometryOnly is never an installed production outcome.
        foreach (var room in rooms)
        {
            if (before[room].AsSpan().SequenceEqual(room.Write())) continue;
            var reread = RoomFile.Read(room.Stage, room.Room, room.Write());
            Assert.True(reread.ParsedCleanly, $"{room} did not reparse after import");
            Assert.True(reread.RdtBuffer.Length <= SpeciesImporter.ResidentPoolFloor,
                $"{room} grounded import exceeded the resident-pool floor");
            var imported = Assert.Single(reread.Enemies, e =>
                e.Species == DinoSpecies.RaptorHeavy && e.SpeciesMatchesCategory);
            var codes = TextureImporter.ReadModelTextureCodes(reread.RdtBuffer, imported.ModelPtr);
            Assert.NotNull(TextureImporter.ExtractSpeciesTexture(reread.OriginalBytes, codes.Tpages, codes.Clut));
        }

        Assert.DoesNotContain(log, l => l.Contains("geometry only", StringComparison.OrdinalIgnoreCase));

        // If a Theri was placed, the pass must have queued its three EXE patches for a free stage (1/2).
        if (log.Any(l => l.Contains("-> Therizinosaurus")))
        {
            Assert.Contains(ctx.ExePatchRequests, r => r.Kind == ExePatchKind.CatSlot && r.Category == 8);
            Assert.Contains(ctx.ExePatchRequests, r => r.Kind == ExePatchKind.Cat8HitReaction);
            Assert.Contains(ctx.ExePatchRequests, r => r.Kind == ExePatchKind.RoomEnemySe);
            Assert.All(ctx.ExePatchRequests.Where(r => r.Kind == ExePatchKind.CatSlot),
                       r => Assert.Contains(r.Stage, new[] { 1, 2 }));
        }
    }
}
