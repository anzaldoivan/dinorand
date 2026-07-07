using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Pins the explicit aquatic-room registry (<see cref="Dc2AquaticRooms"/>): the underwater rooms whose
/// aquatic enemy is delivered through the GENERIC TYPE-0x10 spawn path, so the habitat-driven skip (which
/// only sees hardcoded ctor TYPEs 0x05/0x0a/0x0b/0x0c) can't detect them and they must be protected by
/// st_id (docs/dc2/CROSS-SPECIES-RANDO-PLAN.md).
/// </summary>
public class Dc2AquaticRoomsTests
{
    [Fact]
    public void Contains_TrueForKnownGenericDeliveredAquaticRoom()
    {
        // ST704: underwater; its enemy is a Mosasaurus (E30, aquatic) delivered via the generic TYPE-0x10
        // spawn path (live map: E30=Mosasaurus ST704), so the spawn-habitat skip can't see it.
        Assert.True(Dc2AquaticRooms.Contains("704"));
    }

    [Theory]
    [InlineData("102")] // land raptor room — never aquatic
    [InlineData("700")] // aquatic, but caught by the habitat skip (hardcoded 0x0a) — not in the explicit list
    [InlineData("706")] // aquatic, but caught by the habitat skip (hardcoded 0x05) — not in the explicit list
    public void Contains_FalseForRoomsNotExplicitlyListed(string roomKey)
    {
        // The explicit list carries ONLY rooms the habitat skip misses; rooms already covered by a
        // hardcoded aquatic ctor stay out of it (no double-bookkeeping).
        Assert.False(Dc2AquaticRooms.Contains(roomKey));
    }
}
