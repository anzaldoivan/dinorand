using System.Text;
using DinoRand.FileFormats.Graphics;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;
using DinoRand.Randomizer.Passes;
using Xunit;
using Xunit.Abstractions;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Regenerates <c>data/dc1/emergency-box-hd.json</c> from a real install with the Classic REbirth HD pack and checks
/// it matches. Gated on <c>DINORAND_DC1_DIR</c> and its <c>HD</c> folder; the pack must be pristine. On a mismatch the
/// test prints the table to paste.
///
/// <para>Each box record's cell is found by searching every page of its room's HD folder for the template of its
/// vanilla colour: the three cells checked by hand on 2026-10-18 (red and yellow in ST602 <c>p07</c>, green in ST105
/// <c>p05</c>). The search steps one texel, keeps the best few places per page, then refines them ±4 pixels.</para>
/// </summary>
public class EmergencyBoxHdCensusTests
{
    private readonly ITestOutputHelper _out;
    public EmergencyBoxHdCensusTests(ITestOutputHelper output) => _out = output;

    private static readonly Dictionary<EmergencyBoxColour, (string Folder, string Page, int X, int Y)> Templates = new()
    {
        [EmergencyBoxColour.Red] = ("ST602", "p07", 1500, 1000),
        [EmergencyBoxColour.Yellow] = ("ST602", "p07", 1000, 1000),
        [EmergencyBoxColour.Green] = ("ST105", "p05", 1000, 0),
    };

    /// <summary>ST60D's box 15 has its own low-resolution art (EmergencyBoxTexture.ByBoxId), not an HD cell.</summary>
    private const int NoCellBoxId = 15;

    [Fact]
    public void RealInstall_HdCellsMatchTheTable()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return;
        var game = new DinoCrisis1();
        var dataDir = game.GetDataDir(root);
        if (dataDir is null) return;
        var hd = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataDir))!, "HD");
        if (!Directory.Exists(hd)) return;
        var folders = Directory.EnumerateDirectories(hd).ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        PngImage Read(string folder, string page) => Png.Decode(File.ReadAllBytes(Path.Combine(folders[folder]!, page + ".png")))!;
        var templates = Templates.ToDictionary(t => t.Key, t => (Page: Read(t.Value.Folder, t.Value.Page), t.Value.X, t.Value.Y));

        // A randomizer install keeps the vanilla rooms in its backup folder.
        var backupDir = Path.Combine(dataDir, GameInstaller.BackupDirName);
        var backups = Directory.Exists(backupDir)
            ? Directory.EnumerateFiles(backupDir).ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string?, string>(StringComparer.OrdinalIgnoreCase);
        byte[] Vanilla(string path) => File.ReadAllBytes(backups.GetValueOrDefault(Path.GetFileName(path)) ?? path);
        var records = game.EnumerateRooms(root)
            .SelectMany(r => (RoomFile.Read(r.Stage, r.Room, Vanilla(r.Path)).Script?.EmergencyBoxes ?? [])
                .Select(b => (Folder: $"ST{r.Stage:X}{r.Room:X2}", BoxId: (int)b.OriginalBoxId)))
            .OrderBy(x => x.Folder, StringComparer.Ordinal).ThenBy(x => x.BoxId)
            .ToList();
        Assert.Equal(26, records.Count);

        var found = new List<EmergencyBoxHd.Cell>();
        var pages = new Dictionary<string, PngImage?>();
        foreach (var (folder, boxId) in records)
        {
            var colour = EmergencyBoxRecord.ColourOf(boxId);
            var name = colour.ToString().ToLowerInvariant();
            var cell = new EmergencyBoxHd.Cell(folders.TryGetValue(folder, out var f) ? Path.GetFileName(f)! : folder,
                boxId, name, null, 0, 0, 0, 0);
            if (boxId != NoCellBoxId && f is not null)
            {
                var t = templates[colour];
                (double Score, string Page, int Size, int X, int Y)? best = null;
                foreach (var path in Directory.EnumerateFiles(f, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (!pages.TryGetValue(path, out var page)) pages[path] = page = Png.Decode(File.ReadAllBytes(path));
                    if (page is null || page.Width != page.Height) continue;
                    var hit = Search(page, t.Page, t.X, t.Y);
                    if (best is null || hit.Score < best.Value.Score)
                        best = (hit.Score, Path.GetFileNameWithoutExtension(path), page.Width, hit.X, hit.Y);
                }
                _out.WriteLine($"{folder} box {boxId} {name}: {best?.Page} ({best?.X},{best?.Y}) score {best?.Score:F2}");
                if (best is { } b && b.Score < EmergencyBoxHd.MatchThreshold)
                    cell = cell with { Page = b.Page, PageSize = b.Size, X = b.X, Y = b.Y, Size = b.Size / 4 };
            }
            found.Add(cell);
        }

        var json = ToJson(found);
        var expected = EmergencyBoxHd.Cells.ToList();
        if (!expected.SequenceEqual(found)) _out.WriteLine(json);
        Assert.Equal(expected, found);
        Assert.Equal(25, found.Count(c => c.Page is not null));
    }

    /// <summary>The best place of the template cell (500 pixels at (tx, ty) of a 2000 page) on <paramref name="page"/>.</summary>
    private static (double Score, int X, int Y) Search(PngImage page, PngImage tpl, int tx, int ty)
    {
        int size = page.Width / 4, tSize = tpl.Width / 4;
        double texel = page.Width / 256.0;
        var coarse = new List<(double Score, int X, int Y)>();
        for (int j = 0; j <= 192; j++)
            for (int i = 0; i <= 192; i++)
            {
                int x = (int)Math.Round(i * texel), y = (int)Math.Round(j * texel);
                if (x + size > page.Width || y + size > page.Height) continue;
                coarse.Add((EmergencyBoxHd.Score(tpl, tx, ty, tSize, page, x, y, size, samples: 24), x, y));
            }
        var best = (Score: double.MaxValue, X: 0, Y: 0);
        foreach (var c in coarse.OrderBy(c => c.Score).Take(3))
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = c.X + dx, y = c.Y + dy;
                    if (x < 0 || y < 0 || x + size > page.Width || y + size > page.Height) continue;
                    double s = EmergencyBoxHd.Score(tpl, tx, ty, tSize, page, x, y, size);
                    if (s < best.Score) best = (s, x, y);
                }
        return best;
    }

    private static string ToJson(IEnumerable<EmergencyBoxHd.Cell> cells)
    {
        var sb = new StringBuilder("{\n  \"boxes\": [\n");
        sb.AppendJoin(",\n", cells.Select(c =>
            $"    {{ \"folder\": \"{c.Folder}\", \"boxId\": {c.BoxId}, \"colour\": \"{c.Colour}\", \"page\": "
            + (c.Page is null ? "null" : $"\"{c.Page}\"")
            + $", \"pageSize\": {c.PageSize}, \"x\": {c.X}, \"y\": {c.Y}, \"size\": {c.Size} }}"));
        sb.Append("\n  ]\n}\n");
        return sb.ToString();
    }
}
