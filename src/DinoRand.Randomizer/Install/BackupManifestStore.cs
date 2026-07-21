using System.Security.Cryptography;
using System.Text.Json;

namespace DinoRand.Randomizer.Install;

internal static class BackupManifestStore
{
    private const string BackupDirName = GameInstaller.BackupDirName;
    private const string SiblingBackupSuffix = GameInstaller.SiblingBackupSuffix;
    private const string LooseBackupSubdir = GameInstaller.LooseBackupSubdir;
    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string GameRoot(string dataDir) => Path.GetDirectoryName(Path.GetFullPath(
        dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;

    private static void EnsureNotDrmProtected(string dataDir) => GameInstaller.EnsureNotDrmProtected(dataDir);

    internal static bool FilesEqual(string a, string b) =>
        string.Equals(HashFile(a), HashFile(b), StringComparison.OrdinalIgnoreCase);

    internal static string BackupOnce(string dataDir, string originalPath)
    {
        EnsureNotDrmProtected(dataDir); // every single-file room edit funnels through here — refuse a DRM exe
        var backupDir = Path.Combine(dataDir, BackupDirName);
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(backupDir, Path.GetFileName(originalPath));
        if (!File.Exists(backupPath))
            CapturePristine(originalPath, backupPath); // capture the pristine original once (for Restore)
        return backupPath;
    }

    internal static bool HasPriorEdit(string backupPath, string livePath)
    {
        if (!File.Exists(backupPath) || !File.Exists(livePath)) return false;
        if (new FileInfo(backupPath).Length != new FileInfo(livePath).Length) return true;
        return !File.ReadAllBytes(backupPath).AsSpan().SequenceEqual(File.ReadAllBytes(livePath));
    }

    internal static IReadOnlyList<BackupVerifyResult> VerifyBackups(string dataDir)
    {
        var results = new List<BackupVerifyResult>();
        var backupDir = Path.Combine(dataDir, BackupDirName);
        if (!Directory.Exists(backupDir))
            return results;

        var manifest = ReadManifest(dataDir);
        bool applied = manifest?.Applied == true;
        var hashes = manifest?.OriginalHashes;

        foreach (var backupPath in Directory.EnumerateFiles(backupDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backupDir, backupPath).Replace('\\', '/');
            if (string.Equals(rel, ManifestName, StringComparison.OrdinalIgnoreCase))
                continue;

            bool loose = rel.StartsWith(LooseBackupSubdir + "/", StringComparison.OrdinalIgnoreCase);
            var name = loose ? rel[(LooseBackupSubdir.Length + 1)..] : rel;
            var livePath = loose
                ? Path.Combine(GameRoot(dataDir), name.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(dataDir, name);

            if (hashes is not null && hashes.TryGetValue(name, out var expected)
                && !string.Equals(HashFile(backupPath), expected, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(name, BackupVerifyStatus.Poisoned,
                    "backup does not match the manifest's recorded pristine hash"));
                continue;
            }

            var sibling = livePath + SiblingBackupSuffix;
            if (File.Exists(sibling) && !FilesEqual(backupPath, sibling))
            {
                results.Add(new(name, BackupVerifyStatus.Poisoned,
                    $"backup disagrees with the pristine sibling {Path.GetFileName(sibling)}"));
                continue;
            }

            if (!File.Exists(livePath))
            {
                results.Add(new(name, BackupVerifyStatus.LiveMissing, "no live counterpart for this backup"));
                continue;
            }

            if (FilesEqual(backupPath, livePath))
                results.Add(new(name, BackupVerifyStatus.Ok, "backup matches the live file"));
            else if (applied)
                results.Add(new(name, BackupVerifyStatus.Installed,
                    "live file differs (a randomizer install is applied — expected)"));
            else
                results.Add(new(name, BackupVerifyStatus.Suspect,
                    "backup differs from the live file while no install is applied — "
                    + "a poisoned capture, or an un-manifested in-place edit"));
        }
        return results;
    }

    internal static void CapturePristine(string originalPath, string backupPath)
    {
        File.WriteAllBytes(backupPath, ReadPristineBytes(originalPath));
    }

    internal static byte[] ReadPristineBytes(string originalPath)
    {
        var sibling = originalPath + SiblingBackupSuffix;
        var source = File.Exists(sibling)
                     && !string.Equals(HashFile(sibling), HashFile(originalPath), StringComparison.OrdinalIgnoreCase)
            ? sibling
            : originalPath;
        return File.ReadAllBytes(source);
    }

    internal static string HashBytes(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static InstallManifest? ReadManifest(string dataDir)
    {
        var manifestPath = Path.Combine(dataDir, BackupDirName, ManifestName);
        if (!File.Exists(manifestPath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<InstallManifest>(
                File.ReadAllText(manifestPath), JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
