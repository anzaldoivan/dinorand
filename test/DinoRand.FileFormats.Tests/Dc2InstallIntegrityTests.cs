using System.Buffers.Binary;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Pins the DC2 install-integrity guarantees from docs/dc2/DC2-INSTALL-INTEGRITY-PLAN.md:
/// (1) install overlays only the files the run recorded, (2) the working dir is cleaned of stale room
/// files per run, (3) a DC2 room can never be emitted as a DC1 16-byte container.
/// </summary>
public sealed class Dc2InstallIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dinorand_integrity_" + Guid.NewGuid().ToString("N"));

    public Dc2InstallIntegrityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // Minimal Gian packages (see GameInstallerContainerGuardTests for the format rationale).
    private static byte[] Dc2Package(byte fill)
    {
        var buf = new byte[4096];
        WriteU32(buf, 0, 0); WriteU32(buf, 4, 8);
        for (int i = 2048; i < 2056; i++) buf[i] = fill;
        return buf;
    }
    private static byte[] Dc1Package(byte fill)
    {
        var buf = new byte[6144];
        WriteU32(buf, 0, 0); WriteU32(buf, 4, 8);
        WriteU32(buf, 16, 1); WriteU32(buf, 20, 8);
        for (int i = 2048; i < 2056; i++) buf[i] = fill;
        for (int i = 4096; i < 4104; i++) buf[i] = fill;
        return buf;
    }
    private static void WriteU32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);

    // --- (1) manifest-scoped install ---

    [Fact]
    public void Install_with_onlyFiles_overlays_only_the_listed_files()
    {
        var dataDir = Path.Combine(_root, "Data");
        var modDir = Path.Combine(_root, "mod");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(modDir);

        File.WriteAllBytes(Path.Combine(dataDir, "st101.dat"), Dc2Package(0x11));
        File.WriteAllBytes(Path.Combine(dataDir, "st102.dat"), Dc2Package(0x22)); // a real room, NOT in this run
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), Dc2Package(0xAA));  // this run's output
        File.WriteAllBytes(Path.Combine(modDir, "st102.dat"), Dc2Package(0xBB));  // STALE leftover in the dir

        var result = GameInstaller.Install(dataDir, modDir, "seed", onlyFiles: new[] { "st101.dat" });

        Assert.Equal(1, result.Overlaid);
        Assert.Equal(Dc2Package(0xAA), File.ReadAllBytes(Path.Combine(dataDir, "st101.dat"))); // overlaid
        Assert.Equal(Dc2Package(0x22), File.ReadAllBytes(Path.Combine(dataDir, "st102.dat"))); // untouched
    }

    [Fact]
    public void Install_without_onlyFiles_preserves_overlay_all_behavior()
    {
        var dataDir = Path.Combine(_root, "Data2");
        var modDir = Path.Combine(_root, "mod2");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(modDir);
        File.WriteAllBytes(Path.Combine(dataDir, "st101.dat"), Dc2Package(0x11));
        File.WriteAllBytes(Path.Combine(modDir, "st101.dat"), Dc2Package(0xAA));

        var result = GameInstaller.Install(dataDir, modDir, "seed");
        Assert.Equal(1, result.Overlaid);
    }

    // --- (2) per-run output-dir hygiene ---

    [Fact]
    public void ClearStaleRoomFiles_removes_dat_but_keeps_other_artifacts()
    {
        var dir = Path.Combine(_root, "out");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "ST999.DAT"), new byte[] { 1 });      // stale room
        File.WriteAllText(Path.Combine(dir, "SPOILER.md"), "keep me");             // non-.dat artifact

        int removed = RunOutputDir.ClearStaleRoomFiles(dir);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(Path.Combine(dir, "ST999.DAT")));
        Assert.True(File.Exists(Path.Combine(dir, "SPOILER.md")));
    }

    [Fact]
    public void ClearStaleRoomFiles_is_a_noop_on_a_missing_dir()
        => Assert.Equal(0, RunOutputDir.ClearStaleRoomFiles(Path.Combine(_root, "does-not-exist")));

    // --- (3) emit-time forbid DC1 writer on a DC2 room ---

    [Fact]
    public void EmitGuard_rejects_a_dc2_room_downgraded_to_dc1_stride()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Dc2RoomEmitGuard.EnsureContainerFormatPreserved("ST101.DAT", Dc2Package(0x11), Dc1Package(0x22)));
        Assert.Contains("ST101.DAT", ex.Message);
    }

    [Fact]
    public void EmitGuard_allows_a_dc2_room_kept_as_dc2()
        => Dc2RoomEmitGuard.EnsureContainerFormatPreserved("ST101.DAT", Dc2Package(0x11), Dc2Package(0x99));

    [Fact]
    public void EmitGuard_ignores_non_gian_bytes()
        => Dc2RoomEmitGuard.EnsureContainerFormatPreserved("raw", new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 });
}
