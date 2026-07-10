namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Byte layout of the <b>entry pose</b> inside the 48-B door record — the position/orientation the
/// engine drops the player at when they walk through a door into its <i>destination</i> room.
///
/// <para><b>DECODED — CE-validated 2026-06-20 (docs/decisions/dc1/doors/DOOR-RANDOMIZER-PLAN.md §3.2, Increment A).</b>
/// The four pose offsets are proven: the pose is RE-style, living in the <i>source</i> door record as
/// four consecutive signed words immediately after the destination word (<c>+0x1c</c>) — see
/// <see cref="IsDecoded"/> for the byte-cited proof. So <see cref="IsDecoded"/> is <c>true</c> and
/// re-pointed doors carry their arrival pose. The safety machinery around the gate is retained: while
/// <see cref="IsDecoded"/> is <c>false</c> the offsets fall back to the <see cref="Unknown"/> sentinel
/// and <see cref="RoomScript.ApplyDoorEdits"/> throws rather than write a guessed byte, so the class
/// can be reverted to the undecoded state without touching call sites.</para>
/// </summary>
public static class DoorPoseLayout
{
    /// <summary>Sentinel for an unestablished offset — used only in the reverted/undecoded state
    /// (offsets are CE-established today; see <see cref="IsDecoded"/>).</summary>
    public const int Unknown = -1;

    /// <summary>
    /// <c>true</c> only once the four pose offsets below are CE-validated. While <c>false</c> the
    /// randomizer treats the entry pose as un-carryable and leaves re-pointed doors vanilla.
    ///
    /// <para><b>DECODED — CE-validated 2026-06-20 (docs/reference/dc1/_registries/STATIC-SCD-RE.md, door section).</b> The pose
    /// is model (a) / RE-style: it lives in the <i>source</i> door record as four consecutive signed
    /// words immediately after the destination word (<c>+0x1c</c>). Proven two ways against
    /// <c>0103→0102</c> (Management Office → its hall, <c>st103.dat</c>): (1) on traversal the engine
    /// copies <c>record[+0x1e/+0x20/+0x22/+0x24]</c> verbatim into the entry-pose global
    /// (<c>DINO.exe</c> static data <c>0x006D3E6C</c>, layout X@+0x20/Y@+0x22/Z@+0x24/D@+0x26); and
    /// (2) patching those record words in RAM and walking the door moved/rotated the spawn to the
    /// patched coordinates (X 3360→2000, Z 560→1600, D 0→2048=180° all tracked). The trailing byte
    /// <c>+0x26</c> (candidate camera/floor) varies per-door in the corpus but patching it produced no
    /// visible change (arrival camera is position-driven), so it is <b>not</b> carried.</para>
    /// </summary>
    public const bool IsDecoded = true;

    /// <summary>Offset of the destination-room entry X word (signed). CE-confirmed (plan §7 Increment A).</summary>
    public const int EntryXOffset = 0x1e;
    /// <summary>Offset of the destination-room entry Y word (signed, height). CE-confirmed.</summary>
    public const int EntryYOffset = 0x20;
    /// <summary>Offset of the destination-room entry Z word (signed). CE-confirmed.</summary>
    public const int EntryZOffset = 0x22;
    /// <summary>Offset of the destination-room entry facing/direction word. CE-confirmed.</summary>
    public const int EntryDOffset = 0x24;
}

/// <summary>A door / area-transition placed by SCD opcode <c>0x28</c> in its subtype-0 form.</summary>
/// <remarks>
/// Doors are the sibling of the item record (subtype 4) under the same <c>0x28</c> AOT
/// ("area-of-things") umbrella opcode (<see cref="DcOpcodes.Door"/>/<see cref="DcOpcodes.DoorSubtype"/>,
/// length <see cref="DcOpcodes.DoorLength"/>=48). Within the record:
/// <list type="bullet">
/// <item>destination = word at <see cref="DestOffset"/> (LE) = <c>SSRR</c>: low byte = room,
/// high byte = stage — the canonical room code (e.g. <c>0x010d</c>), the key used throughout
/// <c>data/dc1/map.json</c> / <c>room-data.json</c>.</item>
/// <item>lock = byte at <see cref="LockOffset"/> — the door's <c>GetFlag</c> group-9 gate id
/// (0 = ungated). Almost every normal door (<see cref="DoorType"/>=0) is ungated, while every
/// gated door variant (<see cref="DoorType"/> 1/2/3/8/…) carries a nonzero lock.</item>
/// <item>type = byte at <see cref="DoorTypeOffset"/> — the door sub-action / kind (0 = normal).</item>
/// </list>
/// Proven from <c>DINO.exe</c>: the AOT-type action table (<c>0x64fd58</c>, indexed by the
/// subtype) routes subtype-0 to the door handler family; the common-door handler
/// (<c>0x4493a8</c>) reads <c>word[+0x1c]</c> as the destination (comparing it against literal
/// room codes), and the door sub-action gates on <c>GetFlag(group 9, byte[+0x27])</c>. Validated
/// against the corpus: 306/324 subtype-0 records decode to a known room (the rest target the
/// <c>stA*</c> demo stages), a BFS from the start room reaches 93/95 story rooms, and ~92% of
/// doors are reciprocal. See <c>docs/reference/dc1/_registries/STATIC-SCD-RE.md</c> (2026-06-20 cont.12) and memory
/// <c>scd-door-record-found</c>.
/// </remarks>
public sealed class DoorRecord
{
    /// <summary>Opcode that introduces a door-placement record in the room script.</summary>
    public const byte Opcode = DcOpcodes.Door;
    /// <summary>Subtype byte (<c>record[2]</c>) that marks a <see cref="Opcode"/> record as a door.</summary>
    public const byte Subtype = DcOpcodes.DoorSubtype;
    /// <summary>Total bytes of a door (<c>0x28</c> subtype-0) record, opcode byte included.</summary>
    public const int Length = DcOpcodes.DoorLength;
    /// <summary>Byte offset of the destination word (low byte = room, high byte = stage).</summary>
    public const int DestOffset = 0x1c;
    /// <summary>Byte offset of the lock / gate-flag id (0 = ungated).</summary>
    public const int LockOffset = 0x27;
    /// <summary>Byte offset of the door sub-action / kind byte (0 = normal door).</summary>
    public const int DoorTypeOffset = 0x28;

    /// <summary><see cref="DoorType"/> of a story-flag READER that is one half of a door↔door shortcut:
    /// open iff <c>GetFlag(9, <see cref="LockId"/>)</c>, latched by its partner <see cref="StoryLatchSetterType"/>
    /// door (STATIC-SCD-RE.md cont.40). Type 3 is also a reader, but its lock is set by an in-room SCD op
    /// in its own source room (free-to-cross once reached), so it is NOT latch-gated.</summary>
    public const int StoryLatchReaderType = 1;

    /// <summary><see cref="DoorType"/> of a self-latching SETTER: traversing it runs
    /// <c>SetFlag(9, <see cref="LockId"/>)</c>, permanently opening every same-lock
    /// <see cref="StoryLatchReaderType"/> door (the "open from the far side" shortcut).</summary>
    public const int StoryLatchSetterType = 2;

    /// <summary>Destination stage file index (ST1..STC → 1..12).</summary>
    public int TargetStage { get; set; }

    /// <summary>Destination room id within the target stage.</summary>
    public int TargetRoom { get; set; }

    /// <summary>Lock / gate-flag id (<c>byte[+0x27]</c>), or 0 when ungated.</summary>
    public int LockId { get; set; }

    /// <summary>Door sub-action / kind (<c>byte[+0x28]</c>); 0 = normal door. Selects the DINO.exe
    /// door handler via jump table <c>0x449384</c> (STATIC-SCD-RE.md cont.40): 0 = free; <b>1/3 =
    /// story-flag READER</b> (open iff <c>GetFlag(9, <see cref="LockId"/>)</c>, never sets); <b>2 =
    /// self-latch SETTER</b> (<c>SetFlag(9, LockId)</c> on first traverse — the "open from the far
    /// side" shortcut); 6/7/8 = Key Card ladder; 9..0xfc = item id == type; 0xfd/fe/ff = special.</summary>
    public int DoorType { get; set; }

    /// <summary>
    /// Entry pose the player arrives at in the <i>destination</i> room: position
    /// (<see cref="EntryX"/>/<see cref="EntryY"/>/<see cref="EntryZ"/>) and facing (<see cref="EntryD"/>).
    /// Populated/written when <see cref="DoorPoseLayout.IsDecoded"/> is true (CE-decoded today, plan
    /// §3.2). Should the gate ever be reverted to undecoded, these stay 0 and equal their
    /// <c>Original*</c> mirrors, so <see cref="PoseEdited"/> is false and nothing is written.
    /// </summary>
    public short EntryX { get; set; }
    /// <summary>Entry-pose Y (height) word — see <see cref="EntryX"/>.</summary>
    public short EntryY { get; set; }
    /// <summary>Entry-pose Z word — see <see cref="EntryX"/>.</summary>
    public short EntryZ { get; set; }
    /// <summary>Entry-pose facing/direction word — see <see cref="EntryX"/>.</summary>
    public short EntryD { get; set; }

    /// <summary>Raw payload, so unknown bytes survive a read→write round-trip.</summary>
    public byte[] Raw { get; set; } = Array.Empty<byte>();

    /// <summary>Destination stage as decoded from the file, before any randomizer edit.</summary>
    public int OriginalTargetStage { get; set; }
    /// <summary>Destination room as decoded from the file, before any randomizer edit.</summary>
    public int OriginalTargetRoom { get; set; }
    /// <summary>Lock id as decoded from the file, before any randomizer edit.</summary>
    public int OriginalLockId { get; set; }
    /// <summary>Entry-pose X as decoded from the file, before any randomizer edit.</summary>
    public short OriginalEntryX { get; set; }
    /// <summary>Entry-pose Y as decoded from the file, before any randomizer edit.</summary>
    public short OriginalEntryY { get; set; }
    /// <summary>Entry-pose Z as decoded from the file, before any randomizer edit.</summary>
    public short OriginalEntryZ { get; set; }
    /// <summary>Entry-pose facing as decoded from the file, before any randomizer edit.</summary>
    public short OriginalEntryD { get; set; }

    /// <summary>The destination as a <c>SSRR</c> code (<c>stage&lt;&lt;8 | room</c>).</summary>
    public int TargetCode => ((TargetStage & 0xff) << 8) | (TargetRoom & 0xff);

    /// <summary>True when this door reads a group-9 story latch that a partner door must set first
    /// (<see cref="StoryLatchReaderType"/>): passable only after some same-<see cref="LockId"/>
    /// <see cref="StoryLatchSetterType"/> door has been traversed.</summary>
    public bool GatesOnStoryLatch => DoorType == StoryLatchReaderType;

    /// <summary>True when traversing this door sets its group-9 story latch
    /// (<see cref="StoryLatchSetterType"/>), opening every same-<see cref="LockId"/> reader.</summary>
    public bool SetsStoryLatch => DoorType == StoryLatchSetterType;

    /// <summary>
    /// Byte offset of this record's opcode within the decompressed RDT buffer, so an edited
    /// destination / lock can be patched in place by <see cref="RoomScript.ApplyDoorEdits"/>.
    /// -1 when the record was not located positionally.
    /// </summary>
    public int FileOffset { get; set; } = -1;

    /// <summary>True once the carried entry pose differs from the decoded original. Always false
    /// while <see cref="DoorPoseLayout.IsDecoded"/> is false (pose stays at its 0 = original).</summary>
    public bool PoseEdited => EntryX != OriginalEntryX || EntryY != OriginalEntryY
        || EntryZ != OriginalEntryZ || EntryD != OriginalEntryD;

    /// <summary>True once the destination, lock, or entry pose has been changed from the original.</summary>
    public bool IsEdited => TargetStage != OriginalTargetStage
        || TargetRoom != OriginalTargetRoom || LockId != OriginalLockId || PoseEdited;
}

/// <summary>A pickup placed in a room (item id + quantity).</summary>
/// <remarks>
/// Item placements live in the decompressed RDT buffer's SCD script (<see cref="RoomScript"/>)
/// as opcode <see cref="Opcode"/>=0x28 in its subtype-4 form (<c>record[2]==4</c>, total length
/// <see cref="Length"/>=44). The item id is the low byte of the word at <see cref="IdOffset"/>
/// (high byte is normally 0) and the count is the word at <see cref="CountOffset"/>. Proven from
/// the <c>0x28</c> handler in <c>DINO.exe</c> and validated against the <c>placements.md</c>
/// oracle + a full-corpus scan (see <c>docs/reference/dc1/_registries/STATIC-SCD-RE.md</c>, 2026-06-19 cont.9). An id of
/// <c>0xFF</c> is an empty / runtime-armed slot — not a fixed pickup.
/// </remarks>
public sealed class ItemRecord
{
    /// <summary>Opcode that introduces an item-placement record in the room script.</summary>
    public const byte Opcode = DcOpcodes.Item;
    /// <summary>Total bytes of an item (0x28 subtype-4) record.</summary>
    public const int Length = DcOpcodes.ItemLength;
    /// <summary>Byte offset of the item-id word within the record (id = low byte).</summary>
    public const int IdOffset = 0x1c;
    /// <summary>Byte offset of the count word within the record.</summary>
    public const int CountOffset = 0x1e;
    /// <summary>Item id used for an empty / runtime-armed placement slot (never a real pickup).</summary>
    public const byte EmptySlotId = 0xFF;

    public int ItemId { get; set; }
    public int Amount { get; set; }
    public byte[] Raw { get; set; } = Array.Empty<byte>();

    /// <summary>The id as decoded from the file, before any randomizer edit. Drives change
    /// detection (so an unedited room round-trips byte-exact) and the empty-slot check.</summary>
    public int OriginalItemId { get; set; }

    /// <summary>True for an empty / runtime-armed slot (id 0xFF) — the randomizer leaves these alone.</summary>
    public bool IsEmptySlot => OriginalItemId == EmptySlotId;

    /// <summary>
    /// Byte offset of this record's opcode within the decompressed RDT buffer, so an edited
    /// <see cref="ItemId"/> can be patched in place by <see cref="RoomScript.ApplyEdits"/>.
    /// -1 when the record was not located positionally.
    /// </summary>
    public int FileOffset { get; set; } = -1;
}

/// <summary>An enemy spawn placed by SCD opcode <c>0x20</c> (the <c>SCE_EM_SET</c> analog).</summary>
/// <remarks>
/// The species of a Dino Crisis enemy is <b>not</b> a small id byte in the record (unlike the
/// item surface); it is bound to the EMD <b>model resource</b> the entity loads, referenced by
/// the PSX pointer at <see cref="ModelOffset"/> (its paired motion/animation resource is at
/// <see cref="MotionOffset"/>). Proven from the <c>0x20</c> handler (<c>0x426370</c>) in
/// <c>DINO.exe</c>: it relocates those two <c>0x8010xxxx</c> pointers and calls model-setup
/// <c>0x416b79</c>, which is what installs the species discriminator (<c>word[entity+0x3c]</c>).
/// See <c>docs/reference/dc1/_registries/STATIC-SCD-RE.md</c> cont.10/11 and memory <c>scd-enemy-opcode-found</c>.
///
/// <para>So the editable, in-room-safe surface is the <b>(model, motion) pointer pair</b>: both
/// targets are already loaded in the room (they back its existing enemies), so permuting the pair
/// among the room's same-<see cref="Category"/> records is guaranteed-loaded and round-trips.
/// <see cref="Category"/> (<c>record[2]</c>) is the entity's coarse AI class — the handler
/// dispatches per-category init through <c>[[0x6de990]+cat*4]</c> — so a swap is only valid
/// between records sharing it.</para>
/// </remarks>
public sealed class EnemyRecord
{
    /// <summary>Total bytes of an enemy (<c>0x20</c>) record, opcode byte included.</summary>
    public const int Length = DcOpcodes.EnemyLength;
    /// <summary>Byte offset of the large-entity array slot index (same for <c>0x20</c> and <c>0x59</c>).</summary>
    public const int SlotOffset = 0x01;
    /// <summary>Byte offset of the AI/class category (same for <c>0x20</c> and <c>0x59</c>; only
    /// same-category swaps are valid).</summary>
    public const int CategoryOffset = 0x02;
    /// <summary>Byte offset of the "already killed" GetFlag(group 4) id (<c>0x20</c> only; a
    /// <c>0x59</c> record's kill-flag lives in its instance-table entry).</summary>
    public const int KillFlagOffset = 0x04;
    /// <summary>Byte offset of the per-entity <b>AI parameter</b> byte in a <c>0x20</c> record — copied to
    /// entity <c>+0x2F</c> and consumed across every category bank (cat-2 birth derives
    /// <c>obj+0x18E</c> bit 5 from <c>+3 % 3</c>). 0 = the corpus norm / proven-neutral value.
    /// STATIC-SCD-RE cont.51.</summary>
    public const int AiParamOffset = 0x03;
    /// <summary>Byte offset of the <b>birth-behavior bitfield</b> in a <c>0x20</c> record — copied to
    /// entity <c>+0x18E</c>; its low 2 bits select the initial behavior code <c>+0x3D</c> at the cat-2
    /// birth (<c>0x4C10A2–C8</c>: mode 0 → 1, mode 1 → 0x19, else 0x1A). 0 = default behavior.
    /// STATIC-SCD-RE cont.51.</summary>
    public const int BirthModeOffset = 0x05;
    /// <summary>Byte offset of the preset <b>maxHP</b> word in a <c>0x20</c> record. The handler copies
    /// it into entity <c>+0x11A</c> (<c>DINO.exe 0x42656A</c>); <c>0</c> ⇒ the per-category birth-init
    /// random-rolls <c>{750,850,1000}</c>, nonzero ⇒ that value is kept (DC1-G2, live-confirmed
    /// 2026-07-08). <b>Not meaningful for a <c>0x59</c> record</b>, where <c>+6</c> falls inside the
    /// model pointer (<see cref="Enemy2ModelOffset"/>=0x04).</summary>
    public const int MaxHpOffset = 0x06;
    /// <summary>Byte offset of the model-resource PSX pointer (the species) in a <c>0x20</c> record.</summary>
    public const int ModelOffset = 0x10;
    /// <summary>Byte offset of the motion/animation-resource PSX pointer in a <c>0x20</c> record.</summary>
    public const int MotionOffset = 0x14;
    /// <summary>Byte offset of the model pointer in a <c>0x59</c> (<see cref="DcOpcodes.Enemy2"/>)
    /// record; differs from the <c>0x20</c> <see cref="ModelOffset"/>.</summary>
    public const int Enemy2ModelOffset = 0x04;
    /// <summary>Byte offset of the motion pointer in a <c>0x59</c> record.</summary>
    public const int Enemy2MotionOffset = 0x08;
    /// <summary>Byte offset of the secondary instance-table index in a <c>0x59</c> record
    /// (selects the position + kill-flag entry resolved at load).</summary>
    public const int Enemy2IndexOffset = 0x10;

    /// <summary>The placement opcode this record was parsed from — <see cref="DcOpcodes.Enemy"/>
    /// (<c>0x20</c>, the default) or <see cref="DcOpcodes.Enemy2"/> (<c>0x59</c>). Determines the
    /// model/motion byte offsets (<see cref="ModelFieldOffset"/>/<see cref="MotionFieldOffset"/>) so
    /// edits patch back at the right place.</summary>
    public byte Opcode { get; set; } = DcOpcodes.Enemy;

    /// <summary>Model-pointer byte offset within this record, selected by <see cref="Opcode"/>.</summary>
    public int ModelFieldOffset => Opcode == DcOpcodes.Enemy2 ? Enemy2ModelOffset : ModelOffset;
    /// <summary>Motion-pointer byte offset within this record, selected by <see cref="Opcode"/>.</summary>
    public int MotionFieldOffset => Opcode == DcOpcodes.Enemy2 ? Enemy2MotionOffset : MotionOffset;

    /// <summary>For a <see cref="DcOpcodes.Enemy2"/> (<c>0x59</c>) record, the secondary instance-table
    /// index (<c>record+0x10</c>) that supplies position + kill-flag at load; 0 for a <c>0x20</c>
    /// record, which carries those inline.</summary>
    public byte InstanceIndex { get; set; }
    /// <summary>Byte offset of the spawn X coordinate (word). Copied to entity <c>+0x20</c>.</summary>
    public const int PosXOffset = 0x08;
    /// <summary>Byte offset of the spawn Y coordinate (word, height). Copied to entity <c>+0x22</c>.</summary>
    public const int PosYOffset = 0x0a;
    /// <summary>Byte offset of the spawn Z coordinate (word). Copied to entity <c>+0x24</c>.</summary>
    public const int PosZOffset = 0x0c;
    /// <summary>Byte offset of the spawn Y-axis rotation (word). Copied to entity <c>+0x2a</c>.</summary>
    public const int RotationOffset = 0x0e;

    /// <summary>Large-entity array slot (<c>record[1]</c>); may be reused across subroutines.</summary>
    public byte Slot { get; set; }

    /// <summary>Spawn X coordinate (signed word, <c>rec+8</c>), decoded for cross-room placement.</summary>
    public short PosX { get; set; }
    /// <summary>Spawn Y coordinate / height (signed word, <c>rec+0xa</c>).</summary>
    public short PosY { get; set; }
    /// <summary>Spawn Z coordinate (signed word, <c>rec+0xc</c>).</summary>
    public short PosZ { get; set; }
    /// <summary>Spawn Y-axis rotation (signed word, <c>rec+0xe</c>).</summary>
    public short Rotation { get; set; }
    /// <summary>Coarse AI class (<c>record[2]</c>); swaps stay within one category.</summary>
    public byte Category { get; set; }
    /// <summary>Kill-tracking flag id (<c>record[4]</c>).</summary>
    public byte KillFlag { get; set; }

    /// <summary>File-form model-resource pointer (<c>0x8010xxxx</c>) — the swap target.</summary>
    public uint ModelPtr { get; set; }
    /// <summary>File-form motion-resource pointer — swapped together with <see cref="ModelPtr"/>.</summary>
    public uint MotionPtr { get; set; }

    /// <summary>Model pointer as decoded from the file, before any randomizer edit.</summary>
    public uint OriginalModelPtr { get; set; }
    /// <summary>Motion pointer as decoded from the file, before any randomizer edit.</summary>
    public uint OriginalMotionPtr { get; set; }

    /// <summary>Preset maxHP for a <c>0x20</c> spawn (word at <see cref="MaxHpOffset"/>). A nonzero value
    /// bypasses the engine's <c>{750,850,1000}</c> birth roll; <c>0</c> (the vanilla state — all 83 clean
    /// records zero it) keeps the roll. Always <c>0</c> for a <c>0x59</c> record (its <c>+6</c> is
    /// model-pointer bytes), which the HP pass never edits. <see cref="Passes"/> writes this; the permute
    /// pass leaves it untouched. DC1-G2, EXE-SYMBOLS <c>0x42656A</c> / entity <c>+0x11A</c>.</summary>
    public ushort MaxHp { get; set; }

    /// <summary>MaxHP word as decoded from the file, before any randomizer edit (drives change detection so
    /// a model-only permute leaves <c>+6</c> byte-exact).</summary>
    public ushort OriginalMaxHp { get; set; }

    /// <summary>
    /// Bone count of the model's skeleton, read from the embedded model block
    /// (<see cref="EnemySkeleton.ReadBoneCount"/>); 0 if it did not decode. The bone count is a
    /// stable per-species invariant — see <see cref="Species"/>.
    /// </summary>
    public int SpeciesBoneCount { get; set; }

    /// <summary>
    /// Species class recovered from the model skeleton (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.14), independent
    /// of the script's <see cref="Category"/> byte. Empirically the two always agree
    /// (<see cref="SpeciesMatchesCategory"/>), giving a self-checking decode.
    /// </summary>
    public DinoSpecies Species => EnemySkeleton.FromBoneCount(SpeciesBoneCount);

    /// <summary>
    /// True when the model-derived <see cref="Species"/> agrees with the species implied by the script
    /// <see cref="Category"/> byte (<see cref="EnemySkeleton.SpeciesForCategory"/>) — a self-check from
    /// two independent witnesses. Species and category are <b>not</b> the same number (categories 3 and 4
    /// are both the Tyrannosaurus; docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.23), so this compares decoded species, not
    /// the raw byte. False when the model does not decode (Species == Unknown).
    /// </summary>
    public bool SpeciesMatchesCategory =>
        Species != DinoSpecies.Unknown && Species == EnemySkeleton.SpeciesForCategory(Category);

    /// <summary>
    /// True when this entity's model is a recognised dinosaur class — its skeleton decodes to a
    /// known <see cref="DinoSpecies"/> <b>and</b> that species agrees with the AI
    /// <see cref="Category"/>. The <see cref="EnemyRandomizer"/> only ever permutes entities for
    /// which this holds, so a <c>0x20</c> record whose model does <i>not</i> decode as a dinosaur is
    /// never edited.
    ///
    /// <para><b>What this does and does NOT catch (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.15, corrected cont.41).</b>
    /// <c>0x20</c> is the general entity opcode; it also places non-dinosaurs that <i>share the 15-bone
    /// biped rig</i>, decoding as a 15-bone cat-1 "Velociraptor" because the rig is identical: the
    /// humanoid corpse in <c>st50c</c>, and — <b>refuting cont.15's "NPCs are never <c>0x20</c>
    /// entities"</b> — the live NPC characters (Rick/Gail/Kirk) placed in scene-hub rooms such as
    /// <c>0106</c> (PS1-confirmed on <c>ST106.DAT</c>, cont.41). Bone count / AI category / bind-pose
    /// <b>cannot</b> separate any of these from a real raptor (the NPC placements even carry a vestigial
    /// <i>raptor</i> skeleton — the real character model is loaded into the heap by the cutscene system
    /// at runtime). So this flag is a sanity net against garbage/undecodable models, not an NPC detector.
    /// NPC-scene actors are excluded instead by <see cref="IsNpcSceneActor"/> (the shared-motion /
    /// distinct-model tell), folded in below, plus the singleton rule and
    /// <see cref="DinoRand.Randomizer.Definitions.GameDefinition.CutsceneRoomCodes"/>.</para>
    ///
    /// <para><b>The Tyrannosaurus is excluded (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.23).</b> Both T-Rex model
    /// classes (20-bone cat-3 boss, 10-bone cat-4 Chief's-Room rig) are hand-placed scripted set-pieces,
    /// never a randomizable population — so they are filtered here regardless of category, decoupling the
    /// "is a known dinosaur" check from the per-AI-category permute eligibility.</para>
    /// </summary>
    public bool IsRandomizableDino =>
        SpeciesMatchesCategory && Species != DinoSpecies.Tyrannosaurus && !IsNpcSceneActor;

    /// <summary>
    /// True when this species' birth-init <b>keeps</b> a nonzero preset <see cref="MaxHp"/> — i.e. the
    /// <c>+6</c> HP override actually takes effect in-game. Per-category, from the PC birth handlers
    /// (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.48): cat-2 RaptorHeavy (<c>0x4C0FED</c>:
    /// <c>test maxHP; jne keep</c> at <c>0x4C10D2</c>, else the PSX-standard roll <c>0x5E5ECC</c>) and
    /// cat-7 Pteranodon (<c>0x599CDC</c>, guard <c>0x599D92</c>) kept the PSX keep-if-nonzero idiom.
    /// cat-1 Velociraptor (<c>0x4DBB3D</c>, stores at <c>0x4DBB7F/0x4DBB8B</c>) and cat-5 Swarm
    /// (<c>0x4F83D5</c> family) write <c>1000/1000</c> <b>unconditionally</b> — a preset is clobbered at
    /// birth, so writing <c>+6</c> for them is a silent no-op the HP pass must not claim.
    /// </summary>
    public bool IsHpPresettable =>
        Species is DinoSpecies.RaptorHeavy or DinoSpecies.Pteranodon;

    /// <summary>
    /// True when this <c>0x20</c> record is an <b>NPC-scene actor</b> (Rick/Gail/Kirk placed in a scene
    /// hub), not a randomizable dinosaur — set by <see cref="MarkNpcSceneActors"/> at parse time
    /// (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.41). Such records carry a <i>vestigial raptor skeleton</i> in the RDT
    /// (byte-identical raptor bind-pose — the cutscene system loads the real character model into the
    /// heap at runtime and the file <see cref="MaxHp"/> is ignored), so bone count / AI
    /// <see cref="Category"/> / bind-pose all mis-decode them as a Velociraptor.
    /// </summary>
    public bool IsNpcSceneActor { get; set; }

    /// <summary>
    /// Mark the NPC-scene actors among a single room's enemy records. Rule (cont.41): among the
    /// <c>0x20</c> records, a <b>motion pointer shared by ≥2 distinct model pointers</b> is a shared
    /// biped motion set driving two characters — every record on such a motion is an
    /// <see cref="IsNpcSceneActor"/>. Proven file-static and PS1-stable: a full-corpus scan finds this
    /// pattern in exactly six oracle-confirmed scene rooms (0106/030C/050E/050F/0609/0612). Two are pure
    /// NPC rooms (0106 "Control Room 1F", live-CE Rick/Gail; 0612); the other four are <b>mixed</b> — the
    /// flagged pair is the NPC duo (Rick/Gail/Kirk), coexisting with genuine raptors that keep their
    /// <i>own</i> motion and stay randomizable, so the exclusion is surgical, not per-room. Genuine
    /// multi-raptor spawns never trip the rule: identical raptors share <i>both</i> pointers (one distinct
    /// model), and distinct raptors each get their own motion. Bone count / bind-pose cannot tell an NPC
    /// actor from a raptor (the NPC placement even embeds a raptor skeleton), so this relational check is
    /// the only reliable static discriminator.
    /// </summary>
    public static void MarkNpcSceneActors(IReadOnlyList<EnemyRecord> roomEnemies)
    {
        var distinctModelsByMotion = new Dictionary<uint, HashSet<uint>>();
        foreach (var e in roomEnemies)
        {
            if (e.Opcode != DcOpcodes.Enemy) continue; // 0x59 offsets differ; NPC scenes use 0x20
            if (!distinctModelsByMotion.TryGetValue(e.OriginalMotionPtr, out var models))
                distinctModelsByMotion[e.OriginalMotionPtr] = models = new HashSet<uint>();
            models.Add(e.OriginalModelPtr);
        }
        foreach (var e in roomEnemies)
        {
            if (e.Opcode != DcOpcodes.Enemy) continue;
            if (distinctModelsByMotion.TryGetValue(e.OriginalMotionPtr, out var models) && models.Count >= 2)
                e.IsNpcSceneActor = true;
        }
    }

    public byte[] Raw { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Byte offset of this record's opcode within the decompressed RDT buffer, so an edited
    /// pointer pair can be patched in place by <see cref="RoomScript.ApplyEnemyEdits"/>.
    /// -1 when the record was not located positionally.
    /// </summary>
    public int FileOffset { get; set; } = -1;

    /// <summary>True once the model/motion pointer or the preset maxHP has been changed from the original.</summary>
    public bool IsEdited => ModelPtr != OriginalModelPtr || MotionPtr != OriginalMotionPtr
        || MaxHp != OriginalMaxHp;
}
