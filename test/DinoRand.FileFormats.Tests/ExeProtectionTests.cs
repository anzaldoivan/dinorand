using System.Text;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// DRM/protector detection. The detector is executable-name agnostic — it keys off the embedded
/// "Enigma Protector" signature in the raw PE bytes — so it works for any game's exe (DINO.exe /
/// Dino2.exe). When a path/name is supplied the refusal message names that actual executable.
/// </summary>
public class ExeProtectionTests
{
    private static byte[] WithEnigmaSignature() =>
        Encoding.ASCII.GetBytes("MZ......padding......The Enigma Protector......more padding");

    [Fact]
    public void Enigma_signature_is_detected_regardless_of_exe_name()
    {
        var result = ExeProtection.Inspect(WithEnigmaSignature());
        Assert.True(result.IsProtected);
        Assert.Equal(ExeProtectionKind.EnigmaProtector, result.Kind);
    }

    [Fact]
    public void Clean_bytes_are_not_flagged()
    {
        var result = ExeProtection.Inspect(Encoding.ASCII.GetBytes("a perfectly ordinary file"));
        Assert.False(result.IsProtected);
    }

    [Fact]
    public void Refusal_message_names_the_actual_executable()
    {
        // A DC2 Dino2.exe should be reported as Dino2.exe, not the DC1 default DINO.exe.
        var result = ExeProtection.Inspect(WithEnigmaSignature(), "Dino2.exe");
        Assert.True(result.IsProtected);
        Assert.Contains("Dino2.exe", result.Detail);
        Assert.DoesNotContain("DINO.exe", result.Detail);
    }
}
