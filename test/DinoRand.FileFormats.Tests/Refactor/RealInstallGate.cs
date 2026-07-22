using System.Security.Cryptography;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests.Refactor;

public sealed class RealInstallGate
{
    [Fact]
    public void Required_eligibility_rejects_non_PE_and_unknown_DC2_builds()
    {
        Assert.Throws<InvalidDataException>(() => ValidatePeAndProtection(new byte[64], "Dino2.exe"));
        byte[] foreign = File.ReadAllBytes(typeof(RealInstallGate).Assembly.Location);
        Array.Resize(ref foreign, Dc2WpGatePatch.ExpectedLength);
        Assert.Throws<InvalidDataException>(() => ValidateDc2Executable(foreign));
    }

    [Fact, Trait("RealInstall", "DC1")]
    public void Required_DC1_every_discovered_case_executes_parse_write_install_restore()
        => Execute("dc1", "DINORAND_DC1_DIR");

    [Fact, Trait("RealInstall", "DC2")]
    public void Required_DC2_every_discovered_case_executes_parse_repack_install_restore()
        => Execute("dc2", "DINORAND_DC2_DIR");

    private static void Execute(string game, string variable)
    {
        bool required = Environment.GetEnvironmentVariable("DINORAND_REQUIRE_REAL_INSTALL") == "1";
        string? root = Environment.GetEnvironmentVariable(variable);
        if (!required && string.IsNullOrWhiteSpace(root)) return;
        Assert.True(required, "A real-install fixture was supplied without required mode; set DINORAND_REQUIRE_REAL_INSTALL=1.");
        Assert.False(string.IsNullOrWhiteSpace(root), $"required fixture variable {variable} is missing");

        var definition = game == "dc1" ? (GameDefinition)new DinoCrisis1() : new DinoCrisis2();
        string? dataDir = definition.GetDataDir(root!);
        Assert.False(string.IsNullOrWhiteSpace(dataDir), $"{game} Data folder was not eligible");
        string exeName = game == "dc1" ? "DINO.exe" : "Dino2.exe";
        string exePath = Path.Combine(Path.GetDirectoryName(dataDir!)!, exeName);
        Assert.True(File.Exists(exePath), $"{game} executable was not present beside the selected Data folder");
        var rooms = definition.EnumerateRooms(root!).ToArray();
        var eligibility = ValidateEligibility(game, dataDir!, exePath, rooms);
        var counts = new ExecutionCounts(rooms.Length, 0, 0, 0, 0);
        var failures = new List<string>();
        var fingerprints = new List<(string Name, long Length, string Sha256)>();
        int parsedCleanlyTrue = 0, parsedCleanlyFalse = 0;

        foreach (var roomRef in rooms)
        {
            string source = PristineSource(dataDir!, roomRef.Path);
            string name = Path.GetFileName(roomRef.Path);
            try
            {
                byte[] original = File.ReadAllBytes(source);
                byte[] written;
                if (game == "dc1")
                {
                    var parsed = RoomFile.Read(roomRef.Stage, roomRef.Room, original);
                    if (parsed.ParsedCleanly) parsedCleanlyTrue++; else parsedCleanlyFalse++;
                    written = parsed.Write();
                    Assert.Equal(original, written);
                }
                else
                {
                    var parsed = Dc2RoomFile.Read(roomRef.Stage, roomRef.Room, original);
                    Assert.Equal(original, parsed.Write());
                    var package = GianPackage.TryParse(original);
                    Assert.NotNull(package);
                    Assert.True(package!.IsDc2, $"{name}: not a DC2 Gian package");
                    int entry = package.Entries.Count - 1;
                    var payload = original.AsSpan(package.Entries[entry].PayloadOffset,
                        checked((int)package.Entries[entry].DeclaredSize));
                    written = PackageRepacker.ReplaceEntryDc2(original, entry, payload);
                    AssertCanonicalPackageEqual(original, written, name);
                }

                ExerciseTempInstallRestore(name, original, written);
                counts = counts with { Executed = counts.Executed + 1, Passed = counts.Passed + 1 };
                fingerprints.Add((name, original.LongLength, Hash(original)));
            }
            catch (Exception ex)
            {
                counts = counts with { Executed = counts.Executed + 1, Failed = counts.Failed + 1 };
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        string? receiptRoot = Environment.GetEnvironmentVariable("DINORAND_REAL_INSTALL_RECEIPT_DIR");
        Assert.False(string.IsNullOrWhiteSpace(receiptRoot), "required mode needs DINORAND_REAL_INSTALL_RECEIPT_DIR");
        Directory.CreateDirectory(receiptRoot!);
        string inputHash = AggregateFingerprint(fingerprints);
        File.WriteAllText(Path.Combine(receiptRoot!, $"{game}-required-install.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            game,
            evidence = EvidenceBinding.Create(),
            eligibility,
            counts,
            inputFingerprint = new
            {
                sha256 = inputHash,
                files = fingerprints.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new { name = f.Name, length = f.Length, sha256 = f.Sha256 }).ToArray(),
            },
            runtimeFingerprint = RuntimeFingerprint.Create(),
            operations = game == "dc1"
                ? "RoomFile.Read/Write exact identity + temp-copy GameInstaller.Install/Restore"
                : "Dc2RoomFile.Read/Write + PackageRepacker.ReplaceEntryDc2 canonical compare + temp-copy GameInstaller.Install/Restore",
            parserDiagnostics = game == "dc1" ? new
            {
                parsedCleanlyTrue,
                parsedCleanlyFalse,
                note = "ParsedCleanly is diagnostic current behavior, not an eligibility assertion; false uses the shipped byte-preserving fallback and still must pass Read/Write exact identity.",
            } : null,
            failures,
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(counts.Discovered > 0, $"{game}: no cases discovered");
        Assert.Equal(counts.Discovered, counts.Executed);
        Assert.Equal(0, counts.Failed);
        Assert.Equal(0, counts.Skipped);
        Assert.Equal(counts.Executed, counts.Passed);
    }

    private static string PristineSource(string dataDir, string livePath)
    {
        string backup = Path.Combine(dataDir, GameInstaller.BackupDirName, Path.GetFileName(livePath));
        return File.Exists(backup) ? backup : livePath;
    }

    private static EligibilityProof ValidateEligibility(string game, string dataDir, string liveExe,
        IReadOnlyList<RoomFileRef> rooms)
    {
        var manifest = GameInstaller.ReadManifest(dataDir);
        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.OriginalHashes);
        Assert.NotEmpty(manifest.OriginalHashes!);
        var audit = GameInstaller.VerifyBackups(dataDir);
        Assert.DoesNotContain(audit, result => result.Status is BackupVerifyStatus.Poisoned
            or BackupVerifyStatus.Suspect
            || result.Status == BackupVerifyStatus.LiveMissing
               && !(game == "dc1" && result.Name.Equals(GameInstaller.ExeName, StringComparison.OrdinalIgnoreCase)));

        string backupDir = Path.Combine(dataDir, GameInstaller.BackupDirName);
        int verifiedBackups = 0, manifestUntouched = 0;
        foreach (var room in rooms)
        {
            string name = Path.GetFileName(room.Path);
            var pair = manifest.OriginalHashes!.FirstOrDefault(kv =>
                kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            string backup = Path.Combine(backupDir, name);
            if (!string.IsNullOrEmpty(pair.Key))
            {
                Assert.True(File.Exists(backup), $"manifest-declared pristine backup missing: {name}");
                Assert.Equal(pair.Value, Hash(File.ReadAllBytes(backup)), ignoreCase: true);
                verifiedBackups++;
            }
            else
            {
                Assert.False(File.Exists(backup), $"untracked room backup has no manifest pristine hash: {name}");
                manifestUntouched++;
            }
        }

        string exeEvidence;
        if (game == "dc1")
        {
            string pristineExe = Path.Combine(backupDir, GameInstaller.ExeName);
            Assert.True(File.Exists(pristineExe), "DC1 required mode needs the repository installer's pristine DINO.exe backup");
            byte[] bytes = File.ReadAllBytes(pristineExe);
            ValidatePeAndProtection(bytes, GameInstaller.ExeName);
            Assert.Equal(ExePatcher.Dc1StockExeLength, bytes.Length);
            Dc1Edition edition = Dc1EditionDetector.Detect(dataDir);
            if (edition == Dc1Edition.Unknown && IsGogInlineData(dataDir))
                edition = Dc1Edition.GogInlineText;
            Assert.True(edition is Dc1Edition.RebirthEnglish or Dc1Edition.GogInlineText,
                "DC1 fixture is neither repository-recognized REbirth English nor GOG inline-text data");
            exeEvidence = $"valid PE + not protected + stock DC1 length + repository-recognized {edition} data + installer backup";
        }
        else
        {
            byte[] bytes = File.ReadAllBytes(liveExe);
            ValidateDc2Executable(bytes);
            string wrapper = Path.Combine(Path.GetDirectoryName(liveExe)!, "ddraw.dll");
            Assert.True(File.Exists(wrapper), "DC2 Classic REbirth wrapper is missing");
            byte[] wrapperBytes = File.ReadAllBytes(wrapper);
            if (!Dc2DdrawTrailPatch.IsRecognizedPristine(wrapperBytes))
            {
                Assert.True(Dc2DdrawTrailPatch.IsApplied(wrapperBytes),
                    "DC2 wrapper is neither repository-recognized pristine nor repository-patched");
                string pristineWrapper = wrapper + ".bak";
                Assert.True(File.Exists(pristineWrapper), "repository-patched DC2 wrapper has no pristine sibling backup");
                Assert.True(Dc2DdrawTrailPatch.IsRecognizedPristine(File.ReadAllBytes(pristineWrapper)),
                    "DC2 wrapper sibling backup is not the exact repository-recognized pristine build");
            }
            exeEvidence = "valid PE + not protected + Dc2WpGatePatch pristine sentinel + DC2 wrapper pristine sentinel or applied-with-recognized-pristine-sibling";
        }

        return new EligibilityProof(game == "dc1" ? "GOG DRM-free DC1" : "GOG-owned DC2 via recognized Classic REbirth executable/wrapper",
            exeEvidence, manifest.OriginalHashes.Count, verifiedBackups, manifestUntouched, audit.Count);
    }

    private static void ValidateDc2Executable(byte[] bytes)
    {
        ValidatePeAndProtection(bytes, "Dino2.exe");
        if (!Dc2WpGatePatch.IsRecognizedPristine(bytes))
            throw new InvalidDataException("Dino2.exe is not the exact repository-recognized pristine build");
    }

    private static void ValidatePeAndProtection(byte[] bytes, string name)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.PEHeader is null)
                throw new InvalidDataException($"{name} is not a valid PE image");
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException($"{name} is not a valid PE image", ex);
        }
        var protection = ExeProtection.Inspect(bytes, name);
        if (protection.IsProtected) throw new InvalidDataException(protection.Detail);
    }

    private static bool IsGogInlineData(string dataDir)
    {
        string temp = Directory.CreateTempSubdirectory("dinorand-w1-gog-proof-").FullName;
        try
        {
            string wanted = Dc1PuzzleCodeSync.Family[0].DocFile!;
            string backupDir = Path.Combine(dataDir, GameInstaller.BackupDirName);
            string? source = Directory.Exists(backupDir)
                ? Directory.EnumerateFiles(backupDir).FirstOrDefault(path =>
                    Path.GetFileName(path).Equals(wanted, StringComparison.OrdinalIgnoreCase))
                : null;
            source ??= Directory.EnumerateFiles(dataDir).FirstOrDefault(path =>
                Path.GetFileName(path).Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (source is null) return false;
            File.Copy(source, Path.Combine(temp, wanted));
            return Dc1EditionDetector.HasInlineCodeRun(temp);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static void ExerciseTempInstallRestore(string name, byte[] original, byte[] overlay)
    {
        string root = Directory.CreateTempSubdirectory("dinorand-w1-install-").FullName;
        try
        {
            string data = Directory.CreateDirectory(Path.Combine(root, "Data")).FullName;
            string mod = Directory.CreateDirectory(Path.Combine(root, "mod")).FullName;
            string target = Path.Combine(data, name);
            File.WriteAllBytes(target, original);
            File.WriteAllBytes(Path.Combine(mod, name), overlay);
            var installed = GameInstaller.Install(data, mod, "w1-required", new[] { name });
            Assert.Equal(1, installed.Overlaid);
            Assert.Equal(overlay, File.ReadAllBytes(target));
            var restored = GameInstaller.Restore(data);
            Assert.Equal(1, restored.Restored);
            Assert.Equal(original, File.ReadAllBytes(target));
            Assert.True(Directory.Exists(Path.Combine(data, GameInstaller.BackupDirName)));
            Assert.False(GameInstaller.ReadManifest(data)!.Applied);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(data, GameInstaller.BackupDirName, name)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void AssertCanonicalPackageEqual(byte[] expected, byte[] actual, string name)
    {
        var a = GianPackage.TryParse(expected);
        var b = GianPackage.TryParse(actual);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.EntrySize, b!.EntrySize);
        Assert.Equal(a.Entries.Count, b.Entries.Count);
        for (int i = 0; i < a.Entries.Count; i++)
        {
            Assert.Equal(a.Entries[i].Type, b.Entries[i].Type);
            Assert.Equal(a.Entries[i].DeclaredSize, b.Entries[i].DeclaredSize);
            var pa = expected.AsSpan(a.Entries[i].PayloadOffset, checked((int)a.Entries[i].DeclaredSize));
            var pb = actual.AsSpan(b.Entries[i].PayloadOffset, checked((int)b.Entries[i].DeclaredSize));
            Assert.True(pa.SequenceEqual(pb), $"{name}: canonical payload {i} drifted");
        }
    }

    private static string AggregateFingerprint(IEnumerable<(string Name, long Length, string Sha256)> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Name.ToLowerInvariant()));
            hash.AppendData(BitConverter.GetBytes(file.Length));
            hash.AppendData(Convert.FromHexString(file.Sha256));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed record ExecutionCounts(int Discovered, int Executed, int Passed, int Failed, int Skipped);
    private sealed record EligibilityProof(string Distribution, string ExecutableEvidence, int ManifestHashEntries,
        int VerifiedRoomBackups, int ManifestUntouchedRooms, int BackupAuditEntries);
}
