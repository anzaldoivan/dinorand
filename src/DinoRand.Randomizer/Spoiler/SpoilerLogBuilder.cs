using System.Collections;
using System.Reflection;
using System.Text;

namespace DinoRand.Randomizer.Spoiler;

/// <summary>The bug-report identity of one run (docs/decisions/cross/SPOILER-LOG-PLAN.md §5): everything needed
/// to reproduce and triage a seed, with no room-level spoilers. The timestamp is caller-supplied
/// so the builder stays pure (same document ⇒ same markdown).</summary>
public sealed record SpoilerDebugInfo(
    string SeedString,
    int SeedValue,
    string GameId,
    string AppVersion,
    string GeneratedUtc,
    IReadOnlyList<string> ConfigDump,
    IReadOnlyList<string> PassLog,
    IReadOnlyList<string> OutputFiles);

/// <summary>Pure data for one spoiler log: the debug identity + the collector's sections.</summary>
public sealed record SpoilerDocument(SpoilerDebugInfo Debug, IReadOnlyList<SpoilerSection> Sections);

/// <summary>
/// Renders a <see cref="SpoilerDocument"/> to markdown — pure, file-free, deterministic
/// (docs/decisions/cross/SPOILER-LOG-PLAN.md §4). Layout: debug block FIRST (safe to open/paste for a bug
/// report), then <see cref="SpoilerMarker"/>, then one section per pass that recorded anything —
/// a disabled pass records nothing and is simply absent (dynamic tables).
/// </summary>
public static class SpoilerLogBuilder
{
    /// <summary>The legacy fixed spoiler name removed from a reused output directory.</summary>
    public const string LegacyFileName = "SPOILER.md";

    /// <summary>Build the root-level spoiler file name for the canonical encoded seed string.</summary>
    public static string FileNameFor(string seedString) => $"{seedString}_spoiler.md";

    /// <summary>Whether <paramref name="fileName"/> matches the generated per-seed spoiler convention.</summary>
    public static bool IsGeneratedFileName(string fileName) =>
        fileName.StartsWith("DINO-", StringComparison.OrdinalIgnoreCase)
        && fileName.EndsWith("_spoiler.md", StringComparison.OrdinalIgnoreCase)
        && fileName.Length > "DINO-".Length + "_spoiler.md".Length;

    /// <summary>Remove only root-level generated spoiler files and the legacy fixed name.</summary>
    public static int RemoveStaleFiles(string outputDir)
    {
        if (!Directory.Exists(outputDir)) return 0;

        int removed = 0;
        foreach (var path in Directory.EnumerateFiles(outputDir, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.Equals(LegacyFileName, StringComparison.OrdinalIgnoreCase)
                && !IsGeneratedFileName(fileName))
                continue;

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException) { }
        }
        return removed;
    }

    /// <summary>The unmissable boundary between the shareable debug block and the spoilers.</summary>
    public const string SpoilerMarker = "# ⚠ SPOILERS BELOW";

    public static string Build(SpoilerDocument doc)
    {
        var sb = new StringBuilder();
        var d = doc.Debug;

        sb.AppendLine($"# DinoRand seed report — {d.GameId}");
        sb.AppendLine();
        sb.AppendLine("## Debug info (no spoilers — safe to paste in a bug report)");
        sb.AppendLine();
        sb.AppendLine($"- Seed string: `{d.SeedString}` (paste into DinoRand to reproduce this run)");
        sb.AppendLine($"- Seed: {d.SeedValue}");
        sb.AppendLine($"- Game: {d.GameId}");
        sb.AppendLine($"- DinoRand version: {d.AppVersion}");
        sb.AppendLine($"- Generated (UTC): {d.GeneratedUtc}");
        if (doc.Sections.Count > 0)
            sb.AppendLine($"- Changes: {string.Join("; ", doc.Sections.Select(s => $"{s.Title}: {s.Rows.Count} row(s)"))}");
        sb.AppendLine();

        sb.AppendLine("### Config");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (var line in d.ConfigDump) sb.AppendLine(line);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("### Pass log");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (var line in d.PassLog) sb.AppendLine(line);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("### Output files");
        sb.AppendLine();
        foreach (var f in d.OutputFiles) sb.AppendLine($"- {f}");
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine(SpoilerMarker);
        sb.AppendLine();

        if (doc.Sections.Count == 0)
        {
            sb.AppendLine("No changes recorded (no enabled pass changed anything).");
            return sb.ToString();
        }

        foreach (var section in doc.Sections)
        {
            sb.AppendLine($"## {section.Title}");
            sb.AppendLine();
            foreach (var note in section.Notes)
                sb.AppendLine($"- {note}");
            if (section.Notes.Count > 0) sb.AppendLine();

            if (section.Rows.Count > 0)
            {
                sb.AppendLine("| " + string.Join(" | ", section.Columns.Select(Escape)) + " |");
                sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", section.Columns.Count)));
                foreach (var row in section.Rows)
                    sb.AppendLine("| " + string.Join(" | ", row.Select(Escape)) + " |");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>Escape table-breaking characters in one cell.</summary>
    private static string Escape(string cell) => cell.Replace("|", @"\|");

    /// <summary>The DinoRand version for the debug block: the assembly's
    /// <c>InformationalVersion</c> (fed by <c>Directory.Build.props</c> VersionPrefix), with any
    /// <c>+metadata</c> suffix stripped.</summary>
    public static string AppVersion()
    {
        var info = typeof(SpoilerLogBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(SpoilerLogBuilder).Assembly.GetName().Version?.ToString() ?? "unknown";
        int plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    /// <summary>
    /// Deterministic <c>name = value</c> dump of every public <see cref="RandomizerConfig"/>
    /// property, sorted by name — reflection so a future config field appears automatically.
    /// Dictionaries/collections render sorted; a future non-scalar falls back to
    /// <c>ToString()</c>, never a throw.
    /// </summary>
    public static IReadOnlyList<string> DumpConfig(RandomizerConfig config)
    {
        var lines = new List<string>();
        foreach (var p in typeof(RandomizerConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                                  .OrderBy(p => p.Name, StringComparer.Ordinal))
            lines.Add($"{p.Name} = {Format(p.GetValue(config))}");
        return lines;
    }

    private static string Format(object? value) => value switch
    {
        null => "(null)",
        IReadOnlyDictionary<int, byte> d => string.Join(", ",
            d.OrderBy(kv => kv.Key).Select(kv => $"0x{kv.Key:X2}={kv.Value}")),
        IReadOnlyDictionary<string, string> d => string.Join(", ",
            d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")),
        string s => s,
        IEnumerable e => string.Join(", ",
            e.Cast<object?>().Select(x => Format(x)).OrderBy(x => x, StringComparer.Ordinal)),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "(null)",
    };
}
