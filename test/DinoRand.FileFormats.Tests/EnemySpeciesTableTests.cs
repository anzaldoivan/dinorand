using System.Text.Json;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Locks the human-authored reference table <c>data/dc1/enemies.json</c> to the code's source of
/// truth (the <see cref="DinoSpecies"/> enum + <see cref="EnemySkeleton.FromBoneCount"/> +
/// <see cref="EnemySkeleton.SpeciesForCategory"/>). The JSON carries the prose (names, room hints,
/// confidence); the code carries the operative map. These tests fail the moment the two drift, so
/// adding a model class means editing <i>both</i> — never one.
///
/// <para>Each JSON row is one <b>model class</b>: <c>id</c> = AI category, <c>bones</c> = the model
/// skeleton's bone count. Since species and category are decoupled (docs/dc1/STATIC-SCD-RE.md cont.23 —
/// the Tyrannosaurus has two model classes, id 3 / 20 bones and id 4 / 10 bones), the lock is that the
/// two witnesses <i>agree on the species</i> per row, not that <c>id == (int)species</c>.</para>
/// </summary>
public class EnemySpeciesTableTests
{
    private sealed record SpeciesRow(int id, int bones, string name);
    private sealed record SpeciesFile(SpeciesRow[] species);

    private static SpeciesFile Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "dc1", "enemies.json");
        Assert.True(File.Exists(path), $"enemies.json not found at {path}");
        var file = JsonSerializer.Deserialize<SpeciesFile>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(file);
        Assert.NotNull(file!.species);
        return file;
    }

    [Fact]
    public void EnemiesJson_EveryRow_BonesAndCategoryAgreeOnSpecies()
    {
        foreach (var row in Load().species)
        {
            var byBones = EnemySkeleton.FromBoneCount(row.bones);
            var byCategory = EnemySkeleton.SpeciesForCategory(row.id);
            // bones must decode to a known species, and the AI category must imply the same species
            // (the two independent witnesses agree — the decoupled self-check).
            Assert.NotEqual(DinoSpecies.Unknown, byBones);
            Assert.Equal(byBones, byCategory);
        }
    }

    [Fact]
    public void EnemiesJson_CoversEveryEnumValue_WithDistinctClasses()
    {
        var rows = Load().species;

        // Every real species (enum minus Unknown) is produced by at least one row's bone count.
        var coveredSpecies = rows.Select(r => EnemySkeleton.FromBoneCount(r.bones)).ToHashSet();
        foreach (var sp in Enum.GetValues<DinoSpecies>().Where(s => s != DinoSpecies.Unknown))
            Assert.Contains(sp, coveredSpecies);

        // Each row is a distinct model class: AI categories are unique and bone counts are unique
        // (a species may span >1 class — the Tyrannosaurus — so rows.Length need not equal #species).
        Assert.Equal(rows.Length, rows.Select(r => r.id).ToHashSet().Count);
        Assert.Equal(rows.Length, rows.Select(r => r.bones).ToHashSet().Count);
    }
}
