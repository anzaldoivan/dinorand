namespace DinoRand.Randomizer.Voice;

/// <summary>
/// Canonical decoded audio: mono PCM as float samples in <c>[-1, 1]</c> at a known sample rate. The
/// common currency between the ogg decoder (<see cref="PcWavCodec.DecodeToWav"/>) and the DC1 encoder
/// (<see cref="PcWavCodec.EncodeForTarget"/>) — decode to this, resample, requantize, write back.
/// </summary>
public readonly record struct PcmAudio(float[] Samples, int SampleRate)
{
    /// <summary>
    /// Linear-resample to <paramref name="targetRate"/> (DC1 voice is 22050 Hz; donor oggs are ~18900).
    /// Returns <c>this</c> unchanged when already at the target rate or trivially short.
    /// </summary>
    public PcmAudio Resample(int targetRate)
    {
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate));
        if (targetRate == SampleRate || Samples.Length < 2) return this with { SampleRate = targetRate };

        long outLen = (long)Samples.Length * targetRate / SampleRate;
        var dst = new float[Math.Max(1, outLen)];
        double step = (double)(Samples.Length - 1) / (dst.Length - 1 == 0 ? 1 : dst.Length - 1);
        for (int i = 0; i < dst.Length; i++)
        {
            double pos = i * step;
            int j = (int)pos;
            double frac = pos - j;
            float a = Samples[j];
            float b = j + 1 < Samples.Length ? Samples[j + 1] : a;
            dst[i] = (float)(a + (b - a) * frac);
        }
        return new PcmAudio(dst, targetRate);
    }
}

/// <summary>
/// Minimal RIFF/WAVE PCM reader/writer for the voice codec (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §4). DC1 PC
/// voice files are loose RIFF/WAVE — uncompressed PCM, just renamed <c>.dat</c> under <c>Sound\VOICE\</c>
/// (verified: <c>xa*</c> dialogue = mono 22050 Hz 8-bit, <c>se*</c> = stereo). Only the two PCM
/// encodings DC1 and the donor pipeline actually use are handled: 8-bit unsigned and 16-bit signed.
/// </summary>
public static class WavAudio
{
    /// <summary>
    /// Parse a RIFF/WAVE PCM buffer into canonical mono float samples. Multi-channel input is downmixed
    /// by averaging. Throws on a non-PCM / malformed buffer (the codec only ever feeds it real WAVs).
    /// </summary>
    public static PcmAudio ReadPcm(byte[] wav)
    {
        if (wav.Length < 44 ||
            wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F' ||
            wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E')
            throw new InvalidDataException("Not a RIFF/WAVE buffer.");

        int channels = 0, sampleRate = 0, bits = 0, format = 0;
        int dataOffset = -1, dataLen = 0;

        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(wav, body + 0);
                channels = BitConverter.ToUInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToUInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLen = Math.Min(size, wav.Length - body);
            }
            pos = body + size + (size & 1); // chunks are word-aligned
        }

        if (format != 1) throw new InvalidDataException($"Unsupported WAVE format tag {format} (PCM only).");
        if (channels < 1 || sampleRate < 1 || dataOffset < 0)
            throw new InvalidDataException("Malformed WAVE: missing fmt/data.");

        int bytesPerSample = bits / 8;
        int frames = dataLen / (bytesPerSample * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float acc = 0f;
            for (int c = 0; c < channels; c++)
            {
                int s = dataOffset + (f * channels + c) * bytesPerSample;
                acc += bits switch
                {
                    8 => (wav[s] - 128) / 128f,                       // 8-bit PCM is unsigned
                    16 => BitConverter.ToInt16(wav, s) / 32768f,      // 16-bit PCM is signed
                    _ => throw new InvalidDataException($"Unsupported bit depth {bits}."),
                };
            }
            mono[f] = acc / channels;
        }
        return new PcmAudio(mono, sampleRate);
    }

    /// <summary>Write canonical 16-bit signed mono PCM — the codec's neutral intermediate WAV form.</summary>
    public static byte[] WritePcm16Mono(in PcmAudio a)
    {
        var data = new byte[a.Samples.Length * 2];
        for (int i = 0; i < a.Samples.Length; i++)
        {
            short v = (short)Math.Clamp((int)MathF.Round(a.Samples[i] * 32767f), short.MinValue, short.MaxValue);
            data[i * 2] = (byte)(v & 0xff);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return BuildRiff(data, channels: 1, sampleRate: a.SampleRate, bits: 16);
    }

    /// <summary>Write 8-bit unsigned mono PCM — the on-disk form DC1 PC voice slots use.</summary>
    public static byte[] WritePcm8Mono(in PcmAudio a)
    {
        var data = new byte[a.Samples.Length];
        for (int i = 0; i < a.Samples.Length; i++)
            data[i] = (byte)Math.Clamp((int)MathF.Round(a.Samples[i] * 127f) + 128, 0, 255);
        return BuildRiff(data, channels: 1, sampleRate: a.SampleRate, bits: 8);
    }

    /// <summary>
    /// Parse a RIFF/WAVE PCM buffer <b>preserving channels</b> — interleaved float samples plus the
    /// channel count and sample rate. Unlike <see cref="ReadPcm"/> (which downmixes to mono for the voice
    /// codec), this keeps stereo intact for BGM. Throws on a non-PCM / malformed buffer.
    /// </summary>
    public static (float[] Interleaved, int Channels, int SampleRate) ReadPcmInterleaved(byte[] wav)
    {
        if (wav.Length < 44 ||
            wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F' ||
            wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E')
            throw new InvalidDataException("Not a RIFF/WAVE buffer.");

        int channels = 0, sampleRate = 0, bits = 0, format = 0;
        int dataOffset = -1, dataLen = 0;

        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(wav, body + 0);
                channels = BitConverter.ToUInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToUInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLen = Math.Min(size, wav.Length - body);
            }
            pos = body + size + (size & 1);
        }

        if (format != 1) throw new InvalidDataException($"Unsupported WAVE format tag {format} (PCM only).");
        if (channels < 1 || sampleRate < 1 || dataOffset < 0)
            throw new InvalidDataException("Malformed WAVE: missing fmt/data.");

        int bytesPerSample = bits / 8;
        int total = dataLen / bytesPerSample;
        var samples = new float[total];
        for (int i = 0; i < total; i++)
        {
            int s = dataOffset + i * bytesPerSample;
            samples[i] = bits switch
            {
                8 => (wav[s] - 128) / 128f,
                16 => BitConverter.ToInt16(wav, s) / 32768f,
                _ => throw new InvalidDataException($"Unsupported bit depth {bits}."),
            };
        }
        return (samples, channels, sampleRate);
    }

    /// <summary>Write interleaved 16-bit signed PCM at <paramref name="channels"/>/<paramref name="sampleRate"/>
    /// (the BGM on-disk form). <paramref name="interleaved"/> length must be a multiple of the channel count.</summary>
    public static byte[] WritePcm16(float[] interleaved, int channels, int sampleRate)
    {
        var data = new byte[interleaved.Length * 2];
        for (int i = 0; i < interleaved.Length; i++)
        {
            short v = (short)Math.Clamp((int)MathF.Round(interleaved[i] * 32767f), short.MinValue, short.MaxValue);
            data[i * 2] = (byte)(v & 0xff);
            data[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return BuildRiff(data, channels, sampleRate, bits: 16);
    }

    /// <summary>Assemble a canonical 44-byte-header RIFF/WAVE PCM file around <paramref name="data"/>.</summary>
    private static byte[] BuildRiff(byte[] data, int channels, int sampleRate, int bits)
    {
        int blockAlign = channels * bits / 8;
        int byteRate = sampleRate * blockAlign;
        using var ms = new MemoryStream(44 + data.Length);
        using var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + data.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                       // PCM fmt chunk size
        w.Write((ushort)1);                // PCM
        w.Write((ushort)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((ushort)blockAlign);
        w.Write((ushort)bits);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(data.Length);
        w.Write(data);
        w.Flush();
        return ms.ToArray();
    }
}
