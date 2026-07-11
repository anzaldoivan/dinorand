using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// <see cref="GameInstaller.Restore"/> must never manufacture the DC1 vertex-lift crash pairing
/// (docs/decisions/dc1/crash-rcas/VTXLIFT-RESTORE-PAIRING-RCA.md): the backup holds the PRE-lift
/// <c>DINO.exe</c> while the lifted <c>ddraw.dll</c> lives outside the backup contract, so a plain
/// restore used to leave a lifted DLL writing into the missing <c>.dinovtx</c> section of a stock
/// exe — write-AV at 0x6FD000 on the next character render. Synthetic game tree in a temp dir.
/// </summary>
public sealed class GameInstallerVertexLiftPairingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dinorand_test_" + Guid.NewGuid().ToString("N"));
    private string DataDir => Path.Combine(_root, "Data");
    private string BackupDir => Path.Combine(DataDir, GameInstaller.BackupDirName);
    private string ExePath => Path.Combine(_root, GameInstaller.ExeName);
    private string DdrawPath => Path.Combine(_root, "ddraw.dll");

    public GameInstallerVertexLiftPairingTests()
    {
        Directory.CreateDirectory(BackupDir);
        // Backup = the pre-lift (stock) exe, exactly what the real .dinorand_backup holds.
        File.WriteAllBytes(Path.Combine(BackupDir, GameInstaller.ExeName), ExePatcherTests.NewStockImageForVertexTables());
        File.WriteAllBytes(ExePath, ExePatcherTests.NewStockImageForVertexTables());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Restore_ReappliesExeLift_WhenDdrawIsLifted()
    {
        File.WriteAllBytes(DdrawPath, DdrawPatcher.ExpandRebirthVertexTables(DdrawPatcherTests.NewStockImage()));

        var result = GameInstaller.Restore(DataDir);

        Assert.True(result.Dc1VertexLiftReapplied);
        Assert.True(ExePatcher.IsDc1CharacterVertexTablesExpanded(File.ReadAllBytes(ExePath)));

        // Second restore puts the stock exe back and must re-lift again (idempotent pairing).
        var again = GameInstaller.Restore(DataDir);
        Assert.True(again.Dc1VertexLiftReapplied);
        Assert.True(ExePatcher.IsDc1CharacterVertexTablesExpanded(File.ReadAllBytes(ExePath)));
    }

    [Fact]
    public void Restore_LeavesExeStock_WhenNoDdraw()
    {
        var result = GameInstaller.Restore(DataDir);

        Assert.False(result.Dc1VertexLiftReapplied);
        Assert.Equal(ExePatcherTests.NewStockImageForVertexTables(), File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void Restore_LeavesExeStock_WhenDdrawIsStock()
    {
        File.WriteAllBytes(DdrawPath, DdrawPatcherTests.NewStockImage());

        var result = GameInstaller.Restore(DataDir);

        Assert.False(result.Dc1VertexLiftReapplied);
        Assert.Equal(ExePatcherTests.NewStockImageForVertexTables(), File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void Restore_LeavesUnliftableExeAlone_EvenWhenDdrawIsLifted()
    {
        // A foreign/non-stock exe cannot be lifted; restore must not fail, must not modify it.
        var foreign = new byte[0x1234];
        File.WriteAllBytes(Path.Combine(BackupDir, GameInstaller.ExeName), foreign);
        File.WriteAllBytes(DdrawPath, DdrawPatcher.ExpandRebirthVertexTables(DdrawPatcherTests.NewStockImage()));

        var result = GameInstaller.Restore(DataDir);

        Assert.False(result.Dc1VertexLiftReapplied);
        Assert.Equal(foreign, File.ReadAllBytes(ExePath));
    }

    [Fact]
    public void Restore_HealsPreexistingBrokenPairing_WithoutExeInBackup()
    {
        File.Delete(Path.Combine(BackupDir, GameInstaller.ExeName));
        File.WriteAllBytes(Path.Combine(BackupDir, "st101.dat"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(DdrawPath, DdrawPatcher.ExpandRebirthVertexTables(DdrawPatcherTests.NewStockImage()));

        var result = GameInstaller.Restore(DataDir);

        Assert.True(result.Dc1VertexLiftReapplied);
        Assert.True(ExePatcher.IsDc1CharacterVertexTablesExpanded(File.ReadAllBytes(ExePath)));
    }
}
