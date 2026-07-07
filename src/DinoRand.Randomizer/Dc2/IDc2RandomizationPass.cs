using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// One unit of DC2 randomization. Mirrors DC1's <see cref="IRandomizationPass"/> but is a
/// <b>separate, DC2-typed</b> pipeline (operates on <see cref="Dc2RoomFile"/>, not the DC1
/// <c>RoomFile</c>) — the parallel-stack isolation from docs/parity/BIORAND-REUSE-VALIDATION.md Q3, so
/// DC1's context/passes are never edited. <see cref="Seed"/> and <see cref="RandomizerConfig"/> are
/// shared (game-agnostic).
/// </summary>
public interface IDc2RandomizationPass
{
    string Name { get; }

    bool IsEnabled(RandomizerConfig config);

    void Apply(Dc2RandomizationContext context);
}

/// <summary>Everything a DC2 pass needs: the game (+ its enemy helper), the loaded rooms, RNG, logging.
/// The DC2 analogue of <see cref="RandomizationContext"/>; no <c>RoomGraph</c> yet (door routing is
/// OPEN&#160;#2).</summary>
public sealed class Dc2RandomizationContext
{
    public Dc2RandomizationContext(DinoCrisis2 game, IReadOnlyList<Dc2RoomFile> rooms,
                                   Seed seed, RandomizerConfig config, Action<string> log,
                                   IDc2OutputSink sink, string? dataDir = null)
    {
        Game = game;
        Rooms = rooms;
        Seed = seed;
        Config = config;
        Log = log;
        Sink = sink;
        DataDir = dataDir;
    }

    public DinoCrisis2 Game { get; }
    public IReadOnlyList<Dc2RoomFile> Rooms { get; }

    /// <summary>The game's <c>Data</c> dir (source of non-room files a pass reads, e.g. the
    /// <c>WEP_P*.DAT</c> player models). Null when a caller runs room-only passes without an install.</summary>
    public string? DataDir { get; }

    /// <summary>Where edited room blobs are written (v1 = backup-and-swap <c>ST*.DAT</c>).</summary>
    public IDc2OutputSink Sink { get; }

    /// <summary>The DC2 enemy abstraction (BioRand <c>IEnemyHelper</c> port), registered by the game.</summary>
    public IDc2EnemyHelper EnemyHelper => Game.EnemyHelper;

    public Seed Seed { get; }
    public RandomizerConfig Config { get; }
    public Action<string> Log { get; }

    /// <summary>The typed per-run diff ledger the spoiler log is projected from
    /// (docs/decisions/cross/SPOILER-LOG-PLAN.md). Always present; recording is pure list-appends (no RNG, no
    /// I/O), so it can never change what the passes emit.</summary>
    public Spoiler.SpoilerCollector Spoiler { get; } = new();

    // Room-bytes pipeline: when two passes edit the SAME room file (enemy swap + raptor tiers),
    // the later pass must build on the earlier one's output, not OriginalBytes — otherwise its
    // Emit silently reverts the earlier edits.
    private readonly Dictionary<Dc2RoomFile, byte[]> _workingBytes = new();

    /// <summary>The room's current package bytes: the last <see cref="EmitRoom"/> output this run,
    /// or <c>OriginalBytes</c> if no pass has touched it yet.</summary>
    public byte[] CurrentBytes(Dc2RoomFile room) => _workingBytes.GetValueOrDefault(room, room.OriginalBytes);

    /// <summary>Emit an edited room through the sink AND record it as the room's working bytes for
    /// any later pass. Passes that edit room files should use this instead of <c>Sink.Emit</c>.</summary>
    public void EmitRoom(Dc2RoomFile room, byte[] bytes)
    {
        _workingBytes[room] = bytes;
        Sink.Emit(room, bytes);
    }
}
