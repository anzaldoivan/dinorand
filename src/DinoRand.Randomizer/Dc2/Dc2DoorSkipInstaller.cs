using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// DC2 REbirth <c>DoorSkip</c> passthrough (CUTSCENE-SKIP-FEASIBILITY.md §5, K115). Classic
/// REbirth's door skip is a wrapper-DLL feature toggled by one ini key — <c>DoorSkip = 1</c> in the
/// <c>[DLL]</c> section of the game root's <c>config.ini</c> (the ddraw.dll layer's section, same
/// as <c>MotionTrail</c>). No exe patch and no room edit; DinoRand only flips the key the user
/// could flip by hand, so there is no backup ritual — restore is "set it back in the ini".
/// </summary>
public static class Dc2DoorSkipInstaller
{
    public const string IniName = "config.ini";

    /// <summary>Enable DoorSkip in the rebirth install at <paramref name="installDir"/>.</summary>
    public static bool Apply(string installDir, Action<string>? log = null)
    {
        var dataDir = new DinoCrisis2().GetDataDir(installDir);
        if (dataDir is null)
        {
            log?.Invoke($"[door-skip] no DC2 Data folder under {installDir}; skipped");
            return false;
        }
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(
            dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))!;
        var iniPath = Path.Combine(gameRoot, IniName);
        if (!File.Exists(iniPath))
        {
            log?.Invoke($"[door-skip] {IniName} not found at {gameRoot}; skipped (not a REbirth layout?)");
            return false;
        }

        string text = File.ReadAllText(iniPath);
        string next = ApplyToIni(text);
        if (next != text)
        {
            File.WriteAllText(iniPath, next);
            log?.Invoke("[door-skip] DoorSkip = 1 written to config.ini [DLL]");
        }
        else
        {
            log?.Invoke("[door-skip] DoorSkip already enabled");
        }
        return true;
    }

    /// <summary>
    /// Pure ini rewrite: sets <c>DoorSkip = 1</c> in the <c>[DLL]</c> section, preserving every
    /// other line byte-for-byte. Adds the key at the top of <c>[DLL]</c> when missing; idempotent.
    /// </summary>
    public static string ApplyToIni(string ini)
    {
        string nl = ini.Contains("\r\n") ? "\r\n" : "\n";
        var lines = ini.Split('\n');
        bool inDll = false, replaced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
                inDll = trimmed.Equals("[DLL]", StringComparison.OrdinalIgnoreCase);
            else if (inDll && trimmed.StartsWith("DoorSkip", StringComparison.OrdinalIgnoreCase))
            {
                string ending = lines[i].EndsWith('\r') ? "\r" : "";
                lines[i] = "DoorSkip = 1" + ending;
                replaced = true;
            }
        }
        if (replaced) return string.Join('\n', lines);

        // no DoorSkip line: insert right after the [DLL] header (or append a [DLL] section).
        var list = lines.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Trim().Equals("[DLL]", StringComparison.OrdinalIgnoreCase))
            {
                string ending = list[i].EndsWith('\r') ? "\r" : "";
                list.Insert(i + 1, "DoorSkip = 1" + ending);
                return string.Join('\n', list);
            }
        }
        return ini + (ini.EndsWith('\n') ? "" : nl) + "[DLL]" + nl + "DoorSkip = 1" + nl;
    }
}
