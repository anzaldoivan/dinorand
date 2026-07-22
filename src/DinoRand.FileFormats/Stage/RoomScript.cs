namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Walks the Dino Crisis 1 SCD script in a room's <b>decompressed RDT buffer</b> and exposes
/// the item-placement records the randomizer edits (doors/enemies later).
///
/// <para><b>Layout (proven — see <c>docs/reference/dc1/_registries/STATIC-SCD-RE.md</c>).</b> The RDT buffer is the
/// decompressed payload of the room file's last package entry (LZSS for type-7 entries, raw for
/// type-0). Its header dword at offset <c>0x14</c> is a PSX pointer (<c>0x8010xxxx</c>) to a
/// self-describing function-offset table: the first dword is the table size in bytes, so it
/// holds <c>size/4</c> subroutine offsets (each relative to the table base). This is the same
/// table the engine installs as the per-object gosub table (<c>obj+0x268</c>), so every event
/// subroutine is reachable here. Each subroutine is a variable-length SCD opcode stream walked
/// with <see cref="DcOpcodes"/>.</para>
///
/// <para><b>Items.</b> Item placements are <c>0x28</c> opcodes in subtype-4 form
/// (<see cref="DcOpcodes.Item"/>/<see cref="DcOpcodes.ItemSubtype"/>, length
/// <see cref="DcOpcodes.ItemLength"/>); id = low byte of word at <see cref="ItemRecord.IdOffset"/>,
/// count = word at <see cref="ItemRecord.CountOffset"/>. Records carry their RDT-buffer offset so
/// edits patch the id in place; the buffer is then re-emitted (re-compressed for type-7 entries)
/// by <see cref="RoomFile"/>.</para>
/// </summary>
public sealed class RoomScript
{
    /// <summary>PSX address the RDT buffer is loaded at; header pointers are relative to it.</summary>
    public const uint PsxRdtBase = 0x80100000;

    /// <summary>
    /// PSX address window for a loaded room resource. The RDT relocates into <c>0x8010xxxx..</c>;
    /// real enemy model/motion pointers observed across the corpus fall in
    /// <c>[0x80100000, 0x80180000)</c>. The init-script walk occasionally desyncs on trailing
    /// data and emits a spurious "0x20" record (all-<c>0x80</c> padding, etc.); such records carry
    /// a <see cref="EnemyRecord.ModelOffset"/> value outside this window, so requiring a valid
    /// model pointer cleanly distinguishes a real placed enemy from a walk artifact.
    /// </summary>
    private const uint LoadedPtrLo = PsxRdtBase;
    private const uint LoadedPtrHi = 0x80180000;

    private static bool IsLoadedPtr(uint p) => p >= LoadedPtrLo && p < LoadedPtrHi;

    /// <summary>SCD opcode for a static-scenery display node (op23): it fills the same
    /// <c>scratch+0x7CE8</c> display-node pool item visuals use, so its slot indexes are occupied
    /// (STATIC-SCD-RE cont.72 / handler <c>0x4266A6</c>). Its slot byte is <c>rec+1</c>.</summary>
    private const byte Scenery = 0x23;
    private const int SceneryLength = 32;
    private const int ScenerySlotOffset = 0x01;

    /// <summary>Fail-closed ceiling for Lever-A display-slot allocation. 32 pool entries are proven to
    /// exist (index <c>0x1F</c> is filled by op23 in st202); the pool's true capacity is CE-unmeasured, so
    /// visual normalization never allocates a slot at or above this. PICKUP-GROUND-MODEL-FEASIBILITY.md.</summary>
    public const int DisplaySlotPoolCap = 0x20;

    private RoomScript(bool parsedCleanly, int tableOffset, int subroutineCount,
                       IReadOnlyList<ItemRecord> items, IReadOnlyList<EnemyRecord> enemies,
                       IReadOnlyList<DoorRecord> doors, IReadOnlyCollection<byte> scenerySlots)
    {
        ParsedCleanly = parsedCleanly;
        TableOffset = tableOffset;
        SubroutineCount = subroutineCount;
        Items = items;
        Enemies = enemies;
        Doors = doors;
        SceneryDisplaySlots = scenerySlots;
    }

    /// <summary>True when the function-offset table was valid and every non-trailing subroutine
    /// walked with only known opcodes (trailing non-code data after the last subroutine is normal
    /// and does not clear this flag).</summary>
    public bool ParsedCleanly { get; }

    /// <summary>RDT-buffer offset of the function-offset table (header dword <c>0x14</c>), or -1.</summary>
    public int TableOffset { get; }

    /// <summary>Number of subroutines declared by the function-offset table.</summary>
    public int SubroutineCount { get; }

    /// <summary>Item-placement records found in the script (empty when not parsed cleanly).</summary>
    public IReadOnlyList<ItemRecord> Items { get; }

    /// <summary>Enemy-placement records found in the script (empty when not clean). Includes both
    /// enemy opcodes — <c>0x20</c> and <c>0x59</c> (<see cref="EnemyRecord.Opcode"/> distinguishes
    /// them); a record's <see cref="EnemyRecord.ModelFieldOffset"/> follows its opcode.</summary>
    public IReadOnlyList<EnemyRecord> Enemies { get; }

    /// <summary>All raw door / area-transition records (<c>0x28</c> subtype 0) found in every
    /// function-table subroutine. Use <see cref="DoorRecord.IsTraversableRoomTransition"/> for the
    /// graph's init-path transition contract; event records remain here for lossless consumers.</summary>
    public IReadOnlyList<DoorRecord> Doors { get; }

    /// <summary>Display-node pool slots occupied by op23 static-scenery records in this room (their
    /// <c>rec+1</c> slot bytes). Item pickups share the same pool via <see cref="ItemRecord.DisplaySlot"/>,
    /// so a Lever-A visual normalization must avoid both sets when allocating a fresh slot. cont.72.</summary>
    public IReadOnlyCollection<byte> SceneryDisplaySlots { get; }

    /// <summary>
    /// Parse the SCD script out of a decompressed RDT <paramref name="buffer"/>.
    /// </summary>
    public static RoomScript Parse(ReadOnlySpan<byte> buffer)
    {
        if (!TryReadFunctionTable(buffer, out int tableOffset, out var starts))
            return new RoomScript(false, -1, 0, Array.Empty<ItemRecord>(),
                                  Array.Empty<EnemyRecord>(), Array.Empty<DoorRecord>(),
                                  Array.Empty<byte>());

        var items = new List<ItemRecord>();
        var enemies = new List<EnemyRecord>();
        var doors = new List<DoorRecord>();
        var scenerySlots = new HashSet<byte>();
        bool clean = true;
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i];
            int end = i + 1 < starts.Count ? starts[i + 1] : buffer.Length;
            bool ok = WalkSubroutine(buffer, start, end, i, items, enemies, doors, scenerySlots);
            // A derail anywhere but the last subroutine means an unknown/misaligned opcode.
            if (!ok && i != starts.Count - 1) clean = false;
        }

        // Flag NPC-scene actors (Rick/Gail/Kirk) once all 0x20 records are visible: the shared-motion /
        // distinct-model tell is relational, so it can't be decided per-record at parse (cont.41).
        EnemyRecord.MarkNpcSceneActors(enemies);
        return new RoomScript(clean, tableOffset, starts.Count, items, enemies, doors, scenerySlots);
    }

    /// <summary>
    /// Read the self-describing function-offset table at header dword <c>0x14</c>. Returns its
    /// buffer offset and the list of subroutine start offsets, or false if it isn't valid.
    /// </summary>
    private static bool TryReadFunctionTable(ReadOnlySpan<byte> buffer, out int tableOffset,
                                             out List<int> starts)
    {
        tableOffset = -1;
        starts = new List<int>();
        if (buffer.Length < 0x18) return false;

        uint ptr = ReadU32(buffer, 0x14);
        long off = (long)ptr - PsxRdtBase;
        if (off < 0 || off + 4 > buffer.Length) return false;
        int baseOff = (int)off;

        uint first = ReadU32(buffer, baseOff);
        if (first == 0 || first % 4 != 0 || baseOff + (long)first > buffer.Length) return false;

        int n = (int)(first / 4);
        for (int i = 0; i < n; i++)
        {
            uint entry = ReadU32(buffer, baseOff + i * 4);
            long s = baseOff + (long)entry;
            if (s < 0 || s > buffer.Length) return false;
            starts.Add((int)s);
        }
        tableOffset = baseOff;
        return true;
    }

    /// <summary>
    /// Walk <c>[start, end)</c> as an opcode stream, collecting item records. Returns false if a
    /// record has an unknown length or would run past <paramref name="end"/> (trailing data).
    /// </summary>
    private static bool WalkSubroutine(ReadOnlySpan<byte> buffer, int start, int end, int subroutineIndex,
                                       List<ItemRecord> items, List<EnemyRecord> enemies,
                                       List<DoorRecord> doors, HashSet<byte> scenerySlots)
    {
        if (start < 0 || end > buffer.Length) return false;
        int pos = start;
        while (pos < end)
        {
            int len = DcOpcodes.Length(buffer, pos);
            if (len <= 0 || pos + len > end) return false;

            if (buffer[pos] == Scenery && len == SceneryLength)
                scenerySlots.Add(buffer[pos + ScenerySlotOffset]);
            else if (buffer[pos] == DcOpcodes.Door
                && len == DcOpcodes.DoorLength
                && buffer[pos + 2] == DcOpcodes.DoorSubtype)
            {
                int dest = ReadU16(buffer, pos + DoorRecord.DestOffset);
                int stage = (dest >> 8) & 0xff;
                int droom = dest & 0xff;
                var door = new DoorRecord
                {
                    TargetStage = stage,
                    TargetRoom = droom,
                    OriginalTargetStage = stage,
                    OriginalTargetRoom = droom,
                    LockId = buffer[pos + DoorRecord.LockOffset],
                    OriginalLockId = buffer[pos + DoorRecord.LockOffset],
                    DoorType = buffer[pos + DoorRecord.DoorTypeOffset],
                    SubroutineIndex = subroutineIndex,
                    Raw = buffer.Slice(pos, len).ToArray(),
                    FileOffset = pos,
                };
                // Entry pose is read only once its offsets are CE-decoded (HARD GATE, plan §3.2);
                // while undecoded it stays 0 == Original, so the door round-trips byte-exact.
                if (DoorPoseLayout.IsDecoded)
                {
                    door.EntryX = door.OriginalEntryX = (short)ReadU16(buffer, pos + DoorPoseLayout.EntryXOffset);
                    door.EntryY = door.OriginalEntryY = (short)ReadU16(buffer, pos + DoorPoseLayout.EntryYOffset);
                    door.EntryZ = door.OriginalEntryZ = (short)ReadU16(buffer, pos + DoorPoseLayout.EntryZOffset);
                    door.EntryD = door.OriginalEntryD = (short)ReadU16(buffer, pos + DoorPoseLayout.EntryDOffset);
                }
                doors.Add(door);
            }
            else if (buffer[pos] == DcOpcodes.Item
                && len == DcOpcodes.ItemLength
                && buffer[pos + 2] == DcOpcodes.ItemSubtype)
            {
                byte id = buffer[pos + ItemRecord.IdOffset];
                ushort take = (ushort)ReadU16(buffer, pos + ItemRecord.TakeIndexOffset);
                int amount = ReadU16(buffer, pos + ItemRecord.CountOffset);
                items.Add(new ItemRecord
                {
                    ItemId = id,
                    OriginalItemId = id,
                    Amount = amount,
                    OriginalAmount = amount,
                    TakeIndex = take,
                    OriginalTakeIndex = take,
                    Raw = buffer.Slice(pos, len).ToArray(),
                    FileOffset = pos,
                });
            }
            else if (buffer[pos] == DcOpcodes.Enemy && len == DcOpcodes.EnemyLength)
            {
                uint model = ReadU32(buffer, pos + EnemyRecord.ModelOffset);
                // A real placed enemy carries a loaded EMD pointer here; padding/walk desync does not.
                if (IsLoadedPtr(model))
                {
                    uint motion = ReadU32(buffer, pos + EnemyRecord.MotionOffset);
                    enemies.Add(new EnemyRecord
                    {
                        Slot = buffer[pos + EnemyRecord.SlotOffset],
                        Category = buffer[pos + EnemyRecord.CategoryOffset],
                        KillFlag = buffer[pos + EnemyRecord.KillFlagOffset],
                        PosX = (short)ReadU16(buffer, pos + EnemyRecord.PosXOffset),
                        PosY = (short)ReadU16(buffer, pos + EnemyRecord.PosYOffset),
                        PosZ = (short)ReadU16(buffer, pos + EnemyRecord.PosZOffset),
                        Rotation = (short)ReadU16(buffer, pos + EnemyRecord.RotationOffset),
                        ModelPtr = model,
                        MotionPtr = motion,
                        OriginalModelPtr = model,
                        OriginalMotionPtr = motion,
                        // Preset maxHP (+6) — 0x20 only; copied to entity +0x11A by 0x42656A (DC1-G2).
                        MaxHp = (ushort)ReadU16(buffer, pos + EnemyRecord.MaxHpOffset),
                        OriginalMaxHp = (ushort)ReadU16(buffer, pos + EnemyRecord.MaxHpOffset),
                        // Decode the species from the model's embedded skeleton (cont.14): the
                        // model block lives in this same buffer at modelPtr - PSX base.
                        SpeciesBoneCount = EnemySkeleton.ReadBoneCount(buffer, model),
                        Raw = buffer.Slice(pos, len).ToArray(),
                        FileOffset = pos,
                    });
                }
            }
            else if (buffer[pos] == DcOpcodes.Enemy2 && len == DcOpcodes.Enemy2Length)
            {
                // The second enemy opcode (0x59, cont.20): category + model + motion are inline (at
                // different offsets than 0x20), while position + kill-flag come from a secondary
                // instance table indexed by record+0x10. Same loaded-pointer guard distinguishes a
                // real placement from a walk-desync artifact.
                uint model = ReadU32(buffer, pos + EnemyRecord.Enemy2ModelOffset);
                if (IsLoadedPtr(model))
                {
                    uint motion = ReadU32(buffer, pos + EnemyRecord.Enemy2MotionOffset);
                    enemies.Add(new EnemyRecord
                    {
                        Opcode = DcOpcodes.Enemy2,
                        Slot = buffer[pos + EnemyRecord.SlotOffset],
                        Category = buffer[pos + EnemyRecord.CategoryOffset],
                        InstanceIndex = buffer[pos + EnemyRecord.Enemy2IndexOffset],
                        // Position / kill-flag are not inline in a 0x59 record (instance-table sourced).
                        ModelPtr = model,
                        MotionPtr = motion,
                        OriginalModelPtr = model,
                        OriginalMotionPtr = motion,
                        SpeciesBoneCount = EnemySkeleton.ReadBoneCount(buffer, model),
                        Raw = buffer.Slice(pos, len).ToArray(),
                        FileOffset = pos,
                    });
                }
            }
            pos += len;
        }
        return true;
    }

    /// <summary>
    /// Patch edited item ids back into a decompressed RDT <paramref name="buffer"/>. Only the id
    /// byte of each record is written (the high id byte stays 0); empty slots (id 0xFF) and
    /// records without a positional offset are skipped.
    ///
    /// <para>When <see cref="ItemRecord.NormalizeVisual"/> is set (Lever A, off by default), the record's
    /// ground visual is also rewritten to the shared generic pickup panel: display slot
    /// (<see cref="ItemRecord.DisplaySlotOffset"/>) = <see cref="ItemRecord.NormalizeDisplaySlot"/> and the
    /// model pointer (<see cref="ItemRecord.ModelPtrOffset"/>) = <see cref="ItemRecord.GenericPanelModelPtr"/>.
    /// With the flag unset these two fields are left byte-identical, so an id-only edit round-trips
    /// exactly. PICKUP-GROUND-MODEL-FEASIBILITY.md.</para>
    /// </summary>
    public void ApplyEdits(byte[] buffer, IReadOnlyList<ItemRecord> edited)
    {
        // Validate the complete amount batch before changing the caller's buffer. Item counts are
        // encoded as an unsigned 16-bit word at record+0x1e (STATIC-SCD-RE cont.9).
        foreach (var item in edited)
        {
            if (item.FileOffset < 0 || item.IsEmptySlot) continue;
            if (item.Amount is < ushort.MinValue or > ushort.MaxValue)
                throw new InvalidDataException(
                    $"Item amount {item.Amount} at RDT offset 0x{item.FileOffset:X} is outside the unsigned 16-bit count range.");
        }

        foreach (var item in edited)
        {
            if (item.FileOffset < 0 || item.IsEmptySlot) continue;
            int idPos = item.FileOffset + ItemRecord.IdOffset;
            if (idPos >= 0 && idPos < buffer.Length)
                buffer[idPos] = (byte)item.ItemId;

            if (item.Amount != item.OriginalAmount)
            {
                int countPos = item.FileOffset + ItemRecord.CountOffset;
                if (countPos + 2 <= buffer.Length)
                    WriteU16(buffer, countPos, (ushort)item.Amount);
            }

            // Take-index rekey (AP client only — EXE-SYMBOLS cont.81): both the registration's
            // suppress check and the take commit read this word from the record, so rewriting it
            // re-keys the pickup's taken flag coherently. Unchanged => byte-identical round-trip.
            if (item.TakeIndex != item.OriginalTakeIndex)
            {
                int takePos = item.FileOffset + ItemRecord.TakeIndexOffset;
                if (takePos + 2 <= buffer.Length)
                    WriteU16(buffer, takePos, item.TakeIndex);
            }

            if (item.NormalizeVisual)
            {
                int slotPos = item.FileOffset + ItemRecord.DisplaySlotOffset;
                int ptrPos = item.FileOffset + ItemRecord.ModelPtrOffset;
                if (ptrPos + 4 <= buffer.Length)
                {
                    buffer[slotPos] = item.NormalizeDisplaySlot;
                    // Lever A leaves the default (generic panel); Lever B points at an appended donor mesh.
                    WriteU32(buffer, ptrPos, item.VisualModelPtr);
                }
            }
        }
    }

    /// <summary>
    /// Patch edited enemy (model, motion) pointer pairs back into a decompressed RDT
    /// <paramref name="buffer"/>. Pointers stay in file form (<c>0x8010xxxx</c>); the engine
    /// relocates them uniformly at load, so swapping two of them is correct. Unedited records and
    /// records without a positional offset are skipped.
    /// </summary>
    public void ApplyEnemyEdits(byte[] buffer, IReadOnlyList<EnemyRecord> edited)
    {
        foreach (var enemy in edited)
        {
            if (enemy.FileOffset < 0 || !enemy.IsEdited) continue;
            // Model/motion sit at different offsets in a 0x20 vs a 0x59 record — patch per opcode.
            int modelPos = enemy.FileOffset + enemy.ModelFieldOffset;
            int motionPos = enemy.FileOffset + enemy.MotionFieldOffset;
            if (motionPos + 4 > buffer.Length) continue;
            // Category (record[2]) is the AI-class dispatch byte; for an in-room permute it is
            // unchanged (idempotent write), for a cross-room species import it carries the donor's
            // AI class so the matching AI runs on the imported model.
            buffer[enemy.FileOffset + EnemyRecord.CategoryOffset] = enemy.Category;
            WriteU32(buffer, modelPos, enemy.ModelPtr);
            WriteU32(buffer, motionPos, enemy.MotionPtr);
            // maxHP (+6) is a 0x20-only field (in a 0x59 record +6 is the model pointer); write it only
            // when the HP pass changed it, so a model-only permute leaves the vanilla +6 (roll default,
            // provably byte-exact — RoomFileRoundTripTests.Enemy_MaxHpWord_SurvivesModelEditRoundTrip).
            if (enemy.Opcode == DcOpcodes.Enemy && enemy.MaxHp != enemy.OriginalMaxHp)
                WriteU16(buffer, enemy.FileOffset + EnemyRecord.MaxHpOffset, enemy.MaxHp);
        }
    }

    /// <summary>
    /// Patch edited door destinations / locks (and, once decoded, the carried entry pose) back into
    /// a decompressed RDT <paramref name="buffer"/>. The destination word (<c>+0x1c</c>) is rewritten
    /// as <c>stage&lt;&lt;8 | room</c> and the lock byte (<c>+0x27</c>) as the gate id. The entry-pose
    /// words are written <b>only</b> when <see cref="DoorPoseLayout.IsDecoded"/> is true; all other
    /// bytes (door type, AOT trigger box, …) are left intact. Unedited records and records without a
    /// positional offset are skipped.
    ///
    /// <para><b>HARD GATE (plan §3.2).</b> If a record carries a pose change
    /// (<see cref="DoorRecord.PoseEdited"/>) while the pose offsets are still undecoded, this throws
    /// rather than write to a guessed offset — a re-pointed door must never spawn the player at wrong
    /// coordinates via a silently-wrong byte. The door pass gates on the same flag and so never
    /// reaches this throw in normal operation; it is the defensive backstop.</para>
    /// </summary>
    public void ApplyDoorEdits(byte[] buffer, IReadOnlyList<DoorRecord> edited)
    {
        foreach (var door in edited)
        {
            if (door.FileOffset < 0 || !door.IsEdited) continue;

            if (door.PoseEdited && !DoorPoseLayout.IsDecoded)
                throw new InvalidOperationException(
                    "Door entry-pose offsets are not decoded (DOOR-RANDOMIZER-PLAN.md §3.2 HARD GATE); " +
                    "refusing to write a re-pointed door's spawn pose to a guessed offset. Decode the " +
                    "pose bytes in Cheat Engine and set DoorPoseLayout.IsDecoded before carrying poses.");

            int destPos = door.FileOffset + DoorRecord.DestOffset;
            int lockPos = door.FileOffset + DoorRecord.LockOffset;
            if (lockPos >= buffer.Length) continue;
            buffer[destPos] = (byte)door.TargetRoom;
            buffer[destPos + 1] = (byte)door.TargetStage;
            buffer[lockPos] = (byte)door.LockId;

            if (door.PoseEdited) // implies DoorPoseLayout.IsDecoded (guarded above)
            {
                WriteU16(buffer, door.FileOffset + DoorPoseLayout.EntryXOffset, (ushort)door.EntryX);
                WriteU16(buffer, door.FileOffset + DoorPoseLayout.EntryYOffset, (ushort)door.EntryY);
                WriteU16(buffer, door.FileOffset + DoorPoseLayout.EntryZOffset, (ushort)door.EntryZ);
                WriteU16(buffer, door.FileOffset + DoorPoseLayout.EntryDOffset, (ushort)door.EntryD);
            }
        }
    }

    private static void WriteU16(byte[] b, int off, ushort v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
    }

    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v;
        b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16);
        b[off + 3] = (byte)(v >> 24);
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));

    private static int ReadU16(ReadOnlySpan<byte> b, int off)
        => b[off] | (b[off + 1] << 8);
}
