using System.Runtime.Versioning;
using DinoRand.ApClient;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Ap;

/// <summary>
/// The Archipelago runtime session for DC1, front-end agnostic (AP-CLIENT-PLAN.md D3): connect/login
/// → <c>logic_version</c> gate → loop-closing placement install → 4 Hz poll loop until the token is
/// cancelled. Lifted verbatim out of the CLI's <c>ApConnectDc1</c>/<c>ApPollLoopDc1</c> so the CLI and
/// the Avalonia connect tab run ONE implementation — the CLI's console lines are the live-verified
/// contract (matrix 2026-07-19), so the strings and exit codes here are emitted byte-for-byte as they
/// were and must stay that way.
/// </summary>
public static class Dc1ApRunner
{
    public const int DefaultPort = 38281;
    public const string DefaultOutDir = "mod_dinorand_ap";
    internal const int LogicVersion = 3;

    internal static void ValidateLogicVersion(int actual)
    {
        if (actual != LogicVersion)
            throw new InvalidOperationException(
                $"server world logic_version {actual} is obsolete; this client requires DC1 AP v3 — "
                + "regenerate the multiworld with the matching current dino_crisis_1.apworld");
    }

    /// <summary>Split "host:port" into its parts, defaulting to the AP port.</summary>
    public static (string Host, int Port) ParseHostPort(string hostPort)
    {
        var hp = hostPort.Split(':', 2);
        return (hp[0], hp.Length == 2 && int.TryParse(hp[1], out int p) ? p : DefaultPort);
    }

    /// <summary>
    /// Run a whole AP session. Blocks until <paramref name="ct"/> is cancelled or a step fails.
    /// Returns the process exit code the CLI used (0 = clean disconnect).
    /// </summary>
    /// <param name="log">Progress sink — the CLI passes <c>Console.WriteLine</c>.</param>
    /// <param name="error">Failure sink — the CLI passes <c>Console.Error.WriteLine</c>.</param>
    public static int Run(string hostPort, string? slot, string? password, string? install,
        string? outDirArg, Action<string> log, Action<string> error, CancellationToken ct)
    {
        if (slot is null || install is null)
        {
            error("error: --ap-connect needs --ap-slot <name> and --install <dc1GameDir>");
            return 1;
        }
        var (host, port) = ParseHostPort(hostPort);

        var dataDir = new DinoCrisis1().GetDataDir(install);
        if (dataDir is null)
        {
            error($"error: could not locate a DC1 Data folder under {install}");
            return 1;
        }
        GameInstaller.EnsureNotDrmProtected(dataDir);

        var checks = Dc1ClientChecks.LoadEmbedded();
        using var session = new ApSession();
        ApConnection conn;
        try
        {
            conn = session.Connect(host, port, slot, password);
        }
        catch (Exception ex)
        {
            error($"error: {ex.Message}");
            return 1;
        }
        log($"connected: seed {conn.SeedName}, slot #{conn.Slot} '{slot}', goal room {conn.GoalRoom}");
        try
        {
            ValidateLogicVersion(conn.LogicVersion);
        }
        catch (InvalidOperationException ex)
        {
            error($"error: {ex.Message}");
            return 1;
        }
        // --- loop-closing install (D5): AP's fill replaces KeyItemPlacer ---
        string outDir = outDirArg ?? DefaultOutDir;
        var patches = new List<ApPlacementInstaller.RecordPatch>();
        foreach (var entry in checks.Locations)
        {
            if (!conn.Placements.TryGetValue(entry.ApId, out int placed))
            {
                error($"error: slot_data has no placement for '{entry.Name}' — apworld/client contract drift");
                return 1;
            }
            byte itemId = placed == ApSession.OtherWorldMarker
                ? Dc1Symbols.OtherWorldMarkerItemId
                : checked((byte)placed);
            foreach (var rec in entry.Records)
                patches.Add(new ApPlacementInstaller.RecordPatch(
                    rec.Room,
                    rec.RecOffset,
                    new Dc1ItemRecordClass(
                        checked((byte)rec.ExpectedOpcode),
                        checked((byte)rec.ExpectedSubtype),
                        rec.ExpectedLength),
                    rec.VanillaItemId,
                    rec.VanillaAmount,
                    checked((ushort)rec.VanillaTake),
                    itemId,
                    1,
                    checked((ushort)rec.Take),
                    Visual: null));
        }
        var written = ApPlacementInstaller.WriteRooms(dataDir, outDir, patches, log);
        var installed = GameInstaller.Install(
            dataDir, outDir, $"AP {conn.SeedName}", written.WrittenFiles);
        log($"installed AP placement: {written.RecordsPatched} records in {written.RoomsWritten} rooms "
            + $"(backed up {installed.BackedUp}, overlaid {installed.Overlaid})");

        if (!OperatingSystem.IsWindows())
        {
            error("error: the poll loop must run on the WINDOWS host (WSL cannot attach to DINO.exe). "
                + "The install above is done — rerun this command from Windows to play.");
            return 1;
        }
        return PollLoop(session, conn, checks, outDir, slot, log, error, ct);
    }

    [SupportedOSPlatform("windows")]
    private static int PollLoop(ApSession session, ApConnection conn, Dc1ClientChecks checks,
        string outDir, string slot, Action<string> log, Action<string> error, CancellationToken ct)
    {
        string statePath = ApSlotState.PathFor(outDir, slot);
        var state = ApSlotState.LoadOrNew(statePath, conn.SeedName, slot);
        var sent = new HashSet<long>(state.SentLocationIds);
        sent.UnionWith(conn.CheckedLocations);

        // Reconnect resync (plan §1): the server is authoritative — re-send everything we believe
        // is checked (duplicate-tolerant), and reconcile the full item list below.
        session.SendChecks(sent);

        using var mem = new Win32ProcessMemory();
        var engine = new Dc1PollEngine(mem, checks, conn.GoalRoom);

        log($"polling DINO.exe at 4 Hz — Ctrl-C to disconnect (state: {statePath})");

        bool wasAttached = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!mem.IsAttached && !mem.TryAttach())
                {
                    if (wasAttached) { log("DINO.exe exited — waiting for relaunch…"); wasAttached = false; }
                    if (ct.WaitHandle.WaitOne(2000)) break;
                    continue;
                }
                if (!wasAttached) { log("attached to DINO.exe"); wasAttached = true; }

                var tick = engine.Tick(session.ReceivedItems(), state.AppliedThrough, sent);
                if (tick.InGameplay)
                {
                    if (tick.NewCheckIds.Count > 0)
                    {
                        session.SendChecks(tick.NewCheckIds);
                        sent.UnionWith(tick.NewCheckIds);
                        state.SentLocationIds = sent.ToList();
                        state.Save(statePath);
                        log($"checked: {tick.NewCheckIds.Count} location(s) ({sent.Count} total)");
                    }
                    if (tick.GrantWrites.Count > 0 && engine.Apply(tick.GrantWrites)
                        && tick.AppliedThrough != state.AppliedThrough)
                    {
                        state.AppliedThrough = tick.AppliedThrough;
                        state.Save(statePath);
                        log($"granted items through server index {state.AppliedThrough}");
                    }
                    if (tick.GoalReached)
                    {
                        session.SetGoalAchieved();
                        log("GOAL reached — reported to the server");
                    }
                }
            }
            catch (Exception ex)
            {
                error($"poll error: {ex.Message} — retrying");
            }
            if (ct.WaitHandle.WaitOne(250)) break;
        }

        state.SentLocationIds = sent.ToList();
        state.Save(statePath);
        session.Disconnect();
        log("disconnected");
        return 0;
    }
}
