namespace DinoRand.Randomizer.Voice;

/// <summary>
/// A length-normalized loudness-envelope fingerprint of a mono clip, used to identify which DC1
/// <c>xa*</c> bank holds the same recording as a ripped donor clip (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §7 R1,
/// actor labelling). The donor <c>regina.dc1</c> oggs are re-encodings of DC1's own Regina lines, so the
/// true match shares the recording — its RMS contour matches almost exactly, while a different line of
/// similar length does not.
///
/// <para>Design choices that make this robust: leading/trailing silence is trimmed (ogg encoder
/// delay/padding differs from the WAV), the remaining region is split into a fixed bin count (so clips
/// of slightly different length stay comparable), each bin is RMS loudness, and the vector is
/// L2-normalized so <see cref="Similarity"/> is a cosine in <c>[0, 1]</c> independent of absolute gain
/// (lossy ogg shifts amplitude). Purely numeric ⇒ deterministic and unit-testable.</para>
/// </summary>
public static class VoiceFingerprint
{
    /// <summary>Default bin count — enough resolution to separate distinct speech contours.</summary>
    public const int DefaultBins = 128;

    /// <summary>
    /// Compute the normalized envelope fingerprint of <paramref name="audio"/>. Returns a zero vector
    /// for silent/empty input (which then matches nothing, by construction).
    /// </summary>
    public static float[] Compute(in PcmAudio audio, int bins = DefaultBins)
    {
        if (bins < 1) throw new ArgumentOutOfRangeException(nameof(bins));
        var s = audio.Samples;
        var fp = new float[bins];
        if (s.Length == 0) return fp;

        // Peak-relative silence trim: drop the dead air the codecs pad differently.
        float peak = 0f;
        for (int i = 0; i < s.Length; i++) peak = MathF.Max(peak, MathF.Abs(s[i]));
        if (peak <= 0f) return fp;
        float gate = peak * 0.03f;

        int lo = 0; while (lo < s.Length && MathF.Abs(s[lo]) < gate) lo++;
        int hi = s.Length - 1; while (hi > lo && MathF.Abs(s[hi]) < gate) hi--;
        int len = hi - lo + 1;
        if (len < bins) return fp; // too short to fingerprint meaningfully

        // RMS loudness per equal-width bin over the trimmed region.
        for (int b = 0; b < bins; b++)
        {
            int start = lo + (int)((long)b * len / bins);
            int end = lo + (int)((long)(b + 1) * len / bins);
            if (end <= start) end = start + 1;
            double acc = 0;
            for (int i = start; i < end && i <= hi; i++) acc += (double)s[i] * s[i];
            fp[b] = (float)Math.Sqrt(acc / (end - start));
        }

        // L2-normalize ⇒ cosine similarity is gain-independent.
        double norm = 0; foreach (var v in fp) norm += (double)v * v;
        norm = Math.Sqrt(norm);
        if (norm > 0) for (int b = 0; b < bins; b++) fp[b] = (float)(fp[b] / norm);
        return fp;
    }

    /// <summary>
    /// Cosine similarity of two fingerprints (dot product, since both are L2-normalized). <c>1.0</c> for
    /// identical contours, near <c>0</c> for unrelated ones. Returns <c>0</c> on a length mismatch.
    /// </summary>
    public static float Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        double dot = 0; for (int i = 0; i < a.Length; i++) dot += (double)a[i] * b[i];
        return (float)dot;
    }
}
