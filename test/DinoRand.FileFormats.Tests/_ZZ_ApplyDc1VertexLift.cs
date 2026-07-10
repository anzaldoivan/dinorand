using System;
using System.IO;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// THROWAWAY probe (unstaged): apply <see cref="ExePatcher.ExpandDc1CharacterVertexTables"/> to the
/// installed <c>english/DINO.exe</c>, backup-preserving (<c>DINO.exe.vtxlift-bak</c> = pre-lift
/// image; never overwritten once created). User-authorized 2026-07-02. Idempotent: skips if the
/// image is already expanded.
/// </summary>
public class _ZZ_ApplyDc1VertexLift
{
    // Disarmed with the K82 sweep: manual probes must never auto-fire on a full test run.
    // The lift is already applied on disk (idempotent skip); re-run by removing the Skip.
    [Fact(Skip = "manual probe — writes to the LIVE english/DINO.exe; run by removing this Skip")]
    public void Apply_vertex_table_lift_to_installed_exe()
    {
        var exePath = @"C:\Games\dinorand\english\DINO.exe";
        var bakPath = exePath + ".vtxlift-bak";
        if (!File.Exists(exePath)) return; // gated: no install at the Windows path on this machine

        var bytes = File.ReadAllBytes(exePath);
        if (ExePatcher.IsDc1CharacterVertexTablesExpanded(bytes))
        {
            Console.WriteLine("ALREADY EXPANDED — nothing to do.");
            return;
        }

        var patched = ExePatcher.ExpandDc1CharacterVertexTables(bytes); // throws on any mismatch

        if (!File.Exists(bakPath))
            File.Copy(exePath, bakPath);
        File.WriteAllBytes(exePath, patched);

        var reread = File.ReadAllBytes(exePath);
        Assert.True(ExePatcher.IsDc1CharacterVertexTablesExpanded(reread), "post-write verification failed");
        Console.WriteLine($"APPLIED: {bytes.Length} -> {reread.Length} bytes; backup at {bakPath}");
    }
}
