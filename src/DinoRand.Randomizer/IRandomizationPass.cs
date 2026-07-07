using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Graph;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer;

/// <summary>
/// One unit of randomization in the pipeline (items, enemies, doors, …). Passes run
/// in a fixed order and mutate the loaded stages in place. Keeping each concern in its
/// own pass is what makes the engine extensible by adding an entry, not a code path.
/// </summary>
public interface IRandomizationPass
{
    string Name { get; }

    bool IsEnabled(RandomizerConfig config);

    void Apply(RandomizationContext context);
}

/// <summary>Everything a pass needs: the game data, the loaded rooms, RNG, and logging.</summary>
public sealed class RandomizationContext
{
    public RandomizationContext(GameDefinition game, IReadOnlyList<RoomFile> rooms, RoomGraph graph,
                                Seed seed, RandomizerConfig config, Action<string> log,
                                string? installDir = null)
    {
        Game = game;
        Rooms = rooms;
        Graph = graph;
        Seed = seed;
        Config = config;
        Log = log;
        InstallDir = installDir;
    }

    public GameDefinition Game { get; }
    public IReadOnlyList<RoomFile> Rooms { get; }

    /// <summary>The source install root this run reads from (the runner's <c>installDir</c>), or
    /// <c>null</c> in unit tests that construct the context directly. Passes that need to read the
    /// original on-disk files (e.g. the voice pass reading each slot's native sample rate) use it;
    /// nothing that mutates rooms depends on it.</summary>
    public string? InstallDir { get; }

    /// <summary>The room graph for progression / door passes. Built once by the runner and rebuilt
    /// by <see cref="RebuildGraph"/> after the door pass rewires destinations, so downstream passes
    /// (progression / key logic) see the new world. The setter is private so only
    /// <see cref="RebuildGraph"/> can replace it.</summary>
    public RoomGraph Graph { get; private set; }

    public Seed Seed { get; }
    public RandomizerConfig Config { get; }
    public Action<string> Log { get; }

    /// <summary>The typed per-run diff ledger the spoiler log is projected from
    /// (docs/decisions/cross/SPOILER-LOG-PLAN.md). Always present; recording is pure list-appends (no RNG, no
    /// I/O), so it can never change what the passes emit.</summary>
    public Spoiler.SpoilerCollector Spoiler { get; } = new();

    /// <summary>
    /// EXE patches a pass needs the installer to apply (passes never touch <c>DINO.exe</c> directly — they
    /// declare intent). The runner serializes these to <c>exe-patch-plan.json</c> beside the room <c>.dat</c>s;
    /// <see cref="GameInstaller.Install"/> applies them additively and <see cref="GameInstaller.Restore"/>
    /// reverses them. See docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md.
    /// </summary>
    public List<ExePatchRequest> ExePatchRequests { get; } = new();

    // Some imports (e.g. the Theri's fixed-column texture) are a byte-level transform of the serialized room,
    // not a mutation of the RoomFile object. A pass stores the final bytes here; the runner writes them
    // verbatim instead of RoomFile.Write(). Keyed by the RoomFile instance.
    private readonly Dictionary<RoomFile, byte[]> _roomOutputOverride = new(ReferenceEqualityComparer.Instance);

    /// <summary>Override the bytes the runner writes for <paramref name="room"/> (for a post-serialization
    /// byte transform such as a texture import). Replaces any prior override for the same room.</summary>
    public void SetRoomOutput(RoomFile room, byte[] bytes) => _roomOutputOverride[room] = bytes;

    /// <summary>Get a pass-supplied output override for <paramref name="room"/>, if any.</summary>
    public bool TryGetRoomOutput(RoomFile room, out byte[] bytes) => _roomOutputOverride.TryGetValue(room, out bytes!);

    // Loose (non-room) install files a pass produces — e.g. voice banks under Sound\VOICE\ (the voice
    // pass, VOICE-RANDO-PLAN.md §12.3). Keyed by INSTALL-RELATIVE path with '/' separators; the runner
    // writes them into the mod dir under that subpath and GameInstaller overlays them via the backup
    // contract. A loose-file seam, parallel to the room-output override above.
    private readonly Dictionary<string, byte[]> _looseFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Queue a loose install file at <paramref name="installRelativePath"/> (forward slashes),
    /// replacing any prior bytes for the same path.</summary>
    public void AddLooseFile(string installRelativePath, byte[] bytes)
        => _looseFiles[installRelativePath.Replace('\\', '/')] = bytes;

    /// <summary>The loose install files passes have produced (install-relative path → bytes).</summary>
    public IReadOnlyDictionary<string, byte[]> LooseFiles => _looseFiles;

    public IEnumerable<RoomFile> AllRooms() => Rooms;

    /// <summary>
    /// Rebuild <see cref="Graph"/> from the current door records of <see cref="Rooms"/>. Call after a
    /// pass edits door destinations (the door pass) so later passes run on the rewired graph — the
    /// edges captured in the previous graph hold stale <see cref="RoomNode"/> refs (plan §5.2).
    /// </summary>
    public void RebuildGraph() => Graph = RoomGraph.Build(Rooms, Game.Requirements);
}
