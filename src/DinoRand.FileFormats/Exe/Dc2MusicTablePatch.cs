using System.Buffers.Binary;
using System.Text;

namespace DinoRand.FileFormats.Exe;

/// <summary>
/// The DC2 BGM randomizer lever (docs/decisions/dc2/audio/DC2-BGM-RANDO-PLAN.md): permutes the music slice of
/// <c>Dino2.exe</c>'s Data filename pointer table.
///
/// <para><b>Surface.</b> The exe holds a file-id-indexed <c>char*</c> filename table at VA
/// <c>0x71B230</c> (<c>.data</c>, file <c>0x100230</c>); the music loader at <c>0x404280</c>
/// resolves the current music file-id (<c>[0x878FA8] &amp; 0xFFFF</c>) through it into a
/// <c>data\%s</c> open. Slots 150..217 are the 68 <c>ME_/MF_/MS_*.DAT</c> music-stream
/// containers — so permuting these pointers reroutes whatever id the game requests, without
/// decoding the per-scene selection (the same argument as DC1's <c>--shuffle-bgm</c>,
/// <see cref="ExePatcher.ShuffleBgmCatalog"/>). Byte-cites validated by
/// <c>tools/dc2_re/ms_bgm_probe.py</c>.</para>
///
/// <para><b>Like-class constraint.</b> Each container carries 1..4 tracks keyed by an internal
/// track index, and the game requests (file-id, track-index); a swap is only safe between files
/// exposing the identical track-index set. The caller supplies that grouping
/// (<see cref="Dc2MusicContainer.ReadTrackIndexKey"/> derives it from the <c>Data\</c> files);
/// names without a class (vestigial table entries with no file on disk: ME_2000/MS_0402/MS_0501,
/// or singleton classes) never move.</para>
///
/// <para><b>Non-compounding.</b> The shuffle writes a permutation of the <i>canonical</i>
/// pointer values (each filename's string VA, located by unique search — the strings themselves
/// never move), so applying it to an already-shuffled exe yields the same result as applying it
/// to a pristine one, and <see cref="RestoreCanonical"/> reverts the slice without touching any
/// other patch (WP-gate etc.). Mirrors <see cref="Dc2WpGatePatch"/>'s safety stance: refuses
/// anything that is not the recognized build.</para>
/// </summary>
public static class Dc2MusicTablePatch
{
    /// <summary>File offset of table slot 0 (VA <c>0x71B230</c>; <c>.data</c> raw <c>0x100000</c> = VA <c>0x71B000</c>).</summary>
    public const int TableBaseOffset = 0x100230;

    /// <summary><c>.data</c> raw file offset / VA / raw size — the window filename strings live in.</summary>
    public const int DataSectionOffset = 0x100000;
    public const uint DataSectionVa = 0x71B000;
    public const int DataSectionSize = 0x25000;

    /// <summary>First / last table slot of the music slice (contiguous, probe-validated).</summary>
    public const int MusicFirstSlot = 150;
    public const int MusicLastSlot = 217;
    public const int MusicSlotCount = MusicLastSlot - MusicFirstSlot + 1;

    /// <summary>Canonical slice contents, slot 150 first — the recognized build's music filenames
    /// in table order. Any exe whose slice is not a permutation of exactly this set is refused.</summary>
    public static readonly string[] CanonicalNames =
    {
        "ME_0300.DAT", "ME_0800.DAT", "ME_0900.DAT", "ME_0A00.DAT", "ME_0B00.DAT", "ME_0C00.DAT",
        "ME_0D00.DAT", "ME_1200.DAT", "ME_1300.DAT", "ME_1500.DAT", "ME_1600.DAT", "ME_1700.DAT",
        "ME_2000.DAT", "ME_2100.DAT", "ME_2200.DAT", "MF_0200.DAT", "MF_0201.DAT", "MF_0202.DAT",
        "MF_02_00.DAT", "MF_02_01.DAT", "MF_0300.DAT", "MF_0400.DAT", "MF_0500.DAT", "MF_0600.DAT",
        "MF_0700.DAT", "MF_0A00.DAT", "MF_0A01.DAT", "MF_0A02.DAT", "MF_0A_00.DAT", "MF_0A_01.DAT",
        "MS_0000.DAT", "MS_0001.DAT", "MS_0002.DAT", "MS_0003.DAT", "MS_0004.DAT", "MS_0005.DAT",
        "MS_0006.DAT", "MS_00_01.DAT", "MS_00_02.DAT", "MS_00_03.DAT", "MS_00_04.DAT", "MS_00_05.DAT",
        "MS_00_06.DAT", "MS_0100.DAT", "MS_0300.DAT", "MS_0301.DAT", "MS_0302.DAT", "MS_0303.DAT",
        "MS_03_00.DAT", "MS_03_01.DAT", "MS_03_02.DAT", "MS_03_03.DAT", "MS_0400.DAT", "MS_0401.DAT",
        "MS_0402.DAT", "MS_04_00.DAT", "MS_04_01.DAT", "MS_0500.DAT", "MS_0501.DAT", "MS_0600.DAT",
        "MS_0800.DAT", "MS_0801.DAT", "MS_08_00.DAT", "MS_08_01.DAT", "MS_0900.DAT", "MS_0901.DAT",
        "MS_09_00.DAT", "MS_09_01.DAT",
    };

    /// <summary>One slice slot before/after a shuffle. <see cref="OldName"/> == <see cref="NewName"/>
    /// for a slot the permutation left in place.</summary>
    public readonly record struct MusicShuffleEntry(int Slot, string OldName, string NewName);

    /// <summary>
    /// Shuffle the music slice: for each like-class (names mapping to the same
    /// <paramref name="classOf"/> value), permute which slot points at which filename, keyed by
    /// <paramref name="seed"/> (deterministic — splitmix32 Fisher–Yates, classes in ordinal key
    /// order, members in slot order). Names absent from <paramref name="classOf"/> stay in place.
    /// Validates first via <see cref="Validate"/> and writes nothing on failure. Returns one entry
    /// per slot (ascending) for the manifest/spoiler.
    /// </summary>
    public static MusicShuffleEntry[] Shuffle(byte[] exe, int seed, IReadOnlyDictionary<string, string> classOf)
    {
        ArgumentNullException.ThrowIfNull(exe);
        ArgumentNullException.ThrowIfNull(classOf);
        Validate(exe);

        // The permutation is computed over the CANONICAL slot->name assignment (not the exe's
        // current one), so re-applying with a new seed never compounds.
        var assigned = (string[])CanonicalNames.Clone();
        uint rng = (uint)seed;
        foreach (string cls in classOf.Values.Distinct().OrderBy(c => c, StringComparer.Ordinal))
        {
            var members = new List<int>();
            for (int k = 0; k < MusicSlotCount; k++)
                if (classOf.TryGetValue(CanonicalNames[k], out var c) && c == cls)
                    members.Add(k);
            int m = members.Count;
            var perm = new int[m];
            for (int i = 0; i < m; i++) perm[i] = i;
            for (int i = m - 1; i > 0; i--)
            {
                int j = (int)(NextRand(ref rng) % (uint)(i + 1));
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }
            for (int i = 0; i < m; i++)
                assigned[members[i]] = CanonicalNames[members[perm[i]]];
        }

        return ApplyPlan(exe, assigned);
    }

    /// <summary>Validate and apply an explicit slot-to-name plan produced by the Randomizer layer.</summary>
    internal static MusicShuffleEntry[] ApplyPlan(byte[] exe, IReadOnlyList<string> assigned)
    {
        ArgumentNullException.ThrowIfNull(exe);
        ArgumentNullException.ThrowIfNull(assigned);
        Validate(exe);
        if (assigned.Count != MusicSlotCount)
            throw new ArgumentException($"music plan has {assigned.Count} slots; expected {MusicSlotCount}.", nameof(assigned));
        if (!assigned.OrderBy(n => n, StringComparer.Ordinal)
                     .SequenceEqual(CanonicalNames.OrderBy(n => n, StringComparer.Ordinal)))
            throw new ArgumentException("music plan is not a permutation of the canonical filename set.", nameof(assigned));

        var result = new MusicShuffleEntry[MusicSlotCount];
        for (int k = 0; k < MusicSlotCount; k++)
        {
            WritePointer(exe, MusicFirstSlot + k, FindStringVa(exe, assigned[k]));
            result[k] = new MusicShuffleEntry(MusicFirstSlot + k, CanonicalNames[k], assigned[k]);
        }
        return result;
    }

    /// <summary>Rewrite the slice to the canonical assignment (the un-shuffle; leaves every other
    /// byte — and any other patch — untouched). Validates first; no-op on a pristine slice.</summary>
    public static void RestoreCanonical(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        Validate(exe);
        for (int k = 0; k < MusicSlotCount; k++)
            WritePointer(exe, MusicFirstSlot + k, FindStringVa(exe, CanonicalNames[k]));
    }

    /// <summary>True iff every slice slot points at its canonical filename (no shuffle applied).</summary>
    public static bool IsCanonical(byte[] exe)
    {
        Validate(exe);
        for (int k = 0; k < MusicSlotCount; k++)
            if (ReadName(exe, MusicFirstSlot + k) != CanonicalNames[k])
                return false;
        return true;
    }

    /// <summary>
    /// Throw <see cref="InvalidOperationException"/> unless <paramref name="exe"/> is the
    /// recognized build (exact length, <see cref="Dc2WpGatePatch.ExpectedLength"/>) whose music
    /// slice is a permutation of <see cref="CanonicalNames"/> — i.e. pristine or previously
    /// shuffled by this patch, never anything else.
    /// </summary>
    public static void Validate(byte[] exe)
    {
        if (exe.Length != Dc2WpGatePatch.ExpectedLength)
            throw new InvalidOperationException(
                $"Dino2.exe has unexpected length {exe.Length} (expected {Dc2WpGatePatch.ExpectedLength}) — unrecognized build; refusing to touch the music table.");
        var seen = new List<string>(MusicSlotCount);
        for (int k = 0; k < MusicSlotCount; k++)
        {
            string? name = ReadName(exe, MusicFirstSlot + k);
            if (name is null)
                throw new InvalidOperationException(
                    $"music table slot {MusicFirstSlot + k} does not point at a string in .data — unrecognized build; refusing to touch the music table.");
            seen.Add(name);
        }
        if (!seen.OrderBy(n => n, StringComparer.Ordinal)
                 .SequenceEqual(CanonicalNames.OrderBy(n => n, StringComparer.Ordinal)))
            throw new InvalidOperationException(
                "music table slice is not a permutation of the recognized filename set — unrecognized build; refusing to touch the music table.");
    }

    /// <summary>Resolve the filename a table slot currently points at (null if unresolvable).</summary>
    public static string? ReadName(byte[] exe, int slot)
    {
        uint va = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(TableBaseOffset + slot * 4, 4));
        if (va < DataSectionVa || va >= DataSectionVa + DataSectionSize) return null;
        int off = DataSectionOffset + (int)(va - DataSectionVa);
        int end = Array.IndexOf(exe, (byte)0, off);
        if (end < 0 || end == off || end - off > 64) return null;
        for (int i = off; i < end; i++)
            if (exe[i] < 0x20 || exe[i] > 0x7E) return null;
        return Encoding.ASCII.GetString(exe, off, end - off);
    }

    private static void WritePointer(byte[] exe, int slot, uint va)
        => BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(TableBaseOffset + slot * 4, 4), va);

    /// <summary>VA of <paramref name="name"/>'s NUL-bounded string in <c>.data</c>. The strings
    /// never move (only the pointers permute), and the probe verifies each occurs exactly once.</summary>
    internal static uint FindStringVa(byte[] exe, string name)
    {
        byte[] needle = Encoding.ASCII.GetBytes("\0" + name + "\0");
        int idx = exe.AsSpan(DataSectionOffset, DataSectionSize).IndexOf(needle);
        if (idx < 0)
            throw new InvalidOperationException($"music filename string '{name}' not found in .data.");
        return DataSectionVa + (uint)idx + 1; // +1: skip the leading NUL bound
    }

    /// <summary>splitmix32 — same deterministic PRNG as <see cref="ExePatcher.ShuffleBgmCatalog"/>.</summary>
    private static uint NextRand(ref uint state)
    {
        state += 0x9E3779B9u;
        uint z = state;
        z = (z ^ (z >> 16)) * 0x21F0AAADu;
        z = (z ^ (z >> 15)) * 0x735A2D97u;
        return z ^ (z >> 15);
    }
}

/// <summary>
/// Minimal reader for the DC2 music-stream container header (<c>Data\M[SEF]_*.DAT</c>): one
/// 0x800 header sector of 32-byte records <c>{u32 4, u32 payloadBytes, u32 trackIndex,
/// u32 flag(0|1), pad16}</c> ('dummy header ' filler after the last), then the payloads, each
/// 2048-aligned. Only the track-index set is needed — it is the like-class key for
/// <see cref="Dc2MusicTablePatch.Shuffle"/>.
/// </summary>
public static class Dc2MusicContainer
{
    /// <summary>
    /// Read the container's track-index set as a class key (e.g. <c>"0,1,2"</c>), or null when
    /// the header does not match the decoded format (such a file is never shuffled).
    /// </summary>
    public static string? ReadTrackIndexKey(string path)
    {
        using var f = File.OpenRead(path);
        Span<byte> header = stackalloc byte[0x800];
        if (f.Read(header) != header.Length) return null;
        var idxs = new List<uint>();
        long total = 0x800;
        for (int off = 0; off + 32 <= header.Length; off += 32)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header[off..]);
            if (magic != 4) break;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 4)..]);
            uint tidx = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 8)..]);
            uint flag = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 12)..]);
            if (flag > 1 || header.Slice(off + 16, 16).IndexOfAnyExcept((byte)0) >= 0) return null;
            idxs.Add(tidx);
            total += (size + 2047) & ~2047L;
        }
        if (idxs.Count == 0 || total != f.Length) return null;
        return string.Join(",", idxs.Order());
    }

    // ── Full read/write (payload framing) ────────────────────────────────────────────────────────
    // The whole-container model for DC2 export/import. Framing (byte-validated, docs/decisions/dc2/audio/
    // DC2-BGM-IMPORT-FEASIBILITY.md): the 0x800 header sector is 64 x 32-byte slots — N real records
    // {u32 4, payloadBytes, trackIndex, flag, 16x0}, the rest each "dummy header    " + 16x0 — then the N
    // payloads, each 2048-aligned (zero tail-pad). Each payload is a standard MPEG-1 L3 (MP3) stream: the
    // "[OPEN] codec" was Classic REbirth's re-encode to MP3, so there is nothing proprietary to decode.

    private const int HeaderSectorSize = 0x800;
    private const int RecordSize = 32;
    private const int PayloadAlign = 2048;
    private const int MaxTracks = HeaderSectorSize / RecordSize; // 64 slots
    private static readonly byte[] DummySlotMagic = Encoding.ASCII.GetBytes("dummy header    "); // 16 bytes

    /// <summary>Read a whole container into its ordered tracks (each = its header record fields + the raw
    /// MP3 payload). Inverse of <see cref="WriteTracks"/>. Throws on a container that is not the decoded
    /// framing.</summary>
    public static IReadOnlyList<Dc2MusicTrack> ReadTracks(byte[] container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (container.Length < HeaderSectorSize)
            throw new InvalidOperationException("DC2 music container shorter than its 0x800 header sector.");

        var recs = new List<(uint Size, int TrackIndex, uint Flag)>();
        for (int off = 0; off + RecordSize <= HeaderSectorSize; off += RecordSize)
        {
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(off));
            if (magic != 4) break; // reached the "dummy header" filler slots
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(off + 4));
            uint tidx = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(off + 8));
            uint flag = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(off + 12));
            recs.Add((size, (int)tidx, flag));
        }
        if (recs.Count == 0)
            throw new InvalidOperationException("DC2 music container has no track records (magic 4).");

        var tracks = new Dc2MusicTrack[recs.Count];
        int payloadOff = HeaderSectorSize;
        for (int i = 0; i < recs.Count; i++)
        {
            var (size, tidx, flag) = recs[i];
            if (payloadOff + size > container.Length)
                throw new InvalidOperationException($"DC2 music container track {i} payload runs past end of file.");
            tracks[i] = new Dc2MusicTrack(tidx, flag, container.AsSpan(payloadOff, (int)size).ToArray());
            payloadOff += Align(size);
        }
        return tracks;
    }

    /// <summary>Reassemble a container (0x800 header sector + 2048-aligned payloads) from
    /// <paramref name="tracks"/>. Round-trips byte-identically for tracks read unmodified.</summary>
    public static byte[] WriteTracks(IReadOnlyList<Dc2MusicTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0 || tracks.Count > MaxTracks)
            throw new InvalidOperationException($"DC2 music container needs 1..{MaxTracks} tracks (got {tracks.Count}).");

        long total = HeaderSectorSize;
        foreach (var t in tracks) total += Align((uint)t.Payload.Length);
        var outBuf = new byte[total];

        int payloadOff = HeaderSectorSize;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            int rec = i * RecordSize;
            BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(rec), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(rec + 4), (uint)t.Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(rec + 8), (uint)t.TrackIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(rec + 12), t.Flag);
            // pad[16] stays zero
            t.Payload.CopyTo(outBuf.AsSpan(payloadOff));
            payloadOff += Align((uint)t.Payload.Length);
        }
        // Fill the remaining header slots with the "dummy header    " + 16x0 filler the game writes.
        for (int off = tracks.Count * RecordSize; off + RecordSize <= HeaderSectorSize; off += RecordSize)
            DummySlotMagic.CopyTo(outBuf.AsSpan(off));
        return outBuf;
    }

    private static int Align(uint n) => (int)((n + (PayloadAlign - 1)) & ~(uint)(PayloadAlign - 1));
}

/// <summary>One track inside a DC2 music container: its bank slot index, the per-track flag (0/1, a
/// loop/stream marker — preserved for a byte-faithful rewrite), and the raw MP3 payload bytes.</summary>
public readonly record struct Dc2MusicTrack(int TrackIndex, uint Flag, byte[] Payload);

/// <summary>
/// The DC2 music-payload audio codec. Classic REbirth re-encoded the PSX streams to <b>standard MPEG-1
/// Layer III (MP3)</b> — 141/141 corpus payloads are clean CBR MP3 (docs/decisions/dc2/audio/
/// DC2-BGM-IMPORT-FEASIBILITY.md), so a payload IS a playable <c>.mp3</c>; there is no proprietary codec.
/// Decode/encode to PCM (for transcoding foreign ogg/wav donors on import) shells out to <c>ffmpeg</c>
/// on PATH — no new managed dependency (mirrors <see cref="Dc2MusicContainer"/> staying format-only).
/// </summary>
public static class Dc2MusicCodec
{
    /// <summary>Sample rate / channel count the game's MP3 streams use — import conforms to these.</summary>
    public const int GameSampleRate = 44100;
    public const int GameChannels = 2;

    /// <summary>True if <c>ffmpeg</c> is invokable on PATH (import/PCM-transcode requires it; raw MP3
    /// export and passthrough import do not). Callers skip the ffmpeg path when false.</summary>
    public static bool FfmpegAvailable => _ffmpegAvailable ??= ProbeFfmpeg();
    private static bool? _ffmpegAvailable;

    /// <summary>Decode an MP3 payload to interleaved 16-bit-range float PCM at the game format
    /// (<see cref="GameChannels"/> ch, <see cref="GameSampleRate"/> Hz). Requires ffmpeg.</summary>
    public static (float[] Interleaved, int Channels, int SampleRate) DecodePayload(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] pcm = RunFfmpeg(
            $"-hide_banner -loglevel error -i pipe:0 -f f32le -acodec pcm_f32le -ac {GameChannels} -ar {GameSampleRate} pipe:1",
            payload);
        var samples = new float[pcm.Length / 4];
        Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 4);
        return (samples, GameChannels, GameSampleRate);
    }

    /// <summary>Encode interleaved float PCM into an MP3 container payload (inverse of
    /// <see cref="DecodePayload"/>, lossy) at the game format. Requires ffmpeg.</summary>
    public static byte[] EncodePayload(float[] interleaved, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        var pcm = new byte[interleaved.Length * 4];
        Buffer.BlockCopy(interleaved, 0, pcm, 0, pcm.Length);
        return RunFfmpeg(
            $"-hide_banner -loglevel error -f f32le -ac {channels} -ar {sampleRate} -i pipe:0 " +
            $"-f mp3 -codec:a libmp3lame -b:a 128k -ar {GameSampleRate} -ac {GameChannels} pipe:1",
            pcm);
    }

    private static bool ProbeFfmpeg()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "ffmpeg", "-hide_banner -version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is null) return false;
            p.WaitForExit(5000);
            return true;
        }
        catch { return false; }
    }

    // Feed `stdin` to ffmpeg and return its stdout. Writes stdin on a background task to avoid the
    // classic pipe deadlock when both streams exceed a pipe buffer.
    private static byte[] RunFfmpeg(string args, byte[] stdin)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg not found on PATH — required for DC2 music transcode.");

        var feeder = Task.Run(() =>
        {
            using var s = p.StandardInput.BaseStream;
            s.Write(stdin, 0, stdin.Length);
        });
        using var outMs = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(outMs);
        string err = p.StandardError.ReadToEnd();
        feeder.Wait();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed (exit {p.ExitCode}): {err}");
        return outMs.ToArray();
    }
}
