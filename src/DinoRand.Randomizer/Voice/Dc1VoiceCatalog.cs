namespace DinoRand.Randomizer.Voice;

/// <summary>
/// One DC1 PC voice-bank entry on disk (<c>Sound\VOICE\*.dat</c>) with its decoded RIFF/WAVE format.
/// The on-disk inventory the eventual slot map (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §7 R1) is built over.
///
/// <para><b>Addressing is captured, semantics are not.</b> <see cref="Name"/> is the raw stem
/// (e.g. <c>xa10200</c>, <c>xa_op02a</c>, <c>se20200</c>); <see cref="IsDialogue"/> splits the
/// cutscene-voice <c>xa*</c> banks from the <c>se*</c> sound-effect banks. The <c>xa</c> code's
/// stage/room/line decomposition and — critically — <b>which actor speaks each line</b> remain
/// undecoded (no validated source exists; see the gate). So no <see cref="Definitions.VoiceActor"/> is
/// asserted here.</para>
/// </summary>
/// <param name="Path">Absolute path to the <c>.dat</c> file.</param>
/// <param name="Name">Filename stem without extension (the raw addressing code).</param>
/// <param name="IsDialogue"><c>true</c> for an <c>xa*</c> cutscene-voice bank; <c>false</c> for an <c>se*</c> SFX bank.</param>
/// <param name="Channels">PCM channel count (dialogue is mono, SE is stereo).</param>
/// <param name="SampleRate">PCM sample rate in Hz.</param>
/// <param name="BitsPerSample">PCM bit depth.</param>
public readonly record struct Dc1VoiceFile(
    string Path, string Name, bool IsDialogue, int Channels, int SampleRate, int BitsPerSample);

/// <summary>
/// Reads the DC1 PC voice bank (<c>Sound\VOICE\</c>) into a flat inventory of <see cref="Dc1VoiceFile"/>,
/// decoding each <c>.dat</c>'s RIFF/WAVE header (docs/decisions/dc1/voice/VOICE-RANDO-PLAN.md §4). Pure inspection — it
/// never writes, and the audio bytes are read only enough to parse the format header. This is the
/// foundation the voice-slot manifest is authored over once actor labelling is solved (§7 R1).
/// </summary>
public static class Dc1VoiceCatalog
{
    /// <summary>
    /// Locate the <c>Sound\VOICE\</c> directory under a DC1 install (case-insensitively), or return
    /// <c>null</c> if absent. Accepts either the install root (holding <c>DINO.exe</c>) or a path that
    /// already points at <c>Sound</c>/<c>VOICE</c>.
    /// </summary>
    public static string? FindVoiceDir(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return null;

        foreach (var candidate in new[]
                 {
                     installDir,
                     Path.Combine(installDir, "VOICE"),
                     Path.Combine(installDir, "Sound", "VOICE"),
                 })
            if (Directory.Exists(candidate) &&
                string.Equals(Path.GetFileName(candidate), "VOICE", StringComparison.OrdinalIgnoreCase))
                return candidate;

        // Fall back to a case-insensitive walk of Sound\VOICE under the install root.
        var sound = Directory.EnumerateDirectories(installDir)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Sound", StringComparison.OrdinalIgnoreCase));
        if (sound == null) return null;
        return Directory.EnumerateDirectories(sound)
            .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "VOICE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Enumerate every <c>.dat</c> bank under <paramref name="voiceDir"/>, decoding its WAVE format.
    /// A file whose header doesn't parse as RIFF/WAVE PCM is skipped (the dir holds only audio banks,
    /// so this is defensive). Ordered by name for reproducibility.
    /// </summary>
    public static IReadOnlyList<Dc1VoiceFile> Enumerate(string voiceDir)
    {
        var result = new List<Dc1VoiceFile>();
        if (!Directory.Exists(voiceDir)) return result;

        foreach (var path in Directory.EnumerateFiles(voiceDir, "*.dat").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fmt = TryReadFormat(path);
            if (fmt is not var (channels, sampleRate, bits)) continue;

            var name = Path.GetFileNameWithoutExtension(path);
            var isDialogue = name.StartsWith("xa", StringComparison.OrdinalIgnoreCase);
            result.Add(new Dc1VoiceFile(path, name, isDialogue, channels, sampleRate, bits));
        }
        return result;
    }

    /// <summary>Read just the <c>fmt </c> chunk (channels, rate, bits) from a RIFF/WAVE file head.
    /// Shared with <see cref="Dc2VoiceCatalog"/> (DC2 speech banks are the same RIFF-renamed-.dat trick).</summary>
    internal static (int Channels, int SampleRate, int Bits)? TryReadFormat(string path)
    {
        Span<byte> head = stackalloc byte[64];
        int n;
        using (var fs = File.OpenRead(path)) n = fs.Read(head);
        if (n < 36) return null;

        if (head[0] != (byte)'R' || head[1] != (byte)'I' || head[2] != (byte)'F' || head[3] != (byte)'F' ||
            head[8] != (byte)'W' || head[9] != (byte)'A' || head[10] != (byte)'V' || head[11] != (byte)'E')
            return null;

        // Standard layout: 'fmt ' chunk begins at offset 12.
        if (head[12] != (byte)'f' || head[13] != (byte)'m' || head[14] != (byte)'t' || head[15] != (byte)' ')
            return null;

        int channels = BitConverter.ToUInt16(head.Slice(22, 2));
        int sampleRate = BitConverter.ToInt32(head.Slice(24, 4));
        int bits = BitConverter.ToUInt16(head.Slice(34, 2));
        return (channels, sampleRate, bits);
    }
}
