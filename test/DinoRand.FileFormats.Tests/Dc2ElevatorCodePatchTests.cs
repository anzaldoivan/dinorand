using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Dc2ElevatorCodePatch (docs/decisions/dc2/DC2-PUZZLE-RANDO-PLAN.md §3, K108) on a synthetic exe
/// image: the real build's length with the pinned <c>c7 44 24 disp8</c> opcodes + vanilla digit
/// bytes laid out at the manifest offsets — no game files in the repo.
/// </summary>
public class Dc2ElevatorCodePatchTests
{
    internal static byte[] MakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        for (int i = 0; i < Dc2ElevatorCodePatch.Imm32FileOffsets.Count; i++)
        {
            int off = Dc2ElevatorCodePatch.Imm32FileOffsets[i];
            exe[off - 4] = 0xC7; exe[off - 3] = 0x44; exe[off - 2] = 0x24; exe[off - 1] = (byte)(4 * (i + 1));
            string code = Dc2ElevatorCodePatch.VanillaCodes[i];
            for (int d = 0; d < Dc2ElevatorCodePatch.DigitCount; d++)
                exe[off + d] = (byte)(code[d] - '0');
        }
        return exe;
    }

    [Fact]
    public void Manifest_LoadsEightSlotsAndVanillaCodes()
    {
        Assert.Equal(8, Dc2ElevatorCodePatch.Imm32FileOffsets.Count);
        Assert.Equal(new[] { "2350", "0153", "1452", "5210", "5420", "4015", "3051", "4521" },
                     Dc2ElevatorCodePatch.VanillaCodes);
    }

    [Fact]
    public void SyntheticExe_IsCanonical()
        => Assert.True(Dc2ElevatorCodePatch.IsCanonical(MakeExe()));

    [Fact]
    public void Scramble_WritesEightDistinctCodes_DigitsZeroToFive()
    {
        var exe = MakeExe();
        var entries = Dc2ElevatorCodePatch.Scramble(exe, seed: 1234);

        Assert.Equal(8, entries.Length);
        var newCodes = entries.Select(e => e.NewCode).ToArray();
        Assert.Equal(8, newCodes.Distinct().Count());
        Assert.All(newCodes, c =>
        {
            Assert.Equal(4, c.Length);
            Assert.All(c, ch => Assert.InRange(ch, '0', '5'));
        });
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(Dc2ElevatorCodePatch.VanillaCodes[i], entries[i].OldCode);
            Assert.Equal(entries[i].NewCode, Dc2ElevatorCodePatch.ReadCode(exe, i));
        }
        // the opcode pins are untouched, so a scrambled exe still validates (restore/re-run path)
        Dc2ElevatorCodePatch.Validate(exe);
    }

    [Fact]
    public void Scramble_IsDeterministic_And_NonCompounding()
    {
        var a = MakeExe();
        var b = MakeExe();
        Dc2ElevatorCodePatch.Scramble(a, seed: 42);
        Dc2ElevatorCodePatch.Scramble(b, seed: 42);
        Assert.Equal(a, b);

        // re-running the same seed over an already-scrambled exe writes the same codes (no compounding)
        Dc2ElevatorCodePatch.Scramble(a, seed: 42);
        Assert.Equal(b, a);

        var c = MakeExe();
        Dc2ElevatorCodePatch.Scramble(c, seed: 43);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void RestoreCanonical_RoundTripsToPristine()
    {
        var pristine = MakeExe();
        var exe = (byte[])pristine.Clone();
        Dc2ElevatorCodePatch.Scramble(exe, seed: 7);
        Assert.False(Dc2ElevatorCodePatch.IsCanonical(exe));
        Dc2ElevatorCodePatch.RestoreCanonical(exe);
        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void Validate_RejectsWrongLength_BadOpcode_AndForeignDigit()
    {
        Assert.Throws<InvalidOperationException>(() => Dc2ElevatorCodePatch.Validate(new byte[100]));

        var badOpcode = MakeExe();
        badOpcode[Dc2ElevatorCodePatch.Imm32FileOffsets[0] - 4] = 0x90;
        Assert.Throws<InvalidOperationException>(() => Dc2ElevatorCodePatch.Validate(badOpcode));

        var badDigit = MakeExe();
        badDigit[Dc2ElevatorCodePatch.Imm32FileOffsets[3] + 2] = 9; // outside the 0–5 alphabet
        Assert.Throws<InvalidOperationException>(() => Dc2ElevatorCodePatch.Validate(badDigit));

        // neither refusal path may write
        Assert.Throws<InvalidOperationException>(() => Dc2ElevatorCodePatch.Scramble(badDigit, seed: 1));
    }

    /// <summary>Real-data end-to-end pin, gated on <c>DINORAND_DC2_DIR</c> (the rebirth <c>Data</c>
    /// dir; <c>Dino2.exe</c> is its sibling). In-memory only — never writes to the install. Skipped
    /// when the env var is unset or the exe's slots are not canonical (already scrambled).</summary>
    [Fact]
    public void RealExe_PinsHold_ScrambleRestoreRoundTrips()
    {
        var dir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var path = Path.Combine(dir, "..", "Dino2.exe");
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        Dc2ElevatorCodePatch.Validate(bytes); // c7 44 24 pins + digit alphabet hold on the real build
        if (!Dc2ElevatorCodePatch.IsCanonical(bytes)) return; // already scrambled → skip

        var pristine = (byte[])bytes.Clone();
        Dc2ElevatorCodePatch.Scramble(bytes, seed: 1234);
        Assert.False(Dc2ElevatorCodePatch.IsCanonical(bytes));
        Dc2ElevatorCodePatch.RestoreCanonical(bytes);
        Assert.Equal(pristine, bytes);
    }
}
