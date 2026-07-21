using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Definitions;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc2ItemRuntimeTests
{
    [Fact]
    public void EmbeddedContract_JoinsEveryFillableLocationToOnePinnedWriterSite()
    {
        var data = Dc2ItemData.LoadEmbedded();

        Assert.Equal(2, data.Version);
        Assert.Equal("ST101", data.StartRoomId);
        Assert.Equal("ST504", data.GoalRoomId);
        Assert.Equal(42, data.Locations.Count);
        Assert.Equal(91, data.ConditionalCommitDispositions.Count);
        Assert.Equal(42, data.Locations.Select(x => x.ApId).Distinct().Count());
        Assert.Equal(42, data.Locations.Select(x => x.SourceId).Distinct().Count());
        Assert.Equal(new[] { 0x22, 0x23, 0x2b }, data.FixedLifecycleItemIds.Order());
        Assert.All(data.Locations, location =>
        {
            Assert.Equal(location.SourceId, location.Site.SourceId);
            Assert.Equal(location.ItemId, location.Site.ExpectedItemId);
            Assert.NotEqual(Dc2ItemEditor.ItemRewriteClass.SpecialKey2f, location.RewriteClass);
        });
        Assert.Single(data.Edges, edge => edge.RequiredItems.SequenceEqual(new[] { 0x2e }));
        Assert.DoesNotContain(data.Locations, location => location.SourceType == "sat1_trigger");
    }

    [Fact]
    public void Planner_IsDeterministic_ConservesClasses_AndProvesVictory()
    {
        var data = Dc2ItemData.LoadEmbedded();

        var first = Dc2ItemPlanner.Plan(data, new Seed(1989), randomizeHealth: true, shuffleKeys: true);
        var again = Dc2ItemPlanner.Plan(data, new Seed(1989), randomizeHealth: true, shuffleKeys: true);
        var other = Dc2ItemPlanner.Plan(data, new Seed(1990), randomizeHealth: true, shuffleKeys: true);

        Assert.Equal(first.Placements.Select(x => (x.SourceId, x.ItemId)),
                     again.Placements.Select(x => (x.SourceId, x.ItemId)));
        Assert.False(first.Placements.Select(x => (x.SourceId, x.ItemId))
            .SequenceEqual(other.Placements.Select(x => (x.SourceId, x.ItemId))));
        Assert.True(first.IsBeatable, first.Diagnostics);
        Assert.Contains("ST504", first.ReachableRooms);
        Assert.NotEmpty(first.Spheres);
        Assert.All(first.Placements.Where(x => data.FixedLifecycleItemIds.Contains(x.OriginalItemId)),
            placement => Assert.Equal(placement.OriginalItemId, placement.ItemId));
        Assert.Equal(
            data.Locations.GroupBy(x => x.RewriteClass).ToDictionary(g => g.Key, g => g.Select(x => x.ItemId).Order().ToArray()),
            first.Placements.GroupBy(x => x.RewriteClass).ToDictionary(g => g.Key, g => g.Select(x => x.ItemId).Order().ToArray()));
    }

    [Fact]
    public void Planner_DisabledClassesRemainVanilla()
    {
        var data = Dc2ItemData.LoadEmbedded();
        var plan = Dc2ItemPlanner.Plan(data, new Seed(7), randomizeHealth: false, shuffleKeys: false);

        Assert.All(plan.Placements, placement => Assert.Equal(placement.OriginalItemId, placement.ItemId));
        Assert.True(plan.IsBeatable, plan.Diagnostics);
    }

    [Fact]
    public void Planner_TogglesAffectOnlyTheirCompatibleClass()
    {
        var data = Dc2ItemData.LoadEmbedded();
        var healthOnly = Dc2ItemPlanner.Plan(data, new Seed(81), randomizeHealth: true, shuffleKeys: false);
        var keysOnly = Dc2ItemPlanner.Plan(data, new Seed(81), randomizeHealth: false, shuffleKeys: true);

        Assert.All(healthOnly.Placements.Where(x => x.RewriteClass == Dc2ItemEditor.ItemRewriteClass.GenericKey),
            placement => Assert.Equal(placement.OriginalItemId, placement.ItemId));
        Assert.All(keysOnly.Placements.Where(x => x.RewriteClass == Dc2ItemEditor.ItemRewriteClass.Health),
            placement => Assert.Equal(placement.OriginalItemId, placement.ItemId));
        Assert.DoesNotContain(healthOnly.Placements, placement => placement.ItemId == 0x1e);
        Assert.All(healthOnly.Placements.Where(x => x.RewriteClass == Dc2ItemEditor.ItemRewriteClass.Health),
            placement => Assert.Contains(placement.ItemId, new[] { 0x1a, 0x1b, 0x1c, 0x1d, 0x1f }));
    }

    [Fact]
    public void StaticApPlan_RequiresVersionFullCoverageExactIdentityAndSameClass()
    {
        var data = Dc2ItemData.LoadEmbedded();
        var vanilla = data.Locations
            .Select(x => new Dc2ApPlacementInstaller.StaticPlacement(x.ApId, x.SourceId, x.ItemId))
            .ToArray();

        var plan = Dc2ApPlacementInstaller.CreatePlan(2, vanilla);
        Assert.Equal(42, plan.PlacementCount);
        Assert.Equal(data.Locations.Select(x => x.RoomId).Distinct().Count(), plan.Rooms.Count);
        var slotPlan = Dc2ApPlacementInstaller.CreatePlanFromSlotData(
            2,
            vanilla.ToDictionary(x => x.ApLocationId.ToString(), x => x.ItemId),
            vanilla.ToDictionary(x => x.ApLocationId.ToString(), x => x.SourceId));
        Assert.Equal(plan.PlacementCount, slotPlan.PlacementCount);

        Assert.Throws<InvalidOperationException>(() => Dc2ApPlacementInstaller.CreatePlan(1, vanilla));
        Assert.Throws<InvalidOperationException>(() => Dc2ApPlacementInstaller.CreatePlan(2, vanilla[..^1]));
        Assert.Throws<InvalidOperationException>(() => Dc2ApPlacementInstaller.CreatePlan(2,
            vanilla.Select((x, i) => i == 0 ? x with { ItemId = -1 } : x).ToArray()));
        Assert.Throws<InvalidOperationException>(() => Dc2ApPlacementInstaller.CreatePlan(2,
            vanilla.Select((x, i) => i == 0 ? x with { SourceId = "wrong" } : x).ToArray()));

        var health = data.Locations.First(x => x.RewriteClass == Dc2ItemEditor.ItemRewriteClass.Health);
        var key = data.Locations.First(x => x.RewriteClass == Dc2ItemEditor.ItemRewriteClass.GenericKey);
        Assert.Throws<InvalidOperationException>(() => Dc2ApPlacementInstaller.CreatePlan(2,
            vanilla.Select(x => x.ApLocationId == health.ApId ? x with { ItemId = key.ItemId } : x).ToArray()));

        var fixedKey = data.Locations.First(x => x.ItemId == 0x22);
        var movableKey = data.Locations.First(x => x.ItemId == 0x24);
        Assert.Throws<InvalidOperationException>(() => Dc2ApPlacementInstaller.CreatePlan(2,
            vanilla.Select(x => x.ApLocationId == fixedKey.ApId ? x with { ItemId = movableKey.ItemId }
                : x.ApLocationId == movableKey.ApId ? x with { ItemId = fixedKey.ItemId }
                : x).ToArray()));
    }

    [Fact]
    public void Definition_EnablesItemsAndKeys_WithRealStartGoalAndFixturePool()
    {
        var game = new DinoCrisis2();
        var data = Dc2ItemData.LoadEmbedded();

        Assert.True(game.Supports(GameFeature.Items));
        Assert.True(game.Supports(GameFeature.KeyItems));
        Assert.Equal(0x101, game.StartRoomCode);
        Assert.Equal(0x504, game.GoalRoomCode);
        Assert.Equal(20, game.KeyItemIds.Count);
        Assert.Equal(35, game.ItemPool.Count);
        Assert.Equal(
            data.Locations.Where(x => x.RewriteClass == Dc2ItemEditor.ItemRewriteClass.Health)
                .Select(x => x.ItemId).Order(),
            game.ItemPool.Select(x => x.ItemId).Order());
        Assert.All(game.ItemPool, x => Assert.Equal(1d, x.Weight));
    }

    [Fact]
    public void Pass_PartialLayoutSkipsMissingRooms_AndRecordsProof()
    {
        var log = new List<string>();
        var sink = new CaptureSink();
        var context = new Dc2RandomizationContext(
            new DinoCrisis2(),
            Array.Empty<Dc2RoomFile>(),
            new Seed(44),
            new RandomizerConfig { RandomizeItems = true, ShuffleKeyItems = true },
            log.Add,
            sink);

        new Dc2ItemRandomizer().Apply(context);

        Assert.Empty(sink.Rooms);
        Assert.Contains(log, line => line.Contains("not among the loaded rooms", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains("proved 42/42 locations", StringComparison.Ordinal));
        var section = Assert.Single(context.Spoiler.Sections, x => x.Title == "Items and key items (DC2)");
        Assert.Equal(42, section.Rows.Count);
        Assert.Contains(section.Notes, note => note.StartsWith("Beatability:", StringComparison.Ordinal));
        Assert.Contains(section.Notes, note => note.StartsWith("Partial layout:", StringComparison.Ordinal));
    }

    [Fact]
    public void RealCorpus_AllFillableSitesValidateInRoomBatchesWithoutWriting()
    {
        string? root = NormalizeInstallPath(Environment.GetEnvironmentVariable("DINORAND_DC2_DIR"));
        if (root is null) return;
        string? dataDir = new[] { Path.Combine(root, "rebirth", "Data"), Path.Combine(root, "Data") }
            .FirstOrDefault(Directory.Exists);
        if (dataDir is null) return;

        var data = Dc2ItemData.LoadEmbedded();
        int validated = 0;
        foreach (var group in data.Locations.GroupBy(x => x.RoomId, StringComparer.Ordinal))
        {
            string fileName = group.Key + ".DAT";
            string? path = new[] { Path.Combine(dataDir, ".dinorand_backup"), dataDir }
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateFiles)
                .FirstOrDefault(candidate => string.Equals(
                    Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase));
            Assert.True(path is not null, $"missing pristine room {fileName}");
            byte[] input = File.ReadAllBytes(path!);
            byte[] snapshot = input.ToArray();
            byte[] output = Dc2ItemEditor.ApplyEdits(
                input,
                group.Key,
                group.Select(location => new Dc2ItemEditor.ItemEdit(location.Site, location.ItemId)).ToArray());
            Assert.Equal(snapshot, input);
            Assert.Equal(Dc2DoorEditor.DecompressScdBlob(input), Dc2DoorEditor.DecompressScdBlob(output));
            validated += group.Count();
        }
        Assert.Equal(42, validated);
    }

    private static string? NormalizeInstallPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (OperatingSystem.IsWindows() && value.StartsWith("/mnt/", StringComparison.Ordinal)
            && value.Length > 7 && value[6] == '/')
            return char.ToUpperInvariant(value[5]) + ":\\" + value[7..].Replace('/', '\\');
        return value;
    }

    private sealed class CaptureSink : IDc2OutputSink
    {
        public List<(Dc2RoomFile Room, byte[] Bytes)> Rooms { get; } = new();
        public void Emit(Dc2RoomFile room, byte[] bytes) => Rooms.Add((room, bytes));
        public void EmitFile(string fileName, byte[] bytes) { }
    }
}
