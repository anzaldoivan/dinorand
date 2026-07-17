using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// DC1 cutscene-shorten lever (off by default — pilot rooms not yet human-witnessed in game).
/// Rewrites every whitelisted script-authored cutscene bracket in place via
/// <see cref="CutsceneShortener"/>: side-effect ops (flag writes, item records) keep executing,
/// choreography runs are jumped over, and the native wrapper's own player save/restore makes the
/// skip pose-safe by construction (STATIC-SCD-RE cont.74; CUTSCENE-SKIP-FEASIBILITY.md §9.3).
/// Purely in-place (no relocation), so its position among the record-editing passes is free; it
/// runs before the splicing passes, whose relocation treats the new gotos as ordinary branch sites.
/// </summary>
public sealed class CutsceneShortenPass : IRandomizationPass
{
    public string Name => "Shorten cutscenes";

    public bool IsEnabled(RandomizerConfig config) => config.ShortenCutscenes;

    public void Apply(RandomizationContext context)
    {
        int rooms = 0, total = 0;
        foreach (var room in context.Rooms)
        {
            if (room.RdtBuffer.Length == 0) continue;
            int n = CutsceneShortener.Shorten(room.RdtBuffer);
            if (n > 0)
            {
                rooms++;
                total += n;
                context.Log($"[cutscenes] 0x{((room.Stage & 0xff) << 8) | (room.Room & 0xff):X4}: {n} cutscene bracket(s) shortened");
            }
        }
        context.Log($"[cutscenes] shortened {total} cutscene bracket(s) across {rooms} room(s)");
    }
}
