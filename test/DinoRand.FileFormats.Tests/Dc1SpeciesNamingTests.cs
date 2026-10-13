using System.Text.Json;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Dc1;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Pins the corrected DC1 species names (STATIC-SCD-RE cont.97). AI category 1 / 15 bones is the
/// <b>human</b> rig — every such model in the 148-room corpus is an upright ~1.8 m figure — and
/// category 2 / 21 bones is the Velociraptor, the 4.4 m tailed body placed in 50+ rooms. The old
/// labels ("Velociraptor" for the 15-bone rig, "RaptorHeavy" for the 21-bone one) stay accepted
/// as input so existing command lines keep working.
/// </summary>
public class Dc1SpeciesNamingTests
{
    [Fact]
    public void FifteenBonesIsTheHumanRig_TwentyOneIsTheVelociraptor()
    {
        Assert.Equal(DinoSpecies.Humanoid, EnemySkeleton.FromBoneCount(15));
        Assert.Equal(DinoSpecies.Humanoid, EnemySkeleton.SpeciesForCategory(1));
        Assert.Equal(DinoSpecies.Velociraptor, EnemySkeleton.FromBoneCount(21));
        Assert.Equal(DinoSpecies.Velociraptor, EnemySkeleton.SpeciesForCategory(2));
    }

    /// <summary>The numeric values are what seed strings and the category byte carry; the rename
    /// must not move them.</summary>
    [Fact]
    public void NumericValuesAreUnchangedByTheRename()
    {
        Assert.Equal(1, (int)DinoSpecies.Humanoid);
        Assert.Equal(2, (int)DinoSpecies.Velociraptor);
    }

    [Theory]
    // the raptor, by its real name and by every name it used to be accepted under
    [InlineData("velociraptor", DinoSpecies.Velociraptor)]
    [InlineData("Velociraptor", DinoSpecies.Velociraptor)]
    [InlineData("raptor", DinoSpecies.Velociraptor)]
    [InlineData("raptorheavy", DinoSpecies.Velociraptor)]
    [InlineData("RaptorHeavy", DinoSpecies.Velociraptor)]
    [InlineData("raptor-heavy", DinoSpecies.Velociraptor)]
    [InlineData("heavy", DinoSpecies.Velociraptor)]
    [InlineData("heavyraptor", DinoSpecies.Velociraptor)]
    // the human rig
    [InlineData("humanoid", DinoSpecies.Humanoid)]
    [InlineData("human", DinoSpecies.Humanoid)]
    [InlineData("npc", DinoSpecies.Humanoid)]
    // untouched neighbours
    [InlineData("theri", DinoSpecies.Therizinosaurus)]
    [InlineData("largeground", DinoSpecies.Tyrannosaurus)]
    [InlineData("swarm", DinoSpecies.Swarm)]
    [InlineData("ptera", DinoSpecies.Pteranodon)]
    public void SpeciesNames_ParseToTheCorrectedSpecies(string text, DinoSpecies expected)
    {
        Assert.True(EnemySkeleton.TryParseSpeciesName(text, out var species));
        Assert.Equal(expected, species);
    }

    [Theory]
    [InlineData("")]
    [InlineData("stegosaurus")]
    public void SpeciesNames_RejectUnknownText(string text)
        => Assert.False(EnemySkeleton.TryParseSpeciesName(text, out _));

    [Fact]
    public void DonorDistribution_NamesTheRaptorVelociraptor()
    {
        var rows = Dc1EnemyDistribution.LoadEmbedded().Rows;
        var raptor = Assert.Single(rows, r => r.Species == DinoSpecies.Velociraptor);
        Assert.Equal("Velociraptor", raptor.Creature);
        Assert.DoesNotContain(rows, r => r.Species == DinoSpecies.Humanoid);
    }

    [Theory]
    [InlineData(1, "Humanoid")]
    [InlineData(2, "Velociraptor")]
    public void EnemiesJson_UsesTheCorrectedNames(int category, string expectedName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "dc1", "enemies.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var row = Assert.Single(doc.RootElement.GetProperty("species").EnumerateArray(),
            r => r.GetProperty("id").GetInt32() == category);
        Assert.StartsWith(expectedName, row.GetProperty("name").GetString());
    }
}
