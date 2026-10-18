using DinoRand.FileFormats.Graphics;
using DinoRand.Randomizer.Passes;
using Xunit;
using Cell = DinoRand.Randomizer.Passes.EmergencyBoxHd.Cell;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The HD-pack half of a Full box-id swap (<see cref="EmergencyBoxHd.Plan(IReadOnlyList{ValueTuple{int, int}}, Func{string, byte[]}, IReadOnlyList{Cell})"/>)
/// on synthetic pages: 64-pixel pages with 16-pixel cells stand in for the 2000/500 ones, and a 32-pixel page for a
/// 1024 one. Each cell is painted in its box's colour; the green ST105 cell is a one-pixel checker of two greens.
/// </summary>
public class EmergencyBoxHdTests
{
    private static readonly byte[] Red = { 200, 30, 30 }, Yellow = { 220, 200, 40 }, Green = { 40, 180, 60 },
        Green2 = { 60, 200, 80 }, Grey = { 90, 90, 90 }, Blue = { 20, 40, 220 };

    private static readonly Cell[] Table =
    {
        new("ST104", 13, "yellow", "p06", 64, 0, 16, 16),
        new("ST105", 8, "green", "p05", 64, 0, 0, 16),
        new("ST113", 0, "red", "p21", 64, 48, 48, 16),
        new("ST301", 9, "green", "p23", 64, 16, 0, 16),
        new("ST602", 5, "red", "p07", 64, 32, 16, 16),
        new("ST602", 14, "yellow", "p07", 64, 16, 16, 16),
        new("ST60D", 15, "yellow", null, 0, 0, 0, 0),
        new("ST610", 7, "red", "p21", 32, 8, 8, 8),
        new("STC02", 5, "red", "p07", 64, 32, 16, 16),
        new("STC02", 14, "yellow", "p07", 64, 16, 16, 16),
    };

    private static byte[] ColourOf(Cell c) => c.Colour switch { "red" => Red, "yellow" => Yellow, _ => Green };

    /// <summary>Every page of <see cref="Table"/>, pristine, by install-relative path.</summary>
    private static Dictionary<string, PngImage> Pages()
    {
        var pages = new Dictionary<string, PngImage>();
        foreach (var c in Table.Where(c => c.Page is not null))
        {
            var rel = EmergencyBoxHd.RelPath(c);
            if (!pages.TryGetValue(rel, out var page))
            {
                pages[rel] = page = new PngImage(c.PageSize, c.PageSize, 3, new byte[c.PageSize * c.PageSize * 3]);
                Fill(page, 0, 0, c.PageSize, (_, _) => Grey);
            }
            Fill(page, c.X, c.Y, c.Size, (x, y) => c.Folder == "ST105" && (x + y) % 2 == 1 ? Green2 : ColourOf(c));
        }
        return pages;
    }

    private static void Fill(PngImage page, int x0, int y0, int size, Func<int, int, byte[]> colour)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                colour(x, y).CopyTo(page.Pixels, ((y0 + y) * page.Width + x0 + x) * 3);
    }

    private static byte[] At(PngImage page, int x, int y) => page.Pixels.AsSpan((y * page.Width + x) * 3, 3).ToArray();

    private static (EmergencyBoxHd.HdPlan Plan, Dictionary<string, PngImage> Out, List<string> Reads) Run(
        Dictionary<string, PngImage> pages, params (int, int)[] moves)
    {
        var reads = new List<string>();
        var plan = EmergencyBoxHd.Plan(moves, rel =>
        {
            reads.Add(rel);
            return pages.TryGetValue(rel, out var p) ? Png.Encode(p) : null;
        }, Table);
        return (plan, plan.Files.ToDictionary(f => f.RelPath, f => Png.Decode(f.Png)!), reads);
    }

    /// <summary>The cell's inside (border 2 at these sizes) is all <paramref name="inside"/>, its frame unchanged.</summary>
    private static void AssertCell(PngImage page, Cell c, byte[] inside, byte[] frame)
    {
        for (int y = 0; y < c.Size; y++)
            for (int x = 0; x < c.Size; x++)
            {
                bool inner = x >= 2 && y >= 2 && x < c.Size - 2 && y < c.Size - 2;
                Assert.Equal(inner ? inside : frame, At(page, c.X + x, c.Y + y));
            }
    }

    private static Cell Row(string folder, int boxId) => Table.Single(c => c.Folder == folder && c.BoxId == boxId);

    [Fact]
    public void TwoCellsOnOnePage_AreOneWrite_AndCopiesFollowTheirBaseRoom()
    {
        var (plan, pages, _) = Run(Pages(), (5, 13), (14, 10)); // red → yellow, yellow → green
        Assert.Equal(new[] { "HD/ST602/p07.png", "HD/STC02/p07.png" }, plan.Files.Select(f => f.RelPath).OrderBy(p => p));
        foreach (var folder in new[] { "ST602", "STC02" })
        {
            var page = pages[$"HD/{folder}/p07.png"];
            AssertCell(page, Row(folder, 5), Yellow, Red);
            Assert.Equal(Green, At(page, 16 + 2, 16 + 2));
            Assert.Equal(Green2, At(page, 16 + 3, 16 + 2));
        }
        Assert.Equal(4, plan.Hd.Count);
        Assert.Empty(plan.StandardOnly);
    }

    [Fact]
    public void TwoBoxesSwappingColours_ExchangeTheirCells_FromThePristinePage()
    {
        var (_, pages, _) = Run(Pages(), (5, 14), (14, 5));
        var page = pages["HD/ST602/p07.png"];
        AssertCell(page, Row("ST602", 5), Yellow, Red);
        AssertCell(page, Row("ST602", 14), Red, Yellow);
    }

    [Fact]
    public void ASmallerTarget_IsResampledFromTheLargerDonor()
    {
        var (_, pages, _) = Run(Pages(), (7, 9)); // ST610 red → green, donor ST105 at twice the size
        AssertCell(pages["HD/ST610/p21.png"], Row("ST610", 7), new byte[] { 50, 190, 70 }, Red);
    }

    [Fact]
    public void ACellThatIsNotTheVanillaArt_IsLeftAlone()
    {
        var pages = Pages();
        var c = Row("ST113", 0);
        Fill(pages[EmergencyBoxHd.RelPath(c)], c.X, c.Y, c.Size, (_, _) => Blue);
        var (plan, _, _) = Run(pages, (0, 9));
        Assert.Empty(plan.Files);
        Assert.Contains(plan.StandardOnly, s => s.StartsWith("ST113 box 0 ") && s.Contains("not the expected art"));
    }

    [Fact]
    public void AMissingPage_OrABoxWithNoCell_GetsOnlyTheStandardRecolour()
    {
        var pages = Pages();
        pages.Remove("HD/ST113/p21.png");
        var (plan, _, _) = Run(pages, (0, 9), (15, 1));
        Assert.Empty(plan.Files);
        Assert.Equal(2, plan.StandardOnly.Count);
        Assert.Contains(plan.StandardOnly, s => s.StartsWith("ST60D box 15 (no HD cell)"));
    }

    [Fact]
    public void SameColourMoves_ReadNothing()
    {
        var (plan, _, reads) = Run(Pages(), (0, 5), (5, 0), (9, 8), (8, 9));
        Assert.Empty(plan.Files);
        Assert.Empty(reads);
    }
}
