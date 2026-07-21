using DinoRand.ApClient;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Ap;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Dc2.Passes;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Voice;

internal sealed partial class CliApplication
{
    private readonly string[] argv;

    private CliApplication(string[] argv) => this.argv = argv;

    public static int Run(string[] argv) => new CliApplication(argv).Execute();

    private int Execute()
    {
        if (argv.Length == 0 || argv.Contains("--help") || argv.Contains("-h"))
        {
            CliOutput.WriteHelp();
            return 0;
        }

        if (TryRunDc2Command() is { } dc2CommandExit)
            return dc2CommandExit;

        if (TryRunArchipelagoCommand() is { } archipelagoExit)
            return archipelagoExit;

        string? install = GetOpt("--install");
        if (install is null)
        {
            Console.Error.WriteLine("error: --install <gameDir> is required (see --help)");
            return 1;
        }

        // TOS / DRM guard: refuse to operate on a game whose DINO.exe is wrapped by a protector (e.g. The Enigma
        // Protector on the Steam release). This covers every subcommand at once — none runs past this point on a
        // protected install. See ExeProtection / docs/reference/dc1/drm/STEAM-ENIGMA-DRM.md.
        if (GuardNotDrmProtected(install) is { } drmExit)
            return drmExit;

        // Game selector (default dc1). DC2 is wired as a parallel stack (Dc2RandomizerRunner / DinoCrisis2)
        // so DC1 stays byte-for-byte unchanged — see docs/parity/BIORAND-REUSE-VALIDATION.md Q3.
        // Read-only poisoned-backup audit (K82): compare every .dinorand_backup capture against the
        // manifest hash / .dinorand-bak sibling / live file. Game-agnostic (runs before the --game split);
        // exit 1 when anything is Poisoned/Suspect.
        if (argv.Contains("--verify-backup"))
        {
            // Both game defs may claim a Data dir under the same root (their room patterns overlap);
            // audit the one that actually holds a backup, falling back to the first hit.
            var candidates = new[] { new DinoCrisis1().GetDataDir(install), new DinoCrisis2().GetDataDir(install) }
                .Where(d => d is not null).Distinct().ToList();
            var dataDir = candidates.FirstOrDefault(d => GameInstaller.HasBackup(d!)) ?? candidates.FirstOrDefault();
            if (dataDir is null)
            {
                Console.Error.WriteLine($"error: could not locate a Data folder under {install}");
                return 1;
            }
            var findings = GameInstaller.VerifyBackups(dataDir);
            if (findings.Count == 0)
            {
                Console.WriteLine($"no DinoRand backup found in {dataDir} — nothing to verify");
                return 0;
            }
            foreach (var f in findings.OrderByDescending(f => f.Status))
                Console.WriteLine($"{f.Status,-11} {f.Name}  ({f.Detail})");
            int bad = findings.Count(f => f.Status is BackupVerifyStatus.Poisoned or BackupVerifyStatus.Suspect);
            Console.WriteLine(bad == 0
                ? $"OK: {findings.Count} backup(s) verified, none poisoned"
                : $"FAIL: {bad} of {findings.Count} backup(s) poisoned or suspect");
            return bad == 0 ? 0 : 1;
        }

        string gameId = (GetOpt("--game") ?? "dc1").ToLowerInvariant();
        if (gameId is not ("dc1" or "dc2"))
        {
            Console.Error.WriteLine($"error: --game must be 'dc1' or 'dc2' (got '{gameId}')");
            return 1;
        }

        if (gameId == "dc2")
            return RunDc2(install);


        if (TryRunDc1Command(install) is { } dc1CommandExit)
            return dc1CommandExit;

        return RunRandomizeCommand(install);
    }

    string? GetOpt(string name)
    {
        int i = Array.IndexOf(argv, name);
        return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
    }

    // Parse a hex (0x..) or decimal integer token; null if malformed.
    // cont.57 false-handoff RCA: every single-file edit rebuilds the file FROM the pristine backup, so
    // edits do NOT stack — a second edit silently replaced the first. Make the replacement explicit.
    static string BackupOnceWarned(string dataDir, string originalPath)
    {
        var backupPath = GameInstaller.BackupOnce(dataDir, originalPath);
        if (GameInstaller.HasPriorEdit(backupPath, originalPath))
            Console.WriteLine($"warning : {Path.GetFileName(originalPath)} already differs from its pristine "
                + "backup — single-file edits rebuild FROM the backup and do NOT stack, so this edit "
                + "REPLACES the previous edit(s) on this file (one witness = one deploy).");
        return backupPath;
    }

    static int? ParseItemInt(string t) =>
        t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? (int.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : null)
            : (int.TryParse(t, out var d) ? d : (int?)null);

    // Parse a "id:count,id:count,…" starting-inventory spec into (id,count) pairs; throws on a bad token.
    static List<(int Id, int Count)> ParseStartingItems(string spec)
    {
        var items = new List<(int, int)>();
        foreach (var tok in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = tok.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || ParseItemInt(parts[0]) is not { } id || ParseItemInt(parts[1]) is not { } count)
                throw new FormatException($"bad --starting-items token '{tok}' (expected id:count, e.g. 0x16:30)");
            items.Add((id, count));
        }
        return items;
    }

    // Locate the install's DINO.exe and refuse to proceed if it is DRM-protected (Steam/Enigma build).
    // Returns null when the game is clean (or no exe is present — handled by each op's own not-found path),
    // or a non-zero exit code after printing the reason. Kept here so every subcommand is gated in one place.
    int? GuardNotDrmProtected(string gameDir)
    {
        if (!Directory.Exists(gameDir))
            return null; // a bad path is reported by the operation itself
        string? exePath;
        try
        {
            exePath = Directory.EnumerateFiles(gameDir, GameInstaller.ExeName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch { return null; }
        if (exePath is null)
            return null; // no DINO.exe under the folder — let the op surface its own error
        var detection = ExeProtection.Inspect(exePath);
        if (!detection.IsProtected)
            return null;
        Console.Error.WriteLine($"error: refusing to modify a DRM-protected game.");
        Console.Error.WriteLine($"       {detection.Detail}");
        Console.Error.WriteLine($"       exe: {exePath}");
        return 3;
    }
}
