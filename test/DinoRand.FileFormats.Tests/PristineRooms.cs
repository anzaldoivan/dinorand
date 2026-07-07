using System;
using System.IO;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Resolves a DC2 room file's <b>pristine</b> bytes from the canonical rebirth install. The live
/// <c>Data\ST*.DAT</c> set may be a randomizer install (backups in <c>Data\.dinorand_backup\</c>) or
/// carry probe edits (<c>*.dinorand-bak</c> siblings), so ground-truth tests must prefer the backup
/// chain over the live file — the same doctrine as the toolkit's <c>TestData.Pristine</c>.
/// Resolution order: <c>.dinorand_backup\&lt;name&gt;</c> (GameInstaller's manifest-backed backup) →
/// <c>&lt;name&gt;.bak</c> → <c>&lt;name&gt;.dinorand-bak</c> → live <c>&lt;name&gt;</c>.
/// Returns null when no game install is present (tests skip).
/// </summary>
internal static class PristineRooms
{
    public static byte[]? TryLoad(string name)
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var dataDir = Path.Combine(root, "4249140_DinoCrisis2", "rebirth", "Data");
        foreach (var candidate in new[]
                 {
                     Path.Combine(dataDir, ".dinorand_backup", name),
                     Path.Combine(dataDir, name + ".bak"),
                     Path.Combine(dataDir, name + ".dinorand-bak"),
                     Path.Combine(dataDir, name),
                 })
        {
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
        }
        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "4249140_DinoCrisis2")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
