using System.Linq;
using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for <see cref="ExePatcher"/> — the byte-level DINO.exe patcher. The section-rule
/// (<see cref="ExePatcher.VaToFileOffset"/>) is the safety-critical surface: it must map file-backed
/// VAs with zero delta and refuse every runtime-only / non-raw address so a BSS write can never reach
/// disk. Verified facts encoded here come from <c>docs/dc1/EXE-PATCH-PER-ROOM-PLAN.md</c> and
/// <c>docs/dc1/REGION-INDEX-MAP.md</c>.
/// </summary>
public class ExePatcherTests
{
    // ---- VaToFileOffset: the section rule ----

    [Theory]
    [InlineData(0x00401000u, 0x001000)] // .text start
    [InlineData(0x00647050u, 0x247050)] // population table (doc-cited)
    [InlineData(0x0064705Au, 0x24705A)] // record[0] base
    [InlineData(0x00647080u, 0x247080)] // stage-1 record +0x0E (CE-verified)
    [InlineData(0x00647098u, 0x247098)] // stage-2 record +0x0E (CE-verified)
    [InlineData(0x0066FFFFu, 0x26FFFF)] // last byte before .data raw end
    public void VaToFileOffset_FileBacked_MapsWithZeroDelta(uint va, int expected)
        => Assert.Equal(expected, ExePatcher.VaToFileOffset(va));

    [Theory]
    [InlineData(0x006DE990u)] // installed-record ptr — BSS, not file-backed
    [InlineData(0x006DD8F0u)] // current-room word — BSS, the index byte's home
    [InlineData(0x00670000u)] // exactly the .data raw end (exclusive bound)
    [InlineData(0x006D3E68u)] // game-state — BSS
    [InlineData(0x00400FFFu)] // inside the PE header, below .text
    [InlineData(0x003FFFFFu)] // below the image base
    public void VaToFileOffset_RuntimeOrNonRaw_Throws(uint va)
        => Assert.Throws<ArgumentOutOfRangeException>(() => ExePatcher.VaToFileOffset(va));

    [Theory]
    [InlineData(0x00647050u, true)]
    [InlineData(0x006DE990u, false)]
    [InlineData(0x00670000u, false)]
    [InlineData(0x0046EE00u, true)] // a setup fn lives in .text
    public void IsFileBacked_MatchesWindow(uint va, bool expected)
        => Assert.Equal(expected, ExePatcher.IsFileBacked(va));

    // ---- Record-table helpers ----

    [Fact]
    public void RecordVa_And_SetupFnFieldVa_MatchVerifiedOffsets()
    {
        // record[0] base, stride 0x18, setup-fn field +0x0E.
        Assert.Equal(0x0064705Au, ExePatcher.RecordVa(0));
        Assert.Equal(0x00647072u, ExePatcher.RecordVa(1));
        Assert.Equal(0x0064708Au, ExePatcher.RecordVa(2));
        // The CE-verified setup-fn field offsets for stages 1 and 2.
        Assert.Equal(0x00647080u, ExePatcher.SetupFnFieldVa(1));
        Assert.Equal(0x00647098u, ExePatcher.SetupFnFieldVa(2));
    }

    [Fact]
    public void RecordVa_NegativeIndex_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ExePatcher.RecordVa(-1));

    // ---- Read / write round-trips on a synthetic image ----

    /// <summary>A buffer just large enough to hold the file-backed window we touch.</summary>
    private static byte[] NewImage() => new byte[ExePatcher.FileBackedRvaHi];

    [Fact]
    public void WriteThenRead_AtVa_RoundTrips()
    {
        var exe = NewImage();
        ExePatcher.WriteUInt32AtVa(exe, 0x00647098u, 0xDEADBEEF);
        Assert.Equal(0xDEADBEEFu, ExePatcher.ReadUInt32AtVa(exe, 0x00647098u));
        // The bytes really landed at file offset 0x247098, little-endian.
        Assert.Equal(0xEF, exe[0x247098]);
        Assert.Equal(0xDE, exe[0x24709B]);
    }

    [Fact]
    public void RepointSetupFn_WritesDonor_AndReturnsPrevious()
    {
        var exe = NewImage();
        // Seed stage-2's record +0x0E with the original stage-2 fn.
        ExePatcher.WriteUInt32AtVa(exe, ExePatcher.SetupFnFieldVa(2), ExePatcher.SetupFnStage2);

        uint previous = ExePatcher.RepointSetupFn(exe, 2, ExePatcher.SetupFnBasicRaptor);

        Assert.Equal(ExePatcher.SetupFnStage2, previous);
        Assert.Equal(ExePatcher.SetupFnBasicRaptor, ExePatcher.ReadSetupFn(exe, 2));
    }

    [Fact]
    public void RepointSetupFn_RejectsNonFileBackedDonor()
    {
        var exe = NewImage();
        // [0x6DE990] is BSS — not a valid code pointer to write.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExePatcher.RepointSetupFn(exe, 2, 0x006DE990u));
    }

    [Fact]
    public void Read_OutOfBounds_Throws()
    {
        var small = new byte[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => ExePatcher.ReadUInt32(small, 2));
    }

    // ---- Door skip (cont.78): reversible two-window patch ----

    /// <summary>Seed the two door-skip sites with their pristine bytes (skip-swing prologue + `cmp edx,0x3c`).</summary>
    private static byte[] NewImageWithDoorSitesPristine()
    {
        var exe = NewImage();
        byte[] swing = { 0xC7, 0x45, 0xF0, 0x00, 0x00, 0x80, 0x1F, 0x81, 0x7D, 0xF0, 0x00, 0x00, 0x80, 0x1F };
        int a = ExePatcher.VaToFileOffset(ExePatcher.DoorSkipSwingVa);
        swing.CopyTo(exe, a);
        int g = ExePatcher.VaToFileOffset(ExePatcher.DoorHoldGateVa);
        exe[g] = 0x83; exe[g + 1] = 0xFA; exe[g + 2] = ExePatcher.DoorHoldPristine; // cmp edx, 0x3c
        return exe;
    }

    [Fact]
    public void ApplyDoorSkip_PatchesBothWindows_AndIsDetected()
    {
        var exe = NewImageWithDoorSitesPristine();
        Assert.False(ExePatcher.IsDoorSkipApplied(exe));

        ExePatcher.ApplyDoorSkip(exe);

        // A: skip-swing prologue rewritten to `mov eax,[ebp+8]; mov word[eax+2],1; jmp 0x47149a`.
        int a = ExePatcher.VaToFileOffset(ExePatcher.DoorSkipSwingVa);
        byte[] expected = { 0x8B, 0x45, 0x08, 0x66, 0xC7, 0x40, 0x02, 0x01, 0x00, 0xE9, 0x6A, 0x03, 0x00, 0x00 };
        Assert.Equal(expected, exe.Skip(a).Take(expected.Length).ToArray());
        // B: the 60-frame hold immediate is lowered; the `cmp edx` opcode is untouched.
        int g = ExePatcher.VaToFileOffset(ExePatcher.DoorHoldGateVa);
        Assert.Equal(0x83, exe[g]);
        Assert.Equal(0xFA, exe[g + 1]);
        Assert.Equal(ExePatcher.DoorHoldPatched, exe[g + 2]);
        Assert.True(ExePatcher.IsDoorSkipApplied(exe));
    }

    [Fact]
    public void ApplyDoorSkip_IsIdempotent()
    {
        var exe = NewImageWithDoorSitesPristine();
        ExePatcher.ApplyDoorSkip(exe);
        var once = (byte[])exe.Clone();
        ExePatcher.ApplyDoorSkip(exe); // second apply must be a no-op, not a throw
        Assert.Equal(once, exe);
    }

    [Fact]
    public void ApplyDoorSkip_RefusesUnexpectedSwingBytes()
    {
        var exe = NewImageWithDoorSitesPristine();
        exe[ExePatcher.VaToFileOffset(ExePatcher.DoorSkipSwingVa)] ^= 0xFF; // corrupt the window
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ApplyDoorSkip(exe));
    }

    [Fact]
    public void ApplyDoorSkip_RefusesWrongHoldGuard()
    {
        var exe = NewImageWithDoorSitesPristine();
        exe[ExePatcher.VaToFileOffset(ExePatcher.DoorHoldGateVa)] = 0x90; // not `cmp edx,imm8`
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ApplyDoorSkip(exe));
    }

    // ---- Fast-forward cutscenes (cont.79 v2): reversible hook + code cave ----

    /// <summary>Seed the hook site with the pristine `call 0x46AA41` (E8 AB B5 FF FF); the cave stays zero-slack.</summary>
    private static byte[] NewImageWithFfHookPristine()
    {
        var exe = NewImage();
        byte[] hook = { 0xE8, 0xAB, 0xB5, 0xFF, 0xFF };
        hook.CopyTo(exe, ExePatcher.VaToFileOffset(ExePatcher.CutsceneFfHookVa));
        return exe;
    }

    [Fact]
    public void ApplyCutsceneFastForward_PatchesHookAndCave_AndIsDetected()
    {
        var exe = NewImageWithFfHookPristine();
        Assert.False(ExePatcher.IsCutsceneFastForwardApplied(exe));

        ExePatcher.ApplyCutsceneFastForward(exe);

        // Hook rewritten to `call CutsceneFfCaveVa` (rel32 = cave - (hook+5)).
        int h = ExePatcher.VaToFileOffset(ExePatcher.CutsceneFfHookVa);
        int rel = unchecked((int)(ExePatcher.CutsceneFfCaveVa - (ExePatcher.CutsceneFfHookVa + 5)));
        byte[] expectedHook = { 0xE8, (byte)rel, (byte)(rel >> 8), (byte)(rel >> 16), (byte)(rel >> 24) };
        Assert.Equal(expectedHook, exe.Skip(h).Take(5).ToArray());
        // Cave first byte is a `call` (E8) and the cave is no longer zero-slack.
        int c = ExePatcher.VaToFileOffset(ExePatcher.CutsceneFfCaveVa);
        Assert.Equal(0xE8, exe[c]);
        Assert.True(ExePatcher.IsCutsceneFastForwardApplied(exe));
    }

    [Fact]
    public void ApplyCutsceneFastForward_IsIdempotent()
    {
        var exe = NewImageWithFfHookPristine();
        ExePatcher.ApplyCutsceneFastForward(exe);
        var once = (byte[])exe.Clone();
        ExePatcher.ApplyCutsceneFastForward(exe); // second apply must be a no-op, not a throw
        Assert.Equal(once, exe);
    }

    [Fact]
    public void ApplyCutsceneFastForward_RefusesUnexpectedHookBytes()
    {
        var exe = NewImageWithFfHookPristine();
        exe[ExePatcher.VaToFileOffset(ExePatcher.CutsceneFfHookVa)] ^= 0xFF; // not the pristine call
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ApplyCutsceneFastForward(exe));
    }

    [Fact]
    public void ApplyCutsceneFastForward_RefusesDirtyCave()
    {
        var exe = NewImageWithFfHookPristine();
        exe[ExePatcher.VaToFileOffset(ExePatcher.CutsceneFfCaveVa) + 3] = 0x42; // cave not zero-slack, not our cave
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ApplyCutsceneFastForward(exe));
    }

    [Fact]
    public void RepointSetupFn_IsReversible()
    {
        var exe = NewImage();
        ExePatcher.WriteUInt32AtVa(exe, ExePatcher.SetupFnFieldVa(1), ExePatcher.SetupFnBasicRaptor);
        var pristine = (byte[])exe.Clone();

        uint prev = ExePatcher.RepointSetupFn(exe, 1, ExePatcher.SetupFnStage2);
        Assert.NotEqual(prev, ExePatcher.ReadSetupFn(exe, 1));

        // Restoring the captured previous value yields a byte-identical image.
        ExePatcher.RepointSetupFn(exe, 1, prev);
        Assert.Equal(pristine, exe);
    }

    // ---- Surgical per-category AI-handler slot (cont.30 / Theri cat8) ----

    [Fact]
    public void SetRecordCategoryHandler_WritesSlot_ReturnsPrevious_AtVerifiedOffset()
    {
        var exe = NewImage();
        uint slotVa = ExePatcher.Stage2AiRecordVa + 8 * 4; // cat8 slot = 0x656EAC
        ExePatcher.WriteUInt32AtVa(exe, slotVa, 0x004B3540u); // the free stub stage-2 ships with

        uint previous = ExePatcher.SetRecordCategoryHandler(
            exe, ExePatcher.Stage2AiRecordVa, 8, ExePatcher.TheriCat8HandlerVa);

        Assert.Equal(0x004B3540u, previous);
        Assert.Equal(ExePatcher.TheriCat8HandlerVa, ExePatcher.ReadUInt32AtVa(exe, slotVa));
        // VA 0x656EAC maps to file 0x256EAC (raw == virtual).
        Assert.Equal(0x256EAC, ExePatcher.VaToFileOffset(slotVa));
    }

    [Fact]
    public void SetRecordCategoryHandler_RejectsNonFileBackedHandler()
    {
        var exe = NewImage();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExePatcher.SetRecordCategoryHandler(exe, ExePatcher.Stage2AiRecordVa, 8, 0x006DE990u));
    }

    [Fact]
    public void SetRecordCategoryHandler_RejectsOutOfRangeCategory()
    {
        var exe = NewImage();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExePatcher.SetRecordCategoryHandler(exe, ExePatcher.Stage2AiRecordVa, 32, ExePatcher.TheriCat8HandlerVa));
    }

    [Fact]
    public void SetRecordCategoryHandler_IsReversible()
    {
        var exe = NewImage();
        ExePatcher.WriteUInt32AtVa(exe, ExePatcher.Stage2AiRecordVa + 8 * 4, 0x004B3540u);
        var pristine = (byte[])exe.Clone();

        uint prev = ExePatcher.SetRecordCategoryHandler(exe, ExePatcher.Stage2AiRecordVa, 8, ExePatcher.TheriCat8HandlerVa);
        ExePatcher.SetRecordCategoryHandler(exe, ExePatcher.Stage2AiRecordVa, 8, prev);
        Assert.Equal(pristine, exe);
    }

    // ---- Swarm cat5 handler (stage-1 cat5 free; handler 0x5C8116, EXE-SYMBOLS) ----

    [Fact]
    public void SwarmCat5Handler_IsAFileBackedCodeAddress()
        => Assert.True(ExePatcher.IsFileBacked(ExePatcher.SwarmCat5HandlerVa));

    [Fact]
    public void SetRecordCategoryHandler_Swarm_WritesStage1Cat5Slot_ReturnsPrevious_AtVerifiedOffset()
    {
        var exe = NewImage();
        uint slotVa = ExePatcher.Stage1AiRecordVa + 5 * 4; // cat5 slot = 0x657370 (verified NULL/free pristine)

        uint previous = ExePatcher.SetRecordCategoryHandler(
            exe, ExePatcher.Stage1AiRecordVa, 5, ExePatcher.SwarmCat5HandlerVa);

        Assert.Equal(0u, previous); // stage-1 cat5 is free
        Assert.Equal(ExePatcher.SwarmCat5HandlerVa, ExePatcher.ReadUInt32AtVa(exe, slotVa));
        // VA 0x657370 maps to file 0x257370 (raw == virtual).
        Assert.Equal(0x257370, ExePatcher.VaToFileOffset(slotVa));
    }

    [Fact]
    public void SetRecordCategoryHandler_Swarm_TouchesExactlyOneDword_AndIsByteReversible()
    {
        var exe = NewImage();
        var pristine = (byte[])exe.Clone();

        // The patch writes exactly the cat5 slot dword; restoring that dword (as the installer's pristine
        // backup does) returns the image byte-identical. (SetRecordCategoryHandler can't write the free slot
        // back to its NULL/0 value — it rejects a non-file-backed handler — so reversal is a raw dword write,
        // which is what GameInstaller.Restore performs from the backup.)
        uint prev = ExePatcher.SetRecordCategoryHandler(exe, ExePatcher.Stage1AiRecordVa, 5, ExePatcher.SwarmCat5HandlerVa);
        Assert.Equal(0u, prev);
        ExePatcher.WriteUInt32AtVa(exe, ExePatcher.Stage1AiRecordVa + 5 * 4, prev);
        Assert.Equal(pristine, exe);
    }

    // ---- Cat-8 hit/death descriptor redirect (defect B) ----

    /// <summary>10 distinct 0x14-byte records (table17[0..4] then table15[0..4]) with recognizable bytes.</summary>
    private static byte[] SampleDescriptorRecords()
    {
        int total = ExePatcher.HitDescriptorTotalRecords * ExePatcher.HitDescriptorRecordSize;
        var recs = new byte[total];
        for (int i = 0; i < total; i++) recs[i] = (byte)(0x40 + i); // all non-zero, all distinct mod 256
        return recs;
    }

    [Fact]
    public void RedirectCat8HitDescriptors_WritesCave_AndRepointsBothTables()
    {
        var exe = NewImage();
        var recs = SampleDescriptorRecords();

        uint[] prev = ExePatcher.RedirectCat8HitDescriptors(exe, recs);

        // Cave holds the records verbatim.
        int caveOff = ExePatcher.VaToFileOffset(ExePatcher.HitDescriptorCaveVa);
        Assert.Equal(recs, exe.Skip(caveOff).Take(recs.Length).ToArray());

        // table17[0..4] then table15[0..4] now point at consecutive cave records.
        int r = 0;
        foreach (uint tableVa in new[] { ExePatcher.Cat8HitTable17Va, ExePatcher.Cat8HitTable15Va })
            for (int i = 0; i < ExePatcher.HitDescriptorIndexCount; i++, r++)
            {
                uint expectedRecordVa = ExePatcher.HitDescriptorCaveVa + (uint)r * (uint)ExePatcher.HitDescriptorRecordSize;
                Assert.Equal(expectedRecordVa, ExePatcher.ReadUInt32AtVa(exe, tableVa + (uint)i * 4));
                // Every redirected entry is non-file-form, so the engine's relocations leave it verbatim.
                Assert.True(expectedRecordVa < 0x80000000u);
            }
        Assert.Equal(ExePatcher.HitDescriptorTotalRecords, prev.Length);
    }

    [Fact]
    public void RedirectCat8HitDescriptors_ReturnsPreviousTableDwords()
    {
        var exe = NewImage();
        // Seed the 10 table entries with the real file-form values they ship with.
        uint[] seed17 = { 0x8013bf14, 0x8013e800, 0x801592dc, 0x8013d08c, 0x8013e9fc };
        uint[] seed15 = { 0x8013c0e0, 0x8013e9cc, 0x801594a8, 0x8013d258, 0x8013ebc8 };
        for (int i = 0; i < 5; i++) ExePatcher.WriteUInt32AtVa(exe, ExePatcher.Cat8HitTable17Va + (uint)i * 4, seed17[i]);
        for (int i = 0; i < 5; i++) ExePatcher.WriteUInt32AtVa(exe, ExePatcher.Cat8HitTable15Va + (uint)i * 4, seed15[i]);

        uint[] prev = ExePatcher.RedirectCat8HitDescriptors(exe, SampleDescriptorRecords());

        Assert.Equal(seed17.Concat(seed15).ToArray(), prev);
    }

    [Fact]
    public void RedirectCat8HitDescriptors_RejectsWrongLength()
    {
        var exe = NewImage();
        Assert.Throws<ArgumentException>(() => ExePatcher.RedirectCat8HitDescriptors(exe, new byte[0x13]));
    }

    [Fact]
    public void RedirectCat8HitDescriptors_RefusesNonZeroCave()
    {
        var exe = NewImage();
        // Dirty one byte of the cave region → must refuse (don't overwrite code).
        exe[ExePatcher.VaToFileOffset(ExePatcher.HitDescriptorCaveVa) + 7] = 0xCC;
        Assert.Throws<InvalidOperationException>(
            () => ExePatcher.RedirectCat8HitDescriptors(exe, SampleDescriptorRecords()));
    }

    [Fact]
    public void RedirectCat8HitDescriptors_ReapplyIsIdempotent()
    {
        var exe = NewImage();
        var recs = SampleDescriptorRecords();
        ExePatcher.RedirectCat8HitDescriptors(exe, recs);
        var afterFirst = (byte[])exe.Clone();
        // Re-applying the same records (cave already filled) must succeed and be a no-op.
        ExePatcher.RedirectCat8HitDescriptors(exe, recs);
        Assert.Equal(afterFirst, exe);
    }

    [Fact]
    public void RedirectCat8HitDescriptors_RejectsCaveCrossingTextRawEnd()
    {
        var exe = NewImage();
        uint tooHigh = ExePatcher.TextRawEndVa - 4; // 10*0x14 bytes would cross 0x620000
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ExePatcher.RedirectCat8HitDescriptors(exe, SampleDescriptorRecords(), tooHigh));
    }

    [Fact]
    public void RedirectCat8HitDescriptors_DefaultCave_StaysInTextRawSlack()
    {
        uint end = ExePatcher.HitDescriptorCaveVa
                   + (uint)(ExePatcher.HitDescriptorTotalRecords * ExePatcher.HitDescriptorRecordSize);
        Assert.True(ExePatcher.IsFileBacked(ExePatcher.HitDescriptorCaveVa));
        Assert.True(end <= ExePatcher.TextRawEndVa);
    }

    // ---- Corrected defect-B fix: cat-8 hit-REACTION stream redirect (live-verified) ----

    private static byte[] SampleReactionStream()
    {
        var s = new byte[ExePatcher.Cat8ReactionStreamBytes];
        for (int i = 0; i < s.Length; i++) s[i] = (byte)(0x40 + (i % 0xb0)); // all non-zero
        return s;
    }

    /// <summary>Seed the whole reaction-table region with realistic file-form descriptor entries and a few
    /// code-handler entries (which must be left untouched), returning the list of file-form VAs.</summary>
    private static List<uint> SeedReactionTables(byte[] exe)
    {
        var fileForm = new List<uint>();
        uint d = 0x8013B000u;
        for (uint va = ExePatcher.Cat8ReactionTableLoVa; va < ExePatcher.Cat8ReactionTableHiVa; va += 4)
        {
            // Sprinkle code-handler entries (every 11th) like the real tables' index-≥5 slots.
            if ((va / 4) % 11 == 0) { ExePatcher.WriteUInt32AtVa(exe, va, 0x0056DBC9u); continue; }
            ExePatcher.WriteUInt32AtVa(exe, va, d); d += 0x20; fileForm.Add(va);
        }
        return fileForm;
    }

    [Fact]
    public void RedirectCat8HitReaction_TableRegion_CoversAllThreeCat8Clusters()
    {
        // Clusters 1 (0x6639FC..0x663B00) + 2 (0x663DE8..0x663DF8) + 3 (0x6640D8..0x664138); stops at the
        // cat-3 boss tables (0x6641EC, reader func 0x577445).
        Assert.Equal(0x006639FCu, ExePatcher.Cat8ReactionTableLoVa);
        Assert.Equal(0x006641ECu, ExePatcher.Cat8ReactionTableHiVa);
    }

    [Fact]
    public void RedirectCat8HitReaction_WritesCave_AndRepointsEveryFileFormEntry_LeavingCodeHandlers()
    {
        var exe = NewImage();
        var fileForm = SeedReactionTables(exe);
        var stream = SampleReactionStream();

        uint[] prev = ExePatcher.RedirectCat8HitReaction(exe, stream);

        int caveOff = ExePatcher.VaToFileOffset(ExePatcher.HitDescriptorCaveVa);
        Assert.Equal(stream, exe.Skip(caveOff).Take(stream.Length).ToArray());
        Assert.Equal(fileForm.Count, prev.Length);
        // Every file-form entry now points at the cave; code-handler entries are untouched.
        for (uint va = ExePatcher.Cat8ReactionTableLoVa; va < ExePatcher.Cat8ReactionTableHiVa; va += 4)
        {
            uint v = ExePatcher.ReadUInt32AtVa(exe, va);
            if (fileForm.Contains(va)) Assert.Equal(ExePatcher.HitDescriptorCaveVa, v);
            else Assert.Equal(0x0056DBC9u, v);
        }
    }

    [Fact]
    public void RedirectCat8HitReaction_ReturnsPreviousTableDwords()
    {
        var exe = NewImage();
        var fileForm = SeedReactionTables(exe);
        var before = fileForm.Select(va => ExePatcher.ReadUInt32AtVa(exe, va)).ToArray();

        uint[] prev = ExePatcher.RedirectCat8HitReaction(exe, SampleReactionStream());

        Assert.Equal(before, prev); // ascending-VA order, all file-form values captured
    }

    [Fact]
    public void RedirectCat8HitReaction_RefusesNonZeroCave()
    {
        var exe = NewImage();
        exe[ExePatcher.VaToFileOffset(ExePatcher.HitDescriptorCaveVa) + 5] = 0xCC;
        Assert.Throws<InvalidOperationException>(
            () => ExePatcher.RedirectCat8HitReaction(exe, SampleReactionStream()));
    }

    [Fact]
    public void RedirectCat8HitReaction_ReapplyIsIdempotent()
    {
        var exe = NewImage();
        SeedReactionTables(exe);
        var stream = SampleReactionStream();
        ExePatcher.RedirectCat8HitReaction(exe, stream);
        var afterFirst = (byte[])exe.Clone();
        ExePatcher.RedirectCat8HitReaction(exe, stream); // re-apply: cave already filled, entries already cave
        Assert.Equal(afterFirst, exe);
    }

    [Fact]
    public void RedirectCat8HitReaction_IsReversible()
    {
        var exe = NewImage();
        var fileForm = SeedReactionTables(exe);
        var pristine = fileForm.Select(va => ExePatcher.ReadUInt32AtVa(exe, va)).ToArray();

        uint[] prev = ExePatcher.RedirectCat8HitReaction(exe, SampleReactionStream());
        for (int i = 0; i < prev.Length; i++)
            ExePatcher.WriteUInt32AtVa(exe, fileForm[i], prev[i]); // restore captured dwords
        Assert.Equal(pristine, fileForm.Select(va => ExePatcher.ReadUInt32AtVa(exe, va)).ToArray());
    }

    [Fact]
    public void RedirectCat8HitReaction_RejectsEmptyStream()
        => Assert.Throws<ArgumentException>(() => ExePatcher.RedirectCat8HitReaction(NewImage(), new byte[0]));

    [Fact]
    public void RedirectCat8HitReaction_DefaultCave_StaysInTextRawSlack()
    {
        uint end = ExePatcher.HitDescriptorCaveVa + (uint)ExePatcher.Cat8ReactionStreamBytes;
        Assert.True(ExePatcher.IsFileBacked(ExePatcher.HitDescriptorCaveVa));
        Assert.True(end <= ExePatcher.TextRawEndVa);
    }

    // ---- Universal walker NULL-guard (defect B) ----

    [Fact]
    public void InstallWalkerNullGuard_WritesCave_AndJmpFromWalker_ReturnsOriginal()
    {
        var exe = NewImage();
        // Seed the original walker bytes at WalkerVa so we can confirm they're captured for reversal.
        byte[] orig = { 0x55, 0x8B, 0xEC, 0x8B, 0x45 };
        for (int i = 0; i < 5; i++) exe[ExePatcher.VaToFileOffset(ExePatcher.WalkerVa) + i] = orig[i];

        byte[] prev = ExePatcher.InstallWalkerNullGuard(exe);

        Assert.Equal(orig, prev);
        // WalkerVa now holds a jmp rel32 to the cave.
        int jmpOff = ExePatcher.VaToFileOffset(ExePatcher.WalkerVa);
        Assert.Equal(0xE9, exe[jmpOff]);
        int rel = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(jmpOff + 1, 4));
        Assert.Equal((int)ExePatcher.WalkerCaveVa, (int)ExePatcher.WalkerVa + 5 + rel);
        // Cave starts with `push ebp; mov ebp,esp` and ends with `ret`.
        int caveOff = ExePatcher.VaToFileOffset(ExePatcher.WalkerCaveVa);
        Assert.Equal(0x55, exe[caveOff]);
        Assert.Equal(0x8B, exe[caveOff + 1]);
        Assert.Equal(0xC3, exe[caveOff + 36]); // 37-byte stub, last byte = ret
    }

    [Fact]
    public void InstallWalkerNullGuard_CaveSitsRightAfterDescriptorCave_InTextSlack()
    {
        Assert.Equal(ExePatcher.HitDescriptorCaveVa + (uint)ExePatcher.Cat8ReactionStreamBytes, ExePatcher.WalkerCaveVa);
        Assert.True(ExePatcher.IsFileBacked(ExePatcher.WalkerCaveVa));
        Assert.True(ExePatcher.WalkerCaveVa + 37 <= ExePatcher.TextRawEndVa);
    }

    [Fact]
    public void InstallWalkerNullGuard_ReapplyIsIdempotent()
    {
        var exe = NewImage();
        ExePatcher.InstallWalkerNullGuard(exe);
        var afterFirst = (byte[])exe.Clone();
        ExePatcher.InstallWalkerNullGuard(exe); // cave already filled, jmp already written
        Assert.Equal(afterFirst, exe);
    }

    // ---- Render-model display-node guard (060F elevator / 0511 cross-stage) ----
    // docs/dc1/ELEVATOR-060F-HANDGUN-CRASH.md. We "simulate the crash" by interpreting the actual guard-cave
    // bytes the patcher installs, driven by the EXACT register patterns captured in logs/crash-logs-handgun/:
    // the per-frame transform pass reaches the deref 0x44D130 (`mov ecx,[eax]`, eax = p = node+0x3C) and AVs
    // whenever p is unmapped. We assert: unpatched + the old NARROW (==0) guard crash on the file-form pattern;
    // the WIDENED guard skips it (and NULL) while still rendering a valid heap header.

    /// <summary>Where control lands after the guard runs: the model-header deref (0x44D130) or the per-slot
    /// loop-continue (0x44D516, "skip this node").</summary>
    private enum GuardOutcome { Deref, Skip }

    /// <summary>A mapped heap model header (the relocated <c>0x09xxxxxx</c> band a valid node carries); anything
    /// outside it (NULL, or a PSX file-form <c>0x80xxxxxx</c>) is unmapped and AVs if dereferenced.</summary>
    private static bool IsMappedHeader(uint p) => p >= 0x08000000u && p < 0x80000000u;

    /// <summary>True when reaching the deref with this <c>p</c> reproduces the access violation at 0x44D130.</summary>
    private static bool WouldCrash(GuardOutcome o, uint p) => o == GuardOutcome.Deref && !IsMappedHeader(p);

    /// <summary>
    /// Interpret the guard cave installed at <paramref name="caveVa"/> in <paramref name="exe"/>, for a node
    /// whose model-header pointer (<c>node+0x3C</c>) is <paramref name="p"/>. Models exactly the opcode forms the
    /// cave uses (mov/test/cmp/jz/jb/jmp/nop) and returns whether control reaches the deref (0x44D130) or the
    /// skip (0x44D516). This executes the REAL installed bytes, so it tracks the patcher, not a paraphrase.
    /// </summary>
    private static GuardOutcome SimulateGuard(byte[] exe, uint caveVa, uint p)
    {
        int baseOff = ExePatcher.VaToFileOffset(caveVa);
        uint edx = 0; bool zf = false, cf = false;
        int ip = 0; // offset within the cave
        for (int guard = 0; guard < 64; guard++)
        {
            byte op = exe[baseOff + ip];
            switch (op)
            {
                case 0x8B when exe[baseOff + ip + 1] == 0x4D && exe[baseOff + ip + 2] == 0xF0: ip += 3; break; // mov ecx,[ebp-0x10] (obj)
                case 0x8B when exe[baseOff + ip + 1] == 0x51 && exe[baseOff + ip + 2] == 0x3C: edx = p; ip += 3; break; // mov edx,[ecx+0x3C]=p
                case 0x8B when exe[baseOff + ip + 1] == 0x45 && exe[baseOff + ip + 2] == 0xFC: ip += 3; break; // mov eax,[ebp-0x04]
                case 0x89 when exe[baseOff + ip + 1] == 0x55 && exe[baseOff + ip + 2] == 0xFC: ip += 3; break; // mov [ebp-0x04],edx
                case 0x85 when exe[baseOff + ip + 1] == 0xD2: zf = edx == 0; cf = false; ip += 2; break; // test edx,edx
                case 0x81 when exe[baseOff + ip + 1] == 0xFA: // cmp edx,imm32
                {
                    uint imm = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(baseOff + ip + 2, 4));
                    cf = edx < imm; zf = edx == imm; ip += 6; break;
                }
                case 0x74: ip = zf ? ip + 2 + (sbyte)exe[baseOff + ip + 1] : ip + 2; break; // jz rel8
                case 0x72: ip = cf ? ip + 2 + (sbyte)exe[baseOff + ip + 1] : ip + 2; break; // jb rel8
                case 0x0F when exe[baseOff + ip + 1] == 0x84: // jz rel32
                {
                    int rel = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(baseOff + ip + 2, 4));
                    ip = zf ? ip + 6 + rel : ip + 6; break;
                }
                case 0x90: ip += 1; break; // nop
                case 0xE9: // jmp rel32 -> resolve to a VA; the two exits are the deref and the loop-continue
                {
                    int rel = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(baseOff + ip + 1, 4));
                    uint target = (uint)((int)caveVa + ip + 5 + rel);
                    if (target == ExePatcher.RenderModelDerefVa) return GuardOutcome.Deref;
                    if (target == ExePatcher.RenderLoopContinueVa) return GuardOutcome.Skip;
                    throw new Xunit.Sdk.XunitException($"cave jmp to unexpected VA 0x{target:X}");
                }
                default:
                    throw new Xunit.Sdk.XunitException($"unhandled cave opcode 0x{op:X2} at cave+0x{ip:X}");
            }
        }
        throw new Xunit.Sdk.XunitException("guard simulation did not terminate");
    }

    // The three primary faults captured in logs/crash-logs-handgun/ (no-suffix dumps): node+0x3C values.
    private const uint Slot33FileFormPtr = 0x8017E000; // PIDs 22896 & 7652 (deterministic 060F re-entry)
    private const uint Slot1NullPtr = 0x00000000;       // PID 9796 (the 0511-style NULL variant)
    private const uint ValidHeapHeader = 0x091E0000;    // a normally-resolved model header (must still render)

    [Fact]
    public void RenderGuard_Unpatched_ReproducesAllThreeDumps_FileFormAndNullBothCrash()
    {
        // Unpatched: sub_44CED6 runs straight to 0x44D130 and derefs p unconditionally.
        Assert.True(WouldCrash(GuardOutcome.Deref, Slot33FileFormPtr));  // 22896/7652
        Assert.True(WouldCrash(GuardOutcome.Deref, Slot1NullPtr));       // 9796
        Assert.False(WouldCrash(GuardOutcome.Deref, ValidHeapHeader));   // a real header renders
    }

    [Fact]
    public void RenderGuard_NarrowEqualsZeroGuard_MissesFileForm_StillCrashes()
    {
        // The deprecated apply_nullguard.py cave (==0 only), built independently here so this characterizes the
        // gap regardless of the patcher's current builder: file-form (0x8017E000) sails through and crashes.
        var exe = NewImage();
        byte[] narrow =
        {
            0x8B,0x4D,0xF0, 0x8B,0x51,0x3C, 0x85,0xD2,
            0x0F,0x84,0x0B,0,0,0,            // jz +0x0B (skip) when p==0
            0x89,0x55,0xFC, 0x8B,0x45,0xFC,
            0xE9,0,0,0,0,                    // 0x14: jmp deref
            0xE9,0,0,0,0,                    // 0x19: jmp loop-continue
        };
        uint caveVa = ExePatcher.RenderGuardCaveVa;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(narrow.AsSpan(0x15, 4),
            (int)ExePatcher.RenderModelDerefVa - (int)(caveVa + 0x19));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(narrow.AsSpan(0x1A, 4),
            (int)ExePatcher.RenderLoopContinueVa - (int)(caveVa + 0x1E));
        narrow.CopyTo(exe, ExePatcher.VaToFileOffset(caveVa));

        Assert.True(WouldCrash(SimulateGuard(exe, caveVa, Slot33FileFormPtr), Slot33FileFormPtr)); // GAP: still crashes
        Assert.False(WouldCrash(SimulateGuard(exe, caveVa, Slot1NullPtr), Slot1NullPtr));          // NULL is caught
    }

    [Fact]
    public void RenderGuard_Widened_SkipsFileFormAndNull_RendersValidHeader()
    {
        var exe = NewImage();
        ExePatcher.InstallRenderModelGuard(exe);
        uint caveVa = ExePatcher.RenderGuardCaveVa;

        // The deterministic 060F re-entry crash (slot 33, file-form) is now SKIPPED, not dereferenced.
        Assert.Equal(GuardOutcome.Skip, SimulateGuard(exe, caveVa, Slot33FileFormPtr));
        Assert.False(WouldCrash(SimulateGuard(exe, caveVa, Slot33FileFormPtr), Slot33FileFormPtr));
        // The NULL variant (slot 1) is still skipped.
        Assert.Equal(GuardOutcome.Skip, SimulateGuard(exe, caveVa, Slot1NullPtr));
        // A valid heap header still takes the normal deref/render path (the guard is not a blanket skip).
        Assert.Equal(GuardOutcome.Deref, SimulateGuard(exe, caveVa, ValidHeapHeader));
        Assert.False(WouldCrash(SimulateGuard(exe, caveVa, ValidHeapHeader), ValidHeapHeader));
    }

    [Fact]
    public void InstallRenderModelGuard_WritesHookJmpAndCave_ReturnsOriginal_InTextSlack()
    {
        var exe = NewImage();
        for (int i = 0; i < ExePatcher.RenderGuardOriginalHook.Length; i++)
            exe[ExePatcher.VaToFileOffset(ExePatcher.RenderTransformHookVa) + i] = ExePatcher.RenderGuardOriginalHook[i];

        byte[] prev = ExePatcher.InstallRenderModelGuard(exe);

        Assert.Equal(ExePatcher.RenderGuardOriginalHook, prev);
        int hookOff = ExePatcher.VaToFileOffset(ExePatcher.RenderTransformHookVa);
        Assert.Equal(0xE9, exe[hookOff]); // jmp rel32 ...
        int rel = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(hookOff + 1, 4));
        Assert.Equal((int)ExePatcher.RenderGuardCaveVa, (int)ExePatcher.RenderTransformHookVa + 5 + rel);
        for (int i = 5; i < 12; i++) Assert.Equal(0x90, exe[hookOff + i]); // ... + 7 nops
        // Cave sits in the .text zero-slack, before the descriptor cave (no collision).
        Assert.True(ExePatcher.IsFileBacked(ExePatcher.RenderGuardCaveVa));
        Assert.True(ExePatcher.RenderGuardCaveVa > 0x0061F792 && ExePatcher.RenderGuardCaveVa < ExePatcher.HitDescriptorCaveVa);
    }

    [Fact]
    public void InstallRenderModelGuard_ReapplyIsIdempotent()
    {
        var exe = NewImage();
        ExePatcher.InstallRenderModelGuard(exe);
        var afterFirst = (byte[])exe.Clone();
        ExePatcher.InstallRenderModelGuard(exe); // cave + hook already present
        Assert.Equal(afterFirst, exe);
    }

    // ---- BGM catalog shuffle (the music randomizer lever; docs/dc1/BGM-SYSTEM.md §4) ----

    /// <summary>The flags class a synthetic id is assigned to (3 classes, ~33 ids each).</summary>
    private static uint BgmFlagsFor(int id) => (id % 3) switch { 0 => 0x2Au, 1 => 0x08u, _ => 0x0Au };

    /// <summary>VA where synthetic record <paramref name="id"/>'s <c>bgm\</c> name string is laid down.</summary>
    private static uint BgmNameVa(int id) => 0x00640000u + (uint)id * 0x20u;

    private static void WriteCStringAtVa(byte[] exe, uint va, string s)
    {
        int off = ExePatcher.VaToFileOffset(va);
        var b = System.Text.Encoding.ASCII.GetBytes(s);
        b.CopyTo(exe, off);
        exe[off + b.Length] = 0;
    }

    /// <summary>Seed a valid synthetic BGM catalog: id 1 = a sentinel version-tag record (must stay put), ids
    /// 2..99 = {namePtr→a unique <c>bgm\</c> string, distinct size, one of 3 flags classes}, plus an SE-like row
    /// right after id 99 (must stay put).</summary>
    private static byte[] NewImageWithBgmCatalog()
    {
        var exe = NewImage();
        // id 1: a recognizable sentinel in every field; the shuffle must never touch it.
        uint r1 = ExePatcher.BgmRecordVa(1);
        ExePatcher.WriteUInt32AtVa(exe, r1 + 0, 0x31333232u); // inline "2231" tag bytes (not a pointer)
        ExePatcher.WriteUInt32AtVa(exe, r1 + 4, 0xAABBCCDDu);
        ExePatcher.WriteUInt32AtVa(exe, r1 + 8, 1u);
        ExePatcher.WriteUInt32AtVa(exe, r1 + 0xC, 0x2Au);
        for (int id = ExePatcher.BgmFirstShuffledId; id <= ExePatcher.BgmRecordCount; id++)
        {
            uint rec = ExePatcher.BgmRecordVa(id);
            WriteCStringAtVa(exe, BgmNameVa(id), $@"bgm\trk_{id:D2}");
            ExePatcher.WriteUInt32AtVa(exe, rec + 0, BgmNameVa(id));     // namePtr
            ExePatcher.WriteUInt32AtVa(exe, rec + 4, (uint)(id * 7 + 1)); // size (distinct, non-zero)
            ExePatcher.WriteUInt32AtVa(exe, rec + 8, (uint)id);           // id field
            ExePatcher.WriteUInt32AtVa(exe, rec + 0xC, BgmFlagsFor(id));  // flags class
        }
        // A post-99 SE-like row (index 99) the shuffle must leave alone.
        uint post = ExePatcher.BgmCatalogBaseVa + (uint)ExePatcher.BgmRecordCount * (uint)ExePatcher.BgmRecordStride;
        WriteCStringAtVa(exe, 0x00650000u, @"se\commmon\zinari1_2");
        ExePatcher.WriteUInt32AtVa(exe, post + 0, 0x00650000u);
        ExePatcher.WriteUInt32AtVa(exe, post + 8, 0x1234u);
        return exe;
    }

    private static uint NamePtr(byte[] exe, int id) => ExePatcher.ReadUInt32AtVa(exe, ExePatcher.BgmRecordVa(id) + 0);
    private static uint Size(byte[] exe, int id) => ExePatcher.ReadUInt32AtVa(exe, ExePatcher.BgmRecordVa(id) + 4);
    private static uint Flags(byte[] exe, int id) => ExePatcher.ReadUInt32AtVa(exe, ExePatcher.BgmRecordVa(id) + 0xC);

    [Fact]
    public void ShuffleBgmCatalog_PermutesWithinFlagsClass_Only()
    {
        var exe = NewImageWithBgmCatalog();
        // Original namePtr set per flags class.
        var byClass = new Dictionary<uint, HashSet<uint>>();
        for (int id = ExePatcher.BgmFirstShuffledId; id <= ExePatcher.BgmRecordCount; id++)
            (byClass.TryGetValue(Flags(exe, id), out var set) ? set : byClass[Flags(exe, id)] = new()).Add(NamePtr(exe, id));

        ExePatcher.ShuffleBgmCatalog(exe, seed: 12345);

        for (int id = ExePatcher.BgmFirstShuffledId; id <= ExePatcher.BgmRecordCount; id++)
        {
            // flags field never moves, and the new name came from a record of the SAME class.
            Assert.Equal(BgmFlagsFor(id), Flags(exe, id));
            Assert.Contains(NamePtr(exe, id), byClass[Flags(exe, id)]);
        }
        // Each class is a true permutation: the multiset of names is preserved exactly.
        var afterByClass = new Dictionary<uint, List<uint>>();
        for (int id = ExePatcher.BgmFirstShuffledId; id <= ExePatcher.BgmRecordCount; id++)
            (afterByClass.TryGetValue(Flags(exe, id), out var l) ? l : afterByClass[Flags(exe, id)] = new()).Add(NamePtr(exe, id));
        foreach (var (cls, set) in byClass)
            Assert.Equal(set.OrderBy(x => x), afterByClass[cls].OrderBy(x => x));
    }

    [Fact]
    public void ShuffleBgmCatalog_CarriesSizeWithNamePtr()
    {
        var exe = NewImageWithBgmCatalog();
        // Map each original namePtr to its size (the pair that must travel together).
        var sizeOf = new Dictionary<uint, uint>();
        for (int id = ExePatcher.BgmFirstShuffledId; id <= ExePatcher.BgmRecordCount; id++)
            sizeOf[NamePtr(exe, id)] = Size(exe, id);

        ExePatcher.ShuffleBgmCatalog(exe, seed: 777);

        for (int id = ExePatcher.BgmFirstShuffledId; id <= ExePatcher.BgmRecordCount; id++)
            Assert.Equal(sizeOf[NamePtr(exe, id)], Size(exe, id));
    }

    [Fact]
    public void ShuffleBgmCatalog_LeavesId1_AndPost99Row_Untouched()
    {
        var exe = NewImageWithBgmCatalog();
        var before = (byte[])exe.Clone();

        ExePatcher.ShuffleBgmCatalog(exe, seed: 42);

        // id 1 record bytes are byte-identical.
        int r1 = ExePatcher.VaToFileOffset(ExePatcher.BgmRecordVa(1));
        Assert.Equal(before.Skip(r1).Take(ExePatcher.BgmRecordStride), exe.Skip(r1).Take(ExePatcher.BgmRecordStride));
        // The post-99 row is byte-identical.
        uint post = ExePatcher.BgmCatalogBaseVa + (uint)ExePatcher.BgmRecordCount * (uint)ExePatcher.BgmRecordStride;
        int postOff = ExePatcher.VaToFileOffset(post);
        Assert.Equal(before.Skip(postOff).Take(ExePatcher.BgmRecordStride), exe.Skip(postOff).Take(ExePatcher.BgmRecordStride));
    }

    [Fact]
    public void ShuffleBgmCatalog_IsDeterministicForSeed()
    {
        var a = NewImageWithBgmCatalog();
        var b = NewImageWithBgmCatalog();
        ExePatcher.ShuffleBgmCatalog(a, seed: 2024);
        ExePatcher.ShuffleBgmCatalog(b, seed: 2024);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ShuffleBgmCatalog_ActuallyMovesSomeRecords()
    {
        var exe = NewImageWithBgmCatalog();
        var entries = ExePatcher.ShuffleBgmCatalog(exe, seed: 99);
        // With ~33 members per class a seeded shuffle must reroute at least some ids (sanity: not a no-op).
        Assert.Contains(entries, e => e.OldNamePtr != e.NewNamePtr);
        // Every entry keeps its id and flags; only the name/size pair moves.
        Assert.All(entries, e => Assert.InRange(e.Id, ExePatcher.BgmFirstShuffledId, ExePatcher.BgmRecordCount));
    }

    [Fact]
    public void ShuffleBgmCatalog_IsReversible_ViaReturnedOldPairs()
    {
        var exe = NewImageWithBgmCatalog();
        var pristine = (byte[])exe.Clone();

        var entries = ExePatcher.ShuffleBgmCatalog(exe, seed: 5);
        Assert.NotEqual(pristine, exe); // something moved

        // Restore each record's original {namePtr,size} from the returned entries → byte-identical.
        foreach (var e in entries)
        {
            uint rec = ExePatcher.BgmRecordVa(e.Id);
            ExePatcher.WriteUInt32AtVa(exe, rec + 0, e.OldNamePtr);
            ExePatcher.WriteUInt32AtVa(exe, rec + 4, e.OldSize);
        }
        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void ShuffleBgmCatalog_RejectsWrongIdField()
    {
        var exe = NewImageWithBgmCatalog();
        ExePatcher.WriteUInt32AtVa(exe, ExePatcher.BgmRecordVa(50) + 8, 999u); // corrupt id field
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ShuffleBgmCatalog(exe, seed: 1));
    }

    [Fact]
    public void ShuffleBgmCatalog_RejectsNonBgmName()
    {
        var exe = NewImageWithBgmCatalog();
        WriteCStringAtVa(exe, BgmNameVa(30), @"se\dino\r_walk"); // not a bgm\ string
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ShuffleBgmCatalog(exe, seed: 1));
    }

    [Fact]
    public void BgmRecordVa_MatchesVerifiedLayout()
    {
        Assert.Equal(0x00625438u, ExePatcher.BgmRecordVa(1));
        Assert.Equal(0x00625448u, ExePatcher.BgmRecordVa(2));   // id 2 = me_00SL record (CE-verified)
        Assert.Equal(0x00625A58u, ExePatcher.BgmRecordVa(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExePatcher.BgmRecordVa(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExePatcher.BgmRecordVa(100));
        // The whole catalog lies in the file-backed window.
        Assert.True(ExePatcher.IsFileBacked(ExePatcher.BgmRecordVa(99) + 0xC));
    }

    // --- Emergency-box content shuffle (the box-content randomizer lever) ----------------------------

    // A synthetic image with each of the three International blocks filled with 17 distinguishable, valid
    // box records: [0x0A][unique itemId, unique count][zero pad]. The unique (id,count) lets us track each
    // record through the permutation.
    private static byte[] NewImageWithBoxTable()
    {
        var exe = NewImage();
        for (int b = 0; b < ExePatcher.EmergencyBoxBlockVas.Length; b++)
        {
            int blockOff = ExePatcher.VaToFileOffset(ExePatcher.EmergencyBoxBlockVas[b]);
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
            {
                int rec = blockOff + i * ExePatcher.EmergencyBoxRecordStride;
                exe[rec] = ExePatcher.EmergencyBoxSlotMarker;       // slot count
                exe[rec + 1] = (byte)(0x10 + i);                    // a unique item id per slot
                exe[rec + 2] = (byte)(1 + b * 17 + i);              // a unique count per (block,slot)
            }
        }
        return exe;
    }

    private static byte[] Record(byte[] exe, uint blockVa, int slot)
    {
        int off = ExePatcher.VaToFileOffset(blockVa) + slot * ExePatcher.EmergencyBoxRecordStride;
        return exe.Skip(off).Take(ExePatcher.EmergencyBoxRecordStride).ToArray();
    }

    [Fact]
    public void ShuffleEmergencyBoxContents_PreservesEachBlocksRecordMultiset()
    {
        var exe = NewImageWithBoxTable();
        var before = new Dictionary<uint, List<string>>();
        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
            before[va] = Enumerable.Range(0, ExePatcher.EmergencyBoxesPerBlock)
                .Select(i => Convert.ToHexString(Record(exe, va, i))).OrderBy(x => x).ToList();

        ExePatcher.ShuffleEmergencyBoxContents(exe, seed: 4242);

        // Each block is a true permutation of its own records: same multiset, no record invented/lost, and
        // no record crossed between difficulty blocks (amounts stay difficulty-appropriate).
        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
        {
            var after = Enumerable.Range(0, ExePatcher.EmergencyBoxesPerBlock)
                .Select(i => Convert.ToHexString(Record(exe, va, i))).OrderBy(x => x).ToList();
            Assert.Equal(before[va], after);
        }
    }

    [Fact]
    public void ShuffleEmergencyBoxContents_ActuallyReorders_AndIsDeterministic()
    {
        var a = NewImageWithBoxTable();
        var before = (byte[])a.Clone();
        var b = NewImageWithBoxTable();

        ExePatcher.ShuffleEmergencyBoxContents(a, seed: 2024);
        ExePatcher.ShuffleEmergencyBoxContents(b, seed: 2024);

        Assert.Equal(a, b);                       // deterministic for a seed
        Assert.NotEqual(before, a);               // it actually moved something
    }

    [Fact]
    public void ShuffleEmergencyBoxContents_TouchesOnlyTheThreeBlocks()
    {
        var exe = NewImageWithBoxTable();
        var before = (byte[])exe.Clone();

        ExePatcher.ShuffleEmergencyBoxContents(exe, seed: 7);

        // Every byte outside the three 17×21 blocks is unchanged.
        var blockRanges = ExePatcher.EmergencyBoxBlockVas
            .Select(va => ExePatcher.VaToFileOffset(va))
            .Select(off => (off, end: off + ExePatcher.EmergencyBoxesPerBlock * ExePatcher.EmergencyBoxRecordStride))
            .ToList();
        for (int i = 0; i < exe.Length; i++)
        {
            bool inBlock = blockRanges.Any(r => i >= r.off && i < r.end);
            if (!inBlock) Assert.Equal(before[i], exe[i]);
        }
    }

    [Fact]
    public void ShuffleEmergencyBoxContents_RejectsUnexpectedBuild()
    {
        var exe = NewImageWithBoxTable();
        // Corrupt one record's slot marker → the offsets don't point at the box table; refuse to shuffle.
        int rec = ExePatcher.VaToFileOffset(ExePatcher.EmergencyBoxBlockHardVa) + 5 * ExePatcher.EmergencyBoxRecordStride;
        exe[rec] = 0x00;
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ShuffleEmergencyBoxContents(exe, seed: 1));
    }

    [Fact]
    public void EmergencyBoxBlocks_LieInTheFileBackedWindow()
    {
        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
        {
            Assert.True(ExePatcher.IsFileBacked(va));
            uint last = va + (uint)(ExePatcher.EmergencyBoxesPerBlock * ExePatcher.EmergencyBoxRecordStride) - 1;
            Assert.True(ExePatcher.IsFileBacked(last));
        }
    }

    // In NewImageWithBoxTable each block's record i carries one pair (id 0x10+i, a unique amount), so the
    // block pool is ids 0x10..0x20 with a single amount each — a bijection we can check the reroll against.
    private static (int id, int amount) BoxRecordPair(byte[] exe, uint blockVa, int slot)
    {
        int rec = ExePatcher.VaToFileOffset(blockVa) + slot * ExePatcher.EmergencyBoxRecordStride;
        return (exe[rec + 1], exe[rec + 2]);
    }

    private static int BoxRecordSlotCount(byte[] exe, uint blockVa, int slot)
    {
        int rec = ExePatcher.VaToFileOffset(blockVa) + slot * ExePatcher.EmergencyBoxRecordStride;
        int n = 0;
        for (int k = 0; k < 10; k++)
        {
            byte id = exe[rec + 1 + 2 * k];
            if (id < ExePatcher.EmergencyBoxFirstItemId || id > ExePatcher.EmergencyBoxLastItemId) break;
            n++;
        }
        return n;
    }

    [Fact]
    public void RerollEmergencyBoxContents_DrawsOnlyFromBlockPool_WithThatItemsAmount()
    {
        var seedExe = NewImageWithBoxTable();
        // Per-block map of the pool: item id -> the (single) amount it appears with in that block.
        var amountOf = new Dictionary<uint, Dictionary<int, int>>();
        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
        {
            var map = new Dictionary<int, int>();
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
            {
                var (id, amt) = BoxRecordPair(seedExe, va, i);
                map[id] = amt;
            }
            amountOf[va] = map;
        }

        var exe = (byte[])seedExe.Clone();
        ExePatcher.RerollEmergencyBoxContents(exe, seed: 909);

        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
            {
                var (id, amt) = BoxRecordPair(exe, va, i);
                Assert.True(amountOf[va].ContainsKey(id), $"rerolled id 0x{id:X} not in block 0x{va:X} pool");
                Assert.Equal(amountOf[va][id], amt); // amount came from that item's vanilla box amount
            }
    }

    [Fact]
    public void RerollEmergencyBoxContents_PreservesSlotCountAndMarker()
    {
        var before = NewImageWithBoxTable();
        var exe = (byte[])before.Clone();
        ExePatcher.RerollEmergencyBoxContents(exe, seed: 17);

        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
            {
                int rec = ExePatcher.VaToFileOffset(va) + i * ExePatcher.EmergencyBoxRecordStride;
                Assert.Equal(ExePatcher.EmergencyBoxSlotMarker, exe[rec]);                 // marker kept
                Assert.Equal(BoxRecordSlotCount(before, va, i), BoxRecordSlotCount(exe, va, i)); // count kept
            }
    }

    [Fact]
    public void RerollEmergencyBoxContents_IsDeterministicForSeed()
    {
        var a = NewImageWithBoxTable();
        var b = NewImageWithBoxTable();
        ExePatcher.RerollEmergencyBoxContents(a, seed: 2024);
        ExePatcher.RerollEmergencyBoxContents(b, seed: 2024);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RerollEmergencyBoxContents_TouchesOnlyTheThreeBlocks()
    {
        var exe = NewImageWithBoxTable();
        var before = (byte[])exe.Clone();
        ExePatcher.RerollEmergencyBoxContents(exe, seed: 7);

        var blockRanges = ExePatcher.EmergencyBoxBlockVas
            .Select(va => ExePatcher.VaToFileOffset(va))
            .Select(off => (off, end: off + ExePatcher.EmergencyBoxesPerBlock * ExePatcher.EmergencyBoxRecordStride))
            .ToList();
        for (int i = 0; i < exe.Length; i++)
            if (!blockRanges.Any(r => i >= r.off && i < r.end))
                Assert.Equal(before[i], exe[i]);
    }

    [Fact]
    public void RerollEmergencyBoxContents_RejectsUnexpectedBuild()
    {
        var exe = NewImageWithBoxTable();
        int rec = ExePatcher.VaToFileOffset(ExePatcher.EmergencyBoxBlockVeryHardVa) + 3 * ExePatcher.EmergencyBoxRecordStride;
        exe[rec] = 0x00; // break a slot marker
        Assert.Throws<InvalidOperationException>(() => ExePatcher.RerollEmergencyBoxContents(exe, seed: 1));
    }

    [Fact]
    public void RerollEmergencyBoxContents_SingleItemBlockPool_RerollsToThatItem()
    {
        var exe = NewImage();
        // Fill every block's records with the same one-slot record (item 0x22 ×1): the pool degenerates to
        // a single (item, amount), so any reroll must reproduce exactly it.
        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
            {
                int rec = ExePatcher.VaToFileOffset(va) + i * ExePatcher.EmergencyBoxRecordStride;
                exe[rec] = ExePatcher.EmergencyBoxSlotMarker;
                exe[rec + 1] = 0x22;
                exe[rec + 2] = 1;
            }
        ExePatcher.RerollEmergencyBoxContents(exe, seed: 123);

        foreach (var va in ExePatcher.EmergencyBoxBlockVas)
            for (int i = 0; i < ExePatcher.EmergencyBoxesPerBlock; i++)
                Assert.Equal((0x22, 1), BoxRecordPair(exe, va, i));
    }

    // --- Emergency boxes: per-difficulty runtime coverage (independent oracle) ------------------------
    //
    // These tests deliberately do NOT use ExePatcher.EmergencyBoxBlockVas to build or select blocks —
    // fixtures derived from the patcher's own constants cannot catch a wrong-VA / missed-difficulty bug.
    // Instead they model the REAL decoded EXE as literals (EMERGENCY-BOX-DATA.md, audit 2026-07-16):
    //   • the table = 7 blocks of 17 × 21-byte records at 0x65AB48 + block*360 (357 data + 3 pad):
    //     [0] JP Easy/Normal, [1] Intl Easy/Normal, [2] special-mode, [3] JP Hard, [4] Intl Hard,
    //     [5] JP VH, [6] Intl VH;
    //   • the runtime selector (fn 0x483313): difficulty d → pointer index (d<=1 ? 0 : d==2 ? 1 : 2)
    //     into the International pointer half {0x65ACB0, 0x65B0E8, 0x65B3B8} (bit7 of the difficulty
    //     byte = International; the build DinoRand ships).
    // Guarantee: for EVERY selectable difficulty the block the game reads is randomized and valid, and
    // the blocks the International runtime never reads (JP + special-mode) stay byte-identical.

    private const uint BoxTableBaseVa = 0x0065AB48;   // real table base (block 0), NOT the patcher const
    private const int BoxBlockStride = 360;           // 17*21 data + 3 pad
    private static uint RealBlockVa(int block) => BoxTableBaseVa + (uint)(block * BoxBlockStride);

    /// <summary>The block VA the game's decoded runtime selector reads for a difficulty (International).</summary>
    private static uint RuntimeBoxBlockVa(int difficulty)
        => RealBlockVa(difficulty <= 1 ? 1 : difficulty == 2 ? 4 : 6);

    private static readonly int[] NonRuntimeBoxBlocks = { 0, 2, 3, 5 }; // JP Easy/N, special-mode, JP Hard, JP VH

    /// <summary>A synthetic image with the REAL 7-block topology: every block filled with 17
    /// distinguishable valid records ([0x0A][unique id in 0x10–0x23][unique count]).</summary>
    private static byte[] NewImageWithRealBoxTopology()
    {
        var exe = NewImage();
        for (int b = 0; b < 7; b++)
            for (int i = 0; i < 17; i++)
            {
                int rec = ExePatcher.VaToFileOffset(RealBlockVa(b)) + i * 21;
                exe[rec] = 0x0A;
                exe[rec + 1] = (byte)(0x10 + i);         // unique item id per slot (0x10..0x20)
                exe[rec + 2] = (byte)(1 + b * 17 + i);   // unique count per (block, slot)
            }
        return exe;
    }

    private static byte[] BlockBytes(byte[] exe, uint blockVa)
        => exe.Skip(ExePatcher.VaToFileOffset(blockVa)).Take(17 * 21).ToArray();

    private static void AssertValidBoxBlock(byte[] exe, uint blockVa)
    {
        int off = ExePatcher.VaToFileOffset(blockVa);
        for (int i = 0; i < 17; i++)
        {
            int rec = off + i * 21;
            Assert.Equal(0x0A, exe[rec]); // slot marker at every 21-byte boundary
            for (int k = 0; k < 10; k++)
            {
                byte id = exe[rec + 1 + 2 * k];
                if (id == 0) break;
                Assert.InRange(id, (byte)0x10, (byte)0x23);
            }
        }
    }

    [Theory]
    [InlineData(0)] // Easy
    [InlineData(1)] // Normal (shares the Easy block via the 0x483313 selector — must still be covered)
    [InlineData(2)] // Hard
    [InlineData(3)] // Very Hard
    public void ShuffleEmergencyBoxContents_RandomizesTheBlockEachDifficultyReads(int difficulty)
    {
        var exe = NewImageWithRealBoxTopology();
        uint runtimeVa = RuntimeBoxBlockVa(difficulty);
        var pristine = BlockBytes(exe, runtimeVa);

        ExePatcher.ShuffleEmergencyBoxContents(exe, seed: 4242);

        Assert.False(BlockBytes(exe, runtimeVa).SequenceEqual(pristine),
            $"difficulty {difficulty}: the block the game reads (0x{runtimeVa:X}) was left vanilla");
        AssertValidBoxBlock(exe, runtimeVa);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RerollEmergencyBoxContents_RandomizesTheBlockEachDifficultyReads(int difficulty)
    {
        var exe = NewImageWithRealBoxTopology();
        uint runtimeVa = RuntimeBoxBlockVa(difficulty);
        var pristine = BlockBytes(exe, runtimeVa);

        ExePatcher.RerollEmergencyBoxContents(exe, seed: 777);

        Assert.False(BlockBytes(exe, runtimeVa).SequenceEqual(pristine),
            $"difficulty {difficulty}: the block the game reads (0x{runtimeVa:X}) was left vanilla");
        AssertValidBoxBlock(exe, runtimeVa);
    }

    [Fact]
    public void EmergencyBoxPatches_LeaveBlocksTheRuntimeNeverReadsUntouched()
    {
        // The International runtime never reads the JP blocks or the special-mode block (mode-2 override,
        // identity unconfirmed) — both modes must leave all four byte-identical, pads included.
        foreach (var patch in new[]
                 {
                     (Action<byte[]>)(e => ExePatcher.ShuffleEmergencyBoxContents(e, seed: 4242)),
                     e => ExePatcher.RerollEmergencyBoxContents(e, seed: 777),
                 })
        {
            var exe = NewImageWithRealBoxTopology();
            var pristine = NonRuntimeBoxBlocks.Select(b =>
                exe.Skip(ExePatcher.VaToFileOffset(RealBlockVa(b))).Take(BoxBlockStride).ToArray()).ToArray();

            patch(exe);

            for (int n = 0; n < NonRuntimeBoxBlocks.Length; n++)
                Assert.True(exe.Skip(ExePatcher.VaToFileOffset(RealBlockVa(NonRuntimeBoxBlocks[n])))
                        .Take(BoxBlockStride).SequenceEqual(pristine[n]),
                    $"non-runtime block {NonRuntimeBoxBlocks[n]} (0x{RealBlockVa(NonRuntimeBoxBlocks[n]):X}) was modified");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EmergencyBoxPatches_AreDeterministicPerDifficultyBlock(int difficulty)
    {
        uint runtimeVa = RuntimeBoxBlockVa(difficulty);

        var a = NewImageWithRealBoxTopology();
        var b = NewImageWithRealBoxTopology();
        ExePatcher.ShuffleEmergencyBoxContents(a, seed: 99);
        ExePatcher.ShuffleEmergencyBoxContents(b, seed: 99);
        Assert.Equal(Convert.ToHexString(BlockBytes(a, runtimeVa)), Convert.ToHexString(BlockBytes(b, runtimeVa)));

        var c = NewImageWithRealBoxTopology();
        var d = NewImageWithRealBoxTopology();
        ExePatcher.RerollEmergencyBoxContents(c, seed: 99);
        ExePatcher.RerollEmergencyBoxContents(d, seed: 99);
        Assert.Equal(Convert.ToHexString(BlockBytes(c, runtimeVa)), Convert.ToHexString(BlockBytes(d, runtimeVa)));
    }

    // --- Starting inventory (the new-game starting-inventory randomizer lever) ------------------------

    // A synthetic image with every starting-inventory slot store written as a valid
    // `mov byte [eax + 0x9EBC + slot*4], imm8` (C6 80 <disp32> <imm8>) so ValidateStartingInventory passes.
    private static byte[] NewImageWithStartingInventory()
    {
        var exe = NewImage();
        foreach (var block in ExePatcher.StartingInventoryBlocks)
            foreach (var slot in block.Slots)
            {
                uint disp = ExePatcher.InventorySlotIdBaseDisp + (uint)slot.Slot * 4;
                WriteSlotStore(exe, slot.IdImmVa, disp, 0x16);     // seed a valid supply id (9mm)
                WriteSlotStore(exe, slot.QtyImmVa, disp + 1, 0x10);
            }
        return exe;
    }

    private static void WriteSlotStore(byte[] exe, uint immVa, uint disp, byte imm)
    {
        uint instr = immVa - 6;
        ExePatcher.WriteUInt8AtVa(exe, instr, 0xC6);
        ExePatcher.WriteUInt8AtVa(exe, instr + 1, 0x80);
        ExePatcher.WriteUInt32AtVa(exe, instr + 2, disp);
        ExePatcher.WriteUInt8AtVa(exe, immVa, imm);
    }

    [Fact]
    public void RandomizeStartingInventory_IsDeterministic()
    {
        var a = NewImageWithStartingInventory();
        var b = NewImageWithStartingInventory();
        ExePatcher.RandomizeStartingInventory(a, seed: 31337);
        ExePatcher.RandomizeStartingInventory(b, seed: 31337);
        Assert.Equal(a, b);
        var c = NewImageWithStartingInventory();
        ExePatcher.RandomizeStartingInventory(c, seed: 999);
        Assert.NotEqual(a, c); // a different seed gives a different result (not a no-op)
    }

    [Fact]
    public void RandomizeStartingInventory_GrantsWeaponAmmo_AndOnlyValidSupply()
    {
        var exe = NewImageWithStartingInventory();
        ExePatcher.RandomizeStartingInventory(exe, seed: 7);
        foreach (var block in ExePatcher.StartingInventoryBlocks)
        {
            // Slot 0 of every block is 9mm (the Handgun's ammo); the Handgun itself is a flag grant, untouched.
            Assert.Equal(ExePatcher.StartingInvHandgunAmmoId, ExePatcher.ReadUInt8AtVa(exe, block.Slots[0].IdImmVa));
            foreach (var slot in block.Slots)
            {
                byte id = ExePatcher.ReadUInt8AtVa(exe, slot.IdImmVa);
                byte qty = ExePatcher.ReadUInt8AtVa(exe, slot.QtyImmVa);
                Assert.InRange(id, ExePatcher.StartingInvFirstSupplyId, ExePatcher.StartingInvLastItemId);
                Assert.InRange(qty, (byte)1, (byte)255);
            }
        }
    }

    [Fact]
    public void RandomizeStartingInventory_TouchesOnlyThePatchSpan()
    {
        var before = NewImageWithStartingInventory();
        var exe = (byte[])before.Clone();
        ExePatcher.RandomizeStartingInventory(exe, seed: 4242);
        int lo = ExePatcher.VaToFileOffset(ExePatcher.StartingInventoryPatchLoVa);
        int hi = ExePatcher.VaToFileOffset(ExePatcher.StartingInventoryPatchHiVa);
        for (int i = 0; i < exe.Length; i++)
            if (i < lo || i >= hi)
                Assert.Equal(before[i], exe[i]);
    }

    [Fact]
    public void RandomizeStartingInventory_RejectsUnexpectedBuild()
    {
        var exe = NewImageWithStartingInventory();
        ExePatcher.WriteUInt8AtVa(exe, ExePatcher.StartingInventoryBlocks[0].Slots[0].IdImmVa - 6, 0x00); // clobber a store opcode
        Assert.Throws<InvalidOperationException>(() => ExePatcher.RandomizeStartingInventory(exe, seed: 1));
    }

    [Fact]
    public void SetStartingInventory_WritesExactly_AndZeroesUnusedSlots()
    {
        var exe = NewImageWithStartingInventory();
        var items = new List<(int, int)> { (0x05, 1), (0x16, 30), (0x1D, 2) };
        ExePatcher.SetStartingInventory(exe, items);
        foreach (var block in ExePatcher.StartingInventoryBlocks)
            for (int i = 0; i < block.Slots.Length; i++)
            {
                byte id = ExePatcher.ReadUInt8AtVa(exe, block.Slots[i].IdImmVa);
                byte qty = ExePatcher.ReadUInt8AtVa(exe, block.Slots[i].QtyImmVa);
                if (i < items.Count)
                {
                    Assert.Equal((byte)items[i].Item1, id);
                    Assert.Equal((byte)items[i].Item2, qty);
                }
                else
                {
                    Assert.Equal(0, id);   // unused slots emptied
                    Assert.Equal(0, qty);
                }
            }
    }

    [Fact]
    public void SetStartingInventory_RejectsBadInput()
    {
        var exe = NewImageWithStartingInventory();
        Assert.Throws<ArgumentException>(() => ExePatcher.SetStartingInventory(exe, new List<(int, int)>()));
        Assert.Throws<ArgumentException>(() => ExePatcher.SetStartingInventory(exe, new List<(int, int)> { (0x70, 1) }));   // id out of range
        Assert.Throws<ArgumentException>(() => ExePatcher.SetStartingInventory(exe, new List<(int, int)> { (0x16, 0) }));   // count out of range
        var tooMany = Enumerable.Repeat((0x16, 1), ExePatcher.StartingInventoryMaxCustomItems + 1).ToList();
        Assert.Throws<ArgumentException>(() => ExePatcher.SetStartingInventory(exe, tooMany));
    }

    [Fact]
    public void StartingInventory_SitesLieInFileBackedWindow_AndEncodeAsExpected()
    {
        var exe = NewImageWithStartingInventory();
        ExePatcher.ValidateStartingInventory(exe); // must not throw on a well-formed image
        Assert.True(ExePatcher.IsFileBacked(ExePatcher.StartingInventoryPatchLoVa));
        Assert.True(ExePatcher.IsFileBacked(ExePatcher.StartingInventoryPatchHiVa - 1));
        foreach (var block in ExePatcher.StartingInventoryBlocks)
            foreach (var slot in block.Slots)
            {
                Assert.InRange(slot.IdImmVa, ExePatcher.StartingInventoryPatchLoVa, ExePatcher.StartingInventoryPatchHiVa - 1);
                Assert.InRange(slot.QtyImmVa, ExePatcher.StartingInventoryPatchLoVa, ExePatcher.StartingInventoryPatchHiVa - 1);
            }
    }

    // --- Starting weapon (the group-11 weapon-grant lever) --------------------------------------------

    // Seed each weapon-grant site as the `push 1 (val); push <weaponId> (idx)` pair (6A 01 / 6A id) so
    // ValidateStartingWeaponGrants passes. (The trailing `push 0xb; call` aren't read by the validator.)
    private static byte[] NewImageWithStartingWeapons()
    {
        var exe = NewImage();
        foreach (var block in ExePatcher.StartingWeaponGrantBlocks)
            foreach (var site in block.Sites)
            {
                ExePatcher.WriteUInt8AtVa(exe, site.ValImmVa - 1, 0x6A);
                ExePatcher.WriteUInt8AtVa(exe, site.ValImmVa, 1);
                ExePatcher.WriteUInt8AtVa(exe, site.IdxImmVa - 1, 0x6A);
                ExePatcher.WriteUInt8AtVa(exe, site.IdxImmVa, site.VanillaWeaponId);
            }
        return exe;
    }

    [Fact]
    public void SetStartingWeapon_SetsChosenWeapon_AndDisablesExtras()
    {
        var exe = NewImageWithStartingWeapons();
        ExePatcher.SetStartingWeapon(exe, 0x01); // Shotgun everywhere
        foreach (var block in ExePatcher.StartingWeaponGrantBlocks)
            for (int i = 0; i < block.Sites.Length; i++)
            {
                var site = block.Sites[i];
                if (i == 0)
                {
                    Assert.Equal(0x01, ExePatcher.ReadUInt8AtVa(exe, site.IdxImmVa));
                    Assert.Equal(1, ExePatcher.ReadUInt8AtVa(exe, site.ValImmVa));   // granted
                }
                else
                {
                    Assert.Equal(0, ExePatcher.ReadUInt8AtVa(exe, site.ValImmVa));   // disabled (SetFlag val=0)
                }
            }
    }

    [Fact]
    public void SetStartingWeapon_WeaponlessStart_IsRejected()
    {
        // A truly weaponless start ("None") is NOT deliverable: clearing the group-11 owned-flag is not
        // enough — the engine re-equips a default Handgun via an as-yet-undecoded equipped-weapon path
        // (reported in-game: selecting None still starts Regina with the pistol). So null must throw rather
        // than silently produce a still-armed start. A real weapon id stays valid (see SetsChosenWeapon).
        var exe = NewImageWithStartingWeapons();
        Assert.Throws<ArgumentException>(() => ExePatcher.SetStartingWeapon(exe, null));
    }

    [Fact]
    public void SetStartingWeapon_RejectsBadIdAndUnexpectedBuild()
    {
        var exe = NewImageWithStartingWeapons();
        Assert.Throws<ArgumentOutOfRangeException>(() => ExePatcher.SetStartingWeapon(exe, 0x16)); // not a weapon id
        // Clobber a grant's push opcode → validation refuses.
        ExePatcher.WriteUInt8AtVa(exe, ExePatcher.StartingWeaponGrantBlocks[0].Sites[0].ValImmVa - 1, 0x00);
        Assert.Throws<InvalidOperationException>(() => ExePatcher.SetStartingWeapon(exe, 0x05));
    }

    [Fact]
    public void StartingWeaponGrants_LieInThePatchSpan()
    {
        foreach (var block in ExePatcher.StartingWeaponGrantBlocks)
            foreach (var site in block.Sites)
            {
                Assert.InRange(site.ValImmVa, ExePatcher.StartingInventoryPatchLoVa, ExePatcher.StartingInventoryPatchHiVa - 1);
                Assert.InRange(site.IdxImmVa, ExePatcher.StartingInventoryPatchLoVa, ExePatcher.StartingInventoryPatchHiVa - 1);
            }
    }

    // ---- DC1 vertex-table expansion (the 400-vertex ceiling lift) ----

    /// <summary>Synthetic stock image: only the fields the expansion validates/patches, at their
    /// verified offsets (docs/dc1/VERTEX-CEILING-LIFT-PLAN.md).</summary>
    internal static byte[] NewStockImageForVertexTables()
    {
        var exe = new byte[ExePatcher.Dc1StockExeLength];
        exe[0] = (byte)'M'; exe[1] = (byte)'Z';
        ExePatcher.WriteUInt32(exe, 0x3C, 0xF8);              // e_lfanew
        ExePatcher.WriteUInt32(exe, 0xF8, 0x00004550);        // "PE\0\0"
        ExePatcher.WriteUInt16(exe, 0xFE, 7);                 // NumberOfSections
        ExePatcher.WriteUInt32(exe, 0x148, 0x002FC000);       // SizeOfImage
        foreach (var (va, stock) in ExePatcher.Dc1VertexTableOperands)
            ExePatcher.WriteUInt32AtVa(exe, va, stock);
        return exe;
    }

    [Fact]
    public void ExpandDc1CharacterVertexTables_AppendsSectionAndRepointsAll24Operands()
    {
        var patched = ExePatcher.ExpandDc1CharacterVertexTables(NewStockImageForVertexTables());

        Assert.Equal(ExePatcher.Dc1StockExeLength + ExePatcher.Dc1NewTableSectionSize, patched.Length);
        Assert.Equal(8, ExePatcher.ReadUInt16(patched, 0xFE));
        Assert.Equal(0x002FC000u + (uint)ExePatcher.Dc1NewTableSectionSize, ExePatcher.ReadUInt32(patched, 0x148));
        Assert.Equal(".dinovtx", System.Text.Encoding.ASCII.GetString(patched, 0x308, 8));
        Assert.Equal((uint)ExePatcher.Dc1NewTableSectionSize, ExePatcher.ReadUInt32(patched, 0x308 + 0x08)); // VirtualSize
        Assert.Equal(0x002FC000u, ExePatcher.ReadUInt32(patched, 0x308 + 0x0C));                             // VirtualAddress
        Assert.Equal((uint)ExePatcher.Dc1StockExeLength, ExePatcher.ReadUInt32(patched, 0x308 + 0x14));      // PointerToRawData
        Assert.Equal(0xC0000040u, ExePatcher.ReadUInt32(patched, 0x308 + 0x24));                             // RW data

        int color = 0, otz = 0, xy = 0;
        foreach (var (va, stock) in ExePatcher.Dc1VertexTableOperands)
        {
            uint expected = stock switch
            {
                ExePatcher.Dc1ColorTableVa => ExePatcher.Dc1NewColorTableVa,
                ExePatcher.Dc1OtzTableVa => ExePatcher.Dc1NewOtzTableVa,
                _ => ExePatcher.Dc1NewXyTableVa,
            };
            Assert.Equal(expected, ExePatcher.ReadUInt32AtVa((ReadOnlySpan<byte>)patched, va));
            if (stock == ExePatcher.Dc1ColorTableVa) color++;
            else if (stock == ExePatcher.Dc1OtzTableVa) otz++;
            else xy++;
        }
        // census closure: 8 color + 8 otz + 8 xy = 24 references, none missed, none extra
        Assert.Equal((8, 8, 8), (color, otz, xy));

        // appended tail is zero-filled (fresh tables)
        Assert.All(patched[ExePatcher.Dc1StockExeLength..], b => Assert.Equal(0, b));
        Assert.True(ExePatcher.IsDc1CharacterVertexTablesExpanded(patched));
        Assert.False(ExePatcher.IsDc1CharacterVertexTablesExpanded(NewStockImageForVertexTables()));
    }

    [Fact]
    public void ExpandDc1CharacterVertexTables_NewLayoutPreservesOrderAndAdjacency()
    {
        // COLOR < OTZ < XY at exactly 4*capacity apart, like the stock 0x640-apart layout.
        uint stride = 4u * ExePatcher.Dc1VertexTableExpandedCapacity;
        Assert.Equal(ExePatcher.Dc1NewColorTableVa + stride, ExePatcher.Dc1NewOtzTableVa);
        Assert.Equal(ExePatcher.Dc1NewOtzTableVa + stride, ExePatcher.Dc1NewXyTableVa);
        // and the whole span fits the appended section
        Assert.Equal(3 * (int)stride, ExePatcher.Dc1NewTableSectionSize);
    }

    [Fact]
    public void ExpandDc1CharacterVertexTables_RefusesForeignOrAlreadyPatchedImages()
    {
        // wrong length
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ExpandDc1CharacterVertexTables(new byte[123]));

        // one operand not holding its stock value (already patched / unknown build)
        var tampered = NewStockImageForVertexTables();
        ExePatcher.WriteUInt32AtVa(tampered, ExePatcher.Dc1VertexTableOperands[5].OperandVa, 0xDEADBEEF);
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ExpandDc1CharacterVertexTables(tampered));

        // occupied 8th section-header slot
        var occupied = NewStockImageForVertexTables();
        occupied[0x308] = (byte)'X';
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ExpandDc1CharacterVertexTables(occupied));

        // double-expansion refused (section count no longer 7 — and length differs anyway)
        var once = ExePatcher.ExpandDc1CharacterVertexTables(NewStockImageForVertexTables());
        Assert.Throws<InvalidOperationException>(() => ExePatcher.ExpandDc1CharacterVertexTables(once));
    }
}
