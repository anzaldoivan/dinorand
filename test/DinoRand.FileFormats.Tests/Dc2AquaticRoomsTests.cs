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
    [Theory]
    [InlineData("700")] // op-0x4f Mosasaurus wave room (K72) — hard-blocked by st_id
    [InlineData("702")]
    [InlineData("703")]
    [InlineData("704")] // Plesiosaurus boss area; enemy is generic-delivered (TYPE-0x10), invisible to the habitat skip
    public void Contains_TrueForTheHardBlockedWaterRooms(string roomKey)
    {
        // The op-0x4f Mosasaurus wave rooms + the generic-delivered ST704: the randomizer must never touch
        // them by default (K72, DC2-AQUATIC-LAND-UNLOCK-FEASIBILITY.md).
        Assert.True(Dc2AquaticRooms.Contains(roomKey));
    }

    [Theory]
    [InlineData("102")] // land raptor room — never aquatic
    [InlineData("706")] // aquatic, but caught by the habitat skip (hardcoded 0x05) — not in the explicit list
    public void Contains_FalseForRoomsNotExplicitlyListed(string roomKey)
    {
        // The explicit list carries ONLY rooms the habitat skip misses; rooms already covered by a
        // hardcoded aquatic ctor stay out of it (no double-bookkeeping).
        Assert.False(Dc2AquaticRooms.Contains(roomKey));
    }
}
