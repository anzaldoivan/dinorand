using System.Linq;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Voice;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The shared pre-gate voice AUDITION service (docs/dc1/VOICE-UI-PLAN.md): donor/target/transcode
/// resolution used by both the CLI <c>--voice-preview</c> and the App's "Preview in game" button. The
/// disk-touching <c>Install</c> wrapper is exercised by the CLI/App; here we test the pure parts (Plan,
/// ResolveDonor, Transcode → DC1 bytes) against the real in-repo packs.
/// </summary>
public class VoicePreviewTests
{
    private static string PacksRoot => Path.Combine(RepoRoot(), "biorand", "datapacks");

    // A two-bank Regina target manifest (no install dependency), mirroring VoiceEmissionTests.
    private static Dc1VoiceManifest TwoBankManifest() => Dc1VoiceManifest.Parse("""
        {
          "Sound/VOICE/xa10501.dat": { "actor": "regina" },
          "Sound/VOICE/xa10503.dat": { "actor": "regina" }
        }
        """);

    private static System.Collections.Generic.List<VoiceClipSource> CrossGamePool() =>
        VoiceDataPack.LoadAll(PacksRoot).Where(c => !c.IsNativeDc1).ToList();

    private static string FirstDonor(System.Collections.Generic.List<VoiceClipSource> pool) =>
        pool.Select(c => c.Actor).Distinct(System.StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, System.StringComparer.Ordinal).First();

    [Fact]
    public void Plan_DefaultBank_ReturnsOneWrite_ForResolvedDonor()
    {
        if (!Directory.Exists(PacksRoot)) return;
        var pool = CrossGamePool();
        if (pool.Count == 0) return;
        var donor = FirstDonor(pool);

        var writes = VoicePreview.Plan(pool, donor, allBanks: false, bankStem: null,
            new System.Random(1), TwoBankManifest());

        Assert.Single(writes);
        Assert.Equal(donor, writes[0].Donor.Actor, ignoreCase: true);
    }

    [Fact]
    public void Plan_AllBanks_TargetsEveryBank()
    {
        if (!Directory.Exists(PacksRoot)) return;
        var pool = CrossGamePool();
        if (pool.Count == 0) return;

        var writes = VoicePreview.Plan(pool, FirstDonor(pool), allBanks: true, bankStem: null,
            new System.Random(2), TwoBankManifest());

        Assert.Equal(2, writes.Count);
    }

    [Fact]
    public void Plan_UnknownDonor_Throws()
    {
        if (!Directory.Exists(PacksRoot)) return;
        var pool = CrossGamePool();
        if (pool.Count == 0) return;

        var ex = Assert.Throws<VoicePreviewException>(() =>
            VoicePreview.Plan(pool, "nobody_xyz", allBanks: false, bankStem: null,
                new System.Random(3), TwoBankManifest()));
        Assert.Contains("nobody_xyz", ex.Message);
    }

    [Fact]
    public void ResolveDonor_Explicit_IsLowerCased_Random_FromPool()
    {
        if (!Directory.Exists(PacksRoot)) return;
        var pool = CrossGamePool();
        if (pool.Count == 0) return;

        Assert.Equal("claire", VoicePreview.ResolveDonor(pool, "CLAIRE", new System.Random(1)));

        var rnd = VoicePreview.ResolveDonor(pool, null, new System.Random(7));
        Assert.Contains(pool, c => string.Equals(c.Actor, rnd, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Transcode_StreamsDonors_ToDc1RiffBanks()
    {
        if (!Directory.Exists(PacksRoot)) return;
        var pool = CrossGamePool();
        if (pool.Count == 0) return;

        var writes = VoicePreview.Plan(pool, FirstDonor(pool), allBanks: true, bankStem: null,
            new System.Random(4), TwoBankManifest());

        var modDir = Path.Combine(Path.GetTempPath(), "dinorand_previewtest_" + System.Guid.NewGuid());
        try
        {
            VoicePreview.Transcode(writes, modDir, new PcWavCodec());

            var banks = Directory.GetFiles(modDir, "*.dat", SearchOption.AllDirectories);
            Assert.Equal(2, banks.Length);
            foreach (var bank in banks)
            {
                var bytes = File.ReadAllBytes(bank);
                Assert.Equal((byte)'R', bytes[0]);
                Assert.Equal((byte)'I', bytes[1]);
                Assert.Equal((byte)'F', bytes[2]);
                Assert.Equal((byte)'F', bytes[3]);
                Assert.Equal(1, System.BitConverter.ToUInt16(bytes, 22));      // mono
                Assert.Equal(22050, System.BitConverter.ToInt32(bytes, 24));  // 22050 Hz
                Assert.Equal(16, System.BitConverter.ToUInt16(bytes, 34));    // 16-bit
            }
        }
        finally
        {
            try { Directory.Delete(modDir, recursive: true); } catch { }
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "biorand")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
