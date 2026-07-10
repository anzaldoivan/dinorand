using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Pins the DC2 setpiece/cutscene-room exclusion list (rooms the enemy randomizer must leave alone because
/// swapping their scripted enemies breaks a set-piece or cutscene). Keyed by st_id, the same string the
/// spawn graph and <see cref="Dc2SpawnGraph.RoomKey"/> use. The <see cref="Dc2RoomExclusions.Cutscene"/>
/// set is loaded from the auto-generated, embedded <c>data/dc2/cutscene-rooms.json</c> (the <c>flagged</c>
/// tier of <c>tools/dc2_re/scan_cutscene_rooms.py</c>).
/// </summary>
public class Dc2RoomExclusionsTests
{
    [Fact]
    public void St407_TurretSetpiece_IsExcluded()
    {
        // ST407 is the turret set-piece (maintainer, 2026-06-30): its 2 hardcoded raptors are part of
        // the scripted sequence, so a cross-species swap would break it. Must be skipped.
        Assert.True(Dc2RoomExclusions.IsExcluded("407"));
        Assert.Contains("407", Dc2RoomExclusions.Setpiece);
    }

    [Fact]
    public void GroundTruthCutsceneRooms_AreFlaggedByTheGeneratedSet()
    {
        // The generated cutscene-rooms.json MUST flag the confirmed cutscene/set-piece ground-truth rooms.
        // ST106 = water-tower cutscene (no E* file); ST101 = dock-arrival intro cutscene; ST407 = turret
        // set-piece (op-0x58 auto-confirm).
        Assert.Contains("106", Dc2RoomExclusions.Cutscene);
        Assert.Contains("101", Dc2RoomExclusions.Cutscene);
        Assert.Contains("407", Dc2RoomExclusions.Cutscene);
        Assert.True(Dc2RoomExclusions.IsExcluded("106"));
        Assert.True(Dc2RoomExclusions.IsExcluded("101"));
    }

    [Theory]
    [InlineData("503")] // Giganotosaurus boss fight (maintainer-confirmed, heavily scripted)
    [InlineData("706")] // Mosasaurus aquatic boss fight
    [InlineData("200")] // Tyrannosaurus encounter
    [InlineData("402")] // Giganotosaurus encounter
    [InlineData("903")] // Tyrannosaurus (endgame)
    [InlineData("80A")] // Triceratops set-piece
    public void HardcodedBossSetpieceRooms_AreFlagged(string roomKey)
    {
        // Rooms carrying a hardcoded (mode-0) boss/set-piece spawn TYPE are auto-confirmed by the scanner
        // (BOSS_SETPIECE_TYPES) — a cross-species swap would break or trivialise the scripted fight.
        Assert.Contains(roomKey, Dc2RoomExclusions.Cutscene);
        Assert.True(Dc2RoomExclusions.IsExcluded(roomKey));
    }

    [Fact]
    public void ScriptedAnimationSetpiece_St102_IsFlagged()
    {
        // ST102 = the first-raptor ambush (maintainer, 2026-06-30). Its scripted actor animation (op-0x46)
        // made the K60 cross-species swap divide-by-zero in the anim transform, so it MUST be excluded.
        // (Corrects the original plan's ground truth, which wrongly listed ST102 as a safe combat room.)
        Assert.Contains("102", Dc2RoomExclusions.Cutscene);
        Assert.True(Dc2RoomExclusions.IsExcluded("102"));
    }

    [Theory]
    [InlineData("105")] // adjacent jungle raptor room — raptors, NO cutscene/scripted-anim (maintainer): safe
    [InlineData("104")] // adjacent jungle room: safe
    [InlineData("107")] // adjacent jungle room: safe
    [InlineData("103")] // adjacent jungle room: safe
    [InlineData("202")] // raptor arena — no scripted animation (op-0x46 0, barrier 0): safe
    public void PlainCombatRooms_AreNotExcluded(string roomKey)
    {
        // Plain raptor rooms with no scripted-animation/barrier signature stay randomizable — the whole
        // point is to swap ST102's neighbours while sparing ST102 itself.
        Assert.False(Dc2RoomExclusions.IsExcluded(roomKey));
        Assert.DoesNotContain(roomKey, Dc2RoomExclusions.Cutscene);
    }

    [Fact]
    public void St001_OpeningTurretSetpiece_IsExcluded_EvenWithWaterToggleOn()
    {
        // ST001 = the opening turret set-piece. Its only prior protection was being a water-native (0x0c)
        // wave room, which Dc2CrossSpeciesPlanner skips ONLY while Dc2AllowWaterLevelEnemySwaps is off —
        // the water toggle lifted that skip and unmasked it. As a hand-curated set-piece exclusion it is
        // now skipped unconditionally: the enemy pass consults IsExcluded BEFORE the allowWater branch,
        // so the toggle cannot reach it. Hand-curated → Setpiece (cutscene-rooms.json is generated).
        Assert.True(Dc2RoomExclusions.IsExcluded("001"));
        Assert.Contains("001", Dc2RoomExclusions.Setpiece);
    }

    [Theory]
    [InlineData("408")] // escort set-piece with an NPC
    [InlineData("409")] // escort set-piece with an NPC
    public void EscortSetpieceRooms_AreExcluded(string roomKey)
    {
        // ST408/ST409 = escort set-pieces with an NPC (maintainer ID 2026-07-11): scripted around their
        // enemies, so the randomizer must leave them vanilla. Hand-curated → Setpiece.
        Assert.True(Dc2RoomExclusions.IsExcluded(roomKey));
        Assert.Contains(roomKey, Dc2RoomExclusions.Setpiece);
    }

    [Fact]
    public void St808_AllosaurusSetpiece_IsExcluded()
    {
        // ST808 = Allosaurus set-piece (maintainer ID 2026-07-11): scripted around its specific enemy, so
        // the randomizer must not remix it. Hand-curated → Setpiece.
        Assert.True(Dc2RoomExclusions.IsExcluded("808"));
        Assert.Contains("808", Dc2RoomExclusions.Setpiece);
    }

    [Fact]
    public void St80A_TriceratopsTurretSetpiece_IsExcluded()
    {
        // ST80A = the Triceratops turret set-piece: auto-confirmed by the scanner (flagged tier), so it is
        // excluded via Dc2RoomExclusions.Cutscene. Reverified 2026-07-11.
        Assert.Contains("80A", Dc2RoomExclusions.Cutscene);
        Assert.True(Dc2RoomExclusions.IsExcluded("80A"));
    }

    [Fact]
    public void St905_ExtraCrisisBonusLevel_IsExcluded()
    {
        // ST905 = Extra Crisis bonus level (maintainer ID 2026-07-04): must stay vanilla, so the enemy
        // randomizer must never touch it. Hand-curated → Setpiece (cutscene-rooms.json is generated).
        Assert.True(Dc2RoomExclusions.IsExcluded("905"));
        Assert.Contains("905", Dc2RoomExclusions.Setpiece);
    }

    [Fact]
    public void IsExcluded_MatchesTheRoomKeyFormat()
    {
        // Exclusions are keyed exactly like Dc2SpawnGraph.RoomKey(stage, room) so the pass can look up
        // by the same string. ST407 = stage 4, room 7.
        Assert.Equal("407", Dc2SpawnGraph.RoomKey(4, 7));
        Assert.True(Dc2RoomExclusions.IsExcluded(Dc2SpawnGraph.RoomKey(4, 7)));
    }
}
