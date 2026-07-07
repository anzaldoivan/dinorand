using System.Text.Json;

namespace DinoRand.Randomizer.Voice;

/// <summary>
/// DC2's cutscene cast as a swap <b>target</b> — the DC2 twin of
/// <see cref="Definitions.VoiceActor"/>. Only Dylan and Regina are verifiable from repo data
/// (<c>data/dc2/items.json</c> owners); the supporting cast is added here as the human categorization
/// pass over <c>voice-export/dc2-voice</c> names them. Donors stay open strings
/// (<see cref="Definitions.VoiceClipSource.Actor"/>), so DC2 banks also serve as <c>*.dc2</c> donor
/// clips with no enum involvement.
/// </summary>
public enum Dc2VoiceActor
{
    /// <summary>An unmapped / not-yet-labelled bank. Default so an un-tagged record is inert.</summary>
    Unknown = 0,
    Dylan,
    Regina,
    David,
    /// <summary>Manifest label <c>old-dylan</c> — a distinct curation folder, so a distinct actor.</summary>
    OldDylan,
}

/// <summary>
/// The verified on-disk PCM format of a DC2 rebirth dialogue bank (<c>Speech\NNNN.dat</c>, ids &lt; 1000):
/// mono, 18900 Hz, 16-bit signed (docs/reference/dc2/voice/VOICE-DECODE-REPORT.md). The <see cref="Dc2WavCodec"/> encode
/// target. The stereo 1000-series banks are non-voice and out of scope.
/// </summary>
public static class Dc2VoiceFormat
{
    public const int SampleRate = 18900;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
}

/// <summary>
/// Decode flag for the DC2 voice-randomization pass — the DC2 twin of
/// <see cref="Definitions.VoiceManifestLayout"/>.
///
/// <para><b>OPEN.</b> The bank format/addressing are decoded, the actor labels landed via the
/// 2026-07-05 folder-curation pass (214/284 banks; Paula WIP stays <c>unknown</c>), and the
/// swap-didn't-work RCA confirmed the file lever is valid on Classic REbirth too: rebirth's
/// <c>ddraw.dll</c> loads <c>Speech\%04d.dat</c> itself and CR ships no speech override (its
/// <c>snd.wbk</c> banks are SFX-only) — so overwriting the loose banks is the whole lever, exactly
/// as DC1 shipped. One swapped line ear-verified in-game closes the loop (docs §6).</para>
/// </summary>
public static class Dc2VoiceManifestLayout
{
    public const bool IsDecoded = true;
}

/// <summary>
/// The DC2 <b>target</b> voice manifest (<c>data/dc2/voice.json</c>) — which rebirth <c>Speech\</c>
/// bank belongs to which cast member, in BioRand's <c>voice.json</c> format. The DC2 twin of
/// <see cref="Dc1VoiceManifest"/>, authored the same way: enumerate the real install banks, label by
/// human categorization (<c>data/dc2/voice-corrections.json</c> is the authoritative overlay), then
/// flip <see cref="Dc2VoiceManifestLayout.IsDecoded"/>. Loading is pure inspection.
/// </summary>
public sealed class Dc2VoiceManifest
{
    /// <summary>Logical resource name of the embedded default manifest (see the .csproj EmbeddedResource).</summary>
    public const string DefaultResourceName = "DinoRand.Randomizer.Data.dc2.voice.json";

    private Dc2VoiceManifest(IReadOnlyList<Dc2VoiceClip> clips) => Clips = clips;

    /// <summary>Every target slot (one per install dialogue bank).</summary>
    public IReadOnlyList<Dc2VoiceClip> Clips { get; }

    /// <summary>The labelled slots for one actor.</summary>
    public IEnumerable<Dc2VoiceClip> ClipsFor(Dc2VoiceActor actor) => Clips.Where(c => c.Actor == actor);

    /// <summary>Load the embedded <c>data/dc2/voice.json</c>.</summary>
    public static Dc2VoiceManifest LoadDefault()
    {
        var asm = typeof(Dc2VoiceManifest).Assembly;
        using var stream = asm.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded voice manifest '{DefaultResourceName}' not found. Resources: " +
                string.Join(", ", asm.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Parse the BioRand-format manifest (same rules as <see cref="Dc1VoiceManifest.Parse"/>): keys
    /// starting with <c>_</c> are metadata; every other key is an install-relative bank path mapping to
    /// <c>{player, actor, kind}</c>. A known DC2 cast name maps to its <see cref="Dc2VoiceActor"/>; any
    /// other label (incl. the pre-categorization <c>unknown</c>) maps to
    /// <see cref="Dc2VoiceActor.Unknown"/> — recorded, never a swap target. A missing actor throws.
    /// </summary>
    public static Dc2VoiceManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var clips = new List<Dc2VoiceClip>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name.StartsWith('_')) continue;          // metadata key

            if (!prop.Value.TryGetProperty("actor", out var actorEl) || actorEl.GetString() is not { } actorName)
                throw new InvalidDataException($"Voice clip '{prop.Name}' has no actor.");
            var name = actorName.Trim();
            // "old-dylan" (the folder-curation label) can't Enum.TryParse; map it explicitly.
            if (string.Equals(name, "old-dylan", StringComparison.OrdinalIgnoreCase))
                name = nameof(Dc2VoiceActor.OldDylan);
            Enum.TryParse<Dc2VoiceActor>(name, ignoreCase: true, out var actor);

            clips.Add(new Dc2VoiceClip(prop.Name, actor));
        }
        return new Dc2VoiceManifest(clips);
    }
}

/// <summary>One DC2 target slot: an install-relative <c>Speech/NNNN.dat</c> path and who speaks it.</summary>
public readonly record struct Dc2VoiceClip(string Path, Dc2VoiceActor Actor);

/// <summary>
/// DC2 target codec — same donor decode as <see cref="PcWavCodec"/>, but
/// <see cref="EncodeForTarget"/> produces the DC2 rebirth dialogue form (<see cref="Dc2VoiceFormat"/>:
/// mono, 18900 Hz, 16-bit) instead of DC1's 8-bit/22050.
/// </summary>
public sealed class Dc2WavCodec : IVoiceCodec
{
    private readonly PcWavCodec _inner = new();

    /// <inheritdoc/>
    public byte[] DecodeToWav(string sourcePath) => _inner.DecodeToWav(sourcePath);

    /// <inheritdoc/>
    public byte[] DecodeToWav(Stream source, string extension) => _inner.DecodeToWav(source, extension);

    /// <inheritdoc/>
    // DC2 rebirth dialogue is a fixed 18900 Hz form, so the per-slot rate hint is ignored here.
    public byte[] EncodeForTarget(byte[] wav, int? targetSampleRate = null)
        => WavAudio.WritePcm16Mono(WavAudio.ReadPcm(wav).Resample(Dc2VoiceFormat.SampleRate));
}
