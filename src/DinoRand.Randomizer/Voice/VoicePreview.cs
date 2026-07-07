using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Voice;

/// <summary>A user-facing, recoverable error from the voice preview path (bad donor, no packs, …).</summary>
public sealed class VoicePreviewException : Exception
{
    public VoicePreviewException(string message) : base(message) { }
}

/// <summary>
/// The pre-gate voice AUDITION path (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.7/§13). Overwrites one or all
/// labelled Regina banks with a chosen donor's clips, transcoded to DC1 format, installed through the
/// <see cref="GameInstaller"/> loose-file backup contract (undo: <see cref="GameInstaller.Restore"/>).
///
/// <para>It deliberately <b>bypasses</b> the hard-gated <see cref="Passes.VoiceRandomizer"/> pass — the
/// gate (<see cref="VoiceManifestLayout.IsDecoded"/>) stays closed; this is the listen-test before it
/// flips, and the only voice path that currently produces an audible in-game change. Shared by the CLI
/// (<c>--voice-preview</c>) and the App's "Preview Regina's voice" button so there is one tested code
/// path. A DC1-only donor is Regina→Regina (inaudible), so an audible preview needs a cross-game donor.</para>
/// </summary>
public static class VoicePreview
{
    /// <summary>Outcome of an install: the resolved donor, its source game(s), and the install counts.</summary>
    public sealed record Result(
        string Donor, string DonorGames, int BanksOverlaid, int BackedUp, IReadOnlyList<VoiceWrite> Writes);

    /// <summary>
    /// Resolve the donor → target banks → transcode plan, touching no game files. <paramref name="allBanks"/>
    /// targets every labelled Regina bank; otherwise <paramref name="bankStem"/> picks one bank by stem, and
    /// a null stem falls back to the first bank (ordinal). Throws <see cref="VoicePreviewException"/> with a
    /// user-facing message on any resolvable error.
    /// </summary>
    public static IReadOnlyList<VoiceWrite> Plan(
        IReadOnlyList<VoiceClipSource> pool, string donorActor,
        bool allBanks, string? bankStem, Random rng, Dc1VoiceManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(donorActor))
            throw new VoicePreviewException("No donor character selected.");

        var donor = donorActor.ToLowerInvariant();
        if (!pool.Any(c => string.Equals(c.Actor, donor, StringComparison.OrdinalIgnoreCase)))
            throw new VoicePreviewException($"Donor '{donorActor}' has no clips in the selected packs.");

        IReadOnlyList<VoiceClip> targets;
        if (allBanks)
            targets = manifest.Clips;
        else if (!string.IsNullOrWhiteSpace(bankStem))
        {
            targets = manifest.Clips
                .Where(c => Path.GetFileNameWithoutExtension(c.Path).Equals(bankStem, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (targets.Count == 0)
                throw new VoicePreviewException($"Bank '{bankStem}' is not a labelled Regina bank.");
        }
        else
            targets = new[] { manifest.Clips.OrderBy(c => c.Path, StringComparer.Ordinal).First() };

        var map = new CharacterVoiceMap();
        map.Set(VoiceActor.Regina, donor);
        var writes = VoiceEmission.Plan(targets, map, pool, rng);
        if (writes.Count == 0)
            throw new VoicePreviewException($"Donor '{donorActor}' produced no clip to swap in.");
        return writes;
    }

    /// <summary>Transcode the planned writes into a staging mod dir (install-relative tree).</summary>
    public static void Transcode(IReadOnlyList<VoiceWrite> writes, string modDir, IVoiceCodec codec)
    {
        foreach (var w in writes)
        {
            var dest = Path.Combine(modDir, w.TargetBankPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var donorStream = w.Donor.Open();   // folder ⇒ File.OpenRead; zip ⇒ streamed entry
            File.WriteAllBytes(dest,
                codec.EncodeForTarget(codec.DecodeToWav(donorStream, Path.GetExtension(w.Donor.Path))));
        }
    }

    /// <summary>
    /// Pick the donor actor: an explicit name (lower-cased), or <c>null</c>/<c>"random"</c> ⇒ a random
    /// eligible actor from <paramref name="pool"/> (seeded, so reproducible). Throws when the pool is empty.
    /// </summary>
    public static string ResolveDonor(IReadOnlyList<VoiceClipSource> pool, string? requested, Random rng)
    {
        if (!string.IsNullOrWhiteSpace(requested) &&
            !string.Equals(requested, "random", StringComparison.OrdinalIgnoreCase))
            return requested!.ToLowerInvariant();

        var actors = pool.Select(c => c.Actor).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (actors.Count == 0)
            throw new VoicePreviewException("No donor characters available in the selected packs.");
        return actors[rng.Next(actors.Count)];
    }

    /// <summary>
    /// High-level App entry: load the packs (honouring <paramref name="includeCrossGame"/>), resolve the
    /// donor, transcode the chosen banks, and install them via the loose-file backup contract. Throws
    /// <see cref="VoicePreviewException"/> with a user-facing message on any resolvable error.
    /// </summary>
    public static Result Install(
        string dataDir, string packsRoot, string? donorActor,
        bool allBanks, bool includeCrossGame, Seed seed)
    {
        if (string.IsNullOrWhiteSpace(packsRoot) || !Directory.Exists(packsRoot))
            throw new VoicePreviewException("Set a valid voice donor packs folder first.");

        var pool = VoiceDataPack.LoadAll(packsRoot)
            .Where(c => includeCrossGame || c.IsNativeDc1).ToList();
        if (pool.Count == 0)
            throw new VoicePreviewException("No donor clips found under the packs folder.");

        var rng = seed.RngFor("voice-preview");
        var donor = ResolveDonor(pool, donorActor, rng);

        var writes = Plan(pool, donor, allBanks, bankStem: null, rng, Dc1VoiceManifest.LoadDefault());

        var modDir = Path.Combine(Path.GetTempPath(), "dinorand_voice_preview_" + seed);
        Transcode(writes, modDir, new PcWavCodec());

        var ir = GameInstaller.Install(dataDir, modDir, seed.ToString());
        var games = string.Join("+", writes.Select(w => w.Donor.Game)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g));
        return new Result(donor, games, ir.Overlaid, ir.BackedUp, writes);
    }
}
