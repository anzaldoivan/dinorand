using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Voice;

/// <summary>
/// Pure, RNG-seeded planning for the voice swap (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §5, §13). Given the config
/// and the loaded donor pool, decides which donor actor each swappable DC1 cast member speaks as, and
/// returns a <see cref="CharacterVoiceMap"/>. DOM-free and deterministic (seed + config + pool ⇒ same
/// map), so it is fully unit-testable without touching audio bytes or game files.
///
/// <para>One master lever (<see cref="RandomizerConfig.RandomizeVoices"/>). When on, each member of
/// <see cref="SwappableCast"/> (Regina/Rick/Gail/Kirk) is decided by its per-character entry in
/// <see cref="RandomizerConfig.VoiceDonors"/>:
/// <list type="bullet">
///   <item><b>no entry ⇒ Default</b>: the character keeps their own voice (left vanilla, not swapped);</item>
///   <item><b><c>"random"</c> ⇒</b> a random eligible donor drawn per seed;</item>
///   <item><b>a donor name ⇒</b> that donor (when eligible), else a random one.</item>
/// </list>
/// Donor actors are filtered to native DC1 unless <see cref="RandomizerConfig.IncludeCrossGameVoices"/> is
/// set. Other labelled cast (Cooper/Tom/Colonel) and the non-cast machine voices are always left vanilla.</para>
/// </summary>
public static class VoiceSwapPlanner
{
    /// <summary>
    /// The cast the voice randomizer swaps (docs/decisions/dc1/voice/VOICE-UI-PLAN.md): the protagonist plus the three
    /// supporting characters with broad, verified line coverage. Order is fixed so the seeded random draw
    /// is reproducible.
    /// </summary>
    public static readonly IReadOnlyList<VoiceActor> SwappableCast =
        new[] { VoiceActor.Regina, VoiceActor.Rick, VoiceActor.Gail, VoiceActor.Kirk };

    /// <summary>
    /// Build the actor→voice map. Empty (a no-op) when <see cref="RandomizerConfig.RandomizeVoices"/> is
    /// off, when every target is left on its Default (no <see cref="RandomizerConfig.VoiceDonors"/> entry),
    /// or when the donor pool offers no eligible actor to draw from.
    /// </summary>
    public static CharacterVoiceMap Plan(
        RandomizerConfig config, IReadOnlyList<VoiceClipSource> donorPool, Random rng)
    {
        var map = new CharacterVoiceMap();
        if (!config.RandomizeVoices || config.VoiceDonors is null) return map;

        // Eligible donors are game-specific keys ("actor.game"), honouring the cross-game gate, so a swap
        // never mixes an actor's performances across games (e.g. claire.recv vs claire.re2r).
        var donorKeys = donorPool
            .Where(c => config.IncludeCrossGameVoices || c.IsNativeDc1)
            .Select(c => c.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.Ordinal)   // stable order so the seeded draw is reproducible
            .ToList();
        if (donorKeys.Count == 0) return map;

        foreach (var target in SwappableCast)
        {
            // No entry ⇒ Default: the character keeps their own voice (left vanilla).
            if (!config.VoiceDonors.TryGetValue(target.ToString().ToLowerInvariant(), out var sel))
                continue;
            map.Set(target, ResolveDonorKey(sel, donorKeys, rng));
        }

        return map;
    }

    /// <summary>
    /// The DC2 swappable cast with its <see cref="RandomizerConfig.VoiceDonors"/> keys (the UI's
    /// dropdown targets; "old-dylan" is the folder-curation label). Fixed order for reproducible draws.
    /// </summary>
    private static readonly (Dc2VoiceActor Actor, string Key)[] Dc2SwappableCast =
    {
        (Dc2VoiceActor.Dylan, "dylan"), (Dc2VoiceActor.Regina, "regina"),
        (Dc2VoiceActor.David, "david"), (Dc2VoiceActor.OldDylan, "old-dylan"),
    };

    /// <summary>
    /// The DC2 twin of <see cref="Plan"/>: same Default/random/name rules per cast member, but the
    /// native donor gate is <c>.dc2</c> (with <see cref="RandomizerConfig.IncludeCrossGameVoices"/>
    /// opening the full pool). Empty map ⇒ the pass is a no-op; <see cref="Dc2VoiceActor.Unknown"/> is
    /// never a target.
    /// </summary>
    public static IReadOnlyDictionary<Dc2VoiceActor, string> PlanDc2(
        RandomizerConfig config, IReadOnlyList<VoiceClipSource> donorPool, Random rng)
    {
        var map = new Dictionary<Dc2VoiceActor, string>();
        if (!config.RandomizeVoices || config.VoiceDonors is null) return map;

        var donorKeys = donorPool
            .Where(c => config.IncludeCrossGameVoices
                        || string.Equals(c.Game, "dc2", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (donorKeys.Count == 0) return map;

        foreach (var (actor, key) in Dc2SwappableCast)
            if (config.VoiceDonors.TryGetValue(key, out var sel))   // no entry ⇒ Default ⇒ vanilla
                map[actor] = ResolveDonorKey(sel, donorKeys, rng);

        return map;
    }

    /// <summary>
    /// Resolve one per-character selection to a concrete game-specific donor key from
    /// <paramref name="donorKeys"/>. <c>"random"</c>/blank ⇒ a random key; a composite <c>actor.game</c>
    /// selection ⇒ itself when eligible; a bare actor name (legacy / older saved config) ⇒ a random one of
    /// that actor's games. Anything unmatched falls back to a random key.
    /// </summary>
    private static string ResolveDonorKey(string? sel, IReadOnlyList<string> donorKeys, Random rng)
    {
        if (!string.IsNullOrWhiteSpace(sel) && !sel.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            var pick = sel.ToLowerInvariant();
            if (pick.Contains('.'))
            {
                if (donorKeys.Contains(pick, StringComparer.OrdinalIgnoreCase)) return pick;
            }
            else
            {
                // Bare actor name ⇒ choose one of that actor's game performances (no cross-game mixing).
                var ofActor = donorKeys
                    .Where(k => k.StartsWith(pick + ".", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (ofActor.Count > 0) return ofActor[rng.Next(ofActor.Count)];
            }
        }
        return donorKeys[rng.Next(donorKeys.Count)];
    }
}
