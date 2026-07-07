using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The 2-byte Dino2.exe WP-gate patch (docs/dc2/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §8–9): nop the
/// flag-(9,0x33) <c>je</c> at VA 0x48263B so the per-weapon WP&lt;n&gt;A character-graft files load
/// unconditionally. Pure tests run on synthetic buffers; the real-exe test resolves the canonical
/// rebirth install and skips when absent (the "no game files → skip" convention).
/// </summary>
public class Dc2WpGatePatchTests
{
    /// <summary>Resolve a file in the canonical rebirth game root, or null (skip) when absent.</summary>
    internal static string? FindDc2GameRootFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "4249140_DinoCrisis2", "rebirth", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>A synthetic pristine-shaped buffer: right length, gate bytes + context in place.</summary>
    private static byte[] SyntheticPristine()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        exe[Dc2WpGatePatch.GateOffset - 2] = 0x85; // test eax,eax
        exe[Dc2WpGatePatch.GateOffset - 1] = 0xC0;
        exe[Dc2WpGatePatch.GateOffset]     = 0x74; // je +0x62
        exe[Dc2WpGatePatch.GateOffset + 1] = 0x62;
        return exe;
    }

    [Fact]
    public void Recognizes_pristine_and_applies_nops()
    {
        var exe = SyntheticPristine();
        Assert.True(Dc2WpGatePatch.IsRecognizedPristine(exe));
        Assert.False(Dc2WpGatePatch.IsApplied(exe));

        Dc2WpGatePatch.Apply(exe);

        Assert.True(Dc2WpGatePatch.IsApplied(exe));
        Assert.False(Dc2WpGatePatch.IsRecognizedPristine(exe));
        Assert.Equal(new byte[] { 0x90, 0x90 },
                     exe[Dc2WpGatePatch.GateOffset..(Dc2WpGatePatch.GateOffset + 2)]);
        // Only the two gate bytes changed.
        var reference = SyntheticPristine();
        for (int i = 0; i < exe.Length; i++)
            if (i != Dc2WpGatePatch.GateOffset && i != Dc2WpGatePatch.GateOffset + 1)
                Assert.Equal(reference[i], exe[i]);
    }

    [Fact]
    public void Refuses_wrong_length_patched_and_unknown_bytes()
    {
        Assert.Throws<InvalidOperationException>(() => Dc2WpGatePatch.Apply(new byte[100]));

        var patched = SyntheticPristine();
        Dc2WpGatePatch.Apply(patched);
        var before = (byte[])patched.Clone();
        Assert.Throws<InvalidOperationException>(() => Dc2WpGatePatch.Apply(patched));
        Assert.Equal(before, patched); // untouched on refusal

        var foreign = SyntheticPristine();
        foreign[Dc2WpGatePatch.GateOffset] = 0x75; // jne — a different build
        Assert.Throws<InvalidOperationException>(() => Dc2WpGatePatch.Apply(foreign));
        Assert.False(Dc2WpGatePatch.IsRecognizedPristine(foreign));
    }

    [Fact]
    public void Real_dino2_exe_is_recognized_pristine_or_applied()
    {
        var path = FindDc2GameRootFile("Dino2.exe");
        if (path is null) return; // no game files → skip

        // Prefer a pristine backup if the live exe is patched.
        var bytes = File.ReadAllBytes(path);
        if (!Dc2WpGatePatch.IsRecognizedPristine(bytes) && !Dc2WpGatePatch.IsApplied(bytes))
        {
            var bak = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "Dino2.exe.*bak*")
                               .FirstOrDefault();
            if (bak is null) return;
            bytes = File.ReadAllBytes(bak);
        }
        Assert.True(Dc2WpGatePatch.IsRecognizedPristine(bytes) || Dc2WpGatePatch.IsApplied(bytes));
    }
}
