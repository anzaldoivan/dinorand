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
    private int? TryRunArchipelagoCommand()
    {
        // Archipelago runtime client, DC1 v1 (docs/decisions/cross/AP-CLIENT-PLAN.md): connect to an AP
        // server, patch AP's fill into the local install (loop-closing, D5), then poll the running
        // DINO.exe — pickups → LocationChecks, ReceivedItems → grants, goal room → CLIENT_GOAL.
        // Long-running; Ctrl-C disconnects cleanly. The poll half needs the WINDOWS host (WSL cannot
        // attach to a Windows process).
        if (GetOpt("--ap-connect") is { } apHostPort)
            return ApConnectDc1(apHostPort, GetOpt("--ap-slot"), GetOpt("--ap-password"),
                                GetOpt("--install"), GetOpt("--out"));

        return null;
    }

    // ---------------------------------------------------------------------------------------------
    // Archipelago runtime client, DC1 v1 (docs/decisions/cross/AP-CLIENT-PLAN.md).
    // Flow: connect/login → slot_data placements → patch rooms (ApPlacementInstaller) → overlay
    // install (GameInstaller) → attach DINO.exe → 4 Hz poll loop until Ctrl-C.
    // ---------------------------------------------------------------------------------------------
    // The flow itself lives in Dc1ApRunner (DinoRand.Randomizer/Ap) so this command and the Avalonia
    // connect tab run ONE implementation; this binder only supplies the console sinks and Ctrl-C.
    static int ApConnectDc1(string hostPort, string? slot, string? password, string? install, string? outDirArg)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        return Dc1ApRunner.Run(hostPort, slot, password, install, outDirArg,
            Console.WriteLine, Console.Error.WriteLine, stop.Token);
    }
}
