namespace DinoRand.Randomizer.Voice;

/// <summary>One donor clip's best fingerprint match against the DC1 <c>xa*</c> banks.</summary>
/// <param name="DonorClip">The donor filename (e.g. <c>regina001.ogg</c>).</param>
/// <param name="XaFile">The best-matching DC1 bank stem (e.g. <c>xa10200</c>), or <c>null</c> if none qualified.</param>
/// <param name="Score">Cosine similarity of the best match (1.0 ⇒ identical contour).</param>
/// <param name="Margin">Lead of the best over the second-best candidate (separation = confidence).</param>
/// <param name="IsConfident"><c>true</c> when <see cref="Score"/> and <see cref="Margin"/> clear the thresholds.</param>
public readonly record struct FingerprintMatch(
    string DonorClip, string? XaFile, float Score, float Margin, bool IsConfident);

/// <summary>Summary statistics of a labelling run (the validation deliverable, plan §7 R1).</summary>
/// <param name="Total">Donor clips matched.</param>
/// <param name="Confident">Matches clearing both thresholds.</param>
/// <param name="Conflicts">Distinct xa files claimed by more than one confident donor clip (should be ~0).</param>
public readonly record struct MatchReport(int Total, int Confident, int Conflicts)
{
    /// <summary>Fraction of donor clips that got a confident, unique-looking match.</summary>
    public double ConfidentRate => Total == 0 ? 0 : (double)Confident / Total;
}

/// <summary>
/// Labels which DC1 <c>Sound\VOICE\xa*.dat</c> banks hold Regina's lines by fingerprint-matching them
/// against BioRand's ripped <c>regina.dc1</c> corpus (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §7 R1). The donor
/// oggs are re-encodings of these very files, so a content fingerprint (<see cref="VoiceFingerprint"/>)
/// identifies each line's home bank — solving the protagonist's half of the actor↔file map without
/// manual listening. Supporting-cast banks have no ripped corpus and are labelled separately (chosen
/// approach: cutscene-script RE).
///
/// <para>This stage is <b>validation-only</b> for now: it produces the candidate Regina slot map and a
/// confidence <see cref="MatchReport"/>; nothing is written and the emission gate stays closed until the
/// map is reviewed in-game.</para>
/// </summary>
public static class ReginaVoiceMatcher
{
    /// <summary>Minimum cosine similarity for the best candidate to count as a real match.</summary>
    public const float DefaultScoreThreshold = 0.92f;

    /// <summary>Minimum lead over the runner-up — guards against two similar-contour lines.</summary>
    public const float DefaultMarginThreshold = 0.02f;

    /// <summary>A pre-fingerprinted clip (decoding is the caller's job, so this stays pure/testable).</summary>
    public readonly record struct Clip(string Name, float[] Fingerprint, double Seconds);

    /// <summary>
    /// Match each donor clip to its best <paramref name="xaBanks"/> candidate. Candidates are gated to a
    /// <paramref name="durationTolerance"/> band (default ±20%) before scoring — both a speed-up and a
    /// false-match guard. Deterministic for a given input.
    /// </summary>
    public static IReadOnlyList<FingerprintMatch> Match(
        IReadOnlyList<Clip> donorClips,
        IReadOnlyList<Clip> xaBanks,
        float scoreThreshold = DefaultScoreThreshold,
        float marginThreshold = DefaultMarginThreshold,
        double durationTolerance = 0.20)
    {
        var results = new List<FingerprintMatch>(donorClips.Count);
        foreach (var donor in donorClips)
        {
            float best = -1f, second = -1f;
            string? bestName = null;
            foreach (var bank in xaBanks)
            {
                if (donor.Seconds > 0 && bank.Seconds > 0)
                {
                    double ratio = bank.Seconds / donor.Seconds;
                    if (ratio < 1 - durationTolerance || ratio > 1 + durationTolerance) continue;
                }
                float sim = VoiceFingerprint.Similarity(donor.Fingerprint, bank.Fingerprint);
                if (sim > best) { second = best; best = sim; bestName = bank.Name; }
                else if (sim > second) { second = sim; }
            }

            float margin = best - MathF.Max(second, 0f);
            bool confident = bestName != null && best >= scoreThreshold && margin >= marginThreshold;
            results.Add(new FingerprintMatch(donor.Name, confident ? bestName : null, best, margin, confident));
        }
        return results;
    }

    /// <summary>Tally a match set into a <see cref="MatchReport"/> (confident count + xa-claim conflicts).</summary>
    public static MatchReport Summarize(IReadOnlyList<FingerprintMatch> matches)
    {
        int confident = 0;
        var claims = new Dictionary<string, int>();
        foreach (var m in matches)
            if (m.IsConfident && m.XaFile != null)
            {
                confident++;
                claims[m.XaFile] = claims.GetValueOrDefault(m.XaFile) + 1;
            }
        int conflicts = claims.Values.Count(c => c > 1);
        return new MatchReport(matches.Count, confident, conflicts);
    }

    /// <summary>A DC1 <c>xa*</c> bank's best Regina-donor match — the bank-centric labelling view.</summary>
    /// <param name="XaBank">The DC1 bank stem (e.g. <c>xa_ep09b</c>).</param>
    /// <param name="BestDonor">The best-matching Regina donor (e.g. <c>regina009</c>), or <c>null</c> if below threshold.</param>
    /// <param name="Score">Cosine similarity of the best donor.</param>
    /// <param name="IsRegina"><c>true</c> when <see cref="Score"/> clears the threshold ⇒ this bank is Regina's.</param>
    public readonly record struct BankLabel(string XaBank, string? BestDonor, float Score, bool IsRegina);

    /// <summary>
    /// <b>Bank-centric</b> labelling (the 1-to-many policy, docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §12.5/§12.6):
    /// for each <paramref name="xaBanks"/> entry find its best Regina <paramref name="donorClips"/> match
    /// and label it Regina when the score clears <paramref name="scoreThreshold"/> — <b>margin is
    /// ignored</b>. Unlike the donor-centric <see cref="Match"/>, this captures the duplicated alternate
    /// takes (a line living in two <c>…a</c>/<c>…b</c> banks labels <i>both</i>, since both are Regina),
    /// while the threshold still excludes the supporting cast (whose contours don't match Regina's).
    /// </summary>
    public static IReadOnlyList<BankLabel> MatchBanks(
        IReadOnlyList<Clip> xaBanks,
        IReadOnlyList<Clip> donorClips,
        float scoreThreshold = DefaultScoreThreshold,
        double durationTolerance = 0.20)
    {
        var results = new List<BankLabel>(xaBanks.Count);
        foreach (var bank in xaBanks)
        {
            float best = -1f;
            string? bestDonor = null;
            foreach (var donor in donorClips)
            {
                if (bank.Seconds > 0 && donor.Seconds > 0)
                {
                    double ratio = donor.Seconds / bank.Seconds;
                    if (ratio < 1 - durationTolerance || ratio > 1 + durationTolerance) continue;
                }
                float sim = VoiceFingerprint.Similarity(bank.Fingerprint, donor.Fingerprint);
                if (sim > best) { best = sim; bestDonor = donor.Name; }
            }

            bool isRegina = bestDonor != null && best >= scoreThreshold;
            results.Add(new BankLabel(bank.Name, isRegina ? bestDonor : null, best, isRegina));
        }
        return results;
    }
}
