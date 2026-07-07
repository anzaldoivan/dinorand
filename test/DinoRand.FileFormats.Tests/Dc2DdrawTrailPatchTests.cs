using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for <see cref="Dc2DdrawTrailPatch"/> — the byte-level patcher for the Dino Crisis 2
/// Classic REbirth wrapper <c>ddraw.dll</c> that fixes the MotionTrail over-brightening (the trail
/// renderer's recursive 0.5/0.5 feedback drifts because it never pins sRGB read/write state; the patch
/// hooks the renderer and forces <c>SRGBTEXTURE</c>=0 / <c>SRGBWRITEENABLE</c>=0 around its draw).
/// Root cause + the three reversible edits are documented in
/// <c>docs/dc2</c> (TEXTURE-IMPORT-VRAM / the MotionTrail RCA).
///
/// <para>The patcher is the safety-critical surface: it must recognize <b>only</b> the exact known
/// pristine DLL (exact length + the three sentinel byte windows) and refuse anything else, so a
/// different Classic REbirth build can never be corrupted by these version-specific offsets.</para>
/// </summary>
public class Dc2DdrawTrailPatchTests
{
    // The three reversible edits, independently transcribed here so the test pins the bytes
    // (a regression in the patcher's tables fails the test rather than silently shipping).
    private static readonly byte[] HookOriginal = { 0xA1, 0x44, 0xBA, 0x33, 0x10 }; // mov eax,ds:0x1033ba44
    private static readonly byte[] HookPatched  = { 0xE9, 0x99, 0xED, 0xF9, 0xFF }; // jmp 0x1000a3c5 (->cave)
    private static readonly byte[] RelocOriginal = { 0x28, 0x36 };                  // HIGHLOW entry 0x3628
    private static readonly byte[] RelocPatched  = { 0x00, 0x00 };                  // type-0 ABSOLUTE no-op
    // 68-byte cave: push esi; PIC device load (add eax,0x331679); SetSamplerState(0/1,SRGBTEXTURE,0);
    // SetRenderState(SRGBWRITEENABLE,0); mov eax,esi; pop esi; jmp 0x1006b62c.
    private static readonly byte[] CavePatched = Convert.FromHexString(
        "56e8000000005805791633008b306a006a0b6a00568b0eff91140100006a006a0b6a01568b0e" +
        "ff91140100006a0068c2000000568b0eff91e40000008bc65ee923120600");

    /// <summary>Build a synthetic buffer that looks exactly like the known pristine ddraw.dll to the
    /// recognizer: correct length, the hook original at its offset, the cave region all-<c>0xCC</c>
    /// padding, and the reloc entry holding <c>0x3628</c>. Everything else is zero.</summary>
    private static byte[] NewPristine()
    {
        var dll = new byte[Dc2DdrawTrailPatch.ExpectedLength];
        Array.Fill(dll, (byte)0xCC, Dc2DdrawTrailPatch.CaveOffset, Dc2DdrawTrailPatch.CaveLength);
        HookOriginal.CopyTo(dll.AsSpan(Dc2DdrawTrailPatch.HookOffset));
        RelocOriginal.CopyTo(dll.AsSpan(Dc2DdrawTrailPatch.RelocEntryOffset));
        return dll;
    }

    [Fact]
    public void Pristine_IsRecognized_AndNotApplied()
    {
        var dll = NewPristine();
        Assert.True(Dc2DdrawTrailPatch.IsRecognizedPristine(dll));
        Assert.False(Dc2DdrawTrailPatch.IsApplied(dll));
    }

    [Fact]
    public void Apply_WritesExactlyTheThreeEdits()
    {
        var dll = NewPristine();
        Dc2DdrawTrailPatch.Apply(dll);

        Assert.Equal(HookPatched, dll.Skip(Dc2DdrawTrailPatch.HookOffset).Take(5).ToArray());
        Assert.Equal(CavePatched, dll.Skip(Dc2DdrawTrailPatch.CaveOffset).Take(Dc2DdrawTrailPatch.CaveLength).ToArray());
        Assert.Equal(RelocPatched, dll.Skip(Dc2DdrawTrailPatch.RelocEntryOffset).Take(2).ToArray());
    }

    [Fact]
    public void Apply_TouchesNothingOutsideTheThreeWindows()
    {
        var before = NewPristine();
        var after = NewPristine();
        Dc2DdrawTrailPatch.Apply(after);

        for (int i = 0; i < after.Length; i++)
        {
            bool inHook  = i >= Dc2DdrawTrailPatch.HookOffset && i < Dc2DdrawTrailPatch.HookOffset + 5;
            bool inCave  = i >= Dc2DdrawTrailPatch.CaveOffset && i < Dc2DdrawTrailPatch.CaveOffset + Dc2DdrawTrailPatch.CaveLength;
            bool inReloc = i >= Dc2DdrawTrailPatch.RelocEntryOffset && i < Dc2DdrawTrailPatch.RelocEntryOffset + 2;
            if (!inHook && !inCave && !inReloc)
                Assert.True(before[i] == after[i], $"byte at 0x{i:x} changed unexpectedly");
        }
    }

    [Fact]
    public void Apply_IsDetectableAndNotDoubleApplied()
    {
        var dll = NewPristine();
        Dc2DdrawTrailPatch.Apply(dll);

        Assert.True(Dc2DdrawTrailPatch.IsApplied(dll));
        Assert.False(Dc2DdrawTrailPatch.IsRecognizedPristine(dll)); // already patched is no longer "pristine"
        // Re-applying must be refused (the sentinels no longer match), so we never double-patch.
        Assert.Throws<InvalidOperationException>(() => Dc2DdrawTrailPatch.Apply(dll));
    }

    [Fact]
    public void Apply_RefusesWrongLength()
    {
        var tooShort = new byte[Dc2DdrawTrailPatch.ExpectedLength - 1];
        Assert.False(Dc2DdrawTrailPatch.IsRecognizedPristine(tooShort));
        Assert.Throws<InvalidOperationException>(() => Dc2DdrawTrailPatch.Apply(tooShort));
    }

    [Fact]
    public void Apply_RefusesForeignBytes_AndLeavesBufferUntouched()
    {
        // Correct length, but the hook sentinel doesn't match → a different/unknown build.
        var foreign = NewPristine();
        foreign[Dc2DdrawTrailPatch.HookOffset] = 0x90; // corrupt one sentinel byte
        var snapshot = (byte[])foreign.Clone();

        Assert.False(Dc2DdrawTrailPatch.IsRecognizedPristine(foreign));
        Assert.Throws<InvalidOperationException>(() => Dc2DdrawTrailPatch.Apply(foreign));
        Assert.Equal(snapshot, foreign); // refusal must not corrupt the file
    }

    [Fact]
    public void Apply_RefusesWhenCaveSlackIsNotFree()
    {
        // Right length + right hook/reloc, but the cave region isn't the expected int3 padding
        // (e.g. a build where that code-cave is occupied) → refuse rather than overwrite real code.
        var dll = NewPristine();
        dll[Dc2DdrawTrailPatch.CaveOffset + 4] = 0x12;
        Assert.False(Dc2DdrawTrailPatch.IsRecognizedPristine(dll));
        Assert.Throws<InvalidOperationException>(() => Dc2DdrawTrailPatch.Apply(dll));
    }

    /// <summary>
    /// Real-data end-to-end pin, gated on <c>DINORAND_DC2_DIR</c> (the install root containing
    /// <c>rebirth\ddraw.dll</c>). Confirms the patcher recognizes the shipping pristine DLL
    /// (md5 <c>a545d485…</c>) and reproduces the exact verified patched image (md5 <c>2f73c495…</c>).
    /// Skipped when the env var is unset so CI never needs the game files.
    /// </summary>
    [Fact]
    public void RealDdraw_Pristine_ProducesVerifiedPatchedImage()
    {
        var dir = Environment.GetEnvironmentVariable("DINORAND_DC2_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return; // gated: no install configured
        var path = Path.Combine(dir, "rebirth", "ddraw.dll");
        if (!File.Exists(path)) return;

        var bytes = File.ReadAllBytes(path);
        // Only meaningful against the known pristine build; if already patched/different, skip.
        if (!Dc2DdrawTrailPatch.IsRecognizedPristine(bytes)) return;

        Assert.Equal("a545d485d38d7cb4601174f4d5339fce", Md5(bytes));
        Dc2DdrawTrailPatch.Apply(bytes);
        Assert.Equal("2f73c495e97a9fe0f244d6b705edbece", Md5(bytes));
        Assert.True(Dc2DdrawTrailPatch.IsApplied(bytes));
    }

    private static string Md5(byte[] b) => Convert.ToHexString(MD5.HashData(b)).ToLowerInvariant();
}
