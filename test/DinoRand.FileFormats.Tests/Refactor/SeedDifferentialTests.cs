using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Spoiler;
using Xunit;

namespace DinoRand.FileFormats.Tests.Refactor;

public sealed class SeedDifferentialTests
{
    internal const string BaselineSha = "b15bbd04328e3c3bf338593f5d64eaff7c13088f";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [Fact]
    public void Corpus_and_configuration_contracts_are_complete_and_derived_values_are_exact()
    {
        var corpus = CharacterizationData.LoadCorpus();
        Assert.Equal(0, corpus.Range.Start);
        Assert.Equal(499, corpus.Range.EndInclusive);
        Assert.Equal(new[] { int.MinValue, -1, 0, 1, int.MaxValue }, corpus.BoundarySeeds);
        Assert.Contains(20260719, corpus.ArchipelagoSeeds);
        Assert.Equal(64, corpus.Sha256Derived.Dc1.Length);
        Assert.Equal(64, corpus.Sha256Derived.Dc2.Length);
        Assert.Equal(corpus.Sha256Derived.Dc1, Derive("dc1"));
        Assert.Equal(corpus.Sha256Derived.Dc2, Derive("dc2"));
        Assert.Equal(14, CharacterizationData.LoadMatrix().Rows.Length);

        static int[] Derive(string game) => Enumerable.Range(0, 64).Select(index =>
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"dinorand-refactor-v1|{game}|{index}"));
            return BitConverter.ToInt32(digest, 0);
        }).ToArray();
    }

    [Fact]
    public void Canonicalization_preserves_newline_bytes()
    {
        Assert.NotEqual(CanonicalText("line\r\n", "/fixture/root"),
            CanonicalText("line\n", "/fixture/root"));
        Assert.Equal("<TEMP>|<UTC>\r\n", CanonicalText(
            "/fixture/root|2026-07-21T12:34:56Z\r\n", "/fixture/root"));
    }

    [Fact]
    public void Failure_replay_command_forces_compare_mode_receipt_root_and_exact_test()
    {
        string command = ReplayCommand("/tmp/receipt root");
        Assert.Contains("DINORAND_CHARACTERIZATION_MODE=compare", command, StringComparison.Ordinal);
        Assert.Contains("DINORAND_CHARACTERIZATION_RECEIPT_DIR='/tmp/receipt root'", command,
            StringComparison.Ordinal);
        Assert.EndsWith("--filter FullyQualifiedName~SeedDifferentialTests."
            + nameof(Shipped_runners_record_or_replay_schema_validated_observable_manifests),
            command, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_binding_hashes_every_owned_W1_source()
    {
        var evidence = EvidenceBinding.Create();
        Assert.Equal(BaselineSha, evidence.BaselineSha);
        Assert.Equal(11, evidence.SourceFiles.Length);
        Assert.Equal(11, evidence.SourceFiles.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Matches("^[0-9a-f]{64}$", evidence.SourceFingerprint);
        string repo = EvidenceBinding.FindRepoRoot();
        Assert.All(evidence.SourceFiles, file =>
        {
            string path = Path.Combine(repo, file.Path.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(bytes.LongLength, file.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.Sha256);
        });
    }

    [Fact]
    public void Shipped_runners_record_or_replay_schema_validated_observable_manifests()
    {
        string mode = Environment.GetEnvironmentVariable("DINORAND_CHARACTERIZATION_MODE") ?? "self";
        Assert.Contains(mode, new[] { "self", "record", "compare" });
        string? receiptRoot = Environment.GetEnvironmentVariable("DINORAND_CHARACTERIZATION_RECEIPT_DIR");
        if (mode != "self")
            Assert.False(string.IsNullOrWhiteSpace(receiptRoot),
                "record/compare requires DINORAND_CHARACTERIZATION_RECEIPT_DIR");

        var games = new[] { "dc1", "dc2" };
        var matrix = CharacterizationData.LoadMatrix();
        Assert.Equal(14, matrix.Rows.Length);
        long total = 0;
        var activity = new List<ActivitySample>();
        var evidence = EvidenceBinding.Create();
        foreach (string game in games)
        {
            var seeds = mode == "self" ? new[] { 0 } : CharacterizationData.EffectiveSeeds(game);
            var rows = mode == "self" && game == "dc1" ? matrix.Rows.Take(1).ToArray() : matrix.Rows;
            string? baselinePath = receiptRoot is null ? null : Path.Combine(receiptRoot, $"{game}-observable-manifests.ndjson");
            Directory.CreateDirectory(receiptRoot ?? Path.GetTempPath());

            if (mode == "record")
            {
                using var writer = new StreamWriter(baselinePath!, append: false, new UTF8Encoding(false));
                foreach (int seed in seeds)
                foreach (var row in rows)
                {
                    var manifest = RunCase(game, seed, row, receiptRoot);
                    SchemaValidator.Validate(manifest);
                    CaptureActivity(activity, game, row.Id, manifest);
                    writer.WriteLine(JsonSerializer.Serialize(manifest, JsonOptions));
                    total++;
                }
            }
            else if (mode == "compare")
            {
                Assert.True(File.Exists(baselinePath), $"record receipt missing: {baselinePath}");
                using var reader = new StreamReader(baselinePath!, Encoding.UTF8);
                foreach (int seed in seeds)
                foreach (var row in rows)
                {
                    string? expectedLine = reader.ReadLine();
                    Assert.False(string.IsNullOrWhiteSpace(expectedLine), $"missing baseline for {game}/{seed}/{row.Id}");
                    using var expected = JsonDocument.Parse(expectedLine!);
                    SchemaValidator.Validate(expected.RootElement);
                    var actual = RunCase(game, seed, row, receiptRoot);
                    SchemaValidator.Validate(actual);
                    CaptureActivity(activity, game, row.Id, actual);
                    string actualLine = JsonSerializer.Serialize(actual, JsonOptions);
                    if (!JsonNode.DeepEquals(JsonNode.Parse(expectedLine!), JsonNode.Parse(actualLine)))
                    {
                        WriteFailure(receiptRoot!, game, seed, row, expected.RootElement, actual);
                        Assert.Fail($"observable mismatch: {game}/{seed}/{row.Id}");
                    }
                    total++;
                }
                Assert.Null(reader.ReadLine());
            }
            else
            {
                foreach (int seed in seeds)
                foreach (var row in rows)
                {
                    var recorded = RunCase(game, seed, row, null);
                    var replayed = RunCase(game, seed, row, null);
                    SchemaValidator.Validate(recorded);
                    SchemaValidator.Validate(replayed);
                    CaptureActivity(activity, game, row.Id, recorded);
                    Assert.True(JsonNode.DeepEquals(
                        JsonSerializer.SerializeToNode(recorded, JsonOptions),
                        JsonSerializer.SerializeToNode(replayed, JsonOptions)));
                    total++;
                }
            }
        }

        if (receiptRoot is not null)
        {
            var counts = new
            {
                mode,
                evidence,
                games = games.ToDictionary(g => g, g => new
                {
                    seeds = mode == "self" ? 1 : CharacterizationData.EffectiveSeeds(g).Length,
                    configurations = mode == "self" && g == "dc1" ? 1 : matrix.Rows.Length,
                    cases = (long)(mode == "self" ? 1 : CharacterizationData.EffectiveSeeds(g).Length)
                        * (mode == "self" && g == "dc1" ? 1 : matrix.Rows.Length),
                }),
                totalCases = total,
                mismatches = 0,
                dc2Activity = activity.GroupBy(sample => sample.Row, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        row = group.Key,
                        cases = group.Count(),
                        minRoomsWritten = group.Min(sample => sample.RoomsWritten),
                        maxRoomsWritten = group.Max(sample => sample.RoomsWritten),
                        casesWithBinaryArtifacts = group.Count(sample => sample.BinaryArtifactNames.Length > 0),
                        binaryArtifactNames = group.SelectMany(sample => sample.BinaryArtifactNames)
                            .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                    }).ToArray(),
            };
            File.WriteAllText(Path.Combine(receiptRoot, $"{mode}-summary.json"),
                JsonSerializer.Serialize(counts, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
        }
    }

    private static void CaptureActivity(List<ActivitySample> activity, string game, string row,
        ObservableManifest manifest)
    {
        if (game != "dc2") return;
        activity.Add(new ActivitySample(row, manifest.Result.RoomsWritten,
            manifest.Artifacts.Where(artifact => artifact.Comparison == "exact")
                .Select(artifact => artifact.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray()));
    }

    private static ObservableManifest RunCase(string game, int seedValue, ConfigRow row, string? receiptRoot)
    {
        string root = Directory.CreateTempSubdirectory($"dinorand-w1-{game}-").FullName;
        var seed = new Seed(seedValue);
        var config = CharacterizationData.CreateConfig(game == "dc1" ? row.Dc1 : row.Dc2);
        var runtime = RuntimeFingerprint.Create();
        var evidence = EvidenceBinding.Create(runtime);
        InputFingerprint? inputFingerprint = null;
        try
        {
            string install = game == "dc1"
                ? SyntheticInputs.CreateDc1Install(root)
                : SyntheticInputs.CreateDc2Install(root, config);
            string output = Path.Combine(root, "output");
            inputFingerprint = SyntheticInputs.Fingerprint(install, game);
            int discovered;
            int written;
            IReadOnlyList<string> log;
            if (game == "dc1")
            {
                var result = new RandomizerRunner(new DinoCrisis1()).Run(install, output, seed, config);
                discovered = result.RoomsWritten;
                written = result.RoomsWritten;
                log = result.Log;
            }
            else
            {
                var result = new Dc2RandomizerRunner(new DinoCrisis2()).Run(install, output, seed, config);
                discovered = result.RoomCount;
                written = result.RoomsWritten;
                log = result.Log;
            }

            var artifacts = Inventory(output, root);
            if (game == "dc2") AssertDc2Activity(row, written, log, artifacts);

            return new ObservableManifest(
                1,
                $"{game}|{seedValue}|{row.Id}",
                game,
                new SeedDescriptor(seedValue, seed.ToString(), SeedString.Encode(seed, config)),
                JsonSerializer.SerializeToElement(config, JsonOptions),
                inputFingerprint,
                runtime,
                evidence,
                new ResultDescriptor(discovered, written, log.Select(l => CanonicalText(l, root)).ToArray()),
                artifacts);
        }
        catch (Exception ex)
        {
            if (receiptRoot is not null)
            {
                Directory.CreateDirectory(Path.Combine(receiptRoot, "failures"));
                File.WriteAllText(Path.Combine(receiptRoot, "failures", $"{game}-{seedValue}-{row.Id}-exception.json"),
                    JsonSerializer.Serialize(new
                    {
                        game,
                        seed = new { numeric = seedValue, @string = seed.ToString(), encoded = SeedString.Encode(seed, config) },
                        configuration = config,
                        inputFingerprint = inputFingerprint ?? InputFingerprint.Unavailable(),
                        runtimeFingerprint = runtime,
                        evidence,
                        test = nameof(Shipped_runners_record_or_replay_schema_validated_observable_manifests),
                        command = ReplayCommand(receiptRoot),
                        exception = ex.ToString(),
                    }, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
            }
            throw;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ArtifactDescriptor[] Inventory(string output, string root)
    {
        if (!Directory.Exists(output)) return Array.Empty<ArtifactDescriptor>();
        return Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .OrderBy(p => Path.GetRelativePath(output, p), StringComparer.Ordinal)
            .Select(path =>
            {
                string relative = Path.GetRelativePath(output, path).Replace('\\', '/');
                bool text = Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".dgml";
                byte[] bytes = text
                    ? Encoding.UTF8.GetBytes(CanonicalText(File.ReadAllText(path), root))
                    : File.ReadAllBytes(path);
                return new ArtifactDescriptor(relative, bytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    text ? "canonical-text" : "exact");
            }).ToArray();
    }

    internal static string CanonicalText(string text, string tempRoot)
    {
        string result = text.Replace(tempRoot, "<TEMP>", StringComparison.Ordinal)
            .Replace(tempRoot.Replace('\\', '/'), "<TEMP>", StringComparison.Ordinal);
        result = Regex.Replace(result, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", "<UTC>");
        return result;
    }

    private static void AssertDc2Activity(ConfigRow row, int roomsWritten, IReadOnlyList<string> log,
        IReadOnlyList<ArtifactDescriptor> artifacts)
    {
        var expected = row.Dc2Expected;
        Assert.True(roomsWritten >= expected.MinRoomsWritten,
            $"{row.Id}: expected at least {expected.MinRoomsWritten} DC2 room writes, got {roomsWritten}");
        if (expected.MaxRoomsWritten is int max)
            Assert.True(roomsWritten <= max, $"{row.Id}: expected at most {max} DC2 room writes, got {roomsWritten}");
        var paths = artifacts.Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
        Assert.All(expected.RequiredArtifacts, path => Assert.Contains(path, paths));
        Assert.All(expected.RequiredLogFragments,
            fragment => Assert.Contains(log, line => line.Contains(fragment, StringComparison.Ordinal)));
        if (expected.ForbidBinaryArtifacts)
            Assert.DoesNotContain(artifacts, artifact => artifact.Comparison == "exact");
    }

    internal static string ReplayCommand(string receiptRoot)
    {
        static string Q(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return $"DINORAND_CHARACTERIZATION_MODE=compare DINORAND_CHARACTERIZATION_RECEIPT_DIR={Q(receiptRoot)} "
             + "dotnet test test/DinoRand.FileFormats.Tests/DinoRand.FileFormats.Tests.csproj -c Release --no-build "
             + "--filter FullyQualifiedName~SeedDifferentialTests."
             + nameof(Shipped_runners_record_or_replay_schema_validated_observable_manifests);
    }

    private static void WriteFailure(string root, string game, int seed, ConfigRow row,
        JsonElement expected, ObservableManifest actual)
    {
        string dir = Directory.CreateDirectory(Path.Combine(root, "failures")).FullName;
        File.WriteAllText(Path.Combine(dir, $"{game}-{seed}-{row.Id}.json"), JsonSerializer.Serialize(new
        {
            game,
            seed = actual.Seed,
            configuration = actual.Configuration,
            inputFingerprint = actual.InputFingerprint,
            runtimeFingerprint = actual.RuntimeFingerprint,
            evidence = actual.Evidence,
            test = nameof(Shipped_runners_record_or_replay_schema_validated_observable_manifests),
            command = ReplayCommand(root),
            expected,
            actual,
        }, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
    }
}

internal static class SyntheticInputs
{
    public static string CreateDc1Install(string root)
    {
        string data = Directory.CreateDirectory(Path.Combine(root, "install", "Data")).FullName;
        foreach (string path in Directory.EnumerateFiles(MockRooms.Dc1DataDir(), "*.dat"))
            File.Copy(path, Path.Combine(data, Path.GetFileName(path)));
        // Runner fixtures must model the production start/goal contract now that final progression
        // verification is a hard publication gate. These authored rooms contain only a reciprocal,
        // free synthetic door pair; no game bytes and no extra randomizable records.
        File.WriteAllBytes(Path.Combine(data, "st10d.dat"), SyntheticRoom.Dc1Room(
            Array.Empty<SyntheticRoom.Item>(),
            new[] { new SyntheticRoom.Door(6, 0x0d, 0, 0) },
            Array.Empty<SyntheticRoom.Enemy>()));
        File.WriteAllBytes(Path.Combine(data, "st60d.dat"), SyntheticRoom.Dc1Room(
            Array.Empty<SyntheticRoom.Item>(),
            new[] { new SyntheticRoom.Door(1, 0x0d, 0, 0) },
            Array.Empty<SyntheticRoom.Enemy>()));
        return Path.Combine(root, "install");
    }

    public static string CreateDc2Install(string root, RandomizerConfig config)
    {
        string data = Directory.CreateDirectory(Path.Combine(root, "install", "rebirth", "Data")).FullName;
        File.WriteAllBytes(Path.Combine(data, "ST104.DAT"), BigBlobRoom(0x1000,
            (0x2C9, (byte)0x02), (0x2CC, (byte)0x0F),
            (0x649, (byte)0x02), (0x64C, (byte)0x0F)));
        File.WriteAllBytes(Path.Combine(data, "ST202.DAT"), BigBlobRoom(0x3000,
            (6068, (byte)0x02), (6092, (byte)0x0F),
            (6380, (byte)0x02), (6404, (byte)0x0F)));
        foreach (var spec in Dc2CircuitPatch.Rooms)
            File.WriteAllBytes(Path.Combine(data, spec.FileName),
                PadScdBlob(Dc2CircuitPatchTests.MakePackage(spec), 0x1A000));
        File.WriteAllBytes(Path.Combine(data, "ST205.DAT"),
            PadScdBlob(Dc2PlateKeyPatchTests.MakePackage(), 0x1A000));
        if (config.RandomizeItems)
        {
            foreach (string roomId in new[] { "ST104", "ST202", "ST402" })
            {
                string path = Path.Combine(data, roomId + ".DAT");
                File.WriteAllBytes(path, AddItemSites(File.ReadAllBytes(path), roomId));
            }
        }
        return Path.Combine(root, "install");
    }

    private static byte[] PadScdBlob(byte[] packageBytes, int minimumLength)
    {
        var package = GianPackage.TryParse(packageBytes)
            ?? throw new InvalidDataException("authored DC2 package did not parse");
        int index = package.Entries.ToList().FindIndex(entry => entry.Type == GianEntryType.Lzss0);
        if (index < 0) throw new InvalidDataException("authored DC2 package has no LZSS0 entry");
        var entry = package.Entries[index];
        byte[] blob = Lzss.Decompress(packageBytes.AsSpan(entry.PayloadOffset, checked((int)entry.DeclaredSize)));
        if (blob.Length < minimumLength) Array.Resize(ref blob, minimumLength);
        return PackageRepacker.ReplaceEntryDc2(packageBytes, index, Lzss.Compress(blob));
    }

    private static byte[] BigBlobRoom(int blobLength, params (int Offset, byte Value)[] bytes)
    {
        var blob = new byte[blobLength];
        foreach (var (offset, value) in bytes) blob[offset] = value;
        return SyntheticRoom.Package(GianPackage.Dc2EntrySize,
            (GianEntryType.Lzss0, Lzss.Compress(blob)),
            (GianEntryType.Data, new byte[16]));
    }

    private static byte[] AddItemSites(byte[] packageBytes, string roomId)
    {
        Dc2ItemEditor.ItemSiteSpec[] sites = Dc2ItemData.LoadEmbedded().Locations
            .Where(location => location.RoomId == roomId)
            .Select(location => location.Site).ToArray();
        Assert.NotEmpty(sites);
        Assert.All(sites, site =>
        {
            Assert.Equal(0, site.RoutineOrdinal);
            Assert.Equal(0, site.VmDirectoryIndex);
            Assert.Equal(new[] { 0 }, site.VmDirectoryIndices);
        });

        var package = GianPackage.TryParse(packageBytes)!;
        int entryIndex = package.Entries.ToList().FindIndex(entry => entry.Type == GianEntryType.Lzss0);
        var entry = package.Entries[entryIndex];
        byte[] blob = Lzss.Decompress(packageBytes.AsSpan(entry.PayloadOffset, (int)entry.DeclaredSize));
        int requiredLength = Math.Max(blob.Length, sites.Max(site => site.OpOffset) + 0x100);
        Array.Resize(ref blob, requiredLength);
        blob.AsSpan(0, 0x80).Clear();

        const int sectionStart = 0x80;
        const int opBase = sectionStart + 0x1c;
        int routineStart = sites[0].RoutineStart;
        Assert.All(sites, site => Assert.Equal(routineStart, site.RoutineStart));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x14, 4),
            Dc2DoorEditor.BlobBaseVa + sectionStart);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(opBase, 4),
            (uint)(routineStart - opBase));
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(opBase + 4, 4),
            (uint)(blob.Length - sectionStart));

        int routineEnd = sites.Max(site => site.OpOffset) + 2;
        blob.AsSpan(routineStart, routineEnd - routineStart).Clear();
        for (int offset = routineStart; offset < routineEnd; offset += 2)
            blob[offset] = 0x10;
        foreach (Dc2ItemEditor.ItemSiteSpec site in sites)
        {
            int firstPush = site.SlotOperand?.PushOffset ?? site.KindOperand!.PushOffset;
            for (int push = firstPush; push < site.OpOffset; push += 4)
            {
                blob[push] = 0x05;
                blob[push + 1] = 0;
                BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(push + 2, 2), 0);
            }
            foreach (Dc2ItemEditor.OperandPin pin in new[]
                     {
                         site.ItemOperand, site.P3Operand, site.Flag5Operand, site.CleanupOperand,
                         site.KindOperand, site.SlotOperand,
                     }.OfType<Dc2ItemEditor.OperandPin>())
            {
                blob[pin.PushOffset] = 0x05;
                blob[pin.PushOffset + 1] = pin.Mode;
                BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(pin.ValueOffset, 2), pin.ExpectedValue);
            }
            blob[site.OpOffset] = site.Opcode;
            blob[site.OpOffset + 1] = 0;
        }
        return PackageRepacker.ReplaceEntryDc2(packageBytes, entryIndex, Lzss.Compress(blob));
    }

    public static InputFingerprint Fingerprint(string install, string game)
    {
        string data = game == "dc1" ? Path.Combine(install, "Data") : Path.Combine(install, "rebirth", "Data");
        var files = Directory.EnumerateFiles(data).OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new { Name = Path.GetFileName(path), Bytes = File.ReadAllBytes(path) }).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Name));
            hash.AppendData(BitConverter.GetBytes(file.Bytes.LongLength));
            hash.AppendData(SHA256.HashData(file.Bytes));
        }
        return new InputFingerprint("authored-synthetic", Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            files.Select(f => f.Name).ToArray());
    }
}

internal static class CharacterizationData
{
    private static string Baseline(string name) => Path.Combine(AppContext.BaseDirectory, "Refactor", "Baselines", name);
    public static SeedCorpus LoadCorpus() => JsonSerializer.Deserialize<SeedCorpus>(File.ReadAllText(Baseline("seed-corpus.json")), Options)!;
    public static ConfigMatrix LoadMatrix() => JsonSerializer.Deserialize<ConfigMatrix>(File.ReadAllText(Baseline("config-matrix.json")), Options)!;
    public static string SchemaPath => Baseline("observable-manifest.schema.json");
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static int[] EffectiveSeeds(string game)
    {
        var c = LoadCorpus();
        var derived = game == "dc1" ? c.Sha256Derived.Dc1 : c.Sha256Derived.Dc2;
        return Enumerable.Range(c.Range.Start, c.Range.EndInclusive - c.Range.Start + 1)
            .Concat(c.BoundarySeeds).Concat(c.ExistingFixedSeeds).Concat(c.ArchipelagoSeeds).Concat(derived)
            .Distinct().Order().ToArray();
    }

    public static RandomizerConfig CreateConfig(JsonElement overrides)
    {
        var config = new RandomizerConfig();
        foreach (var item in overrides.EnumerateObject())
        {
            PropertyInfo property = typeof(RandomizerConfig).GetProperty(item.Name)
                ?? throw new InvalidDataException($"unknown RandomizerConfig property {item.Name}");
            Assert.True(property.CanWrite, $"configuration property is not writable: {item.Name}");
            object? value = JsonSerializer.Deserialize(item.Value.GetRawText(), property.PropertyType, Options);
            property.SetValue(config, value);
        }
        return config;
    }
}

internal static class SchemaValidator
{
    public static void Validate(ObservableManifest manifest) =>
        Validate(JsonSerializer.SerializeToElement(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    public static void Validate(JsonElement instance)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(CharacterizationData.SchemaPath));
        ValidateNode(instance, schema.RootElement, schema.RootElement, "$" );
    }

    private static void ValidateNode(JsonElement value, JsonElement schema, JsonElement root, string path)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            JsonElement target = root;
            foreach (string segment in reference.GetString()![2..].Split('/')) target = target.GetProperty(segment);
            ValidateNode(value, target, root, path);
            return;
        }
        if (schema.TryGetProperty("type", out var type))
            Assert.True(TypeMatches(value, type.GetString()!), $"{path}: expected {type.GetString()}, got {value.ValueKind}");
        if (schema.TryGetProperty("const", out var constant))
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(value.GetRawText()), JsonNode.Parse(constant.GetRawText())), $"{path}: const mismatch");
        if (schema.TryGetProperty("enum", out var choices))
            Assert.Contains(choices.EnumerateArray(), c => JsonNode.DeepEquals(JsonNode.Parse(value.GetRawText()), JsonNode.Parse(c.GetRawText())));
        if (schema.TryGetProperty("pattern", out var pattern)) Assert.Matches(pattern.GetString()!, value.GetString()!);
        if (schema.TryGetProperty("minLength", out var minLength)) Assert.True(value.GetString()!.Length >= minLength.GetInt32(), $"{path}: too short");
        if (schema.TryGetProperty("minItems", out var minItems)) Assert.True(value.GetArrayLength() >= minItems.GetInt32(), $"{path}: too few items");

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray()) Assert.True(value.TryGetProperty(name.GetString()!, out _), $"{path}: missing {name.GetString()}");
            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in value.EnumerateObject())
                    if (properties.TryGetProperty(property.Name, out var propertySchema))
                        ValidateNode(property.Value, propertySchema, root, $"{path}.{property.Name}");
                    else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                        Assert.Fail($"{path}: additional property {property.Name}");
            }
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            int index = 0;
            foreach (var item in value.EnumerateArray()) ValidateNode(item, items, root, $"{path}[{index++}]");
        }
    }

    private static bool TypeMatches(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false,
    };
}

internal sealed record EvidenceBinding(string BaselineSha, string SourceFingerprint,
    SourceFileFingerprint[] SourceFiles, RuntimeFingerprint RuntimeFingerprint)
{
    private static readonly string[] OwnedPaths =
    {
        "test/DinoRand.FileFormats.Tests/DinoRand.FileFormats.Tests.csproj",
        "test/DinoRand.FileFormats.Tests/Refactor/RealInstallGate.cs",
        "test/DinoRand.FileFormats.Tests/Refactor/CliCharacterizationTests.cs",
        "test/DinoRand.FileFormats.Tests/Refactor/SeedDifferentialTests.cs",
        "test/DinoRand.FileFormats.Tests/Refactor/InstallCharacterizationTests.cs",
        "test/DinoRand.FileFormats.Tests/Refactor/FileFormatCharacterizationTests.cs",
        "test/DinoRand.FileFormats.Tests/Refactor/ViewModelCharacterizationTests.cs",
        "test/DinoRand.FileFormats.Tests/Refactor/RunnerArtifactCharacterizationTests.cs",
        "test/DinoRand.FileFormats.Tests/Refactor/Baselines/seed-corpus.json",
        "test/DinoRand.FileFormats.Tests/Refactor/Baselines/config-matrix.json",
        "test/DinoRand.FileFormats.Tests/Refactor/Baselines/observable-manifest.schema.json",
    };

    private static readonly Lazy<(string Aggregate, SourceFileFingerprint[] Files)> Source = new(ComputeSource);

    public static EvidenceBinding Create(RuntimeFingerprint? runtime = null) => new(
        SeedDifferentialTests.BaselineSha, Source.Value.Aggregate, Source.Value.Files,
        runtime ?? RuntimeFingerprint.Create());

    private static (string, SourceFileFingerprint[]) ComputeSource()
    {
        string repo = FindRepoRoot();
        var files = OwnedPaths.Select(relative =>
        {
            string path = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"W1 evidence source missing: {relative}");
            byte[] bytes = File.ReadAllBytes(path);
            return new SourceFileFingerprint(relative, bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }).ToArray();
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            aggregate.AppendData(Encoding.UTF8.GetBytes(file.Path));
            aggregate.AppendData(BitConverter.GetBytes(file.Length));
            aggregate.AppendData(Convert.FromHexString(file.Sha256));
        }
        return (Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(), files);
    }

    internal static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DinoRand.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("repository root containing DinoRand.sln was not found");
    }
}

internal sealed record SourceFileFingerprint(string Path, long Length, string Sha256);

internal sealed record ObservableManifest(int SchemaVersion, string CaseId, string Game, SeedDescriptor Seed,
    JsonElement Configuration, InputFingerprint InputFingerprint, RuntimeFingerprint RuntimeFingerprint,
    EvidenceBinding Evidence, ResultDescriptor Result, ArtifactDescriptor[] Artifacts);
internal sealed record SeedDescriptor(int Numeric, string String, string Encoded);
internal sealed record InputFingerprint(string Kind, string Sha256, string[] Files)
{
    public static InputFingerprint Unavailable() => new("unavailable-before-failure",
        Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(), Array.Empty<string>());
}
internal sealed record RuntimeFingerprint(string Framework, string Os, string ProcessArchitecture, string AssemblyVersion)
{
    public static RuntimeFingerprint Create() => new(RuntimeInformation.FrameworkDescription,
        RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
        typeof(RandomizerRunner).Assembly.GetName().Version?.ToString() ?? "unknown");
}
internal sealed record ResultDescriptor(int RoomsDiscovered, int RoomsWritten, string[] Log);
internal sealed record ArtifactDescriptor(string Path, long Length, string Sha256, string Comparison);
internal sealed record ActivitySample(string Row, int RoomsWritten, string[] BinaryArtifactNames);
internal sealed record SeedCorpus(int SchemaVersion, SeedRange Range, int[] BoundarySeeds, int[] ExistingFixedSeeds,
    int[] ArchipelagoSeeds, string Derivation, DerivedSeeds Sha256Derived);
internal sealed record SeedRange(int Start, int EndInclusive);
internal sealed record DerivedSeeds(int[] Dc1, int[] Dc2);
internal sealed record ConfigMatrix(int SchemaVersion, ConfigRow[] Rows);
internal sealed record ConfigRow(string Id, string Kind, JsonElement Dc1, JsonElement Dc2,
    Dc2ExpectedActivity Dc2Expected);
internal sealed record Dc2ExpectedActivity(int MinRoomsWritten, int? MaxRoomsWritten,
    string[] RequiredArtifacts, string[] RequiredLogFragments, bool ForbidBinaryArtifacts);
