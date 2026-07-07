using DinoRand.Randomizer.Voice;
using Xunit;
using Xunit.Abstractions;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Validation of the Regina actor-labelling fingerprinter (docs/dc1/VOICE-RANDO-PLAN.md §7 R1).
/// Pure-numeric tests cover the fingerprint/match primitives; the real-data test decodes the in-repo
/// <c>regina.dc1</c> corpus against the install's <c>xa*</c> banks and reports the confident-match rate
/// (the "fingerprint tool first, then decide" deliverable). No bytes are emitted — labelling only.
/// </summary>
public class ReginaVoiceMatcherTests
{
    private readonly ITestOutputHelper _out;
    public ReginaVoiceMatcherTests(ITestOutputHelper output) => _out = output;

    // --- fingerprint primitives (synthetic, deterministic) ------------------------------------

    private static PcmAudio Tone(int n, double cyclesPerSample, float gain = 0.8f, int sr = 22050)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = gain * MathF.Sin((float)(2 * Math.PI * cyclesPerSample * i));
        return new PcmAudio(s, sr);
    }

    [Fact]
    public void Fingerprint_IdenticalSignal_SimilarityIsOne()
    {
        var a = VoiceFingerprint.Compute(Tone(10000, 0.01));
        Assert.True(VoiceFingerprint.Similarity(a, a) > 0.999f);
    }

    [Fact]
    public void Fingerprint_GainInvariant()
    {
        // Same envelope shape at different volume ⇒ near-identical fingerprint (L2-normalized).
        var quiet = VoiceFingerprint.Compute(EnvelopeRamp(8000, 0.2f));
        var loud = VoiceFingerprint.Compute(EnvelopeRamp(8000, 0.9f));
        Assert.True(VoiceFingerprint.Similarity(quiet, loud) > 0.99f);
    }

    [Fact]
    public void Fingerprint_DifferentEnvelopes_AreSeparable()
    {
        // Energy front-loaded vs back-loaded ⇒ clearly distinguishable contours.
        var front = VoiceFingerprint.Compute(HalfLoud(8000, firstHalf: true));
        var back = VoiceFingerprint.Compute(HalfLoud(8000, firstHalf: false));
        Assert.True(VoiceFingerprint.Similarity(front, back) < 0.6f);
        Assert.True(VoiceFingerprint.Similarity(front, front) > 0.999f);
    }

    [Fact]
    public void Fingerprint_SilenceTrim_MakesPaddingIrrelevant()
    {
        // The same body with extra leading/trailing silence must fingerprint the same (codec padding).
        var body = HalfLoud(6000, firstHalf: true);
        var padded = new float[body.Samples.Length + 3000];
        Array.Copy(body.Samples, 0, padded, 1500, body.Samples.Length);
        var a = VoiceFingerprint.Compute(body);
        var b = VoiceFingerprint.Compute(new PcmAudio(padded, body.SampleRate));
        Assert.True(VoiceFingerprint.Similarity(a, b) > 0.98f);
    }

    [Fact]
    public void Matcher_PicksConfidentUniqueMatch()
    {
        var bankA = VoiceFingerprint.Compute(HalfLoud(8000, firstHalf: true));
        var bankB = VoiceFingerprint.Compute(HalfLoud(8000, firstHalf: false));
        var banks = new[]
        {
            new ReginaVoiceMatcher.Clip("xaA", bankA, 0.36),
            new ReginaVoiceMatcher.Clip("xaB", bankB, 0.36),
        };
        var donor = new[] { new ReginaVoiceMatcher.Clip("regina001", bankA, 0.36) };

        var m = ReginaVoiceMatcher.Match(donor, banks);
        Assert.True(m[0].IsConfident);
        Assert.Equal("xaA", m[0].XaFile);
    }

    // --- real corpus validation (skipped if assets absent) ------------------------------------

    [Fact]
    public void Validate_ReginaCorpus_MatchesInstallBanks_AndReports()
    {
        var corpus = DecodeRealCorpus();
        if (corpus == null) return; // need both fixtures
        var (banks, donors) = corpus.Value;

        var matches = ReginaVoiceMatcher.Match(donors, banks);
        var report = ReginaVoiceMatcher.Summarize(matches);

        _out.WriteLine($"banks(xa)={banks.Count} donors(regina)={donors.Count}");
        _out.WriteLine($"confident={report.Confident}/{report.Total} ({report.ConfidentRate:P1}) conflicts={report.Conflicts}");
        var top = matches.OrderByDescending(m => m.Score).Take(3);
        foreach (var m in top) _out.WriteLine($"  ex {m.DonorClip} -> {m.XaFile} score={m.Score:F3} margin={m.Margin:F3}");
        var weak = matches.Where(m => !m.IsConfident).Take(5);
        foreach (var m in weak) _out.WriteLine($"  weak {m.DonorClip} best={m.Score:F3} margin={m.Margin:F3}");

        // Persist the full report next to the test binaries (not in the repo tree).
        var reportPath = Path.Combine(AppContext.BaseDirectory, "regina-match-report.txt");
        File.WriteAllLines(reportPath, new[]
            {
                $"banks(xa)={banks.Count} donors(regina)={donors.Count}",
                $"confident={report.Confident}/{report.Total} rate={report.ConfidentRate:P1} conflicts={report.Conflicts}",
            }.Concat(matches.OrderBy(m => m.DonorClip)
                .Select(m => $"{m.DonorClip}\t{m.XaFile ?? "-"}\t{m.Score:F3}\t{m.Margin:F3}\t{(m.IsConfident ? "OK" : "weak")}")));

        // Measured 2026-06-27 against the in-repo english/ install + dinocrisis pack:
        //   207 donors vs 649 xa banks → 130 confident-unique (62.8%), 3 conflicts, 195 with best≥0.80.
        // The fingerprint independently lands on the index-matching bank (regina00N ↔ xa_ep0Nb), and the
        // sub-confident cases are margin failures from lines duplicated across alternate-condition banks
        // (…a/…b), i.e. legitimate 1-to-many — not wrong matches. Floors guard that this holds.
        int highScore = matches.Count(m => m.Score >= 0.80f); // true match found, margin aside
        Assert.Equal(207, report.Total);
        Assert.True(report.ConfidentRate > 0.55, $"confident rate regressed: {report.ConfidentRate:P1}");
        Assert.True(highScore / (double)report.Total > 0.85, $"high-score coverage regressed: {highScore}/{report.Total}");
        Assert.True(report.Conflicts < 10, $"too many xa-claim conflicts: {report.Conflicts}");
    }

    // --- bank-centric 1-to-many labelling (§12.5/§12.6) ---------------------------------------

    [Fact]
    public void MatchBanks_LabelsBank_WhenAnyDonorScoresHigh_IgnoringMargin()
    {
        // Two banks share the same contour (an …a/…b duplicate); the donor-centric Match would reject one
        // on margin, but bank-centric labels BOTH (both are Regina).
        var contour = VoiceFingerprint.Compute(HalfLoud(8000, firstHalf: true));
        var other = VoiceFingerprint.Compute(HalfLoud(8000, firstHalf: false));
        var banks = new[]
        {
            new ReginaVoiceMatcher.Clip("xa_a", contour, 0.36),
            new ReginaVoiceMatcher.Clip("xa_b", contour, 0.36),
            new ReginaVoiceMatcher.Clip("xa_other", other, 0.36),
        };
        var donors = new[] { new ReginaVoiceMatcher.Clip("regina001", contour, 0.36) };

        var labels = ReginaVoiceMatcher.MatchBanks(banks, donors);

        Assert.True(labels.Single(l => l.XaBank == "xa_a").IsRegina);
        Assert.True(labels.Single(l => l.XaBank == "xa_b").IsRegina);   // duplicate also labelled
        Assert.False(labels.Single(l => l.XaBank == "xa_other").IsRegina); // dissimilar bank excluded
    }

    [Fact]
    public void Validate_BankCentric_LabelsMoreThanConfident_ButNotEveryBank()
    {
        var corpus = DecodeRealCorpus();
        if (corpus == null) return;
        var (banks, donors) = corpus.Value;

        var labels = ReginaVoiceMatcher.MatchBanks(banks, donors);
        var regina = labels.Where(l => l.IsRegina).ToList();

        _out.WriteLine($"bank-centric Regina banks: {regina.Count}/{banks.Count}");

        // Persist the bank labels so data/dc1/voice.json can be regenerated from real data (§12.6).
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "regina-bank-labels.txt"),
            labels.OrderBy(l => l.XaBank)
                  .Select(l => $"{l.XaBank}\t{l.BestDonor ?? "-"}\t{l.Score:F3}\t{(l.IsRegina ? "regina" : "-")}"));

        // Measured 2026-06-27 (english/ install): bank-centric @0.92 ⇒ 144 banks. The score histogram is
        // bimodal — a non-Regina mass ≤~0.85 and a Regina cluster ≥~0.92 — so 0.92 recovers the ~14
        // duplicate …a/…b takes the donor-centric pass dropped on margin (130 → 144) without dipping into
        // the supporting cast. Pin that band; precision is favoured (a false positive mis-voices another actor).
        Assert.InRange(regina.Count, 138, 175);                                   // expanded past confident-130…
        Assert.True(regina.Count < banks.Count * 0.4, $"over-claimed banks as Regina: {regina.Count}/{banks.Count}");
    }

    // --- helpers ------------------------------------------------------------------------------

    /// <summary>Decode the install's xa* banks + the ripped Regina donors into fingerprinted clips,
    /// or <c>null</c> when either fixture is absent (so real-data tests skip off-machine).</summary>
    private (List<ReginaVoiceMatcher.Clip> Banks, List<ReginaVoiceMatcher.Clip> Donors)? DecodeRealCorpus()
    {
        var voiceDir = Dc1VoiceCatalog.FindVoiceDir(Path.Combine(RepoRoot(), "english"));
        var reginaDir = Path.Combine(RepoRoot(), "biorand", "datapacks", "dinocrisis", "data", "voice", "regina.dc1");
        if (voiceDir == null || !Directory.Exists(reginaDir)) return null;

        var codec = new PcWavCodec();
        var banks = Dc1VoiceCatalog.Enumerate(voiceDir)
            .Where(f => f.IsDialogue)
            .Select(f =>
            {
                var pcm = WavAudio.ReadPcm(File.ReadAllBytes(f.Path));
                return new ReginaVoiceMatcher.Clip(f.Name, VoiceFingerprint.Compute(pcm),
                    pcm.Samples.Length / (double)pcm.SampleRate);
            })
            .ToList();
        var donors = Directory.EnumerateFiles(reginaDir, "*.ogg")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var pcm = WavAudio.ReadPcm(codec.DecodeToWav(p));
                return new ReginaVoiceMatcher.Clip(Path.GetFileName(p), VoiceFingerprint.Compute(pcm),
                    pcm.Samples.Length / (double)pcm.SampleRate);
            })
            .ToList();
        return (banks, donors);
    }

    private static PcmAudio EnvelopeRamp(int n, float peak)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = peak * (i / (float)n) * MathF.Sin(0.05f * i);
        return new PcmAudio(s, 22050);
    }

    private static PcmAudio HalfLoud(int n, bool firstHalf)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            bool loud = firstHalf ? i < n / 2 : i >= n / 2;
            s[i] = (loud ? 0.9f : 0.05f) * MathF.Sin(0.07f * i);
        }
        return new PcmAudio(s, 22050);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "biorand")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
