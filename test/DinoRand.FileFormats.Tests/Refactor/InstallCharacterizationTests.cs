using System.Security.Cryptography;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests.Refactor;

public sealed class InstallCharacterizationTests
{
    [Theory]
    [InlineData("st100.dat")]
    [InlineData("ST890.DAT")]
    public void Overlay_manifest_and_restore_have_exact_inventory_and_hash_effects(string roomName)
    {
        string root = Directory.CreateTempSubdirectory("dinorand-w1-inst-").FullName;
        try
        {
            string data = Directory.CreateDirectory(Path.Combine(root, "Data")).FullName;
            string mod = Directory.CreateDirectory(Path.Combine(root, "mod")).FullName;
            byte[] original = { 1, 2, 3, 4 };
            byte[] overlay = { 9, 8, 7, 6 };
            File.WriteAllBytes(Path.Combine(data, roomName), original);
            File.WriteAllBytes(Path.Combine(mod, roomName.ToUpperInvariant()), overlay);
            File.WriteAllBytes(Path.Combine(mod, "foreign.dat"), new byte[] { 0xff });

            var result = GameInstaller.Install(data, mod, "seed-42", new[] { roomName });
            Assert.Equal(1, result.BackedUp);
            Assert.Equal(1, result.Overlaid);
            Assert.Equal(overlay, File.ReadAllBytes(Path.Combine(data, roomName)));
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(data, GameInstaller.BackupDirName, roomName)));
            Assert.False(File.Exists(Path.Combine(data, "foreign.dat")));

            var manifest = GameInstaller.ReadManifest(data);
            Assert.NotNull(manifest);
            Assert.Equal("seed-42", manifest!.Seed);
            Assert.Equal(new[] { roomName }, manifest.Files, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)),
                manifest.OriginalHashes![roomName]);

            var restored = GameInstaller.Restore(data);
            Assert.Equal(1, restored.Restored);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(data, roomName)));
            Assert.Equal(new[] { roomName }, Directory.EnumerateFiles(data).Select(Path.GetFileName).ToArray());
            Assert.True(Directory.Exists(Path.Combine(data, GameInstaller.BackupDirName)));
            Assert.False(GameInstaller.ReadManifest(data)!.Applied);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(data, GameInstaller.BackupDirName, roomName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Reroll_preserves_the_first_pristine_backup_and_updates_only_the_overlay()
    {
        string root = Directory.CreateTempSubdirectory("dinorand-w1-reroll-").FullName;
        try
        {
            string data = Directory.CreateDirectory(Path.Combine(root, "Data")).FullName;
            string mod = Directory.CreateDirectory(Path.Combine(root, "mod")).FullName;
            string name = "st100.dat";
            byte[] original = { 1, 2, 3 };
            File.WriteAllBytes(Path.Combine(data, name), original);
            File.WriteAllBytes(Path.Combine(mod, name), new byte[] { 4, 5, 6 });
            GameInstaller.Install(data, mod, "first");
            File.WriteAllBytes(Path.Combine(mod, name), new byte[] { 7, 8, 9 });
            var second = GameInstaller.Install(data, mod, "second");
            Assert.Equal(0, second.BackedUp);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(data, GameInstaller.BackupDirName, name)));
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(data, name)));
            GameInstaller.Restore(data);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(data, name)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
