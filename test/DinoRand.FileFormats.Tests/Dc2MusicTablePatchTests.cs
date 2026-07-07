using System.Buffers.Binary;
using System.Text;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Dc2MusicTablePatch (docs/dc2/DC2-BGM-RANDO-PLAN.md I2/I4) on a synthetic exe image: the real
/// build's length, table offset and .data window, with the canonical filename strings laid out in
/// .data and the slice pointers wired to them — no game files in the repo.
/// </summary>
public class Dc2MusicTablePatchTests
{
    /// <summary>Build a recognizable synthetic exe: canonical strings packed after the table,
    /// each slice slot pointing at its canonical name.</summary>
    private static byte[] MakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        int strOff = Dc2MusicTablePatch.TableBaseOffset + 0x1000; // inside .data, clear of the table
        foreach (string name in Dc2MusicTablePatch.CanonicalNames)
        {
            byte[] b = Encoding.ASCII.GetBytes(name);
            b.CopyTo(exe, strOff + 1); // leading byte stays 0 => NUL-bounded
            uint va = Dc2MusicTablePatch.DataSectionVa
                      + (uint)(strOff + 1 - Dc2MusicTablePatch.DataSectionOffset);
            int slot = Dc2MusicTablePatch.MusicFirstSlot
                       + Array.IndexOf(Dc2MusicTablePatch.CanonicalNames, name);
            BinaryPrimitives.WriteUInt32LittleEndian(
                exe.AsSpan(Dc2MusicTablePatch.TableBaseOffset + slot * 4, 4), va);
            strOff += 1 + b.Length + 1;
        }
        return exe;
    }

    /// <summary>Two 3-member classes + everything else unclassed (stays put).</summary>
    private static Dictionary<string, string> Classes() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["MS_0001.DAT"] = "0,1,2,3", ["MS_0002.DAT"] = "0,1,2,3", ["MS_0003.DAT"] = "0,1,2,3",
        ["ME_0300.DAT"] = "0,1,2", ["ME_1500.DAT"] = "0,1,2", ["ME_1600.DAT"] = "0,1,2",
    };

    private static string[] SliceNames(byte[] exe)
        => Enumerable.Range(Dc2MusicTablePatch.MusicFirstSlot, Dc2MusicTablePatch.MusicSlotCount)
                     .Select(s => Dc2MusicTablePatch.ReadName(exe, s)!).ToArray();

    [Fact]
    public void SyntheticExe_IsCanonical()
    {
        var exe = MakeExe();
        Assert.True(Dc2MusicTablePatch.IsCanonical(exe));
        Assert.Equal(Dc2MusicTablePatch.CanonicalNames, SliceNames(exe));
    }

    [Fact]
    public void Shuffle_PermutesOnlyWithinClasses_AndReportsMoves()
    {
        var exe = MakeExe();
        var entries = Dc2MusicTablePatch.Shuffle(exe, seed: 1234, Classes());

        var names = SliceNames(exe);
        for (int k = 0; k < names.Length; k++)
        {
            string canon = Dc2MusicTablePatch.CanonicalNames[k];
            if (!Classes().TryGetValue(canon, out var cls))
                Assert.Equal(canon, names[k]); // unclassed slots never move
            else
                Assert.Equal(cls, Classes()[names[k]]); // moved slots stay in-class
        }
        // slice is still a permutation of the canonical set
        Assert.Equal(Dc2MusicTablePatch.CanonicalNames.Order(), names.Order());
        // report matches the bytes
        foreach (var e in entries)
            Assert.Equal(e.NewName, names[e.Slot - Dc2MusicTablePatch.MusicFirstSlot]);
    }

    [Fact]
    public void Shuffle_IsDeterministic_And_NonCompounding()
    {
        var a = MakeExe();
        var b = MakeExe();
        Dc2MusicTablePatch.Shuffle(a, 42, Classes());
        Dc2MusicTablePatch.Shuffle(b, 7, Classes());  // different first shuffle...
        Dc2MusicTablePatch.Shuffle(b, 42, Classes()); // ...re-seeding lands on the same bytes
        Assert.Equal(a, b);
    }

    [Fact]
    public void RestoreCanonical_IsByteExact()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        Dc2MusicTablePatch.Shuffle(exe, 99, Classes());
        Assert.NotEqual(pristine, exe);
        Dc2MusicTablePatch.RestoreCanonical(exe);
        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void Validate_RefusesWrongLengthAndBrokenSlice()
    {
        Assert.Throws<InvalidOperationException>(() => Dc2MusicTablePatch.Validate(new byte[100]));

        var exe = MakeExe();
        // point one slot at a non-music string -> not a permutation of the canonical set
        BinaryPrimitives.WriteUInt32LittleEndian(
            exe.AsSpan(Dc2MusicTablePatch.TableBaseOffset + Dc2MusicTablePatch.MusicFirstSlot * 4, 4), 0);
        Assert.Throws<InvalidOperationException>(() => Dc2MusicTablePatch.Validate(exe));
    }

    [Fact]
    public void Container_ReadTrackIndexKey_ParsesAndRejects()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dinorand-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // valid: 2 tracks (indices 1,3), payloads 2048-aligned, dummy-header filler
            string ok = Path.Combine(dir, "MS_TEST.DAT");
            var header = new byte[0x800];
            void Rec(int i, uint size, uint tidx, uint flag)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 32), 4);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 32 + 4), size);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 32 + 8), tidx);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 32 + 12), flag);
            }
            Rec(0, 100, 3, 1);
            Rec(1, 3000, 1, 0);
            Encoding.ASCII.GetBytes("dummy header    ").CopyTo(header, 2 * 32);
            File.WriteAllBytes(ok, header.Concat(new byte[2048 + 4096]).ToArray());
            Assert.Equal("1,3", Dc2MusicContainer.ReadTrackIndexKey(ok));

            // invalid: truncated body
            string bad = Path.Combine(dir, "MS_BAD.DAT");
            File.WriteAllBytes(bad, header.Concat(new byte[100]).ToArray());
            Assert.Null(Dc2MusicContainer.ReadTrackIndexKey(bad));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
