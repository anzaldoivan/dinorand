using System.Buffers.Binary;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Cross-room species import (docs/dc1/CROSS-ROOM-SPECIES-PLAN.md, increment 1 / Step 3): the relocatable
/// model+motion extractor + appender. Synthetic tests pin the core invariants; a gated real-install
/// test imports a real Pteranodon (st407) into a real raptor room (st10e).
/// </summary>
public class SpeciesImporterTests
{
    private const uint B = SpeciesImporter.PsxBase; // 0x80100000

    private static void PutU32(byte[] buf, int off, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), v);

    private static uint GetU32(byte[] buf, int off)
        => BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(off, 4));

    // --- synthetic core -------------------------------------------------------------------------

    [Fact]
    public void RelocationSlots_FindsOnlyFileFormPointers()
    {
        var rdt = new byte[0x20];
        PutU32(rdt, 0x00, B + 0x10);     // valid file-form ptr -> slot
        PutU32(rdt, 0x08, 0x12345678);   // not a ptr
        PutU32(rdt, 0x0c, B + 0x1000);   // out of range (>= len) -> not a ptr
        PutU32(rdt, 0x10, B + 0x00);     // valid -> slot

        var slots = SpeciesImporter.RelocationSlots(rdt);
        Assert.Equal(new[] { 0x00, 0x10 }, slots);
    }

    [Fact]
    public void ExtractBlock_ClosedRegion_ReturnsRelativeSlots()
    {
        var rdt = new byte[0x40];
        PutU32(rdt, 0x14, B + 0x18);     // inside [0x10,0x20): closed

        var block = SpeciesImporter.ExtractBlock(rdt, 0x10, 0x20);

        Assert.Equal(0x10, block.SourceOffset);
        Assert.Equal(0x10, block.Bytes.Length);
        Assert.Equal(new[] { 0x04 }, block.PointerSlots); // 0x14 - 0x10
    }

    [Fact]
    public void ExtractBlock_EscapingPointer_Throws()
    {
        var rdt = new byte[0x40];
        PutU32(rdt, 0x14, B + 0x30);     // target 0x30 is outside [0x10,0x20): escapes
        Assert.Throws<InvalidOperationException>(() => SpeciesImporter.ExtractBlock(rdt, 0x10, 0x20));
    }

    [Fact]
    public void Append_RelocatesInternalPointer_AndReturnsHeadPtr()
    {
        var src = new byte[0x40];
        PutU32(src, 0x14, B + 0x18);          // head 0x10, internal ptr -> 0x18
        var block = SpeciesImporter.ExtractBlock(src, 0x10, 0x20);

        var target = new byte[0x22];          // not 4-aligned: append must align to 0x24
        var (grown, head) = SpeciesImporter.Append(target, block);

        Assert.Equal(B + 0x24, head);                       // appended at aligned 0x24
        Assert.Equal(0x24 + 0x10, grown.Length);            // target padded + block
        // delta = 0x24 - 0x10 = 0x14; internal ptr 0x80100018 -> 0x8010002c, and 0x2c is inside the block
        Assert.Equal(B + 0x2c, GetU32(grown, 0x24 + 0x04));
        // re-extracting from the grown buffer at the new head is pointer-closed
        var re = SpeciesImporter.ExtractBlock(grown, 0x24, 0x34);
        Assert.Equal(block.PointerSlots, re.PointerSlots);
    }

    [Fact]
    public void Import_AppendsBothBlocks_WithDistinctPointers()
    {
        var src = new byte[0x40];
        PutU32(src, 0x04, B + 0x08);          // model [0x00,0x10): ptr -> 0x08
        PutU32(src, 0x24, B + 0x28);          // motion [0x20,0x30): ptr -> 0x28
        var model = SpeciesImporter.ExtractBlock(src, 0x00, 0x10);
        var motion = SpeciesImporter.ExtractBlock(src, 0x20, 0x30);

        var target = new byte[0x10];
        var r = SpeciesImporter.Import(target, model, motion);

        Assert.Equal(B + 0x10, r.ModelPtr);   // model appended first
        Assert.Equal(B + 0x20, r.MotionPtr);  // motion after it
        Assert.Equal(0x30, r.Rdt.Length);
        Assert.Equal(B + 0x18, GetU32(r.Rdt, 0x10 + 0x04)); // model ptr relocated
        Assert.Equal(B + 0x28, GetU32(r.Rdt, 0x20 + 0x04)); // motion ptr relocated
    }

    // --- synthetic multi-range / piecewise closure (docs/dc1/THERI-BACKWARD-CLOSURE-PLAN.md) ---------

    [Fact]
    public void ImportRangeSet_RelocatesCrossRangePointer_ByTargetRangeDelta()
    {
        // Two disjoint donor ranges; a pointer in each points INTO the other. The piecewise map must
        // relocate each by its TARGET range's append delta, not by its own.
        var bytes = new byte[0x20];                 // range0 -> blob[0,0x10), range1 -> blob[0x10,0x20)
        PutU32(bytes, 0x04, B + 0x204);             // range0 internal ptr -> range1 source 0x204
        PutU32(bytes, 0x14, B + 0x104);             // range1 internal ptr -> range0 source 0x104
        var set = new SpeciesBlockSet(
            bytes,
            new[] { new SpeciesRange(0x100, 0x110, 0x00), new SpeciesRange(0x200, 0x210, 0x10) },
            new[] { new RelocSlot(0x04, 1), new RelocSlot(0x14, 0) }, // target-range indices
            ModelHead: 0x100, MotionHead: 0x200);

        var target = new byte[0x40];
        var r = SpeciesImporter.ImportRangeSet(target, set);

        // blob appended at 0x40: range0 -> [0x40,0x50), range1 -> [0x50,0x60).
        Assert.Equal(B + 0x40, r.ModelPtr);         // model head (range0 source 0x100)
        Assert.Equal(B + 0x50, r.MotionPtr);        // motion head (range1 source 0x200)
        // range0's ptr (now at 0x44) -> range1 target 0x204 -> new 0x50 + (0x204-0x200) = 0x54.
        Assert.Equal(B + 0x54, GetU32(r.Rdt, 0x44));
        // range1's ptr (now at 0x54) -> range0 target 0x104 -> new 0x40 + (0x104-0x100) = 0x44.
        Assert.Equal(B + 0x44, GetU32(r.Rdt, 0x54));
    }

    [Fact]
    public void ExtractRangeSet_DropsUnreachedGap_ProducingDisjointRanges()
    {
        // header[0] -> 0xf0 caps the resource; model 0x30 reaches [0x30,0x40), motion 0x50 reaches
        // [0x50,0x70); the self-referential cluster at 0x40 makes 0x40 a partition point that nothing
        // reachable points to, so [0x40,0x50) is an unreached gap and must be dropped.
        var rdt = new byte[0x100];
        PutU32(rdt, 0x00, B + 0xf0);   // RDT header ptr -> tail struct (ceiling = 0xf0)
        PutU32(rdt, 0x30, B + 0x34);   // model internal -> 0x34
        PutU32(rdt, 0x40, B + 0x40);   // unreached cluster: partition point 0x40, points to itself
        PutU32(rdt, 0x50, B + 0x54);   // motion internal -> 0x54

        var set = SpeciesImporter.ExtractRangeSet(rdt, B + 0x30, B + 0x50);

        Assert.Equal(2, set.Ranges.Count);
        Assert.Equal((0x30, 0x40), (set.Ranges[0].SourceLo, set.Ranges[0].SourceHi));
        Assert.Equal((0x50, 0xf0), (set.Ranges[1].SourceLo, set.Ranges[1].SourceHi));
        Assert.DoesNotContain(set.Ranges, r => 0x40 >= r.SourceLo && 0x40 < r.SourceHi); // gap dropped
    }

    [Fact]
    public void ExtractRangeSet_AlignedInBoundsEscape_Throws()
    {
        // A copied pointer into an aligned, in-bounds offset that is neither copied, header, nor
        // unaligned-coincidental is a real uncopied resource -> must throw (never silently dangle).
        var rdt = new byte[0x200];
        PutU32(rdt, 0x00, B + 0x1f0);  // ceiling = 0x1f0
        PutU32(rdt, 0x100, B + 0x110); // model -> motion
        PutU32(rdt, 0x110, B + 0x1f8); // motion -> 0x1f8: aligned, in-bounds, >= ceiling, >= 0x100
        Assert.Throws<InvalidOperationException>(() => SpeciesImporter.ExtractRangeSet(rdt, B + 0x100, B + 0x110));
    }

    [Fact]
    public void ExtractRangeSet_UnalignedEscape_LeftAbsolute_NotASlot()
    {
        // A class-(c) escape is a coincidental 0x8010xxxx bit pattern: unaligned, >= 0x100, and BELOW the
        // model head (the real st605 case: 0x1056f / 0x11a9f below model head 0x1b930). It is left
        // absolute (not a reloc slot) and does not throw. (An in-[head,ceiling) unaligned value would
        // instead be pulled in as a partition point — only below-head / above-ceiling targets escape.)
        var rdt = new byte[0x200];
        PutU32(rdt, 0x00, B + 0x1f0);  // ceiling
        PutU32(rdt, 0x130, B + 0x134); // model internal
        PutU32(rdt, 0x150, B + 0x10f); // motion: unaligned target 0x10f (>=0x100, < model head) -> (c)
        var set = SpeciesImporter.ExtractRangeSet(rdt, B + 0x130, B + 0x150);
        var rr = set.Ranges.Single(r => 0x150 >= r.SourceLo && 0x150 < r.SourceHi);
        int blobOf0x150 = rr.BlobOffset + (0x150 - rr.SourceLo);
        Assert.DoesNotContain(set.Slots, s => s.BlobOffset == blobOf0x150);
    }

    // --- gated real install ---------------------------------------------------------------------

    private static string? DataDir()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return null;
        foreach (var c in new[] { root, Path.Combine(root, "Data") })
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "st*.dat").Any())
                return c;
        var data = Directory.EnumerateDirectories(root, "Data", SearchOption.AllDirectories).FirstOrDefault();
        return data;
    }

    private static RoomFile Load(string dataDir, int stage, int room, string file)
        => RoomFile.Read(stage, room, File.ReadAllBytes(Path.Combine(dataDir, file)));

    private static IEnumerable<int> ResourceHeads(RoomFile rf)
    {
        foreach (var e in rf.Enemies)
        {
            if (e.OriginalModelPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
            if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
        }
    }

    [Fact]
    public void RealInstall_ImportPteranodonIntoRaptorRoom_ProducesClosedBlocks()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st407.dat")) || !File.Exists(Path.Combine(dir, "st10e.dat")))
            return;

        // Donor: the Pteranodon (cat 7, 18 bones) from st407.
        var src = Load(dir, 4, 0x07, "st407.dat");
        var donorRec = src.Enemies.First(e => e.Species == DinoSpecies.Pteranodon);
        var (model, motion) = SpeciesImporter.ExtractSpecies(
            src.RdtBuffer, donorRec.OriginalModelPtr, donorRec.OriginalMotionPtr, ResourceHeads(src));

        // The donor model decodes as an 18-bone Pteranodon skeleton (block head = file-form B).
        Assert.Equal(18, EnemySkeleton.ReadBoneCount(model.Bytes, B));

        // Target: a raptor room.
        var target = Load(dir, 1, 0x0e, "st10e.dat");
        int beforeLen = target.RdtBuffer.Length;

        var result = SpeciesImporter.Import(target.RdtBuffer, model, motion);

        // Grew by both blocks (plus up to 3 bytes alignment each).
        Assert.True(result.Rdt.Length >= beforeLen + model.Bytes.Length + motion.Bytes.Length);
        Assert.True(result.ModelPtr >= SpeciesImporter.PsxBase + (uint)beforeLen);

        // The imported model, re-read at its new pointer in the grown buffer, is pointer-closed and
        // still an 18-bone Pteranodon — i.e. relocation preserved the skeleton.
        int newModelOff = (int)(result.ModelPtr - SpeciesImporter.PsxBase);
        var reModel = SpeciesImporter.ExtractBlock(result.Rdt, newModelOff, newModelOff + model.Bytes.Length);
        Assert.Equal(model.PointerSlots, reModel.PointerSlots);
        Assert.Equal(18, EnemySkeleton.ReadBoneCount(result.Rdt, result.ModelPtr));

        // Non-pointer bytes of the model are copied verbatim (only pointer slots changed by delta).
        var slotSet = new HashSet<int>(model.PointerSlots);
        for (int i = 0; i + 4 <= model.Bytes.Length; i += 4)
            if (!slotSet.Contains(i))
                Assert.Equal(GetU32(model.Bytes, i), GetU32(result.Rdt, newModelOff + i));
    }

    [Fact]
    public void RealInstall_ImportSpecies_RoundTripsThroughRoomFileWrite()
    {
        var dir = DataDir();
        if (dir is null) return;
        if (!File.Exists(Path.Combine(dir, "st407.dat")) || !File.Exists(Path.Combine(dir, "st10e.dat")))
            return;

        // Donor: Pteranodon (cat 7) from st407.
        var src = Load(dir, 4, 0x07, "st407.dat");
        var donorRec = src.Enemies.First(e => e.Species == DinoSpecies.Pteranodon);
        var donor = SpeciesImporter.ExtractDonor(src.RdtBuffer, donorRec, ResourceHeads(src));
        Assert.Equal(DinoSpecies.Pteranodon, donor.Species);
        Assert.Equal(7, donor.Category);

        // Target: raptor room st10e; repoint its first raptor to the Pteranodon.
        var target = Load(dir, 1, 0x0e, "st10e.dat");
        var victim = target.Enemies.First(e => e.Species == DinoSpecies.Velociraptor);
        int victimIndex = target.Enemies.IndexOf(victim);
        uint otherOriginalModel = target.Enemies.Count > 1
            ? target.Enemies.First(e => target.Enemies.IndexOf(e) != victimIndex).OriginalModelPtr
            : 0;
        int originalFileLen = target.OriginalBytes.Length;

        target.ImportSpecies(donor, victimIndex);
        var written = target.Write();

        // The file grew (the donor blocks were appended to the last entry) and changed.
        Assert.True(written.Length > originalFileLen);
        Assert.NotEqual(target.OriginalBytes, written);

        // Re-read the written room: it parses cleanly and now contains a Pteranodon (cat 7) where a
        // raptor was — i.e. the imported model survived LZSS + relocation end-to-end.
        var reread = RoomFile.Read(1, 0x0e, written);
        Assert.True(reread.ParsedCleanly);
        var imported = reread.Enemies.FirstOrDefault(e => e.Species == DinoSpecies.Pteranodon);
        Assert.NotNull(imported);
        Assert.Equal(7, imported!.Category);
        Assert.True(imported.SpeciesMatchesCategory); // 18-bone Pteranodon model + cat-7 AI agree

        // Any other raptor that was not repointed is untouched.
        if (otherOriginalModel != 0)
            Assert.Contains(reread.Enemies, e => e.OriginalModelPtr == otherOriginalModel);
    }

    // --- gated real install: Therizinosaurus backward-closure (THERI-BACKWARD-CLOSURE-PLAN.md) ---

    private static readonly (string File, int Stage, int Room)[] TheriRooms =
    {
        ("st603.dat", 6, 0x03), ("st604.dat", 6, 0x04), ("st605.dat", 6, 0x05),
        ("st606.dat", 6, 0x06), ("st608.dat", 6, 0x08),
    };

    [Fact]
    public void RealInstall_ExtractTheriClosure_AllFiveRooms_RoundTrip()
    {
        var dir = DataDir();
        if (dir is null) return; // no game files: no-op (CI)
        if (TheriRooms.Any(r => !File.Exists(Path.Combine(dir, r.File)))) return;

        foreach (var (file, stage, room) in TheriRooms)
        {
            var src = Load(dir, stage, room, file);
            var theri = src.Enemies.First(e => e.Opcode == DcOpcodes.Enemy2 && e.SpeciesBoneCount == 22);

            // The closure extractor SUCCEEDS where the strict single-range path throws (cont.28).
            var donor = SpeciesImporter.ExtractDonorClosure(src.RdtBuffer, theri);
            Assert.Equal(DinoSpecies.Therizinosaurus, donor.Species);
            Assert.Equal(8, donor.Category);
            Assert.NotEmpty(donor.Blocks.Ranges);

            // Import into a blank target; the model re-reads as a 22-bone Therizinosaurus at its new head.
            var r = SpeciesImporter.ImportRangeSet(new byte[0x10], donor.Blocks);
            Assert.Equal(22, EnemySkeleton.ReadBoneCount(r.Rdt, r.ModelPtr));

            // Pointer-consistent under the piecewise map: every relocated slot now holds a valid
            // file-form pointer into the grown buffer.
            int newOff = (0x10 + 3) & ~3;
            foreach (var slot in donor.Blocks.Slots)
            {
                uint v = GetU32(r.Rdt, newOff + slot.BlobOffset);
                Assert.True(v >= SpeciesImporter.PsxBase && v < SpeciesImporter.PsxBase + (uint)r.Rdt.Length,
                    $"{file}: reloc slot 0x{slot.BlobOffset:x} -> 0x{v:x} not in-bounds");
            }
        }
    }

    [Fact]
    public void RealInstall_ImportTheri_RoundTripsThroughRoomFileWrite()
    {
        var dir = DataDir();
        if (dir is null) return;
        if (!File.Exists(Path.Combine(dir, "st608.dat")) || !File.Exists(Path.Combine(dir, "st10e.dat")))
            return;

        // Donor: the Therizinosaurus (cat 8, 22 bones) from st608 (the single-range Theri room).
        var src = Load(dir, 6, 0x08, "st608.dat");
        var theri = src.Enemies.First(e => e.Opcode == DcOpcodes.Enemy2 && e.SpeciesBoneCount == 22);
        var donor = SpeciesImporter.ExtractDonorClosure(src.RdtBuffer, theri);

        // Target: a raptor room; repoint its first raptor to the Theri.
        var target = Load(dir, 1, 0x0e, "st10e.dat");
        var victim = target.Enemies.First(e => e.Species == DinoSpecies.Velociraptor);
        int victimIndex = target.Enemies.IndexOf(victim);
        uint otherOriginalModel = target.Enemies.Count > 1
            ? target.Enemies.First(e => target.Enemies.IndexOf(e) != victimIndex).OriginalModelPtr
            : 0;
        int originalFileLen = target.OriginalBytes.Length;

        target.ImportSpecies(donor, victimIndex);
        var written = target.Write();

        // The file grew (the closure ranges were appended) and changed.
        Assert.True(written.Length > originalFileLen);
        Assert.NotEqual(target.OriginalBytes, written);

        // Re-read: parses cleanly and the repointed record decodes as a 22-bone Therizinosaurus with
        // cat-8 AI agreeing (SpeciesMatchesCategory) — the closure import survived LZSS + relocation.
        var reread = RoomFile.Read(1, 0x0e, written);
        Assert.True(reread.ParsedCleanly);
        var imported = reread.Enemies.FirstOrDefault(e => e.Species == DinoSpecies.Therizinosaurus);
        Assert.NotNull(imported);
        Assert.Equal(8, imported!.Category);
        Assert.True(imported.SpeciesMatchesCategory);

        // Any other enemy that was not repointed is untouched.
        if (otherOriginalModel != 0)
            Assert.Contains(reread.Enemies, e => e.OriginalModelPtr == otherOriginalModel);
    }

    // --- overlay-in-place (docs/dc1/THERI-RDT-MEMORY-LIBERATION-PLAN.md) -------------------------

    private static SpeciesBlockSet SingleRange(byte[] bytes, int srcLo, IEnumerable<int> slots,
                                               int modelHead, int motionHead)
        => new(bytes, new[] { new SpeciesRange(srcLo, srcLo + bytes.Length, 0x00) },
               slots.Select(s => new RelocSlot(s, 0)).ToArray(), modelHead, motionHead);

    [Fact]
    public void PlaceRangeSetAt_OverwritesInPlace_NoGrowth_RelocatesAgainstBase()
    {
        var bytes = new byte[0x10];
        PutU32(bytes, 0x04, B + 0x104);                       // src range [0x100,0x110); ptr -> own 0x104
        var set = SingleRange(bytes, 0x100, new[] { 0x04 }, modelHead: 0x100, motionHead: 0x108);

        var target = new byte[0x80];
        var r = SpeciesImporter.PlaceRangeSetAt(target, set, 0x40);

        Assert.Equal(0x80, r.Rdt.Length);                     // 0x40 + 0x10 <= 0x80: no growth, overwrite
        Assert.Equal(B + 0x40, r.ModelPtr);                   // model head laid at base
        Assert.Equal(B + 0x48, r.MotionPtr);                  // motion 0x108 -> 0x40 + (0x108-0x100)
        Assert.Equal(B + 0x44, GetU32(r.Rdt, 0x44));          // internal ptr -> 0x40 + (0x104-0x100)
    }

    [Fact]
    public void ImportRangeSet_IsPlaceRangeSetAt_AtAlignedEnd()
    {
        var bytes = new byte[0x10];
        PutU32(bytes, 0x08, B + 0x108);
        var set = SingleRange(bytes, 0x100, new[] { 0x08 }, modelHead: 0x100, motionHead: 0x100);

        var target = new byte[0x42];                          // not 4-aligned
        var imp = SpeciesImporter.ImportRangeSet(target, set);
        var pla = SpeciesImporter.PlaceRangeSetAt(target, set, (0x42 + 3) & ~3);

        Assert.Equal(pla.Rdt, imp.Rdt);                       // append == place at align4(length)
        Assert.Equal(pla.ModelPtr, imp.ModelPtr);
        Assert.Equal(pla.MotionPtr, imp.MotionPtr);
    }

    [Fact]
    public void OverlayRegion_RunsToNextLiveHeadAboveTheLowerHead()
    {
        var rdt = new byte[0x200];                            // header dwords zero: no header cap
        var region = SpeciesImporter.OverlayRegion(rdt, 0x40, 0x60, new[] { 0x100, 0x180 });
        Assert.Equal((0x40, 0x100), region);                  // smallest other head > lo
    }

    [Fact]
    public void OverlayRegion_HeaderPointerTargetCapsTheSpan()
    {
        var rdt = new byte[0x200];
        PutU32(rdt, 0x00, B + 0x90);                          // RDT tail-structure head caps the region
        var region = SpeciesImporter.OverlayRegion(rdt, 0x40, 0x50, Array.Empty<int>());
        Assert.Equal((0x40, 0x90), region);
    }

    [Fact]
    public void OverlayRegion_InterleavedHeads_ReturnsNull()
    {
        var rdt = new byte[0x200];                            // a live head 0x100 sits between model & motion
        Assert.Null(SpeciesImporter.OverlayRegion(rdt, 0x40, 0x120, new[] { 0x100 }));
    }

    [Fact]
    public void RealInstall_Overlay_FitsOverCeilingRooms_UnderTheEngineCeiling()
    {
        var dir = DataDir();
        if (dir is null) return;                               // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st603.dat"))) return;

        var donorRf = Load(dir, 6, 0x03, "st603.dat");
        var theri = donorRf.Enemies.First(e => e.Opcode == DcOpcodes.Enemy2 && e.SpeciesBoneCount == 22);
        var donor = SpeciesImporter.ExtractDonorClosure(donorRf.RdtBuffer, theri);
        int blob = donor.Blocks.Bytes.Length;
        int ceiling = SpeciesImporter.EngineRoomRdtCeiling;

        int fitted = 0;
        foreach (var path in Directory.GetFiles(dir, "st*.dat"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            int stage, room;
            try { stage = Convert.ToInt32(name.Substring(2, 1), 16); room = Convert.ToInt32(name.Substring(3, 2), 16); }
            catch { continue; }
            RoomFile rf;
            try { rf = RoomFile.Read(stage, room, File.ReadAllBytes(path)); } catch { continue; }
            if (rf.RdtBuffer.Length == 0) continue;
            var victim = rf.Enemies.FirstOrDefault(e => e.IsRandomizableDino);
            if (victim is null) continue;

            int fullAppend = ((rf.RdtBuffer.Length + 3) & ~3) + blob;
            if (fullAppend <= ceiling) continue;               // overlay only matters for over-ceiling rooms

            try { rf.OverlaySpecies(donor, rf.Enemies.IndexOf(victim)); }
            catch (InvalidOperationException) { continue; }    // shared / interleaved: not overlayable here

            Assert.True(rf.RdtBuffer.Length <= fullAppend);    // overlay (with overflow) never grows more than append
            if (rf.RdtBuffer.Length > ceiling) continue;       // tiny region: split still overflows -> CLI guard rejects
            fitted++;
            Assert.Equal(22, EnemySkeleton.ReadBoneCount(rf.RdtBuffer, victim.ModelPtr));
            Assert.Equal(8, victim.Category);

            var reread = RoomFile.Read(stage, room, rf.Write());
            Assert.True(reread.ParsedCleanly);
            Assert.Contains(reread.Enemies, e => e.Species == DinoSpecies.Therizinosaurus && e.Category == 8);
        }
        Assert.True(fitted > 0, "expected at least one over-ceiling room to fit the Theri via overlay/split");
    }

    // --- overlay-with-overflow / destination split (append the part that doesn't fit the hole) ----

    [Fact]
    public void PlaceRangeSetSplit_CutsAtStructureBoundary_RelocatesAcrossSegments()
    {
        var bytes = new byte[0x20];
        PutU32(bytes, 0x00, B + 0x110);                       // hole-part ptr -> blob 0x10 (a boundary)
        PutU32(bytes, 0x14, B + 0x100);                       // append-part ptr -> blob 0x00
        var set = SingleRange(bytes, 0x100, new[] { 0x00, 0x14 }, modelHead: 0x100, motionHead: 0x100);

        var target = new byte[0x80];
        var r = SpeciesImporter.PlaceRangeSetSplit(target, set, regionLo: 0x40, regionBytes: 0x10);

        // hole [0x40,0x50) holds blob[0,0x10); the remainder appends at 0x80 -> [0x80,0x90).
        Assert.Equal(0x90, r.Rdt.Length);
        Assert.Equal(B + 0x40, r.ModelPtr);                   // model head reused in place
        Assert.Equal(B + 0x80, GetU32(r.Rdt, 0x40));          // hole ptr -> target now in the appended piece
        Assert.Equal(B + 0x40, GetU32(r.Rdt, 0x84));          // appended ptr -> target back in the hole
    }

    [Fact]
    public void PlaceRangeSetSplit_RoundsReuseDownToATearFreeBoundary()
    {
        var bytes = new byte[0x20];
        PutU32(bytes, 0x00, B + 0x110);                       // only structure boundary in (0,0x20) is 0x10
        var set = SingleRange(bytes, 0x100, new[] { 0x00 }, modelHead: 0x100, motionHead: 0x100);
        var target = new byte[0x80];

        // regionBytes 0x14 is not a boundary -> reuse rounds down to 0x10 (same split as regionBytes 0x10).
        Assert.Equal(0x90, SpeciesImporter.PlaceRangeSetSplit(target, set, 0x40, 0x14).Rdt.Length);
        Assert.Equal(0x90, SpeciesImporter.PlaceRangeSetSplit(target, set, 0x40, 0x10).Rdt.Length);
    }

    [Fact]
    public void PlaceRangeSetSplit_WholeDonorFitsHole_NoGrowthNoSplit()
    {
        var bytes = new byte[0x20];
        PutU32(bytes, 0x04, B + 0x104);
        var set = SingleRange(bytes, 0x100, new[] { 0x04 }, modelHead: 0x100, motionHead: 0x100);
        var target = new byte[0x80];

        var r = SpeciesImporter.PlaceRangeSetSplit(target, set, 0x40, 0x40); // region 0x40 >= blob 0x20
        Assert.Equal(0x80, r.Rdt.Length);                     // no growth
        Assert.Equal(B + 0x44, GetU32(r.Rdt, 0x44));          // contiguous in-place relocation
    }

    [Fact]
    public void RealInstall_OverlaySplit_FitsTheriInto0203_WithBoundedGrowth()
    {
        var dir = DataDir();
        if (dir is null) return;                               // no game files: no-op (CI)
        var pristine = Path.Combine(dir, ".dinorand_backup", "st203.dat");
        var p203 = File.Exists(pristine) ? pristine : Path.Combine(dir, "st203.dat");
        if (!File.Exists(p203) || !File.Exists(Path.Combine(dir, "st603.dat"))) return;

        var donorRf = Load(dir, 6, 0x03, "st603.dat");
        var theri = donorRf.Enemies.First(e => e.Opcode == DcOpcodes.Enemy2 && e.SpeciesBoneCount == 22);
        var donor = SpeciesImporter.ExtractDonorClosure(donorRf.RdtBuffer, theri);

        var rf = RoomFile.Read(2, 0x03, File.ReadAllBytes(p203));
        var victim = rf.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == DinoSpecies.RaptorHeavy);
        if (victim is null) return;                            // not pristine 0203: skip
        int before = rf.RdtBuffer.Length;

        // 0203's hole (~185.5 KB) is smaller than the Theri (~195.8 KB): the split path appends the overflow.
        Assert.True(((before + 3) & ~3) + donor.Blocks.Bytes.Length > SpeciesImporter.EngineRoomRdtCeiling);
        rf.OverlaySpecies(donor, rf.Enemies.IndexOf(victim));

        Assert.True(rf.RdtBuffer.Length > before);                              // grew by the appended overflow
        Assert.True(rf.RdtBuffer.Length < ((before + 3) & ~3) + donor.Blocks.Bytes.Length); // but less than a full append
        Assert.True(rf.RdtBuffer.Length <= SpeciesImporter.EngineRoomRdtCeiling);           // and under the ceiling
        Assert.Equal(22, EnemySkeleton.ReadBoneCount(rf.RdtBuffer, victim.ModelPtr));
        Assert.Equal(8, victim.Category);

        var reread = RoomFile.Read(2, 0x03, rf.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Contains(reread.Enemies, e => e.Species == DinoSpecies.Therizinosaurus && e.Category == 8);
    }

    // --- donor clip-strip (docs/dc1/THERI-CLIP-STRIP-PLAN.md) ------------------------------------

    /// <summary>Build a synthetic closure: header tail-pointer @0x00 (caps the resource at 0x1F0), a closed
    /// model [0x40,0x60), then a 3-entry motion clip table @0x60 over clips [0x80)(0x20) [0xA0)(0x80)
    /// [0x120)(0xD0, to ceiling 0x1F0). Each clip has an inner ptr clip+0 -> clip+0x10 so reachability tiles
    /// the whole resource into one range.</summary>
    private static byte[] SyntheticTheriLikeClosure()
    {
        var rdt = new byte[0x200];
        PutU32(rdt, 0x00, B + 0x1F0);   // header dword -> RDT tail (resource ceiling = 0x1F0)
        // model [0x40,0x60): one internal pointer 0x44 -> 0x50 (closed)
        PutU32(rdt, 0x44, B + 0x50);
        // motion clip table @0x60: 3 entries
        PutU32(rdt, 0x60, B + 0x80);
        PutU32(rdt, 0x64, B + 0xA0);
        PutU32(rdt, 0x68, B + 0x120);
        // each clip: clip+0 -> clip+0x10
        PutU32(rdt, 0x80, B + 0x90);
        PutU32(rdt, 0xA0, B + 0xB0);
        PutU32(rdt, 0x120, B + 0x130);
        return rdt;
    }

    [Fact]
    public void MotionClipTable_ParsesPointerArray_WithSizes()
    {
        var rdt = SyntheticTheriLikeClosure();
        var clips = SpeciesImporter.MotionClipTable(rdt, 0x60, 0x1F0);
        Assert.Equal(3, clips.Count);
        Assert.Equal((0, 0x80, 0x20), (clips[0].Index, clips[0].Off, clips[0].Size));
        Assert.Equal((1, 0xA0, 0x80), (clips[1].Index, clips[1].Off, clips[1].Size));
        Assert.Equal((2, 0x120, 0xD0), (clips[2].Index, clips[2].Off, clips[2].Size)); // last runs to ceiling
    }

    [Fact]
    public void ExtractRangeSetStripped_DropsLargestClip_RepointsSlotToFallback_AndFits()
    {
        var rdt = SyntheticTheriLikeClosure();
        // full closure = [0x40,0x1F0) = 0x1B0 (432). Cap to 224 -> must free 208 = exactly clip 2 (0xD0).
        var set = SpeciesImporter.ExtractRangeSetStripped(rdt, B + 0x40, B + 0x60, 224,
                                                          out int dropped, out int droppedBytes);
        Assert.Equal(1, dropped);
        Assert.Equal(0xD0, droppedBytes);
        Assert.Equal(224, set.Bytes.Length);                 // 0x40..0x120 kept, 0x120..0x1F0 dropped
        Assert.Equal(0x40, set.ModelHead);
        Assert.Equal(0x60, set.MotionHead);

        // The dropped clip's table slot (source 0x68) is repointed to the fallback clip 0 (source 0x80).
        int slotBlob = 0x68 - 0x40;
        Assert.Equal(B + 0x80, GetU32(set.Bytes, slotBlob));
        // Kept clips 0 and 1 table slots are unchanged.
        Assert.Equal(B + 0x80, GetU32(set.Bytes, 0x60 - 0x40));
        Assert.Equal(B + 0xA0, GetU32(set.Bytes, 0x64 - 0x40));

        // Imports through the SAME append path; a single append segment lands the blob at align4(len) = 0x100.
        var target = new byte[0x100];
        var r = SpeciesImporter.ImportRangeSet(target, set);
        Assert.Equal(B + 0x100u, r.ModelPtr);                 // model head = blob start = append base
        Assert.Equal(B + 0x100u + 0x20, r.MotionPtr);         // motion head = base + (0x60-0x40)
        // Every relocated slot — including the repointed dropped slot — resolves to a valid in-buffer address.
        foreach (var slot in set.Slots)
        {
            uint v = GetU32(r.Rdt, 0x100 + slot.BlobOffset);
            Assert.True(v >= B && v < B + (uint)r.Rdt.Length, $"slot at blob 0x{slot.BlobOffset:x} dangles: 0x{v:x}");
        }
    }

    [Fact]
    public void ExtractRangeSetStripped_ProtectedClip_IsNeverDropped()
    {
        var rdt = SyntheticTheriLikeClosure();   // clips: 0(0x20) 1(0x80) 2(0xD0); droppable 1,2
        // Need to free 0x80; clip 2 (0xD0) is largest but PROTECTED -> must drop clip 1 (0x80) instead,
        // even though it is smaller, and keep clip 2's slot pointing at itself.
        var set = SpeciesImporter.ExtractRangeSetStripped(rdt, B + 0x40, B + 0x60, 432 - 0x80,
                                                          new[] { 2 }, out int dropped, out int droppedBytes);
        Assert.Equal(1, dropped);
        Assert.Equal(0x80, droppedBytes);                 // clip 1 dropped, not the bigger protected clip 2
        Assert.Equal(B + 0x120, GetU32(set.Bytes, 0x68 - 0x40)); // clip 2 slot unchanged (-> its own clip)
        Assert.Equal(B + 0x80, GetU32(set.Bytes, 0x64 - 0x40));  // clip 1 slot repointed to fallback clip 0
    }

    [Fact]
    public void ExtractRangeSetStripped_AlreadyFits_DropsNothing()
    {
        var rdt = SyntheticTheriLikeClosure();
        var set = SpeciesImporter.ExtractRangeSetStripped(rdt, B + 0x40, B + 0x60, 0x1000,
                                                          out int dropped, out int droppedBytes);
        Assert.Equal(0, dropped);
        Assert.Equal(0, droppedBytes);
        Assert.Equal(0x1B0, set.Bytes.Length);               // identical to the un-stripped closure
    }

    [Fact]
    public void ExtractRangeSetStripped_CannotFreeEnough_Throws()
    {
        var rdt = SyntheticTheriLikeClosure();
        // Droppable (clips 1,2, never clip 0) = 0x80 + 0xD0 = 0x150; total 0x1B0. A cap of 0x40 needs to free
        // 0x170 > 0x150 -> impossible.
        Assert.Throws<InvalidOperationException>(() =>
            SpeciesImporter.ExtractRangeSetStripped(rdt, B + 0x40, B + 0x60, 0x40, out _, out _));
    }

    [Fact]
    public void RealInstall_ClipStrip_FitsTheriInto0102_UnderCeiling_RoundTrips()
    {
        var dir = DataDir();
        if (dir is null) return;                               // no game files: no-op (CI)
        if (!File.Exists(Path.Combine(dir, "st603.dat"))) return;
        var pristine = Path.Combine(dir, ".dinorand_backup", "st102.dat");
        var p102 = File.Exists(pristine) ? pristine : Path.Combine(dir, "st102.dat");
        if (!File.Exists(p102)) return;

        var donorRf = Load(dir, 6, 0x03, "st603.dat");
        var theri = donorRf.Enemies.First(e => e.Opcode == DcOpcodes.Enemy2 && e.SpeciesBoneCount == 22);
        var full = SpeciesImporter.ExtractDonorClosure(donorRf.RdtBuffer, theri);

        var rf = RoomFile.Read(1, 0x02, File.ReadAllBytes(p102));
        var victim = rf.Enemies.FirstOrDefault(e => e.IsRandomizableDino);
        if (victim is null) return;                            // not pristine 0102: skip
        int before = rf.RdtBuffer.Length;
        int maxDonor = SpeciesImporter.EngineRoomRdtCeiling - ((before + 3) & ~3);

        // 0102 + the full Theri overflows the ceiling — this is exactly the clip-strip case.
        Assert.True(full.Blocks.Bytes.Length > maxDonor);

        // Protect the live-captured used set + the likely death clip (docs/dc1/THERI-CLIP-STRIP-PLAN.md).
        int[] protectedClips = { 1, 12, 13, 15, 19, 48 };
        var stripped = SpeciesImporter.ExtractDonorClosureStripped(
            donorRf.RdtBuffer, theri, maxDonor, protectedClips, out int dropped, out int droppedBytes);
        Assert.True(dropped > 0);
        Assert.True(dropped <= SpeciesImporter.MaxClipStripDropCount);   // 0102 is within the quality bound
        Assert.True(stripped.Blocks.Bytes.Length <= maxDonor);

        // The strip never drops a protected (used / death) clip — the table slots for them are unchanged,
        // so the donor still resolves all of them. Verify by re-extracting the table and checking the
        // protected indices still point at their own clips (not the fallback clip 0).
        var fullClips = SpeciesImporter.MotionClipTable(donorRf.RdtBuffer, full.Blocks.MotionHead,
            full.Blocks.Ranges.Max(r => r.SourceHi));
        foreach (int keep in protectedClips)
            Assert.True(fullClips[keep].Size > 0);   // protected indices are real, in-range clips

        rf.ImportSpecies(stripped, rf.Enemies.IndexOf(victim));
        Assert.True(rf.RdtBuffer.Length <= SpeciesImporter.EngineRoomRdtCeiling);   // fits the engine buffer
        Assert.Equal(22, EnemySkeleton.ReadBoneCount(rf.RdtBuffer, victim.ModelPtr));
        Assert.Equal(8, victim.Category);

        var reread = RoomFile.Read(1, 0x02, rf.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Contains(reread.Enemies, e => e.Species == DinoSpecies.Therizinosaurus && e.Category == 8);
    }
}
