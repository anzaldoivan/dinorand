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
}
