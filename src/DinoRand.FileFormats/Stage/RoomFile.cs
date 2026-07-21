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

    private readonly RoomTextureStager _textureStager = new();

    /// <summary>True when the SCD script walked cleanly (writes are only applied then).</summary>
    public bool ParsedCleanly => Script?.ParsedCleanly ?? false;

    public static RoomFile Read(int stage, int room, ReadOnlySpan<byte> bytes)
    {
        var rf = new RoomFile(stage, room) { OriginalBytes = bytes.ToArray() };
        rf.Package = GianPackage.TryParse(bytes);
        if (rf.Package?.RoomDataEntry is { } rdt)
        {
            (rf.RdtBuffer, rf.RdtCompressed) = RoomPackageCodec.DecodeRdt(bytes, rdt);

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
        => RoomSpeciesEditor.Import(this, donor, enemyIndex);

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
        => RoomSpeciesEditor.Import(this, donor, enemyIndex);

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
        => RoomSpeciesEditor.Overlay(this, donor, enemyIndex);

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
        /// <summary>No free VRAM region existed, so the donor texture was placed over the replaced
        /// victim's own texture column / palette row (safe because no surviving model samples them;
        /// the staged entry uploads after the original and wins). The generalization of the Theri
        /// fixed-column strategy — see TEXTURE-IMPORT-VRAM.md "Outcome census".</summary>
        ReclaimedVictim,
    }

    /// <summary>Result of a textured import: the <see cref="Outcome"/> and (when relocated) the
    /// texture/palette VRAM rects placed. The final package bytes come from <see cref="Write"/>, which
    /// emits any staged texture entries — so callers import then call <see cref="Write"/> as usual.</summary>
    public sealed record TexturedImportResult(TextureImportOutcome Outcome,
                                              VramRect? TextureRect, VramRect? PaletteRect);

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
        var reclaim = RoomSpeciesEditor.VictimReclaimableRects(this, enemyIndex);
        ImportSpecies(donor, enemyIndex);
        return StageTexture(donor.Texture, Enemies[enemyIndex].ModelPtr, reclaim);
    }

    /// <summary>
    /// Budget and apply a production grounded-species import as one commit. The complete RDT + texture fit is
    /// evaluated on a fresh parse of the room's current serialized bytes; geometry, model-code rewrites and
    /// texture staging then happen only on that private copy. This instance adopts the final serialized room
    /// only after the import remains textured (<see cref="TextureImportOutcome.Relocated"/> or
    /// <see cref="TextureImportOutcome.ReclaimedVictim"/>) and reparses cleanly. Every refusal or exception
    /// before that point leaves this room byte-identical and semantically unchanged.
    /// </summary>
    public bool TryImportSpeciesTexturedAtomic(
        SpeciesDonor donor, int enemyIndex, int rdtCeiling,
        out EnemyImportFit fit, out TexturedImportResult? texture)
    {
        texture = null;
        var work = Read(Stage, Room, Write());
        if (enemyIndex < 0 || enemyIndex >= work.Enemies.Count)
            throw new ArgumentOutOfRangeException(nameof(enemyIndex));

        var reclaim = RoomSpeciesEditor.VictimReclaimableRects(work, enemyIndex);
        fit = EnemyImportBudget.Evaluate(
            donor.Model, donor.Motion, work.RdtBuffer.Length, rdtCeiling,
            work.OriginalBytes, donor.Texture, reclaim, requireTexture: true);
        if (!fit.Fits) return false;

        var staged = work.ImportSpeciesTextured(donor, enemyIndex);
        if (staged.Outcome is not (TextureImportOutcome.Relocated or TextureImportOutcome.ReclaimedVictim)
            || staged.Outcome != fit.TextureOutcome)
        {
            fit = new EnemyImportFit(false, 0, 0, work.RdtBuffer.Length,
                ImportFitConstraint.Vram, null, null, null,
                $"texture staging produced {staged.Outcome} after a {fit.TextureOutcome} preflight");
            return false;
        }

        var final = work.Write();
        var committed = Read(Stage, Room, final);
        if (!committed.ParsedCleanly)
            throw new InvalidOperationException("grounded import serialized to an unparseable room");
        Adopt(committed);
        texture = staged;
        return true;
    }

    /// <summary>Multi-range (entangled species) sibling of
    /// <see cref="ImportSpeciesTextured(SpeciesDonor,int)"/>.</summary>
    public TexturedImportResult ImportSpeciesTextured(SpeciesDonorMulti donor, int enemyIndex)
    {
        var reclaim = RoomSpeciesEditor.VictimReclaimableRects(this, enemyIndex);
        ImportSpecies(donor, enemyIndex);
        return StageTexture(donor.Texture, Enemies[enemyIndex].ModelPtr, reclaim);
    }

    /// <summary>VRAM rects already claimed by staged texture entries (enemy imports, earlier pickup
    /// imports) — a later placement into this room must avoid them as well as the on-disk uploads.</summary>
    public IReadOnlyList<VramRect> StagedTextureRects
        => _textureStager.Rects;

    /// <summary>Stage one extra texture/palette package entry for <see cref="Write"/> to inject before
    /// the RDT (Lever B pickup texture travel; same channel as <see cref="StageTexture"/>).</summary>
    public void StageTextureEntry(PackageRepacker.NewEntry entry)
        => _textureStager.Add(this, entry);

    /// <summary>
    /// Append a pickup ground-mesh blob at the END of the decompressed RDT and return its file-form
    /// pointer (<c>0x80100000 + offset</c>). End-append is <see cref="ScriptInjector.Insert"/>'s
    /// degenerate case — nothing shifts, so no pointer/table/branch relocation is needed (and the
    /// blob must NOT go through <c>Insert</c>: its pointer scan would double-shift the blob's own
    /// header self-pointers). The caller rebases the blob for the returned pointer
    /// (<see cref="PickupMeshFormat.RebaseAndRetarget"/>) BEFORE calling. Fails (returns false)
    /// when growth would cross the engine work-struct ceiling
    /// (<see cref="SpeciesImporter.EngineRoomRdtCeiling"/>).
    /// </summary>
    public bool TryAppendPickupMesh(byte[] blob, out uint filePtr)
    {
        filePtr = RoomScript.PsxRdtBase + (uint)RdtBuffer.Length;
        if (RdtBuffer.Length + blob.Length > SpeciesImporter.EngineRoomRdtCeiling) return false;
        var grown = new byte[RdtBuffer.Length + blob.Length];
        RdtBuffer.CopyTo(grown, 0);
        blob.CopyTo(grown, RdtBuffer.Length);
        RdtBuffer = grown;
        _structurallyEdited = true;
        return true;
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
        => RoomEnemyInjector.ImportSpeciesGroupTextured(
            this, donor, victimIndex, extraMembers, groupSetupRecord);

    /// <summary>Texture-aware counterpart of <see cref="AddEnemy"/>: inject a brand-new enemy and, if
    /// the <paramref name="donor"/> carries a texture, relocate it into this room and rewrite the new
    /// model's codes (<see cref="StageTexture"/>). Returns the new record and the texture outcome;
    /// call <see cref="Write"/> for the final bytes.</summary>
    public (EnemyRecord Enemy, TexturedImportResult Texture) AddEnemyTextured(
        SpeciesDonor donor, short x, short y, short z, short rotation, byte? slot = null, byte? killFlag = null,
        EnemyAuthoring authoring = default)
        => RoomEnemyInjector.AddEnemyTextured(
            this, donor, x, y, z, rotation, slot, killFlag, authoring);

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
        => RoomEnemyInjector.AddEnemy(
            this, donor, x, y, z, rotation, slot, killFlag, authoring);

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
        => RoomEnemyInjector.AddEnemyAt(
            this, donor, rdtOffset, x, y, z, rotation, slot, killFlag, authoring);

    /// <summary>
    /// The (model, motion) file-form pointers of an enemy this room ALREADY loads for
    /// <paramref name="species"/>, or <c>null</c> when the room has none. Reusing such a pointer is what
    /// makes an injected enemy renderable: the model is already resident in a node, whereas a freshly
    /// imported duplicate is never loaded → the render pass dereferences a garbage header
    /// (docs/decisions/dc1/spawn/ADD-ENEMY-EVENT-INJECTION-CRASH-RCA.md). Matches only positively-decoded dinosaurs.
    /// </summary>
    public (uint Model, uint Motion)? LoadedModelFor(DinoSpecies species)
        => RoomEnemyInjector.LoadedModelFor(this, species);

    /// <summary>
    /// <see cref="AddEnemyAt"/> plus the donor's texture staged into the target room (same as
    /// <see cref="AddEnemyTextured"/> but at a caller-specified offset).
    /// </summary>
    public (EnemyRecord Enemy, TexturedImportResult Texture) AddEnemyAtTextured(
        SpeciesDonor donor, int rdtOffset, short x, short y, short z, short rotation,
        byte? slot = null, byte? killFlag = null, EnemyAuthoring authoring = default)
        => RoomEnemyInjector.AddEnemyAtTextured(
            this, donor, rdtOffset, x, y, z, rotation, slot, killFlag, authoring);

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
        => RoomEnemyInjector.AddEnemyStanding(
            this, donor, x, y, z, rotation, slot, killFlag);

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
        => RoomEnemyInjector.AddEnemyEncounter(
            this, donor, x, y, z, rotation, slot, killFlag);

    internal void ReplaceRdtBuffer(byte[] buffer) => RdtBuffer = buffer;

    internal void MarkStructurallyEdited() => _structurallyEdited = true;

    private void Adopt(RoomFile committed)
    {
        OriginalBytes = committed.OriginalBytes;
        Package = committed.Package;
        RdtBuffer = committed.RdtBuffer;
        RdtCompressed = committed.RdtCompressed;
        Script = committed.Script;
        Items.Clear(); Items.AddRange(committed.Items);
        Enemies.Clear(); Enemies.AddRange(committed.Enemies);
        Doors.Clear(); Doors.AddRange(committed.Doors);
        _structurallyEdited = false;
        _textureStager.Clear();
    }

    internal TexturedImportResult StageTexture(
        TextureBlock? texture, uint importedModelPtr, IReadOnlyList<VramRect>? reclaim = null)
        => _textureStager.Stage(this, texture, importedModelPtr, reclaim);

    /// <summary>Re-walk the (grown) <see cref="RdtBuffer"/> and refresh the record lists / script.</summary>
    internal void Reparse()
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
            if (!item.IsEmptySlot && (item.ItemId != item.OriginalItemId || item.NormalizeVisual
                || item.TakeIndex != item.OriginalTakeIndex)) { changed = true; break; }
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

        byte[] payload = RoomPackageCodec.EncodeRdt(edited, RdtCompressed);
        byte[] result = RoomPackageCodec.RebuildLastEntry(OriginalBytes, Package!, entry, payload);
        return _textureStager.Apply(result);
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
        return RoomPackageCodec.RewriteRdt(
            OriginalBytes, Package, entry, RdtCompressed, editedDecompressedRdt);
    }

    public static RoomFile ReadFromFile(int stage, int room, string path)
        => Read(stage, room, File.ReadAllBytes(path));

    public override string ToString() => $"ST{Stage:X}:R{Room:X2}";
}
