using DinoRand.Randomizer.Definitions;

namespace DinoRand.Randomizer.Passes;

/// <summary>
/// Cutscene character-voice randomization (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md). Ports BioRand's voice rando
/// (<c>VoiceRandomiser.cs</c>): draw donor clips from BioRand-layout datapacks
/// (<see cref="Voice.VoiceDataPack"/>), decide each cast member's new voice
/// (<see cref="Voice.VoiceSwapPlanner"/>), then overwrite the original DC1 WAV bytes via the
/// <see cref="Voice.IVoiceCodec"/> + the <c>GameInstaller</c> backup contract — the game's own
/// cutscene→file lookup is untouched.
///
/// <para><b>LIVE (plan §13).</b> DC1's cutscene-voice file addressing is decoded (per-bank actor labels in
/// <c>data/dc1/voice.json</c>) and the overwrite path is in-game-verified, so
/// <see cref="VoiceManifestLayout.IsDecoded"/> is <c>true</c> and this pass <b>emits</b> the swapped voice
/// banks with the seed (loose-file install under <c>Sound\VOICE\</c>, reversed by Restore). Should the gate
/// ever be re-closed it returns after logging, committing nothing. Model swap is a deferred placeholder
/// (<see cref="CharacterModelSwap"/>, §9).</para>
/// </summary>
public sealed class VoiceRandomizer : IRandomizationPass
{
    public string Name => "voices";

    public bool IsEnabled(RandomizerConfig config) => config.RandomizeVoices;

    public void Apply(RandomizationContext ctx)
    {
        // Decode gate: if the cutscene-voice addressing is ever marked undecoded again, commit nothing
        // (mirrors DoorPoseLayout.IsDecoded). Open today (const true), so this branch is dormant — the
        // pragma silences the expected "unreachable" warning while keeping the defensive guard in place.
#pragma warning disable CS0162 // Unreachable code (the gate is a compile-time const)
        if (!VoiceManifestLayout.IsDecoded)
        {
            ctx.Log("[voices] gated: DC1 cutscene-voice addressing is undecoded — no voice bytes emitted.");
            return;
        }
#pragma warning restore CS0162

        Emit(ctx);
    }

    /// <summary>
    /// Compose the emission (plan §12.2): build the swapped voice banks and register them as loose install
    /// files. Separated from <see cref="Apply"/> so the tail is unit-testable without flipping the global
    /// gate. A no-op when no donor packs root is configured or the swap resolves to nothing.
    /// </summary>
    public static void Emit(RandomizationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Config.VoicePacksRoot))
        {
            ctx.Log("[voices] no VoicePacksRoot configured — nothing to draw donors from.");
            return;
        }

        // Per-slot native-rate preservation: read each target bank's own sample rate from the source
        // install so a replacement keeps it (most banks are 22050 Hz; a couple ship at 44100 Hz). No
        // install (unit tests) or an unreadable slot ⇒ null ⇒ the codec's 22050 Hz default.
        var voiceDir = ctx.InstallDir is { } id ? Voice.Dc1VoiceCatalog.FindVoiceDir(id) : null;
        Func<string, int?>? targetRate = voiceDir is null ? null : bankPath =>
        {
            var original = Path.Combine(voiceDir, Path.GetFileName(bankPath));
            return File.Exists(original) ? Voice.Dc1VoiceCatalog.TryReadFormat(original)?.SampleRate : null;
        };

        var files = Voice.VoiceEmission.BuildFiles(
            ctx.Config, ctx.Config.VoicePacksRoot!, ctx.Seed.RngFor("voices"),
            new Voice.PcWavCodec(), Voice.Dc1VoiceManifest.LoadDefault(), targetRate);

        foreach (var (path, bytes) in files)
            ctx.AddLooseFile(path, bytes);

        ctx.Log($"[voices] prepared {files.Count} voice bank overwrite(s).");
    }
}
