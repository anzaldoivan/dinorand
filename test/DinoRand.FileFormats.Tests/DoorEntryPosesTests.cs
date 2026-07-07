using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for <see cref="DoorEntryPoses.IntoRoom"/> — gathering the player's arrival poses for a room
/// from the door records (in any room) whose destination is that room. These are the guaranteed-walkable
/// floor points used as the <c>--add-enemy</c> position fallback.
/// </summary>
public class DoorEntryPosesTests
{
    private static DoorRecord Door(int stage, int room, short x, short y, short z, short d) => new()
    {
        TargetStage = stage,
        TargetRoom = room,
        EntryX = x,
        EntryY = y,
        EntryZ = z,
        EntryD = d,
    };

    [Fact]
    public void IntoRoom_ReturnsDistinctPosesForMatchingDestinationOnly()
    {
        var doors = new[]
        {
            Door(0x01, 0x0a, 7696, 0, -6896, 0),   // 010D → 010A
            Door(0x01, 0x0a, 7696, 0, -6896, 0),   // a paired/reciprocal door sharing the same arrival point
            Door(0x01, 0x0a, 2624, 0, 3061, 0),    // 010B → 010A (a distinct arrival point)
            Door(0x01, 0x07, 1, 0, 2, 0),          // → 0107 (different room, must be excluded)
        };

        var poses = DoorEntryPoses.IntoRoom(doors, 0x010a);

        Assert.Equal(2, poses.Count); // duplicate collapsed, other-room door excluded
        Assert.Contains(new EntryPose(7696, 0, -6896, 0), poses);
        Assert.Contains(new EntryPose(2624, 0, 3061, 0), poses);
    }

    [Fact]
    public void IntoRoom_EmptyWhenNoDoorLeadsIn()
    {
        var doors = new[] { Door(0x01, 0x07, 1, 0, 2, 0) };
        Assert.Empty(DoorEntryPoses.IntoRoom(doors, 0x010a));
    }

    [Fact]
    public void TargetCode_PacksStageAndRoom()
    {
        Assert.Equal(0x010a, Door(0x01, 0x0a, 0, 0, 0, 0).TargetCode);
    }
}
