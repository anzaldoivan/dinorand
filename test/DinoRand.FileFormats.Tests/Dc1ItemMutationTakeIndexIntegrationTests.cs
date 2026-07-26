using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Passes;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public sealed class Dc1ItemMutationTakeIndexIntegrationTests
{
    private const int ChiefRoomCode = 0x0202;
    private const int ChiefRecordOffset = 0x4ff0c;
    private const int MaxSeedScan = 64;
    private static readonly GameDefinition Game = new DinoCrisis1();

    private sealed class Run
    {
        public Run(List<RoomFile> rooms, RoomGraph graph, RandomizationContext context, RoomFile chief,
                   ItemRecord source)
        {
            Rooms = rooms;
            Graph = graph;
            Context = context;
            Chief = chief;
            Source = source;
        }

        public List<RoomFile> Rooms { get; }
        public RoomGraph Graph { get; }
        public RandomizationContext Context { get; }
        public RoomFile Chief { get; }
        public ItemRecord Source { get; }
        public NodeItem? ScatterLanding { get; set; }
        public int ItemIdBeforeReplacement { get; set; }
    }

    [Fact]
    public void RealInstall_KeyShuffle_PreservesChiefsRoomTakeIndex85()
    {
        var root = InstallRoot();
        if (root is null) return;

        var run = Find(root, new RandomizerConfig
        {
            RandomizeItems = false,
            ShuffleKeyItems = true,
            RelocateDdkDiscs = true,
        }, candidate =>
        {
            new ProgressionPass().Apply(candidate.Context);
            return candidate.Source.ItemId != candidate.Source.OriginalItemId;
        });

        Assert.NotNull(run);
        Assert.NotEqual(0x63, run!.Source.ItemId);
        Assert.NotEqual(ItemRecord.EmptySlotId, run.Source.ItemId);

        var reread = Reparse(run.Chief);
        Assert.Equal(run.Source.ItemId, reread.ItemId);
        Assert.Equal((ushort)85, reread.TakeIndex);
    }

    [Fact]
    public void RealInstall_ScatterPlacesDdkInputDiscN_AndPreservesChiefsRoomTakeIndex85()
    {
        var root = InstallRoot();
        if (root is null) return;

        var run = Find(root, new RandomizerConfig
        {
            RandomizeItems = false,
            ShuffleKeyItems = true,
            ShuffleKeyItemsIntoPickups = true,
            RelocateDdkDiscs = true,
        }, candidate =>
        {
            new ProgressionPass().Apply(candidate.Context);
            candidate.ScatterLanding = ScatterLanding(candidate);
            return candidate.Source.ItemId != candidate.Source.OriginalItemId
                && candidate.ScatterLanding is not null;
        });

        Assert.NotNull(run);
        Assert.NotNull(run!.ScatterLanding);
        Assert.NotSame(run.Source, run.ScatterLanding!.Record);
        Assert.True(run.ScatterLanding.IsScatterTarget);
        Assert.Equal(0x63, run.ScatterLanding.Record.ItemId);
        Assert.NotEqual(0x63, run.Source.ItemId);

        var rereadChief = Reparse(run.Chief);
        Assert.Equal((ushort)85, rereadChief.TakeIndex);

        var landingRoom = run.Rooms.Single(room => RoomCode(room) == run.ScatterLanding!.RoomCode);
        var rereadLanding = Reparse(landingRoom, run.ScatterLanding.Record.FileOffset);
        Assert.Equal(0x63, rereadLanding.ItemId);
    }

    [Fact]
    public void RealInstall_ScatterThenGenericReplacement_PreservesChiefsRoomTakeIndex85()
    {
        var root = InstallRoot();
        if (root is null) return;

        var run = Find(root, new RandomizerConfig
        {
            ShuffleKeyItems = true,
            ShuffleKeyItemsIntoPickups = true,
            RelocateDdkDiscs = true,
            RandomizeItems = true,
            ReplaceItemPool = true,
            RandomizeWeapons = false,
        }, candidate =>
        {
            new ProgressionPass().Apply(candidate.Context);
            candidate.ScatterLanding = ScatterLanding(candidate);
            if (candidate.ScatterLanding is null) return false;

            candidate.ItemIdBeforeReplacement = candidate.Source.ItemId;
            new ItemRandomizer().Apply(candidate.Context);
            return candidate.Source.ItemId != candidate.ItemIdBeforeReplacement;
        });

        Assert.NotNull(run);
        Assert.NotEqual(run!.ItemIdBeforeReplacement, run.Source.ItemId);
        Assert.Contains(run.Source.ItemId, Game.ItemPool.Select(item => item.ItemId));
        Assert.NotEqual(ItemRecord.EmptySlotId, run.Source.ItemId);

        var reread = Reparse(run.Chief);
        Assert.Equal(run.Source.ItemId, reread.ItemId);
        Assert.Equal((ushort)85, reread.TakeIndex);
    }

    [Fact]
    public void RealInstall_KeyShuffleThenVisualNormalization_PreservesChiefsRoomTakeIndex85()
    {
        var root = InstallRoot();
        if (root is null) return;

        var run = Find(root, new RandomizerConfig
        {
            RandomizeItems = false,
            ShuffleKeyItems = true,
            RelocateDdkDiscs = true,
            NormalizePickupVisuals = true,
        }, candidate =>
        {
            new ProgressionPass().Apply(candidate.Context);
            if (candidate.Source.ItemId == candidate.Source.OriginalItemId) return false;

            new NormalizePickupVisualsPass().Apply(candidate.Context);
            return candidate.Source.NormalizeVisual;
        });

        Assert.NotNull(run);
        Assert.True(run!.Source.NormalizeVisual);
        Assert.NotEqual(ItemRecord.NoDisplaySlot, run.Source.NormalizeDisplaySlot);

        var reread = Reparse(run.Chief);
        Assert.Equal(run.Source.NormalizeDisplaySlot, reread.DisplaySlot);
        Assert.Equal(ItemRecord.GenericPanelModelPtr, ModelPointer(reread));
        Assert.Equal((ushort)85, reread.TakeIndex);
    }

    private static string? InstallRoot()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        return string.IsNullOrEmpty(root) || Game.EnumerateRooms(root).Count == 0 ? null : root;
    }

    private static Run? Find(string root, RandomizerConfig config, Func<Run, bool> attempt)
    {
        for (int seed = 0; seed < MaxSeedScan; seed++)
        {
            var candidate = Load(root, config, seed);
            if (attempt(candidate)) return candidate;
        }

        return null;
    }

    private static Run Load(string root, RandomizerConfig config, int seed)
    {
        var refs = Game.EnumerateRooms(root);
        var rooms = refs.Select(reference =>
            RoomFile.ReadFromFile(reference.Stage, reference.Room, reference.Path)).ToList();
        var graph = RoomGraph.Build(rooms, Game.Requirements);
        var context = new RandomizationContext(Game, rooms, graph, new Seed(seed), config, _ => { }, root);
        var chief = rooms.Single(room => RoomCode(room) == ChiefRoomCode);
        var source = chief.Items.Single(item => item.FileOffset == ChiefRecordOffset);

        Assert.Equal(0x63, source.OriginalItemId);
        Assert.Equal((ushort)85, source.OriginalTakeIndex);
        return new Run(rooms, graph, context, chief, source);
    }

    private static NodeItem? ScatterLanding(Run run) =>
        run.Graph.Nodes.SelectMany(node => node.Items)
            .SingleOrDefault(item => item.Record.ItemId == 0x63 && !ReferenceEquals(item.Record, run.Source)
                                     && item.IsScatterTarget);

    private static ItemRecord Reparse(RoomFile room, int fileOffset = ChiefRecordOffset) =>
        RoomFile.Read(room.Stage, room.Room, room.Write()).Items
            .Single(item => item.FileOffset == fileOffset);

    private static int RoomCode(RoomFile room) => (room.Stage << 8) | room.Room;

    private static uint ModelPointer(ItemRecord item) =>
        (uint)(item.Raw[0x24] | (item.Raw[0x25] << 8) | (item.Raw[0x26] << 16) | (item.Raw[0x27] << 24));
}
