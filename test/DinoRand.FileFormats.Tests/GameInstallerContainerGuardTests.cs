using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Pins the installer's <b>container-format guard</b>: an overlaid <c>ST*.DAT</c> must keep the same
/// Gian entry stride as its pristine original (DC2 = 32-byte entries, DC1 = 16-byte). A file whose
/// stride has flipped is a wrong-format rebuild (a DC1 16-byte container written for a DC2 room); the
/// DC2 engine misreads its resource directory into an out-of-range GPU-resource index and hard-crashes
/// on room load (docs/dc2/DC2-ROOM-CONTAINER-STRIDE-CRASH-RCA.md). The installer must refuse it rather
/// than overlay it. Synthetic packages in temp dirs — no game data required.
/// </summary>
public sealed class GameInstallerContainerGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dinorand_guard_" + Guid.NewGuid().ToString("N"));
    private readonly string _dataDir;
    private readonly string _modDir;

    public GameInstallerContainerGuardTests()
    {
        _dataDir = Path.Combine(_root, "Data");
        _modDir = Path.Combine(_root, "mod_dinorand");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_modDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteData(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_dataDir, name), bytes);
    private void WriteMod(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(_modDir, name), bytes);
    private byte[] ReadData(string name) => File.ReadAllBytes(Path.Combine(_dataDir, name));

    // A minimal DC2 Gian package: 2048-byte header, entry[0] = {type, size} at offset 0, bytes 16..31
    // left zero (the DC2 marker GianPackage.TryParse keys on), one sector-aligned payload.
    private static byte[] Dc2Package(byte fill)
    {
        var buf = new byte[2048 + 2048];
        BinaryPrimitives_WriteU32(buf, 0, 0);   // type = Data
        BinaryPrimitives_WriteU32(buf, 4, 8);   // size = 8
        for (int i = 2048; i < 2048 + 8; i++) buf[i] = fill;  // payload
        return buf;
    }

    // A minimal DC1 Gian package: 16-byte entries, so entry[1] lands at offset 16 with a NON-zero
    // {type,size} — which is exactly why the DC2 loader (32-byte stride) misreads it.
    private static byte[] Dc1Package(byte fill)
    {
        var buf = new byte[2048 + 2048 + 2048];
        BinaryPrimitives_WriteU32(buf, 0, 0);    // entry[0].type = Data
        BinaryPrimitives_WriteU32(buf, 4, 8);    // entry[0].size = 8
        BinaryPrimitives_WriteU32(buf, 16, 1);   // entry[1].type = Texture (bytes 16..31 non-zero => DC1)
        BinaryPrimitives_WriteU32(buf, 20, 8);   // entry[1].size = 8
        for (int i = 2048; i < 2048 + 8; i++) buf[i] = fill;
        for (int i = 4096; i < 4096 + 8; i++) buf[i] = fill;
        return buf;
    }

    private static void BinaryPrimitives_WriteU32(byte[] b, int o, uint v)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);

    [Fact]
    public void Parses_as_expected_strides() // guards the fixtures themselves
    {
        Assert.True(GianPackage.TryParse(Dc2Package(0xAA))!.IsDc2);
        Assert.False(GianPackage.TryParse(Dc1Package(0xBB))!.IsDc2);
    }

    [Fact]
    public void Install_refuses_a_room_whose_entry_stride_flipped_to_dc1()
    {
        WriteData("st101.dat", Dc2Package(0x11));   // pristine original: DC2 (32-byte entries)
        WriteMod("st101.dat", Dc1Package(0x22));    // mod: DC1 (16-byte) — the corruption

        var ex = Assert.Throws<ContainerFormatMismatchException>(
            () => GameInstaller.Install(_dataDir, _modDir, "seed-guard"));
        Assert.Contains("st101.dat", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Fail-safe: the real file is left untouched (still the pristine DC2 container).
        Assert.Equal(Dc2Package(0x11), ReadData("st101.dat"));
    }

    [Fact]
    public void Install_allows_a_room_whose_dc2_stride_is_preserved()
    {
        WriteData("st105.dat", Dc2Package(0x11));   // pristine DC2
        WriteMod("st105.dat", Dc2Package(0x77));    // mod: still DC2, different content

        var result = GameInstaller.Install(_dataDir, _modDir, "seed-ok");

        Assert.Equal(1, result.Overlaid);
        Assert.Equal(Dc2Package(0x77), ReadData("st105.dat"));
    }

    [Fact]
    public void Install_leaves_non_gian_fixtures_alone()
    {
        // Tiny non-Gian blobs (as other installer tests use) are below the header size and don't parse
        // as a Gian package, so the guard has no oracle and must not interfere.
        WriteData("st100.dat", new byte[] { 1, 1, 1 });
        WriteMod("st100.dat", new byte[] { 9, 9, 9 });

        var result = GameInstaller.Install(_dataDir, _modDir, "seed-raw");

        Assert.Equal(1, result.Overlaid);
        Assert.Equal(new byte[] { 9, 9, 9 }, ReadData("st100.dat"));
    }
}
