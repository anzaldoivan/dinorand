namespace DinoRand.FileFormats.Stage;

/// <summary>The constraint that decided an <see cref="EnemyImportBudget"/> evaluation — what limited the
/// import (or <see cref="None"/> when it fits with room to spare).</summary>
public enum ImportFitConstraint
{
    /// <summary>Fits as-is; no clip-strip and no VRAM problem.</summary>
    None,
    /// <summary>Fits, but only after dropping <see cref="EnemyImportFit.DroppedClips"/> animation clips
    /// (the entangled-donor clip-strip, within the drop cap).</summary>
    ClipStrip,
    /// <summary>Refused: the donor cannot be brought under the RDT budget — either the grown RDT crosses the
    /// ceiling/floor and no clip-strip can free enough (entangled donor), or the appended single-range
    /// model+motion overruns the ceiling.</summary>
    RdtBudget,
    /// <summary>Refused: the donor fits the RDT only by dropping more clips than the quality-bounded cap
    /// (<c>maxClipDrops</c>) allows — see <see cref="SpeciesImporter.MaxClipStripDropCount"/>.</summary>
    ClipStripBudget,
    /// <summary>Refused: the donor texture has no free VRAM region in the target room
    /// (<see cref="TextureImporter.PickFreeRegion"/> found no column / palette row).</summary>
    Vram,
}

/// <summary>
/// The verdict of an <see cref="EnemyImportBudget"/> fit evaluation: whether the donor fits, the
/// clip-strip it would cost, the resulting RDT length, the VRAM relocation outcome, and the
/// <see cref="ImportFitConstraint"/> that decided it.
/// </summary>
/// <param name="Fits">True when the import can proceed (RDT and, if a texture was given, VRAM both fit).</param>
/// <param name="DroppedClips">Animation clips the entangled-donor strip would drop (0 = none / single-range);
/// surfaced even on a <see cref="ImportFitConstraint.ClipStripBudget"/> refusal so the caller can report how
/// many were needed.</param>
/// <param name="DroppedBytes">Total clip-payload bytes the strip would remove.</param>
/// <param name="ResultRdtLength">The target RDT length after the (possibly stripped) append.</param>
/// <param name="Limiting">The constraint that decided the verdict.</param>
/// <param name="TextureRect">The relocated donor-texture VRAM rect (when a texture was evaluated and fits).</param>
/// <param name="PaletteRect">The relocated donor-palette VRAM rect (when a texture was evaluated and fits).</param>
/// <param name="Reason">A human-readable one-line explanation (the refusal cause, or "fits").</param>
public sealed record EnemyImportFit(
    bool Fits,
    int DroppedClips,
    int DroppedBytes,
    int ResultRdtLength,
    ImportFitConstraint Limiting,
    VramRect? TextureRect,
    VramRect? PaletteRect,
    string Reason);

/// <summary>
/// The single reusable "will this enemy donor fit this room?" check shared by the Therizinosaurus swap
/// (<c>Program.SwapTherizinosaurus</c>) and the novel-species add path (<see cref="RoomFile.AddEnemyAt"/>),
/// covering BOTH the RDT-size and the VRAM constraints in one verdict
/// (docs/decisions/dc1/enemies/ADD-ENEMY-NOVEL-SPECIES-TECHDEBT.md "Risks").
///
/// <para><b>RDT.</b> A cross-imported species' model + motion is appended to the target RDT, which must stay
/// under the engine room buffer — <see cref="SpeciesImporter.EngineRoomRdtCeiling"/> (the hard upper bound)
/// or, for a pool-sharing species, the lower <see cref="SpeciesImporter.ResidentPoolFloor"/>. An entangled
/// donor (the Theri closure) that overruns the budget is clip-stripped to fit
/// (<see cref="SpeciesImporter.ExtractRangeSetStripped"/>); the import is refused when even the maximum
/// eligible strip cannot free enough, or when fitting would need more drops than the quality cap allows.</para>
///
/// <para><b>VRAM.</b> When a donor texture is supplied, its texture page + palette must relocate to a free
/// VRAM region of the target (<see cref="TextureImporter.PickFreeRegion"/>); with no free column / palette
/// row the import is refused. The RDT constraint is checked first, so it takes precedence as the limiter.
/// (The Theri swap places its texture by the fixed-column overwrite/append path instead, so it passes a
/// null texture here and evaluates the RDT budget only.)</para>
///
/// <para>Pure and DOM-free: it reuses <see cref="SpeciesImporter"/>'s constants / closure extractor /
/// clip-table and <see cref="TextureImporter.PickFreeRegion"/>, and never mutates a room — callers act on
/// the verdict (and, for the entangled donor, import the returned prepared block set).</para>
/// </summary>
public static class EnemyImportBudget
{
    /// <summary>
    /// Evaluate an <b>entangled / closure</b> donor (the Therizinosaurus) against a target room: extract its
    /// resource — clip-stripped to <paramref name="maxDonorBytes"/> when it overruns (largest-first, never a
    /// <paramref name="protectedClips"/> index) — and report whether the (possibly stripped) append fits, plus
    /// the prepared <paramref name="preparedDonor"/> block set to import when it does.
    /// </summary>
    /// <param name="donorRdt">The donor room's decompressed RDT buffer.</param>
    /// <param name="donorModelPtr">The donor enemy record's file-form model pointer.</param>
    /// <param name="donorMotionPtr">The donor enemy record's file-form motion pointer.</param>
    /// <param name="targetRdtLength">The target room's current RDT length (the append base is its 4-alignment).</param>
    /// <param name="maxDonorBytes">The largest donor blob that fits the RDT budget — typically
    /// <c>ResidentPoolFloor - align4(targetRdtLength)</c> (the caller owns the exact budget, incl. any override).</param>
    /// <param name="maxClipDrops">The quality-bounded cap on clips the strip may drop
    /// (<see cref="SpeciesImporter.MaxClipStripDropCount"/>); more than this is refused.</param>
    /// <param name="protectedClips">Clip indices the strip must never drop (live-used + likely-death set).</param>
    /// <param name="targetPackage">The target room package bytes, for the VRAM check (empty to skip).</param>
    /// <param name="donorTexture">The donor texture to relocate, or null to evaluate the RDT budget only.</param>
    /// <param name="preparedDonor">The (possibly stripped) block set to import when <see cref="EnemyImportFit.Fits"/>;
    /// null on refusal.</param>
    public static EnemyImportFit Evaluate(
        ReadOnlySpan<byte> donorRdt, uint donorModelPtr, uint donorMotionPtr,
        int targetRdtLength, int maxDonorBytes, int maxClipDrops,
        IReadOnlyCollection<int> protectedClips,
        ReadOnlySpan<byte> targetPackage, TextureBlock? donorTexture,
        out SpeciesBlockSet? preparedDonor)
    {
        preparedDonor = null;

        // Extract clip-stripped to the budget (a no-op strip when the donor already fits). InvalidOperationException
        // here means the resource cannot be brought under the budget by any eligible strip -> RDT refusal.
        SpeciesBlockSet prepared;
        int droppedClips, droppedBytes;
        try
        {
            prepared = SpeciesImporter.ExtractRangeSetStripped(donorRdt, donorModelPtr, donorMotionPtr,
                maxDonorBytes, protectedClips, out droppedClips, out droppedBytes);
        }
        catch (InvalidOperationException ex)
        {
            return new EnemyImportFit(false, 0, 0, 0, ImportFitConstraint.RdtBudget, null, null, ex.Message);
        }

        int resultRdtLength = ((targetRdtLength + 3) & ~3) + prepared.Bytes.Length;

        if (droppedClips > maxClipDrops)
            return new EnemyImportFit(false, droppedClips, droppedBytes, resultRdtLength,
                ImportFitConstraint.ClipStripBudget, null, null,
                $"needs {droppedClips} clips dropped ({droppedBytes} B) to fit, over the cap of {maxClipDrops}");

        var (vramOk, texRect, palRect, vramReason) = EvaluateVram(targetPackage, donorTexture);
        if (!vramOk)
            return new EnemyImportFit(false, droppedClips, droppedBytes, resultRdtLength,
                ImportFitConstraint.Vram, null, null, vramReason!);

        preparedDonor = prepared;
        var constraint = droppedClips > 0 ? ImportFitConstraint.ClipStrip : ImportFitConstraint.None;
        return new EnemyImportFit(true, droppedClips, droppedBytes, resultRdtLength, constraint,
            texRect, palRect, droppedClips > 0 ? $"fits after dropping {droppedClips} clips" : "fits");
    }

    /// <summary>
    /// Evaluate a <b>single-range</b> donor (an ordinary <see cref="SpeciesDonor"/> model + motion, the novel
    /// <see cref="RoomFile.AddEnemyAt"/> import) against a target room: the appended model + motion must keep
    /// the RDT under <paramref name="rdtCeiling"/>, and (when a texture is given) the donor texture must
    /// relocate to a free VRAM region. There is no clip-strip on this path
    /// (<see cref="EnemyImportFit.DroppedClips"/> is always 0).
    /// </summary>
    /// <param name="model">The donor's relocatable model block.</param>
    /// <param name="motion">The donor's relocatable motion block.</param>
    /// <param name="targetRdtLength">The target room's current RDT length.</param>
    /// <param name="rdtCeiling">The RDT length the grown buffer must not exceed
    /// (<see cref="SpeciesImporter.EngineRoomRdtCeiling"/> for a non-pool species).</param>
    /// <param name="targetPackage">The target room package bytes, for the VRAM check (empty to skip).</param>
    /// <param name="donorTexture">The donor texture to relocate, or null to evaluate the RDT budget only.</param>
    public static EnemyImportFit Evaluate(
        SpeciesBlock model, SpeciesBlock motion,
        int targetRdtLength, int rdtCeiling,
        ReadOnlySpan<byte> targetPackage, TextureBlock? donorTexture)
    {
        // Mirror SpeciesImporter.Import's append: model at align4(len), then motion at align4 of that.
        int afterModel = ((targetRdtLength + 3) & ~3) + model.Bytes.Length;
        int resultRdtLength = ((afterModel + 3) & ~3) + motion.Bytes.Length;

        if (resultRdtLength > rdtCeiling)
            return new EnemyImportFit(false, 0, 0, resultRdtLength, ImportFitConstraint.RdtBudget, null, null,
                $"appended RDT {resultRdtLength} B exceeds the ceiling {rdtCeiling} B by {resultRdtLength - rdtCeiling} B");

        var (vramOk, texRect, palRect, vramReason) = EvaluateVram(targetPackage, donorTexture);
        if (!vramOk)
            return new EnemyImportFit(false, 0, 0, resultRdtLength, ImportFitConstraint.Vram, null, null, vramReason!);

        return new EnemyImportFit(true, 0, 0, resultRdtLength, ImportFitConstraint.None, texRect, palRect, "fits");
    }

    /// <summary>
    /// The VRAM half of the fit check: can <paramref name="donorTexture"/> relocate to a free region of
    /// <paramref name="targetPackage"/> (<see cref="TextureImporter.PickFreeRegion"/>)? A null texture is
    /// trivially OK (geometry-only, or a fixed-column texture path the caller handles itself). Returns the
    /// placed texture/palette rects on success, or a reason on failure.
    /// </summary>
    public static (bool Ok, VramRect? Texture, VramRect? Palette, string? Reason) EvaluateVram(
        ReadOnlySpan<byte> targetPackage, TextureBlock? donorTexture)
    {
        if (donorTexture is null)
            return (true, null, null, null);
        try
        {
            var placed = TextureImporter.PickFreeRegion(targetPackage, donorTexture);
            return (true, placed.Texture.Dst, placed.Palette.Dst, null);
        }
        catch (InvalidOperationException ex)
        {
            return (false, null, null, ex.Message);
        }
    }
}
