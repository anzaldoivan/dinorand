using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace DinoRand.FileFormats.Tests.Refactor;

public sealed class CliCharacterizationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void Help_and_invalid_invocations_pin_exit_stdout_and_stderr()
    {
        var value = CaptureTextInvocations();
        Assert.Equal(0, value.Help.ExitCode);
        Assert.Contains("DinoRand", value.Help.Stdout);
        Assert.Contains("--install", value.Help.Stdout);
        Assert.Equal("", value.Help.Stderr);
        Assert.NotEqual(0, value.Invalid.ExitCode);
        Assert.Contains("error", value.Invalid.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", value.Invalid.Stdout);
        Assert.Equal(value, CaptureTextInvocations());
        RecordOrCompare("cli-text", value, nameof(Help_and_invalid_invocations_pin_exit_stdout_and_stderr));
    }

    [Fact]
    public void Synthetic_randomize_install_and_restore_record_then_exactly_replay_all_observables()
    {
        var value = CaptureScenario();
        Assert.Equal(0, value.Randomize.Result.ExitCode);
        Assert.Equal("", value.Randomize.Result.Stderr);
        Assert.Contains("seed 123 →", value.Randomize.Result.Stdout, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine, value.Randomize.Result.Stdout, StringComparison.Ordinal);
        Assert.Contains("log_dinorand.txt", value.Randomize.Inventory.Select(x => x.Path));
        Assert.Contains("map.dgml", value.Randomize.Inventory.Select(x => x.Path));
        Assert.DoesNotContain("SPOILER.md", value.Randomize.Inventory.Select(x => x.Path));

        Assert.Equal(0, value.Install.Result.ExitCode);
        Assert.Equal("", value.Install.Result.Stderr);
        Assert.Contains("installed to <TEMP>/install/Data:", value.Install.Result.Stdout, StringComparison.Ordinal);
        Assert.True(value.Install.ManifestApplied);
        Assert.Contains(".dinorand_backup/manifest.json", value.Install.Inventory.Select(x => x.Path));

        Assert.Equal(0, value.Restore.Result.ExitCode);
        Assert.Equal("", value.Restore.Result.Stderr);
        Assert.Contains("restored", value.Restore.Result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.False(value.Restore.ManifestApplied);
        Assert.Equal(value.OriginalLiveInventory, value.Restore.LiveInventory);

        if (Mode() == "self") AssertJsonEqual(value, CaptureScenario(), "CLI self replay drifted");
        RecordOrCompare("cli-randomize-install-restore", value,
            nameof(Synthetic_randomize_install_and_restore_record_then_exactly_replay_all_observables));
    }

    private static TextInvocations CaptureTextInvocations() =>
        new(Run("--help"), Run("--definitely-invalid-w1-option"));

    private static CliScenario CaptureScenario()
    {
        string root = Directory.CreateTempSubdirectory("dinorand-w1-cli-").FullName;
        try
        {
            string install = SyntheticInputs.CreateDc1Install(root);
            string data = Path.Combine(install, "Data");
            string output = Path.Combine(root, "output");
            var input = SyntheticInputs.Fingerprint(install, "dc1");
            var originalLive = Snapshot(data, root, SearchOption.TopDirectoryOnly);
            using var overrides = JsonDocument.Parse("{\"RandomizeItems\":false,\"RandomizeEnemies\":false}");
            var configuration = CharacterizationData.CreateConfig(overrides.RootElement);

            var randomizeResult = Canonicalize(Run("--install", install, "--out", output, "--seed", "123",
                "--no-items", "--no-enemies", "--no-spoiler"), root);
            var randomize = new CliPhase(randomizeResult, Snapshot(output, root), ManifestApplied: null);

            var installResult = Canonicalize(Run("--install", install, "--out", output, "--seed", "123",
                "--no-items", "--no-enemies", "--no-spoiler", "--install-to-data"), root);
            var installPhase = new CliPhase(installResult, Snapshot(data, root), ReadApplied(data));

            var restoreResult = Canonicalize(Run("--install", install, "--restore"), root);
            var restorePhase = new RestorePhase(restoreResult, Snapshot(data, root), ReadApplied(data),
                Snapshot(data, root, SearchOption.TopDirectoryOnly));

            return new CliScenario(1, new SeedDescriptor(123, new DinoRand.Randomizer.Seed(123).ToString(),
                    DinoRand.Randomizer.Spoiler.SeedString.Encode(new DinoRand.Randomizer.Seed(123),
                        configuration)),
                JsonSerializer.SerializeToElement(configuration),
                new[] { "--no-items", "--no-enemies", "--no-spoiler" }, input,
                EvidenceBinding.Create(), originalLive, randomize, installPhase, restorePhase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CliResult Run(params string[] args)
    {
        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release" : "Debug";
        string repo = EvidenceBinding.FindRepoRoot();
        string cli = Path.Combine(repo, "src", "DinoRand.Cli", "bin", configuration, "net8.0", "dinorand.dll");
        Assert.True(File.Exists(cli), $"built CLI not found: {cli}");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(cli);
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "CLI invocation timed out");
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    private static FileArtifact[] Snapshot(string root, string tempRoot,
        SearchOption option = SearchOption.AllDirectories) => Directory.EnumerateFiles(root, "*", option)
        .Select(path =>
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            byte[] bytes;
            string comparison;
            if (relative.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                if (node.ContainsKey("installedUtc")) node["installedUtc"] = "<UTC>";
                bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
                comparison = "canonical-json";
            }
            else if (Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".dgml")
            {
                bytes = Encoding.UTF8.GetBytes(SeedDifferentialTests.CanonicalText(File.ReadAllText(path), tempRoot));
                comparison = "canonical-text";
            }
            else
            {
                bytes = File.ReadAllBytes(path);
                comparison = "exact";
            }
            return new FileArtifact(relative, bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), comparison);
        }).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();

    private static bool ReadApplied(string dataDir)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, ".dinorand_backup", "manifest.json")));
        return manifest.RootElement.GetProperty("applied").GetBoolean();
    }

    private static CliResult Canonicalize(CliResult result, string tempRoot) => result with
    {
        Stdout = SeedDifferentialTests.CanonicalText(result.Stdout, tempRoot),
        Stderr = SeedDifferentialTests.CanonicalText(result.Stderr, tempRoot),
    };

    private static string Mode()
    {
        string mode = Environment.GetEnvironmentVariable("DINORAND_CHARACTERIZATION_MODE") ?? "self";
        Assert.Contains(mode, new[] { "self", "record", "compare" });
        return mode;
    }

    private static void RecordOrCompare(string name, object value, string test)
    {
        string mode = Mode();
        string? root = Environment.GetEnvironmentVariable("DINORAND_CLI_RECEIPT_DIR");
        if (mode == "self" && string.IsNullOrWhiteSpace(root)) return;
        Assert.False(string.IsNullOrWhiteSpace(root), "CLI record/compare requires DINORAND_CLI_RECEIPT_DIR");
        Directory.CreateDirectory(root!);
        var envelope = new { schemaVersion = 1, evidence = EvidenceBinding.Create(), value };
        string baseline = Path.Combine(root!, $"{name}-baseline.json");
        if (mode == "record")
        {
            File.WriteAllText(baseline, JsonSerializer.Serialize(envelope, JsonOptions));
            return;
        }
        if (mode == "self")
        {
            File.WriteAllText(Path.Combine(root!, $"{name}-self.json"), JsonSerializer.Serialize(envelope, JsonOptions));
            return;
        }

        Assert.True(File.Exists(baseline), $"CLI record receipt missing: {baseline}");
        var expected = JsonNode.Parse(File.ReadAllText(baseline));
        var actual = JsonSerializer.SerializeToNode(envelope);
        if (!JsonNode.DeepEquals(expected, actual))
        {
            string replay = ReplayCommand(root!, test);
            File.WriteAllText(Path.Combine(root!, $"{name}-failure.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                evidence = EvidenceBinding.Create(),
                inputFingerprint = value is CliScenario scenario ? scenario.InputFingerprint : InputFingerprint.Unavailable(),
                test,
                command = replay,
                expected,
                actual,
            }, JsonOptions));
            Assert.Fail($"CLI observable mismatch; replay with: {replay}");
        }
        File.WriteAllText(Path.Combine(root!, $"{name}-compare.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            evidence = EvidenceBinding.Create(),
            matched = true,
            baselineFile = Path.GetFileName(baseline),
            test,
            command = ReplayCommand(root!, test),
        }, JsonOptions));
    }

    private static string ReplayCommand(string root, string test)
    {
        static string Q(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return $"DINORAND_CHARACTERIZATION_MODE=compare DINORAND_CLI_RECEIPT_DIR={Q(root)} "
             + "dotnet test test/DinoRand.FileFormats.Tests/DinoRand.FileFormats.Tests.csproj -c Release --no-build "
             + $"--filter FullyQualifiedName~CliCharacterizationTests.{test}";
    }

    private static void AssertJsonEqual(object expected, object actual, string message) =>
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(expected), JsonSerializer.SerializeToNode(actual)), message);

    private sealed record TextInvocations(CliResult Help, CliResult Invalid);
    private sealed record CliScenario(int SchemaVersion, SeedDescriptor Seed, JsonElement Configuration,
        string[] ConfigurationArguments,
        InputFingerprint InputFingerprint, EvidenceBinding Evidence, FileArtifact[] OriginalLiveInventory,
        CliPhase Randomize, CliPhase Install, RestorePhase Restore);
    private sealed record CliPhase(CliResult Result, FileArtifact[] Inventory, bool? ManifestApplied);
    private sealed record RestorePhase(CliResult Result, FileArtifact[] Inventory, bool ManifestApplied,
        FileArtifact[] LiveInventory);
    private sealed record FileArtifact(string Path, long Length, string Sha256, string Comparison);
    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
