using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Voice;

/// <summary>One resolved overwrite: a DC1 target bank ← a donor clip (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.2).</summary>
/// <param name="TargetBankPath">Install-relative bank path (e.g. <c>Sound/VOICE/xa_ep09b.dat</c>).</param>
/// <param name="Donor">The donor clip whose audio replaces the bank.</param>
public readonly record struct VoiceWrite(string TargetBankPath, VoiceClipSource Donor);

/// <summary>
/// The voice emission planner (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12). Ports BioRand's
/// <c>GetRandomVoice</c>/<c>CheckSample</c> selection: each remapped target draws a replacement of the
/// <b>same kind</b> by the <b>resolved donor actor</b>, <b>without replacement</b> (a shuffled per-actor
/// pool that refills when exhausted, so a clip never repeats until the actor's set is used up).
///
/// <para>Two deliberate deviations from BioRand (DE3/DE4): <b>no length matching</b> — DC1 cutscene
/// timing is animation-driven (§6), so the <c>strict</c>/max-length/silence machinery is dropped; and
/// <b>unmapped actors are left vanilla</b> — only a target whose actor is remapped is overwritten.
/// Pure and deterministic (targets + map + pool + seed ⇒ same writes); touches no audio bytes — that is
/// <see cref="BuildFiles"/>.</para>
/// </summary>
public static class VoiceEmission
{
    /// <summary>
    /// Resolve the overwrite plan: for each target whose actor is remapped, pick a same-kind donor clip
    /// of the mapped donor actor. Targets with no remap are skipped (vanilla). Order follows
    /// <paramref name="targets"/> so the draw is reproducible.
    /// </summary>
    public static IReadOnlyList<VoiceWrite> Plan(
        IReadOnlyList<VoiceClip> targets, CharacterVoiceMap map,
        IReadOnlyList<VoiceClipSource> pool, Random rng)
    {
        var drawer = new DonorDrawer(pool, rng);
        var writes = new List<VoiceWrite>();
        foreach (var target in targets)
        {
            if (!map.TryResolve(target.Actor, out var donorActor)) continue; // DE4: leave vanilla
            var clip = drawer.Next(donorActor, target.Kind);
            if (clip is not null)
                writes.Add(new VoiceWrite(target.Path, clip));
        }
        return writes;
    }

    /// <summary>
    /// Build the loose-file outputs for the run: load the donor packs, plan the actor map and the
    /// overwrites, then transcode each donor through <paramref name="codec"/> into DC1 bank bytes
    /// (16-bit/mono). Keyed by install-relative bank path — the input to
    /// <see cref="RandomizationContext.AddLooseFile"/>. Empty when the swap is a no-op.
    /// <paramref name="targetSampleRate"/> optionally supplies each target slot's native sample rate
    /// (by install-relative bank path) so a replacement preserves it — e.g. the two 44100 Hz DC1
    /// dialogue banks; <c>null</c> (or a null result) falls back to the 22050 Hz default.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> BuildFiles(
        RandomizerConfig config, string packsRoot, Random rng, IVoiceCodec codec, Dc1VoiceManifest manifest,
        Func<string, int?>? targetSampleRate = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        var pool = VoiceDataPack.LoadAll(packsRoot);
        var map = VoiceSwapPlanner.Plan(config, pool, rng);
        if (map.IsEmpty) return files;

        // Honour the cross-game gate on the draw pool too (the planner already gated actor selection).
        var donorPool = pool.Where(c => config.IncludeCrossGameVoices || c.IsNativeDc1).ToList();

        foreach (var write in Plan(manifest.Clips, map, donorPool, rng))
        {
            using var donor = write.Donor.Open();   // folder ⇒ File.OpenRead; zip ⇒ streamed entry
            files[write.TargetBankPath] = codec.EncodeForTarget(
                codec.DecodeToWav(donor, Path.GetExtension(write.Donor.Path)),
                targetSampleRate?.Invoke(write.TargetBankPath));
        }

        return files;
    }

    /// <summary>
    /// The DC2 twin of <see cref="Plan"/>: every DC2 bank is Dialogue (no kind axis), so a remapped
    /// target simply draws the mapped donor's next clip. Unmapped actors (incl. Unknown) stay vanilla.
    /// </summary>
    public static IReadOnlyList<VoiceWrite> PlanDc2(
        IReadOnlyList<Dc2VoiceClip> targets, IReadOnlyDictionary<Dc2VoiceActor, string> map,
        IReadOnlyList<VoiceClipSource> pool, Random rng)
    {
        var drawer = new DonorDrawer(pool, rng);
        var writes = new List<VoiceWrite>();
        foreach (var target in targets)
        {
            if (!map.TryGetValue(target.Actor, out var donorActor)) continue;
            var clip = drawer.Next(donorActor, VoiceKind.Dialogue);
            if (clip is not null)
                writes.Add(new VoiceWrite(target.Path, clip));
        }
        return writes;
    }

    /// <summary>
    /// The DC2 twin of <see cref="BuildFiles"/>: donor packs → <see cref="VoiceSwapPlanner.PlanDc2"/>
    /// map → per-bank draws → <see cref="Dc2WavCodec"/> transcode to the rebirth dialogue form
    /// (mono/18900/16-bit). Keys are install-root-relative <c>Speech/NNNN.dat</c> paths.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> BuildFilesDc2(
        RandomizerConfig config, string packsRoot, Random rng, IVoiceCodec codec, Dc2VoiceManifest manifest)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        var pool = VoiceDataPack.LoadAll(packsRoot);
        var map = VoiceSwapPlanner.PlanDc2(config, pool, rng);
        if (map.Count == 0) return files;

        var donorPool = pool
            .Where(c => config.IncludeCrossGameVoices
                        || string.Equals(c.Game, "dc2", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var write in PlanDc2(manifest.Clips, map, donorPool, rng))
        {
            using var donor = write.Donor.Open();
            files[write.TargetBankPath] =
                codec.EncodeForTarget(codec.DecodeToWav(donor, Path.GetExtension(write.Donor.Path)));
        }

        return files;
    }

    /// <summary>
    /// Draw-without-replacement donor source. Per donor token it keeps a shuffled queue (same-kind when the
    /// token has that kind, else any kind) that refills — reshuffled — once drained, so replacements cycle
    /// through the donor's clips before repeating (BioRand's no-duplicates intent). The token is normally a
    /// game-specific <c>&lt;actor&gt;.&lt;game&gt;</c> key (so a swap never mixes performances across games);
    /// a bare actor name is also accepted (any game) for the dev preview path.
    /// </summary>
    private sealed class DonorDrawer
    {
        private readonly Dictionary<string, List<VoiceClipSource>> _byActor;
        private readonly Dictionary<string, List<VoiceClipSource>> _byKey;   // "actor.game"
        private readonly Dictionary<(string, VoiceKind), Queue<VoiceClipSource>> _queues = new();
        private readonly Random _rng;

        public DonorDrawer(IReadOnlyList<VoiceClipSource> pool, Random rng)
        {
            _rng = rng;
            _byActor = pool
                .GroupBy(c => c.Actor, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            _byKey = pool
                .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        }

        public VoiceClipSource? Next(string donorToken, VoiceKind kind)
        {
            // A composite "actor.game" token draws only that game's performance; a bare actor draws any game.
            var lookup = donorToken.Contains('.') ? _byKey : _byActor;
            if (!lookup.TryGetValue(donorToken, out var clips) || clips.Count == 0)
                return null;

            // Prefer the donor's same-kind clips; fall back to ANY of the donor's clips if none of that kind.
            var effectiveKind = clips.Any(c => c.Kind == kind) ? kind : (VoiceKind?)null;
            var key = (donorToken.ToLowerInvariant(), effectiveKind ?? (VoiceKind)(-1));

            if (!_queues.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                var source = effectiveKind is { } k ? clips.Where(c => c.Kind == k) : clips;
                queue = new Queue<VoiceClipSource>(Shuffle(source.ToList()));
                _queues[key] = queue;
            }
            return queue.Dequeue();
        }

        private List<VoiceClipSource> Shuffle(List<VoiceClipSource> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return list;
        }
    }
}
