using System.Buffers.Binary;

namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level patcher for the Dino Crisis 1 executable (<c>DINO.exe</c>, the Classic REbirth PSX
/// recompile). Parallel to <see cref="DinoRand.FileFormats.Stage.SpeciesImporter"/> /
/// <c>ScriptInjector</c> but for the EXE rather than an RDT: it does <b>data-table offset/length/value
/// writes</b> — plus one small code stub, the defect-B walker NULL-guard
/// (<see cref="InstallWalkerNullGuard"/>), a fixed hand-assembled byte sequence written into <c>.text</c>
/// zero-slack (it does not re-assemble or relocate existing code).
///
/// <para><b>Why an EXE patch exists at all.</b> DC1 selects each room's enemy identity/AI/model set
/// inside the EXE from a hardcoded population/record table at VA <c>0x647050</c>, keyed on an index;
/// a <c>.dat</c> SCD record can only place an <i>instance</i>, not change which enemy categories a
/// room hosts. Cross-species therefore needs an EXE patch of the room's record (see
/// <c>docs/decisions/dc1/exe/EXE-PATCH-PER-ROOM-PLAN.md</c>).</para>
///
/// <para><b>The index is the stage byte.</b> The loader dispatch at <c>0x432F64</c> reads the record
/// index as <c>[edx+0x9AB1]</c>, which resolves to <c>0x6DD8F1</c> — the <i>high byte of the 16-bit
/// current-room id word</i> at <c>0x6DD8F0</c>. So <c>index = roomId &gt;&gt; 8</c> (the stage
/// number); the record table is indexed by stage, and a patch to one record is <b>stage-scoped</b>
/// (it affects every room in that stage). Located and sourced live in
/// <c>docs/decisions/dc1/enemies/REGION-INDEX-MAP.md</c>.</para>
///
/// <para><b>Section rule (the file-backed window).</b> A read-only PE parse confirmed that the
/// <c>.text</c>/<c>.rdata</c>/<c>.data</c> sections are mapped raw == virtual, contiguous over
/// RVA <c>[0x1000, 0x270000)</c> (i.e. VA <c>[0x401000, 0x670000)</c>); for any VA in that window
/// <c>file_offset = VA - 0x400000</c> with zero delta. The rule <b>breaks</b> at the <c>.data</c> raw
/// end (RVA <c>0x270000</c>): everything past it is either BSS (runtime-only, not in the file at all —
/// e.g. <c>[0x6DE990]</c>, the room-id word) or <c>.idata</c>/<c>.tls</c>/<c>.rsrc</c>/<c>.reloc</c>
/// (present but raw != virtual). <see cref="VaToFileOffset"/> enforces this and refuses anything
/// outside the window, so a runtime-only address can never be written to disk.</para>
///
/// <para>All methods operate on an in-memory copy of the whole EXE (<c>byte[]</c> / spans). Reading
/// the file, backing it up, and writing it back are the installer's concern, not this type's.</para>
/// </summary>
public static class ExePatcher
{
    /// <summary>PE image base; <c>DINO.exe</c> has no ASLR, so this is fixed. <c>[verified]</c></summary>
    public const uint ImageBase = 0x00400000;

    /// <summary>Lowest file-backed RVA (start of <c>.text</c>). Below this is the PE header. <c>[verified]</c></summary>
    public const uint FileBackedRvaLo = 0x00001000;

    /// <summary>
    /// Exclusive upper bound of the raw == virtual window: the <c>.data</c> raw end (RVA
    /// <c>0x270000</c>, where <c>.idata</c>'s raw data begins). VAs at or above
    /// <c>ImageBase + this</c> are BSS / non-raw-aligned and are not patchable on disk. <c>[verified]</c>
    /// </summary>
    public const uint FileBackedRvaHi = 0x00270000;

    // ---- Enemy population/record table (the patch lever) ----

    /// <summary>VA of <c>record[0]</c>, the real base of the per-stage record table the loader
    /// dispatches through (<c>0x647068 = record[0] + 0x0E</c>). <c>[verified live]</c></summary>
    public const uint RecordTableBaseVa = 0x0064705A;

    /// <summary>Size of one record. Layout: <c>+0x00</c> word id, <c>+0x02</c> dword count,
    /// <c>+0x06</c>/<c>+0x0A</c> data ptrs, <c>+0x0E</c> setup-fn ptr, <c>+0x12</c> dword,
    /// <c>+0x16</c> word. <c>[verified]</c></summary>
    public const uint RecordStride = 0x18;

    /// <summary>Offset within a record of the setup-fn pointer — the chosen patch lever. The setup fn
    /// installs the room's whole AI-handler set (<c>mov [0x6DE990], &lt;handler table&gt;</c>), so
    /// repointing it swaps the stage's enemy set to a donor's. <c>[verified live]</c></summary>
    public const uint SetupFnFieldOffset = 0x0E;

    /// <summary>Stage-1 basic-raptor setup fn (installs <c>0x65735C</c> = cat0/1/2). <c>[verified live]</c></summary>
    public const uint SetupFnBasicRaptor = 0x0046EE00;

    /// <summary>Stage-2 richer setup fn (installs <c>0x656E8C</c> = cat0–4,6,7). <c>[verified live]</c></summary>
    public const uint SetupFnStage2 = 0x0046EAC0;

    /// <summary>
    /// Translate a VA to its on-disk file offset, enforcing the verified section rule. Valid only for
    /// VAs in the raw == virtual window (<c>.text</c>/<c>.rdata</c>/<c>.data</c> below the <c>.data</c>
    /// raw end); throws <see cref="ArgumentOutOfRangeException"/> for anything outside it — including
    /// every runtime-only / BSS address, which by design must never be written to disk.
    /// </summary>
    public static int VaToFileOffset(uint va)
    {
        if (va < ImageBase)
            throw new ArgumentOutOfRangeException(nameof(va), $"VA 0x{va:X} is below the image base 0x{ImageBase:X}.");
        uint rva = va - ImageBase;
        if (rva < FileBackedRvaLo || rva >= FileBackedRvaHi)
            throw new ArgumentOutOfRangeException(nameof(va),
                $"VA 0x{va:X} (RVA 0x{rva:X}) is outside the file-backed window " +
                $"[0x{ImageBase + FileBackedRvaLo:X}, 0x{ImageBase + FileBackedRvaHi:X}); " +
                "it is BSS/runtime-only or non-raw-aligned and cannot be patched on disk.");
        return (int)rva; // raw == virtual ⇒ file offset == RVA, delta 0
    }

    /// <summary>True when <paramref name="va"/> lies in the file-backed window and can be patched.</summary>
    public static bool IsFileBacked(uint va)
        => va >= ImageBase && (va - ImageBase) >= FileBackedRvaLo && (va - ImageBase) < FileBackedRvaHi;

    // ---- Raw reads/writes at a file offset ----

    /// <summary>Read a little-endian <c>ushort</c> at <paramref name="fileOffset"/>.</summary>
    public static ushort ReadUInt16(ReadOnlySpan<byte> exe, int fileOffset)
        => BinaryPrimitives.ReadUInt16LittleEndian(Slice(exe, fileOffset, 2));

    /// <summary>Read a little-endian <c>uint</c> at <paramref name="fileOffset"/>.</summary>
    public static uint ReadUInt32(ReadOnlySpan<byte> exe, int fileOffset)
        => BinaryPrimitives.ReadUInt32LittleEndian(Slice(exe, fileOffset, 4));

    /// <summary>Write a little-endian <c>ushort</c> at <paramref name="fileOffset"/>.</summary>
    public static void WriteUInt16(Span<byte> exe, int fileOffset, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(Slice(exe, fileOffset, 2), value);

    /// <summary>Write a little-endian <c>uint</c> at <paramref name="fileOffset"/>.</summary>
    public static void WriteUInt32(Span<byte> exe, int fileOffset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(Slice(exe, fileOffset, 4), value);

    // ---- Reads/writes addressed by VA (section rule applied) ----

    /// <summary>Read a <c>uint</c> at virtual address <paramref name="va"/>.</summary>
    public static uint ReadUInt32AtVa(ReadOnlySpan<byte> exe, uint va)
        => ReadUInt32(exe, VaToFileOffset(va));

    /// <summary>Read a <c>ushort</c> at virtual address <paramref name="va"/>.</summary>
    public static ushort ReadUInt16AtVa(ReadOnlySpan<byte> exe, uint va)
        => ReadUInt16(exe, VaToFileOffset(va));

    /// <summary>Write a <c>uint</c> at virtual address <paramref name="va"/>.</summary>
    public static void WriteUInt32AtVa(Span<byte> exe, uint va, uint value)
        => WriteUInt32(exe, VaToFileOffset(va), value);

    /// <summary>Write a <c>ushort</c> at virtual address <paramref name="va"/>.</summary>
    public static void WriteUInt16AtVa(Span<byte> exe, uint va, ushort value)
        => WriteUInt16(exe, VaToFileOffset(va), value);

    // ---- Record-table helpers (the lever) ----

    /// <summary>VA of <c>record[<paramref name="index"/>]</c> in the per-stage table.</summary>
    public static uint RecordVa(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index, "record index must be ≥ 0.");
        return RecordTableBaseVa + (uint)index * RecordStride;
    }

    /// <summary>VA of the setup-fn pointer field (<c>+0x0E</c>) of <c>record[<paramref name="index"/>]</c>.
    /// Since <c>index = roomId &gt;&gt; 8</c>, pass the room's stage number.</summary>
    public static uint SetupFnFieldVa(int index) => RecordVa(index) + SetupFnFieldOffset;

    /// <summary>Read the current setup-fn pointer of <c>record[<paramref name="index"/>]</c>.</summary>
    public static uint ReadSetupFn(ReadOnlySpan<byte> exe, int index)
        => ReadUInt32AtVa(exe, SetupFnFieldVa(index));

    /// <summary>
    /// Repoint <c>record[<paramref name="index"/>]</c>'s setup-fn pointer to <paramref name="donorFnVa"/>
    /// (an existing room-type's installer, e.g. <see cref="SetupFnBasicRaptor"/> /
    /// <see cref="SetupFnStage2"/>), giving that stage the donor's whole enemy set. Returns the previous
    /// pointer (for logging / reversal). The donor VA must itself be a file-backed code address — a
    /// guard against writing a runtime-only or garbage pointer. <b>Stage-scoped:</b> affects every room
    /// whose <c>roomId &gt;&gt; 8 == index</c>.
    /// </summary>
    public static uint RepointSetupFn(Span<byte> exe, int index, uint donorFnVa)
    {
        if (!IsFileBacked(donorFnVa))
            throw new ArgumentOutOfRangeException(nameof(donorFnVa),
                $"donor setup-fn VA 0x{donorFnVa:X} is not a file-backed code address.");
        int off = VaToFileOffset(SetupFnFieldVa(index));
        uint previous = ReadUInt32(exe, off);
        WriteUInt32(exe, off, donorFnVa);
        return previous;
    }

    // ---- Surgical per-category AI-handler slot (the precise cross-species lever) ----

    /// <summary>VA of <b>stage 1</b>'s installed per-category AI-handler record (installed by
    /// <see cref="SetupFnBasicRaptor"/> = <c>0x46EE00</c>, read from <c>record[1].setupFn</c>). cat0-2 are
    /// the basic-raptor handlers; <b>cat8 = <c>0</c> is NULL/free</b> (no stage-1 room hosts a cat8 enemy).
    /// <c>[verified from DINO.exe]</c></summary>
    public const uint Stage1AiRecordVa = 0x0065735C;

    /// <summary>VA of <b>stage 2</b>'s installed per-category AI-handler record. The loader's stage-2
    /// setup fn (<see cref="SetupFnStage2"/> = <c>0x46EAC0</c>, read from <c>record[2].setupFn</c>) installs
    /// this table into <c>[0x6DE990]</c>; its slot <c>cat*4</c> is the per-frame AI handler for that
    /// category. cat0-4/6/7 are real handlers, <b>cat8 = <c>0x4B3540</c> is a free stub</b> (no stage-2 room
    /// hosts a cat8 enemy). <c>[verified from DINO.exe + live cont.30]</c></summary>
    public const uint Stage2AiRecordVa = 0x00656E8C;

    /// <summary>The cat-8 Therizinosaurus per-frame AI handler, live-captured in st605
    /// (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.30). <c>[verified live]</c></summary>
    public const uint TheriCat8HandlerVa = 0x0056BFA0;

    /// <summary>The cat-3 Tyrannosaurus (boss rig) per-frame AI handler — the value the stage-6 installed
    /// record <c>0x655B50</c> uses to drive the live 060C/060A T-Rex (it overrides the static
    /// <c>0x656E8C[3]=0x5D0DA8</c>; docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.31). Installed into a stage whose
    /// per-category record lacks cat3 (stage 1's <see cref="Stage1AiRecordVa"/> cat3 slot is NULL) so an
    /// imported T-Rex dispatches to the real boss AI. <c>[verified live cont.31]</c></summary>
    public const uint TrexCat3HandlerVa = 0x00578BA8;

    /// <summary>The cat-5 Swarm (small-dino / compy) per-frame AI handler — the cat5 slot of <b>stage 3</b>'s
    /// installed AI record (<c>0x656B68</c>, installed by stage-3 setup fn <c>0x46E5E0</c>; stage 3 natively
    /// hosts the swarm, e.g. st307 = 4 cat5 instances). Derived statically: the engine has no runtime AI-record
    /// override — every stage's setup fn just <c>mov [0x6DE990], &lt;table&gt;</c> — so a stage's installed
    /// record is exactly the table its setup fn names, and <c>0x656B68[5] = 0x5C8116</c>. A clean function entry
    /// (<c>push ebp; mov ebp,esp; sub esp,0x20</c>) sharing the per-category AI shape of cat1 raptor
    /// <c>0x4DB920</c>. Installed into a stage whose cat5 slot is free (stage 1's <see cref="Stage1AiRecordVa"/>
    /// cat5 is NULL) so an imported swarm dispatches to the real AI. <c>[verified live 2026-06-23]</c> — it is
    /// <b>byte-identical</b> to the stage-4 cat5 sibling <c>0x5A8EC6</c> (only a relative-<c>call</c> rel32
    /// differs; both call helper <c>0x5F33A0</c>), and <c>0x5A8EC6</c> was live-captured in 040B driving 4
    /// active cat-5/7-bone compies (with <c>[0x6DE990]=0x656598</c> matching the static stage-4 table, proving
    /// the no-runtime-override model the stage-3 derivation rests on).</summary>
    public const uint SwarmCat5HandlerVa = 0x005C8116;

    /// <summary>VA of <b>stage 1</b>'s per-frame <b>effect-dispatch table</b> (loaded into <c>[0x6D3CC4]</c> by
    /// the stage-1 setup fn; the per-frame reaction driver <c>0x46F4ED→0x46F380</c> dispatches each active
    /// effect object by its type byte via <c>call [[0x6D3CC4] + byte[effect+1]*4]</c>). Like <c>[0x6DE990]</c>
    /// it is stage-keyed, and stage 1's slot for the swarm's effect type (<see cref="SwarmEffectType"/>) is
    /// uninitialised (`0x657400` = garbage `0x414AC117`) since stage 1 hosts no swarm. <c>[verified from
    /// DINO.exe + crash dump 22032]</c></summary>
    public const uint Stage1EffectTableVa = 0x006573A4;

    /// <summary>VA of stage 2's effect-dispatch table (sibling of <see cref="Stage1EffectTableVa"/>); its
    /// type-<see cref="SwarmEffectType"/> slot is likewise garbage (`0x00000021`). <c>[verified]</c></summary>
    public const uint Stage2EffectTableVa = 0x00656ED0;

    /// <summary>The swarm coordination effect's type byte (set by the op58 record <c>58 00 17 02 …</c>);
    /// indexes the effect-dispatch table.</summary>
    public const int SwarmEffectType = 0x17;

    /// <summary>The <b>type-0x17 swarm coordination-effect handler</b> — the stage-4 table's slot
    /// (<c>0x6565D8[0x17]</c>). Dispatches by <c>byte[effect+3]</c> through its static sub-table <c>0x664CA8</c>
    /// (all 6 entries valid code, so `byte[effect+3]=0` from a cross-import is safe). Installed into a stage
    /// whose effect-table type-0x17 slot is free so the imported swarm's coordination effect is processed
    /// instead of wild-calling. <c>[live-RE'd 040B + crash dump 22032, 2026-06-24]</c></summary>
    public const uint SwarmEffectHandlerVa = 0x00598805;

    /// <summary>
    /// Write <c>record[..]</c>'s category-<paramref name="category"/> AI-handler slot (at
    /// <c><paramref name="recordVa"/> + category*4</c>) to <paramref name="handlerVa"/> — the <b>surgical</b>
    /// cross-species lever: it points one category at a donor's handler without the collateral of
    /// <see cref="RepointSetupFn"/> (which swaps the stage's whole 4-pointer resource record). Safe only for
    /// a category the target stage does not itself host (a free/stub slot), else it hijacks that category's
    /// native enemies. Returns the previous slot value (for logging / reversal). Both VAs must be file-backed.
    /// </summary>
    public static uint SetRecordCategoryHandler(Span<byte> exe, uint recordVa, int category, uint handlerVa)
    {
        if (category is < 0 or > 31)
            throw new ArgumentOutOfRangeException(nameof(category), category, "category must be 0..31.");
        if (!IsFileBacked(handlerVa))
            throw new ArgumentOutOfRangeException(nameof(handlerVa),
                $"handler VA 0x{handlerVa:X} is not a file-backed code address.");
        int off = VaToFileOffset(recordVa + (uint)category * 4);
        uint previous = ReadUInt32(exe, off);
        WriteUInt32(exe, off, handlerVa);
        return previous;
    }

    // ---- Cat-8 hit/death descriptor redirect (defect B; docs/decisions/dc1/theri/THERI-0102-PLAYABLE-FIX-PLAN.md) ----

    /// <summary>VA of the cat-8 hit/death **reaction descriptor-pointer table** for the <c>0x15</c> path
    /// (read only by the cat-8 state machine <c>0x56DA87</c>; cat-8 is only the Theri). Entries are
    /// file-form RDT-base-relative pointers; only indices <c>0..4</c> are ever relocated/resolved
    /// (reloc loop <c>0x56D8C4</c>, bound <c>cmp 5</c>). <c>[verified from DINO.exe]</c></summary>
    public const uint Cat8HitTable15Va = 0x00663A10;

    /// <summary>VA of the cat-8 reaction descriptor table for the <c>0x17</c> path
    /// (<c>= 0x663A10 − 0x14</c>; reader <c>0x56DAC2</c>, reloc <c>0x56D863</c>). <c>[verified]</c></summary>
    public const uint Cat8HitTable17Va = 0x006639FC;

    /// <summary>VA of the EXE cave that holds the canonical hit/death descriptor records. Sits in the
    /// <c>.text</c> zero-slack (raw padding <c>0x61F792..0x620000</c>, after the on-disk nullguard cave);
    /// confirmed zero-filled and file-backed (RVA <c>0x21F900 &lt; 0x270000</c>). Being below
    /// <c>0x80000000</c> it is <b>non-file-form</b>, so both the table reloc (<c>0x56D8C4</c>) and
    /// <c>SpawnHitEffect</c> (<c>0x4611A5</c>) leave it verbatim. <c>[verified]</c></summary>
    public const uint HitDescriptorCaveVa = 0x0061F900;

    /// <summary>Exclusive VA bound of the <c>.text</c> raw region (<c>.rdata</c> begins here); the cave
    /// must not cross it. <c>[verified PE parse]</c></summary>
    public const uint TextRawEndVa = 0x00620000;

    /// <summary>Size of one descriptor record (scalar, pointer-free: pos x/y/z, <c>+0xe</c> bone idx,
    /// <c>+0x10</c> death-state, counts). <c>[verified]</c></summary>
    public const int HitDescriptorRecordSize = 0x14;

    /// <summary>Resolvable descriptor entries per table (indices <c>0..4</c>); the only ones the engine
    /// relocates. <c>[verified]</c></summary>
    public const int HitDescriptorIndexCount = 5;

    /// <summary>Total canonical records the redirect installs: indices <c>0..4</c> of both tables.</summary>
    public const int HitDescriptorTotalRecords = 2 * HitDescriptorIndexCount;

    // ---- Corrected defect-B fix (live-verified 2026-06-22): cat-8 hit-REACTION stream redirect ----
    // The live attack crash (AV 0x41685B) proved the reaction is descriptor-driven: cat-8 AI 0x56DA7A picks
    // descPtr = table[byte[ent+0x1cb]] -> SpawnHitEffect 0x4611A5 builds a 0x14-byte-record timeline ->
    // 0x4B077D walks the entity anim chain byte[desc+0xe] times via the no-NULL-guard walker 0x416845. The
    // dump-confirmed used index is 5 (0x17 path -> table entry 0x663A10[0] = RDT 0x3C0E0); the hit-type writer
    // (0x4BC09C) can also set index 4 (-> 0x663A10[4] / 0x6639FC[4]). Those RDT offsets hold valid descriptor
    // records ONLY in a normally-laid-out Theri room (st603); a cross-import (appended at the RDT tail) leaves
    // them garbage -> walk count 0xf0 -> overrun. Fix: cave st603's valid 0x3C0E0 reaction stream (records all
    // have walk-count 0..0xb -> walk-safe / reproduce native st603) and repoint the index-4/5 table slots to
    // it (non-file-form -> the engine's translate + SpawnHitEffect leave it verbatim; cat-8-exclusive).
    // Supersedes RedirectCat8HitDescriptors above (the st605 single-record model — wrong source + shape).
    // docs/decisions/dc1/theri/THERI-0102-PLAYABLE-FIX-PLAN.md Deviations.

    /// <summary>RDT offset of the canonical cat-8 hit-reaction descriptor stream in a normal Theri room
    /// (st603) — the target of table entry <c>0x663A10[0]</c> (file-form <c>0x8013C0E0</c>). <c>[verified]</c></summary>
    public const int Cat8ReactionDonorRdtOffset = 0x3C0E0;

    /// <summary>Bytes of the reaction stream to cave: <c>0x300</c> = 38 records, covering both the <c>0x15</c>
    /// (21-record) and <c>0x17</c> (23-record) reaction counts plus over-advance margin, within the cave
    /// window. A cursor that over-reads past it lands in the zero-slack (walk-count 0 ⇒ safe).</summary>
    public const int Cat8ReactionStreamBytes = 0x300;

    /// <summary>Low VA (inclusive) of the cat-8 reaction descriptor-table region — read by the cat-8 (Theri)
    /// AI as `[byte[ent+0x1cb]*4 + table]`. The cat-8 descriptors live in THREE clusters (80 file-form entries):
    /// cluster&#160;1 `0x6639FC`..`0x663B00` (readers `0x56Dxxx`..`0x56Fxxx`), cluster&#160;2 `0x663DE8`..`0x663DF8`
    /// (reader `0x572456`), cluster&#160;3 `0x6640D8`..`0x664138` (reader func `0x575836`, called from cat-8
    /// `0x570365`/`0x572B6F`). <c>[verified — whole-.text table-base scan + reader call-graph]</c></summary>
    public const uint Cat8ReactionTableLoVa = 0x006639FC;

    /// <summary>High VA (exclusive) of the cat-8 reaction-table region. The last cat-8 descriptor entry is
    /// `0x664138` (cluster&#160;3). The inter-cluster gaps are code-handler / position data (skipped — only
    /// file-form entries are repointed). <b>Stops at `0x6641EC`</b>, where the <i>cat-3 Tyrannosaurus</i> boss
    /// reaction tables begin (reader func `0x577445` = the cat-3 boss init, EXE-SYMBOLS) — those belong to the
    /// T-Rex swap and must NOT be touched. <c>[verified reader call-graph: 0x575836←cat-8, 0x577445←cat-3]</c></summary>
    public const uint Cat8ReactionTableHiVa = 0x006641EC;

    /// <summary>True when <paramref name="v"/> is a file-form RDT-base-relative descriptor pointer
    /// (<c>[0x80100000, 0x80200000)</c>) — the values repointed; <c>0x0056xxxx</c> code-handler entries
    /// (the tables' indices ≥ 5) are left untouched.</summary>
    private static bool IsRdtFileFormPtr(uint v) => v >= 0x80100000u && v < 0x80200000u;

    /// <summary>
    /// Corrected defect-B fix (comprehensive): write the cat-8 reaction descriptor <paramref name="stream"/>
    /// (a valid walk-safe record set from a normal Theri room at <see cref="Cat8ReactionDonorRdtOffset"/>) into
    /// the EXE cave and repoint <b>every file-form descriptor entry across all the cat-8 reaction tables</b>
    /// (<see cref="Cat8ReactionTableLoVa"/>..<see cref="Cat8ReactionTableHiVa"/>, two clusters) to it. The
    /// cat-8 hit/attack/death reactions are driven by these tables (one per reaction type / AI state, indexed
    /// by <c>byte[ent+0x1cb]</c>);
    /// a cross-import has appended garbage at every EXE-baked descriptor offset, so any reaction can overrun the
    /// no-NULL-guard walker <c>0x416845</c> (AV <c>0x41685B</c>) — fixing one table only moves the crash to the
    /// next. Pointing them all at one valid stream (<c>state ≤ 7</c>, walk-count 0 ⇒ safe for the walker AND the
    /// downstream anim-state table) makes every cat-8 reaction crash-safe. Cave VA is non-file-form, so the
    /// per-room reloc (<c>0x56D8C1</c>) and <c>SpawnHitEffect 0x4611A5</c> leave it verbatim; the tables are
    /// cat-8-exclusive so no other enemy is touched. <b>Note:</b> the tables are global ⇒ this also makes every
    /// <i>native</i> cat-8 Theri read the one stream (its hit/death/attack reactions become a single flinch);
    /// reversible by restoring the returned dwords. Code-handler entries (indices ≥ 5) are left untouched.
    /// Returns the previous values of the repointed entries, in ascending-VA order.
    /// </summary>
    public static uint[] RedirectCat8HitReaction(
        Span<byte> exe, ReadOnlySpan<byte> stream, uint caveVa = HitDescriptorCaveVa)
    {
        if (stream.Length == 0)
            throw new ArgumentException("reaction stream is empty.", nameof(stream));
        uint caveEnd = caveVa + (uint)stream.Length;
        if (caveVa < ImageBase || !IsFileBacked(caveVa) || caveEnd > TextRawEndVa)
            throw new ArgumentOutOfRangeException(nameof(caveVa),
                $"cave [0x{caveVa:X}, 0x{caveEnd:X}) must lie in the .text raw-slack window [{ImageBase:X}.., 0x{TextRawEndVa:X}).");
        if (caveVa >= 0x80000000)
            throw new ArgumentOutOfRangeException(nameof(caveVa), "cave VA must be < 0x80000000 (non-file-form).");

        int caveOff = VaToFileOffset(caveVa);
        // Clean-cave / idempotent guard: each target byte must be zero-slack or already the stream byte we write.
        for (int i = 0; i < stream.Length; i++)
            if (exe[caveOff + i] != 0 && exe[caveOff + i] != stream[i])
                throw new InvalidOperationException(
                    $"cave at VA 0x{caveVa:X} byte 0x{i:X} = 0x{exe[caveOff + i]:X2} is neither zero-slack nor the " +
                    $"intended stream byte 0x{stream[i]:X2}; refusing to overwrite (not a clean cave).");
        stream.CopyTo(exe.Slice(caveOff, stream.Length));

        // Repoint every file-form descriptor entry across all 9 tables; leave code-handler entries untouched.
        var previous = new List<uint>();
        for (uint va = Cat8ReactionTableLoVa; va < Cat8ReactionTableHiVa; va += 4)
        {
            int eoff = VaToFileOffset(va);
            uint cur = ReadUInt32(exe, eoff);
            if (!IsRdtFileFormPtr(cur) && cur != caveVa) continue; // skip code handlers / already-caved
            previous.Add(cur);
            WriteUInt32(exe, eoff, caveVa);
        }
        return previous.ToArray();
    }

    // ---- Universal walker NULL-guard (defect B, live-verified 2026-06-23) ----
    // The cat-8 Theri reaction system reads descriptor tables across MANY sets (the whole-.text scan found 214
    // enemy table groups). A cross-import has garbage at every EXE-baked descriptor offset, and ANY of them can
    // feed a garbage walk-count to the generic "+0x34 linked-list walker" 0x416845, which has NO NULL guard ⇒
    // it overruns the entity anim chain and derefs [NULL+0x34] → AV 0x41685B. Repointing tables is whack-a-mole
    // (the Theri hits several paths: cat-8 0x663xxx, the generic hit handler 0x4B5xxx reading 0x65D7AC, …). The
    // single universal fix: redirect 0x416845 to a NULL-guarded reimplementation in a code cave, so the walk
    // stops at the chain end and returns the last valid node instead of derefing NULL. Benign for every valid
    // caller (count ≤ chain length never reaches NULL ⇒ identical result); fixes every table/index/enemy at
    // once. LIVE-VERIFIED in CE: with this patch the imported 0102 Theri is killable with no crash. The cat-8
    // descriptor repoint above is kept too — it gives the cat-8 reactions VALID descriptors (state ≤ 7), so the
    // guard never has to engage on that path and no garbage state reaches the downstream anim-state table.

    /// <summary>VA of the generic <c>+0x34</c> linked-list walker (`node = *(node+0x34)`, count times; no NULL
    /// guard ⇒ AV <c>0x41685B</c> on overrun). Redirected to <see cref="WalkerCaveVa"/>. <c>[verified]</c></summary>
    public const uint WalkerVa = 0x00416845;

    /// <summary>VA of the NULL-guarded walker reimplementation cave — immediately after the reaction-descriptor
    /// cave (<see cref="HitDescriptorCaveVa"/> + <see cref="Cat8ReactionStreamBytes"/>); zero-slack, file-backed,
    /// inside the <c>.text</c> raw window. <c>[verified]</c></summary>
    public const uint WalkerCaveVa = HitDescriptorCaveVa + (uint)Cat8ReactionStreamBytes; // 0x61FC00

    /// <summary>The NULL-guarded walker (37 bytes): <c>push ebp; mov ebp,esp; loop { if(count==0) break;
    /// if(node==0) break; next=[node+0x34]; if(next==0) break; node=next; count--; } return node;</c> —
    /// equivalent to the original for valid counts, but stops at the chain end instead of derefing NULL.</summary>
    private static readonly byte[] NullGuardedWalker =
    {
        0x55,             // push ebp
        0x8B, 0xEC,       // mov  ebp, esp
        0x8B, 0x45, 0x0C, // loop: mov eax, [ebp+0xC]   (count)
        0x85, 0xC0,       // test eax, eax
        0x74, 0x16,       // jz   done
        0x8B, 0x55, 0x08, // mov  edx, [ebp+8]          (node)
        0x85, 0xD2,       // test edx, edx
        0x74, 0x0F,       // jz   done
        0x8B, 0x42, 0x34, // mov  eax, [edx+0x34]       (next)
        0x85, 0xC0,       // test eax, eax
        0x74, 0x08,       // jz   done
        0x89, 0x45, 0x08, // mov  [ebp+8], eax          (node = next)
        0xFF, 0x4D, 0x0C, // dec  dword [ebp+0xC]       (count--)
        0xEB, 0xE3,       // jmp  loop
        0x8B, 0x45, 0x08, // done: mov eax, [ebp+8]
        0x5D,             // pop  ebp
        0xC3,             // ret
    };

    /// <summary>
    /// Install the universal defect-B walker NULL-guard: write <see cref="NullGuardedWalker"/> into the cave at
    /// <see cref="WalkerCaveVa"/> and overwrite <see cref="WalkerVa"/> with a <c>jmp</c> to it, so every caller
    /// of the <c>+0x34</c> walker gets the NULL-safe version (callers <c>call 0x416845</c> → jmp cave → cave
    /// <c>ret</c>s to them). Idempotent (re-applying is a no-op) and reversible (returns the original 5 bytes at
    /// <see cref="WalkerVa"/>; <c>Restore</c> reverts via the pristine backup). Cat-8-independent and benign for
    /// all enemies — it only changes the overrun (crash → graceful stop), never a valid walk.
    /// </summary>
    public static byte[] InstallWalkerNullGuard(Span<byte> exe)
    {
        uint caveEnd = WalkerCaveVa + (uint)NullGuardedWalker.Length;
        if (!IsFileBacked(WalkerCaveVa) || caveEnd > TextRawEndVa)
            throw new ArgumentOutOfRangeException(nameof(WalkerCaveVa),
                $"walker cave [0x{WalkerCaveVa:X}, 0x{caveEnd:X}) must lie in the .text raw-slack window (.., 0x{TextRawEndVa:X}).");

        int caveOff = VaToFileOffset(WalkerCaveVa);
        for (int i = 0; i < NullGuardedWalker.Length; i++)
            if (exe[caveOff + i] != 0 && exe[caveOff + i] != NullGuardedWalker[i])
                throw new InvalidOperationException(
                    $"walker cave at 0x{WalkerCaveVa:X} byte 0x{i:X} = 0x{exe[caveOff + i]:X2} is neither zero-slack " +
                    $"nor the intended byte 0x{NullGuardedWalker[i]:X2}; refusing to overwrite.");
        NullGuardedWalker.CopyTo(exe.Slice(caveOff, NullGuardedWalker.Length));

        // jmp rel32 from WalkerVa to the cave.
        int jmpOff = VaToFileOffset(WalkerVa);
        var original = exe.Slice(jmpOff, 5).ToArray();
        int rel = (int)WalkerCaveVa - ((int)WalkerVa + 5);
        exe[jmpOff] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(exe.Slice(jmpOff + 1, 4), rel);
        return original;
    }

    // ---- Render-model display-node guard (060F elevator / 0511 cross-stage; docs/decisions/dc1/crash-rcas/ELEVATOR-060F-HANDGUN-CRASH.md) ----
    // The per-frame object-transform pass sub_44CED6 walks the display-object work array (base 0x6DBB28,
    // stride 0x88) and, for each active node, dereferences the model/animation header pointer at node+0x3C
    // (0x44D130: `mov ecx,[eax]`, eax = p = node+0x3C). A node whose model resource is not resident has an
    // INVALID p there and the read AVs. Two captured forms (logs/crash-logs-handgun/): p == 0 (NULL, the 0511
    // signature) and p == an un-relocated PSX file-form pointer (0x8017E000, the deterministic 060F-re-entry
    // crash). The original on-disk nullguard (tools/scd_re/apply_nullguard.py) hooks 0x44D124 and skips only
    // when p == 0 — so the file-form form sails straight through it and still crashes. This widened guard
    // (ported into the shipped patcher, superseding that tool) skips a node when p is NULL OR a PSX file-form
    // pointer ([0x80000000,0x80200000) — the same range the in-function check at 0x44D135 uses), mirroring the
    // existing node+0x40==0 skip. Benign/universal: a valid heap header (< 0x80000000) takes the normal deref
    // path and renders unchanged; only an unmapped/un-relocated header is skipped (drawn-skip, not a crash).

    /// <summary>VA of the hook site in <c>sub_44CED6</c> — the 12 bytes that load <c>obj</c> and
    /// <c>obj+0x3C</c> before the model-header deref. Replaced with a <c>jmp</c> to
    /// <see cref="RenderGuardCaveVa"/> + 7 <c>nop</c>s. <c>[verified disasm]</c></summary>
    public const uint RenderTransformHookVa = 0x0044D124;

    /// <summary>VA of the faulting deref <c>0044D130: 8B 08 mov ecx,[eax]</c> (<c>eax = p = node+0x3C</c>) —
    /// the normal-path target the cave jumps back to when <c>p</c> is a valid heap header. <c>[verified — all
    /// three crash dumps fault here]</c></summary>
    public const uint RenderModelDerefVa = 0x0044D130;

    /// <summary>VA of the per-slot loop-continue (the existing skip target inside <c>sub_44CED6</c>); the cave
    /// jumps here to drop a model-less / un-relocated node without drawing it. <c>[verified]</c></summary>
    public const uint RenderLoopContinueVa = 0x0044D516;

    /// <summary>VA of the render-model guard cave, in the <c>.text</c> zero-slack just past real code
    /// (<c>0x61F792</c>) and <b>before</b> the descriptor cave (<see cref="HitDescriptorCaveVa"/> =
    /// <c>0x61F900</c>). Same site the deprecated <c>apply_nullguard.py</c> used. <c>[verified PE parse]</c></summary>
    public const uint RenderGuardCaveVa = 0x0061F7A0;

    /// <summary>The 12 original bytes at <see cref="RenderTransformHookVa"/> the cave reproduces
    /// (<c>mov ecx,[ebp-0x10]; mov edx,[ecx+0x3C]; mov [ebp-0x04],edx; mov eax,[ebp-0x04]</c>); also the
    /// pristine-state fingerprint the installer expects before hooking. <c>[verified disasm]</c></summary>
    public static readonly byte[] RenderGuardOriginalHook =
        { 0x8B, 0x4D, 0xF0, 0x8B, 0x51, 0x3C, 0x89, 0x55, 0xFC, 0x8B, 0x45, 0xFC };

    /// <summary>Low bound (inclusive) of the PSX file-form pointer range an un-relocated model header carries —
    /// the same range the in-function check at <c>0x44D135</c> uses. A <c>node+0x3C</c> here is unmapped in the
    /// Win32 image and AVs if dereferenced (the deterministic 060F re-entry crash, <c>p = 0x8017E000</c>).</summary>
    public const uint PsxFileFormLo = 0x80000000;

    /// <summary>High bound (exclusive) of the PSX file-form pointer range (<c>0x80200000</c>).</summary>
    public const uint PsxFileFormHi = 0x80200000;

    /// <summary>Build the guard cave for a given cave VA (the two tail <c>jmp rel32</c>s depend on it).
    /// Skips the node when <c>node+0x3C</c> is NULL <b>or</b> a PSX file-form pointer
    /// (<see cref="PsxFileFormLo"/>..<see cref="PsxFileFormHi"/>); a valid heap header takes the normal
    /// deref/render path. Widens the deprecated <c>apply_nullguard.py</c> cave (<c>==0</c> only), which the
    /// file-form 060F crash sailed through.</summary>
    private static byte[] BuildRenderModelGuardCave(uint caveVa)
    {
        var cave = new byte[]
        {
            0x8B, 0x4D, 0xF0,                   // 00: mov ecx,[ebp-0x10]      ; obj
            0x8B, 0x51, 0x3C,                   // 03: mov edx,[ecx+0x3C]      ; p = node->modelHdr
            0x85, 0xD2,                         // 06: test edx,edx
            0x74, 0x1B,                         // 08: jz  SKIP (+0x1B -> 0x25); p==0 -> skip
            0x81, 0xFA, 0x00, 0x00, 0x00, 0x80, // 0A: cmp edx,0x80000000
            0x72, 0x08,                         // 10: jb  DEREF(+0x08 -> 0x1A); p<0x80000000 -> heap, deref
            0x81, 0xFA, 0x00, 0x00, 0x20, 0x80, // 12: cmp edx,0x80200000
            0x72, 0x0B,                         // 18: jb  SKIP (+0x0B -> 0x25); file-form -> skip
            0x89, 0x55, 0xFC,                   // 1A: DEREF: mov [ebp-0x04],edx
            0x8B, 0x45, 0xFC,                   // 1D: mov eax,[ebp-0x04]      ; eax = p
            0xE9, 0, 0, 0, 0,                   // 20: jmp RenderModelDerefVa
            0xE9, 0, 0, 0, 0,                   // 25: SKIP: jmp RenderLoopContinueVa
        };                                      // 2A: end (42 bytes)
        // imm32s already encode PsxFileFormLo/Hi; assert so a constant change can't silently desync the bytes.
        System.Diagnostics.Debug.Assert(
            BinaryPrimitives.ReadUInt32LittleEndian(cave.AsSpan(0x0C, 4)) == PsxFileFormLo &&
            BinaryPrimitives.ReadUInt32LittleEndian(cave.AsSpan(0x14, 4)) == PsxFileFormHi);
        BinaryPrimitives.WriteInt32LittleEndian(cave.AsSpan(0x21, 4),
            (int)RenderModelDerefVa - (int)(caveVa + 0x25));
        BinaryPrimitives.WriteInt32LittleEndian(cave.AsSpan(0x26, 4),
            (int)RenderLoopContinueVa - (int)(caveVa + 0x2A));
        return cave;
    }

    /// <summary>
    /// Install the render-model display-node guard: write <see cref="BuildRenderModelGuardCave"/> into the cave
    /// at <see cref="RenderGuardCaveVa"/> and hook <see cref="RenderTransformHookVa"/> with a <c>jmp</c> to it
    /// (+ 7 <c>nop</c>s), so the per-frame transform pass skips a display node whose model header (<c>node+0x3C</c>)
    /// is invalid instead of dereferencing it. Returns the 12 original hook bytes (for reversal). Idempotent
    /// (re-applying writes the same bytes) and reversed by the installer's pristine backup. Universal and benign —
    /// a valid heap header renders unchanged.
    /// </summary>
    public static byte[] InstallRenderModelGuard(Span<byte> exe)
    {
        byte[] cave = BuildRenderModelGuardCave(RenderGuardCaveVa);
        uint caveEnd = RenderGuardCaveVa + (uint)cave.Length;
        // The render-guard cave must sit in the .text raw-slack window and not run into the descriptor cave.
        if (!IsFileBacked(RenderGuardCaveVa) || caveEnd > HitDescriptorCaveVa)
            throw new ArgumentOutOfRangeException(nameof(RenderGuardCaveVa),
                $"render-guard cave [0x{RenderGuardCaveVa:X}, 0x{caveEnd:X}) must lie in .text slack before the " +
                $"descriptor cave (0x{HitDescriptorCaveVa:X}).");

        int caveOff = VaToFileOffset(RenderGuardCaveVa);
        for (int i = 0; i < cave.Length; i++)
            if (exe[caveOff + i] != 0 && exe[caveOff + i] != cave[i])
                throw new InvalidOperationException(
                    $"render-guard cave at 0x{RenderGuardCaveVa:X} byte 0x{i:X} = 0x{exe[caveOff + i]:X2} is neither " +
                    $"zero-slack nor the intended byte 0x{cave[i]:X2}; refusing to overwrite (revert a prior/narrow " +
                    "guard or restore the pristine exe first).");
        cave.CopyTo(exe.Slice(caveOff, cave.Length));

        // Hook: jmp rel32 to the cave + 7 nops (fills the 12-byte original instruction block).
        int hookOff = VaToFileOffset(RenderTransformHookVa);
        var original = exe.Slice(hookOff, RenderGuardOriginalHook.Length).ToArray();
        int rel = (int)RenderGuardCaveVa - ((int)RenderTransformHookVa + 5);
        exe[hookOff] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(exe.Slice(hookOff + 1, 4), rel);
        for (int i = 5; i < RenderGuardOriginalHook.Length; i++) exe[hookOff + i] = 0x90;
        return original;
    }

    /// <summary>
    /// Fix defect B: copy the canonical cat-8 hit/death descriptor records into the EXE cave and repoint
    /// <see cref="Cat8HitTable17Va"/><c>[0..4]</c> then <see cref="Cat8HitTable15Va"/><c>[0..4]</c> to the
    /// cave, so the Theri's hit/death reactions resolve to valid (pointer-free, RDT-independent) data in
    /// <i>every</i> room — fixing the <c>0x4B0794</c>/<c>0x45dc0e</c> AVs at the source without touching any
    /// other enemy (the tables are cat-8-exclusive).
    /// <para><paramref name="records"/> must be exactly <see cref="HitDescriptorTotalRecords"/> ×
    /// <see cref="HitDescriptorRecordSize"/> bytes: the five <c>0x17</c>-table records (indices 0..4) then
    /// the five <c>0x15</c>-table records — extracted verbatim from the canonical st605 RDT (the order the
    /// table dwords are repointed in).</para>
    /// Returns the previous 10 table dwords (table17[0..4] then table15[0..4]) for logging / reversal.
    /// </summary>
    public static uint[] RedirectCat8HitDescriptors(
        Span<byte> exe, ReadOnlySpan<byte> records, uint caveVa = HitDescriptorCaveVa)
    {
        int expected = HitDescriptorTotalRecords * HitDescriptorRecordSize;
        if (records.Length != expected)
            throw new ArgumentException(
                $"expected {expected} bytes ({HitDescriptorTotalRecords} × 0x{HitDescriptorRecordSize:X} records), got {records.Length}.",
                nameof(records));
        uint caveEnd = caveVa + (uint)expected;
        if (caveVa < ImageBase || !IsFileBacked(caveVa) || caveEnd > TextRawEndVa)
            throw new ArgumentOutOfRangeException(nameof(caveVa),
                $"cave [0x{caveVa:X}, 0x{caveEnd:X}) must lie in the .text raw-slack window [{ImageBase:X}.., 0x{TextRawEndVa:X}).");
        // A non-file-form sentinel is required so the engine's file-form relocations leave it verbatim.
        if (caveVa >= 0x80000000)
            throw new ArgumentOutOfRangeException(nameof(caveVa), "cave VA must be < 0x80000000 (non-file-form).");
        // Safety: never write over real code — each cave byte must be zero-slack, or already equal the
        // record byte we are about to write (so re-applying the patch on a live exe is idempotent; real
        // code can't coincidentally equal all 200 record bytes).
        int caveOff = VaToFileOffset(caveVa);
        for (int i = 0; i < expected; i++)
            if (exe[caveOff + i] != 0 && exe[caveOff + i] != records[i])
                throw new InvalidOperationException(
                    $"cave at VA 0x{caveVa:X} byte 0x{i:X} = 0x{exe[caveOff + i]:X2} is neither zero-slack nor the " +
                    $"intended record byte 0x{records[i]:X2}; refusing to overwrite (not a clean cave).");

        // Lay the records into the cave.
        records.CopyTo(exe.Slice(caveOff, expected));

        // Repoint the 10 table dwords: table17[0..4] then table15[0..4], matching the records order.
        var previous = new uint[HitDescriptorTotalRecords];
        int r = 0;
        foreach (uint tableVa in new[] { Cat8HitTable17Va, Cat8HitTable15Va })
            for (int i = 0; i < HitDescriptorIndexCount; i++, r++)
            {
                uint entryVa = tableVa + (uint)i * 4;
                uint recordVa = caveVa + (uint)r * (uint)HitDescriptorRecordSize;
                int eoff = VaToFileOffset(entryVa);
                previous[r] = ReadUInt32(exe, eoff);
                WriteUInt32(exe, eoff, recordVa);
            }
        return previous;
    }

    // ---- Per-room enemy SE (sound-effect) bank redirect (docs/reference/dc1/se/ENEMY-SOUND-SYSTEM.md) ----
    // The PC build loads each room's enemy SFX from a per-room SE manifest in DINO.exe: an array of
    // 16-byte records { u32 id; u32 flags; char* namePtr; u32 _ } grouped one BLOCK per room, selected
    // by a room->block directory at 0x63A470 indexed (stage*32 + room) (loader 0x4053D0). The block
    // loader 0x404450 walks a block until namePtr(+8)==0, loading each named WAV (Sound\SE\<name>.dat)
    // into an SE node that stores the record's id(+0) — so playback is **id-keyed** (the AI/motion emit
    // an id; the engine plays the node whose id matches). The dino SE set a room loads is baked per room:
    // a default room (e.g. st0102) carries the Velociraptor set (se\dino\r_*), so after a cross-species
    // swap the room still loads raptor samples and any enemy plays raptor SE. The fix copies a NATIVE
    // target-species room's contiguous dino sub-block (id+name pairs, verbatim) over the swapped room's
    // dino records and early-terminates — so the room loads exactly the target species' set at exactly
    // the ids its AI emits, footprint-faithful to a native room. Cross-species only (an in-category
    // permute keeps the species, so its SE is already correct). [decoded ENEMY-SOUND-SYSTEM]

    /// <summary>VA of the room→SE-block directory; <c>block = *(SeDirectoryBaseVa + (stage*32+room)*4)</c>
    /// (loader <c>0x4053D0</c>, <c>stage = roomId&gt;&gt;8</c>, <c>room = roomId&amp;0xFF</c>). <c>[verified disasm]</c></summary>
    public const uint SeDirectoryBaseVa = 0x0063A470;

    /// <summary>Low VA (inclusive) of the SE manifest table (the room blocks). <c>[verified]</c></summary>
    public const uint SeManifestLoVa = 0x0062F320;

    /// <summary>High VA (exclusive) of the SE manifest table — equals the directory base (the directory
    /// immediately follows the blocks). <c>[verified]</c></summary>
    public const uint SeManifestHiVa = SeDirectoryBaseVa;

    /// <summary>Stride of one SE manifest record: <c>{ u32 id; u32 flags; char* namePtr; u32 _ }</c>.</summary>
    public const int SeRecordStride = 0x10;

    /// <summary>Offset of the id field within an SE record (the bank-lookup key). <c>[verified]</c></summary>
    public const int SeRecordIdOffset = 0x00;

    /// <summary>Offset of the name-pointer field (the WAV path; <c>0</c> terminates a block). <c>[verified]</c></summary>
    public const int SeRecordNameOffset = 0x08;

    /// <summary>Path prefix marking an enemy (dino) SE record (the records the retarget replaces).</summary>
    public const string SeDinoPrefix = @"se\dino\";

    /// <summary>VA of the SE manifest block for a room: <c>*(SeDirectoryBaseVa + (stage*32+room)*4)</c>.</summary>
    public static uint SeBlockVa(ReadOnlySpan<byte> exe, int stage, int room)
    {
        if (stage < 0) throw new ArgumentOutOfRangeException(nameof(stage), stage, "stage must be ≥ 0.");
        if (room is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(room), room, "room must be 0..31.");
        return ReadUInt32AtVa(exe, SeDirectoryBaseVa + (uint)(stage * 32 + room) * 4);
    }

    /// <summary>Read a NUL-terminated ASCII string at a VA, or <c>null</c> if not file-backed, unterminated
    /// within <paramref name="maxLen"/>, or empty.</summary>
    public static string? ReadCStringAtVa(ReadOnlySpan<byte> exe, uint va, int maxLen = 64)
    {
        if (!IsFileBacked(va)) return null;
        int off = VaToFileOffset(va);
        int end = off;
        while (end < exe.Length && end - off < maxLen && exe[end] != 0) end++;
        if (end >= exe.Length || end - off >= maxLen || end == off) return null;
        return System.Text.Encoding.ASCII.GetString(exe.Slice(off, end - off));
    }

    /// <summary>True when <paramref name="blockVa"/> is a plausible SE manifest block start (file-backed
    /// and inside the table region).</summary>
    private static bool IsSeBlockVa(uint blockVa)
        => IsFileBacked(blockVa) && blockVa >= SeManifestLoVa && blockVa < SeManifestHiVa;

    /// <summary>
    /// Extract the contiguous dino (<c>se\dino\</c>) sub-block of a donor room's SE manifest block as raw
    /// 16-byte records (id+flags+namePtr+_), verbatim. The donor must be a room that natively hosts the
    /// target species (e.g. a Theri room st603/st605). Returns empty if the room has no dino SE records.
    /// </summary>
    public static byte[] ExtractRoomDinoSubBlock(ReadOnlySpan<byte> exe, int stage, int room)
    {
        uint blockVa = SeBlockVa(exe, stage, room);
        if (!IsSeBlockVa(blockVa))
            throw new InvalidOperationException(
                $"donor SE block for st{stage}{room:X2} (VA 0x{blockVa:X}) is outside the manifest table " +
                $"[0x{SeManifestLoVa:X},0x{SeManifestHiVa:X}) — unexpected build/locale.");
        uint startVa = 0; int count = 0;
        for (uint va = blockVa; ; va += (uint)SeRecordStride)
        {
            uint namePtr = ReadUInt32AtVa(exe, va + (uint)SeRecordNameOffset);
            if (namePtr == 0) break; // block terminator
            string? nm = ReadCStringAtVa(exe, namePtr);
            bool dino = nm != null && nm.StartsWith(SeDinoPrefix, StringComparison.OrdinalIgnoreCase);
            if (dino) { if (startVa == 0) startVa = va; count++; }
            else if (startVa != 0) break; // dino run ended (dino records sit at the block tail)
        }
        if (startVa == 0) return Array.Empty<byte>();
        return exe.Slice(VaToFileOffset(startVa), count * SeRecordStride).ToArray();
    }

    /// <summary>Outcome of <see cref="RetargetRoomDinoSe"/>: how many records were written and the room's
    /// dino-record capacity (for logging).</summary>
    public readonly record struct SeRetargetResult(uint TargetBlockVa, int RecordsWritten, int Capacity);

    /// <summary>
    /// Make room (<paramref name="stage"/>,<paramref name="room"/>) load the target species' enemy SE by
    /// overwriting its contiguous dino sub-block with <paramref name="donorSubBlock"/> (a native
    /// target-species sub-block from <see cref="ExtractRoomDinoSubBlock"/>) and early-terminating the block
    /// (<c>namePtr=0</c>) right after the copied records — so the loader installs exactly the donor's
    /// id→WAV set and no original (e.g. raptor) sample loads. The donor count must fit the room's dino
    /// capacity. Idempotent (re-applying writes the same bytes; the prior terminator caps capacity to the
    /// donor count). Returns a summary.
    /// </summary>
    public static SeRetargetResult RetargetRoomDinoSe(Span<byte> exe, int stage, int room, ReadOnlySpan<byte> donorSubBlock)
    {
        if (donorSubBlock.Length == 0 || donorSubBlock.Length % SeRecordStride != 0)
            throw new ArgumentException(
                $"donor sub-block must be a non-empty multiple of 0x{SeRecordStride:X} bytes; got {donorSubBlock.Length}.",
                nameof(donorSubBlock));
        int donorCount = donorSubBlock.Length / SeRecordStride;

        uint blockVa = SeBlockVa(exe, stage, room);
        if (!IsSeBlockVa(blockVa))
            throw new InvalidOperationException(
                $"room st{stage}{room:X2} SE block (VA 0x{blockVa:X}) is outside the manifest table " +
                $"[0x{SeManifestLoVa:X},0x{SeManifestHiVa:X}) — unexpected build/locale.");

        // Find the room's contiguous dino sub-block (start + capacity).
        uint dinoStartVa = 0; int capacity = 0;
        for (uint va = blockVa; ; va += (uint)SeRecordStride)
        {
            uint namePtr = ReadUInt32AtVa(exe, va + (uint)SeRecordNameOffset);
            if (namePtr == 0) break;
            string? nm = ReadCStringAtVa(exe, namePtr);
            bool dino = nm != null && nm.StartsWith(SeDinoPrefix, StringComparison.OrdinalIgnoreCase);
            if (dino) { if (dinoStartVa == 0) dinoStartVa = va; capacity++; }
            else if (dinoStartVa != 0) break;
        }
        if (dinoStartVa == 0)
            throw new InvalidOperationException(
                $"room st{stage}{room:X2} has no enemy (se\\dino\\) SE records to retarget.");
        if (donorCount > capacity)
            throw new InvalidOperationException(
                $"donor SE sub-block ({donorCount} records) exceeds room st{stage}{room:X2} dino capacity ({capacity}).");

        int dinoStartOff = VaToFileOffset(dinoStartVa);
        donorSubBlock.CopyTo(exe.Slice(dinoStartOff, donorSubBlock.Length));

        // Early-terminate: zero the namePtr of the record right after the copied sub-block so the loader
        // stops there (the residual raptor records + the original pad after it are left unread).
        if (donorCount < capacity)
        {
            uint termVa = dinoStartVa + (uint)(donorCount * SeRecordStride) + (uint)SeRecordNameOffset;
            WriteUInt32(exe, VaToFileOffset(termVa), 0);
        }
        return new SeRetargetResult(blockVa, donorCount, capacity);
    }

    // ---- BGM catalog shuffle (the music randomizer lever; docs/reference/dc1/bgm/BGM-SYSTEM.md §4) ----
    // DC1 resolves every BGM request through one global id→file catalog in DINO.exe: 99 × 16-byte records
    // { char* namePtr@+0; u32 size@+4; u32 id@+8; u32 flags@+0xC }, id-indexed (record(id) = base + (id-1)*16).
    // id 1 is a "2231325" version tag (inline, not a bgm\ pointer); ids 2..99 each point at a bgm\<name> string.
    // The catalog is consumed at GAME INIT (the resource registry 0x6240F0 type-1 entry caches a pointer into
    // this live table; a runtime memory edit does NOT reroute play-time tracks — LIVE-VERIFIED 2026-06-23: with
    // all 98 namePtrs overwritten in the running process, a fresh room still played its own track). So the lever
    // is an ON-DISK patch read at the next launch, exactly the SE-fix shape (no selection decode needed —
    // it reroutes whatever id any room/scene requests). We permute each record's {namePtr,size} WITHIN its
    // `flags` class (keeps the stream/loop behaviour correct) and leave id 1 + the post-99 SE rows untouched.

    /// <summary>VA of <c>record[0]</c> (id 1) of the BGM catalog table; file <c>0x225438</c>. <c>[verified]</c></summary>
    public const uint BgmCatalogBaseVa = 0x00625438;

    /// <summary>Stride of one BGM catalog record. Layout: <c>+0</c> <c>char* namePtr</c>, <c>+4</c> <c>u32 size</c>,
    /// <c>+8</c> <c>u32 id</c>, <c>+0xC</c> <c>u32 flags</c>. <c>[verified]</c></summary>
    public const int BgmRecordStride = 0x10;

    /// <summary>Number of catalog records (ids 1..99); after id 99 the region continues into unrelated SE rows
    /// that must not be touched. <c>[verified]</c></summary>
    public const int BgmRecordCount = 99;

    /// <summary>First id the shuffle moves: id 1 is the <c>"2231325"</c> version tag (no <c>bgm\</c> pointer) and
    /// is left in place, so the permutation runs over ids <see cref="BgmFirstShuffledId"/>..<see cref="BgmRecordCount"/>.</summary>
    public const int BgmFirstShuffledId = 2;

    private const int BgmNamePtrOffset = 0x00;
    private const int BgmSizeOffset = 0x04;
    private const int BgmIdOffset = 0x08;
    private const int BgmFlagsOffset = 0x0C;

    /// <summary>Path prefix every shuffled catalog name carries (e.g. <c>bgm\me_00SL</c>) — the validation key
    /// that the table is the expected build/locale before any record is moved.</summary>
    public const string BgmNamePrefix = @"bgm\";

    /// <summary>VA of BGM catalog <c>record(id)</c> for <paramref name="id"/> in 1..99.</summary>
    public static uint BgmRecordVa(int id)
    {
        if (id is < 1 or > BgmRecordCount)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"BGM id must be 1..{BgmRecordCount}.");
        return BgmCatalogBaseVa + (uint)(id - 1) * BgmRecordStride;
    }

    /// <summary>One catalog record's identity, plus the <c>{namePtr,size}</c> pair before and after a shuffle.
    /// <see cref="OldNamePtr"/>/<see cref="NewNamePtr"/> are equal for a record the permutation left in place.</summary>
    public readonly record struct BgmShuffleEntry(
        int Id, uint Flags, uint OldNamePtr, uint OldSize, uint NewNamePtr, uint NewSize, string? OldName, string? NewName);

    /// <summary>
    /// Shuffle the BGM catalog (the music randomizer lever): permute each record's <c>{namePtr,size}</c> pair
    /// <b>within its <c>flags</c> class</c></b> (so stream/loop behaviour stays correct), keyed by
    /// <paramref name="seed"/> — deterministic, so the same seed on the same pristine exe yields byte-identical
    /// output. id 1 (the version tag) and the post-99 SE rows are left untouched; the <c>id</c> and <c>flags</c>
    /// fields stay in place (only the name/size move), so any room still requests the same id but it now streams a
    /// different file. Validates the table shape first (each id field == its index+1, each name a <c>bgm\</c>
    /// string) and throws <see cref="InvalidOperationException"/> for an unexpected build/locale. Returns one
    /// <see cref="BgmShuffleEntry"/> per shuffled id (ascending), the old/new pairs (for logging / reversal).
    /// Reversal is normally via the installer's pristine backup (<c>Restore</c>), like every other EXE patch.
    /// </summary>
    public static BgmShuffleEntry[] ShuffleBgmCatalog(Span<byte> exe, int seed)
    {
        // Validate + snapshot the records we will move (ids 2..99).
        int n = BgmRecordCount - (BgmFirstShuffledId - 1);
        var ids = new int[n];
        var flags = new uint[n];
        var namePtr = new uint[n];
        var size = new uint[n];
        var name = new string?[n];
        for (int k = 0; k < n; k++)
        {
            int id = BgmFirstShuffledId + k;
            int off = VaToFileOffset(BgmRecordVa(id));
            uint recId = ReadUInt32(exe, off + BgmIdOffset);
            if (recId != (uint)id)
                throw new InvalidOperationException(
                    $"BGM catalog record {id} has id field 0x{recId:X} (expected {id}) — unexpected build/locale; refusing to shuffle.");
            uint np = ReadUInt32(exe, off + BgmNamePtrOffset);
            string? nm = ReadCStringAtVa(exe, np);
            if (nm is null || !nm.StartsWith(BgmNamePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"BGM catalog record {id} namePtr 0x{np:X} does not resolve to a '{BgmNamePrefix}' string " +
                    "— unexpected build/locale; refusing to shuffle.");
            ids[k] = id;
            flags[k] = ReadUInt32(exe, off + BgmFlagsOffset);
            namePtr[k] = np;
            size[k] = ReadUInt32(exe, off + BgmSizeOffset);
            name[k] = nm;
        }

        // Group record indices by flags class, then permute the {namePtr,size} pairs within each class.
        // Classes are processed in ascending flags order, members in ascending id order, so the result is a
        // pure deterministic function of (seed, table) — independent of dictionary iteration order.
        uint rng = (uint)seed;
        var newNamePtr = (uint[])namePtr.Clone();
        var newSize = (uint[])size.Clone();
        var newName = (string?[])name.Clone();
        foreach (uint cls in flags.Distinct().OrderBy(f => f))
        {
            var members = new List<int>();
            for (int k = 0; k < n; k++) if (flags[k] == cls) members.Add(k);
            // Fisher–Yates over the member slots: pick a source pair for each destination slot.
            int m = members.Count;
            var perm = new int[m];
            for (int i = 0; i < m; i++) perm[i] = i;
            for (int i = m - 1; i > 0; i--)
            {
                int j = (int)(NextRand(ref rng) % (uint)(i + 1));
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }
            for (int i = 0; i < m; i++)
            {
                int dst = members[i], src = members[perm[i]];
                newNamePtr[dst] = namePtr[src];
                newSize[dst] = size[src];
                newName[dst] = name[src];
            }
        }

        // Write back the permuted pairs and build the change list.
        var result = new BgmShuffleEntry[n];
        for (int k = 0; k < n; k++)
        {
            int off = VaToFileOffset(BgmRecordVa(ids[k]));
            WriteUInt32(exe, off + BgmNamePtrOffset, newNamePtr[k]);
            WriteUInt32(exe, off + BgmSizeOffset, newSize[k]);
            result[k] = new BgmShuffleEntry(ids[k], flags[k], namePtr[k], size[k], newNamePtr[k], newSize[k], name[k], newName[k]);
        }
        return result;
    }

    /// <summary>Deterministic splitmix32 step — a stable PRNG for the catalog shuffle (independent of
    /// <see cref="Random"/>'s cross-runtime behaviour, so a seed reproduces byte-identically).</summary>
    private static uint NextRand(ref uint state)
    {
        state += 0x9E3779B9u;
        uint z = state;
        z = (z ^ (z >> 16)) * 0x21F0AAADu;
        z = (z ^ (z >> 15)) * 0x735A2D97u;
        return z ^ (z >> 15);
    }

    // ---- Emergency-box contents (the box-content randomizer lever) ----
    // The live box-contents table in DINO.exe (docs/reference/dc1/items/EMERGENCY-BOX-DATA.md, EXE-SYMBOLS 0x65AC05).
    // Each box = a 21-byte record [u8 slotCount=0x0A][≤10 (itemId,count) byte pairs][zero pad]. Boxes are
    // grouped into per-(region,difficulty) blocks of 17 records. The three INTERNATIONAL difficulty blocks
    // below were located + validated against data/dc1/emergency-boxes.json (every box's content multiset
    // matched the binary verbatim). Easy and Normal share one block (identical contents in this build).

    /// <summary>VA of the International Easy/Normal emergency-box block (17 × 21-byte records).</summary>
    public const uint EmergencyBoxBlockEasyVa = 0x0065ACB0;
    /// <summary>VA of the International Hard emergency-box block.</summary>
    public const uint EmergencyBoxBlockHardVa = 0x0065B0E8;
    /// <summary>VA of the International Very Hard emergency-box block.</summary>
    public const uint EmergencyBoxBlockVeryHardVa = 0x0065B3B8;

    /// <summary>Size of one emergency-box record: <c>1</c> slot-count byte + <c>10×2</c> (id,count) pairs.</summary>
    public const int EmergencyBoxRecordStride = 21;
    /// <summary>Boxes (records) per difficulty block.</summary>
    public const int EmergencyBoxesPerBlock = 17;
    /// <summary>Leading byte of every box record — the slot count (10 slots/box). Used to validate the build.</summary>
    public const byte EmergencyBoxSlotMarker = 0x0A;

    /// <summary>The International difficulty blocks the shuffle operates on, ascending VA (the order the
    /// permutation stream walks — so the result is a pure function of the seed + table).</summary>
    public static readonly uint[] EmergencyBoxBlockVas =
        { EmergencyBoxBlockEasyVa, EmergencyBoxBlockHardVa, EmergencyBoxBlockVeryHardVa };

    /// <summary>One shuffled box slot: where it sits and which slot's contents it now holds.</summary>
    public readonly record struct EmergencyBoxShuffleEntry(uint BlockVa, int Slot, int SourceSlot);

    /// <summary>
    /// Shuffle emergency-box contents (the box-content randomizer lever): within each International
    /// difficulty block, permute the 17 fixed-size box records among the boxes, keyed by
    /// <paramref name="seed"/> — deterministic, so the same seed on the same pristine exe yields
    /// byte-identical output. The permutation moves whole, game-authored records (each stays a valid box
    /// with valid items and difficulty-appropriate amounts), so it is safe by construction; it only changes
    /// <i>which</i> box holds which loot. Blocks are processed in ascending VA over one PRNG stream.
    /// Validates each record's slot-count marker first and throws <see cref="InvalidOperationException"/>
    /// for an unexpected build/locale. Reversal is via the installer's pristine backup, like every EXE
    /// patch. Returns one entry per moved slot (for logging). docs/reference/dc1/items/EMERGENCY-BOX-DATA.md.
    /// </summary>
    public static EmergencyBoxShuffleEntry[] ShuffleEmergencyBoxContents(Span<byte> exe, int seed)
    {
        uint rng = (uint)seed;
        var result = new List<EmergencyBoxShuffleEntry>(EmergencyBoxBlockVas.Length * EmergencyBoxesPerBlock);
        var scratch = new byte[EmergencyBoxesPerBlock * EmergencyBoxRecordStride];

        foreach (uint blockVa in EmergencyBoxBlockVas)
        {
            int blockOff = VaToFileOffset(blockVa);
            CheckBounds(exe.Length, blockOff, EmergencyBoxesPerBlock * EmergencyBoxRecordStride);

            // Validate the table shape: every record must lead with the 10-slot marker. A mismatch means
            // the offsets do not point at the box table (wrong build/locale) — refuse rather than corrupt.
            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
                if (exe[blockOff + i * EmergencyBoxRecordStride] != EmergencyBoxSlotMarker)
                    throw new InvalidOperationException(
                        $"Emergency-box block 0x{blockVa:X} record {i} does not start with the 0x{EmergencyBoxSlotMarker:X2} " +
                        "slot marker — unexpected build/locale; refusing to shuffle.");

            // Snapshot the 17 records, then Fisher–Yates a permutation and write each destination from its
            // source snapshot (records are equal-size, so this is a pure reorder — multiset preserved).
            exe.Slice(blockOff, scratch.Length).CopyTo(scratch);
            var perm = new int[EmergencyBoxesPerBlock];
            for (int i = 0; i < EmergencyBoxesPerBlock; i++) perm[i] = i;
            for (int i = EmergencyBoxesPerBlock - 1; i > 0; i--)
            {
                int j = (int)(NextRand(ref rng) % (uint)(i + 1));
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }
            for (int dst = 0; dst < EmergencyBoxesPerBlock; dst++)
            {
                int src = perm[dst];
                scratch.AsSpan(src * EmergencyBoxRecordStride, EmergencyBoxRecordStride)
                       .CopyTo(exe.Slice(blockOff + dst * EmergencyBoxRecordStride, EmergencyBoxRecordStride));
                result.Add(new EmergencyBoxShuffleEntry(blockVa, dst, src));
            }
        }
        return result.ToArray();
    }

    /// <summary>Lowest box item id (<c>SG Bullets</c>) — the start of the box-item id range.</summary>
    public const byte EmergencyBoxFirstItemId = 0x10;
    /// <summary>Highest box item id (<c>Multiplier</c>) — the end of the box-item id range.</summary>
    public const byte EmergencyBoxLastItemId = 0x23;

    /// <summary>
    /// Reroll emergency-box contents (the pool-reroll randomizer mode, EXPERIMENTAL): for each International
    /// difficulty block, build a pool from <b>that block's own vanilla loot</b> — item weights = how often
    /// each item appears in the block's boxes, amounts = the real per-item box amounts (e.g. 9mm ∈ {17,34}) —
    /// then reroll every box slot from it. Each box keeps its vanilla <b>slot count</b> and 0x0A marker, and
    /// every drawn (item, amount) is one the game already ships in that difficulty's boxes, so records stay
    /// valid by construction. Distinct from the map-level item pool (which omits the box-signature enhancers).
    /// Keyed by <paramref name="seed"/> over one PRNG stream (blocks ascending) ⇒ deterministic. Validates
    /// each record's slot marker first and throws <see cref="InvalidOperationException"/> for an unexpected
    /// build. Reversal is via the installer's pristine backup. docs/reference/dc1/items/EMERGENCY-BOX-DATA.md.
    /// </summary>
    public static EmergencyBoxShuffleEntry[] RerollEmergencyBoxContents(Span<byte> exe, int seed)
    {
        uint rng = (uint)seed;
        var result = new List<EmergencyBoxShuffleEntry>(EmergencyBoxBlockVas.Length * EmergencyBoxesPerBlock);

        foreach (uint blockVa in EmergencyBoxBlockVas)
        {
            int blockOff = VaToFileOffset(blockVa);
            CheckBounds(exe.Length, blockOff, EmergencyBoxesPerBlock * EmergencyBoxRecordStride);

            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
                if (exe[blockOff + i * EmergencyBoxRecordStride] != EmergencyBoxSlotMarker)
                    throw new InvalidOperationException(
                        $"Emergency-box block 0x{blockVa:X} record {i} does not start with the 0x{EmergencyBoxSlotMarker:X2} " +
                        "slot marker — unexpected build/locale; refusing to reroll.");

            // Build this block's pool from its own records: weightedItems (one entry per occurrence) and the
            // set of amounts each item is seen with. Slot count of a record = its run of valid (id,count) pairs.
            var weightedItems = new List<byte>();
            var amountsOf = new Dictionary<byte, List<byte>>();
            var slotCount = new int[EmergencyBoxesPerBlock];
            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
            {
                int rec = blockOff + i * EmergencyBoxRecordStride;
                int slots = 0;
                for (int k = 0; k < 10; k++)
                {
                    byte id = exe[rec + 1 + 2 * k];
                    if (id < EmergencyBoxFirstItemId || id > EmergencyBoxLastItemId) break;
                    byte amt = exe[rec + 2 + 2 * k];
                    weightedItems.Add(id);
                    if (!amountsOf.TryGetValue(id, out var list)) amountsOf[id] = list = new List<byte>();
                    if (!list.Contains(amt)) list.Add(amt);
                    slots++;
                }
                slotCount[i] = slots;
            }
            if (weightedItems.Count == 0) continue; // empty block (no loot to draw from)

            // Reroll each record's slots in place from the block pool (item, then amount for that item).
            for (int i = 0; i < EmergencyBoxesPerBlock; i++)
            {
                int rec = blockOff + i * EmergencyBoxRecordStride;
                for (int k = 0; k < slotCount[i]; k++)
                {
                    byte item = weightedItems[(int)(NextRand(ref rng) % (uint)weightedItems.Count)];
                    var amts = amountsOf[item];
                    byte amount = amts[(int)(NextRand(ref rng) % (uint)amts.Count)];
                    exe[rec + 1 + 2 * k] = item;
                    exe[rec + 2 + 2 * k] = amount;
                }
                result.Add(new EmergencyBoxShuffleEntry(blockVa, i, i));
            }
        }
        return result.ToArray();
    }

    // ---- Starting inventory (the new-game starting-inventory randomizer lever) ----
    // DC1's starting inventory is NOT a flat data table — it is an init-code "give item" sequence in
    // DINO.exe (docs/reference/dc1/items/STARTING-INVENTORY.md, EXE-SYMBOLS 0x441033). The new-game init 0x441033
    // memsets the supply array (scratchpad0+0x9EBC = 0x6DDCFC, 4-byte [id,qty,cat,0] slots), then a
    // difficulty selector (byte[scratchpad0+0x9AB8]&0x7F → 0/1/2/default) picks one of four blocks; each
    // block grants the starting weapon(s) via SetFlag(group 11, idx, 1) (item-held flags — the Handgun)
    // and writes its supply slots as immediate `mov byte [eax + 0x9EBC + slot*4], imm8` id/qty stores.
    // The lever rewrites those id/qty immediates; the weapon grants are left untouched, so a usable
    // Handgun is always present. Read at new-game ⇒ seen on the next new game after relaunch.

    /// <summary>VA of the new-game starting-inventory init function (<c>0x441033</c>). <c>[verified disasm]</c></summary>
    public const uint StartingInventoryInitFnVa = 0x00441033;

    /// <summary>Base displacement of slot 0's <c>id</c> byte within scratchpad0 (the supply array;
    /// <c>scratchpad0(0x6D3E40) + 0x9EBC = 0x6DDCFC</c>). Each slot is 4 bytes: id, qty, cat, 0. <c>[verified]</c></summary>
    public const uint InventorySlotIdBaseDisp = 0x9EBC;

    /// <summary>Lowest item id a starting slot accepts (weapons begin here; supply ammo+health are
    /// <c>0x10..0x23</c>). Keys/ancient parts are excluded. <c>[verified items.json]</c></summary>
    public const byte StartingInvFirstItemId = 0x01;
    /// <summary>Highest item id a starting slot accepts (<c>Multiplier</c>). <c>[verified]</c></summary>
    public const byte StartingInvLastItemId = 0x23;

    /// <summary>Lowest supply (ammo+health) id — the range the slots are designed for and the random pool draws from.</summary>
    public const byte StartingInvFirstSupplyId = 0x10;

    /// <summary>The opcode of a slot store: <c>mov byte ptr [eax + disp32], imm8</c> = <c>C6 80</c>.
    /// The <c>imm8</c> is the 7th byte (instruction start = imm VA − 6). <c>[verified]</c></summary>
    private static readonly byte[] SlotStoreOpcode = { 0xC6, 0x80 };

    /// <summary>One supply slot's two immediate sites (the <c>id</c> and <c>qty</c> bytes of its store
    /// instruction) plus its slot index (which fixes the expected store displacement).</summary>
    public readonly record struct StartingInvSlotSite(uint IdImmVa, uint QtyImmVa, int Slot);

    /// <summary>One difficulty block of the new-game init (its selector value + ordered supply slots).</summary>
    public sealed record StartingInvBlock(string Name, byte Selector, StartingInvSlotSite[] Slots);

    /// <summary>The four difficulty blocks of <see cref="StartingInventoryInitFnVa"/> and their supply-slot
    /// immediate sites (docs/reference/dc1/items/STARTING-INVENTORY.md). Order is the PRNG walk order. <c>[verified disasm]</c></summary>
    public static readonly StartingInvBlock[] StartingInventoryBlocks =
    {
        new("diff0", 0, new[]
        {
            new StartingInvSlotSite(0x44116D, 0x4411AC, 0),
            new StartingInvSlotSite(0x4411EB, 0x44122A, 1),
            new StartingInvSlotSite(0x441269, 0x4412A8, 2),
            new StartingInvSlotSite(0x4412E7, 0x441326, 3),
            new StartingInvSlotSite(0x441365, 0x4413A4, 4),
        }),
        new("diff1", 1, new[]
        {
            new StartingInvSlotSite(0x4413F6, 0x441435, 0),
            new StartingInvSlotSite(0x441474, 0x4414BC, 1),
            new StartingInvSlotSite(0x441513, 0x44156A, 2),
        }),
        new("diff2", 2, new[]
        {
            new StartingInvSlotSite(0x4415E2, 0x441639, 0),
            new StartingInvSlotSite(0x441690, 0x4416E7, 1),
            new StartingInvSlotSite(0x44173E, 0x441795, 2),
        }),
        new("default", 0xFF, new[]
        {
            new StartingInvSlotSite(0x44181B, 0x441872, 0),
            new StartingInvSlotSite(0x4418C9, 0x441920, 1),
            new StartingInvSlotSite(0x441977, 0x4419CE, 2),
            new StartingInvSlotSite(0x441A25, 0x441A7C, 3),
            new StartingInvSlotSite(0x441AD3, 0x441B2A, 4),
            new StartingInvSlotSite(0x441B9C, 0x441BF3, 9),
        }),
    };

    /// <summary>Inclusive-low VA of the patch span. Covers both the weapon-grant <c>SetFlag(11,…)</c>
    /// pushes (the earliest is diff0's first <c>push 1</c> at <c>0x441105</c>) and the supply slot stores
    /// — the only bytes any starting-inventory write touches. The installer transplants <c>[Lo, Hi)</c>.</summary>
    public const uint StartingInventoryPatchLoVa = 0x00441105;
    /// <summary>Exclusive-high VA of the patch span (after the last <c>qty</c> immediate <c>0x441BF3</c>).</summary>
    public const uint StartingInventoryPatchHiVa = 0x00441BF4;

    /// <summary>Max items a CUSTOM starting inventory may have: the supply-slot count common to every
    /// difficulty block (blocks 1 and 2 have 3 slots), so the same list fits all difficulties.</summary>
    public static int StartingInventoryMaxCustomItems => StartingInventoryBlocks.Min(b => b.Slots.Length);

    /// <summary>9mm Parabellum — the Handgun's ammo, forced into slot 0 of every block by the RANDOM mode
    /// so the always-flag-granted Handgun is usable (beatability).</summary>
    public const byte StartingInvHandgunAmmoId = 0x16;
    private const byte StartingInvHandgunAmmoFull = 0x22; // 34, the 9mm max stack (full mag)

    /// <summary>The RANDOM mode's draw pool: <c>(supply id, max stack)</c> for ammo + health only — every
    /// drawn <c>(id, count)</c> is a valid supply item with a realistic count (<c>1..max</c>).</summary>
    private static readonly (byte Id, byte Max)[] StartingInvRandomPool =
    {
        (0x16, 34), (0x11, 10), (0x10, 10), (0x12, 3), (0x13, 3), (0x14, 3), (0x18, 6), (0x19, 6),
        (0x1B, 3), (0x1C, 2), (0x1D, 2), (0x1E, 1), (0x1F, 1),
    };

    /// <summary>Read a byte at virtual address <paramref name="va"/>.</summary>
    public static byte ReadUInt8AtVa(ReadOnlySpan<byte> exe, uint va) => Slice(exe, VaToFileOffset(va), 1)[0];

    /// <summary>Write a byte at virtual address <paramref name="va"/>.</summary>
    public static void WriteUInt8AtVa(Span<byte> exe, uint va, byte value) => Slice(exe, VaToFileOffset(va), 1)[0] = value;

    /// <summary>One slot written by a starting-inventory patch (for logging).</summary>
    public readonly record struct StartingInvWrite(string Block, int Slot, byte Id, byte Count);

    /// <summary>
    /// Validate that the build's starting-inventory init matches the decoded shape before any write:
    /// every slot's <c>id</c>/<c>qty</c> store must be a <c>C6 80 &lt;disp32&gt; &lt;imm8&gt;</c> instruction
    /// whose displacement is <c>0x9EBC + slot*4</c> (+1 for qty), and the current id must be 0 (memset)
    /// or a real item id (<c>0x01..0x23</c>). Throws <see cref="InvalidOperationException"/> otherwise
    /// (unexpected build/locale) — so a patch never corrupts a mismatched executable.
    /// </summary>
    public static void ValidateStartingInventory(ReadOnlySpan<byte> exe)
    {
        foreach (var block in StartingInventoryBlocks)
            foreach (var slot in block.Slots)
            {
                CheckSlotStore(exe, slot.IdImmVa, InventorySlotIdBaseDisp + (uint)slot.Slot * 4, block.Name, slot.Slot, "id");
                CheckSlotStore(exe, slot.QtyImmVa, InventorySlotIdBaseDisp + (uint)slot.Slot * 4 + 1, block.Name, slot.Slot, "qty");
                byte id = ReadUInt8AtVa(exe, slot.IdImmVa);
                if (id != 0 && (id < StartingInvFirstItemId || id > StartingInvLastItemId))
                    throw new InvalidOperationException(
                        $"Starting-inventory {block.Name} slot {slot.Slot} id immediate is 0x{id:X2} " +
                        "(not 0 or a valid item id) — unexpected build/locale; refusing to patch.");
            }
    }

    private static void CheckSlotStore(ReadOnlySpan<byte> exe, uint immVa, uint expectedDisp, string block, int slot, string field)
    {
        uint instrVa = immVa - 6;
        if (ReadUInt8AtVa(exe, instrVa) != SlotStoreOpcode[0] || ReadUInt8AtVa(exe, instrVa + 1) != SlotStoreOpcode[1]
            || ReadUInt32AtVa(exe, instrVa + 2) != expectedDisp)
            throw new InvalidOperationException(
                $"Starting-inventory {block} slot {slot} {field} store at VA 0x{instrVa:X} is not the expected " +
                $"`mov byte [eax+0x{expectedDisp:X}], imm8` (C6 80 …) — unexpected build/locale; refusing to patch.");
    }

    /// <summary>
    /// RANDOM starting inventory (EXPERIMENTAL): redraw every difficulty block's supply slots from the
    /// ammo+health pool, keyed by <paramref name="seed"/> (deterministic splitmix32, so the same seed on
    /// the same pristine exe yields byte-identical output). <b>Beatability:</b> the starting weapon grants
    /// (group-11 flags) are left untouched, so the Handgun is always present, and <b>slot 0 of every block
    /// is forced to 9mm</b> (full mag) so the Handgun has ammo. Every drawn <c>(id, count)</c> is a valid
    /// supply item with a realistic count. Validates the build first (<see cref="ValidateStartingInventory"/>)
    /// and throws on an unexpected build. Returns one entry per slot written (for logging).
    /// docs/reference/dc1/items/STARTING-INVENTORY.md.
    /// </summary>
    public static StartingInvWrite[] RandomizeStartingInventory(Span<byte> exe, int seed)
    {
        ValidateStartingInventory(exe);
        uint rng = (uint)seed;
        var result = new List<StartingInvWrite>();
        foreach (var block in StartingInventoryBlocks)
            for (int i = 0; i < block.Slots.Length; i++)
            {
                var slot = block.Slots[i];
                byte id, count;
                if (i == 0)
                {
                    id = StartingInvHandgunAmmoId;
                    count = StartingInvHandgunAmmoFull;
                }
                else
                {
                    var (pid, pmax) = StartingInvRandomPool[(int)(NextRand(ref rng) % (uint)StartingInvRandomPool.Length)];
                    id = pid;
                    count = (byte)(1 + NextRand(ref rng) % pmax);
                }
                WriteUInt8AtVa(exe, slot.IdImmVa, id);
                WriteUInt8AtVa(exe, slot.QtyImmVa, count);
                result.Add(new StartingInvWrite(block.Name, slot.Slot, id, count));
            }
        return result.ToArray();
    }

    /// <summary>
    /// CUSTOM starting inventory (EXPERIMENTAL): write the explicit <paramref name="items"/>
    /// <c>(id, count)</c> list into every difficulty block's slots (item <c>i</c> → slot <c>i</c>); any
    /// remaining slots in a block are emptied (id=0, qty=0). The list must have 1..<see cref="StartingInventoryMaxCustomItems"/>
    /// items (the supply-slot budget common to all difficulty blocks), each id <c>0x01..0x23</c> and count
    /// <c>1..255</c>. The starting weapon grant (Handgun) is preserved. Validates the build first and throws
    /// on an unexpected build; throws <see cref="ArgumentException"/> for a bad list. Returns the slots
    /// written (for logging). docs/reference/dc1/items/STARTING-INVENTORY.md.
    /// </summary>
    public static StartingInvWrite[] SetStartingInventory(Span<byte> exe, IReadOnlyList<(int Id, int Count)> items)
    {
        if (items is null || items.Count == 0)
            throw new ArgumentException("custom starting inventory is empty.", nameof(items));
        if (items.Count > StartingInventoryMaxCustomItems)
            throw new ArgumentException(
                $"custom starting inventory has {items.Count} items but at most {StartingInventoryMaxCustomItems} " +
                "fit every difficulty block.", nameof(items));
        for (int i = 0; i < items.Count; i++)
        {
            var (id, count) = items[i];
            if (id < StartingInvFirstItemId || id > StartingInvLastItemId)
                throw new ArgumentException(
                    $"item {i} id 0x{id:X} is out of range 0x{StartingInvFirstItemId:X2}..0x{StartingInvLastItemId:X2}.", nameof(items));
            if (count is < 1 or > 0xFF)
                throw new ArgumentException($"item {i} count {count} is out of range 1..255.", nameof(items));
        }
        ValidateStartingInventory(exe);

        var result = new List<StartingInvWrite>();
        foreach (var block in StartingInventoryBlocks)
            for (int i = 0; i < block.Slots.Length; i++)
            {
                var slot = block.Slots[i];
                byte id = i < items.Count ? (byte)items[i].Id : (byte)0;
                byte count = i < items.Count ? (byte)items[i].Count : (byte)0;
                WriteUInt8AtVa(exe, slot.IdImmVa, id);
                WriteUInt8AtVa(exe, slot.QtyImmVa, count);
                result.Add(new StartingInvWrite(block.Name, slot.Slot, id, count));
            }
        return result.ToArray();
    }

    // ---- Starting weapon (the group-11 weapon-grant lever) ----
    // The starting WEAPON is not a supply slot — each difficulty block of the new-game init 0x441033
    // grants it via SetFlag(group 11, idx, 1), where group 11 = "owns item id N" indexed by item id
    // (confirmed: the indices span the weapon range 0x01..0x0a, and 11:5 = the Handgun 0x05, the only
    // weapon granted in the standard diff1 block; the same bank also holds key-item-held flags 0x41/0x42).
    // Each grant is the instruction pair `push 1 (val); push idx; push 0xb; call SetFlag` (all `6A imm8`
    // pushes). The lever rewrites the FIRST grant of each block to the chosen weapon (val=1) and disables
    // the rest (val=0 ⇒ SetFlag(11,idx,0), a harmless clear on the fresh bank), so every difficulty grants
    // exactly the chosen weapon (or none) — uniformly. docs/reference/dc1/items/STARTING-INVENTORY.md.

    /// <summary>Lowest weapon item id a starting weapon may be (Shotgun). <c>[verified items.json]</c></summary>
    public const byte StartingWeaponFirstId = 0x01;
    /// <summary>Highest weapon item id a starting weapon may be (Grenade Gun Custom). <c>[verified]</c></summary>
    public const byte StartingWeaponLastId = 0x0A;

    /// <summary>One <c>SetFlag(11, idx, val)</c> weapon grant: the VAs of its <c>val</c> (1/0) and <c>idx</c>
    /// (weapon id) immediate bytes, plus the vanilla weapon id it grants.</summary>
    public readonly record struct StartingWeaponGrantSite(uint ValImmVa, uint IdxImmVa, byte VanillaWeaponId);

    /// <summary>One difficulty block's ordered weapon grants (the first is the slot the lever sets).</summary>
    public sealed record StartingWeaponGrantBlock(string Name, StartingWeaponGrantSite[] Sites);

    /// <summary>The weapon-grant <c>SetFlag(11,…)</c> sites in <see cref="StartingInventoryInitFnVa"/>,
    /// per difficulty block (docs/reference/dc1/items/STARTING-INVENTORY.md). <c>[verified disasm]</c></summary>
    public static readonly StartingWeaponGrantBlock[] StartingWeaponGrantBlocks =
    {
        new("diff0", new StartingWeaponGrantSite[]
        {
            new(0x441106, 0x441108, 0x01), new(0x441114, 0x441116, 0x05), new(0x441122, 0x441124, 0x09),
        }),
        new("diff1", new StartingWeaponGrantSite[]
        {
            new(0x4413AB, 0x4413AD, 0x05),
        }),
        new("diff2", new StartingWeaponGrantSite[]
        {
            new(0x441571, 0x441573, 0x01), new(0x44157F, 0x441581, 0x05),
        }),
        new("default", new StartingWeaponGrantSite[]
        {
            new(0x44179C, 0x44179E, 0x01), new(0x4417AA, 0x4417AC, 0x05),
            new(0x4417B8, 0x4417BA, 0x09), new(0x441B39, 0x441B3B, 0x09),
        }),
    };

    /// <summary>The opcode of an <c>SetFlag</c> argument push: <c>push imm8</c> = <c>6A</c>; the
    /// <c>imm8</c> is the byte after it (so the instruction start = imm VA − 1). <c>[verified]</c></summary>
    private const byte PushImm8Opcode = 0x6A;

    /// <summary>Validate that the build's weapon-grant sites match the decoded shape: each grant's
    /// <c>val</c>/<c>idx</c> bytes must be the <c>imm8</c> of a <c>push imm8</c> (<c>6A</c>) instruction,
    /// the <c>val</c> currently 1, and the <c>idx</c> a weapon id (<c>0x01..0x0A</c>). Throws
    /// <see cref="InvalidOperationException"/> otherwise (unexpected build/locale).</summary>
    public static void ValidateStartingWeaponGrants(ReadOnlySpan<byte> exe)
    {
        foreach (var block in StartingWeaponGrantBlocks)
            foreach (var site in block.Sites)
            {
                if (ReadUInt8AtVa(exe, site.ValImmVa - 1) != PushImm8Opcode
                    || ReadUInt8AtVa(exe, site.IdxImmVa - 1) != PushImm8Opcode
                    || ReadUInt8AtVa(exe, site.ValImmVa) != 1)
                    throw new InvalidOperationException(
                        $"Starting-weapon {block.Name} grant at VA 0x{site.ValImmVa - 1:X} is not the expected " +
                        "`push 1; push idx` SetFlag(11,…) form — unexpected build/locale; refusing to patch.");
                byte idx = ReadUInt8AtVa(exe, site.IdxImmVa);
                if (idx < StartingWeaponFirstId || idx > StartingWeaponLastId)
                    throw new InvalidOperationException(
                        $"Starting-weapon {block.Name} grant idx 0x{idx:X2} is not a weapon id " +
                        $"(0x{StartingWeaponFirstId:X2}..0x{StartingWeaponLastId:X2}) — unexpected build/locale; refusing to patch.");
            }
    }

    /// <summary>
    /// Set the new-game starting weapon (EXPERIMENTAL): make every difficulty block grant exactly
    /// <paramref name="weaponId"/> (a weapon id <c>0x01..0x0A</c>) — or <b>no</b> starting weapon when
    /// <paramref name="weaponId"/> is <c>null</c>. Rewrites each block's first <c>SetFlag(11,idx,1)</c> grant
    /// to the chosen weapon and disables the block's remaining grants (sets their <c>val</c> push to 0, i.e.
    /// <c>SetFlag(11,idx,0)</c> — a harmless clear), so the start is uniform across difficulties. Validates
    /// the build first (<see cref="ValidateStartingWeaponGrants"/>) and throws on an unexpected build.
    /// Returns the per-block weapon id now granted (<c>0</c> = none), for logging. docs/reference/dc1/items/STARTING-INVENTORY.md.
    /// </summary>
    public static (string Block, byte WeaponId)[] SetStartingWeapon(Span<byte> exe, int? weaponId)
    {
        // A weaponless start ("None") is NOT supported yet: clearing the group-11 owned-flag is not enough —
        // the engine re-equips a default Handgun through an as-yet-undecoded equipped-weapon path (confirmed
        // in-game: None still starts Regina with the pistol). So null would silently leave her armed; reject
        // it rather than lie. Needs a runtime (CE) decode of the equipped-weapon source. docs/reference/dc1/items/STARTING-INVENTORY.md.
        if (weaponId is null)
            throw new ArgumentException(
                "a weaponless start ('None') is not supported yet — the engine re-equips a default Handgun via an " +
                "undecoded path, so it can't be reliably removed. Choose a weapon id (0x01..0x0A).", nameof(weaponId));
        if (weaponId is { } w && (w < StartingWeaponFirstId || w > StartingWeaponLastId))
            throw new ArgumentOutOfRangeException(nameof(weaponId), weaponId,
                $"starting weapon id must be 0x{StartingWeaponFirstId:X2}..0x{StartingWeaponLastId:X2}.");
        ValidateStartingWeaponGrants(exe);

        var result = new List<(string, byte)>();
        foreach (var block in StartingWeaponGrantBlocks)
        {
            byte granted = 0;
            for (int i = 0; i < block.Sites.Length; i++)
            {
                var site = block.Sites[i];
                if (i == 0 && weaponId is { } id)
                {
                    WriteUInt8AtVa(exe, site.IdxImmVa, (byte)id);
                    WriteUInt8AtVa(exe, site.ValImmVa, 1);
                    granted = (byte)id;
                }
                else
                {
                    WriteUInt8AtVa(exe, site.ValImmVa, 0); // disable this grant (SetFlag(11,idx,0) — clear)
                }
            }
            result.Add((block.Name, granted));
        }
        return result.ToArray();
    }

    // ---- DC1 character-renderer vertex-table expansion (the 400-vertex ceiling lift) ----
    //
    // The character render path transforms model vertices into three FIXED 400-entry .data BSS
    // arrays indexed by raw vertex index (docs/decisions/dc1/renderer/RENDERER-VERTEX-CEILING-RCA.md): color 0x6B51A0,
    // otz 0x6B57E0, screen-XY 0x6B5E20 (4-byte stride, 0x640 apart). Any model > 400 verts reads and
    // writes past them (the 445-vert crash). The tables are hemmed in on both sides (slot-pointer
    // array 0x6B5160 below, live globals from 0x6B6460 up), so the lift RELOCATES them into a new
    // zero-filled RW PE section and repoints every reference. The reference census is closed: a
    // whole-file byte scan finds the three addresses in exactly 24 places, all operand dwords of the
    // known code sites (cursor init in the transform writer 0x44ECA0, tri builder 0x5F13C0, quad
    // builder 0x5F15B0); no data-side pointer literals exist (docs/decisions/dc1/renderer/VERTEX-CEILING-LIFT-PLAN.md).

    /// <summary>Stock capacity of the three per-vertex transform tables (entries). <c>[verified]</c></summary>
    public const int Dc1VertexTableStockCapacity = 400;

    /// <summary>Expanded capacity after <see cref="ExpandDc1CharacterVertexTables"/>. Sized so the
    /// practical model bound becomes the PSX-side blob budget (~805 verts for core00), not the
    /// renderer.</summary>
    public const int Dc1VertexTableExpandedCapacity = 1024;

    /// <summary>Stock table VAs (BSS; never file-backed — only the code operands are patched).</summary>
    public const uint Dc1ColorTableVa = 0x006B51A0;
    public const uint Dc1OtzTableVa = 0x006B57E0;
    public const uint Dc1XyTableVa = 0x006B5E20;

    /// <summary>New section layout: RVA immediately after <c>.reloc</c>'s aligned end (== stock
    /// <c>SizeOfImage</c>), raw data appended at the stock EOF. Same COLOR &lt; OTZ &lt; XY order and
    /// adjacency as the stock tables, stride <c>4 * ExpandedCapacity</c>.</summary>
    public const uint Dc1NewTableSectionRva = 0x002FC000;
    public const int Dc1NewTableSectionSize = 0x3000; // 3 tables x 1024 entries x 4 B
    public const int Dc1StockExeLength = 0x28C000;
    public const uint Dc1NewColorTableVa = ImageBase + Dc1NewTableSectionRva;
    public const uint Dc1NewOtzTableVa = Dc1NewColorTableVa + 4 * (uint)Dc1VertexTableExpandedCapacity;
    public const uint Dc1NewXyTableVa = Dc1NewOtzTableVa + 4 * (uint)Dc1VertexTableExpandedCapacity;

    // PE header field positions, verified against the stock image (e_lfanew 0xF8, 7 sections,
    // section table 0x1F0..0x308 with a zeroed 8th slot, SizeOfImage 0x2FC000, checksum 0).
    private const int PeSigOffset = 0xF8;
    private const int NumberOfSectionsOffset = 0xFE;
    private const int SizeOfImageOffset = 0x148;
    private const int NewSectionHeaderOffset = 0x308;

    /// <summary>
    /// Every code reference to the three tables, as the file offset of the 32-bit address operand
    /// (instruction VA + 3 in all 24 cases; VA == file offset in the <c>.text</c> window). Derived by
    /// exhaustive .text operand scan AND whole-file byte scan — the two agree exactly.
    /// Writer cursor-init: <c>0x44ED49/50/57</c>; tri builder reads: <c>0x5F14E4/14F4/1502</c> color,
    /// <c>0x5F152F/1536/154A</c> otz, <c>0x5F140B/141E/142F</c> XY; quad builder reads:
    /// <c>0x5F1703/1713/1723/1731</c> color, <c>0x5F176A/1771/177C/1790</c> otz,
    /// <c>0x5F15FB/160E/161F/165B</c> XY.
    /// </summary>
    public static readonly (uint OperandVa, uint StockValue)[] Dc1VertexTableOperands =
    {
        // transform-writer cursor init (mov [ebp-k], imm32) — one per table
        (0x0044ED4Cu, Dc1XyTableVa), (0x0044ED53u, Dc1OtzTableVa), (0x0044ED5Au, Dc1ColorTableVa),
        // tri packet builder 0x5F13C0 ([idx*4 + disp32])
        (0x005F140Eu, Dc1XyTableVa), (0x005F1421u, Dc1XyTableVa), (0x005F1432u, Dc1XyTableVa),
        (0x005F14E7u, Dc1ColorTableVa), (0x005F14F7u, Dc1ColorTableVa), (0x005F1505u, Dc1ColorTableVa),
        (0x005F1532u, Dc1OtzTableVa), (0x005F1539u, Dc1OtzTableVa), (0x005F154Du, Dc1OtzTableVa),
        // quad packet builder 0x5F15B0
        (0x005F15FEu, Dc1XyTableVa), (0x005F1611u, Dc1XyTableVa), (0x005F1622u, Dc1XyTableVa), (0x005F165Eu, Dc1XyTableVa),
        (0x005F1706u, Dc1ColorTableVa), (0x005F1716u, Dc1ColorTableVa), (0x005F1726u, Dc1ColorTableVa), (0x005F1734u, Dc1ColorTableVa),
        (0x005F176Du, Dc1OtzTableVa), (0x005F1774u, Dc1OtzTableVa), (0x005F177Fu, Dc1OtzTableVa), (0x005F1793u, Dc1OtzTableVa),
    };

    private static uint Dc1NewTableVaFor(uint stockVa) => stockVa switch
    {
        Dc1ColorTableVa => Dc1NewColorTableVa,
        Dc1OtzTableVa => Dc1NewOtzTableVa,
        Dc1XyTableVa => Dc1NewXyTableVa,
        _ => throw new ArgumentOutOfRangeException(nameof(stockVa)),
    };

    /// <summary>
    /// Lift the DC1 character-model 400-vertex renderer ceiling to
    /// <see cref="Dc1VertexTableExpandedCapacity"/>: append a zero-filled read/write PE section
    /// (<c>.dinovtx</c>) hosting three 1024-entry tables and repoint all 24 code references. Returns a
    /// NEW buffer (<see cref="Dc1NewTableSectionSize"/> bytes longer); the input is not modified.
    /// Validates every precondition against the stock image and throws
    /// <see cref="InvalidOperationException"/> on any mismatch (foreign build, or already expanded) —
    /// it never writes a byte it cannot verify. Reversal is the installer's backup file.
    /// </summary>
    public static byte[] ExpandDc1CharacterVertexTables(ReadOnlySpan<byte> exe)
    {
        if (exe.Length != Dc1StockExeLength)
            throw new InvalidOperationException(
                $"DINO.exe is 0x{exe.Length:X} bytes, expected stock 0x{Dc1StockExeLength:X}.");
        if (exe[0] != 'M' || exe[1] != 'Z' || ReadUInt32(exe, 0x3C) != PeSigOffset
            || ReadUInt32(exe, PeSigOffset) != 0x00004550) // "PE\0\0"
            throw new InvalidOperationException("Not the expected PE layout (MZ/PE anchors mismatch).");
        if (ReadUInt16(exe, NumberOfSectionsOffset) != 7)
            throw new InvalidOperationException("Expected 7 PE sections (stock image).");
        if (ReadUInt32(exe, SizeOfImageOffset) != Dc1NewTableSectionRva)
            throw new InvalidOperationException("SizeOfImage is not the stock 0x2FC000.");
        for (int i = 0; i < 0x28; i++)
            if (exe[NewSectionHeaderOffset + i] != 0)
                throw new InvalidOperationException("The 8th section-header slot is not empty.");
        foreach (var (va, stock) in Dc1VertexTableOperands)
            if (ReadUInt32AtVa(exe, va) != stock)
                throw new InvalidOperationException(
                    $"Operand at VA 0x{va:X} does not hold stock table address 0x{stock:X} " +
                    "(already expanded, or an unknown build).");

        var outBuf = new byte[Dc1StockExeLength + Dc1NewTableSectionSize];
        exe.CopyTo(outBuf);
        var span = outBuf.AsSpan();

        // header: 8th section ".dinovtx", RW initialized data, raw == appended zero tail
        WriteUInt16(span, NumberOfSectionsOffset, 8);
        WriteUInt32(span, SizeOfImageOffset, Dc1NewTableSectionRva + Dc1NewTableSectionSize);
        System.Text.Encoding.ASCII.GetBytes(".dinovtx").CopyTo(span.Slice(NewSectionHeaderOffset, 8));
        WriteUInt32(span, NewSectionHeaderOffset + 0x08, Dc1NewTableSectionSize); // VirtualSize
        WriteUInt32(span, NewSectionHeaderOffset + 0x0C, Dc1NewTableSectionRva);  // VirtualAddress
        WriteUInt32(span, NewSectionHeaderOffset + 0x10, Dc1NewTableSectionSize); // SizeOfRawData
        WriteUInt32(span, NewSectionHeaderOffset + 0x14, Dc1StockExeLength);      // PointerToRawData
        WriteUInt32(span, NewSectionHeaderOffset + 0x24, 0xC0000040);             // RW initialized data

        foreach (var (va, stock) in Dc1VertexTableOperands)
            WriteUInt32AtVa(span, va, Dc1NewTableVaFor(stock));

        return outBuf;
    }

    /// <summary>True when <paramref name="exe"/> already carries the expanded vertex tables.</summary>
    public static bool IsDc1CharacterVertexTablesExpanded(ReadOnlySpan<byte> exe)
        => exe.Length == Dc1StockExeLength + Dc1NewTableSectionSize
           && ReadUInt16(exe, NumberOfSectionsOffset) == 8
           && ReadUInt32AtVa(exe, Dc1VertexTableOperands[0].OperandVa) == Dc1NewXyTableVa;

    private static Span<byte> Slice(Span<byte> buf, int off, int len)
    {
        CheckBounds(buf.Length, off, len);
        return buf.Slice(off, len);
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> buf, int off, int len)
    {
        CheckBounds(buf.Length, off, len);
        return buf.Slice(off, len);
    }

    private static void CheckBounds(int bufLen, int off, int len)
    {
        if (off < 0 || len < 0 || off + len > bufLen)
            throw new ArgumentOutOfRangeException(nameof(off),
                $"[0x{off:X}, 0x{off + len:X}) is outside the {bufLen}-byte buffer.");
    }
}
