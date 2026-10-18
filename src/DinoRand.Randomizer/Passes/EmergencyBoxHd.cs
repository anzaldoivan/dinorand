using System.Text.Json;
using DinoRand.FileFormats.Graphics;
using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// DC1 emergency boxes in the Classic REbirth HD texture pack (<c>HD/&lt;ROOM&gt;/pNN.png</c>), which the game draws
/// instead of the room's own texture page, so the CLUT recolour of <see cref="EmergencyBoxRandomizer"/> does not show
/// there (EMERGENCY-BOX-DATA.md "HD texture pack"). A box front is one square cell of 64 texels: 500 pixels on a 2000
/// page, 256 on a 1024 page. The pack draws every box of a colour with the same art, so a box changes colour by copying
/// a cell of the target colour over it.
///
/// <para>Where each box's cell is comes from <c>data/dc1/emergency-box-hd.json</c>, one row per box record (base rooms
/// and their stage A/B/C copies), found by <c>EmergencyBoxHdCensusTests</c> on a real install. A row with no page
/// has no HD cell (ST60D's box 15 has its own low-resolution art).</para>
/// </summary>
public static class EmergencyBoxHd
{
    /// <summary>Embedded-resource logical name (see DinoRand.Randomizer.csproj).</summary>
    public const string ResourceName = "DinoRand.Randomizer.Data.dc1.emergency-box-hd.json";

    /// <summary>One box record's cell. <see cref="BoxId"/> is the vanilla id at the location
    /// (<c>EmergencyBoxRecord.OriginalBoxId</c>); <see cref="X"/>, <see cref="Y"/> and <see cref="Size"/> are pixels of a
    /// <see cref="PageSize"/>² page.</summary>
    public sealed record Cell(string Folder, int BoxId, string Colour, string? Page, int PageSize, int X, int Y, int Size);

    /// <summary>Two cells are the same art when <see cref="Score"/> is below this. Against the 2000-pixel templates, the
    /// boxes on 2000 pages score 3–7 and the two on 1024 pages, a separate upscale, 16 (ST610) and 17 (ST612); a box of
    /// another colour scores 41 or more.</summary>
    public const double MatchThreshold = 20;

    public static IReadOnlyList<Cell> Cells { get; } = Load();

    private static IReadOnlyList<Cell> Load()
    {
        using var s = typeof(EmergencyBoxHd).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' missing: data/dc1/emergency-box-hd.json must ship in the assembly");
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.GetProperty("boxes").Deserialize<Cell[]>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>The HD pages to write and the changed boxes each way: <see cref="Hd"/> got the target colour's HD art,
    /// <see cref="StandardOnly"/> only the CLUT recolour (no cell, page missing or unreadable, or a cell that does not
    /// match its vanilla art, which means another pack version).</summary>
    public sealed record HdPlan(IReadOnlyList<(string RelPath, byte[] Png)> Files, IReadOnlyList<string> Hd,
        IReadOnlyList<string> StandardOnly);

    /// <summary>The page of <paramref name="cell"/>, install-relative.</summary>
    public static string RelPath(Cell cell) => $"HD/{cell.Folder}/{cell.Page}.png";

    /// <summary>
    /// Copy the HD art of each box's new colour over the boxes whose colour changes. <paramref name="moves"/> pairs a
    /// location's vanilla id with its new id; every record of the location (base room and stage copy) changes alike.
    /// <paramref name="readPristine"/> returns a page's vanilla bytes by <see cref="RelPath"/>, or null.
    ///
    /// <para>The donor of a colour is the first cell of that colour whose page reads, on a page of the target's size
    /// when there is one. Each target page is decoded once, all its cells are copied in, and it is encoded once; donors
    /// always come from the pristine decode, so two boxes on one page (ST602 <c>p07</c>) can take each other's colour.
    /// A target or donor cell is used only if it matches another cell of its colour (<see cref="MatchThreshold"/>), so
    /// another pack version leaves the page alone.</para>
    /// </summary>
    public static HdPlan Plan(IReadOnlyList<(int LocationId, int NewId)> moves, Func<string, byte[]?> readPristine)
        => Plan(moves, readPristine, Cells);

    internal static HdPlan Plan(IReadOnlyList<(int LocationId, int NewId)> moves, Func<string, byte[]?> readPristine,
        IReadOnlyList<Cell> table)
    {
        var decoded = new Dictionary<string, PngImage?>(StringComparer.OrdinalIgnoreCase);
        PngImage? PageOf(Cell c)
        {
            var rel = RelPath(c);
            if (!decoded.TryGetValue(rel, out var page))
                decoded[rel] = page = readPristine(rel) is { } bytes ? Png.Decode(bytes) : null;
            return page is not null && page.Width == c.PageSize && page.Height == c.PageSize ? page : null;
        }
        IEnumerable<Cell> OfColour(EmergencyBoxColour colour)
            => table.Where(c => c.Page is not null && EmergencyBoxRecord.ColourOf(c.BoxId) == colour);
        // A cell is the expected art when it matches a cell of its colour elsewhere (a stage copy's cell is the same
        // place, so it does not count).
        var verified = new Dictionary<Cell, bool>();
        bool Verified(Cell c)
        {
            if (verified.TryGetValue(c, out var ok)) return ok;
            var page = PageOf(c);
            return verified[c] = page is not null && OfColour(EmergencyBoxRecord.ColourOf(c.BoxId))
                .Where(o => (o.Page, o.X, o.Y) != (c.Page, c.X, c.Y))
                .Any(o => PageOf(o) is { } other
                          && Score(page, c.X, c.Y, c.Size, other, o.X, o.Y, o.Size) < MatchThreshold);
        }
        Cell? Donor(EmergencyBoxColour colour, int pageSize)
            => OfColour(colour).OrderBy(c => c.PageSize == pageSize ? 0 : 1).FirstOrDefault(Verified);

        var targets = moves
            .Where(m => EmergencyBoxRecord.ColourOf(m.LocationId) != EmergencyBoxRecord.ColourOf(m.NewId))
            .SelectMany(m => table.Where(c => c.BoxId == m.LocationId)
                .Select(c => (Cell: c, To: EmergencyBoxRecord.ColourOf(m.NewId))))
            .ToList();
        var files = new List<(string, byte[])>();
        var hd = new List<string>();
        var standard = targets.Where(t => t.Cell.Page is null).Select(t => $"{t.Cell.Folder} box {t.Cell.BoxId} (no HD cell)").ToList();
        foreach (var group in targets.Where(t => t.Cell.Page is not null).GroupBy(t => RelPath(t.Cell), StringComparer.OrdinalIgnoreCase))
        {
            var pristine = PageOf(group.First().Cell);
            var edit = pristine is null ? null : pristine with { Pixels = (byte[])pristine.Pixels.Clone() };
            int copied = 0;
            foreach (var (cell, to) in group)
            {
                var label = $"{cell.Folder} box {cell.BoxId}";
                if (pristine is null)
                {
                    standard.Add($"{label} ({group.Key} missing or unreadable)");
                    continue;
                }
                if (!Verified(cell))
                {
                    standard.Add($"{label} ({group.Key} is not the expected art)");
                    continue;
                }
                if (Donor(to, cell.PageSize) is not { } donor)
                {
                    standard.Add($"{label} (no HD art of colour {to})");
                    continue;
                }
                Png.CopyCell(PageOf(donor)!, donor.X, donor.Y, donor.Size, edit!, cell.X, cell.Y, cell.Size,
                    border: cell.PageSize >= 2000 ? 4 : 2);
                hd.Add(label);
                copied++;
            }
            if (copied > 0) files.Add((group.Key, Png.Encode(edit!)));
        }
        return new HdPlan(files, hd, standard);
    }

    /// <summary>Mean absolute RGB difference (0–255) between two cells, sampled on a <paramref name="samples"/>²
    /// grid that skips the outer 2% (where neighbouring cells bleed in). The cells may differ in size.</summary>
    public static double Score(PngImage a, int ax, int ay, int aSize, PngImage b, int bx, int by, int bSize, int samples = 125)
    {
        long sum = 0;
        for (int j = 0; j < samples; j++)
            for (int i = 0; i < samples; i++)
            {
                double u = 0.02 + 0.96 * (i + 0.5) / samples, v = 0.02 + 0.96 * (j + 0.5) / samples;
                int pa = ((ay + (int)(v * aSize)) * a.Width + ax + (int)(u * aSize)) * a.Channels;
                int pb = ((by + (int)(v * bSize)) * b.Width + bx + (int)(u * bSize)) * b.Channels;
                for (int k = 0; k < 3; k++) sum += Math.Abs(a.Pixels[pa + k] - b.Pixels[pb + k]);
            }
        return sum / (3.0 * samples * samples);
    }
}
