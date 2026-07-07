using System;
using System.IO;
using DinoRand.FileFormats.Exe;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Tests for <see cref="Dc2MotionTrailInstaller"/> — the install-time step that applies the DC2
/// <c>ddraw.dll</c> MotionTrail fix to the game's wrapper: it locates <c>ddraw.dll</c> in the game
/// root, backs it up once, and applies <see cref="Dc2DdrawTrailPatch"/> idempotently. It must never
/// corrupt an unrecognized build and must be safe to re-run (re-rolling a seed).
/// </summary>
public class Dc2MotionTrailInstallerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "dinorand_dc2trail_" + Guid.NewGuid().ToString("N"));

    public Dc2MotionTrailInstallerTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    private string WritePristineDll()
    {
        var path = Path.Combine(_tmp, "ddraw.dll");
        File.WriteAllBytes(path, NewPristine());
        return path;
    }

    private static byte[] NewPristine()
    {
        var dll = new byte[Dc2DdrawTrailPatch.ExpectedLength];
        Array.Fill(dll, (byte)0xCC, Dc2DdrawTrailPatch.CaveOffset, Dc2DdrawTrailPatch.CaveLength);
        new byte[] { 0xA1, 0x44, 0xBA, 0x33, 0x10 }.CopyTo(dll.AsSpan(Dc2DdrawTrailPatch.HookOffset));
        new byte[] { 0x28, 0x36 }.CopyTo(dll.AsSpan(Dc2DdrawTrailPatch.RelocEntryOffset));
        return dll;
    }

    [Fact]
    public void ApplyToFile_PatchesAndBacksUp()
    {
        var path = WritePristineDll();
        var outcome = Dc2MotionTrailInstaller.ApplyToFile(path);

        Assert.Equal(Dc2TrailFixOutcome.Applied, outcome);
        Assert.True(Dc2DdrawTrailPatch.IsApplied(File.ReadAllBytes(path)));
        // a pristine backup is captured next to the dll
        var bak = path + Dc2MotionTrailInstaller.BackupSuffix;
        Assert.True(File.Exists(bak));
        Assert.True(Dc2DdrawTrailPatch.IsRecognizedPristine(File.ReadAllBytes(bak)));
    }

    [Fact]
    public void ApplyToFile_IsIdempotent()
    {
        var path = WritePristineDll();
        Assert.Equal(Dc2TrailFixOutcome.Applied, Dc2MotionTrailInstaller.ApplyToFile(path));
        var afterFirst = File.ReadAllBytes(path);

        // Second run detects the existing patch and does nothing further.
        Assert.Equal(Dc2TrailFixOutcome.AlreadyApplied, Dc2MotionTrailInstaller.ApplyToFile(path));
        Assert.Equal(afterFirst, File.ReadAllBytes(path));
    }

    [Fact]
    public void ApplyToFile_DoesNotOverwriteAnExistingPristineBackup()
    {
        var path = WritePristineDll();
        var bak = path + Dc2MotionTrailInstaller.BackupSuffix;
        File.WriteAllBytes(bak, NewPristine()); // a backup already exists from a prior run

        Dc2MotionTrailInstaller.ApplyToFile(path);
        // The backup must stay the pristine original, never re-captured from an already-modded file.
        Assert.True(Dc2DdrawTrailPatch.IsRecognizedPristine(File.ReadAllBytes(bak)));
    }

    [Fact]
    public void ApplyToFile_UnrecognizedVersion_LeavesFileUntouchedAndDoesNotBackUp()
    {
        var path = Path.Combine(_tmp, "ddraw.dll");
        var foreign = new byte[1024]; // not the known build at all
        File.WriteAllBytes(path, foreign);

        var outcome = Dc2MotionTrailInstaller.ApplyToFile(path);

        Assert.Equal(Dc2TrailFixOutcome.UnrecognizedVersion, outcome);
        Assert.Equal(foreign, File.ReadAllBytes(path));      // untouched
        Assert.False(File.Exists(path + Dc2MotionTrailInstaller.BackupSuffix)); // no backup of an unknown file
    }

    [Fact]
    public void ApplyToFile_Missing_ReturnsNotFound()
    {
        var outcome = Dc2MotionTrailInstaller.ApplyToFile(Path.Combine(_tmp, "nope.dll"));
        Assert.Equal(Dc2TrailFixOutcome.NotFound, outcome);
    }

    [Fact]
    public void Apply_ResolvesDdrawFromGameRoot()
    {
        // Lay out a minimal DC2 install: <root>/rebirth/Data/ST101.DAT + <root>/rebirth/ddraw.dll
        var rebirth = Path.Combine(_tmp, "rebirth");
        Directory.CreateDirectory(Path.Combine(rebirth, "Data"));
        File.WriteAllBytes(Path.Combine(rebirth, "Data", "ST101.DAT"), new byte[16]);
        File.WriteAllBytes(Path.Combine(rebirth, "ddraw.dll"), NewPristine());

        var outcome = Dc2MotionTrailInstaller.Apply(_tmp);

        Assert.Equal(Dc2TrailFixOutcome.Applied, outcome);
        Assert.True(Dc2DdrawTrailPatch.IsApplied(File.ReadAllBytes(Path.Combine(rebirth, "ddraw.dll"))));
    }
}
