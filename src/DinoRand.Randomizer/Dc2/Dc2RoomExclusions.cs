using System.Text.Json;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// DC2 rooms the randomizer must leave alone because their enemies are part of a scripted <b>set-piece</b>
/// or <b>cutscene</b> — swapping the species (or count/position) would break the sequence. Keyed by
/// <c>st_id</c> (the same string <see cref="Dc2SpawnGraph.RoomKey"/> and <c>data/dc2/spawn-graph.json</c>
/// use), so the enemy pass can skip a room with one lookup.
///
/// <para>Two sources, merged by <see cref="IsExcluded"/>:</para>
/// <list type="bullet">
/// <item><see cref="Setpiece"/> — the hand-curated set-piece list (maintainer/playtest).</item>
/// <item><see cref="Cutscene"/> — the auto-detected, ground-truth-confirmed cutscene/set-piece rooms
/// loaded from the generated <c>data/dc2/cutscene-rooms.json</c> (<c>flagged</c> tier;
/// <c>tools/dc2_re/scan_cutscene_rooms.py</c>). Only rooms with independent confirmation (the ST407-unique
/// op-0x58 turret script, or a human ground-truth label) are in <c>flagged</c>. The scanner's much larger
/// <c>uncertain</c> tier — rooms carrying the scripted-event barrier op-0x41/0x42 but not confirmed — is
/// deliberately NOT excluded here (static opcodes cannot tell a swap-breaking cutscene from a safe scripted
/// room); it is surfaced for playtest promotion. See <c>docs/decisions/dc2/cutscene/CUTSCENE-DETECTION-PLAN.md</c>.</item>
/// </list>
/// </summary>
public static class Dc2RoomExclusions
{
    /// <summary>Set-piece room <c>st_id</c>s skipped by the enemy randomizer (hand-curated).</summary>
    public static IReadOnlySet<string> Setpiece { get; } = new HashSet<string>
    {
        "407", // ST407 — the TURRET set-piece (maintainer ID 2026-06-30): its 2 hardcoded raptors are
               // scripted into the turret sequence; a cross-species swap would break it. (Also why ST407
               // is NOT a candidate for the file-path validation — it must be excluded, not edited.)
        "905", // ST905 — Extra Crisis bonus level (detected 2026-07-04): must stay vanilla.
    };

    /// <summary>Auto-detected cutscene/set-piece room <c>st_id</c>s (the <c>flagged</c> tier of
    /// <c>data/dc2/cutscene-rooms.json</c>). Loaded once from the embedded resource.</summary>
    public static IReadOnlySet<string> Cutscene { get; } = LoadCutsceneFlagged();

    /// <summary>Embedded-resource logical name (see DinoRand.Randomizer.csproj).</summary>
    public const string CutsceneResourceName = "DinoRand.Randomizer.Data.dc2.cutscene-rooms.json";

    /// <summary>True iff <paramref name="roomKey"/> (an <c>st_id</c> like "407") is a set-piece or
    /// confirmed-cutscene room the randomizer must not touch.</summary>
    public static bool IsExcluded(string roomKey) =>
        Setpiece.Contains(roomKey) || Cutscene.Contains(roomKey);

    private static IReadOnlySet<string> LoadCutsceneFlagged()
    {
        var asm = typeof(Dc2RoomExclusions).Assembly;
        using var s = asm.GetManifestResourceStream(CutsceneResourceName);
        if (s is null) return new HashSet<string>();          // resource absent → fall back to Setpiece only
        using var doc = JsonDocument.Parse(s);
        var set = new HashSet<string>();
        if (doc.RootElement.TryGetProperty("flagged", out var flagged) &&
            flagged.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in flagged.EnumerateArray())
                if (r.GetString() is { } id) set.Add(id);
        }
        return set;
    }
}
