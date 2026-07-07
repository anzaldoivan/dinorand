using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage;

/// <summary>
/// A self-contained, relocatable resource block extracted from a room's decompressed RDT buffer —
/// e.g. an enemy's model (skeleton + mesh) or motion block. The block is "pointer-closed": every
/// file-form PSX pointer it contains targets a byte inside the block, so it can be copied to another
/// RDT and made valid by adding a single offset delta to each pointer
/// (see <see cref="SpeciesImporter"/>).
/// </summary>
/// <param name="Bytes">The block's raw bytes (a copy of <c>rdt[SourceOffset .. SourceOffset+len)</c>).</param>
/// <param name="SourceOffset">The block's offset within the source RDT buffer (its file-form head
/// pointer is <see cref="SpeciesImporter.PsxBase"/> + this).</param>
/// <param name="PointerSlots">Offsets <b>within <see cref="Bytes"/></b> (i.e. relative to the block
/// head) of every dword holding a file-form PSX pointer that must be relocated.</param>
public sealed record SpeciesBlock(byte[] Bytes, int SourceOffset, IReadOnlyList<int> PointerSlots);

/// <summary>Result of importing a species into a target RDT: the grown buffer plus the new file-form
/// model/motion pointers to write into the target's <c>0x20</c> enemy record.</summary>
public sealed record SpeciesImportResult(byte[] Rdt, uint ModelPtr, uint MotionPtr);

/// <summary>
/// A foreign species ready to drop into another room: its relocatable model + motion blocks plus the
/// AI <see cref="Category"/> byte (<c>record[2]</c>) the engine dispatches on (so the matching AI runs
/// on the imported model, not the target room's original AI class) and the decoded <see cref="Species"/>.
/// Built by <see cref="SpeciesImporter.ExtractDonor"/> from a source room.
/// </summary>
public sealed record SpeciesDonor(DinoSpecies Species, byte Category, SpeciesBlock Model, SpeciesBlock Motion)
{
    /// <summary>The donor's relocatable texture (texture page + palette + the model's tpage/CLUT
    /// codes), extracted from the donor room package so the import can carry the skin. Null = the
    /// donor texture could not be resolved and the import is geometry-only (may render mis-coloured).
    /// Set via <see cref="SpeciesImporter.TryExtractTexture"/>; consumed by
    /// <see cref="RoomFile.ImportSpeciesTextured(SpeciesDonor,int)"/>.</summary>
    public TextureBlock? Texture { get; init; }
}

/// <summary>One source byte range of a multi-range donor (<see cref="SpeciesBlockSet"/>), plus its offset
/// within the compacted blob.</summary>
/// <param name="SourceLo">Range start in the source RDT (4-aligned).</param>
/// <param name="SourceHi">Range end (exclusive) in the source RDT.</param>
/// <param name="BlobOffset">Where this range's bytes start inside <see cref="SpeciesBlockSet.Bytes"/>.</param>
public sealed record SpeciesRange(int SourceLo, int SourceHi, int BlobOffset);

/// <summary>A pointer dword (blob-relative) to relocate on append, tagged with the index of the donor
/// range its <b>target</b> falls in — the <i>piecewise</i> relocation map. A cross-range pointer is
/// relocated by its target range's append delta, not by its own range's.</summary>
public sealed record RelocSlot(int BlobOffset, int TargetRangeIndex);

/// <summary>
/// A relocatable donor made of N disjoint source byte ranges (compacted into <see cref="Bytes"/>) plus a
/// piecewise relocation map — the backward-closure generalisation of the single-range, uniform-delta
/// <see cref="SpeciesBlock"/> (docs/decisions/dc1/theri/THERI-BACKWARD-CLOSURE-PLAN.md). Used for an <i>entangled</i> species
/// whose model + motion + animation data are one interlinked resource, not two independently-closable
/// blocks (the Therizinosaurus; same path later unblocks LargeGround / Tyrannosaurus). For a fully-
/// connected resource (4 of the 5 Theri rooms) this degenerates to a single range / uniform delta.
/// </summary>
/// <param name="Bytes">The donor ranges concatenated in order (gaps between ranges dropped).</param>
/// <param name="Ranges">The source ranges + their blob offsets, ascending.</param>
/// <param name="Slots">The pointer dwords to relocate (blob-relative) + their target-range index.</param>
/// <param name="ModelHead">Source offset of the model head (resolved to its imported pointer on append).</param>
/// <param name="MotionHead">Source offset of the motion head.</param>
public sealed record SpeciesBlockSet(
    byte[] Bytes,
    IReadOnlyList<SpeciesRange> Ranges,
    IReadOnlyList<RelocSlot> Slots,
    int ModelHead,
    int MotionHead);

/// <summary>Multi-range sibling of <see cref="SpeciesDonor"/> for an entangled species: the closure-
/// extracted range set (<see cref="SpeciesBlockSet"/>) bundled with its AI <see cref="Category"/> and
/// decoded <see cref="Species"/>. Built by <see cref="SpeciesImporter.ExtractDonorClosure"/>.</summary>
public sealed record SpeciesDonorMulti(DinoSpecies Species, byte Category, SpeciesBlockSet Blocks)
{
    /// <summary>The donor's relocatable texture — see <see cref="SpeciesDonor.Texture"/>. Consumed by
    /// <see cref="RoomFile.ImportSpeciesTextured(SpeciesDonorMulti,int)"/>.</summary>
    public TextureBlock? Texture { get; init; }
}

/// <summary>
/// Cross-room species import (docs/decisions/dc1/enemies/CROSS-ROOM-SPECIES-PLAN.md, increment 1: geometry only).
///
/// <para>An enemy's species is the loaded model resource referenced by the <c>0x20</c> record's
/// model pointer (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.14); a room's RDT only contains the species it uses, and
/// the pointer is an absolute file-form address (<c>0x8010xxxx</c>) relocated at load via the engine's
/// PSX-RAM base. To place a <i>foreign</i> species we therefore copy its model + motion blocks into the
/// target room's RDT and rewrite the copied internal pointers — there is no id byte to flip.</para>
///
/// <para><b>Relocation set.</b> The engine relocates exactly those dwords whose value is a valid
/// file-form PSX pointer (cont.9/14); so the relocation set is every 4-aligned dword in <c>[PsxBase,
/// PsxBase+len)</c>. Appending a block at offset <c>newOff</c> shifts its head from <c>SourceOffset</c>
/// to <c>newOff</c>; every internal pointer's target shifts by the same <c>delta = newOff -
/// SourceOffset</c>, so we add <c>delta</c> to each pointer value. The model and motion blocks are each
/// pointer-closed and do not reference one another, so they import independently.</para>
///
/// <para>This is geometry only; textures (separate type-8 TIM entries / VRAM tpages) are not imported,
/// so an imported species may render mis-coloured but, geometry being valid, should not crash
/// (increment 2 adds textures).</para>
/// </summary>
public static class SpeciesImporter
{
    /// <summary>The file-form PSX-RAM base the RDT's internal pointers are relative to.</summary>
    public const uint PsxBase = RoomScript.PsxRdtBase; // 0x80100000

    /// <summary>
    /// Maximum <b>decompressed</b> room-RDT size the engine can load, in bytes. The RDT decompresses to
    /// PSX RAM at <c>0x80100000</c> and the next fixed engine work-structure sits at <c>0x80160000</c>, so
    /// the RDT region is <c>0x60000</c> = 393 216 B (CE-confirmed, docs/decisions/dc1/theri/THERI-0203-SWAP-PLAN.md). The
    /// largest vanilla room (st102) is <c>0x5CCD8</c>, just under it; a cross-species import that pushes the
    /// grown RDT past this overruns that structure and crashes the model-setup walk
    /// (<c>DINO.exe</c> AV @ <c>0x45955E</c>). Callers must refuse an import whose result RDT exceeds this.
    /// </summary>
    public const int EngineRoomRdtCeiling = 0x60000;

    /// <summary>
    /// The effective append ceiling for a cross-imported species: the <b>resident-pool floor</b> at RDT
    /// offset <c>0x5D000</c> (PSX <c>0x8015D000</c> = 380,928 B). The engine loads the persistent
    /// door/prop models that survive across rooms into a heap pool whose minimum address is read live
    /// from the file-form persistent-model registry at <c>DINO.exe</c> <c>0x657440</c> (the registry is a
    /// fixed master ⇒ the floor is <b>stable / room-independent</b>, not scenario-variable — CE-measured
    /// 2026-06-22, docs/decisions/dc1/theri/THERI-0102-PLAYABLE-FIX-PLAN.md "LIVE SESSION"). A grown RDT that crosses this
    /// floor overwrites those resident models, which is the root cause of <b>defect&#160;A</b> (0102 room-entry
    /// render AV) and — per the live convergent hypothesis — of <b>defect&#160;B</b> (the hit/death crash:
    /// the same overrun corrupts the shared death/anim object, not a separate descriptor-table bug). So an
    /// import must clip-strip the donor until the grown RDT lands <c>≤ ResidentPoolFloor</c>, well below the
    /// looser work-struct <see cref="EngineRoomRdtCeiling"/> (which stays the hard upper bound for species
    /// that don't share the pool, e.g. the cat-3 boss).
    /// </summary>
    public const int ResidentPoolFloor = 0x5D000;

    /// <summary>
    /// Quality-bounded eligibility cap for the donor clip-strip (docs/decisions/dc1/theri/THERI-CLIP-STRIP-PLAN.md): the
    /// maximum number of animation clips a Theri swap may drop to fit an over-ceiling room. A conservative
    /// pre-capture bound — it admits the named targets (0102 needs 5, 0203 needs 7) and the other low-drop
    /// rooms while refusing the 20–47-drop rooms that would ship a visibly stripped Theri. Raised once the
    /// live used-clip capture confirms which clips are never played (so dropping them costs nothing).
    /// </summary>
    public const int MaxClipStripDropCount = 8;

    /// <summary>True when <paramref name="value"/> is a valid file-form pointer into an RDT of
    /// <paramref name="rdtLen"/> bytes.</summary>
    public static bool IsFileFormPtr(uint value, int rdtLen)
        => value >= PsxBase && value < PsxBase + (uint)rdtLen;

    /// <summary>
    /// Offsets of every 4-aligned dword in <paramref name="rdt"/> that holds a file-form PSX pointer —
    /// the set the engine relocates, and the set the importer rewrites.
    /// </summary>
    public static List<int> RelocationSlots(ReadOnlySpan<byte> rdt)
    {
        var slots = new List<int>();
        for (int o = 0; o + 4 <= rdt.Length; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(o, 4));
            if (IsFileFormPtr(v, rdt.Length))
                slots.Add(o);
        }
        return slots;
    }

    /// <summary>
    /// Extract <c>[lo, hi)</c> of <paramref name="rdt"/> as a relocatable <see cref="SpeciesBlock"/>.
    /// Throws when the region is not pointer-closed (an internal pointer escapes the region) — that is
    /// the safety invariant that makes the block independently movable (a closed block stays valid
    /// after a uniform offset shift; an escaping pointer would dangle).
    /// </summary>
    public static SpeciesBlock ExtractBlock(ReadOnlySpan<byte> rdt, int lo, int hi)
    {
        if (lo < 0 || hi > rdt.Length || lo >= hi || (lo & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(lo), $"bad block bounds [{lo:x},{hi:x})");

        var slots = new List<int>();
        for (int o = lo; o + 4 <= hi; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(o, 4));
            if (!IsFileFormPtr(v, rdt.Length))
                continue;
            int target = (int)(v - PsxBase);
            if (target < lo || target >= hi)
                throw new InvalidOperationException(
                    $"block [{lo:x},{hi:x}) is not pointer-closed: slot {o:x} -> {target:x} escapes");
            slots.Add(o - lo);
        }
        return new SpeciesBlock(rdt.Slice(lo, hi - lo).ToArray(), lo, slots);
    }

    /// <summary>
    /// Targets of the (up to 9) relocated RDT header dwords at <c>rdt+0..0x20</c>. These head the RDT's
    /// tail structures (func table etc.), so they cap a trailing resource — e.g. a motion block — that
    /// has no model/motion/op23 head after it. Used as region boundaries by <see cref="RegionExtent"/>.
    /// </summary>
    public static IReadOnlyList<int> HeaderPointerTargets(ReadOnlySpan<byte> rdt)
    {
        var targets = new List<int>();
        for (int o = 0; o + 4 <= rdt.Length && o < 0x24; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(o, 4));
            if (IsFileFormPtr(v, rdt.Length))
                targets.Add((int)(v - PsxBase));
        }
        return targets;
    }

    /// <summary>True when <c>[lo,hi)</c> of <paramref name="rdt"/> is pointer-closed (no file-form
    /// pointer inside it targets a byte outside it) — the invariant that makes a block movable.</summary>
    public static bool IsPointerClosed(ReadOnlySpan<byte> rdt, int lo, int hi)
    {
        for (int o = lo; o + 4 <= hi; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(o, 4));
            if (!IsFileFormPtr(v, rdt.Length)) continue;
            int t = (int)(v - PsxBase);
            if (t < lo || t >= hi) return false;
        }
        return true;
    }

    /// <summary>
    /// Bound a resource starting at <paramref name="head"/> as <c>[head, hi)</c>, where <c>hi</c> is the
    /// <b>smallest</b> <paramref name="boundaries"/> entry at which the region is pointer-closed
    /// (<see cref="IsPointerClosed"/>), or the buffer end. A single "next resource" boundary is not
    /// enough: a boundary marker can fall <i>inside</i> a large model (a sub-block start that another
    /// record / header dword also references), which would split the model and leave its later pointers
    /// dangling. Scanning boundaries in ascending order and taking the first that closes finds the true
    /// extent (the model's own sub-block boundaries do not close because its pointers reach past them).
    /// </summary>
    public static (int Lo, int Hi) RegionExtent(ReadOnlySpan<byte> rdt, int head, IEnumerable<int> boundaries)
    {
        foreach (int hi in boundaries.Where(b => b > head).Distinct().OrderBy(b => b))
            if (IsPointerClosed(rdt, head, hi))
                return (head, hi);
        return (head, rdt.Length); // closes trivially at the buffer end (over-copy, still safe)
    }

    /// <summary>
    /// Extract a species' <paramref name="modelPtr"/> and <paramref name="motionPtr"/> blocks from
    /// <paramref name="rdt"/> as relocatable <see cref="SpeciesBlock"/>s. <paramref name="resourceHeads"/>
    /// are the offsets of other referenced resource heads (e.g. every enemy record's model/motion
    /// target) used — together with <see cref="HeaderPointerTargets"/> — to bound each block. Throws
    /// (via <see cref="ExtractBlock"/>) if a bounded region is not pointer-closed.
    /// </summary>
    public static (SpeciesBlock Model, SpeciesBlock Motion) ExtractSpecies(
        ReadOnlySpan<byte> rdt, uint modelPtr, uint motionPtr, IEnumerable<int> resourceHeads)
    {
        int modelHead = (int)(modelPtr - PsxBase);
        int motionHead = (int)(motionPtr - PsxBase);
        var bounds = new List<int>(resourceHeads);
        bounds.AddRange(HeaderPointerTargets(rdt));
        bounds.Add(modelHead);
        bounds.Add(motionHead);

        var (mLo, mHi) = RegionExtent(rdt, modelHead, bounds);
        var (tLo, tHi) = RegionExtent(rdt, motionHead, bounds);
        return (ExtractBlock(rdt, mLo, mHi), ExtractBlock(rdt, tLo, tHi));
    }

    /// <summary>
    /// Build a <see cref="SpeciesDonor"/> from a source room's enemy record: extract its model + motion
    /// blocks and capture its AI category + decoded species. <paramref name="resourceHeads"/> bound the
    /// blocks (see <see cref="ExtractSpecies"/>) — typically every enemy record's model/motion target
    /// in the source room.
    /// </summary>
    public static SpeciesDonor ExtractDonor(ReadOnlySpan<byte> rdt, EnemyRecord src,
                                            IEnumerable<int> resourceHeads)
    {
        var (model, motion) = ExtractSpecies(rdt, src.OriginalModelPtr, src.OriginalMotionPtr, resourceHeads);
        return new SpeciesDonor(src.Species, src.Category, model, motion);
    }

    /// <summary>
    /// Append <paramref name="block"/> to the end of <paramref name="rdt"/> (4-aligned), relocating its
    /// internal pointers. Returns the grown buffer and the block's new file-form head pointer (what to
    /// store as the model / motion pointer in the target enemy record).
    /// </summary>
    public static (byte[] Rdt, uint HeadPtr) Append(byte[] rdt, SpeciesBlock block)
    {
        int newOff = (rdt.Length + 3) & ~3;             // 4-align the append site
        var result = new byte[newOff + block.Bytes.Length];
        Array.Copy(rdt, 0, result, 0, rdt.Length);      // [rdt.Length, newOff) stays zero-pad
        Array.Copy(block.Bytes, 0, result, newOff, block.Bytes.Length);

        int delta = newOff - block.SourceOffset;
        foreach (int rel in block.PointerSlots)
        {
            int at = newOff + rel;
            uint p = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(at, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at, 4), (uint)(p + delta));
        }
        return (result, PsxBase + (uint)newOff);
    }

    /// <summary>
    /// Import a species (its <paramref name="model"/> and <paramref name="motion"/> blocks) into
    /// <paramref name="targetRdt"/>, appending and relocating both. The caller writes the returned
    /// pointers into a target <c>0x20</c> record's <see cref="EnemyRecord.ModelOffset"/> /
    /// <see cref="EnemyRecord.MotionOffset"/>.
    /// </summary>
    public static SpeciesImportResult Import(byte[] targetRdt, SpeciesBlock model, SpeciesBlock motion)
    {
        var (rdt1, modelPtr) = Append(targetRdt, model);
        var (rdt2, motionPtr) = Append(rdt1, motion);
        return new SpeciesImportResult(rdt2, modelPtr, motionPtr);
    }

    // --- backward-closure / multi-range path (docs/decisions/dc1/theri/THERI-BACKWARD-CLOSURE-PLAN.md) -------------------

    /// <summary>Targets below this are the shared RDT header / low offsets — escape class (b), left
    /// absolute (dead data in the target, never relocated by the engine either).</summary>
    public const int HeaderThreshold = 0x100;

    /// <summary>
    /// The <b>tight extent</b> of an embedded resource as a set of disjoint, merged byte ranges, found by
    /// <b>pointer reachability</b> from the model + motion heads — the bounding for an <i>entangled</i>
    /// species whose model/motion/animation cross block boundaries (docs/decisions/dc1/theri/THERI-BACKWARD-CLOSURE-PLAN.md).
    ///
    /// <para>The resource <i>ceiling</i> is the first <see cref="HeaderPointerTargets"/> above
    /// <paramref name="motionHead"/> (those head the RDT <i>tail</i> structures — func table / script —
    /// which are the room's own, never copied). Inside <c>[modelHead, ceiling)</c> the in-resource pointer
    /// targets partition the space into candidate structures (a structure headed at <c>t</c> spans
    /// <c>[t, nextTarget)</c>); a breadth-first walk from the two heads, following pointers into their
    /// target structures, yields the connected resource. Reached structures are merged into ranges;
    /// unreached gaps are dropped (the only meaningful saving in 1 of the 5 Theri rooms — the rest are one
    /// fully-connected range).</para>
    /// </summary>
    public static List<(int Lo, int Hi)> ReachabilityExtent(ReadOnlySpan<byte> rdt, int modelHead, int motionHead)
    {
        int ceiling = rdt.Length;
        foreach (int t in HeaderPointerTargets(rdt))
            if (t > motionHead && t < ceiling) ceiling = t;

        // resource-form target of the dword at o, or -1 if it is not a file-form ptr (static: a span
        // parameter cannot be captured by a local function, so it is passed explicitly).
        static int Target(ReadOnlySpan<byte> r, int o)
        {
            if (o < 0 || o + 4 > r.Length) return -1;
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(o, 4));
            return v >= PsxBase && v < PsxBase + (uint)r.Length ? (int)(v - PsxBase) : -1;
        }

        // Partition points = the two heads + every in-resource pointer target (4-aligned).
        var pts = new SortedSet<int> { modelHead & ~3, motionHead & ~3 };
        for (int o = modelHead; o + 4 <= ceiling; o += 4)
        {
            int t = Target(rdt, o);
            if (t >= modelHead && t < ceiling) pts.Add(t & ~3);
        }
        var list = pts.ToList();
        int Next(int t)
        {
            int i = list.BinarySearch(t);
            if (i < 0) i = ~i; else i++;
            return i < list.Count ? list[i] : ceiling;
        }

        // BFS over structures [head, Next(head)), following every pointer found inside.
        var reached = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(modelHead & ~3);
        stack.Push(motionHead & ~3);
        while (stack.Count > 0)
        {
            int h = stack.Pop();
            if (!reached.Add(h)) continue;
            int end = Next(h);
            for (int o = (h + 3) & ~3; o + 4 <= end; o += 4)
            {
                int t = Target(rdt, o);
                if (t >= modelHead && t < ceiling) stack.Push(t & ~3);
            }
        }

        var ivs = new List<(int Lo, int Hi)>();
        foreach (int h in reached) ivs.Add((h, Next(h)));
        ivs.Sort((a, b) => a.Lo.CompareTo(b.Lo));

        var merged = new List<(int Lo, int Hi)>();
        foreach (var iv in ivs)
        {
            if (merged.Count > 0 && iv.Lo <= merged[^1].Hi)
                merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, iv.Hi));
            else merged.Add(iv);
        }
        return merged;
    }

    /// <summary>
    /// Extract a species' resource — reachable from <paramref name="modelPtr"/> + <paramref name="motionPtr"/>
    /// (<see cref="ReachabilityExtent"/>) — as a relocatable <see cref="SpeciesBlockSet"/>: the disjoint
    /// ranges compacted into one blob, every in-range pointer recorded as a <see cref="RelocSlot"/> tagged
    /// with the range its target lands in (the piecewise map). <b>Throws</b> on any escape that is an
    /// aligned, in-bounds target outside all ranges (a real uncopied resource — never silently left);
    /// class (b) low/header and class (c) unaligned-coincidental escapes are left absolute.
    /// </summary>
    public static SpeciesBlockSet ExtractRangeSet(ReadOnlySpan<byte> rdt, uint modelPtr, uint motionPtr)
    {
        int modelHead = (int)(modelPtr - PsxBase);
        int motionHead = (int)(motionPtr - PsxBase);
        var ranges = ReachabilityExtent(rdt, modelHead, motionHead);
        if (ranges.Count == 0)
            throw new InvalidOperationException("closure extent is empty");

        int total = 0;
        foreach (var r in ranges) total += r.Hi - r.Lo;
        var bytes = new byte[total];
        var rangeList = new List<SpeciesRange>(ranges.Count);
        int blobOff = 0;
        foreach (var (lo, hi) in ranges)
        {
            rdt.Slice(lo, hi - lo).CopyTo(bytes.AsSpan(blobOff));
            rangeList.Add(new SpeciesRange(lo, hi, blobOff));
            blobOff += hi - lo;
        }

        int RangeOf(int x)
        {
            for (int i = 0; i < ranges.Count; i++)
                if (x >= ranges[i].Lo && x < ranges[i].Hi) return i;
            return -1;
        }

        var slots = new List<RelocSlot>();
        foreach (var r in rangeList)
            for (int o = r.SourceLo; o + 4 <= r.SourceHi; o += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(o, 4));
                if (v < PsxBase || v >= PsxBase + (uint)rdt.Length) continue;
                int t = (int)(v - PsxBase);
                int ti = RangeOf(t);
                if (ti >= 0)
                    slots.Add(new RelocSlot(r.BlobOffset + (o - r.SourceLo), ti));
                else if (t >= HeaderThreshold && (t & 3) == 0)
                    throw new InvalidOperationException(
                        $"closure escape: slot 0x{o:x} -> 0x{t:x} is an aligned, in-bounds target outside all donor ranges");
                // else: class (b) (t < HeaderThreshold) or class (c) (unaligned) — leave absolute.
            }

        return new SpeciesBlockSet(bytes, rangeList, slots, modelHead, motionHead);
    }

    /// <summary>Build a <see cref="SpeciesDonorMulti"/> from a source room's enemy record via
    /// <see cref="ExtractRangeSet"/> (model+motion heads from the record), capturing its AI category +
    /// decoded species. The entangled-species counterpart of <see cref="ExtractDonor"/>.</summary>
    public static SpeciesDonorMulti ExtractDonorClosure(ReadOnlySpan<byte> rdt, EnemyRecord src)
    {
        var set = ExtractRangeSet(rdt, src.OriginalModelPtr, src.OriginalMotionPtr);
        return new SpeciesDonorMulti(src.Species, src.Category, set);
    }

    // --- donor clip-strip (docs/decisions/dc1/theri/THERI-CLIP-STRIP-PLAN.md) --------------------------------------------

    /// <summary>One animation clip in a species' motion table: its <paramref name="Index"/> in the table,
    /// its source RDT byte range <c>[Off, Off+Size)</c>.</summary>
    public readonly record struct MotionClip(int Index, int Off, int Size);

    /// <summary>
    /// Parse a species' <b>motion clip table</b> — the contiguous array of file-form pointers starting at
    /// <paramref name="motionHead"/>, one per animation clip, each pointing at the clip's payload
    /// (docs/decisions/dc1/theri/THERI-CLIP-STRIP-PLAN.md). The table ends at the first byte a clip occupies (we have walked
    /// into clip data) or the first non-pointer dword. Each clip's size is the gap to the next-higher clip
    /// start, the last running to <paramref name="ceiling"/> (the resource ceiling). Returned in table order.
    /// </summary>
    public static List<MotionClip> MotionClipTable(ReadOnlySpan<byte> rdt, int motionHead, int ceiling)
    {
        var targets = new List<int>();
        int firstTarget = int.MaxValue;
        for (int o = motionHead; o + 4 <= ceiling && o < firstTarget; o += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(o, 4));
            if (!IsFileFormPtr(v, rdt.Length)) break;
            int t = (int)(v - PsxBase);
            if (t < motionHead || t >= ceiling) break;   // table entries point into the motion region
            firstTarget = Math.Min(firstTarget, t);
            targets.Add(t);
        }
        var starts = targets.Distinct().OrderBy(x => x).ToList();
        int SizeOf(int start)
        {
            int i = starts.IndexOf(start);
            int hi = i + 1 < starts.Count ? starts[i + 1] : ceiling;
            return hi - start;
        }
        var clips = new List<MotionClip>(targets.Count);
        for (int i = 0; i < targets.Count; i++) clips.Add(new MotionClip(i, targets[i], SizeOf(targets[i])));
        return clips;
    }

    /// <summary>
    /// Extract a species' resource (<see cref="ExtractRangeSet"/>) <b>shrunk to fit</b>
    /// <paramref name="maxDonorBytes"/> by dropping the largest animation clips: the dropped clips' payloads
    /// are excluded from the range set and their motion-table slots are repointed to a kept fallback clip
    /// (default clip&#160;0, the idle), so every table index 0..N still resolves to a structurally valid clip
    /// (docs/decisions/dc1/theri/THERI-CLIP-STRIP-PLAN.md). Index&#160;0 is never dropped (it is the fallback). The result is a
    /// normal <see cref="SpeciesBlockSet"/> that imports through the same append path as the un-stripped donor.
    ///
    /// <para>Crash-safe by construction (a dropped-then-played index plays the fallback clip, never a dangling
    /// pointer); whether a dropped clip is ever actually played — and so whether the fallback ever shows — is
    /// the cosmetic CE gate the live capture closes.</para>
    /// </summary>
    /// <param name="protectedClips">Clip indices that must <b>never</b> be dropped (in addition to index&#160;0)
    /// — the live-captured <i>used</i> set plus any conservatively-kept clip (e.g. the death animation), so a
    /// strip never silently removes a clip known/likely to play. Pass empty for pure largest-first.</param>
    /// <param name="droppedClips">Number of clips dropped (0 = donor already fit).</param>
    /// <param name="droppedBytes">Total clip-payload bytes removed.</param>
    /// <exception cref="InvalidOperationException">When even dropping every droppable clip cannot bring the
    /// donor to <paramref name="maxDonorBytes"/>.</exception>
    public static SpeciesBlockSet ExtractRangeSetStripped(ReadOnlySpan<byte> rdt, uint modelPtr, uint motionPtr,
                                                          int maxDonorBytes, IReadOnlyCollection<int> protectedClips,
                                                          out int droppedClips, out int droppedBytes)
    {
        int modelHead = (int)(modelPtr - PsxBase);
        int motionHead = (int)(motionPtr - PsxBase);
        var ranges = ReachabilityExtent(rdt, modelHead, motionHead);
        if (ranges.Count == 0) throw new InvalidOperationException("closure extent is empty");
        int total = ranges.Sum(r => r.Hi - r.Lo);
        droppedClips = 0; droppedBytes = 0;

        if (total <= maxDonorBytes)   // already fits — no strip needed; defer to the plain extractor
            return ExtractRangeSet(rdt, modelPtr, motionPtr);

        int ceiling = ranges.Max(r => r.Hi);
        var clips = MotionClipTable(rdt, motionHead, ceiling);
        if (clips.Count == 0)
            throw new InvalidOperationException("no motion clip table at motion head — cannot clip-strip this donor");
        int fallback = clips[0].Off;   // index 0 (idle) is the fallback and is never dropped

        // Choose the smallest set of droppable clips (largest-first; never index 0 or a protected/used clip).
        int need = total - maxDonorBytes;
        var byBiggest = clips.Where(c => c.Index != 0 && !protectedClips.Contains(c.Index))
                             .OrderByDescending(c => c.Size).ToList();
        var drop = new List<MotionClip>();
        int freed = 0;
        foreach (var c in byBiggest)
        {
            if (freed >= need) break;
            drop.Add(c); freed += c.Size;
        }
        if (freed < need)
            throw new InvalidOperationException(
                $"clip-strip cannot free enough: need {need} B but only {freed} B is droppable (room too big for the Theri even stripped)");

        droppedClips = drop.Count; droppedBytes = freed;
        var dropIvs = drop.Select(c => (Lo: c.Off, Hi: c.Off + c.Size)).ToList();

        // Repoint each dropped clip's table slot to the fallback clip (in a working copy — the table is kept).
        var work = rdt.ToArray();
        foreach (var c in drop)
            BinaryPrimitives.WriteUInt32LittleEndian(work.AsSpan(motionHead + c.Index * 4, 4),
                                                     PsxBase + (uint)fallback);

        // Kept coverage = the closure ranges minus the dropped clip intervals (each interval lies inside one
        // range; subtracting splits that range). The table region is never an interval, so it is fully kept.
        var kept = new List<(int Lo, int Hi)>();
        foreach (var (lo, hi) in ranges)
        {
            var cuts = dropIvs.Where(d => d.Lo >= lo && d.Hi <= hi).OrderBy(d => d.Lo).ToList();
            int cur = lo;
            foreach (var d in cuts) { if (d.Lo > cur) kept.Add((cur, d.Lo)); cur = d.Hi; }
            if (cur < hi) kept.Add((cur, hi));
        }

        return BuildRangeSet(work, kept, modelHead, motionHead);
    }

    /// <summary>Build a <see cref="SpeciesBlockSet"/> from explicit, ascending, disjoint source
    /// <paramref name="ranges"/> of <paramref name="rdt"/> — the shared core of <see cref="ExtractRangeSet"/>
    /// (reachability ranges) and <see cref="ExtractRangeSetStripped"/> (reachability minus dropped clips).
    /// Records every in-range file-form pointer as a piecewise <see cref="RelocSlot"/>; <b>throws</b> on any
    /// aligned in-bounds escape outside all ranges (a real uncopied resource), leaves class (b)/(c) absolute.</summary>
    private static SpeciesBlockSet BuildRangeSet(ReadOnlySpan<byte> rdt, IReadOnlyList<(int Lo, int Hi)> ranges,
                                                 int modelHead, int motionHead)
    {
        int total = ranges.Sum(r => r.Hi - r.Lo);
        var bytes = new byte[total];
        var rangeList = new List<SpeciesRange>(ranges.Count);
        int blobOff = 0;
        foreach (var (lo, hi) in ranges)
        {
            rdt.Slice(lo, hi - lo).CopyTo(bytes.AsSpan(blobOff));
            rangeList.Add(new SpeciesRange(lo, hi, blobOff));
            blobOff += hi - lo;
        }

        int RangeOf(int x)
        {
            for (int i = 0; i < ranges.Count; i++)
                if (x >= ranges[i].Lo && x < ranges[i].Hi) return i;
            return -1;
        }

        var slots = new List<RelocSlot>();
        foreach (var r in rangeList)
            for (int o = r.SourceLo; o + 4 <= r.SourceHi; o += 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(o, 4));
                if (v < PsxBase || v >= PsxBase + (uint)rdt.Length) continue;
                int t = (int)(v - PsxBase);
                int ti = RangeOf(t);
                if (ti >= 0)
                    slots.Add(new RelocSlot(r.BlobOffset + (o - r.SourceLo), ti));
                else if (t >= HeaderThreshold && (t & 3) == 0)
                    throw new InvalidOperationException(
                        $"closure escape: slot 0x{o:x} -> 0x{t:x} is an aligned, in-bounds target outside all donor ranges");
            }

        return new SpeciesBlockSet(bytes, rangeList, slots, modelHead, motionHead);
    }

    /// <summary>Pure largest-first overload (protects only the fallback clip&#160;0). See the
    /// <see cref="ExtractRangeSetStripped(ReadOnlySpan{byte},uint,uint,int,IReadOnlyCollection{int},out int,out int)"/>
    /// primary for the protected-set form.</summary>
    public static SpeciesBlockSet ExtractRangeSetStripped(ReadOnlySpan<byte> rdt, uint modelPtr, uint motionPtr,
                                                          int maxDonorBytes, out int droppedClips, out int droppedBytes)
        => ExtractRangeSetStripped(rdt, modelPtr, motionPtr, maxDonorBytes, Array.Empty<int>(),
                                   out droppedClips, out droppedBytes);

    /// <summary>Build a clip-stripped <see cref="SpeciesDonorMulti"/> from a source room's enemy record so the
    /// import fits <paramref name="maxDonorBytes"/> (docs/decisions/dc1/theri/THERI-CLIP-STRIP-PLAN.md). <paramref name="protectedClips"/>
    /// (the live-captured used set + conservatively-kept clips) are never dropped. The clip-strip counterpart of
    /// <see cref="ExtractDonorClosure"/>.</summary>
    public static SpeciesDonorMulti ExtractDonorClosureStripped(ReadOnlySpan<byte> rdt, EnemyRecord src,
                                                                int maxDonorBytes, IReadOnlyCollection<int> protectedClips,
                                                                out int droppedClips, out int droppedBytes)
    {
        var set = ExtractRangeSetStripped(rdt, src.OriginalModelPtr, src.OriginalMotionPtr,
                                          maxDonorBytes, protectedClips, out droppedClips, out droppedBytes);
        return new SpeciesDonorMulti(src.Species, src.Category, set);
    }

    /// <summary>
    /// Resolve the donor model's texture from its room package: read the model's tpage/CLUT codes
    /// from its primitives (<see cref="TextureImporter.ReadModelTextureCodes"/>) and extract the
    /// covering texture + palette block (<see cref="TextureImporter.ExtractSpeciesTexture"/>). Returns
    /// null (geometry-only import) when the room does not upload the model's texture/palette — e.g. the
    /// donor record's model is a scripted set-piece whose skin lives in a different room. Attach the
    /// result to a donor with <c>donor with { Texture = ... }</c>.
    /// </summary>
    public static TextureBlock? TryExtractTexture(ReadOnlySpan<byte> donorPackage, byte[] donorRdt, uint modelPtr)
    {
        try
        {
            var (tpages, clut) = TextureImporter.ReadModelTextureCodes(donorRdt, modelPtr);
            return TextureImporter.ExtractSpeciesTexture(donorPackage, tpages, clut);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Blob offset (into <see cref="SpeciesBlockSet.Bytes"/>) of a donor-range source offset.</summary>
    private static int BlobOffsetOfHead(SpeciesBlockSet set, int sourceHead)
    {
        foreach (var r in set.Ranges)
            if (sourceHead >= r.SourceLo && sourceHead < r.SourceHi)
                return r.BlobOffset + (sourceHead - r.SourceLo);
        throw new InvalidOperationException($"head 0x{sourceHead:x} is not inside any donor range");
    }

    /// <summary>Blob offset of a pointer slot's <i>target</i> (the byte it points at), via the slot's
    /// target range.</summary>
    private static int TargetBlobOffset(SpeciesBlockSet set, RelocSlot slot)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(set.Bytes.AsSpan(slot.BlobOffset, 4));
        var tr = set.Ranges[slot.TargetRangeIndex];
        return tr.BlobOffset + ((int)(v - PsxBase) - tr.SourceLo);
    }

    /// <summary>One contiguous destination span of a (possibly split) donor placement: donor
    /// <see cref="SpeciesBlockSet.Bytes"/> range <c>[BlobStart, BlobStart+Length)</c> lands at RDT offset
    /// <c>DestStart</c>. A placement's segments partition <c>[0, blobLen)</c> in ascending blob order with
    /// 4-aligned destinations; a 4-aligned, <b>structure-boundary</b> split point (see
    /// <see cref="PlaceRangeSetSplit"/>) keeps every donor pointer dword — and every read-contiguously
    /// structure — wholly inside one segment.</summary>
    public readonly record struct PlacementSegment(int BlobStart, int DestStart, int Length);

    /// <summary>
    /// Import a multi-range donor into <paramref name="targetRdt"/>: compact-append the blob (4-aligned)
    /// and relocate every recorded slot by <b>its target range's</b> append delta (the piecewise map).
    /// Returns the grown buffer and the imported model/motion file-form head pointers to write into the
    /// target enemy record. Tolerated (b)/(c) escapes are not in <see cref="SpeciesBlockSet.Slots"/>, so
    /// they are copied verbatim (left absolute) for free.
    /// </summary>
    public static SpeciesImportResult ImportRangeSet(byte[] targetRdt, SpeciesBlockSet set)
        => PlaceRangeSetAt(targetRdt, set, (targetRdt.Length + 3) & ~3);

    /// <summary>
    /// Lay a multi-range donor down contiguously at an arbitrary 4-aligned <paramref name="baseOff"/> in
    /// <paramref name="targetRdt"/> — the placement behind both <see cref="ImportRangeSet"/> (append, where
    /// <c>baseOff = align4(length)</c> grows the buffer) and the contiguous-fit overlay-in-place
    /// (docs/decisions/dc1/theri/THERI-RDT-MEMORY-LIBERATION-PLAN.md), where <paramref name="baseOff"/> is the swapped-out
    /// enemy's region start and the blob fits within the existing buffer so it does <b>not</b> grow.
    /// </summary>
    public static SpeciesImportResult PlaceRangeSetAt(byte[] targetRdt, SpeciesBlockSet set, int baseOff)
    {
        if (baseOff < 0 || (baseOff & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(baseOff), $"base 0x{baseOff:x} must be non-negative and 4-aligned");
        return PlaceRangeSetSegmented(targetRdt, set, new[] { new PlacementSegment(0, baseOff, set.Bytes.Length) });
    }

    /// <summary>
    /// <b>Overlay-with-overflow</b> placement (docs/decisions/dc1/theri/THERI-RDT-MEMORY-LIBERATION-PLAN.md): reuse the
    /// swapped-out enemy's hole at <paramref name="regionLo"/> for as much of the donor as fits, then APPEND
    /// the remainder past the buffer end — so a donor larger than the hole still avoids growing the buffer by
    /// its full size. The donor is split at a <b>structure boundary</b> (the largest pointed-to partition
    /// point ≤ <paramref name="regionBytes"/>, via <see cref="SafeCut"/>) so no read-contiguous structure is
    /// torn across the two non-adjacent destinations; cross-segment pointers are relocated to whichever piece
    /// their target lands in. When the whole donor fits the hole this degenerates to the in-place overlay;
    /// when no structure boundary fits the hole it degenerates to a plain append.
    /// </summary>
    public static SpeciesImportResult PlaceRangeSetSplit(byte[] targetRdt, SpeciesBlockSet set,
                                                         int regionLo, int regionBytes)
    {
        if (regionLo < 0 || (regionLo & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(regionLo), $"region 0x{regionLo:x} must be non-negative and 4-aligned");
        if (regionBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(regionBytes), "region size must be non-negative");

        int reuse = Math.Min(set.Bytes.Length, regionBytes);
        if (reuse < set.Bytes.Length) reuse = SafeCut(set, reuse);   // round down to a tear-free boundary
        if (reuse == 0)                                              // nothing safely fits the hole: plain append
            return PlaceRangeSetAt(targetRdt, set, (targetRdt.Length + 3) & ~3);

        var segments = new List<PlacementSegment> { new(0, regionLo, reuse) };
        if (reuse < set.Bytes.Length)
            segments.Add(new PlacementSegment(reuse, (targetRdt.Length + 3) & ~3, set.Bytes.Length - reuse));
        return PlaceRangeSetSegmented(targetRdt, set, segments);
    }

    /// <summary>The largest 4-aligned <b>structure boundary</b> of <paramref name="set"/> that is ≤
    /// <paramref name="maxReuse"/> — a blob offset that heads a donor structure (a range start, a head, or a
    /// pointer <i>target</i>), so cutting the donor there tears no read-contiguous structure. 0 (the blob
    /// start) is always a valid boundary.</summary>
    private static int SafeCut(SpeciesBlockSet set, int maxReuse)
    {
        int best = 0;
        void Consider(int p) { if (p > best && p <= maxReuse && (p & 3) == 0) best = p; }
        Consider(BlobOffsetOfHead(set, set.ModelHead));
        Consider(BlobOffsetOfHead(set, set.MotionHead));
        foreach (var r in set.Ranges) Consider(r.BlobOffset);
        foreach (var slot in set.Slots) Consider(TargetBlobOffset(set, slot));
        return best;
    }

    /// <summary>
    /// Placement core: copy each donor <see cref="PlacementSegment"/> to its destination span (growing the
    /// buffer only for a segment past the current end) and relocate every recorded pointer slot to the
    /// destination offset of its target's blob byte — handling cross-segment pointers (the destination-side
    /// piecewise map). All placement shapes — append (<see cref="ImportRangeSet"/>), in-place overlay
    /// (<see cref="PlaceRangeSetAt"/>) and overlay-with-overflow (<see cref="PlaceRangeSetSplit"/>) — flow
    /// through here as one segment list.
    /// </summary>
    public static SpeciesImportResult PlaceRangeSetSegmented(byte[] targetRdt, SpeciesBlockSet set,
                                                             IReadOnlyList<PlacementSegment> segments)
    {
        if (segments.Count == 0) throw new ArgumentException("no placement segments", nameof(segments));

        int covered = 0, end = targetRdt.Length;
        foreach (var s in segments)
        {
            if (s.BlobStart != covered || s.Length <= 0)
                throw new ArgumentException($"segments must partition [0,{set.Bytes.Length}) in ascending blob order", nameof(segments));
            if ((s.DestStart & 3) != 0)
                throw new ArgumentException($"segment dest 0x{s.DestStart:x} must be 4-aligned", nameof(segments));
            covered += s.Length;
            end = Math.Max(end, s.DestStart + s.Length);
        }
        if (covered != set.Bytes.Length)
            throw new ArgumentException($"segments cover {covered} of {set.Bytes.Length} donor bytes", nameof(segments));

        var result = new byte[end];
        Array.Copy(targetRdt, 0, result, 0, targetRdt.Length);
        foreach (var s in segments)
            Array.Copy(set.Bytes, s.BlobStart, result, s.DestStart, s.Length);

        int DestOf(int blobOff)
        {
            foreach (var s in segments)
                if (blobOff >= s.BlobStart && blobOff < s.BlobStart + s.Length)
                    return s.DestStart + (blobOff - s.BlobStart);
            throw new InvalidOperationException($"blob offset 0x{blobOff:x} is outside all placement segments");
        }

        foreach (var slot in set.Slots)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(DestOf(slot.BlobOffset), 4),
                                                     PsxBase + (uint)DestOf(TargetBlobOffset(set, slot)));

        return new SpeciesImportResult(result,
            PsxBase + (uint)DestOf(BlobOffsetOfHead(set, set.ModelHead)),
            PsxBase + (uint)DestOf(BlobOffsetOfHead(set, set.MotionHead)));
    }

    /// <summary>
    /// The contiguous RDT span owned solely by the enemy whose resource heads are at <paramref name="modelHead"/>
    /// and <paramref name="motionHead"/> — the region safe to <b>overwrite</b> when that enemy is swapped out
    /// (overlay-in-place, docs/decisions/dc1/theri/THERI-RDT-MEMORY-LIBERATION-PLAN.md). It runs from the lower of the two heads
    /// up to the next live resource head above it — any <paramref name="otherLiveHeads"/> entry (every OTHER
    /// record's model/motion head) or <see cref="HeaderPointerTargets"/> entry (the RDT tail-structure heads) —
    /// capped at the buffer end. Returns null when the span does not contain <b>both</b> heads (the enemy's
    /// model and motion are interleaved with another live resource, so there is no single contiguous hole).
    ///
    /// <para>Safety rests on the same boundary assumption the closure extractor uses: a referenced resource
    /// head is a genuine resource start, so nothing live extends across it from below — thus <c>[lo, hi)</c>
    /// is the swapped-out enemy's own territory (its data + any trailing padding), safe to overwrite. Whether
    /// the room <i>reads</i> that territory by absolute layout after the swap is the documented CE gate;
    /// overlay leaves valid replacement bytes there, never freed memory.</para>
    /// </summary>
    public static (int Lo, int Hi)? OverlayRegion(ReadOnlySpan<byte> rdt, int modelHead, int motionHead,
                                                  IEnumerable<int> otherLiveHeads)
    {
        int lo = Math.Min(modelHead, motionHead);
        if (lo < 0 || lo >= rdt.Length) return null;

        int hi = rdt.Length;
        foreach (int b in otherLiveHeads) if (b > lo && b < hi) hi = b;
        foreach (int t in HeaderPointerTargets(rdt)) if (t > lo && t < hi) hi = t;

        int max = Math.Max(modelHead, motionHead);
        return max >= hi ? null : (lo, hi);   // both heads must fall inside the single contiguous span
    }
}
