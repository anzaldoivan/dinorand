using DinoRand.Randomizer.Voice;

namespace DinoRand.Randomizer.Dc2.Passes;

/// <summary>
/// DC2 cutscene character-voice randomization — the DC2 twin of <see cref="Passes.VoiceRandomizer"/>,
/// reusing the same donor engine (<see cref="VoiceDataPack"/>/<see cref="VoiceSwapPlanner"/>/
/// <see cref="VoiceEmission"/>; DC2 banks join the pool as <c>&lt;actor&gt;.dc2</c> donors with no new
/// code). Driven by the same config surface as DC1 (<see cref="RandomizerConfig.RandomizeVoices"/> +
/// <see cref="RandomizerConfig.VoicePacksRoot"/>).
///
/// <para><b>LIVE.</b> <see cref="Dc2VoiceManifestLayout.IsDecoded"/> is <c>true</c> (labels landed
/// 2026-07-05; the swap-didn't-work RCA confirmed rebirth's <c>ddraw.dll</c> reads
/// <c>Speech\%04d.dat</c> directly, so the loose-file overwrite is valid on CR installs too). The
/// tail mirrors DC1's §12.2: <see cref="VoiceEmission.BuildFilesDc2"/> over
/// <see cref="Dc2VoiceManifest.LoadDefault"/> with <see cref="Dc2WavCodec"/>, emitted as
/// <c>Speech/NNNN.dat</c> files the installer overlays game-root-relative.</para>
/// </summary>
public sealed class Dc2VoiceRandomizer : IDc2RandomizationPass
{
    public string Name => "voices";

    public bool IsEnabled(RandomizerConfig config) => config.RandomizeVoices;

    public void Apply(Dc2RandomizationContext ctx)
    {
        // Decode gate (mirrors DC1's VoiceManifestLayout guard): open today (const true), so this branch
        // is dormant. The pragma silences the expected "unreachable" warning.
#pragma warning disable CS0162 // Unreachable code (the gate is a compile-time const)
        if (!Dc2VoiceManifestLayout.IsDecoded)
        {
            ctx.Log("[voices] gated: DC2 voice swap is undecoded — no voice bytes emitted.");
            return;
        }
        Emit(ctx);
#pragma warning restore CS0162
    }

    /// <summary>The emission tail, separated from <see cref="Apply"/> like DC1's
    /// <see cref="Passes.VoiceRandomizer.Emit"/> so it stays testable if the gate ever re-closes.</summary>
    public static void Emit(Dc2RandomizationContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Config.VoicePacksRoot))
        {
            ctx.Log("[voices] no VoicePacksRoot configured — nothing to draw donors from.");
            return;
        }

        var files = VoiceEmission.BuildFilesDc2(
            ctx.Config, ctx.Config.VoicePacksRoot!, ctx.Seed.RngFor("voices"),
            new Dc2WavCodec(), Dc2VoiceManifest.LoadDefault());

        foreach (var (path, bytes) in files)
            ctx.Sink.EmitFile(path, bytes);

        ctx.Log($"[voices] prepared {files.Count} Speech bank overwrite(s).");
    }
}
