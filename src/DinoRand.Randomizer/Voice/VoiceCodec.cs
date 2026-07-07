using NVorbis;

namespace DinoRand.Randomizer.Voice;

/// <summary>
/// The audio-conversion seam (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §5.1/§5.3). A donor clip (ogg/wav) is
/// decoded to a common WAV form, then encoded into the target platform's on-disk sound format. Keeping
/// this an interface lets the PC loose-WAV path be one entry and a future PSX XA/VAB path another,
/// selected by target platform (D4) — extension by data, not new code paths.
/// </summary>
public interface IVoiceCodec
{
    /// <summary>Decode a donor file (ogg/wav) to canonical PCM WAV bytes.</summary>
    byte[] DecodeToWav(string sourcePath);

    /// <summary>
    /// Decode a donor clip read from an arbitrary (seekable) stream to canonical PCM WAV bytes,
    /// dispatched by <paramref name="extension"/> (e.g. <c>.ogg</c>/<c>.wav</c>). Lets a zip-streamed
    /// donor transcode without ever touching disk.
    /// </summary>
    byte[] DecodeToWav(Stream source, string extension);

    /// <summary>Encode canonical WAV bytes into the target platform's on-disk sound file bytes.
    /// <paramref name="targetSampleRate"/> lets the caller preserve a specific slot's native rate
    /// (e.g. the two 44100 Hz DC1 dialogue banks); <c>null</c> uses the platform default.</summary>
    byte[] EncodeForTarget(byte[] wav, int? targetSampleRate = null);
}

/// <summary>
/// PC target codec (D4). DC1 PC voice slots are loose RIFF/WAVE files renamed <c>.dat</c>
/// (<c>Sound\VOICE\xa*.dat</c>), and the verified on-disk form for dialogue is <b>16-bit signed PCM,
/// mono, 22050 Hz</b> (<see cref="Dc1VoiceFormat"/>; every shipped <c>xa*.dat</c> header reads 16-bit —
/// the earlier "8-bit" claim was a mis-measurement). Donor corpora are Ogg Vorbis (~18900 Hz), so:
/// <list type="bullet">
///   <item><see cref="DecodeToWav"/> turns an <c>.ogg</c> donor into canonical 16-bit-signed mono WAV
///   (a <c>.wav</c> donor is read straight through) using <c>NVorbis</c> (plan §7 R2, signed off).</item>
///   <item><see cref="EncodeForTarget"/> resamples to the target slot's rate (22050 Hz default, or the
///   slot's own native rate when the caller passes it — e.g. the two 44100 Hz banks) and writes 16-bit
///   signed mono — the byte form a DC1 voice slot expects. (PSX XA/VAB is a separate future codec.)</item>
/// </list>
/// No length matching is needed: DC1 cutscenes drive timing from the model animation (plan §6).
/// </summary>
public sealed class PcWavCodec : IVoiceCodec
{
    /// <inheritdoc/>
    public byte[] DecodeToWav(string sourcePath)
    {
        using var stream = File.OpenRead(sourcePath);
        return DecodeToWav(stream, Path.GetExtension(sourcePath));
    }

    /// <inheritdoc/>
    public byte[] DecodeToWav(Stream source, string extension)
    {
        if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            source.CopyTo(ms);
            return ms.ToArray();
        }

        return WavAudio.WritePcm16Mono(DecodeOggToMono(source));
    }

    /// <inheritdoc/>
    public byte[] EncodeForTarget(byte[] wav, int? targetSampleRate = null)
    {
        var pcm = WavAudio.ReadPcm(wav).Resample(targetSampleRate ?? Dc1VoiceFormat.SampleRate);
        return WavAudio.WritePcm16Mono(pcm);
    }

    /// <summary>Decode an Ogg Vorbis stream to canonical mono float PCM at its source sample rate. The
    /// caller owns <paramref name="source"/> (<c>closeOnDispose: false</c>).</summary>
    private static PcmAudio DecodeOggToMono(Stream source)
    {
        using var reader = new VorbisReader(source, closeOnDispose: false);
        int channels = reader.Channels;
        var interleaved = new float[(int)Math.Min(int.MaxValue, reader.TotalSamples) * channels];

        int read = reader.ReadSamples(interleaved, 0, interleaved.Length);
        int frames = read / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float acc = 0f;
            for (int c = 0; c < channels; c++) acc += interleaved[f * channels + c];
            mono[f] = acc / channels;
        }
        return new PcmAudio(mono, reader.SampleRate);
    }
}

/// <summary>
/// The verified on-disk PCM format of a DC1 PC dialogue voice slot (<c>Sound\VOICE\xa*.dat</c>):
/// mono, 22050 Hz, 16-bit signed (every shipped header reads 16-bit; docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §4).
/// The <see cref="PcWavCodec"/> encode target. SE slots (<c>se*.dat</c>) are stereo and out of scope
/// for voice replacement.
/// </summary>
public static class Dc1VoiceFormat
{
    public const int SampleRate = 22050;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
}
