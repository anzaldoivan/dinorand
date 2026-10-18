using System.Text.Json;
using DinoRand.FileFormats.Graphics;

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
