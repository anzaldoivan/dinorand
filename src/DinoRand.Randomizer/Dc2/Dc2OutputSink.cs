using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2;

/// <summary>Where a randomized DC2 room blob goes. v1 = <see cref="Dc2BackupSwapSink"/>; a CR
/// <c>.d2p</c> override (docs/reference/dc2/loader/LOADER-DC2.md K52) is a future second implementation, not a
/// rewrite (docs/decisions/dc2/enemies/CROSS-SPECIES-RANDO-PLAN.md).</summary>
public interface IDc2OutputSink
{
    /// <summary>Emit the (already edited + repacked) bytes for <paramref name="room"/>.</summary>
    void Emit(Dc2RoomFile room, byte[] bytes);

    /// <summary>Emit a non-room Data file (e.g. a <c>WEP_P*.DAT</c> player model from
    /// <see cref="Passes.Dc2PlayerModelSwap"/>) under <paramref name="fileName"/>. Install-time
    /// handling is identical to rooms: <see cref="Install.GameInstaller.Install"/> overlays any
    /// name-matched <c>*.dat</c> with a manifested pristine backup.</summary>
    void EmitFile(string fileName, byte[] bytes);
}

/// <summary>
/// Backs up the original <c>ST*.DAT</c> to <c>ST*.DAT.bak</c> (once — never overwriting an existing
/// backup, so re-runs keep the *vanilla* original) and writes the randomized bytes in place. Reversible
/// by restoring the <c>.bak</c>. The chosen v1 sink: proven this session, no Classic REbirth dependency.
/// </summary>
public sealed class Dc2BackupSwapSink : IDc2OutputSink
{
    public const string BackupSuffix = ".bak";

    private readonly string? _dataDir;

    /// <param name="dataDir">The game <c>Data</c> dir non-room files are swapped in. Optional —
    /// room emits resolve their own path; <see cref="EmitFile"/> requires it.</param>
    public Dc2BackupSwapSink(string? dataDir = null) => _dataDir = dataDir;

    public void EmitFile(string fileName, byte[] bytes)
    {
        if (_dataDir is null)
            throw new InvalidOperationException(
                "Dc2BackupSwapSink needs a dataDir to emit non-room files; construct it with one");
        EmitTo(Path.Combine(_dataDir, fileName), bytes);
    }

    public void Emit(Dc2RoomFile room, byte[] bytes)
    {
        var path = room.SourcePath
                   ?? throw new InvalidOperationException(
                       $"room {room} has no SourcePath; backup-swap needs the on-disk ST*.DAT path");
        Dc2RoomEmitGuard.EnsureContainerFormatPreserved(Path.GetFileName(path), room.OriginalBytes, bytes);
        EmitTo(path, bytes);
    }

    /// <summary>Backup-once + overwrite at an explicit path (the testable core).</summary>
    public static void EmitTo(string path, byte[] bytes)
    {
        var backup = path + BackupSuffix;
        if (!File.Exists(backup))
            File.Copy(path, backup);     // idempotent: the first run captures the vanilla file
        File.WriteAllBytes(path, bytes);
    }
}

/// <summary>
/// Non-destructive sink: writes each randomized room to <c>outputDir\ST*.DAT</c> (the room's own
/// filename), leaving the game install untouched — the DC2 analogue of DC1's "generate to a mod dir,
/// then overlay" flow. The App's <b>Generate</b> uses this (a preview to the working mod dir); <b>Install</b>
/// then overlays that dir onto <c>rebirth\Data</c> via <see cref="Install.GameInstaller.Install"/> (the same
/// <c>.dinorand_backup</c> contract as DC1, reversed by Restore). Counts the rooms it wrote.
/// (The in-place <see cref="Dc2BackupSwapSink"/> is retained for the single-room <c>--dc2-swap-enemies</c>
/// CLI op, which edits one <c>ST*.DAT</c> directly with an <c>ST*.DAT.bak</c>.)
/// </summary>
public sealed class Dc2OutputDirSink : IDc2OutputSink
{
    private readonly string _outputDir;

    /// <summary>How many rooms have been written so far (the changed rooms the passes emitted).</summary>
    public int RoomsWritten { get; private set; }

    public Dc2OutputDirSink(string outputDir) => _outputDir = outputDir;

    /// <summary>How many non-room Data files have been written (e.g. swapped player models).</summary>
    public int FilesWritten { get; private set; }

    private readonly List<string> _writtenFiles = new();

    /// <summary>The file names written this run (rooms + non-room files, in emit order) — the
    /// spoiler debug block's output-file list (docs/decisions/cross/SPOILER-LOG-PLAN.md §4).</summary>
    public IReadOnlyList<string> WrittenFiles => _writtenFiles;

    public void EmitFile(string fileName, byte[] bytes)
    {
        EmitTo(_outputDir, fileName, bytes);
        FilesWritten++;
        _writtenFiles.Add(fileName);
    }

    public void Emit(Dc2RoomFile room, byte[] bytes)
    {
        var name = Path.GetFileName(room.SourcePath)
                   ?? throw new InvalidOperationException(
                       $"room {room} has no SourcePath; cannot name its output file");
        Dc2RoomEmitGuard.EnsureContainerFormatPreserved(name, room.OriginalBytes, bytes);
        EmitTo(_outputDir, name, bytes);
        RoomsWritten++;
        _writtenFiles.Add(name);
    }

    /// <summary>Write <paramref name="bytes"/> to <c>outputDir\fileName</c>, creating the dir — including
    /// a relative subdir in <paramref name="fileName"/> (e.g. the voice pass's <c>Speech/0001.dat</c>).
    /// Never touches the source install.</summary>
    public static void EmitTo(string outputDir, string fileName, byte[] bytes)
    {
        var path = Path.Combine(outputDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}
