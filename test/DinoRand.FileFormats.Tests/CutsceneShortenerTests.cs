using System.Buffers.Binary;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer;
using DinoRand.Randomizer.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The DC1 cutscene-shorten lever (CUTSCENE-SKIP-FEASIBILITY.md §9.3, STATIC-SCD-RE cont.74).
/// A cutscene is a script-authored <c>SetFlag(2,2,1) … SetFlag(2,2,0)</c> bracket in an event sub;
/// the lever rewrites eligible brackets IN PLACE so every side-effect op (flag writes, item records)
/// still executes while each maximal choreography run is jumped over with an op-<c>0x0c</c> goto
/// (dead bytes tiled with op-<c>0x00</c> so a linear walker still lands on boundaries). Synthetic
/// scripts pin the classifier and the rewrite; a gated real-install test proves the corpus contract
/// (≥60 eligible brackets, pilots st202/st10f rewritten, idempotent, walk stays clean).
/// </summary>
public class CutsceneShortenerTests
{
    private const uint B = ScriptInjector.PsxBase; // 0x80100000

    private static void PutU32(byte[] buf, int off, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), v);
    private static void PutI16(byte[] buf, int off, short v)
        => BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(off, 2), v);
    private static short GetI16(byte[] buf, int off)
        => BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(off, 2));

    private static void PutFlagOp(byte[] buf, int off, byte group, byte idx, byte val)
    {
        buf[off] = 0x25; buf[off + 1] = group; buf[off + 2] = idx; buf[off + 3] = val;
    }

    /// <summary>
    /// Minimal valid RDT with one sub holding one bracket:
    /// open(2,2,1) @0x24 · choreo 0x22 @0x28 · choreo 0x24(len8) @0x2c · KEEP flag(0,5,1) @0x34 ·
    /// choreo 0x22 @0x38 · close(2,2,0) @0x3c · return @0x40.
    /// </summary>
    private static byte[] BuildBracketScript()
    {
        var rdt = new byte[0x44];
        PutU32(rdt, 0x14, B + 0x20);   // header -> func table
        PutU32(rdt, 0x20, 0x04);       // entry0 = 4 => one sub at 0x24
        PutFlagOp(rdt, 0x24, 2, 2, 1); // open
        rdt[0x28] = 0x22;              // choreography (entity bind, len 4)
        rdt[0x2c] = 0x24;              // choreography (len 8)
        PutFlagOp(rdt, 0x34, 0, 5, 1); // KEEP: story flag write
        rdt[0x38] = 0x22;              // choreography (len 4)
        PutFlagOp(rdt, 0x3c, 2, 2, 0); // close
        rdt[0x40] = 0x10;              // return (len 2)
        return rdt;
    }

    [Fact]
    public void FindBrackets_FindsEligibleBracket()
    {
        var rdt = BuildBracketScript();
        var brackets = CutsceneShortener.FindBrackets(rdt);

        var b = Assert.Single(brackets);
        Assert.Equal(0, b.SubIndex);
        Assert.Equal(0x24, b.OpenOffset);
        Assert.Equal(0x3c, b.CloseOffset);
        Assert.True(b.Eligible, b.Reason);
    }

    [Fact]
    public void Shorten_ReplacesChoreographyRuns_KeepsSideEffects_LengthInvariant()
    {
        var rdt = BuildBracketScript();
        var before = (byte[])rdt.Clone();

        int n = CutsceneShortener.Shorten(rdt);

        Assert.Equal(1, n);
        Assert.Equal(before.Length, rdt.Length);
        // open, KEEP and close are byte-identical.
        Assert.Equal(before[0x24..0x28], rdt[0x24..0x28]);
        Assert.Equal(before[0x34..0x38], rdt[0x34..0x38]);
        Assert.Equal(before[0x3c..0x40], rdt[0x3c..0x40]);
        // run1 [0x28,0x34) -> goto +0x0c, 0x00-tiled.
        Assert.Equal(0x0c, rdt[0x28]);
        Assert.Equal(0x0c, GetI16(rdt, 0x2a));
        for (int i = 0x2c; i < 0x34; i++) Assert.Equal(0x00, rdt[i]);
        // run2 [0x38,0x3c) -> goto +4 (pure nop-jump).
        Assert.Equal(0x0c, rdt[0x38]);
        Assert.Equal(4, GetI16(rdt, 0x3a));
        // the rewritten sub still walks onto clean opcode boundaries (return op reachable).
        Assert.Equal(0, ScriptInjector.SubroutineAtBoundary(rdt, 0x40));
        // both new gotos are well-formed branch sites with in-sub forward targets.
        var sites = ScriptInjector.BranchSites(rdt);
        Assert.Contains(sites, s => s.Offset == 0x28 && s.Forward && s.Target == 0x34);
        Assert.Contains(sites, s => s.Offset == 0x38 && s.Forward && s.Target == 0x3c);
    }

    [Fact]
    public void Shorten_IsIdempotent()
    {
        var rdt = BuildBracketScript();
        CutsceneShortener.Shorten(rdt);
        var once = (byte[])rdt.Clone();
        CutsceneShortener.Shorten(rdt);
        Assert.Equal(once, rdt);
    }

    [Theory]
    [InlineData((byte)0x0f)] // gosub — side effects may live in the callee
    [InlineData((byte)0x01)] // task-spawn — parallel task may carry side effects
    [InlineData((byte)0x05)] // goto-sub — control leaves the bracket
    public void Bracket_WithControlTransferOp_IsRejected(byte op)
    {
        var rdt = BuildBracketScript();
        rdt[0x38] = op; // replace a choreography op with the dangerous one (all are len 4)
        var before = (byte[])rdt.Clone();

        var b = Assert.Single(CutsceneShortener.FindBrackets(rdt));
        Assert.False(b.Eligible);

        Assert.Equal(0, CutsceneShortener.Shorten(rdt));
        Assert.Equal(before, rdt);
    }

    [Fact]
    public void Bracket_WithExternalBranchIntoChoreography_IsRejected()
    {
        // sub0: goto @0x24 targeting 0x38 (interior of the choreography run, not its start) ·
        // open @0x28 · choreo @0x2c (len4) · choreo @0x30 (len8) · choreo @0x38 (len4) ·
        // close @0x3c · return @0x40.
        var rdt = new byte[0x44];
        PutU32(rdt, 0x14, B + 0x20);
        PutU32(rdt, 0x20, 0x04);
        rdt[0x24] = 0x0c; PutI16(rdt, 0x26, 0x14); // 0x24 + 0x14 = 0x38
        PutFlagOp(rdt, 0x28, 2, 2, 1);
        rdt[0x2c] = 0x22;
        rdt[0x30] = 0x24;
        rdt[0x38] = 0x22;
        PutFlagOp(rdt, 0x3c, 2, 2, 0);
        rdt[0x40] = 0x10;
        var before = (byte[])rdt.Clone();

        var b = Assert.Single(CutsceneShortener.FindBrackets(rdt));
        Assert.False(b.Eligible);
        Assert.Equal(0, CutsceneShortener.Shorten(rdt));
        Assert.Equal(before, rdt);
    }

    [Fact]
    public void Config_Defaults_Off()
    {
        var config = new RandomizerConfig();
        Assert.False(config.ShortenCutscenes);
        Assert.False(config.Dc2DoorSkip);
    }

    // ---- gated real-data integration ----

    private static string? DataDir()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        foreach (var c in new[] { root, Path.Combine(root, "Data") })
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*.dat").Any())
                return c;
        return null;
    }

    [Fact]
    public void RealCorpus_ShortensWhitelistedBrackets_WalkStaysClean()
    {
        var dir = DataDir();
        if (dir is null) return; // gated: no install configured

        int shortened = 0;
        var shortenedRooms = new HashSet<string>();
        // Case-insensitive glob (the St502.dat lesson).
        foreach (var path in Directory.EnumerateFiles(dir, "*.dat", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                     .Where(p => Path.GetFileName(p).StartsWith("st", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant(); // stXYZ
            if (name.Length != 5
                || !int.TryParse(name[2..3], System.Globalization.NumberStyles.HexNumber, null, out int stage)
                || !int.TryParse(name[3..], System.Globalization.NumberStyles.HexNumber, null, out int room))
                continue;
            RoomFile rf;
            try { rf = RoomFile.Read(stage, room, File.ReadAllBytes(path)); }
            catch { continue; }
            var rdt = rf.RdtBuffer;
            if (rdt.Length == 0) continue;

            int n = CutsceneShortener.Shorten(rdt);
            if (n > 0)
            {
                shortened += n;
                shortenedRooms.Add(name[2..]);
                // walk integrity: after the rewrite every bracket is still FOUND (open/close intact)
                // and its close op sits on a clean, reachable opcode boundary — a derail into the
                // 0x00 tiles would break both. (Branch-site COUNT may legally shrink: choreography
                // runs can contain branch ops that the rewrite kills.)
                var after = CutsceneShortener.FindBrackets(rdt);
                Assert.NotEmpty(after);
                foreach (var b in after)
                    Assert.NotEqual(-1, ScriptInjector.SubroutineAtBoundary(rdt, b.CloseOffset));
                // idempotent: a second pass changes nothing.
                var once = (byte[])rdt.Clone();
                CutsceneShortener.Shorten(rdt);
                Assert.Equal(once, rdt);
            }
        }

        // cont.74 census: 65 eligible brackets corpus-wide; the C# classifier is allowed to be
        // stricter, but the lever is pointless below this floor.
        Assert.True(shortened >= 60, $"only {shortened} brackets shortened");
        Assert.Contains("202", shortenedRooms); // pilot
        Assert.Contains("10f", shortenedRooms); // pilot
    }
}

/// <summary>
/// DC2 REbirth <c>DoorSkip</c> passthrough (CUTSCENE-SKIP-FEASIBILITY.md §5/K115): one ini key in
/// the <c>[DLL]</c> section of <c>rebirth/config.ini</c>, owned by REbirth's ddraw.dll.
/// </summary>
public class Dc2DoorSkipInstallerTests
{
    [Fact]
    public void ApplyToIni_FlipsExistingKey_PreservingEverythingElse()
    {
        const string ini = "[GAME]\r\nDisplayMode = 2\r\n\r\n[DLL]\r\nSMAA = 1\r\nDoorSkip = 0\r\nMotionTrail = 1\r\n";
        string outp = Dc2DoorSkipInstaller.ApplyToIni(ini);
        Assert.Contains("DoorSkip = 1", outp);
        Assert.DoesNotContain("DoorSkip = 0", outp);
        Assert.Contains("DisplayMode = 2", outp);
        Assert.Contains("SMAA = 1", outp);
        Assert.Contains("MotionTrail = 1", outp);
    }

    [Fact]
    public void ApplyToIni_AddsKeyWhenMissing_InDllSection()
    {
        const string ini = "[GAME]\r\nDisplayMode = 2\r\n\r\n[DLL]\r\nSMAA = 1\r\n";
        string outp = Dc2DoorSkipInstaller.ApplyToIni(ini);
        Assert.Contains("DoorSkip = 1", outp);
        // inserted under [DLL], not [GAME]
        Assert.True(outp.IndexOf("DoorSkip = 1", StringComparison.Ordinal)
                    > outp.IndexOf("[DLL]", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyToIni_IsIdempotent()
    {
        const string ini = "[DLL]\r\nDoorSkip = 0\r\n";
        string once = Dc2DoorSkipInstaller.ApplyToIni(ini);
        Assert.Equal(once, Dc2DoorSkipInstaller.ApplyToIni(once));
    }
}
