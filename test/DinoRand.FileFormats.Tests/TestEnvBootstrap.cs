using System.Runtime.CompilerServices;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Loads a repo-root <c>.env</c> (if present) into process environment variables <b>before any test runs</b>,
/// so a dev can point the room round-trip gates at their own Dino Crisis installs without exporting shell
/// vars. Recognised keys are the same ones the tests already read — <c>DINORAND_DC1_DIR</c> and
/// <c>DINORAND_DC2_DIR</c> (see <c>.env.example</c>). An already-set environment variable always wins, and a
/// missing <c>.env</c> is a no-op (CI has none → the tests fall back to <see cref="MockRooms"/>).
///
/// <para><c>.env</c> is gitignored — it holds per-machine install paths, never game bytes.</para>
/// </summary>
internal static class TestEnvBootstrap
{
    [ModuleInitializer]
    internal static void Load()
    {
        var root = FindRepoRoot();
        if (root is null) return;
        var envPath = Path.Combine(root, ".env");
        if (!File.Exists(envPath)) return;

        foreach (var raw in File.ReadAllLines(envPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim().Trim('"');
            // An explicitly-exported var wins over the file.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DinoRand.sln")))
                return dir.FullName;
        return null;
    }
}
