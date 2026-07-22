using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Unit tests for the SCD script walker on synthetic RDT buffers (no game files). They build
/// a buffer whose header dword 0x14 points at a self-describing function-offset table, then
/// assert the walker finds <c>0x28</c> subtype-4 item records and edits/round-trips them.
/// </summary>
public class RoomScriptTests
{
    private const uint PsxBase = RoomScript.PsxRdtBase;

    /// <summary>Build a 44-byte 0x28 subtype-4 item record with the given id/count, and optionally its
    /// ground-visual fields (display slot @ +0x22, model pointer @ +0x24). Default slot 0xFF + model 0 =
    /// an interaction-only spot.</summary>
    private static byte[] ItemRec(byte id, ushort count = 1,
                                  byte slot = ItemRecord.NoDisplaySlot, uint model = 0)
    {
        var r = new byte[DcOpcodes.ItemLength];
        r[0] = DcOpcodes.Item;        // 0x28
        r[2] = DcOpcodes.ItemSubtype; // subtype 4 → length 44
        r[ItemRecord.IdOffset] = id;
        r[ItemRecord.CountOffset] = (byte)count;
        r[ItemRecord.CountOffset + 1] = (byte)(count >> 8);
        r[ItemRecord.DisplaySlotOffset] = slot;
        WriteU32(r, ItemRecord.ModelPtrOffset, model);
        return r;
    }

    /// <summary>Build a 32-byte op23 static-scenery record occupying display-pool slot <paramref name="slot"/>
    /// (its <c>rec+1</c> byte).</summary>
    private static byte[] SceneryRec(byte slot)
    {
        var r = new byte[32];
        r[0] = 0x23;
        r[1] = slot;
        return r;
    }

    /// <summary>
    /// Assemble a decompressed RDT buffer with one subroutine. Layout: a 0x24-byte header
    /// (dword 0x14 → table base), then the self-describing function-offset table, then the
    /// subroutine. The table holds one dword whose value (= n*4 = 4) is both the table size and
    /// the offset from the table base to subroutine 0 (which sits right after the table).
    /// </summary>
    private static byte[] BuildRdt(params byte[][] records)
    {
        const int headerLen = 0x24;
        var body = new List<byte>();
        foreach (var rec in records) body.AddRange(rec);

        int tableBase = headerLen;
        const int tableBytes = 4;                 // one entry → n*4
        int subStart = tableBase + tableBytes;
        var buf = new byte[subStart + body.Count];
        WriteU32(buf, 0x14, PsxBase + (uint)tableBase);
        WriteU32(buf, tableBase, tableBytes);     // entry0 = n*4 = offset to sub0
        body.CopyTo(buf, subStart);
        return buf;
    }

    /// <summary>Build a 24-byte 0x20 enemy record with the given fields (file-form ptrs).</summary>
    private static byte[] EnemyRec(byte slot, byte category, uint model, uint motion, byte killFlag = 0)
    {
        var r = new byte[DcOpcodes.EnemyLength];
        r[0] = DcOpcodes.Enemy;                       // 0x20
        r[EnemyRecord.SlotOffset] = slot;
        r[EnemyRecord.CategoryOffset] = category;
        r[EnemyRecord.KillFlagOffset] = killFlag;
        WriteU32(r, EnemyRecord.ModelOffset, model);
        WriteU32(r, EnemyRecord.MotionOffset, motion);
        return r;
    }

    /// <summary>Build a 20-byte 0x59 enemy record (model @ +0x04, motion @ +0x08, instance-table
    /// index @ +0x10); category + slot share the 0x20 offsets.</summary>
    private static byte[] Enemy2Rec(byte slot, byte category, uint model, uint motion, byte index = 0)
    {
        var r = new byte[DcOpcodes.Enemy2Length];
        r[0] = DcOpcodes.Enemy2;                       // 0x59
        r[EnemyRecord.SlotOffset] = slot;
        r[EnemyRecord.CategoryOffset] = category;
        r[EnemyRecord.Enemy2IndexOffset] = index;
        WriteU32(r, EnemyRecord.Enemy2ModelOffset, model);
        WriteU32(r, EnemyRecord.Enemy2MotionOffset, motion);
        return r;
    }

    /// <summary>Build a 48-byte 0x28 subtype-0 door record (dest stage/room, lock, type, entry pose).
    /// The entry pose words sit at the CE-decoded offsets +0x1e/+0x20/+0x22/+0x24 (plan §3.2).</summary>
    private static byte[] DoorRec(byte stage, byte room, byte lockId = 0, byte doorType = 0,
                                  short x = 0, short y = 0, short z = 0, short d = 0)
    {
        var r = new byte[DcOpcodes.DoorLength];
        r[0] = DcOpcodes.Door;            // 0x28
        r[2] = DcOpcodes.DoorSubtype;     // subtype 0 → length 48
        r[DoorRecord.DestOffset] = room;          // word[+0x1c] low = room
        r[DoorRecord.DestOffset + 1] = stage;     // word[+0x1c] high = stage
        r[DoorRecord.LockOffset] = lockId;
        r[DoorRecord.DoorTypeOffset] = doorType;
        WriteU16(r, DoorPoseLayout.EntryXOffset, (ushort)x);
        WriteU16(r, DoorPoseLayout.EntryYOffset, (ushort)y);
        WriteU16(r, DoorPoseLayout.EntryZOffset, (ushort)z);
        WriteU16(r, DoorPoseLayout.EntryDOffset, (ushort)d);
        return r;
    }

    private static void WriteU16(byte[] b, int off, ushort v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    [Fact]
    public void Walk_FindsItemRecords_WithIdAndCount()
    {
        var rdt = BuildRdt(ItemRec(0x16, 17), ItemRec(0x2b));
        var s = RoomScript.Parse(rdt);

        Assert.True(s.ParsedCleanly);
        Assert.Equal(2, s.Items.Count);
        Assert.Equal(0x16, s.Items[0].ItemId);
        Assert.Equal(17, s.Items[0].Amount);
        Assert.Equal(0x2b, s.Items[1].ItemId);
        Assert.Equal(DcOpcodes.Item, rdt[s.Items[0].FileOffset]);
    }

    [Fact]
    public void Parse_CollectsSceneryAndItemDisplaySlots()
    {
        // op23 scenery on slots 0/1/3 + a visible item on slot 2 + an interaction-only item (0xFF).
        var rdt = BuildRdt(SceneryRec(0), SceneryRec(1), SceneryRec(3),
                           ItemRec(0x16, 1, slot: 2, model: 0x80123456),
                           ItemRec(0x2b));
        var s = RoomScript.Parse(rdt);

        Assert.True(s.ParsedCleanly);
        Assert.Equal(new byte[] { 0, 1, 3 }, s.SceneryDisplaySlots.OrderBy(x => x));
        Assert.Equal(2, s.Items.Single(i => i.ItemId == 0x16).DisplaySlot);
        Assert.Equal(ItemRecord.NoDisplaySlot, s.Items.Single(i => i.ItemId == 0x2b).DisplaySlot);
    }

    [Fact]
    public void ApplyEdits_FlagOff_LeavesVisualFieldsByteIdentical()
    {
        var rdt = BuildRdt(ItemRec(0x16, 1, slot: 5, model: 0x80123456));
        var s = RoomScript.Parse(rdt);
        s.Items[0].ItemId = 0x2b; // relocate the id only

        var buf = (byte[])rdt.Clone();
        s.ApplyEdits(buf, s.Items);
        var reread = RoomScript.Parse(buf);

        Assert.Equal(0x2b, reread.Items[0].ItemId);
        Assert.Equal(5, reread.Items[0].DisplaySlot);              // slot untouched
        Assert.Equal(0x80123456u, ReadU32(reread.Items[0].Raw, ItemRecord.ModelPtrOffset)); // model untouched
    }

    [Fact]
    public void ApplyEdits_AmountOnly_RoundTripsAndChangesOnlyCountWord()
    {
        var rdt = BuildRdt(ItemRec(0x16, 17, slot: 5, model: 0x80123456));
        var script = RoomScript.Parse(rdt);
        var item = Assert.Single(script.Items);
        item.Amount = 0x1234;

        var edited = (byte[])rdt.Clone();
        script.ApplyEdits(edited, script.Items);

        Assert.Equal(0x1234, Assert.Single(RoomScript.Parse(edited).Items).Amount);
        int countOffset = item.FileOffset + ItemRecord.CountOffset;
        for (int i = 0; i < rdt.Length; i++)
        {
            if (i == countOffset || i == countOffset + 1) continue;
            Assert.Equal(rdt[i], edited[i]);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void ApplyEdits_InvalidAmount_FailsClosed(int amount)
    {
        var rdt = BuildRdt(ItemRec(0x16, 17));
        var script = RoomScript.Parse(rdt);
        Assert.Single(script.Items).Amount = amount;
        var edited = (byte[])rdt.Clone();

        Assert.Throws<InvalidDataException>(() => script.ApplyEdits(edited, script.Items));
        Assert.Equal(rdt, edited);
    }

    [Fact]
    public void ApplyEdits_NormalizeVisual_WritesChosenSlotAndGenericPanel()
    {
        // A relocated key in a bespoke-mesh spot (slot 5, old model): reuse the slot, repoint the model.
        var rdt = BuildRdt(ItemRec(0x3a, 1, slot: 5, model: 0x80123456));
        var s = RoomScript.Parse(rdt);
        var it = s.Items[0];
        it.ItemId = 0x2b;
        it.NormalizeVisual = true;
        it.NormalizeDisplaySlot = 9;   // e.g. a freshly-allocated slot

        var buf = (byte[])rdt.Clone();
        s.ApplyEdits(buf, s.Items);
        var reread = RoomScript.Parse(buf);

        Assert.Equal(0x2b, reread.Items[0].ItemId);
        Assert.Equal(9, reread.Items[0].DisplaySlot);
        Assert.Equal(ItemRecord.GenericPanelModelPtr,
                     ReadU32(reread.Items[0].Raw, ItemRecord.ModelPtrOffset));
    }

    [Fact]
    public void Walk_TreatsFfRecord_AsEmptySlot()
    {
        var s = RoomScript.Parse(BuildRdt(ItemRec(0xFF)));
        Assert.Single(s.Items);
        Assert.True(s.Items[0].IsEmptySlot);
    }

    [Fact]
    public void Walk_NotClean_WhenNonTrailingOpcodeUnknown()
    {
        // An unknown opcode (0x72, past the 0x00–0x71 table) derails a non-last subroutine.
        // Build two subroutines so the bad one is not the last.
        const int headerLen = 0x24;
        var sub0 = new byte[] { 0x72, 0, 0, 0 };       // unknown opcode
        var sub1 = ItemRec(0x16);
        const int tableBytes = 8;                      // two entries → n*4
        int tableBase = headerLen;
        int s0 = tableBase + tableBytes;
        int s1 = s0 + sub0.Length;
        var buf = new byte[s1 + sub1.Length];
        WriteU32(buf, 0x14, PsxBase + (uint)tableBase);
        WriteU32(buf, tableBase, tableBytes);          // entry0 = 8 = offset to sub0
        WriteU32(buf, tableBase + 4, (uint)(s1 - tableBase)); // entry1 = offset to sub1
        sub0.CopyTo(buf, s0); sub1.CopyTo(buf, s1);

        Assert.False(RoomScript.Parse(buf).ParsedCleanly);
    }

    [Fact]
    public void Parse_Invalid_WhenNoFunctionTable()
    {
        var s = RoomScript.Parse(new byte[0x20]); // dword 0x14 = 0 → bad pointer
        Assert.False(s.ParsedCleanly);
        Assert.Empty(s.Items);
    }

    [Fact]
    public void ApplyEdits_PatchesIdByte_OnlyForChangedNonEmptyRecords()
    {
        var rdt = BuildRdt(ItemRec(0x16), ItemRec(0xFF));
        var s = RoomScript.Parse(rdt);

        s.Items[0].ItemId = 0x21;     // reroll the real item
        var copy = (byte[])rdt.Clone();
        s.ApplyEdits(copy, s.Items);

        Assert.Equal(0x21, copy[s.Items[0].FileOffset + ItemRecord.IdOffset]);
        // The 0xFF slot is untouched; restore the one edited byte → buffers match.
        copy[s.Items[0].FileOffset + ItemRecord.IdOffset] = 0x16;
        Assert.Equal(rdt, copy);
    }

    [Fact]
    public void OpcodeTable_IsTrustworthy()
    {
        Assert.True(DcOpcodes.IsTrustworthy);
    }

    [Fact]
    public void RoomFile_EditRoundTrips_ThroughLzss()
    {
        // Wrap a synthetic RDT in a one-entry LZSS package, read it, reroll an id, write it,
        // re-read, and assert the new id survives the decompress→edit→recompress→decompress cycle.
        var rdt = BuildRdt(ItemRec(0x16, 17), ItemRec(0x2b));
        var file = BuildLzssPackage(rdt);

        var room = RoomFile.Read(1, 0, file);
        Assert.True(room.ParsedCleanly);
        Assert.Equal(2, room.Items.Count);

        // No edit → byte-exact round-trip.
        Assert.Equal(file, room.Write());

        // Edit → re-read shows the new id; the rest of the RDT is unchanged.
        room.Items[0].ItemId = 0x21;
        var rewritten = room.Write();
        Assert.NotEqual(file, rewritten);

        var reread = RoomFile.Read(1, 0, rewritten);
        Assert.Equal(0x21, reread.Items[0].ItemId);
        Assert.Equal(0x2b, reread.Items[1].ItemId);
        Assert.Equal(17, reread.Items[0].Amount);
        Assert.Equal(room.RdtBuffer.Length, reread.RdtBuffer.Length);
    }

    [Fact]
    public void RoomFile_ScatterItemAndAmount_RoundTripTogether()
    {
        var file = BuildLzssPackage(BuildRdt(ItemRec(0x16, 17)));
        var room = RoomFile.Read(1, 0, file);
        var item = Assert.Single(room.Items);
        item.ItemId = 0x30;
        item.Amount = 5;

        var reread = RoomFile.Read(1, 0, room.Write());

        var scattered = Assert.Single(reread.Items);
        Assert.Equal((0x30, 5), (scattered.ItemId, scattered.Amount));
    }

    [Fact]
    public void Walk_FindsEnemyRecords_WithModelMotionAndCategory()
    {
        var rdt = BuildRdt(
            EnemyRec(0x02, 0x01, 0x8010f3b4, 0x8014cdd4, killFlag: 0x12),
            EnemyRec(0x03, 0x07, 0x80107f64, 0x8010abe4));
        var s = RoomScript.Parse(rdt);

        Assert.True(s.ParsedCleanly);
        Assert.Equal(2, s.Enemies.Count);
        Assert.Equal(0x02, s.Enemies[0].Slot);
        Assert.Equal(0x01, s.Enemies[0].Category);
        Assert.Equal(0x12, s.Enemies[0].KillFlag);
        Assert.Equal(0x8010f3b4u, s.Enemies[0].ModelPtr);
        Assert.Equal(0x8014cdd4u, s.Enemies[0].MotionPtr);
        Assert.Equal(0x80107f64u, s.Enemies[1].ModelPtr);
        Assert.Equal(DcOpcodes.Enemy, rdt[s.Enemies[0].FileOffset]);
        Assert.False(s.Enemies[0].IsEdited);
    }

    [Fact]
    public void Walk_CollectsBothEnemyOpcodes_0x20And0x59()
    {
        var rdt = BuildRdt(
            EnemyRec(0x02, 0x02, 0x8010f3b4, 0x8014cdd4, killFlag: 0x12),
            Enemy2Rec(0x05, 0x08, 0x8010cd68, 0x8010f8ac, index: 0x03));
        var s = RoomScript.Parse(rdt);

        Assert.True(s.ParsedCleanly);
        Assert.Equal(2, s.Enemies.Count);

        var e20 = s.Enemies[0];
        Assert.Equal(DcOpcodes.Enemy, e20.Opcode);
        Assert.Equal(EnemyRecord.ModelOffset, e20.ModelFieldOffset);
        Assert.Equal(0x8010f3b4u, e20.ModelPtr);

        // The 0x59 record: model/motion read from +0x04/+0x08, plus the instance-table index.
        var e59 = s.Enemies[1];
        Assert.Equal(DcOpcodes.Enemy2, e59.Opcode);
        Assert.Equal(DcOpcodes.Enemy2, rdt[e59.FileOffset]);
        Assert.Equal((byte)0x05, e59.Slot);
        Assert.Equal((byte)0x08, e59.Category);
        Assert.Equal((byte)0x03, e59.InstanceIndex);
        Assert.Equal(0x8010cd68u, e59.ModelPtr);
        Assert.Equal(0x8010f8acu, e59.MotionPtr);
        Assert.Equal(EnemyRecord.Enemy2ModelOffset, e59.ModelFieldOffset);
        Assert.False(e59.IsEdited);
    }

    [Fact]
    public void Apply0x59EnemyEdit_PatchesModelAtOpcodeOffset_AndRoundTrips()
    {
        var rdt = BuildRdt(Enemy2Rec(0x05, 0x08, 0x8010cd68, 0x8010f8ac, index: 0x03));
        var s = RoomScript.Parse(rdt);
        var e = Assert.Single(s.Enemies);

        var copy = (byte[])rdt.Clone();
        e.ModelPtr = 0x80112233;
        e.MotionPtr = 0x80114455;
        s.ApplyEnemyEdits(copy, s.Enemies);

        // Patched at the 0x59 offsets (+0x04 / +0x08), not the 0x20 model offset (+0x10).
        Assert.Equal(0x80112233u, ReadU32(copy, e.FileOffset + EnemyRecord.Enemy2ModelOffset));
        Assert.Equal(0x80114455u, ReadU32(copy, e.FileOffset + EnemyRecord.Enemy2MotionOffset));

        var reparsed = RoomScript.Parse(copy);
        Assert.Equal(0x80112233u, Assert.Single(reparsed.Enemies).ModelPtr);
    }

    /// <summary>
    /// Build a single-subroutine RDT whose one enemy record's model pointer targets an
    /// <b>embedded model block</b> with the given skeleton bone count — mirroring the real files,
    /// where the model data lives in the same decompressed RDT buffer as the script (cont.14). The
    /// model block is: a bone-count dword at +0x14, then a <c>boneCount * 0x14</c> bone array.
    /// </summary>
    private static byte[] BuildRdtWithEnemyModel(byte category, int boneCount)
    {
        const int headerLen = 0x24;
        const int tableBytes = 4;
        int tableBase = headerLen;
        int subStart = tableBase + tableBytes;

        int modelOff = subStart + DcOpcodes.EnemyLength;
        uint modelPtr = PsxBase + (uint)modelOff;
        var rec = EnemyRec(0x00, category, model: modelPtr, motion: PsxBase + 0x14u);

        int modelBlockSize = EnemySkeleton.BoneArrayOffset + boneCount * EnemySkeleton.BoneStride;
        var buf = new byte[modelOff + modelBlockSize];
        WriteU32(buf, 0x14, PsxBase + (uint)tableBase);
        WriteU32(buf, tableBase, tableBytes);
        rec.CopyTo(buf, subStart);
        WriteU32(buf, modelOff + EnemySkeleton.BoneCountOffset, (uint)boneCount);
        return buf;
    }

    [Theory]
    [InlineData((byte)1, 15, DinoSpecies.Velociraptor)]
    [InlineData((byte)2, 21, DinoSpecies.RaptorHeavy)]
    [InlineData((byte)3, 20, DinoSpecies.Tyrannosaurus)] // 20-bone cat-3 T-Rex boss (cont.23)
    [InlineData((byte)4, 10, DinoSpecies.Tyrannosaurus)]
    [InlineData((byte)5, 7, DinoSpecies.Swarm)]
    [InlineData((byte)7, 18, DinoSpecies.Pteranodon)]
    public void Walk_DecodesSpecies_FromModelSkeleton(byte category, int bones, DinoSpecies expected)
    {
        var s = RoomScript.Parse(BuildRdtWithEnemyModel(category, bones));

        Assert.Single(s.Enemies);
        Assert.Equal(bones, s.Enemies[0].SpeciesBoneCount);
        Assert.Equal(expected, s.Enemies[0].Species);
        Assert.True(s.Enemies[0].SpeciesMatchesCategory);
    }

    [Fact]
    public void Walk_SpeciesUnknown_WhenModelBlockOutOfBuffer()
    {
        // Model ptr 0x8010f3b4 is in the loaded window (so the record is kept) but there is no
        // model block at that offset in this tiny buffer → the skeleton must not decode.
        var s = RoomScript.Parse(BuildRdt(EnemyRec(0x02, 0x01, 0x8010f3b4, 0x8014cdd4)));

        Assert.Single(s.Enemies);
        Assert.Equal(0, s.Enemies[0].SpeciesBoneCount);
        Assert.Equal(DinoSpecies.Unknown, s.Enemies[0].Species);
        Assert.False(s.Enemies[0].SpeciesMatchesCategory);
    }

    [Theory]
    [InlineData(15, DinoSpecies.Velociraptor)]
    [InlineData(21, DinoSpecies.RaptorHeavy)]
    [InlineData(20, DinoSpecies.Tyrannosaurus)] // both T-Rex rigs decode to Tyrannosaurus (cont.23)
    [InlineData(10, DinoSpecies.Tyrannosaurus)]
    [InlineData(7, DinoSpecies.Swarm)]
    [InlineData(18, DinoSpecies.Pteranodon)]
    [InlineData(22, DinoSpecies.Therizinosaurus)]
    [InlineData(0, DinoSpecies.Unknown)]
    [InlineData(99, DinoSpecies.Unknown)]
    public void EnemySkeleton_MapsBoneCountToSpecies(int bones, DinoSpecies expected)
        => Assert.Equal(expected, EnemySkeleton.FromBoneCount(bones));

    [Fact]
    public void Walk_SkipsEnemyRecord_WhenModelPtrNotInLoadedWindow()
    {
        // A 0x20-length record from a walk desync carries junk at +0x10 (here 0x00800080,
        // outside the loaded-resource window) — it must not be surfaced as a placed enemy.
        var rdt = BuildRdt(EnemyRec(0x00, 0x80, 0x00800080, 0x00800080));
        Assert.Empty(RoomScript.Parse(rdt).Enemies);
    }

    [Fact]
    public void ApplyEnemyEdits_PatchesModelAndMotion_OnlyForChangedRecords()
    {
        var rdt = BuildRdt(
            EnemyRec(0x00, 0x01, 0x80124d28, 0x80146d10),
            EnemyRec(0x01, 0x01, 0x80128868, 0x80148fb4));
        var s = RoomScript.Parse(rdt);

        // Swap the two enemies' model/motion pairs.
        s.Enemies[0].ModelPtr = 0x80128868; s.Enemies[0].MotionPtr = 0x80148fb4;
        var copy = (byte[])rdt.Clone();
        s.ApplyEnemyEdits(copy, s.Enemies);

        Assert.Equal(0x80128868u, ReadU32(copy, s.Enemies[0].FileOffset + EnemyRecord.ModelOffset));
        Assert.Equal(0x80148fb4u, ReadU32(copy, s.Enemies[0].FileOffset + EnemyRecord.MotionOffset));
        // The second record was untouched (still its original pair).
        Assert.Equal(0x80128868u, ReadU32(copy, s.Enemies[1].FileOffset + EnemyRecord.ModelOffset));
    }

    [Fact]
    public void RoomFile_EnemyEditRoundTrips_ThroughLzss()
    {
        var rdt = BuildRdt(
            EnemyRec(0x00, 0x01, 0x80124d28, 0x80146d10),
            EnemyRec(0x01, 0x01, 0x80128868, 0x80148fb4));
        var file = BuildLzssPackage(rdt);

        var room = RoomFile.Read(5, 0x03, file);
        Assert.True(room.ParsedCleanly);
        Assert.Equal(2, room.Enemies.Count);

        // No edit → byte-exact round-trip.
        Assert.Equal(file, room.Write());

        // Swap the pair → re-read shows the swapped model/motion survives the LZSS cycle.
        room.Enemies[0].ModelPtr = 0x80128868; room.Enemies[0].MotionPtr = 0x80148fb4;
        var rewritten = room.Write();
        Assert.NotEqual(file, rewritten);

        var reread = RoomFile.Read(5, 0x03, rewritten);
        Assert.Equal(0x80128868u, reread.Enemies[0].ModelPtr);
        Assert.Equal(0x80148fb4u, reread.Enemies[0].MotionPtr);
        Assert.Equal(0x80128868u, reread.Enemies[1].ModelPtr); // unchanged record
    }

    [Fact]
    public void Walk_FindsDoorRecords_WithDestinationAndLock()
    {
        var rdt = BuildRdt(
            DoorRec(0x01, 0x0d),                       // -> 0x010d, ungated
            DoorRec(0x04, 0x00, lockId: 0x2e, doorType: 3));  // cross-stage -> 0x0400, gated
        var s = RoomScript.Parse(rdt);

        Assert.True(s.ParsedCleanly);
        Assert.Equal(2, s.Doors.Count);
        Assert.Equal(1, s.Doors[0].TargetStage);
        Assert.Equal(0x0d, s.Doors[0].TargetRoom);
        Assert.Equal(0x010d, s.Doors[0].TargetCode);
        Assert.Equal(0, s.Doors[0].LockId);
        Assert.Equal(4, s.Doors[1].TargetStage);
        Assert.Equal(0x00, s.Doors[1].TargetRoom);
        Assert.Equal(0x2e, s.Doors[1].LockId);
        Assert.Equal(3, s.Doors[1].DoorType);
        Assert.Equal(DcOpcodes.Door, rdt[s.Doors[0].FileOffset]);
        Assert.False(s.Doors[0].IsEdited);
    }

    [Fact]
    public void Walk_PreservesDoorSubroutineContext_ForGraphClassification()
    {
        const int headerLen = 0x24;
        const int tableBytes = 8;
        int tableBase = headerLen;
        int s0 = tableBase + tableBytes;
        var sub0 = DoorRec(0x06, 0x09);
        var sub1 = DoorRec(0x06, 0x09);
        int s1 = s0 + sub0.Length;
        var buf = new byte[s1 + sub1.Length];
        WriteU32(buf, 0x14, PsxBase + (uint)tableBase);
        WriteU32(buf, tableBase, tableBytes);
        WriteU32(buf, tableBase + 4, (uint)(s1 - tableBase));
        sub0.CopyTo(buf, s0);
        sub1.CopyTo(buf, s1);

        var doors = RoomScript.Parse(buf).Doors;
        Assert.Equal(2, doors.Count);
        Assert.Equal(0, doors[0].SubroutineIndex);
        Assert.True(doors[0].IsTraversableRoomTransition);
        Assert.Equal(1, doors[1].SubroutineIndex);
        Assert.False(doors[1].IsTraversableRoomTransition);
    }

    [Fact]
    public void Walk_DistinguishesDoorsFromItems_SameOpcode()
    {
        // 0x28 is shared: subtype 0 = door, subtype 4 = item. The walker must split them.
        var rdt = BuildRdt(ItemRec(0x16, 17), DoorRec(0x02, 0x03));
        var s = RoomScript.Parse(rdt);

        Assert.Single(s.Items);
        Assert.Single(s.Doors);
        Assert.Equal(0x16, s.Items[0].ItemId);
        Assert.Equal(0x0203, s.Doors[0].TargetCode);
    }

    [Fact]
    public void ApplyDoorEdits_PatchesDestAndLock_OnlyForChangedRecords()
    {
        var rdt = BuildRdt(DoorRec(0x01, 0x0d), DoorRec(0x02, 0x03, lockId: 0x12));
        var s = RoomScript.Parse(rdt);

        s.Doors[0].TargetStage = 0x05;
        s.Doors[0].TargetRoom = 0x07;
        s.Doors[0].LockId = 0x2e;
        var copy = (byte[])rdt.Clone();
        s.ApplyDoorEdits(copy, s.Doors);

        Assert.Equal(0x07, copy[s.Doors[0].FileOffset + DoorRecord.DestOffset]);
        Assert.Equal(0x05, copy[s.Doors[0].FileOffset + DoorRecord.DestOffset + 1]);
        Assert.Equal(0x2e, copy[s.Doors[0].FileOffset + DoorRecord.LockOffset]);
        // The second door was untouched (still its original dest + lock).
        Assert.Equal(0x03, copy[s.Doors[1].FileOffset + DoorRecord.DestOffset]);
        Assert.Equal(0x12, copy[s.Doors[1].FileOffset + DoorRecord.LockOffset]);
    }

    [Fact]
    public void RoomFile_DoorEditRoundTrips_ThroughLzss()
    {
        var rdt = BuildRdt(DoorRec(0x01, 0x0d), DoorRec(0x02, 0x03, lockId: 0x12));
        var file = BuildLzssPackage(rdt);

        var room = RoomFile.Read(1, 0x0c, file);
        Assert.True(room.ParsedCleanly);
        Assert.Equal(2, room.Doors.Count);

        // No edit → byte-exact round-trip.
        Assert.Equal(file, room.Write());

        // Repoint + relock door 0 → survives the LZSS cycle; door 1 unchanged.
        room.Doors[0].TargetStage = 0x04;
        room.Doors[0].TargetRoom = 0x00;
        room.Doors[0].LockId = 0x2e;
        var rewritten = room.Write();
        Assert.NotEqual(file, rewritten);

        var reread = RoomFile.Read(1, 0x0c, rewritten);
        Assert.Equal(0x0400, reread.Doors[0].TargetCode);
        Assert.Equal(0x2e, reread.Doors[0].LockId);
        Assert.Equal(0x0203, reread.Doors[1].TargetCode);
        Assert.Equal(0x12, reread.Doors[1].LockId);
    }

    [Fact]
    public void PoseLayout_IsDecoded_WithCeConfirmedOffsets()
    {
        // Increment A landed: the four pose offsets are CE-confirmed (consecutive signed words right
        // after the destination word) and the read/write paths are now live.
        Assert.True(DoorPoseLayout.IsDecoded);
        Assert.Equal(0x1e, DoorPoseLayout.EntryXOffset);
        Assert.Equal(0x20, DoorPoseLayout.EntryYOffset);
        Assert.Equal(0x22, DoorPoseLayout.EntryZOffset);
        Assert.Equal(0x24, DoorPoseLayout.EntryDOffset);
    }

    [Fact]
    public void Walk_ReadsDoorEntryPose_WhenDecoded()
    {
        // The 0103→0102 pose observed in Cheat Engine: X=3360, Y=0, Z=560, D=0.
        var rdt = BuildRdt(DoorRec(0x01, 0x02, x: 3360, y: 0, z: 560, d: 0));
        var door = Assert.Single(RoomScript.Parse(rdt).Doors);

        Assert.Equal(3360, door.EntryX);
        Assert.Equal(0, door.EntryY);
        Assert.Equal(560, door.EntryZ);
        Assert.Equal(0, door.EntryD);
        // Original mirrors equal the decoded values → an untouched pose is not "edited".
        Assert.Equal(3360, door.OriginalEntryX);
        Assert.False(door.PoseEdited);
        Assert.False(door.IsEdited);
    }

    [Fact]
    public void ApplyDoorEdits_WritesEntryPose_WhenPoseCarried()
    {
        var rdt = BuildRdt(DoorRec(0x01, 0x02, x: 3360, y: 0, z: 560, d: 0));
        var s = RoomScript.Parse(rdt);
        var door = Assert.Single(s.Doors);

        // Re-point this door and carry a donor pose (negative coords exercise signed round-trip).
        door.TargetStage = 0x06; door.TargetRoom = 0x0d;
        door.EntryX = -2512; door.EntryY = 8; door.EntryZ = 1992; door.EntryD = 2048;
        Assert.True(door.PoseEdited);

        var copy = (byte[])rdt.Clone();
        s.ApplyDoorEdits(copy, s.Doors);

        Assert.Equal((short)-2512, (short)ReadU16(copy, door.FileOffset + DoorPoseLayout.EntryXOffset));
        Assert.Equal((short)8, (short)ReadU16(copy, door.FileOffset + DoorPoseLayout.EntryYOffset));
        Assert.Equal((short)1992, (short)ReadU16(copy, door.FileOffset + DoorPoseLayout.EntryZOffset));
        Assert.Equal((short)2048, (short)ReadU16(copy, door.FileOffset + DoorPoseLayout.EntryDOffset));
        // Destination travelled with the pose; the AOT trigger box (local) was left intact.
        Assert.Equal(0x060d, ((copy[door.FileOffset + DoorRecord.DestOffset + 1] & 0xff) << 8)
                              | (copy[door.FileOffset + DoorRecord.DestOffset] & 0xff));
    }

    [Fact]
    public void RoomFile_RepointedDoor_CarriesDonorPose_ThroughLzss()
    {
        // Two reciprocal doors with distinct poses. Re-point door 0 to door 1's destination and
        // carry door 1's (donor) pose — the §4.1 reciprocal rewrite — and prove it survives the
        // decompress→edit→recompress→decompress cycle byte-for-byte.
        var rdt = BuildRdt(
            DoorRec(0x01, 0x02, x: 3360, y: 0, z: 560, d: 0),       // door 0 → 0102
            DoorRec(0x06, 0x0d, x: -2512, y: 8, z: 1992, d: 2048)); // door 1 → 060d (the donor)
        var file = BuildLzssPackage(rdt);

        var room = RoomFile.Read(1, 0x0c, file);
        Assert.Equal(2, room.Doors.Count);
        Assert.Equal(file, room.Write()); // no edit → byte-exact

        var donor = room.Doors[1];
        room.Doors[0].TargetStage = donor.OriginalTargetStage;
        room.Doors[0].TargetRoom = donor.OriginalTargetRoom;
        room.Doors[0].EntryX = donor.OriginalEntryX;
        room.Doors[0].EntryY = donor.OriginalEntryY;
        room.Doors[0].EntryZ = donor.OriginalEntryZ;
        room.Doors[0].EntryD = donor.OriginalEntryD;
        Assert.True(room.Doors[0].PoseEdited);

        var reread = RoomFile.Read(1, 0x0c, room.Write());
        Assert.Equal(0x060d, reread.Doors[0].TargetCode);
        Assert.Equal(-2512, reread.Doors[0].EntryX);
        Assert.Equal(8, reread.Doors[0].EntryY);
        Assert.Equal(1992, reread.Doors[0].EntryZ);
        Assert.Equal(2048, reread.Doors[0].EntryD);
        // Donor door 1 itself was not edited → unchanged.
        Assert.Equal(0x060d, reread.Doors[1].TargetCode);
        Assert.False(reread.Doors[1].IsEdited);
    }

    private static uint ReadU32(byte[] b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    private static int ReadU16(byte[] b, int off) => b[off] | (b[off + 1] << 8);

    private static byte[] BuildLzssPackage(byte[] rdt)
    {
        var payload = Lzss.Compress(rdt);
        int sector = GianPackage.SectorSize;
        int padded = (payload.Length + sector - 1) & ~(sector - 1);
        var file = new byte[GianPackage.HeaderSize + padded];
        file[0] = (byte)GianEntryType.Unknown;               // type 7 (LZSS room data)
        uint size = (uint)payload.Length;
        file[4] = (byte)size; file[5] = (byte)(size >> 8);
        file[6] = (byte)(size >> 16); file[7] = (byte)(size >> 24);
        payload.CopyTo(file, GianPackage.HeaderSize);
        return file;
    }
}
