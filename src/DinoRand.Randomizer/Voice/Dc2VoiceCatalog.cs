namespace DinoRand.Randomizer.Voice;

/// <summary>
/// One DC2 PC voice-bank entry on disk (<c>Speech\NNNN.dat</c>, rebirth build) with its decoded
/// RIFF/WAVE format — the DC2 twin of <see cref="Dc1VoiceFile"/> (docs/reference/dc2/voice/VOICE-DECODE-REPORT.md).
///
/// <para><b>Addressing is captured, semantics are not.</b> <see cref="Id"/> is the numeric bank id
/// (the exe loads by <c>Speech\%04d.DAT</c>); <see cref="IsDialogue"/> splits the mono dialogue banks
/// (ids &lt; 1000) from the stereo non-voice 1000-series. Which actor speaks each bank is undecoded
/// pending the human categorization pass (see the gate).</para>
/// </summary>
/// <param name="Path">Absolute path to the <c>.dat</c> file.</param>
/// <param name="Id">Numeric bank id (the filename stem).</param>
/// <param name="IsDialogue"><c>true</c> for a mono dialogue bank (id &lt; 1000); <c>false</c> for the stereo 1000-series.</param>
/// <param name="Channels">PCM channel count (dialogue is mono).</param>
/// <param name="SampleRate">PCM sample rate in Hz (dialogue is 18900).</param>
/// <param name="BitsPerSample">PCM bit depth (dialogue is 16).</param>
public readonly record struct Dc2VoiceFile(
    string Path, int Id, bool IsDialogue, int Channels, int SampleRate, int BitsPerSample);

/// <summary>
/// Reads the DC2 rebirth speech bank (<c>Speech\</c>) into a flat inventory of <see cref="Dc2VoiceFile"/>,
/// decoding each <c>.dat</c>'s RIFF/WAVE header — the DC2 twin of <see cref="Dc1VoiceCatalog"/>.
/// Pure inspection; never writes.
/// </summary>
public static class Dc2VoiceCatalog
{
    /// <summary>
    /// Locate the <c>Speech\</c> directory under a DC2 install (case-insensitively), or return
    /// <c>null</c> if absent. Accepts either the install root (holding <c>Dino2.exe</c>) or a path that
    /// already points at <c>Speech</c>.
    /// </summary>
    public static string? FindSpeechDir(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return null;

        if (string.Equals(Path.GetFileName(installDir), "Speech", StringComparison.OrdinalIgnoreCase))
            return installDir;
        return Directory.EnumerateDirectories(installDir)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Speech", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Enumerate every <c>NNNN.dat</c> bank under <paramref name="speechDir"/>, decoding its WAVE format.
    /// A file whose stem is not numeric or whose header doesn't parse as RIFF/WAVE PCM is skipped
    /// (defensive). Ordered by id for reproducibility.
    /// </summary>
    public static IReadOnlyList<Dc2VoiceFile> Enumerate(string speechDir)
    {
        var result = new List<Dc2VoiceFile>();
        if (!Directory.Exists(speechDir)) return result;

        foreach (var path in Directory.EnumerateFiles(speechDir, "*.dat").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) continue;
            var fmt = Dc1VoiceCatalog.TryReadFormat(path);
            if (fmt is not var (channels, sampleRate, bits)) continue;

            result.Add(new Dc2VoiceFile(path, id, IsDialogue: id < 1000, channels, sampleRate, bits));
        }
        return result;
    }
}
