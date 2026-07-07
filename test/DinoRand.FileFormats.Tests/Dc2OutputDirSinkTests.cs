using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>The non-destructive output-dir sink: writes each randomized room to <c>outDir\ST*.DAT</c>
/// (leaving the game install untouched) so the App's DC2 Generate is non-destructive and Install can
/// overlay the dir via GameInstaller — the DC2 analogue of DC1's generate-then-overlay flow
/// (docs/dc2/CROSS-SPECIES-RANDO-PLAN.md).</summary>
public class Dc2OutputDirSinkTests
{
    [Fact]
    public void EmitTo_WritesNamedFileIntoOutputDir_CreatingIt()
    {
        var root = Directory.CreateTempSubdirectory("dc2out").FullName;
        try
        {
            var outDir = Path.Combine(root, "mod");   // does not exist yet
            var bytes = new byte[] { 9, 8, 7, 6 };

            Dc2OutputDirSink.EmitTo(outDir, "ST202.DAT", bytes);

            var written = Path.Combine(outDir, "ST202.DAT");
            Assert.True(File.Exists(written));
            Assert.Equal(bytes, File.ReadAllBytes(written));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EmitTo_DoesNotTouchTheSourceInstall()
    {
        // The install file must be left exactly as-is (non-destructive Generate).
        var root = Directory.CreateTempSubdirectory("dc2out").FullName;
        try
        {
            var install = Path.Combine(root, "install");
            Directory.CreateDirectory(install);
            var installFile = Path.Combine(install, "ST202.DAT");
            var vanilla = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(installFile, vanilla);

            Dc2OutputDirSink.EmitTo(Path.Combine(root, "mod"), "ST202.DAT", new byte[] { 9, 9 });

            Assert.Equal(vanilla, File.ReadAllBytes(installFile));            // install untouched
            Assert.False(File.Exists(installFile + ".bak"));                  // no in-place backup made
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
