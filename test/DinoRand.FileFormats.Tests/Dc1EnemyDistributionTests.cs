using System.Text.Json;
using DinoRand.App;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc1;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Passes;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc1EnemyDistributionTests
{
    [Fact]
    public void Registry_UsesNormalizedProductionCensusAndOnlyTwoDonors()
    {
        var root = FindRepoRoot();
        using var distribution = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "data", "dc1", "enemy-distribution.json")));
        Assert.Equal("normalized_production_placements",
            distribution.RootElement.GetProperty("census_scope").GetString());

        var registry = Dc1EnemyDistribution.LoadEmbedded();
        Assert.Equal(new[] { DinoSpecies.Velociraptor, DinoSpecies.Therizinosaurus },
            registry.Rows.Select(row => row.Species));
        Assert.Equal((byte)46, registry.DefaultWeights[DinoSpecies.Velociraptor]);
        Assert.Equal((byte)9, registry.DefaultWeights[DinoSpecies.Therizinosaurus]);
        Assert.Equal(new[] { DinoSpecies.Velociraptor, DinoSpecies.Therizinosaurus },
            AppSeed.Dc1CanonicalSpeciesOrder);
    }

    [Fact]
    public void Registry_DefaultWeightsMatchAuthoredPlacedEnemyCounts()
    {
        var root = FindRepoRoot();
        using var roomData = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "dc1", "room-data.json")));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var room in roomData.RootElement.GetProperty("rooms").EnumerateObject())
            foreach (var enemy in room.Value.GetProperty("placed_enemies").EnumerateArray())
            {
                var species = enemy.GetProperty("species").GetString()!;
                counts[species] = counts.GetValueOrDefault(species) + enemy.GetProperty("count").GetInt32();
            }
        var registry = Dc1EnemyDistribution.LoadEmbedded();
        Assert.All(registry.Rows, row => Assert.Equal(counts[row.Species.ToString()], row.VanillaCount));
        Assert.Equal(46, counts[nameof(DinoSpecies.Velociraptor)]);
        Assert.Equal(9, counts[nameof(DinoSpecies.Therizinosaurus)]);
    }

    [Fact]
    public void Picker_WeightedZeroAndFixedAvoidDisabledDonors()
    {
        var pool = new[] { DinoSpecies.Velociraptor, DinoSpecies.Therizinosaurus };
        Assert.Equal(DinoSpecies.Therizinosaurus,
            Dc1DonorPicker.Weighted(new Dictionary<DinoSpecies, byte>
                { [DinoSpecies.Velociraptor] = 0, [DinoSpecies.Therizinosaurus] = 1 })
                .Pick(pool, new Random(1)));
        Assert.Null(Dc1DonorPicker.Weighted(new Dictionary<DinoSpecies, byte>()).Pick(pool, new Random(1)));
        Assert.Null(Dc1DonorPicker.Fixed(DinoSpecies.Humanoid).Pick(pool, new Random(1)));
    }

    [Fact]
    public void Picker_FixedConsumesNoRng()
    {
        var rng = new Random(42);
        Assert.Equal(DinoSpecies.Velociraptor,
            Dc1DonorPicker.Fixed(DinoSpecies.Velociraptor)
                .Pick(new[] { DinoSpecies.Therizinosaurus, DinoSpecies.Velociraptor }, rng));
        Assert.Equal(new Random(42).Next(1000), rng.Next(1000));
    }

    [Fact]
    public void Planner_ChanceStreamIsIndependentOfDonorSelectionAndInputOrder()
    {
        var candidates = new[]
        {
            new RoomCandidate(1, 2, true, new[] { DinoSpecies.Humanoid }),
            new RoomCandidate(1, 1, true, new[] { DinoSpecies.Humanoid }),
        };
        var weighted = CrossSpeciesPlanner.Plan(candidates,
            new[] { DinoSpecies.Velociraptor, DinoSpecies.Therizinosaurus }, 0.5,
            new Random(7), Dc1DonorPicker.Weighted(Dc1EnemyDistribution.LoadEmbedded().DefaultWeights));
        var fixedMode = CrossSpeciesPlanner.Plan(candidates.Reverse().ToArray(),
            new[] { DinoSpecies.Velociraptor, DinoSpecies.Therizinosaurus }, 0.5,
            new Random(7), Dc1DonorPicker.Fixed(DinoSpecies.Velociraptor));
        Assert.Equal(weighted.Select(p => (p.Stage, p.Room)), fixedMode.Select(p => (p.Stage, p.Room)));
    }

    [Fact]
    public void Seed_DefaultIsUnchangedAndDistributionOptionsRoundTrip()
    {
        var defaultSeed = SeedString.Encode(new Seed(7), new RandomizerConfig());
        Assert.Equal("DINO-BwAAACOAEBAPBw", defaultSeed);

        var config = new RandomizerConfig
        {
            Dc1EnemyMode = Dc1EnemyDistributionMode.Fixed,
            Dc1FixedSpecies = DinoSpecies.Therizinosaurus,
            Dc1SpeciesWeights = new Dictionary<DinoSpecies, byte>
            {
                [DinoSpecies.Velociraptor] = 0,
                [DinoSpecies.Therizinosaurus] = 15,
            },
        };
        var encoded = SeedString.Encode(new Seed(123), config);
        Assert.True(SeedString.TryParse(encoded, out var seed, out var parsed));
        Assert.Equal(encoded, SeedString.Encode(seed, parsed));
        Assert.Equal(Dc1EnemyDistributionMode.Fixed, parsed.Dc1EnemyMode);
        Assert.Equal(DinoSpecies.Therizinosaurus, parsed.Dc1FixedSpecies);
        Assert.Equal((byte)0, parsed.Dc1SpeciesWeights![DinoSpecies.Velociraptor]);
    }

    [Fact]
    public void LegacyEnemyEnabledSeed_UsesWeightedDefaults()
    {
        var legacy = SeedString.Encode(new Seed(7), new RandomizerConfig());
        Assert.True(SeedString.TryParse(legacy, out _, out var config));
        Assert.Equal(Dc1EnemyDistributionMode.Weighted, config.Dc1EnemyMode);
        Assert.Null(config.Dc1SpeciesWeights);
        Assert.Equal(DinoSpecies.Velociraptor,
            Dc1DonorPicker.Weighted(Dc1EnemyDistribution.LoadEmbedded().DefaultWeights)
                .Pick(new[] { DinoSpecies.Velociraptor, DinoSpecies.Therizinosaurus }, new Random(1)));
    }

    [Fact]
    public void Seed_RejectsReservedDc1ModeAndDisabledFixedSpecies()
    {
        var config = new RandomizerConfig
        {
            Dc1SpeciesWeights = new Dictionary<DinoSpecies, byte>
            {
                [DinoSpecies.Velociraptor] = 1,
                [DinoSpecies.Therizinosaurus] = 1,
            },
        };
        var bytes = Convert.FromBase64String(SeedString.Encode(new Seed(7), config)[5..]
            .Replace('-', '+').Replace('_', '/').PadRight(36, '='));
        bytes[24] = 0x13;
        var invalid = "DINO-" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(SeedString.TryParse(invalid, out _, out _));
        Assert.Throws<ArgumentException>(() => SeedString.Encode(new Seed(7), new RandomizerConfig
        {
            Dc1EnemyMode = Dc1EnemyDistributionMode.Fixed,
            Dc1FixedSpecies = DinoSpecies.Humanoid,
        }));
    }

    [Fact]
    public void Seed_CombinedDc1AndDc2BlocksRoundTripWithoutLeakingState()
    {
        var config = new RandomizerConfig
        {
            Dc1EnemyMode = Dc1EnemyDistributionMode.Fixed,
            Dc1FixedSpecies = DinoSpecies.Velociraptor,
            Dc1SpeciesWeights = new Dictionary<DinoSpecies, byte>
            {
                [DinoSpecies.Velociraptor] = 0,
                [DinoSpecies.Therizinosaurus] = 7,
            },
            Dc2RandomizeRaptorTiers = true,
            Dc2BlueRaptorComboThreshold = 7,
        };
        var encoded = SeedString.Encode(new Seed(321), config);
        Assert.True(SeedString.TryParse(encoded, out var seed, out var parsed));
        Assert.Equal(encoded, SeedString.Encode(seed, parsed));
        Assert.Equal(DinoSpecies.Velociraptor, parsed.Dc1FixedSpecies);
        Assert.Equal((byte)0, parsed.Dc1SpeciesWeights![DinoSpecies.Velociraptor]);
        Assert.True(parsed.Dc2RandomizeRaptorTiers);
        Assert.Equal(7, parsed.Dc2BlueRaptorComboThreshold);
    }

    [Fact]
    public void RawCorpusCharacterization_IsSeparateFromNormalizedRegistry()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrWhiteSpace(root)) return;
        var refs = new DinoCrisis1().EnumerateRooms(root);
        if (refs.Count == 0) return;
        // The randomizer's own room view: campaign stages 1-6 only. (The former 67 also counted the
        // 10 raptor records of the unmapped stage-A rooms, which used to load like campaign rooms.)
        var counts = DinoRand.Randomizer.RandomizerRunner.LoadRooms(new DinoCrisis1(), refs)
            .SelectMany(room => room.Enemies)
            .GroupBy(enemy => enemy.Species)
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(57, counts.GetValueOrDefault(DinoSpecies.Velociraptor));
        Assert.Equal(11, counts.GetValueOrDefault(DinoSpecies.Therizinosaurus));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "dc1", "room-data.json")))
                return dir.FullName;
        throw new DirectoryNotFoundException("repository root not found");
    }
}
