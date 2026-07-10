using System;
using System.IO;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// THROWAWAY probe (unstaged): apply <see cref="DdrawPatcher.ExpandRebirthVertexTables"/> to the
/// installed <c>english/ddraw.dll</c>, backup-preserving (<c>ddraw.dll.vtxlift-bak</c>).
/// REFUSES to run unless the installed DINO.exe already carries the EXE-side lift (the patched DLL
/// reads the EXE's .dinovtx tables). User-authorized 2026-07-02. Idempotent.
/// </summary>
public class _ZZ_ApplyRebirthVertexLift
{
    // Disarmed with the K82 sweep: manual probes must never auto-fire on a full test run.
    [Fact(Skip = "manual probe — writes to the LIVE english/ddraw.dll; run by removing this Skip")]
    public void Apply_vertex_table_lift_to_installed_ddraw()
    {
        var dllPath = @"C:\Games\dinorand\english\ddraw.dll";
        var exePath = @"C:\Games\dinorand\english\DINO.exe";
        var bakPath = dllPath + ".vtxlift-bak";
        if (!File.Exists(dllPath) || !File.Exists(exePath)) return; // gated: no install at the Windows path on this machine

        Assert.True(ExePatcher.IsDc1CharacterVertexTablesExpanded(File.ReadAllBytes(exePath)),
            "DINO.exe is NOT lifted — the patched ddraw.dll requires the .dinovtx EXE tables; refusing.");

        var bytes = File.ReadAllBytes(dllPath);
        if (DdrawPatcher.IsRebirthVertexTablesExpanded(bytes))
        {
            Console.WriteLine("ALREADY EXPANDED — nothing to do.");
            return;
        }

        var patched = DdrawPatcher.ExpandRebirthVertexTables(bytes); // throws on any mismatch

        if (!File.Exists(bakPath))
            File.Copy(dllPath, bakPath);
        File.WriteAllBytes(dllPath, patched);

        Assert.True(DdrawPatcher.IsRebirthVertexTablesExpanded(File.ReadAllBytes(dllPath)),
            "post-write verification failed");
        Console.WriteLine($"APPLIED: {bytes.Length} -> {patched.Length} bytes; backup at {bakPath}");
    }
}
