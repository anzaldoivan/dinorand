using DinoRand.FileFormats.Graphics;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Dc2.Passes;

/// <summary>
/// Draws "DINORAND V&lt;version&gt; / SEED &lt;n&gt;" into DC2's two static title backgrounds —
/// the full-screen LZSS0 page inside <c>TITLE.DAT</c> and <c>TITLE2.DAT</c> (K109;
/// docs/decisions/cross/SEED-WATERMARK-PLAN.md). Emits through the sink, so the installer's
/// backup contract applies and <c>--restore</c> reverses it.
///
/// <para>Reads the <b>pristine</b> container (installer backup, then <c>.bak</c> sibling, then
/// the live file — the <c>Dc2PlayerModelSwap</c> resolution order) so re-rolls never stack
/// watermarks. Cosmetic pass: missing/unrecognized files log and skip, never fail the run.</para>
/// </summary>
public sealed class Dc2TitleWatermarkPass : IDc2RandomizationPass
{
    private static readonly string[] TitleFiles = { "TITLE.DAT", "TITLE2.DAT" };

    public string Name => "Title watermark";

    public bool IsEnabled(RandomizerConfig config) => config.TitleWatermark;

    public void Apply(Dc2RandomizationContext context)
    {
        if (context.DataDir is not { } dataDir || !Directory.Exists(dataDir)) return;

        foreach (var name in TitleFiles)
        {
            // Case-insensitive lookup (the st502 lesson).
            var livePath = Directory.EnumerateFiles(dataDir).FirstOrDefault(
                p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
            if (livePath is null)
            {
                context.Log($"[watermark] {name} not found under {dataDir} — skipped");
                continue;
            }

            var fileName = Path.GetFileName(livePath);
            var installerBackup = Path.Combine(dataDir, GameInstaller.BackupDirName, fileName);
            var siblingBackup = livePath + Dc2BackupSwapSink.BackupSuffix;
            var source = File.Exists(installerBackup) ? installerBackup
                       : File.Exists(siblingBackup) ? siblingBackup
                       : livePath;

            try
            {
                // Line 2 = the SAME shareable identity the GUI seed box and the spoiler log show
                // (SeedString.Encode == AppSeed.ToString(): seed + config), so a screenshot is re-typeable.
                var seedString = Spoiler.SeedString.Encode(context.Seed, context.Config);
                var bytes = Dc2TitleWatermark.Apply(File.ReadAllBytes(source),
                    $"DINORAND V{Spoiler.SpoilerLogBuilder.AppVersion()}",
                    seedString);
                context.Sink.EmitFile(fileName, bytes);
                context.Log($"[watermark] {seedString} → {fileName}");
            }
            catch (InvalidDataException e)
            {
                context.Log($"[watermark] {fileName} not the expected title package ({e.Message}) — skipped");
            }
        }
    }
}
