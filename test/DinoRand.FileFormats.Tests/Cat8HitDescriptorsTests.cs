using System;
using System.IO;
using System.Linq;
using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for <see cref="Cat8HitDescriptors.Extract"/> — resolving the 10 cat-8 hit/death descriptor
/// records (defect B) from the canonical st605 RDT via the EXE's file-form table entries. Synthetic
/// tests always run; the real-data integration test is gated on <c>DINORAND_DC1_DIR</c>.
/// </summary>
public class Cat8HitDescriptorsTests
{
    private const uint Base = Cat8HitDescriptors.RdtPsxBase; // 0x80100000
    private static byte[] NewExe() => new byte[ExePatcher.FileBackedRvaHi];

    /// <summary>Build a host RDT and an EXE whose 10 table entries point at distinct, recognizable
    /// records inside it. Records are placed at file-form ptr <paramref name="ptrOf"/>(group, i).</summary>
    private static (byte[] exe, byte[] rdt, byte[] expected) BuildSynthetic(Func<int, int, uint> ptrOf)
    {
        int rec = ExePatcher.HitDescriptorRecordSize;
        var exe = NewExe();
        var rdt = new byte[0x1000];
        var expected = new byte[ExePatcher.HitDescriptorTotalRecords * rec];
        int w = 0;
        var tables = new[] { ExePatcher.Cat8HitTable17Va, ExePatcher.Cat8HitTable15Va };
        for (int g = 0; g < tables.Length; g++)
            for (int i = 0; i < ExePatcher.HitDescriptorIndexCount; i++)
            {
                uint ptr = ptrOf(g, i);
                ExePatcher.WriteUInt32AtVa(exe, tables[g] + (uint)i * 4, ptr);
                int off = (int)(ptr - Base);
                for (int b = 0; b < rec; b++)
                {
                    byte val = (byte)(0x40 + g * 0x50 + i * rec + b); // distinct per (g,i,b)
                    rdt[off + b] = val;
                    expected[w + b] = val;
                }
                w += rec;
            }
        return (exe, rdt, expected);
    }

    [Fact]
    public void Extract_ResolvesAllTenRecords_InTableOrder()
    {
        var (exe, rdt, expected) = BuildSynthetic((g, i) => Base + 0x100 + (uint)(g * 0x200 + i * 0x20));
        byte[] got = Cat8HitDescriptors.Extract(exe, rdt);
        Assert.Equal(expected, got);
        Assert.Equal(ExePatcher.HitDescriptorTotalRecords * ExePatcher.HitDescriptorRecordSize, got.Length);
    }

    [Fact]
    public void Extract_OutputFeedsRedirect_RoundTrip()
    {
        var (exe, rdt, _) = BuildSynthetic((g, i) => Base + 0x100 + (uint)(g * 0x200 + i * 0x20));
        byte[] records = Cat8HitDescriptors.Extract(exe, rdt);

        var target = NewExe();
        ExePatcher.RedirectCat8HitDescriptors(target, records); // must accept the extractor's output
        int caveOff = ExePatcher.VaToFileOffset(ExePatcher.HitDescriptorCaveVa);
        Assert.Equal(records, target.Skip(caveOff).Take(records.Length).ToArray());
    }

    [Fact]
    public void Extract_NonFileFormEntry_Throws()
    {
        var (exe, rdt, _) = BuildSynthetic((g, i) => Base + 0x100 + (uint)(g * 0x200 + i * 0x20));
        // Poison one entry with a code address (like the real table's index-5 entries).
        ExePatcher.WriteUInt32AtVa(exe, ExePatcher.Cat8HitTable15Va + 2 * 4, 0x0056DBC9u);
        Assert.Throws<InvalidDataException>(() => Cat8HitDescriptors.Extract(exe, rdt));
    }

    [Fact]
    public void Extract_EntryBeyondHostRdt_Throws()
    {
        var (exe, _, _) = BuildSynthetic((g, i) => Base + 0x100 + (uint)(g * 0x200 + i * 0x20));
        // A file-form ptr that resolves past a too-small host RDT (the st603/st612 failure mode).
        ExePatcher.WriteUInt32AtVa(exe, ExePatcher.Cat8HitTable15Va + 2 * 4, Base + 0x594a8);
        var tooSmall = new byte[0x4000];
        Assert.Throws<InvalidDataException>(() => Cat8HitDescriptors.Extract(exe, tooSmall));
    }

    // ---- gated real-data integration ----

    private static string? DataDir()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        foreach (var c in new[] { root, Path.Combine(root, "Data") })
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "st*.dat").Any())
                return c;
        return Directory.EnumerateDirectories(root, "Data", SearchOption.AllDirectories).FirstOrDefault();
    }

    [Fact]
    public void Extract_RealSt605_ProducesValidDeathDescriptor()
    {
        var dir = DataDir();
        if (dir is null) return; // gated: no install configured
        string st605 = Path.Combine(dir, "st605.dat");
        string exePath = GameInstaller.ExePath(dir);
        // Prefer the PRISTINE backup exe: a shipped install repoints the cat-8 descriptor tables (the corrected
        // defect-B fix, RedirectCat8HitReaction), so the live exe's entries are no longer file-form — the
        // superseded Extract path this test exercises needs the original file-form tables.
        var backup = Path.Combine(dir, GameInstaller.BackupDirName, GameInstaller.ExeName);
        if (File.Exists(backup)) exePath = backup;
        if (!File.Exists(st605) || !File.Exists(exePath)) return;

        byte[] exe = File.ReadAllBytes(exePath);
        byte[] rdt = RoomFile.Read(6, 5, File.ReadAllBytes(st605)).RdtBuffer;

        byte[] records = Cat8HitDescriptors.Extract(exe, rdt);

        Assert.Equal(ExePatcher.HitDescriptorTotalRecords * ExePatcher.HitDescriptorRecordSize, records.Length);
        // table15[2] (0x801594a8) is the confirmed death descriptor: a valid small death-state at +0x10.
        // Output order: table17[0..4] then table15[0..4] ⇒ table15[2] = record index 5+2 = 7.
        int rec = ExePatcher.HitDescriptorRecordSize;
        byte deathState = records[7 * rec + 0x10];
        Assert.True(deathState <= 6, $"death-state index 0x{deathState:X2} should be in the 7-entry table range");

        // The extractor's output must be installable.
        var target = NewExe();
        ExePatcher.RedirectCat8HitDescriptors(target, records);
    }
}
