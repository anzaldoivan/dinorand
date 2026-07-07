using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Door-destination edit on a real DC2 room file (<c>ST101.DAT</c>). Operates on an in-memory COPY of
/// the bytes — the original game file is never written. Proves the full edit path:
/// read → rewrite dest → recompress → repack → re-parse → re-decompress, with the LZSS0 round-trip
/// established first (docs/dc2/DOOR-RANDOMIZER-PLAN.md; data/dc2/door-graph.json ST101 → ST201).
///
/// <para>Resolves <c>ST101.DAT</c> from the canonical <b>rebirth</b> DC2 install
/// (<c>4249140_DinoCrisis2/rebirth/Data</c>) relative to the repo root — the rebirth basis (README.md;
/// T0), not english. The ST201 door's dest word sits at the same blob offset in both builds (the door
/// commit is in the build-identical head of slot-5; only the blob tail diverges), so the offset below is
/// build-stable. With the file absent it no-ops (the "no game files → skip" convention).</para>
/// </summary>
public class Dc2DoorEditorTests
{
    // ST101's ST201 door: blob offset of the editable 2-byte dest word (data/dc2/door-graph.json
    // rooms["101"].doors[0].dest_push_off = 42994). 0x0102 = stage 2, room 1 = ST201 on rebirth.
    private const int St201DoorOffset = 0xA7F2;

    // Pristine-aware: the live file may be a randomizer install (see PristineRooms).
    private static byte[]? LoadSt101() => PristineRooms.TryLoad("ST101.DAT");

    [Fact]
    public void Lzss_RoundTrips_RealSt101ScdBlob()
    {
        var package = LoadSt101();
        if (package is null) return; // no game files present → skip

        var blob = Dc2DoorEditor.DecompressScdBlob(package);
        Assert.True(blob.Length > St201DoorOffset + 2, "SCD blob shorter than the known door offset");

        // decode → encode → decode must be byte-identical (the edit path depends on this).
        var reEncoded = Lzss.Compress(blob);
        var reDecoded = Lzss.Decompress(reEncoded);
        Assert.Equal(blob, reDecoded);
    }

    [Fact]
    public void ReadDestination_St201Door_DecodesToStage2Room1()
    {
        var package = LoadSt101();
        if (package is null) return;

        var blob = Dc2DoorEditor.DecompressScdBlob(package);
        var dest = Dc2DoorEditor.ReadDestination(blob, St201DoorOffset);

        Assert.Equal(2, dest.Stage);
        Assert.Equal(1, dest.Room);
        Assert.Equal("201", dest.StId);
        Assert.Equal(0x0102, dest.Word);
    }

    [Fact]
    public void WriteDestination_St201ToSt102_ChangesOnlyTheTwoDestBytes()
    {
        var package = LoadSt101();
        if (package is null) return;

        var originalBlob = Dc2DoorEditor.DecompressScdBlob(package);

        // Retarget the ST201 door to ST102 (stage 1, room 2 = bytes "01 02").
        var edited = Dc2DoorEditor.WriteDestination(package, St201DoorOffset, newStage: 1, newRoom: 2);

        // 1. The original input buffer was not mutated.
        Assert.Equal(2, originalBlob[St201DoorOffset]);
        Assert.Equal(1, originalBlob[St201DoorOffset + 1]);

        // 2. The edited package re-parses and re-decompresses to a blob of the same length.
        var editedBlob = Dc2DoorEditor.DecompressScdBlob(edited);
        Assert.Equal(originalBlob.Length, editedBlob.Length);

        // 3. The dest word now reads ST102 (stage 1, room 2).
        var dest = Dc2DoorEditor.ReadDestination(editedBlob, St201DoorOffset);
        Assert.Equal(1, dest.Stage);
        Assert.Equal(2, dest.Room);
        Assert.Equal("102", dest.StId);

        // 4. Every other byte of the blob is identical — only the 2 dest bytes changed.
        for (int i = 0; i < originalBlob.Length; i++)
            if (i != St201DoorOffset && i != St201DoorOffset + 1)
                Assert.Equal(originalBlob[i], editedBlob[i]);
    }

    [Fact]
    public void WriteDestination_RejectsOutOfRangeRoom()
    {
        var package = LoadSt101();
        if (package is null) return;

        // Stage 0 has only 6 rooms (per-stage counts {6,17,...}); room 6 is out of range.
        Assert.Throws<ArgumentException>(
            () => Dc2DoorEditor.WriteDestination(package, St201DoorOffset, newStage: 0, newRoom: 6));
    }

    [Fact]
    public void IsValidDestination_RespectsPerStageRoomCounts()
    {
        Assert.True(Dc2DoorEditor.IsValidDestination(2, 1));   // ST201
        Assert.True(Dc2DoorEditor.IsValidDestination(1, 16));  // ST110 (stage 1 has 17 rooms)
        Assert.False(Dc2DoorEditor.IsValidDestination(1, 17)); // one past stage 1
        Assert.False(Dc2DoorEditor.IsValidDestination(0, 6));  // stage 0 has 6 rooms (0..5)
        Assert.False(Dc2DoorEditor.IsValidDestination(10, 0)); // no stage 10
    }

}
