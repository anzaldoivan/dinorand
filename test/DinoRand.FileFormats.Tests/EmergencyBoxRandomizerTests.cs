using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Logic;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The emergency-box record reader/writer (<see cref="EmergencyBoxRecord"/>) and
/// <see cref="EmergencyBoxRandomizer"/>, on synthetic room packages (no game files): every vanilla box
/// location of <see cref="EmergencyBoxTests.VanillaBoxes"/> plus revisit copies, and plug pickups.
/// </summary>
public class EmergencyBoxRandomizerTests
{
    private const byte Plug = 0x2B;
    private static readonly DinoCrisis1 Game = new();

    private static byte[] BoxRec(byte id, byte cost)
    {
        var r = new byte[DcOpcodes.EmergencyBoxLength];
        r[0] = DcOpcodes.Item;
        r[2] = DcOpcodes.EmergencyBoxSubtype;
        r[EmergencyBoxRecord.BoxIdOffset] = id;
        r[EmergencyBoxRecord.PlugCostOffset] = cost;
        r[0x1e] = 0x5A; // a byte after the cost, which must survive edits
        return r;
    }

    private static byte[] PlugRec(ushort take)
    {
        var r = new byte[DcOpcodes.ItemLength];
        r[0] = DcOpcodes.Item;
        r[2] = DcOpcodes.ItemSubtype;
        r[ItemRecord.IdOffset] = Plug;
        r[ItemRecord.CountOffset] = 1;
        r[ItemRecord.TakeIndexOffset] = (byte)take;
        r[ItemRecord.TakeIndexOffset + 1] = (byte)(take >> 8);
        r[ItemRecord.DisplaySlotOffset] = ItemRecord.NoDisplaySlot;
        return r;
    }

    /// <summary>A decompressed RDT with one subroutine holding <paramref name="records"/>.</summary>
    internal static byte[] Rdt(params byte[][] records)
    {
        const int table = 0x24;
        var buf = new byte[table + 4 + records.Sum(r => r.Length)];
        BitConverter.TryWriteBytes(buf.AsSpan(0x14), RoomScript.PsxRdtBase + table);
        BitConverter.TryWriteBytes(buf.AsSpan(table), 4u);
        int pos = table + 4;
        foreach (var r in records) { r.CopyTo(buf, pos); pos += r.Length; }
        return buf;
    }

    /// <summary>The value of CLUT word <paramref name="k"/> of the synthetic palette (768,505)–(1023,509) in room
    /// <paramref name="code"/>: distinct per room, with the STP bit on even words.</summary>
    private static ushort PaletteWord(int code, int k) => (ushort)(((code * 97 + k) & 0x7FFF) | (k % 2 == 0 ? 0x8000 : 0));

    /// <summary>A DC1 room package: a palette entry at VRAM (768,505), 256×5, then <paramref name="rdt"/>, raw,
    /// loaded at 0x80100000.</summary>
    private static byte[] Package(byte[] rdt, int code = 0)
    {
        const int paletteSize = 256 * 5 * 2, paletteAligned = 4096;
        var b = new byte[GianPackage.HeaderSize + paletteAligned + ((rdt.Length + 2047) & ~2047)];
        BitConverter.TryWriteBytes(b.AsSpan(0), (uint)GianEntryType.Palette);
        BitConverter.TryWriteBytes(b.AsSpan(4), (uint)paletteSize);
        BitConverter.TryWriteBytes(b.AsSpan(8), (ushort)768);
        BitConverter.TryWriteBytes(b.AsSpan(10), (ushort)505);
        BitConverter.TryWriteBytes(b.AsSpan(12), (ushort)256);
        BitConverter.TryWriteBytes(b.AsSpan(14), (ushort)5);
        BitConverter.TryWriteBytes(b.AsSpan(20), (uint)rdt.Length);
        BitConverter.TryWriteBytes(b.AsSpan(26), (ushort)0x8010);
        for (int k = 0; k < paletteSize / 2; k++)
            BitConverter.TryWriteBytes(b.AsSpan(GianPackage.HeaderSize + 2 * k), PaletteWord(code, k));
        rdt.CopyTo(b, GianPackage.HeaderSize + paletteAligned);
        return b;
    }

    private static readonly (int Stage, int Room, int Id)[] Copies = { (0xA, 0x13, 0), (0xA, 0x05, 8), (0xC, 0x02, 5), (0xC, 0x02, 14) };

    /// <summary>Every vanilla box in its room, the <see cref="Copies"/>, and <paramref name="plugs"/> plug pickups
    /// on distinct take flags (plus one duplicate record of the first flag) in the start room. The copies are
    /// opaque, as <see cref="RandomizerRunner.LoadRooms"/> loads stages 7-C.</summary>
    private static List<RoomFile> World(int plugs = 26)
    {
        var records = new Dictionary<(int, int), List<byte[]>>();
        void Add(int stage, int room, byte[] rec)
        {
            if (!records.TryGetValue((stage, room), out var list)) records[(stage, room)] = list = new();
            list.Add(rec);
        }
        foreach (var (id, code, cost) in EmergencyBoxTests.VanillaBoxes)
            Add(code >> 8, code & 0xff, BoxRec((byte)id, (byte)cost));
        foreach (var (stage, room, id) in Copies)
            Add(stage, room, BoxRec((byte)id, (byte)EmergencyBoxTests.VanillaBoxes[id].Cost));
        for (int i = 0; i < plugs; i++) Add(1, 0x01, PlugRec((ushort)(100 + i)));
        if (plugs > 0) Add(1, 0x01, PlugRec(100));
        return records.Select(kv =>
        {
            var bytes = Package(Rdt(kv.Value.ToArray()), kv.Key.Item1 << 8 | kv.Key.Item2);
            return kv.Key.Item1 >= 0xA ? RoomFile.ReadOpaque(kv.Key.Item1, kv.Key.Item2, bytes)
                                       : RoomFile.Read(kv.Key.Item1, kv.Key.Item2, bytes);
        }).ToList();
    }

    /// <summary>Run the pass, then parse every room as read and as the runner would write it.</summary>
    private static List<(RoomFile Vanilla, RoomFile Written)> Run(List<RoomFile> rooms, int seed, PlugCostMode costs, BoxIdMode ids)
    {
        var config = new RandomizerConfig { PlugCostMode = costs, BoxIdMode = ids };
        var context = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(seed), config, _ => { });
        new EmergencyBoxRandomizer().Apply(context);
        return rooms.Select(r => (RoomFile.Read(r.Stage, r.Room, r.OriginalBytes),
            RoomFile.Read(r.Stage, r.Room, context.TryGetRoomOutput(r, out var o) ? o : r.Write()))).ToList();
    }

    /// <summary>The written (id, cost) of each vanilla location, asserting that all of its records agree.</summary>
    private static Dictionary<int, (int Id, int Cost)> ByLocation(List<(RoomFile Vanilla, RoomFile Written)> result)
    {
        var records = result.SelectMany(x => x.Vanilla.Script!.EmergencyBoxes.Zip(x.Written.Script!.EmergencyBoxes));
        var byLocation = new Dictionary<int, (int, int)>();
        foreach (var g in records.GroupBy(p => (int)p.First.BoxId))
        {
            Assert.Single(g.Select(p => (p.Second.BoxId, p.Second.PlugCost)).Distinct());
            byLocation[g.Key] = (g.First().Second.BoxId, g.First().Second.PlugCost);
        }
        return byLocation;
    }

    [Fact]
    public void Reader_FindsBoxRecords_AndWriterChangesOnlyIdAndCost()
    {
        var rdt = Rdt(BoxRec(3, 2), PlugRec(7), BoxRec(10, 1));
        var script = RoomScript.Parse(rdt);
        Assert.True(script.ParsedCleanly);
        Assert.Equal(new[] { (3, 2, EmergencyBoxColour.Red), (10, 1, EmergencyBoxColour.Green) },
            script.EmergencyBoxes.Select(b => ((int)b.BoxId, (int)b.PlugCost, b.Colour)).ToArray());

        var edited = (byte[])rdt.Clone();
        script.EmergencyBoxes[0].BoxId = 14;
        script.EmergencyBoxes[1].PlugCost = 3;
        script.ApplyEmergencyBoxEdits(edited, script.EmergencyBoxes);

        int a = script.EmergencyBoxes[0].FileOffset, b = script.EmergencyBoxes[1].FileOffset;
        var changed = Enumerable.Range(0, rdt.Length).Where(i => rdt[i] != edited[i]).ToArray();
        Assert.Equal(new[] { a + EmergencyBoxRecord.BoxIdOffset, b + EmergencyBoxRecord.PlugCostOffset }, changed);
        Assert.Equal(new[] { (14, 2), (10, 3) },
            RoomScript.Parse(edited).EmergencyBoxes.Select(x => ((int)x.BoxId, (int)x.PlugCost)).ToArray());
    }

    [Fact]
    public void Writer_RejectsAnIdOutsideTheTable()
    {
        var rdt = Rdt(BoxRec(3, 2));
        var script = RoomScript.Parse(rdt);
        script.EmergencyBoxes[0].BoxId = 17;
        Assert.Throws<InvalidDataException>(() => script.ApplyEmergencyBoxEdits(rdt, script.EmergencyBoxes));
    }

    [Fact]
    public void RoomFile_RoundTripsUnedited_AndWritesEditedBoxes()
    {
        var bytes = Package(Rdt(BoxRec(5, 3), BoxRec(14, 1)));
        var room = RoomFile.Read(6, 2, bytes);
        Assert.Equal(bytes, room.Write());

        room.Script!.EmergencyBoxes[0].PlugCost = 1;
        var reread = RoomFile.Read(6, 2, room.Write());
        Assert.Equal(new[] { (5, 1), (14, 1) },
            reread.Script!.EmergencyBoxes.Select(x => ((int)x.BoxId, (int)x.PlugCost)).ToArray());
    }

    [Fact]
    public void PlugSupply_CountsOneCopyPerTakeFlag()
    {
        var rooms = World(plugs: 26);
        Assert.Equal(27, rooms.Sum(r => r.Items.Count));
        Assert.Equal(26, PlugEconomy.PlugSupply(rooms.SelectMany(r => r.Items), Plug));
    }

    [Fact]
    public void BoxCosts_GiveOneCostPerLocation_FromTheRecords()
    {
        var rooms = World();
        rooms.Where(r => r.Script is not null).SelectMany(r => r.Script!.EmergencyBoxes)
             .Where(b => b.OriginalBoxId == 0).ToList().ForEach(b => b.PlugCost = 3);
        var costs = PlugEconomy.BoxCosts(rooms);
        Assert.Equal(17, costs.Count);
        Assert.Equal(3, costs[0]);
        Assert.Equal(27, PlugEconomy.ReachableBoxDemand(Game, Game.EmergencyBoxes.Select(b => b.RoomCode).ToHashSet(), costs));
    }

    [Fact]
    public void ShuffleCosts_PermutesTheVanillaCosts_AndCopiesAgree()
    {
        var loc = ByLocation(Run(World(), seed: 11, PlugCostMode.Shuffle, BoxIdMode.Off));
        Assert.Equal(EmergencyBoxTests.VanillaBoxes.Select(b => b.Cost).Order(), loc.Values.Select(v => v.Cost).Order());
        Assert.NotEqual(EmergencyBoxTests.VanillaBoxes.Select(b => b.Cost), loc.OrderBy(kv => kv.Key).Select(kv => kv.Value.Cost));
        Assert.All(loc, kv => Assert.Equal(kv.Key, kv.Value.Id));
        Assert.Equal(1, loc[8].Cost);
    }

    [Theory]
    [InlineData(26)]
    [InlineData(20)]
    [InlineData(17)]
    public void RandomCosts_StayOneToThree_WithinTheSupply(int plugs)
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var costs = ByLocation(Run(World(plugs), seed, PlugCostMode.Random, BoxIdMode.Off)).Values.Select(v => v.Cost).ToArray();
            Assert.All(costs, c => Assert.InRange(c, EmergencyBoxRandomizer.MinCost, EmergencyBoxRandomizer.MaxCost));
            Assert.True(costs.Sum() <= plugs, $"seed {seed}: total {costs.Sum()} > supply {plugs}");
            Assert.Equal(1, ByLocation(Run(World(plugs), seed, PlugCostMode.Random, BoxIdMode.Off))[8].Cost);
        }
    }

    [Fact]
    public void RandomCosts_RefuseASupplyBelowOnePlugPerBox()
        => Assert.Throws<InvalidOperationException>(() => Run(World(plugs: 16), 1, PlugCostMode.Random, BoxIdMode.Off));

    [Fact]
    public void ShuffledCosts_RefuseASupplyBelowTheirTotal()
        => Assert.Throws<InvalidOperationException>(() => Run(World(plugs: 25), 1, PlugCostMode.Shuffle, BoxIdMode.Off));

    [Theory]
    [InlineData(BoxIdMode.WithinColour)]
    [InlineData(BoxIdMode.Full)]
    public void BoxIds_ArePermuted_WithPinsKept_AndCopiesAgree(BoxIdMode mode)
    {
        bool anyColourChange = false;
        for (int seed = 0; seed < 20; seed++)
        {
            var loc = ByLocation(Run(World(), seed, PlugCostMode.Vanilla, mode));
            Assert.Equal(Enumerable.Range(0, 17), loc.Values.Select(v => v.Id).Order());
            foreach (int pinned in EmergencyBoxRandomizer.PinnedIds(mode))
                Assert.Equal(pinned, loc[pinned].Id);
            Assert.All(loc, kv => Assert.Equal(EmergencyBoxTests.VanillaBoxes[kv.Key].Cost, kv.Value.Cost));
            bool colourChanged = loc.Any(kv => EmergencyBoxRecord.ColourOf(kv.Key) != EmergencyBoxRecord.ColourOf(kv.Value.Id));
            if (mode == BoxIdMode.WithinColour) Assert.False(colourChanged);
            anyColourChange |= colourChanged;
        }
        Assert.Equal(mode == BoxIdMode.Full, anyColourChange);
    }

    [Fact]
    public void ClutEntries_AreFoundInThePaletteEntry_AndWritten()
    {
        var room = RoomFile.Read(1, 0x13, Package(Rdt(BoxRec(0, 2)), 0x113));
        Assert.Equal(GianPackage.HeaderSize + ((509 - 505) * 256 + (816 - 768) + 3) * 2, room.ClutEntryOffset(816, 509, 3));
        Assert.Null(room.ClutEntryOffset(816, 510, 3));
        Assert.Equal(PaletteWord(0x113, (509 - 505) * 256 + 48 + 3), room.ReadClutEntry(816, 509, 3));

        Assert.True(room.SetClutEntry(816, 509, 3, 0x1234));
        var written = RoomFile.Read(1, 0x13, room.Write());
        Assert.Equal((ushort)0x1234, written.ReadClutEntry(816, 509, 3));
        Assert.Equal(PaletteWord(0x113, (509 - 505) * 256 + 48 + 4), written.ReadClutEntry(816, 509, 4));
    }

    [Fact]
    public void FullMode_RecoloursEachBoxWhoseColourChanged_FromTheNewIdsVanillaBox()
    {
        var result = Run(World(), 5, PlugCostMode.Vanilla, BoxIdMode.Full);
        var vanillaRoomOf = result.SelectMany(x => x.Vanilla.Script!.EmergencyBoxes.Select(b => (b.BoxId, x.Vanilla)))
            .GroupBy(x => x.BoxId).ToDictionary(g => (int)g.Key, g => g.First().Vanilla);

        var recolouredRooms = new HashSet<(int, int)>();
        foreach (var (vanilla, written) in result)
        {
            var touched = new HashSet<int>();
            foreach (var (was, now) in vanilla.Script!.EmergencyBoxes.Zip(written.Script!.EmergencyBoxes))
            {
                if (now.Colour == was.Colour) continue;
                recolouredRooms.Add((vanilla.Stage, vanilla.Room));
                var to = EmergencyBoxTexture.ByBoxId[was.BoxId];
                var from = EmergencyBoxTexture.ByBoxId[now.BoxId];
                foreach (var (t, f) in new[] { (to.Body, from.Body), (to.Fill, from.Fill), (to.Edge, from.Edge) })
                {
                    ushort colour = vanillaRoomOf[now.BoxId].ReadClutEntry(from.ClutX, from.ClutY, f)!.Value;
                    ushort old = vanilla.ReadClutEntry(to.ClutX, to.ClutY, t)!.Value;
                    Assert.Equal((ushort)((colour & 0x7FFF) | (old & 0x8000)), written.ReadClutEntry(to.ClutX, to.ClutY, t));
                    touched.Add((to.ClutY - 505) * 256 + to.ClutX - 768 + t);
                }
            }
            // Nothing else in the palette entry changed.
            for (int k = 0; k < 256 * 5; k++)
                if (!touched.Contains(k))
                    Assert.Equal(vanilla.OriginalBytes.AsSpan(GianPackage.HeaderSize + 2 * k, 2).ToArray(),
                                 written.OriginalBytes.AsSpan(GianPackage.HeaderSize + 2 * k, 2).ToArray());
        }
        Assert.NotEmpty(recolouredRooms);
        Assert.Contains(recolouredRooms, r => r.Item1 >= 0xA); // a copy, through the room-output override
    }

    [Fact]
    public void WithinColourMode_LeavesThePalettesAlone()
    {
        foreach (var (vanilla, written) in Run(World(), 9, PlugCostMode.Vanilla, BoxIdMode.WithinColour))
            Assert.Equal(vanilla.OriginalBytes.AsSpan(0, GianPackage.HeaderSize + 4096).ToArray(),
                         written.OriginalBytes.AsSpan(0, GianPackage.HeaderSize + 4096).ToArray());
    }

    [Fact]
    public void Pass_IsDeterministicPerSeed()
        => Assert.Equal(ByLocation(Run(World(), 42, PlugCostMode.Random, BoxIdMode.Full)).OrderBy(kv => kv.Key),
                        ByLocation(Run(World(), 42, PlugCostMode.Random, BoxIdMode.Full)).OrderBy(kv => kv.Key));

    [Fact]
    public void Pass_LeavesRoomsAlone_WhenABoxIsMissing()
    {
        var rooms = World().Where(r => !(r.Stage == 6 && r.Room == 0x12)).ToList(); // no box 16
        Assert.All(Run(rooms, 3, PlugCostMode.Shuffle, BoxIdMode.Full),
            x => Assert.Equal(x.Vanilla.OriginalBytes, x.Written.OriginalBytes));
    }

    /// <summary>Run the pass in Full mode against an install folder holding one room file, after
    /// <paramref name="setup"/> adds HD files to it; returns the loose files queued.</summary>
    private static IReadOnlyDictionary<string, byte[]> RunWithInstall(Action<string> setup)
    {
        var install = Directory.CreateTempSubdirectory("dinorand_boxhd_").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(install, "Data"));
            File.WriteAllBytes(Path.Combine(install, "Data", "st100.dat"), new byte[1]);
            setup(install);
            var rooms = World();
            var config = new RandomizerConfig { BoxIdMode = BoxIdMode.Full };
            var context = new RandomizationContext(Game, rooms, RoomGraph.Build(rooms), new Seed(5), config, _ => { }, install);
            new EmergencyBoxRandomizer().Apply(context);
            return context.LooseFiles;
        }
        finally { Directory.Delete(install, true); }
    }

    [Fact]
    public void FullMode_WithoutTheHdPack_WritesNoLooseFiles()
        => Assert.Empty(RunWithInstall(_ => { }));

    [Fact]
    public void FullMode_PutsBackAnHdPageAnEarlierInstallEdited()
    {
        // The backup is not a PNG, so the planner cannot use the page; it is still written back as it was.
        var files = RunWithInstall(install =>
        {
            Directory.CreateDirectory(Path.Combine(install, "HD", "ST602"));
            var backup = Path.Combine(install, "Data", GameInstaller.BackupDirName, GameInstaller.LooseBackupSubdir, "HD", "ST602");
            Directory.CreateDirectory(backup);
            File.WriteAllBytes(Path.Combine(backup, "p07.png"), new byte[] { 1, 2, 3 });
        });
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(files, f => f.Key == "HD/ST602/p07.png").Value);
    }
}
