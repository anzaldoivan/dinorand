using System.Text.Json;

namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// DC1 room-exclusion tiers for the enemy passes, loaded once from the generated
/// <c>data/dc1/cutscene-rooms.json</c> (embedded resource; regenerate with
/// <c>tools/scd_re/cutscene_catalog.py --apply</c>). Room keys are 4-hex codes
/// (<c>stage&lt;&lt;8 | room</c>), parsed to ints here. Three tiers:
/// <list type="bullet">
/// <item><see cref="ScriptedEnemy"/> / <see cref="Cutscene"/> — the hand-curated exclusion lists the
/// enemy passes have always shipped (formerly hardcoded in <see cref="DinoCrisis1"/>); always
/// excluded, so moving them here changes no output byte.</item>
/// <item><see cref="Choreography"/> — the derived census (STATIC-SCD-RE cont.49/59): rooms where a
/// script sub op-0x22-binds an enemy-record slot and op-0x3a/0x5a-installs a scripted behavior on it
/// (waypoint walk + group-3 completion flag). Species swaps and even same-species (model, motion)
/// permutes can desync the choreography there (cont.58 policy) — consulted only when
/// <see cref="RandomizerConfig.Dc1CutsceneSafeEnemies"/> is on, keeping default seeds byte-identical.</item>
/// </list>
/// </summary>
public static class Dc1RoomExclusions
{
    /// <summary>Embedded-resource logical name (see DinoRand.Randomizer.csproj).</summary>
    public const string ResourceName = "DinoRand.Randomizer.Data.dc1.cutscene-rooms.json";

    /// <summary>Hand-curated scripted T-Rex set-piece rooms (the shipped exclusion list).</summary>
    public static IReadOnlySet<int> ScriptedEnemy { get; }

    /// <summary>Hand-curated choreographed-cutscene rooms (the shipped exclusion list).</summary>
    public static IReadOnlySet<int> Cutscene { get; }

    /// <summary>Derived choreography-involved rooms (the census <c>flagged</c> tier).</summary>
    public static IReadOnlySet<int> Choreography { get; }

    static Dc1RoomExclusions()
    {
        var asm = typeof(Dc1RoomExclusions).Assembly;
        using var s = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"embedded resource '{ResourceName}' missing — data/dc1/cutscene-rooms.json must ship in the assembly "
                + "(it carries the always-on scripted/cutscene exclusion lists; a silent empty fallback would change seeds)");
        using var doc = JsonDocument.Parse(s);
        ScriptedEnemy = ReadCodes(doc.RootElement, "scripted_enemy");
        Cutscene = ReadCodes(doc.RootElement, "cutscene");
        Choreography = ReadCodes(doc.RootElement, "flagged");
    }

    private static IReadOnlySet<int> ReadCodes(JsonElement root, string tier)
    {
        var set = new HashSet<int>();
        if (!root.TryGetProperty(tier, out var el)) return set;
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in el.EnumerateArray())
                if (r.GetString() is { } id) set.Add(Convert.ToInt32(id, 16));
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
                set.Add(Convert.ToInt32(p.Name, 16));
        }
        return set;
    }
}
