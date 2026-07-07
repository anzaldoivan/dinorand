using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for the pure single-room FORCED enemy-swap helper behind the CLI op
/// <c>--dc2-swap-enemies</c> (docs/dc2/CROSS-SPECIES-RANDO-PLAN.md). Unlike
/// <see cref="Dc2CrossSpeciesPlanner"/> (random non-native donor, collision-avoidance, aquatic-room
/// skip), this converts EVERY eligible hardcoded enemy spawn in one room to ONE caller-chosen donor —
/// the file-edit twin of the CE cave. RNG-free / file-free, so donor resolution, the LAND guard, and
/// the eligibility/edit rules are pinned without any game files.
/// </summary>
public class Dc2RoomEnemySwapTests
{
    private static Dc2SpawnRecord Spawn(int type, int slot, int off, int mode = 0) =>
        new(type, mode, off, slot);

    // --- ResolveDonor: creature name (case/space-insensitive) or TYPE literal (0xNN / decimal) ---

    [Theory]
    [InlineData("oviraptor", 0x07)]
    [InlineData("Oviraptor", 0x07)]
    [InlineData("OVIRAPTOR", 0x07)]
    [InlineData("Velociraptor", 0x02)]
    [InlineData("inostrancevia", 0x0e)]
    public void ResolveDonor_ByCreatureName_IsCaseInsensitive(string spec, int expectedType)
    {
        var donor = Dc2RoomEnemySwap.ResolveDonor(spec);
        Assert.NotNull(donor);
        Assert.Equal(expectedType, donor!.Type);
    }

    [Theory]
    [InlineData("0x07", 0x07)]
    [InlineData("0x0e", 0x0e)]
    [InlineData("7", 0x07)]   // bare decimal
    [InlineData("14", 0x0e)]
    public void ResolveDonor_ByTypeLiteral_HexOrDecimal(string spec, int expectedType)
    {
        var donor = Dc2RoomEnemySwap.ResolveDonor(spec);
        Assert.NotNull(donor);
        Assert.Equal(expectedType, donor!.Type);
    }

    [Theory]
    [InlineData("dodo")]       // unknown creature
    [InlineData("0x20")]       // not an enemy ctor TYPE
    [InlineData("0x10")]       // generic spawn TYPE — not a species ctor
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDonor_Unknown_ReturnsNull(string spec)
    {
        Assert.Null(Dc2RoomEnemySwap.ResolveDonor(spec));
    }

    [Fact]
    public void ResolveDonor_ResolvesNonLandTypes_ForClassification()
    {
        // Aquatic / flyer / unresolved RESOLVE (so the CLI can name them in its error/warning), but they
        // are not safe donors — that gate is IsSafeLandDonor below.
        Assert.Equal(0x05, Dc2RoomEnemySwap.ResolveDonor("mosasaurus")!.Type);  // aquatic
        Assert.Equal(0x04, Dc2RoomEnemySwap.ResolveDonor("pteranodon")!.Type);  // flyer
        Assert.Equal(0x0a, Dc2RoomEnemySwap.ResolveDonor("0x0a")!.Type);        // unresolved
    }

    // --- IsSafeLandDonor: the LAND-only guard (anything else needs --allow-unsafe) ---

    [Fact]
    public void IsSafeLandDonor_TrueOnlyForLand()
    {
        Assert.True(Dc2RoomEnemySwap.IsSafeLandDonor(Dc2SpeciesTable.ForType(0x02)!)); // Velociraptor LAND
        Assert.True(Dc2RoomEnemySwap.IsSafeLandDonor(Dc2SpeciesTable.ForType(0x03)!)); // T-Rex LAND (boss)
        Assert.True(Dc2RoomEnemySwap.IsSafeLandDonor(Dc2SpeciesTable.ForType(0x09)!)); // Triceratops LAND (setpiece)
        Assert.False(Dc2RoomEnemySwap.IsSafeLandDonor(Dc2SpeciesTable.ForType(0x05)!)); // Mosasaurus AQUATIC
        Assert.False(Dc2RoomEnemySwap.IsSafeLandDonor(Dc2SpeciesTable.ForType(0x04)!)); // Pteranodon FLYER
        Assert.False(Dc2RoomEnemySwap.IsSafeLandDonor(Dc2SpeciesTable.ForType(0x0a)!)); // AQUATIC
    }

    // --- EligibleSpawns + Plan ---

    [Fact]
    public void EligibleSpawns_KeepsLiteralEnemyCtors_DropsGenericAndNonLiteral()
    {
        var spawns = new[]
        {
            Spawn(0x02, 5, 500),            // literal raptor — eligible
            Spawn(0x10, 6, 600),            // generic — not a species ctor
            Spawn(0x11, 7, 700),            // item — not an enemy
            Spawn(0x07, 8, 800, mode: 6),   // ovi via a global — not a literal
        };
        var eligible = Dc2RoomEnemySwap.EligibleSpawns(spawns);
        Assert.Equal(new[] { 500 }, eligible.Select(s => s.TypeValueOff));
    }

    [Fact]
    public void Plan_ConvertsEveryEligibleSpawnToDonor()
    {
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x02, 6, 600), Spawn(0x07, 7, 700) };
        var edits = Dc2RoomEnemySwap.Plan(spawns, donorType: 0x08); // Allosaurus

        Assert.Equal(3, edits.Count);
        Assert.All(edits, e => Assert.Equal(0x08, e.NewType));
        Assert.Equal(new[] { 500, 600, 700 }, edits.Select(e => e.ValueOff).OrderBy(x => x));
        // OldType is preserved per spawn (used by the CLI summary).
        Assert.Contains(edits, e => e.ValueOff == 700 && e.OldType == 0x07);
    }

    // --- IsAquaticNativeRoom: the room-level guard (parity with the bulk planner's aquatic-room skip) ---

    [Fact]
    public void IsAquaticNativeRoom_TrueWhenAnySpawnIsAquatic()
    {
        // ST706-shape: a hardcoded Mosasaurus (0x05, aquatic) present ⇒ the room is aquatic-native.
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x05, 6, 600) };
        Assert.True(Dc2RoomEnemySwap.IsAquaticNativeRoom(spawns));
    }

    [Fact]
    public void IsAquaticNativeRoom_ChecksEvenNonLiteralSpawns()
    {
        // The aquatic native may arrive via a non-literal operand; still flag the room (mirrors the planner).
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x05, 6, 600, mode: 6) };
        Assert.True(Dc2RoomEnemySwap.IsAquaticNativeRoom(spawns));
    }

    [Fact]
    public void IsAquaticNativeRoom_FalseForAllLandRoom()
    {
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x07, 6, 600), Spawn(0x09, 7, 700) };
        Assert.False(Dc2RoomEnemySwap.IsAquaticNativeRoom(spawns));
    }

    [Theory]
    [InlineData(0x0a)] // confirmed AQUATIC (native host ST700)
    [InlineData(0x0b)] // confirmed NON-LAND (unresolved; conservative skip 2026-06-30)
    [InlineData(0x0c)] // confirmed NON-LAND (unresolved; conservative skip 2026-06-30)
    [InlineData(0x04)] // FLYER (Pteranodon) — a land replacement spawns outside the level hitbox (live 2026-07-04)
    public void IsAquaticNativeRoom_TrueForNonLandNatives(int nativeType)
    {
        // Parity with the bulk planner's skip: a room natively hosting a confirmed non-land species is
        // flagged so the CLI refuses it (unless --force), same as an 0x05 Mosasaurus room.
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(nativeType, 6, 600) };
        Assert.True(Dc2RoomEnemySwap.IsAquaticNativeRoom(spawns));
    }

    [Fact]
    public void IsAquaticNativeRoom_WithRoomKey_FlagsExplicitlyListedAquaticRoom()
    {
        // ST704's aquatic enemy (Mosasaurus) is delivered by the generic TYPE-0x10 path, so its spawns
        // carry no aquatic ctor TYPE — the spawn-only check is false. The room-key overload additionally
        // consults the explicit aquatic-room list (Dc2AquaticRooms), catching ST704.
        var genericSpawns = new[] { Spawn(0x10, 5, 500), Spawn(0x13, 6, 600) };
        Assert.False(Dc2RoomEnemySwap.IsAquaticNativeRoom(genericSpawns));          // spawn-only: not seen
        Assert.True(Dc2RoomEnemySwap.IsAquaticNativeRoom(genericSpawns, "704"));    // + room key: protected
        Assert.False(Dc2RoomEnemySwap.IsAquaticNativeRoom(genericSpawns, "102"));   // land room: not protected
    }

    [Fact]
    public void IsAquaticNativeRoom_WithRoomKey_StillFlagsHardcodedAquaticNative()
    {
        // The overload subsumes the spawn-habitat check: a hardcoded aquatic native is flagged regardless
        // of whether the room key is in the explicit list.
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x05, 6, 600) };
        Assert.True(Dc2RoomEnemySwap.IsAquaticNativeRoom(spawns, "706"));
    }

    [Fact]
    public void IsAquaticNativeRoom_WaveOverload_TrueForFlyerNativeWave()
    {
        // A wave natively spawning the Flyer (0x04) also protects the room — a land donor placed on a
        // flyer wave lands outside the level hitbox (unreachable, live-verified 2026-07-04). Note the
        // skip is whole-room: any co-resident land spawns are left unchanged too.
        var wave = new Dc2WaveRoom(
            new[] { new Dc2WaveDescriptor(0x1000, 0x1001, 0x04, Armed: true) },
            Array.Empty<Dc2GenericCreatureSpawn>());
        Assert.True(Dc2RoomEnemySwap.IsAquaticNativeRoom(Array.Empty<Dc2SpawnRecord>(), "102", wave));
    }

    [Fact]
    public void Plan_NoEligibleSpawns_ReturnsEmpty()
    {
        var spawns = new[] { Spawn(0x10, 5, 500), Spawn(0x11, 6, 600) };
        Assert.Empty(Dc2RoomEnemySwap.Plan(spawns, donorType: 0x08));
    }

    [Fact]
    public void Plan_ForcedSwap_DoesNotAvoidNativeDonor()
    {
        // FORCED (unlike the planner): converting raptors to a native-present donor is allowed — the CLI
        // is a manual, explicit test tool (the CE cave behaved exactly this way).
        var spawns = new[] { Spawn(0x02, 5, 500), Spawn(0x07, 6, 600) };
        var edits = Dc2RoomEnemySwap.Plan(spawns, donorType: 0x07); // donor == a native species
        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Equal(0x07, e.NewType));
    }
}
