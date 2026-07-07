using System.Text;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The K65 wave-spawn dataset + lever: <see cref="Dc2WaveTable"/> (data/dc2/wave-descriptors.json,
/// tools/dc2_re/scan_wave_descriptors.py), the <see cref="Dc2SpawnEditor.WriteByte"/> /
/// <see cref="Dc2SpawnEditor.ApplyEdits"/> primitives, and
/// <see cref="Dc2CrossSpeciesPlanner.PlanRoomWithWaves"/>. The ST105 offsets pinned here are the
/// exact bytes of the LIVE-VALIDATED Gate-W1/W2 edits (ST105 waves → Oviraptor/T-Rex in-game,
/// docs/dc2/ST105-REAL-SPAWNER-PLAN.md).
/// </summary>
public class Dc2WaveTableTests
{
    // ST105 ground truth (K64/K65): the two wave descriptors and the four dormant ambush spawns.
    private const int Desc0TypeOff = 0x5ed5; // desc @0x5ed4 (also the op-0x23 preload target)
    private const int Desc1TypeOff = 0x6195; // desc @0x6194 (flag-gated variant)

    [Fact]
    public void LoadEmbedded_MatchesTheK65Census()
    {
        var table = Dc2WaveTable.LoadEmbedded();
        Assert.Equal(49, table.Rooms.Count); // rooms arming op-0x4f, corpus census

        var st105 = table.ForRoom("105");
        Assert.NotNull(st105);
        Assert.Equal(2, st105!.Descriptors.Count);
        Assert.Equal(new[] { Desc0TypeOff, Desc1TypeOff },
            st105.Descriptors.Select(d => d.TypeOff).OrderBy(o => o).ToArray());
        Assert.All(st105.Descriptors, d => Assert.Equal(0x02, d.NativeType)); // Velociraptor

        // the four zone-3 ambush spawns (K64 blob offsets), all on E00 0x633000
        Assert.Equal(4, st105.GenericCreatureSpawns.Count);
        Assert.Equal(new[] { 0x91a8, 0x9234, 0x930a, 0x93b0 },
            st105.GenericCreatureSpawns.Select(g => g.TypePushOff).OrderBy(o => o).ToArray());
        Assert.All(st105.GenericCreatureSpawns, g => Assert.Equal(0x633000, g.MbBase));

        // aquatic/non-land-native wave rooms are present in the data (the planner must skip them)
        foreach (var key in new[] { "001", "600", "700", "704" })
            Assert.NotNull(table.ForRoom(key));

        // ST104 (0 op-0x1a spawns) IS wave-armed — that's where its raptors come from; ST202's
        // enemies are all op-0x1a (the K61 live-validated legacy path), so it has no wave data.
        Assert.NotNull(table.ForRoom("104"));
        Assert.Null(table.ForRoom("202"));
    }

    [Fact]
    public void Parse_ReadsDescriptorsAndGenerics()
    {
        const string json = """
        { "rooms": {
            "105": {
              "wave_descriptors": [
                { "desc_off": "0x5ed4", "type_off": "0x5ed5", "variant_off": "0x5ed8",
                  "kind": 11, "native_type": 2, "variant": 0,
                  "arm_sites": [ {"routine": 0, "op_off": "0x8a20"} ], "also_preload": true },
                { "desc_off": "0x6194", "type_off": "0x6195", "variant_off": "0x6198",
                  "kind": 11, "native_type": 2, "variant": 1,
                  "arm_sites": [], "also_preload": true } ],
              "generic_creature_spawns": [
                { "spawn_off": "0x91cc", "type_push_off": "0x91a8", "mb_push_off": "0x91bc",
                  "hp_push_off": "0x91c0", "hp_push_mode": 6, "mb_glb_idx": 13,
                  "mb_base": "0x633000" } ],
              "disarm_sites": 0 }
        } }
        """;
        var table = Dc2WaveTable.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        var room = table.ForRoom("105")!;
        Assert.Equal(new Dc2WaveDescriptor(0x5ed4, 0x5ed5, 2, Armed: true, VariantOff: 0x5ed8, Variant: 0),
            room.Descriptors[0]);
        Assert.Equal(new Dc2WaveDescriptor(0x6194, 0x6195, 2, Armed: false, VariantOff: 0x6198, Variant: 1),
            room.Descriptors[1]);
        Assert.Equal(new Dc2GenericCreatureSpawn(0x91a8, 0x91bc, 0x91c0, 6, 0x633000),
            room.GenericCreatureSpawns[0]);
    }

    // ---- planner -------------------------------------------------------------------------------

    private static Dc2WaveRoom St105Wave() => new(
        new[]
        {
            new Dc2WaveDescriptor(0x5ed4, Desc0TypeOff, 0x02, Armed: true),
            new Dc2WaveDescriptor(0x6194, Desc1TypeOff, 0x02, Armed: true),
        },
        new[]
        {
            new Dc2GenericCreatureSpawn(0x91a8, 0x91bc, 0x91c0, 6, 0x633000),
            new Dc2GenericCreatureSpawn(0x9234, 0x9248, 0x924c, 6, 0x633000),
        });

    [Fact]
    public void PlanRoomWithWaves_OneDonor_EditsDescriptorsAndNormalizesAmbush()
    {
        // ST105 shape: no eligible hardcoded spawns, 2 descriptors, 2 ambush records (subset).
        var spawns = new[] { new Dc2SpawnRecord(0x10, 0, 0x91aa, 2) };
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, St105Wave(), new Random(1));

        Assert.False(plan.IsEmpty);
        int donor = plan.ByteEdits.First(b => b.Offset == Desc0TypeOff).NewValue;
        Assert.True(Dc2SpeciesTable.IsEnemyCtorType(donor));
        Assert.NotEqual(0x02, donor); // never the native raptor

        // both descriptors get the SAME donor byte
        Assert.Equal(donor, plan.ByteEdits.First(b => b.Offset == Desc1TypeOff).NewValue);

        // each ambush record: TYPE word -> donor, MODEL_BASE mode byte -> 0 + word -> 0, HP likewise
        Assert.Contains(plan.WordEdits, w => w.ValueOff == 0x91a8 + 2 && w.NewType == donor);
        Assert.Contains(plan.ByteEdits, b => b.Offset == 0x91bc + 1 && b.NewValue == 0);
        Assert.Contains(plan.WordEdits, w => w.ValueOff == 0x91bc + 2 && w.NewType == 0);
        Assert.Contains(plan.ByteEdits, b => b.Offset == 0x91c0 + 1 && b.NewValue == 0);
        Assert.Contains(plan.WordEdits, w => w.ValueOff == 0x91c0 + 2 && w.NewType == 0);
    }

    [Fact]
    public void PlanRoomWithWaves_IsSeedDeterministicPerRoom()
    {
        var spawns = Array.Empty<Dc2SpawnRecord>();
        var a = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, St105Wave(), new Random(42));
        var b = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, St105Wave(), new Random(42));
        Assert.Equal(a.ByteEdits, b.ByteEdits);
        Assert.Equal(a.WordEdits, b.WordEdits);
    }

    [Fact]
    public void PlanRoomWithWaves_AquaticNativeWaves_LeavesRoomUnchanged()
    {
        // ST700-shape: waves natively TYPE 0x0a (aquatic) — a land donor there is wrong.
        var wave = new Dc2WaveRoom(
            new[] { new Dc2WaveDescriptor(0x1000, 0x1001, 0x0a, Armed: true) },
            Array.Empty<Dc2GenericCreatureSpawn>());
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            Array.Empty<Dc2SpawnRecord>(), wave, new Random(1));
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void PlanRoomWithWaves_NullWave_MatchesLegacyPlanRoom()
    {
        var spawns = new[] { new Dc2SpawnRecord(0x02, 0, 0x1234, 3) };
        var legacy = Dc2CrossSpeciesPlanner.PlanRoom(spawns, new Random(7));
        var combined = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, null, new Random(7));
        Assert.Equal(legacy, combined.WordEdits);
        Assert.Empty(combined.ByteEdits);
    }

    [Fact]
    public void PlanRoomWithWaves_DonorAvoidsWaveNativeAndHardcodedNative()
    {
        // room natively hosts raptor waves AND hardcoded ovi spawns -> donor is neither.
        var wave = new Dc2WaveRoom(
            new[] { new Dc2WaveDescriptor(0x1000, 0x1001, 0x02, Armed: true) },
            Array.Empty<Dc2GenericCreatureSpawn>());
        var spawns = new[] { new Dc2SpawnRecord(0x07, 0, 0x2000, 3) };
        for (int seed = 0; seed < 20; seed++)
        {
            var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(spawns, wave, new Random(seed));
            if (plan.IsEmpty) continue;
            int donor = plan.ByteEdits[0].NewValue;
            Assert.NotEqual(0x02, donor);
            Assert.NotEqual(0x07, donor);
            // the hardcoded spawn is converted to the same donor
            Assert.Contains(plan.WordEdits, w => w.ValueOff == 0x2000 && w.NewType == donor);
        }
    }

    // ---- write primitives on the real room (the exact live-validated Gate-W1 edit) -------------

    [Fact]
    public void WriteByte_St105GateW1Edit_ChangesOnlyTheDescriptorByte()
    {
        var package = LoadRoom("ST105.DAT");
        if (package is null) return;

        Assert.Equal(0x02, Dc2SpawnEditor.ReadByteFromPackage(package, Desc0TypeOff));

        var edited = Dc2SpawnEditor.WriteByte(package, Desc0TypeOff, 0x07); // Gate W1: Oviraptor
        Assert.Equal(0x07, Dc2SpawnEditor.ReadByteFromPackage(edited, Desc0TypeOff));

        // only that one blob byte differs
        var a = Dc2DoorEditor.DecompressScdBlob(package);
        var b = Dc2DoorEditor.DecompressScdBlob(edited);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            if (i != Desc0TypeOff) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void ApplyEdits_FullSt105Plan_RoundTripsInOnePass()
    {
        var package = LoadRoom("ST105.DAT");
        if (package is null) return;

        var table = Dc2WaveTable.LoadEmbedded();
        var graph = Dc2SpawnGraph.LoadEmbedded();
        var plan = Dc2CrossSpeciesPlanner.PlanRoomWithWaves(
            graph.ForRoom("105") ?? Array.Empty<Dc2SpawnRecord>(),
            table.ForRoom("105"), new Random(105));
        Assert.False(plan.IsEmpty);

        var edited = Dc2SpawnEditor.ApplyEdits(package,
            plan.WordEdits.Select(w => (w.ValueOff, (short)w.NewType)),
            plan.ByteEdits.Select(b => (b.Offset, b.NewValue)));

        int donor = plan.ByteEdits.First(b => b.Offset == Desc0TypeOff).NewValue;
        Assert.Equal(donor, Dc2SpawnEditor.ReadByteFromPackage(edited, Desc0TypeOff));
        Assert.Equal(donor, Dc2SpawnEditor.ReadByteFromPackage(edited, Desc1TypeOff));
        // a normalized ambush record: TYPE word = donor, MODEL_BASE push = mode 0 literal 0
        var blob = Dc2DoorEditor.DecompressScdBlob(edited);
        Assert.Equal(donor, Dc2SpawnEditor.ReadOperand(blob, 0x91a8 + 2));
        Assert.Equal(0, blob[0x91bc + 1]);
        Assert.Equal(0, Dc2SpawnEditor.ReadOperand(blob, 0x91bc + 2));
    }

    // Pristine-aware: the live file may be a randomizer install (see PristineRooms).
    private static byte[]? LoadRoom(string name) => PristineRooms.TryLoad(name);
}
