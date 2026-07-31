using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc1BlueRaptorProfilePassTests
{
    [Fact]
    public void IsEnabled_requires_explicit_opt_in_and_ignores_enemy_randomization_switch()
    {
        var pass = new Dc1BlueRaptorProfilePass();

        Assert.False(pass.IsEnabled(new RandomizerConfig()));
        Assert.False(pass.IsEnabled(new RandomizerConfig { RandomizeEnemies = false }));
        Assert.True(pass.IsEnabled(new RandomizerConfig
        {
            RandomizeEnemies = false,
            Dc1BlueRaptorProfile = true,
        }));
    }

    [Fact]
    public void Apply_UsesOnlyPinnedSt508ForSt102_AndQueuesDescriptorPatch()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrWhiteSpace(root)) return;
        var data = Path.Combine(root, "Data");
        if (!Directory.Exists(data)) return;
        string? targetPath = Directory.EnumerateFiles(data, "*102.dat")
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), "st102.dat", StringComparison.OrdinalIgnoreCase));
        string? donorPath = Directory.EnumerateFiles(data, "*508.dat")
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), "st508.dat", StringComparison.OrdinalIgnoreCase));
        if (targetPath is null || donorPath is null) return;

        var target = RoomFile.Read(1, 2, File.ReadAllBytes(targetPath));
        var donor = RoomFile.Read(5, 8, File.ReadAllBytes(donorPath));
        var rooms = new[] { target, donor };
        var game = new DinoCrisis1();
        var context = new RandomizationContext(game, rooms,
            RoomGraph.Build(rooms, game.Requirements), new Seed(1),
            new RandomizerConfig(), _ => { }, root);

        new Dc1BlueRaptorProfilePass().Apply(context);

        Assert.Single(context.ExePatchRequests);
        Assert.Equal(DinoRand.Randomizer.Install.ExePatchKind.BlueRaptorDescriptor,
            context.ExePatchRequests[0].Kind);
        Assert.True(context.TryGetRoomOutput(target, out var output));
        Assert.NotEqual(target.OriginalBytes, output);
        Assert.Equal((byte)6, target.Enemies.Single(e => e.Species == DinoSpecies.RaptorHeavy).AiParam);
    }
}
