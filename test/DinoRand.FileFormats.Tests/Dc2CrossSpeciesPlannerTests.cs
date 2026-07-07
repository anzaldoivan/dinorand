using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for the pure cross-species swap planner (docs/dc2/CROSS-SPECIES-RANDO-PLAN.md).
/// File-free / RNG-injected, so the eligibility + donor-selection + collision rules are pinned
/// without any game files.
/// </summary>
public class Dc2CrossSpeciesPlannerTests
{
    private static Dc2SpawnRecord Spawn(int type, int slot, int off, int mode = 0) =>
        new(type, mode, off, slot);

    [Fact]
    public void PlanRoom_NoEligibleSpawns_ReturnsEmpty()
    {
        // generic 0x10, item 0x11, player 0x00 — none are species-hardcoded enemy ctors.
        var spawns = new[] { Spawn(0x10, 1, 100), Spawn(0x11, 2, 200), Spawn(0x00, 0, 300) };
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1)));
    }

    [Fact]
    public void PlanRoom_RaptorRoom_ConvertsAllToOneNonNativeDonor()
    {
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x02, 6, 600), Spawn(0x02, 7, 700) };

        var edits = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(42));

        Assert.Equal(3, edits.Count);
        int donor = Assert.Single(edits.Select(e => e.NewType).Distinct()); // per-room single donor
        Assert.NotEqual(0x02, donor);                                       // donor != native
        Assert.Contains(donor, Dc2SpeciesTable.DefaultDonors.Select(d => d.Type));
        Assert.Equal(new[] { 500, 600, 700 }, edits.Select(e => e.ValueOff).OrderBy(x => x));
        Assert.All(edits, e => Assert.Equal(0x02, e.OldType));
    }

    [Fact]
    public void PlanRoom_DonorNeverEqualsNative_AcrossManySeeds()
    {
        var spawns = new[] { Spawn(0x02, 5, 500) };
        for (int s = 0; s < 250; s++)
        {
            var edits = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(s));
            foreach (var e in edits) Assert.NotEqual(0x02, e.NewType);
        }
    }

    [Fact]
    public void PlanRoom_SameSeed_IsDeterministic()
    {
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x02, 6, 600) };
        var a = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(7));
        var b = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(7));
        Assert.Equal(a.Select(e => (e.ValueOff, e.NewType)), b.Select(e => (e.ValueOff, e.NewType)));
    }

    [Fact]
    public void PlanRoom_LeavesGenericAndNonEnemySpawnsUntouched()
    {
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x10, 6, 600), Spawn(0x11, 7, 700) };
        var edits = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(3));
        Assert.Equal(500, Assert.Single(edits).ValueOff); // only the hardcoded raptor spawn
    }

    [Fact]
    public void PlanRoom_SkipsNonLiteralTypeOperands()
    {
        // a TYPE delivered by a global (mode 6), not a literal — cannot be rewritten in the blob.
        var spawns = new[] { Spawn(0x02, 5, 500, mode: 6) };
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1)));
    }

    [Fact]
    public void PlanRoom_AllDonorsNative_ReturnsEmpty()
    {
        // room natively hosts every donor-pool species → no valid distinct donor → leave unchanged.
        // v2: the default pool is {0x02,0x07,0x08,0x0e} (Triceratops 0x09 is setpiece/opt-in, not in it),
        // so the room must host all four for there to be no distinct donor left.
        var spawns = new[]
        {
            Spawn(0x02, 5, 500), Spawn(0x07, 6, 600), Spawn(0x08, 7, 700), Spawn(0x0e, 8, 800),
        };
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1)));
    }

    [Fact]
    public void PlanRoom_MixedNativeSpecies_DonorAvoidsEveryNative()
    {
        // ST202-shape: raptor(0x02) + ovi(0x07) native → valid default donors are Allosaurus(0x08) and
        // shared-base Inostrancevia(0x0e) (Triceratops 0x09 is setpiece/opt-in, not in the default pool);
        // the room has no generic-0x10 spawn so the shared donor is permitted. Picked donor avoids natives.
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x07, 6, 600) };
        var edits = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1));
        Assert.Equal(2, edits.Count);
        int donor = Assert.Single(edits.Select(e => e.NewType).Distinct());
        Assert.Contains(donor, new[] { 0x08, 0x0e });
    }

    // --- v2: aquatic-native room skip (docs/dc2/CROSS-SPECIES-RANDO-PLAN.md) ---

    [Fact]
    public void PlanRoom_SkipsRoomWithNativeAquaticSpecies()
    {
        // ST706-shape: a hardcoded Mosasaurus (0x05, aquatic) present → leave the WHOLE room unchanged
        // (don't convert its intended aquatic enemy to a land donor). Even the land raptor stays.
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x05, 6, 600) };
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1)));
    }

    [Fact]
    public void PlanRoom_Triceratops0x09_IsLand_NotAquaticSkipped()
    {
        // ST80A-shape: a hardcoded Triceratops (0x09, E70) is LAND (live-confirmed, no crash) → the room
        // is NOT aquatic-skipped; 0x09 is an eligible source and converts to a (distinct) land donor.
        var spawns = new[] { Spawn(0x09, 1, 500) };
        var edits = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1));
        Assert.Equal(500, Assert.Single(edits).ValueOff);
        Assert.NotEqual(0x09, Assert.Single(edits).NewType); // donor != native Triceratops
    }

    [Fact]
    public void PlanRoom_AquaticSkip_AppliesEvenToNonLiteralAquaticSpawn()
    {
        // The aquatic native may arrive via a non-literal operand; still skip the room.
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x05, 6, 600, mode: 6) };
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1)));
    }

    [Theory]
    [InlineData(0x0a)] // confirmed AQUATIC (live crash + maintainer visual ID, ST202); native host ST700
    [InlineData(0x0b)] // confirmed NON-LAND (live crash); aquatic-vs-flyer unresolved
    [InlineData(0x0c)] // confirmed NON-LAND (live crash); aquatic-vs-flyer unresolved
    [InlineData(0x04)] // FLYER (Pteranodon) — land replacement spawns outside the level hitbox (live 2026-07-04)
    public void PlanRoom_SkipsRoomWithNativeNonLandSpecies(int nativeType)
    {
        // A room whose native enemy is a confirmed non-land species (0x0a aquatic; 0x0b/0x0c non-land,
        // conservative decision 2026-06-30) must be left UNCHANGED — same as an 0x05 Mosasaurus room —
        // so its intended non-land enemy is never converted to a land donor. Regression for the bug where
        // these were tagged Unknown and slipped past the skip.
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(nativeType, 6, 600) };
        Assert.Empty(Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1)));
    }

    // --- v2: shared-base donor budget guard (≤1 resident 0x640000) ---

    [Fact]
    public void PlanRoom_SharedBaseDonor_ExcludedWhenGenericSpawnPresent()
    {
        // A generic-0x10 spawn may load a different 0x640000 model from a global we can't see, so a
        // shared-base donor could collide → if the only donor offered is shared, leave the room alone.
        var inostra = Dc2SpeciesTable.ForType(0x0e)!; // shared-base Inostrancevia
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x10, 6, 600) };
        var edits = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1), new[] { inostra });
        Assert.Empty(edits);
    }

    [Fact]
    public void PlanRoom_SharedBaseDonor_AllowedWhenNoGenericSpawn()
    {
        // No generic-0x10 spawn → per-room single donor guarantees the donor is the room's sole enemy
        // category, so a shared-base donor is safe.
        var inostra = Dc2SpeciesTable.ForType(0x0e)!;
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x02, 6, 600) };
        var edits = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1), new[] { inostra });
        Assert.Equal(2, edits.Count);
        Assert.Equal(0x0e, Assert.Single(edits.Select(e => e.NewType).Distinct()));
    }

    [Fact]
    public void PlanRoom_DedicatedDonorStillAllowedWhenGenericSpawnPresent()
    {
        // The generic guard only blocks SHARED-base donors; a dedicated donor (Oviraptor 0x07) stays
        // valid in a room with a generic-0x10 spawn.
        var ovi = Dc2SpeciesTable.ForType(0x07)!;
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x10, 6, 600) };
        var edits = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(1), new[] { ovi });
        Assert.Equal(0x07, Assert.Single(edits.Select(e => e.NewType).Distinct()));
        Assert.Equal(500, Assert.Single(edits).ValueOff); // only the hardcoded raptor, not the generic
    }
}
