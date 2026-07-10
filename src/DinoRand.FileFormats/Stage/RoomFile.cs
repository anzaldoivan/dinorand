using System.Buffers.Binary;
using DinoRand.FileFormats.Compression;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// Optional authored fields for an injected <c>0x20</c> enemy record — everything defaults to the
/// zero template proven neutral by cont.51 ("zeros ⇒ mode 0 ⇒ the default behavior") — plus the
/// optional op-<c>0x22</c>/op-<c>0x3a</c> activation-pair emission (cont.49). The CE-gate labs
/// (<c>--add-enemy</c>/<c>--add-enemy-at</c>) drive these; nothing in the bulk randomizer sets them.
/// </summary>
public readonly record struct EnemyAuthoring
{
    /// <summary>Preset maxHP word (record <c>+6</c> → entity <c>+0x11A</c> at <c>0x42656A</c>).
    /// 0 = keep the birth roll. Only cat-2/cat-7 births keep a preset (<see cref="EnemyRecord.IsHpPresettable"/>,
    /// cont.48).</summary>
    public ushort MaxHp { get; init; }

    /// <summary>Per-entity AI parameter (record <c>+3</c> → entity <c>+0x2F</c>, cont.51). 0 = corpus norm.</summary>
    public byte AiParam { get; init; }

    /// <summary>Birth-behavior bitfield (record <c>+5</c> → entity <c>+0x18E</c>; low 2 bits select the
    /// initial behavior code <c>+0x3D</c> — cat-2: 0→1, 1→0x19, else 0x1A; cont.51). 0 = default.</summary>
    public byte BirthMode { get; init; }

    /// <summary>When non-null, emit the activation pair right after the injected record: op <c>0x22</c>
    /// binding the record's slot + op <c>0x3a</c> installing this behavior code (top state 4 —
    /// cont.49). Required for a cat-1 to hunt from an init placement; cat-2 self-activates without it.</summary>
    public byte? ActivateBehavior { get; init; }

    /// <summary>The emitted op <c>0x3a</c>'s 8 operand bytes. Null = copy the first native <c>0x3a</c>'s
    /// blob in the target room, falling back to zeros (a retail-legal value — st102 sub6, cont.52).</summary>
    public byte[]? ActivateBlob { get; init; }

    /// <summary>The emitted op <c>0x3a</c>'s byte <c>[3]</c> (semantics unmapped; retail behavior-1
    /// installs carry 0x1F/0x20/0x21 there, behavior-5 installs carry 0 — st10e/st102). Null = copy it
    /// from the same native <c>0x3a</c> the blob comes from (0 when the blob is explicit or absent).</summary>
    public byte? ActivateB3 { get; init; }
}

/// <summary>
/// One Dino Crisis 1 room — physically one <c>stNXX.dat</c> file (N = stage 1–9/A/B/C,
/// XX = room number in hex), e.g. <c>st10a.dat</c> = stage&#160;1, room&#160;0x0A.
/// This is the DC analogue of Resident Evil's per-room RDT, so the engine treats it the
/// same way: a chunked container whose last entry holds the room's RDT (camera, collision,
/// the SCD script, …) from which items / doors / enemies are read.
///
/// <para>The file is a "Gian package" (<see cref="GianPackage"/>). The room RDT is the
/// package's <b>last entry</b>; for most rooms it is LZSS-compressed (type
/// <see cref="GianEntryType.Unknown"/>), for a few it is stored raw
/// (<see cref="GianEntryType.Data"/>). We decompress it to <see cref="RdtBuffer"/> and walk
/// the SCD script (<see cref="RoomScript"/>) to find item records.</para>
///
/// <para><b>Round-trip.</b> <see cref="Write"/> is byte-exact when no item id was edited.
/// When ids change, the edited RDT buffer is re-emitted (re-compressed for type-7 entries)
/// and the (last) entry's size in the header is updated; because it is the last entry, no
/// other entry's offset shifts.</para>
/// </summary>
public sealed class RoomFile
{
    public RoomFile(int stage, int room)
    {
        Stage = stage;
        Room = room;
    }

    /// <summary>Stage number 1–12 (12 stages: 1–9 then A=10, B=11, C=12).</summary>
    public int Stage { get; }

    /// <summary>Room id within the stage (the XX in stNXX.dat).</summary>
    public int Room { get; }

    /// <summary>Doors / area transitions parsed from the room's script segment.</summary>
    public List<DoorRecord> Doors { get; } = new();

    /// <summary>Item pickups placed in the room.</summary>
    public List<ItemRecord> Items { get; } = new();

    /// <summary>Enemy spawns in the room.</summary>
    public List<EnemyRecord> Enemies { get; } = new();

    /// <summary>Raw file bytes as read.</summary>
    public byte[] OriginalBytes { get; private set; } = Array.Empty<byte>();

    /// <summary>Parsed package container, or null if the file isn't a recognized Gian package.</summary>
    public GianPackage? Package { get; private set; }

    /// <summary>The decompressed room RDT buffer (the last entry's payload, LZSS-expanded), or empty.</summary>
    public byte[] RdtBuffer { get; private set; } = Array.Empty<byte>();

    /// <summary>True if the last entry was LZSS-compressed (so <see cref="Write"/> re-compresses).</summary>
    public bool RdtCompressed { get; private set; }

    /// <summary>The walked SCD script, or null if the file isn't a recognized package.</summary>
    public RoomScript? Script { get; private set; }

    /// <summary>
    /// True once a structural edit grew <see cref="RdtBuffer"/> (e.g. <see cref="AddEnemy"/> injected a
    /// new record), so <see cref="Write"/> must re-emit even though no existing record's id/pointer
    /// changed. (<see cref="ImportSpecies"/> instead marks an existing record edited, so it does not
    /// need this.)
    /// </summary>
    private bool _structurallyEdited;

    /// <summary>True when the SCD script walked cleanly (writes are only applied then).</summary>
    public bool ParsedCleanly => Script?.ParsedCleanly ?? false;

    /// <summary>Whether a package entry type stores its payload LZSS-compressed.</summary>
    private static bool IsCompressed(GianEntryType type) => type switch
    {
        GianEntryType.Lzss0 or GianEntryType.Lzss1 or GianEntryType.Lzss2
            or GianEntryType.Unknown => true,
        _ => false,
    };

    public static RoomFile Read(int stage, int room, ReadOnlySpan<byte> bytes)
    {
        var rf = new RoomFile(stage, room) { OriginalBytes = bytes.ToArray() };
        rf.Package = GianPackage.TryParse(bytes);
        if (rf.Package?.RoomDataEntry is { } rdt)
        {
            var payload = bytes.Slice(rdt.PayloadOffset, (int)rdt.DeclaredSize);
            rf.RdtCompressed = IsCompressed(rdt.Type);
            rf.RdtBuffer = rf.RdtCompressed ? Lzss.Decompress(payload) : payload.ToArray();

            rf.Script = RoomScript.Parse(rf.RdtBuffer);
            if (rf.Script.ParsedCleanly && DcOpcodes.IsTrustworthy)
            {
                rf.Items.AddRange(rf.Script.Items);
                rf.Enemies.AddRange(rf.Script.Enemies);
                rf.Doors.AddRange(rf.Script.Doors);
            }
        }
        return rf;
    }

    /// <summary>
    /// Cross-room species import (docs/decisions/dc1/enemies/CROSS-ROOM-SPECIES-PLAN.md, increment 1: geometry only).
    /// Appends the <paramref name="donor"/>'s model + motion blocks to <see cref="RdtBuffer"/> (growing
    /// it) and repoints the enemy record at <paramref name="enemyIndex"/> to the imported model/motion
    /// and AI category. <see cref="Write"/> then re-emits the grown, repointed RDT (the last package
    /// entry already grows safely). The <c>0x20</c> handler loads the model straight from the record
    /// pointer — no op23 registration is required (verified statically; the enemy path never touches the
    /// op23 scenery array). Textures are not imported (the species may render mis-coloured but, geometry
    /// being valid, should not crash — increment 2 adds textures).
    /// </summary>
    public void ImportSpecies(SpeciesDonor donor, int enemyIndex)
    {
        if (enemyIndex < 0 || enemyIndex >= Enemies.Count)
            throw new ArgumentOutOfRangeException(nameof(enemyIndex));

        var result = SpeciesImporter.Import(RdtBuffer, donor.Model, donor.Motion);
        RdtBuffer = result.Rdt;

        var e = Enemies[enemyIndex];
        e.ModelPtr = result.ModelPtr;
        e.MotionPtr = result.MotionPtr;
        e.Category = donor.Category;
        e.SpeciesBoneCount = EnemySkeleton.ReadBoneCount(donor.Model.Bytes, SpeciesImporter.PsxBase);
    }

    /// <summary>
    /// Cross-room import of an <i>entangled</i> species (the Therizinosaurus) whose resource is a
    /// closure-extracted <see cref="SpeciesDonorMulti"/> rather than two independently-closable blocks
    /// (docs/decisions/dc1/theri/THERI-BACKWARD-CLOSURE-PLAN.md). Compact-appends the donor's range set to
    /// <see cref="RdtBuffer"/> (relocating its piecewise pointer map) and repoints the enemy record at
    /// <paramref name="enemyIndex"/> to the imported model/motion + AI category, exactly as the single-
    /// range <see cref="ImportSpecies(SpeciesDonor,int)"/>. <see cref="Write"/> re-emits via the grow-
    /// last-entry path. Geometry only (no textures); animation correctness is a later CE gate.
    /// </summary>
    public void ImportSpecies(SpeciesDonorMulti donor, int enemyIndex)
    {
        if (enemyIndex < 0 || enemyIndex >= Enemies.Count)
            throw new ArgumentOutOfRangeException(nameof(enemyIndex));

        var result = SpeciesImporter.ImportRangeSet(RdtBuffer, donor.Blocks);
        RdtBuffer = result.Rdt;

        var e = Enemies[enemyIndex];
        e.ModelPtr = result.ModelPtr;
        e.MotionPtr = result.MotionPtr;
        e.Category = donor.Category;
        e.SpeciesBoneCount = EnemySkeleton.ReadBoneCount(RdtBuffer, result.ModelPtr);
    }

    /// <summary>
    /// <b>⚠ DORMANT — DOES NOT WORK IN-ENGINE.</b> Overlay-in-place was CE-tested and <b>crashes on room
    /// load</b>: the room references the swapped-out enemy's RDT region by the original species' byte layout,
    /// which an imported species cannot satisfy (docs/decisions/dc1/theri/THERI-RDT-MEMORY-LIBERATION-PLAN.md, CE gate
    /// 2026-06-22). Kept only as static-verified placement math; <b>do not</b> wire it into a real swap —
    /// append (<see cref="ImportSpecies(SpeciesDonorMulti,int)"/>) is the only working placement.
    ///
    /// Place an entangled-species donor <b>into the swapped-out enemy's own RDT region</b> instead of
    /// appending it (overlay-in-place, docs/decisions/dc1/theri/THERI-RDT-MEMORY-LIBERATION-PLAN.md) — so an over-ceiling
    /// room does not grow past the engine room buffer (<see cref="SpeciesImporter.EngineRoomRdtCeiling"/>).
    /// Overwrites the victim enemy's resource span (<see cref="SpeciesImporter.OverlayRegion"/>) with the
    /// relocated donor (<see cref="SpeciesImporter.PlaceRangeSetAt"/>) and repoints the record at
    /// <paramref name="enemyIndex"/> — exactly the repoint <see cref="ImportSpecies(SpeciesDonorMulti,int)"/>
    /// does, but with the buffer length unchanged when the donor fits the region.
    ///
    /// <para><b>Throws <see cref="InvalidOperationException"/></b> when the victim's geometry is shared by
    /// another placement (the region is still live), when the victim resource is not a single contiguous
    /// region, or when the donor does not fit it — the caller then keeps the headroom-guard failure. The
    /// room's other references to the old region resolving to valid replacement bytes is the documented
    /// CE gate (never freed memory; the same stale-data situation the shipped append-swap tolerates).</para>
    /// </summary>
    public void OverlaySpecies(SpeciesDonorMulti donor, int enemyIndex)
    {
        if (enemyIndex < 0 || enemyIndex >= Enemies.Count)
            throw new ArgumentOutOfRangeException(nameof(enemyIndex));
        var victim = Enemies[enemyIndex];
        const uint psx = SpeciesImporter.PsxBase;

        var otherHeads = new List<int>();
        for (int i = 0; i < Enemies.Count; i++)
        {
            if (i == enemyIndex) continue;
            var e = Enemies[i];
            if (e.OriginalModelPtr == victim.OriginalModelPtr || e.OriginalMotionPtr == victim.OriginalMotionPtr)
                throw new InvalidOperationException(
                    "victim geometry is shared by another placement; overlay would corrupt the survivor");
            if (e.OriginalModelPtr >= psx) otherHeads.Add((int)(e.OriginalModelPtr - psx));
            if (e.OriginalMotionPtr >= psx) otherHeads.Add((int)(e.OriginalMotionPtr - psx));
        }

        int modelHead = (int)(victim.OriginalModelPtr - psx);
        int motionHead = (int)(victim.OriginalMotionPtr - psx);
        var region = SpeciesImporter.OverlayRegion(RdtBuffer, modelHead, motionHead, otherHeads)
            ?? throw new InvalidOperationException(
                "victim resource is not a single contiguous region (model/motion interleaved with live data); cannot overlay");

        // Reuse the hole for as much of the donor as fits at a tear-free boundary; append the overflow.
        // When the donor fits the hole this does not grow the buffer; otherwise it grows by the remainder
        // (the caller's headroom guard then accepts/rejects the result against the engine ceiling).
        var result = SpeciesImporter.PlaceRangeSetSplit(RdtBuffer, donor.Blocks, region.Lo, region.Hi - region.Lo);
        RdtBuffer = result.Rdt;

        victim.ModelPtr = result.ModelPtr;
        victim.MotionPtr = result.MotionPtr;
        victim.Category = donor.Category;
        victim.SpeciesBoneCount = EnemySkeleton.ReadBoneCount(RdtBuffer, result.ModelPtr);
    }

    /// <summary>The texture outcome of a <see cref="ImportSpeciesTextured(SpeciesDonor,int)"/> call —
    /// surfaced so callers can log which path was taken.</summary>
    public enum TextureImportOutcome
    {
        /// <summary>Donor carried no texture; geometry-only import (may render mis-coloured).</summary>
        GeometryOnly,
        /// <summary>The donor texture was relocated to a free VRAM region, the model's codes rewritten,
        /// and the texture + palette injected as new package entries.</summary>
        Relocated,
        /// <summary>No texture work: the injected enemy reused a model the room already loads, so its
        /// texture is already resident in VRAM (the renderable path for same-species event injection;
        /// ADD-ENEMY-EVENT-INJECTION-CRASH-RCA.md).</summary>
        Reused,
    }

    /// <summary>Result of a textured import: the <see cref="Outcome"/> and (when relocated) the
    /// texture/palette VRAM rects placed. The final package bytes come from <see cref="Write"/>, which
    /// emits any staged texture entries — so callers import then call <see cref="Write"/> as usual.</summary>
    public sealed record TexturedImportResult(TextureImportOutcome Outcome,
                                              VramRect? TextureRect, VramRect? PaletteRect);

    /// <summary>Texture + palette entries staged by a textured import, injected before the RDT by
    /// <see cref="Write"/> (so a normal Write re-emits them — the seed pipeline needs no special path).</summary>
    private List<PackageRepacker.NewEntry>? _textureInserts;

    /// <summary>
    /// Texture-aware counterpart of <see cref="ImportSpecies(SpeciesDonor,int)"/>: imports geometry,
    /// then — if <see cref="SpeciesDonor.Texture"/> is present — relocates the donor texture to a free
    /// VRAM region of this room (<see cref="TextureImporter.PickFreeRegion"/>), rewrites the imported
    /// model's primitive tpage/CLUT codes to the relocated coords
    /// (<see cref="TextureImporter.RewriteModelCodes"/>), and <b>stages</b> the relocated texture +
    /// palette as new package entries that <see cref="Write"/> injects. Falls back to a geometry-only
    /// import when there is no donor texture or no free VRAM. docs/reference/dc1/textures/TEXTURE-IMPORT-VRAM.md §(d)(ii).
    /// </summary>
    public TexturedImportResult ImportSpeciesTextured(SpeciesDonor donor, int enemyIndex)
    {
        ImportSpecies(donor, enemyIndex);
        return StageTexture(donor.Texture, Enemies[enemyIndex].ModelPtr);
    }

    /// <summary>Multi-range (entangled species) sibling of
    /// <see cref="ImportSpeciesTextured(SpeciesDonor,int)"/>.</summary>
    public TexturedImportResult ImportSpeciesTextured(SpeciesDonorMulti donor, int enemyIndex)
    {
        ImportSpecies(donor, enemyIndex);
        return StageTexture(donor.Texture, Enemies[enemyIndex].ModelPtr);
    }

    private TexturedImportResult StageTexture(TextureBlock? texture, uint importedModelPtr)
    {
        if (texture is null) return new TexturedImportResult(TextureImportOutcome.GeometryOnly, null, null);

        TextureBlock placed;
        try { placed = TextureImporter.PickFreeRegion(OriginalBytes, texture); }
        catch (InvalidOperationException) { return new TexturedImportResult(TextureImportOutcome.GeometryOnly, null, null); }

        // Rewrite the imported model's baked codes (donor coords) to the relocated coords; Write() then
        // emits the rewritten RDT and injects the relocated texture + palette entries.
        var map = new Dictionary<ushort, ushort>(texture.TpageCodes.Count);
        for (int i = 0; i < texture.TpageCodes.Count; i++)
            map[texture.TpageCodes[i]] = placed.TpageCodes[i];
        TextureImporter.RewriteModelCodes(RdtBuffer, importedModelPtr, map, placed.ClutCode);

        _textureInserts = new List<PackageRepacker.NewEntry>
        {
            new(GianEntryType.Lzss2, placed.Texture.Dst, Lzss.Compress(placed.Texture.Pixels)),
            new(GianEntryType.Palette, placed.Palette.Dst, placed.Palette.Pixels),
        };
        _structurallyEdited = true; // ensure Write() emits even if record-edit detection would skip
        return new TexturedImportResult(TextureImportOutcome.Relocated, placed.Texture.Dst, placed.Palette.Dst);
    }

    /// <summary>
    /// Place a COORDINATED GROUP of one species that shares a SINGLE imported model/motion closure. The
    /// cat-5 swarm (compy) only works when ≥N like-model members spawn together: the engine forms the
    /// pack-coordination state (global descriptor <c>0x6D5888</c>, the per-member <c>+0x154</c>/<c>+0x1C8</c>
    /// links) from the set of same-model cat-5 entities, and a lone import NULL-AVs in the swarm AI
    /// (docs/decisions/dc1/spawn/SWARM-0102-GROUP-SPAWN-PLAN.md, Architecture B; EXE-SYMBOLS <c>0x5C6ADD</c>/<c>0x5ABA34</c>).
    ///
    /// <para>Imports the donor closure + texture <b>once</b> over the victim at <paramref name="victimIndex"/>
    /// (member 0, via <see cref="ImportSpeciesTextured(SpeciesDonor,int)"/>), then injects one op-<c>0x20</c>
    /// record per entry in <paramref name="extraMembers"/>, each pointing at the <b>same</b> relocated
    /// model/motion (no re-import — the members share one closure exactly like a native swarm room, and the
    /// RDT grows by only the records, not N copies of the geometry). All injected records share one free
    /// kill-flag (native swarm rooms share a group kill-flag). The whole record blob is spliced in a single
    /// <see cref="ScriptInjector.Insert"/> so every file-form pointer (closure-internal, the victim's, and
    /// the new records') is relocated consistently. <see cref="Write"/> emits the result.</para>
    /// </summary>
    /// <returns>Member 0's texture outcome (shared by the whole group, which samples the one model).</returns>
    public TexturedImportResult ImportSpeciesGroupTextured(
        SpeciesDonor donor, int victimIndex,
        IReadOnlyList<(short X, short Y, short Z, short Rotation)> extraMembers,
        byte[]? groupSetupRecord = null)
    {
        var tex = ImportSpeciesTextured(donor, victimIndex);
        if (extraMembers is null || extraMembers.Count == 0) return tex;

        if (Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject group members");

        // Member 0 carries the relocated closure pointers + AI category — every extra member reuses them
        // (so all share ONE model/motion, the precondition for the engine to form the pack).
        var member0 = Enemies[victimIndex];
        uint modelPtr = member0.ModelPtr, motionPtr = member0.MotionPtr;
        byte category = member0.Category;
        int member0Offset = member0.FileOffset;

        // BAKE member 0's import edit (model/motion/category) into the RDT bytes now: ImportSpecies only
        // mutated the EnemyRecord object, and the Reparse below re-reads from the buffer — without this the
        // victim would revert to the original raptor. (Insert then relocates this freshly-written pointer
        // alongside the closure and the new records, all consistently.)
        Script.ApplyEnemyEdits(RdtBuffer, Enemies);

        // Slots: the next free large-entity indices (native swarm uses contiguous slots 0..3). One shared
        // free kill-flag for the added members (native swarm rooms share a group kill-flag).
        var usedSlots = new HashSet<int>(Enemies.Select(e => (int)e.Slot));
        byte killFlag = PickFreeKillFlag();

        // The blob: an OPTIONAL group-setup record (the cat-5 swarm's op58 `58 00 17 02 …`, which the engine
        // runs to ALLOCATE the shared type-0x17 coordination effect — `op58` handler 0x429AD2: alloc 0x41681F
        // then `byte[slot+1]=byte[record+2]`=0x17), followed by the N member records. The setup MUST precede
        // the members: each member's spawn runs the per-member init 0x5A6B15, which scans the effect pool
        // (`scratchpad0+0x9008`, stride 0x24) for the type-0x17 entry and links it into `entity+0x1C8`. Without
        // it the scan finds nothing, `+0x1C8` stays 0, and the swarm AI NULL-AVs at 0x5C6ADD. (Live-RE'd in
        // 040B 2026-06-24; docs/decisions/dc1/spawn/SWARM-0102-GROUP-SPAWN-PLAN.md.)
        int setupLen = groupSetupRecord?.Length ?? 0;
        var blob = new byte[setupLen + extraMembers.Count * EnemyRecord.Length];
        groupSetupRecord?.CopyTo(blob, 0);
        byte slot = 0;
        for (int i = 0; i < extraMembers.Count; i++)
        {
            while (usedSlots.Contains(slot) && slot < 0xff) slot++;
            usedSlots.Add(slot);
            var (x, y, z, rot) = extraMembers[i];
            BuildEnemyRecord(category, slot, killFlag, x, y, z, rot, modelPtr, motionPtr)
                .CopyTo(blob, setupLen + i * EnemyRecord.Length);
        }

        // Inject the blob IMMEDIATELY BEFORE the victim's record, NOT at the subroutine tail. The SCD init
        // task is cooperative: a handler returning non-zero YIELDS the task for the frame (dispatch loop
        // 0x46AB0B). The setup + members must run in the SAME uninterrupted span the engine forms the pack —
        // if even one yields before them, the already-spawned members run their AI alone and the swarm AI
        // NULL-AVs. The enemy opcodes 0x20/0x59 both return 0 (continue), so records spliced right before the
        // victim spawn in one run with it, ahead of st102's trailing op58/op05 (which can yield). See
        // docs/decisions/dc1/spawn/SWARM-0102-GROUP-SPAWN-PLAN.md.
        RdtBuffer = ScriptInjector.Insert(RdtBuffer, member0Offset, blob);
        _structurallyEdited = true;
        Reparse(); // surface the added members in Enemies
        return tex;
    }

    /// <summary>Texture-aware counterpart of <see cref="AddEnemy"/>: inject a brand-new enemy and, if
    /// the <paramref name="donor"/> carries a texture, relocate it into this room and rewrite the new
    /// model's codes (<see cref="StageTexture"/>). Returns the new record and the texture outcome;
    /// call <see cref="Write"/> for the final bytes.</summary>
    public (EnemyRecord Enemy, TexturedImportResult Texture) AddEnemyTextured(
        SpeciesDonor donor, short x, short y, short z, short rotation, byte? slot = null, byte? killFlag = null,
        EnemyAuthoring authoring = default)
    {
        var added = AddEnemy(donor, x, y, z, rotation, slot, killFlag, authoring);
        return (added, StageTexture(donor.Texture, added.ModelPtr));
    }

    /// <summary>
    /// Add a brand-new enemy to the room by injecting a fresh <c>0x20</c> record into its init
    /// subroutine — the "enemy in a safe room" capability (docs/decisions/dc1/enemies/ADD-ENEMY-PLAN.md), distinct from
    /// <see cref="ImportSpecies"/> which only repoints an <i>existing</i> placement.
    ///
    /// <para>Steps: (1) import the <paramref name="donor"/>'s model + motion (reuses
    /// <see cref="SpeciesImporter.Import"/>, growing <see cref="RdtBuffer"/>); (2) author the 24-byte
    /// record — donor AI <see cref="SpeciesDonor.Category"/>, the chosen spawn position/rotation, a free
    /// large-entity <paramref name="slot"/> and an unused <paramref name="killFlag"/> (so it is not
    /// pre-"killed"); (3) inject it into the init subroutine via <see cref="ScriptInjector"/>, which
    /// relocates the function table, file-form pointers and pc-relative branches. <see cref="Write"/>
    /// then re-emits the grown buffer through the existing last-entry grow path.</para>
    ///
    /// <para><b>Spawn position is the caller's responsibility.</b> A valid <c>(x,y,z)</c> must be a real
    /// floor point in the room (Y is the height; the corpus places grounded enemies at Y=0). Bad
    /// coordinates spawn the enemy in a wall/void — CE is the only true validator (it cannot be checked
    /// statically here).</para>
    /// </summary>
    /// <returns>The new enemy record, surfaced in <see cref="Enemies"/> at its injected offset.</returns>
    public EnemyRecord AddEnemy(SpeciesDonor donor, short x, short y, short z, short rotation,
                               byte? slot = null, byte? killFlag = null, EnemyAuthoring authoring = default)
    {
        if (Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject");

        byte chosenSlot = slot ?? PickFreeSlot();
        byte chosenKill = killFlag ?? PickFreeKillFlag();

        // 1. Import model + motion (appends to the buffer end; nothing before the append moves).
        var imp = SpeciesImporter.Import(RdtBuffer, donor.Model, donor.Motion);
        RdtBuffer = imp.Rdt;

        // 2. Choose the injection offset inside the init subroutine (subroutine 0).
        if (!ScriptInjector.TryReadFuncTable(RdtBuffer, out _, out var starts) || starts.Count == 0)
            throw new InvalidOperationException("room has no readable init subroutine");
        int o = InitInsertOffset(RdtBuffer, starts);
        if (o < 0)
            throw new InvalidOperationException("init subroutine has no interior insertion point");

        // 3. Author the record and inject it (ScriptInjector does all relocation).
        var record = BuildEnemyRecord(donor.Category, chosenSlot, chosenKill, x, y, z, rotation,
                                      imp.ModelPtr, imp.MotionPtr, authoring);
        if (authoring.ActivateBehavior is byte behavior)
        {
            var (b3, blob) = ResolveActivation(behavior, authoring);
            record = AppendActivationPair(record, chosenSlot, behavior, b3, blob);
        }
        RdtBuffer = ScriptInjector.Insert(RdtBuffer, o, record);

        _structurallyEdited = true;
        Reparse(); // refresh every record's offset and surface the new enemy
        return Enemies.First(e => e.FileOffset == o);
    }

    /// <summary>
    /// Add a brand-new enemy by injecting a fresh <c>0x20</c> record at a <b>caller-specified RDT
    /// offset</b>, instead of the auto-chosen init point used by <see cref="AddEnemy"/>. Whether the enemy
    /// hunts depends on the <b>reachability of the offset</b>, not on init-vs-event (the refined model —
    /// docs/reference/dc1/enemies/ENEMY-INJECTION-MODES.md): a site reached conditionally (an event sub, or a sub0
    /// branch-target block) spawns <b>active</b>, while a site on the settled main init flow (one that
    /// post-dominates sub0's entry) spawns <b>inert</b> (STATIC-SCD-RE.md cont.16/18; the active case
    /// dump-proven by copy B, ADD-ENEMY-EVENT-INJECTION-CRASH-RCA.md "44912 dump RCA"). Prefer the
    /// intent-named <see cref="AddEnemyEncounter"/> / <see cref="AddEnemyStanding"/> wrappers — which pick a
    /// site of the right mode via <see cref="InjectionSiteClassifier"/> — over hand-deriving an offset. The
    /// offset must be a clean opcode boundary inside a subroutine — validated here, since a mid-instruction
    /// splice would corrupt the script — and 4-aligned (an <see cref="ScriptInjector.Insert"/> invariant).
    /// For the enemy to render, the target subroutine must already have loaded the species' resources by
    /// the time it runs.
    /// </summary>
    /// <returns>The new enemy record, surfaced in <see cref="Enemies"/> at <paramref name="rdtOffset"/>.</returns>
    /// <exception cref="InvalidOperationException">When the script is unparsed, or the offset is not a
    /// clean opcode boundary in any subroutine.</exception>
    public EnemyRecord AddEnemyAt(SpeciesDonor donor, int rdtOffset, short x, short y, short z, short rotation,
                                  byte? slot = null, byte? killFlag = null, EnemyAuthoring authoring = default)
        => InjectAt(donor, rdtOffset, x, y, z, rotation, slot, killFlag, authoring);

    /// <summary>The shared splice core behind <see cref="AddEnemyAt"/>, <see cref="AddEnemyStanding"/> and
    /// <see cref="AddEnemyEncounter"/>: resolve the model (reuse an already-loaded one, else import),
    /// validate the offset is a clean opcode boundary, author the 24-byte <c>0x20</c> record and inject it
    /// via <see cref="ScriptInjector"/>. Site SELECTION is the callers' job (see
    /// <see cref="InjectionSiteClassifier"/>); this places at a given, validated offset.</summary>
    private EnemyRecord InjectAt(SpeciesDonor donor, int rdtOffset, short x, short y, short z, short rotation,
                                byte? slot = null, byte? killFlag = null, EnemyAuthoring authoring = default)
    {
        if (Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject");

        byte chosenSlot = slot ?? PickFreeSlot();
        byte chosenKill = killFlag ?? PickFreeKillFlag();

        // 1. Resolve the model+motion. If the room ALREADY loads this species, REUSE that loaded model:
        //    a freshly imported copy is appended to the RDT but never loaded into a renderable node, so an
        //    event-spawned enemy using it render-AVs (ADD-ENEMY-EVENT-INJECTION-CRASH-RCA.md). Only import
        //    a species the room does not already load. Either path leaves rdtOffset valid — a reuse moves
        //    nothing, and an import appends at the buffer END (at/before rdtOffset is untouched).
        uint modelPtr, motionPtr;
        if (LoadedModelFor(donor.Species) is { } loaded)
            (modelPtr, motionPtr) = loaded;
        else
        {
            var imp = SpeciesImporter.Import(RdtBuffer, donor.Model, donor.Motion);
            RdtBuffer = imp.Rdt;
            (modelPtr, motionPtr) = (imp.ModelPtr, imp.MotionPtr);
        }

        // 2. Validate the requested offset is a clean opcode boundary inside a subroutine (else the splice
        //    would split an instruction). Insert separately enforces 4-alignment / after-table placement.
        int sub = ScriptInjector.SubroutineAtBoundary(RdtBuffer, rdtOffset);
        if (sub < 0)
            throw new InvalidOperationException(
                $"offset 0x{rdtOffset:x} is not a clean opcode boundary inside any subroutine; " +
                "injecting there would split an instruction (pick an offset from the room's decoded script)");

        // Refuse to displace a control-flow opcode (branch / loop / the 0x04 counter-gated loop-return):
        // a record spliced there derails the SCD VM (0102 crashed at load with the auto-init offset on
        // sub0's 0x04 — docs/reference/dc1/enemies/ENEMY-INJECTION-MODES.md "0102 load-crash RCA").
        if (ScriptCfg.IsControlOpcode(RdtBuffer[rdtOffset]))
            throw new InvalidOperationException(
                $"offset 0x{rdtOffset:x} is a control-flow opcode (0x{RdtBuffer[rdtOffset]:x2}); splicing a " +
                "record there derails the SCD VM — pick a plain opcode boundary, not a branch/loop/return");

        // 3. Author the record and inject it (ScriptInjector relocates the function table, file-form
        //    pointers and pc-relative branches around the shift).
        var record = BuildEnemyRecord(donor.Category, chosenSlot, chosenKill, x, y, z, rotation,
                                      modelPtr, motionPtr, authoring);
        if (authoring.ActivateBehavior is byte behavior)
        {
            var (b3, blob) = ResolveActivation(behavior, authoring);
            record = AppendActivationPair(record, chosenSlot, behavior, b3, blob);
        }
        RdtBuffer = ScriptInjector.Insert(RdtBuffer, rdtOffset, record);

        _structurallyEdited = true;
        Reparse();
        return Enemies.First(e => e.FileOffset == rdtOffset);
    }

    /// <summary>
    /// The (model, motion) file-form pointers of an enemy this room ALREADY loads for
    /// <paramref name="species"/>, or <c>null</c> when the room has none. Reusing such a pointer is what
    /// makes an injected enemy renderable: the model is already resident in a node, whereas a freshly
    /// imported duplicate is never loaded → the render pass dereferences a garbage header
    /// (docs/decisions/dc1/spawn/ADD-ENEMY-EVENT-INJECTION-CRASH-RCA.md). Matches only positively-decoded dinosaurs.
    /// </summary>
    public (uint Model, uint Motion)? LoadedModelFor(DinoSpecies species)
    {
        var e = Enemies.FirstOrDefault(x => x.IsRandomizableDino && x.Species == species);
        return e is null ? null : (e.OriginalModelPtr, e.OriginalMotionPtr);
    }

    /// <summary>
    /// <see cref="AddEnemyAt"/> plus the donor's texture staged into the target room (same as
    /// <see cref="AddEnemyTextured"/> but at a caller-specified offset).
    /// </summary>
    public (EnemyRecord Enemy, TexturedImportResult Texture) AddEnemyAtTextured(
        SpeciesDonor donor, int rdtOffset, short x, short y, short z, short rotation,
        byte? slot = null, byte? killFlag = null, EnemyAuthoring authoring = default)
    {
        // When AddEnemyAt reuses an already-loaded model, the texture is already resident — no staging.
        bool reused = LoadedModelFor(donor.Species) is not null;
        var added = AddEnemyAt(donor, rdtOffset, x, y, z, rotation, slot, killFlag, authoring);
        return reused
            ? (added, new TexturedImportResult(TextureImportOutcome.Reused, null, null))
            : (added, StageTexture(donor.Texture, added.ModelPtr));
    }

    /// <summary>
    /// Add an <b>active, persistent "standing"</b> enemy — one that hunts and re-instantiates on every room
    /// entry, like the room's native standing raptor. Picks an (active, every-entry) site via
    /// <see cref="InjectionSiteClassifier.StandingSite"/> (a flag-gated init branch-target spawn block,
    /// dump-proven by copy B in 010A — docs/reference/dc1/enemies/ENEMY-INJECTION-MODES.md) and splices there. The site's
    /// existing gate decides on which entries it actually places (e.g. post-encounter, once the room's story
    /// flag has latched). Same-species reuse applies (no render-AV).
    /// </summary>
    /// <exception cref="InvalidOperationException">When the script is unparsed, or the room has no such
    /// site — the honest failure, so a standing enemy is never silently downgraded to inert/one-shot.</exception>
    public EnemyRecord AddEnemyStanding(SpeciesDonor donor, short x, short y, short z, short rotation,
                                        byte? slot = null, byte? killFlag = null)
    {
        if (Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject");
        int o = InjectionSiteClassifier.StandingSite(RdtBuffer);
        if (o < 0)
            throw new InvalidOperationException(
                "room has no standing (active+persistent) injection site (no flag-gated init branch-target " +
                "spawn block); use AddEnemyEncounter for a one-shot event spawn, or AddEnemy for an inert prop");
        return InjectAt(donor, o, x, y, z, rotation, slot, killFlag);
    }

    /// <summary>
    /// Add an <b>active, one-shot "encounter"</b> enemy — one that hunts when its trigger fires but does not
    /// return (it rides a self-latching event). Picks an (active, one-shot) event-sub site via
    /// <see cref="InjectionSiteClassifier.EncounterSite"/> and splices there. The target sub must already
    /// load the species' resources by the time it runs (use a same-species donor — reuse applies — or an
    /// event sub known to load it). Formalizes the event-injection capability (copy A family / the
    /// <c>--add-enemy-at</c> path) as a named intent.
    /// </summary>
    /// <exception cref="InvalidOperationException">When the script is unparsed, or the room has no event
    /// (non-init) subroutine.</exception>
    public EnemyRecord AddEnemyEncounter(SpeciesDonor donor, short x, short y, short z, short rotation,
                                         byte? slot = null, byte? killFlag = null)
    {
        if (Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject");
        int o = InjectionSiteClassifier.EncounterSite(RdtBuffer);
        if (o < 0)
            throw new InvalidOperationException(
                "room has no event (active one-shot) injection site (no non-init subroutine)");
        return InjectAt(donor, o, x, y, z, rotation, slot, killFlag);
    }

    /// <summary>The dominance-safe injection offset inside subroutine 0: the latest 4-aligned opcode
    /// boundary that <b>post-dominates the subroutine entry</b>, i.e. is guaranteed to execute on every
    /// room entry (<see cref="ScriptCfg"/>; docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.17). This is the fix for the
    /// "sometimes it spawns" gap: the old tail offset (largest interior boundary) is provably
    /// undominated in 13 of 25 enemy rooms — a branch or an <c>0x04</c>/<c>0x10</c> exit skips it on
    /// some entries, so the injected enemy silently fails to spawn. Falls back to the tail boundary only
    /// if the CFG yields no interior post-dominated point (no observed corpus room). -1 if subroutine 0
    /// has no interior opcode boundary at all.</summary>
    private static int InitInsertOffset(ReadOnlySpan<byte> rdt, IReadOnlyList<int> starts)
    {
        int s0 = starts[0];
        int e0 = starts.Count > 1 ? starts[1] : rdt.Length;

        int safe = ScriptCfg.SafeInsertOffset(rdt, s0, e0);
        if (safe > 0) return safe;

        // Fallback: largest 4-aligned interior PLAIN-opcode boundary (the pre-cont.17 behaviour, minus
        // control-flow slots — splicing before a branch / loop / the 0x04 loop-return derails the VM).
        int best = -1, pos = s0;
        while (pos < e0)
        {
            int len = DcOpcodes.Length(rdt, pos);
            if (len <= 0 || pos + len > e0) break; // trailing data / derail
            if (pos > s0 && (pos & 3) == 0 && !ScriptCfg.IsControlOpcode(rdt[pos])) best = pos;
            pos += len;
        }
        return best;
    }

    /// <summary>Smallest large-entity slot index not already used by a placed enemy (0 when empty).</summary>
    private byte PickFreeSlot()
    {
        var used = new HashSet<int>(Enemies.Select(e => (int)e.Slot));
        byte s = 0;
        while (used.Contains(s) && s < 0xff) s++;
        return s;
    }

    /// <summary>An "already-killed" GetFlag(group 4) id not used by another enemy in this room, so the
    /// new enemy is not treated as pre-killed (and killing it does not mark a sibling dead). 0 is the
    /// corpus norm and is returned when free.</summary>
    private byte PickFreeKillFlag()
    {
        var used = new HashSet<int>(Enemies.Select(e => (int)e.KillFlag));
        byte f = 0;
        while (used.Contains(f) && f < 0xff) f++;
        return f;
    }

    private static byte[] BuildEnemyRecord(byte category, byte slot, byte killFlag,
                                           short x, short y, short z, short rotation,
                                           uint modelPtr, uint motionPtr, EnemyAuthoring authoring = default)
    {
        var r = new byte[DcOpcodes.EnemyLength]; // 24, zero-filled (matches the corpus template)
        r[0] = DcOpcodes.Enemy;
        r[EnemyRecord.SlotOffset] = slot;
        r[EnemyRecord.CategoryOffset] = category;
        r[EnemyRecord.AiParamOffset] = authoring.AiParam;
        r[EnemyRecord.KillFlagOffset] = killFlag;
        r[EnemyRecord.BirthModeOffset] = authoring.BirthMode;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(EnemyRecord.MaxHpOffset, 2), authoring.MaxHp);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(EnemyRecord.PosXOffset, 2), x);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(EnemyRecord.PosYOffset, 2), y);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(EnemyRecord.PosZOffset, 2), z);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(EnemyRecord.RotationOffset, 2), rotation);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(EnemyRecord.ModelOffset, 4), modelPtr);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(EnemyRecord.MotionOffset, 4), motionPtr);
        return r;
    }

    /// <summary>
    /// Append the script activation pair after an authored <c>0x20</c> record (STATIC-SCD-RE cont.49):
    /// op <c>0x22</c> (<c>22 02 &lt;slot&gt; 00</c> — bind the running task's implicit entity,
    /// <c>ctx+0xB = slot</c>) then op <c>0x3a</c> (<c>3a 00 &lt;behavior&gt; &lt;b3&gt;</c> + 8 operand
    /// bytes — the "brain install": <c>entity+0x190 ← ptr(rec+4)</c>, <c>+0x18F = 1</c>,
    /// <c>dword +0x3C ← (behavior&lt;&lt;8)|4</c>). This is what wakes a cat-1 placed by init (cat-2
    /// self-activates without it). Total emitted insert = 24 + 4 + 12 = 40 bytes (4-aligned).
    /// </summary>
    private static byte[] AppendActivationPair(byte[] record, byte slot, byte behavior, byte b3, byte[] blob)
    {
        if (blob.Length != 8)
            throw new ArgumentException($"op 0x3a operand blob must be 8 bytes, got {blob.Length}", nameof(blob));
        var r = new byte[record.Length + 16];
        record.CopyTo(r, 0);
        int o = record.Length;
        r[o] = 0x22; r[o + 1] = 0x02; r[o + 2] = slot;      // 22 02 <slot> 00
        r[o + 4] = 0x3a; r[o + 6] = behavior; r[o + 7] = b3; // 3a 00 <behavior> <b3> <blob8>
        blob.CopyTo(r, o + 8);
        return r;
    }

    /// <summary>Resolve the emitted op <c>0x3a</c>'s byte[3] + 8 operand bytes from
    /// <paramref name="authoring"/>: explicit values win; otherwise both are copied together from the
    /// first native <c>0x3a</c> in this room (preferring one whose behavior code matches — retail
    /// pairs b3/blob with the behavior, e.g. st10e's behavior-1 installs carry b3 0x1F/0x21 + what
    /// look like target coords, while behavior-5 installs are all-zero); zeros when the room has none
    /// (a retail-legal value — st102 sub6, cont.52).</summary>
    private (byte B3, byte[] Blob) ResolveActivation(byte behavior, EnemyAuthoring authoring)
    {
        if (authoring.ActivateBlob is { } explicitBlob)
            return (authoring.ActivateB3 ?? 0, explicitBlob);
        var native = FindNative3a(behavior) ?? FindNative3a(null);
        return (authoring.ActivateB3 ?? native?.B3 ?? 0, native?.Blob ?? new byte[8]);
    }

    /// <summary>The (byte[3], operand blob) of the first native op <c>0x3a</c> in this room's script —
    /// filtered to <paramref name="behavior"/> when given — or null. The operands reference
    /// script-embedded behavior data (format unmapped — cont.49), so a same-room copy keeps them valid.</summary>
    private (byte B3, byte[] Blob)? FindNative3a(byte? behavior)
    {
        if (!ScriptInjector.TryReadFuncTable(RdtBuffer, out _, out var starts)) return null;
        for (int i = 0; i < starts.Count; i++)
        {
            int s = starts[i], e = i + 1 < starts.Count ? starts[i + 1] : RdtBuffer.Length;
            for (int pos = s; pos < e;)
            {
                int len = DcOpcodes.Length(RdtBuffer, pos);
                if (len <= 0 || pos + len > e) break; // trailing data / derail
                if (RdtBuffer[pos] == 0x3a && (behavior is null || RdtBuffer[pos + 2] == behavior))
                    return (RdtBuffer[pos + 3], RdtBuffer.AsSpan(pos + 4, 8).ToArray());
                pos += len;
            }
        }
        return null;
    }

    /// <summary>Re-walk the (grown) <see cref="RdtBuffer"/> and refresh the record lists / script.</summary>
    private void Reparse()
    {
        Script = RoomScript.Parse(RdtBuffer);
        Items.Clear(); Enemies.Clear(); Doors.Clear();
        if (Script.ParsedCleanly && DcOpcodes.IsTrustworthy)
        {
            Items.AddRange(Script.Items);
            Enemies.AddRange(Script.Enemies);
            Doors.AddRange(Script.Doors);
        }
    }

    /// <summary>
    /// Serialize back. Byte-exact when no item id changed; otherwise the edited RDT buffer is
    /// re-emitted into the (last) package entry, re-compressed when the entry was compressed.
    /// </summary>
    public byte[] Write()
    {
        if (Script is not { ParsedCleanly: true } || Package?.RoomDataEntry is not { } entry)
            return OriginalBytes;

        bool changed = _structurallyEdited;
        foreach (var item in Items)
            if (!item.IsEmptySlot && item.ItemId != item.OriginalItemId) { changed = true; break; }
        if (!changed)
            foreach (var enemy in Enemies)
                if (enemy.IsEdited) { changed = true; break; }
        if (!changed)
            foreach (var door in Doors)
                if (door.IsEdited) { changed = true; break; }
        if (!changed) return OriginalBytes;

        var edited = (byte[])RdtBuffer.Clone();
        Script.ApplyEdits(edited, Items);
        Script.ApplyEnemyEdits(edited, Enemies);
        Script.ApplyDoorEdits(edited, Doors);

        byte[] payload = RdtCompressed ? Lzss.Compress(edited) : edited;
        byte[] result = RebuildLastEntry(payload, entry);
        if (_textureInserts is { Count: > 0 })
            result = PackageRepacker.InsertEntriesBeforeRdt(result, _textureInserts);
        return result;
    }

    /// <summary>
    /// Rebuild the on-disk file bytes from an externally-edited <b>decompressed</b> RDT buffer: re-LZSS (if
    /// the original RDT entry was compressed) then repack the last package entry (size field + sector pad).
    /// Use for raw RDT edits that bypass the Script/Items/Enemies/Doors path — e.g. document glyph-token
    /// rewrites (<see cref="MgmtOfficeDocumentCode"/>). The game re-reads the entry's declared size, so a
    /// re-compressed payload of any length is fine. Returns <see cref="OriginalBytes"/> if unpackaged.
    /// </summary>
    public byte[] WriteWithRdt(ReadOnlySpan<byte> editedDecompressedRdt)
    {
        if (Package?.RoomDataEntry is not { } entry) return OriginalBytes;
        byte[] payload = RdtCompressed
            ? Lzss.Compress(editedDecompressedRdt)
            : editedDecompressedRdt.ToArray();
        return RebuildLastEntry(payload, entry);
    }

    /// <summary>
    /// Rebuild the file with the last entry's payload replaced by <paramref name="payload"/>.
    /// The header up to (and including) the prior entries is kept verbatim; only the last
    /// entry's size field is rewritten and its payload re-padded to the sector boundary.
    /// </summary>
    private byte[] RebuildLastEntry(byte[] payload, GianEntry entry)
    {
        int sector = GianPackage.SectorSize;
        int padded = (payload.Length + sector - 1) & ~(sector - 1);

        var result = new byte[entry.PayloadOffset + padded];
        // Header + earlier entries (everything before this entry's payload) verbatim.
        Array.Copy(OriginalBytes, 0, result, 0, entry.PayloadOffset);
        Array.Copy(payload, 0, result, entry.PayloadOffset, payload.Length);

        // Patch the last entry's declared size in the header table (size field at entry+4).
        int entryIndex = Package!.Entries.Count - 1;
        int sizeFieldOffset = entryIndex * Package.EntrySize + 4;
        uint size = (uint)payload.Length;
        result[sizeFieldOffset + 0] = (byte)size;
        result[sizeFieldOffset + 1] = (byte)(size >> 8);
        result[sizeFieldOffset + 2] = (byte)(size >> 16);
        result[sizeFieldOffset + 3] = (byte)(size >> 24);
        return result;
    }

    public static RoomFile ReadFromFile(int stage, int room, string path)
        => Read(stage, room, File.ReadAllBytes(path));

    public override string ToString() => $"ST{Stage:X}:R{Room:X2}";
}
