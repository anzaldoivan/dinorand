using DinoRand.Randomizer.Voice;
using NVorbis;

namespace DinoRand.Randomizer.Bgm;

/// <summary>
/// Transcodes a donor music track (ogg/wav) into a DC RIFF/WAVE slot's on-disk form
/// (docs/decisions/cross/BGM-RANDO-PLAN.md). DC1 BGM slots are plain RIFF/WAVE under <c>Sound/BGM/</c>
/// (BGM-SYSTEM.md §1a), so the target is 16-bit signed PCM at the slot's own channels+rate — unlike the
/// mono voice codec, BGM stays stereo. Reuses <see cref="WavAudio"/> for RIFF I/O and NVorbis for ogg
/// (same dependency as the voice codec); no length matching is forced, but a generous cap avoids a
/// pathologically large file in a loop-flagged slot.
/// </summary>
public static class BgmCodec
{
    /// <summary>ponytail: length ceiling — most DC1 BGM banks are a couple of minutes; cap keeps an
    /// oversized donor from bloating the install. Raise if long ambient imports get truncated.</summary>
    public const double MaxSeconds = 4 * 60;

    /// <summary>
    /// Decode <paramref name="donor"/> (dispatched by <paramref name="extension"/>, e.g. <c>.ogg</c>/<c>.wav</c>),
    /// conform it to <paramref name="targetChannels"/>/<paramref name="targetRate"/>, cap its length, and
    /// return 16-bit PCM RIFF bytes ready to drop into a slot. The caller owns the stream.
    /// </summary>
    public static byte[] Transcode(Stream donor, string extension, int targetChannels, int targetRate)
    {
        if (targetChannels < 1) targetChannels = 1;
        if (targetRate < 1) targetRate = 44100;

        var (interleaved, channels, rate) = Decode(donor, extension);
        interleaved = ConformChannels(interleaved, channels, targetChannels);
        interleaved = Resample(interleaved, targetChannels, rate, targetRate);
        interleaved = Cap(interleaved, targetChannels, targetRate);
        return WavAudio.WritePcm16(interleaved, targetChannels, targetRate);
    }

    private static (float[] Interleaved, int Channels, int Rate) Decode(Stream donor, string extension)
    {
        if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            donor.CopyTo(ms);
            var r = WavAudio.ReadPcmInterleaved(ms.ToArray());
            return (r.Interleaved, r.Channels, r.SampleRate);
        }

        using var reader = new VorbisReader(donor, closeOnDispose: false);
        int ch = reader.Channels;
        long frames = Math.Min(reader.TotalSamples, int.MaxValue / Math.Max(1, ch));
        var buf = new float[frames * ch];
        int read = reader.ReadSamples(buf, 0, buf.Length);
        if (read < buf.Length) Array.Resize(ref buf, read);
        return (buf, ch, reader.SampleRate);
    }

    /// <summary>Map <paramref name="src"/> channel layout to <paramref name="target"/>: downmix to mono by
    /// averaging, or fan a mono/averaged signal out to N channels. Same source and target ⇒ unchanged.</summary>
    private static float[] ConformChannels(float[] src, int srcChannels, int target)
    {
        if (srcChannels == target) return src;

        int frames = src.Length / Math.Max(1, srcChannels);
        // Collapse to mono first (average across source channels), then fan out to the target width.
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float acc = 0f;
            for (int c = 0; c < srcChannels; c++) acc += src[f * srcChannels + c];
            mono[f] = acc / srcChannels;
        }
        if (target == 1) return mono;

        var dst = new float[frames * target];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < target; c++) dst[f * target + c] = mono[f];
        return dst;
    }

    /// <summary>Linear-resample interleaved audio to <paramref name="targetRate"/>, per channel.</summary>
    private static float[] Resample(float[] src, int channels, int srcRate, int targetRate)
    {
        if (targetRate == srcRate || src.Length < channels * 2) return src;

        int srcFrames = src.Length / channels;
        long dstFrames = Math.Max(1, (long)srcFrames * targetRate / srcRate);
        var dst = new float[dstFrames * channels];
        double step = (double)(srcFrames - 1) / (dstFrames - 1 == 0 ? 1 : dstFrames - 1);
        for (long i = 0; i < dstFrames; i++)
        {
            double pos = i * step;
            int j = (int)pos;
            double frac = pos - j;
            for (int c = 0; c < channels; c++)
            {
                float a = src[j * channels + c];
                float b = (j + 1 < srcFrames) ? src[(j + 1) * channels + c] : a;
                dst[i * channels + c] = (float)(a + (b - a) * frac);
            }
        }
        return dst;
    }

    private static float[] Cap(float[] interleaved, int channels, int rate)
    {
        long maxSamples = (long)(MaxSeconds * rate) * channels;
        if (interleaved.Length <= maxSamples) return interleaved;
        var capped = new float[maxSamples];
        Array.Copy(interleaved, capped, maxSamples);
        return capped;
    }
}
