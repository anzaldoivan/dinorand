using DinoRand.FileFormats.Graphics;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// Draws "DINORAND V&lt;version&gt; / SEED &lt;n&gt;" into DC1's title screen
/// (<c>Data\t_image.imd</c>, docs/decisions/cross/SEED-WATERMARK-PLAN.md — the BioRand
/// title-background mechanism transposed). Emits the edited TIM as a loose install file;
/// <see cref="GameInstaller"/> overlays it with a pristine backup, so <c>--restore</c> reverses it.
///
/// <para>Reads the <b>pristine</b> image: a previous install leaves the live file watermarked,
/// so the loose backup (when present) is the source — watermarks never stack. Cosmetic pass:
/// any missing/unrecognized file logs and skips, never fails the run.</para>
/// </summary>
public sealed class TitleWatermarkPass : IRandomizationPass
{
    public const string TitleImageName = "t_image.imd";

    public string Name => "Title watermark";

    public bool IsEnabled(RandomizerConfig config) => config.TitleWatermark;

    public void Apply(RandomizationContext context)
    {
        if (context.InstallDir is not { } install) return;
        var dataDir = context.Game.GetDataDir(install);
        if (dataDir is null || !Directory.Exists(dataDir)) return;

        // Case-insensitive lookup (the st502 lesson: never glob case-sensitively on Linux/WSL).
        var livePath = Directory.EnumerateFiles(dataDir).FirstOrDefault(
            p => string.Equals(Path.GetFileName(p), TitleImageName, StringComparison.OrdinalIgnoreCase));
        if (livePath is null)
        {
            context.Log($"[watermark] {TitleImageName} not found under {dataDir} — skipped");
            return;
        }

        var fileName = Path.GetFileName(livePath);
        var pristine = Path.Combine(dataDir, GameInstaller.BackupDirName,
                                    GameInstaller.LooseBackupSubdir, "Data", fileName);
        var source = File.Exists(pristine) ? pristine : livePath;

        try
        {
            // Line 2 = the SAME shareable identity the GUI seed box and the spoiler log show
            // (SeedString.Encode == AppSeed.ToString(): seed + config), so a screenshot is re-typeable.
            var seedString = Spoiler.SeedString.Encode(context.Seed, context.Config);
            var bytes = Dc1TitleWatermark.Apply(File.ReadAllBytes(source),
                $"DINORAND V{Spoiler.SpoilerLogBuilder.AppVersion()}",
                seedString);
            context.AddLooseFile("Data/" + fileName, bytes);
            context.Log($"[watermark] {seedString} → Data/{fileName}");
        }
        catch (InvalidDataException e)
        {
            context.Log($"[watermark] {fileName} not the expected title TIM ({e.Message}) — skipped");
        }
    }
}
