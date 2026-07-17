using System.Text.Json;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Guards the <see cref="Dc2SpeciesTable"/> registry: the v1 donor-pool invariant, the eligibility
/// predicate, and a <b>lock</b> to <c>data/dc2/enemies.json</c> <c>type_ctor_map</c> so the C# table
/// can't silently drift from the reverse-engineered ground truth.
/// </summary>
public class Dc2SpeciesTableTests
{
    [Fact]
    public void DefaultDonors_AreTheNonBossKnownLandSpecies()
    {
        // v2 (2026-06-30): the pool broadened from dedicated-only to ALL non-boss/Known/LAND species
        // that aren't setpiece-only, so shared-base Inostrancevia (0x0e) joins the dedicated raptor/ovi/
        // allo. Triceratops (0x09, E70) is LAND but a non-threatening SETPIECE enemy, so it is EXCLUDED
        // by default (opt-in only — see DonorPool). Bosses (0x06, 0x03) and aquatic Mosasaurus (0x05) out.
        Assert.Equal(new[] { 0x02, 0x07, 0x08, 0x0e },
                     Dc2SpeciesTable.DefaultDonors.Select(s => s.Type).OrderBy(x => x));
        Assert.All(Dc2SpeciesTable.DefaultDonors, s =>
        {
            Assert.False(s.IsBoss);
            Assert.False(s.IsSetpiece);
            Assert.Equal(Confidence.Known, s.Confidence);
            Assert.Equal(Dc2Habitat.Land, s.Habitat);
        });
        // The shared-base donor is in the default pool — the v2 budget guard (planner) keeps it safe.
        Assert.Contains(Dc2SpeciesTable.DefaultDonors,
                        s => s.Type == 0x0e && s.BaseClass == Dc2BaseClass.Shared640000);
    }

    [Fact]
    public void DefaultDonors_AreAllLand_AndExcludeAquaticSpecies()
    {
        Assert.All(Dc2SpeciesTable.DefaultDonors, s => Assert.Equal(Dc2Habitat.Land, s.Habitat));

        // Aquatic set (K68/K66/K70): 0x05=E30 Plesiosaurus BOSS, 0x0a=E80 Mosasaurus, 0x0b/0x0c=E31/E32
        // Plesiosaurus grunt. All aquatic (crash-on-land via op-0x1a, K62b) ⇒ never in the DEFAULT pool.
        var boss = Dc2SpeciesTable.ForType(0x05)!;
        Assert.Equal("Plesiosaurus (boss form)", boss.Creature);
        Assert.Equal("E30", boss.EFile);
        var mosa = Dc2SpeciesTable.ForType(0x0a)!;
        Assert.Equal("Mosasaurus", mosa.Creature);
        Assert.Equal("E80", mosa.EFile);
        foreach (var t in new[] { 0x05, 0x0a, 0x0b, 0x0c })
        {
            Assert.Equal(Dc2Habitat.Aquatic, Dc2SpeciesTable.ForType(t)!.Habitat);
            Assert.DoesNotContain(t, Dc2SpeciesTable.DefaultDonors.Select(d => d.Type));
        }
    }

    [Fact]
    public void Type09_IsTriceratops_Land_Setpiece_OptInDonorOnly()
    {
        // TYPE 0x09 / E70 RESOLVED LIVE 2026-06-30 (CE cave, ST202): loaded category [scene_mgr+0x680]=9,
        // resident header @0x650000 = 434/62/20 (unique E70 fingerprint), 3 actors [+0x60]=0x650000
        // [+0x58]=9 HP-max 5333, NO CRASH ⇒ LAND. Creature = Triceratops (maintainer in-game ID). But the
        // Triceratops is a non-threatening SETPIECE enemy (deals no damage) → flagged IsSetpiece and kept
        // OUT of the default donor pool; it joins only when the caller opts in (DonorPool(includeSetpiece)).
        var tri = Dc2SpeciesTable.ForType(0x09);
        Assert.NotNull(tri);
        Assert.Equal("Triceratops", tri!.Creature);
        Assert.Equal("E70", tri.EFile);
        Assert.Equal(0x00650000u, tri.ModelBase);
        Assert.Equal(Dc2BaseClass.Dedicated, tri.BaseClass);
        Assert.False(tri.IsBoss);
        Assert.True(tri.IsSetpiece);
        Assert.Equal(Dc2Habitat.Land, tri.Habitat);
        Assert.DoesNotContain(0x09, Dc2SpeciesTable.DefaultDonors.Select(d => d.Type));
    }

    [Fact]
    public void DonorPool_IncludesSetpieceTriceratops_OnlyWhenOptedIn()
    {
        // includeSetpiece:false == the default pool (no Triceratops); true adds the LAND setpiece donors.
        Assert.Equal(Dc2SpeciesTable.DefaultDonors.Select(s => s.Type).OrderBy(x => x),
                     Dc2SpeciesTable.DonorPool(includeSetpiece: false).Select(s => s.Type).OrderBy(x => x));
        Assert.DoesNotContain(0x09, Dc2SpeciesTable.DonorPool(includeSetpiece: false).Select(s => s.Type));

        var withSetpiece = Dc2SpeciesTable.DonorPool(includeSetpiece: true).Select(s => s.Type).ToList();
        Assert.Contains(0x09, withSetpiece);
        // Giganotosaurus (0x06) is now a LAND setpiece (boss→setpiece 2026-07-09), so it joins here too.
        Assert.Equal(new[] { 0x02, 0x06, 0x07, 0x08, 0x09, 0x0e }, withSetpiece.OrderBy(x => x));
        // Still LAND/non-boss/Known — the setpiece flag is the only thing the default pool drops.
        Assert.All(Dc2SpeciesTable.DonorPool(includeSetpiece: true),
                   s => { Assert.False(s.IsBoss); Assert.Equal(Dc2Habitat.Land, s.Habitat); });
    }

    [Fact]
    public void DonorPool_IncludesBosses_OnlyWhenOptedIn()
    {
        // includeBoss:false (default) keeps the boss out; true adds the sole LAND boss donor — T-Rex
        // (0x03, shared), live-proven LAND but degenerate as a trash mob. (Giganotosaurus 0x06 is no
        // longer a boss donor — it moved boss→setpiece 2026-07-09.)
        var noBoss = Dc2SpeciesTable.DonorPool(includeSetpiece: false, includeBoss: false).Select(s => s.Type).ToList();
        Assert.DoesNotContain(0x03, noBoss);
        Assert.DoesNotContain(0x06, noBoss);
        Assert.Equal(Dc2SpeciesTable.DefaultDonors.Select(s => s.Type).OrderBy(x => x), noBoss.OrderBy(x => x));

        var withBoss = Dc2SpeciesTable.DonorPool(includeSetpiece: false, includeBoss: true).Select(s => s.Type).ToList();
        Assert.Contains(0x03, withBoss);
        Assert.DoesNotContain(0x06, withBoss); // Giganotosaurus is a setpiece now, not a boss
        Assert.Equal(new[] { 0x02, 0x03, 0x07, 0x08, 0x0e }, withBoss.OrderBy(x => x).ToArray());
        // The boss is added, but the LAND-only / Known invariant still holds (no aquatic/flyer/unresolved).
        Assert.All(Dc2SpeciesTable.DonorPool(includeSetpiece: false, includeBoss: true),
                   s => { Assert.Equal(Dc2Habitat.Land, s.Habitat); Assert.Equal(Confidence.Known, s.Confidence); });
        // Aquatic species must never enter the pool via the boss flag.
        Assert.DoesNotContain(0x05, withBoss);
        Assert.DoesNotContain(0x0a, withBoss);
    }

    [Fact]
    public void DonorPool_SetpieceAndBoss_Compose()
    {
        // Both opt-ins together = default + setpiece Triceratops (0x09) + bosses (0x03/0x06).
        var both = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true).Select(s => s.Type).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 0x02, 0x03, 0x06, 0x07, 0x08, 0x09, 0x0e }, both);
    }

    [Fact]
    public void Type04_IsPteranodon_E20_Flyer_NotADonor()
    {
        // TYPE 0x04 RESOLVED LIVE 2026-06-30 (CE cave, ST202): loaded category 4, resident header
        // @0x640000 = 358/58/18 (unique E20 fingerprint), 3 actors [+0x58]=4 [+0x60]=0x640000 HP-max
        // 2400, and — refuting the old "flyers also crash" assumption — NO CRASH. Creature = Pteranodon
        // (maintainer in-game ID, a flying dino). FLYER ⇒ excluded from the LAND-only donor pool (its
        // ground behaviour is unverified), even with setpiece opt-in.
        var pt = Dc2SpeciesTable.ForType(0x04);
        Assert.NotNull(pt);
        Assert.Equal("Pteranodon", pt!.Creature);
        Assert.Equal("E20", pt.EFile);
        Assert.Equal(Dc2BaseClass.Shared640000, pt.BaseClass);
        Assert.Equal(Dc2Habitat.Flyer, pt.Habitat);
        Assert.DoesNotContain(0x04, Dc2SpeciesTable.DefaultDonors.Select(d => d.Type));
        Assert.DoesNotContain(0x04, Dc2SpeciesTable.DonorPool(includeSetpiece: true).Select(d => d.Type));
    }

    [Fact]
    public void AquaticTypes_AreNeverLandDonors_AndAbsentUnlessWaterFlagOn()
    {
        // Aquatic set closed live 2026-07-08 (K68): 0x0a=E80 Mosasaurus, 0x0b=E31 / 0x0c=E32 Plesiosaurus
        // grunt (the old "NonLand/unresolved" tags are corrected to Aquatic). The planner's aquatic-native
        // room skip + Dc2RoomEnemySwap.IsAquaticNativeRoom leave their rooms unchanged, like Plesiosaurus
        // boss (0x05).
        foreach (var t in new[] { 0x05, 0x0a, 0x0b, 0x0c })
            Assert.Equal(Dc2Habitat.Aquatic, Dc2SpeciesTable.ForType(t)!.Habitat);

        // None enters the DEFAULT (land-only) pool, even with every opt-in — the water flag is the only
        // door in, and there they are wave-only (planner placement gate).
        foreach (var t in new[] { 0x05, 0x0a, 0x0b, 0x0c })
        {
            Assert.DoesNotContain(t, Dc2SpeciesTable.DefaultDonors.Select(d => d.Type));
            Assert.DoesNotContain(t, Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: true).Select(d => d.Type));
        }
    }

    [Fact]
    public void E32_IsNeverADonor_EvenWithWaterFlagOn_ButE31AndMosasaurusStayEligible()
    {
        // Crash RCA 2026-07-17 (DC2 dump 13-22-55, Dino2.exe): a live E32 (0x0c) grunt swapped onto land
        // crashed with an AV reading NULL+0x40 at 0x00432061 — its per-tick spawner AI (sub 0x431fe0, from
        // state handler 0x430404) dereferences a NULL spawn-anchor at actor+0x210 that only its native
        // aquatic init populates. The fault is in the grunt BEHAVIOR, not the load/residency path, so the
        // wave-preload path does NOT prevent it (the earlier "healthy on spawn" CE snapshot is the delay
        // before the AI ticks into the spawn state). So E32 is hard-excluded from the donor pool in EVERY
        // flag combination — see docs/reference/dc2/_registries/EXE-SYMBOLS.md.
        foreach (var (setpiece, boss, water) in new[]
                 {
                     (false, false, false), (true, true, false),
                     (false, false, true),  (true, false, true), (true, true, true),
                 })
        {
            var pool = Dc2SpeciesTable.DonorPool(setpiece, boss, water).Select(d => d.Type).ToList();
            Assert.DoesNotContain(0x0c, pool);
            Assert.False(Dc2SpeciesTable.IsDonorPoolMember(0x0c, setpiece, boss, water));
        }

        // Boundary: the exclusion is scoped to E32 (0x0c) ONLY. E31 (0x0b), the sibling grunt, is separately
        // live-confirmed NOT to crash (user evidence 2026-07-17) and stays wave-eligible; Mosasaurus (0x0a,
        // the wave-safe aquatic donor) likewise. Both must STILL be admitted when the water flag is on.
        var waterPool = Dc2SpeciesTable.DonorPool(includeSetpiece: true, includeBoss: false, allowWater: true).Select(d => d.Type).ToList();
        Assert.Contains(0x0b, waterPool);
        Assert.Contains(0x0a, waterPool);
    }

    [Fact]
    public void IsEnemyCtorType_ExcludesGenericPlayerAndNonEnemy()
    {
        Assert.True(Dc2SpeciesTable.IsEnemyCtorType(0x02));  // Velociraptor
        Assert.True(Dc2SpeciesTable.IsEnemyCtorType(0x07));  // Oviraptor
        Assert.True(Dc2SpeciesTable.IsEnemyCtorType(0x0e));  // Inostrancevia (shared, still a ctor type)
        Assert.False(Dc2SpeciesTable.IsEnemyCtorType(0x10)); // generic (model from global)
        Assert.False(Dc2SpeciesTable.IsEnemyCtorType(0x00)); // player
        Assert.False(Dc2SpeciesTable.IsEnemyCtorType(0x01)); // partner
        Assert.False(Dc2SpeciesTable.IsEnemyCtorType(0x11)); // effect/item/NPC
    }

    [Fact]
    public void Table_IsLockedTo_EnemiesJson_TypeCtorMap()
    {
        var root = FindRepoRoot();
        if (root is null) return; // no repo → skip (CI without source tree)
        var path = Path.Combine(root, "data", "dc2", "enemies.json");
        if (!File.Exists(path)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var map = doc.RootElement.GetProperty("mapping").GetProperty("type_ctor_map");

        // Every resolved-unique-base (dedicated) row must match the C# table exactly.
        foreach (var row in map.GetProperty("resolved_unique_base").EnumerateObject())
        {
            int type = Convert.ToInt32(row.Name, 16);
            var sp = Dc2SpeciesTable.ForType(type);
            Assert.NotNull(sp);
            Assert.Equal(Dc2BaseClass.Dedicated, sp!.BaseClass);
            Assert.Equal(Convert.ToUInt32(row.Value.GetProperty("base").GetString(), 16), sp.ModelBase);
            Assert.Equal(row.Value.GetProperty("efile").GetString(), sp.EFile);
        }

        // Every shared-0x640000 row must be tagged Shared640000 at base 0x640000.
        foreach (var row in map.GetProperty("shared_640000_group").EnumerateObject())
        {
            if (row.Name.StartsWith('_')) continue; // skip the "_note" key
            int type = Convert.ToInt32(row.Name, 16);
            var sp = Dc2SpeciesTable.ForType(type);
            Assert.NotNull(sp);
            Assert.Equal(Dc2BaseClass.Shared640000, sp!.BaseClass);
            Assert.Equal(0x00640000u, sp.ModelBase);
        }
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DinoRand.sln")))
                return dir.FullName;
        return null;
    }
}
