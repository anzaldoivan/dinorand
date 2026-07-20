using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Passes;

/// <summary>Performs the heavy per-room cross-species import for <see cref="CrossSpeciesEnemyPass"/>. Behind
/// an interface so the pass's orchestration (selection, request accumulation, logging) is unit-testable with a
/// fake, while the real importer (which needs decoded RDTs/textures) is exercised by a gated corpus test.</summary>
public interface ICrossSpeciesImporter
{
    /// <summary>Species this importer can actually place (an enabled catalog species with an extractable donor).</summary>
    IReadOnlyCollection<DinoSpecies> AvailableDonors { get; }

    /// <summary>
    /// Import <paramref name="def"/>'s species over the room's victim at <paramref name="victimIdx"/>, mutating
    /// <paramref name="room"/> (and, when the import is a post-serialization byte transform like a texture, setting
    /// the room's output override on <paramref name="context"/>). Returns the EXE patches the placement needs (empty
    /// for a no-patch species), or <c>false</c> with a <paramref name="note"/> when the room can't fit it (skip).
    /// </summary>
    bool TryImport(RandomizationContext context, RoomFile room, int victimIdx, ExoticSpeciesDef def,
                   out IReadOnlyList<ExePatchRequest> patches, out string note);
}

/// <summary>
/// The real importer: reuses <see cref="SpeciesImporter"/> / <see cref="TextureImporter"/> with the same recipe
/// as the CLI <c>--swap-species</c> path (docs/decisions/dc1/theri/THERI-0203-SWAP-PLAN.md / TREX-INTO-0102-FEASIBILITY.md), and
/// emits the matching <see cref="ExePatchRequest"/>s instead of calling <see cref="GameInstaller"/> directly.
/// Only the enabled catalog species with a usable donor in the corpus are offered.
/// </summary>
public sealed class CrossSpeciesImporter : ICrossSpeciesImporter
{
    // The Therizinosaurus VRAM identity (data/dc1/enemy-textures.json cat8): X=640 column, palette row 511.
    private static readonly ushort[] TheriTpages = { 0x8a, 0x9a };
    private const ushort TheriClut = 0x7ff0;
    private static readonly int[] TheriProtectedClips = { 1, 12, 13, 15, 19, 48 };

    private readonly IReadOnlyList<RoomFile> _corpus;
    private readonly Dictionary<DinoSpecies, SpeciesDonor> _grounded = new();
    private TheriDonor? _theri;
    private bool _theriSearched;

    public CrossSpeciesImporter(IReadOnlyList<RoomFile> corpus) => _corpus = corpus;

    public IReadOnlyCollection<DinoSpecies> AvailableDonors
    {
        get
        {
            var set = new HashSet<DinoSpecies>();
            foreach (var def in ExoticSpeciesCatalog.Enabled)
                if (HasDonor(def)) set.Add(def.Species);
            return set;
        }
    }

    private bool HasDonor(ExoticSpeciesDef def) => def.Kind switch
    {
        PlacementKind.Cat8Therizinosaurus => FindTheriDonor() is not null,
        PlacementKind.SameCategoryNoPatch => FindGroundedDonor(def.Species) is not null,
        _ => false, // gated kinds are never enabled this increment
    };

    public bool TryImport(RandomizationContext context, RoomFile room, int victimIdx, ExoticSpeciesDef def,
                          out IReadOnlyList<ExePatchRequest> patches, out string note)
    {
        patches = Array.Empty<ExePatchRequest>();
        return def.Kind switch
        {
            PlacementKind.SameCategoryNoPatch => TryImportGrounded(room, victimIdx, def, out note),
            PlacementKind.Cat8Therizinosaurus => TryImportTheri(context, room, victimIdx, out patches, out note),
            _ => Fail(out note, $"{def.Species} placement kind {def.Kind} is not enabled"),
        };
    }

    // ---- Grounded same-category (RaptorHeavy): geometry+texture into the RoomFile, no EXE patch ----

    private bool TryImportGrounded(RoomFile room, int victimIdx, ExoticSpeciesDef def, out string note)
    {
        var donor = FindGroundedDonor(def.Species);
        if (donor is null) { note = $"no {def.Species} donor"; return false; }
        var tex = room.ImportSpeciesTextured(donor, victimIdx);
        note = tex.Outcome switch
        {
            RoomFile.TextureImportOutcome.Relocated => "geometry + texture",
            RoomFile.TextureImportOutcome.ReclaimedVictim => "geometry + texture (victim column reclaimed)",
            _ => "geometry only",
        };
        return true;
    }

    private SpeciesDonor? FindGroundedDonor(DinoSpecies species)
    {
        if (_grounded.TryGetValue(species, out var cached)) return cached;
        foreach (var room in _corpus)
        {
            var rec = room.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == species);
            if (rec is null) continue;
            try
            {
                var donor = SpeciesImporter.ExtractDonor(room.RdtBuffer, rec, ResourceHeads(room))
                    with { Texture = SpeciesImporter.TryExtractTexture(room.OriginalBytes, room.RdtBuffer, rec.OriginalModelPtr) };
                _grounded[species] = donor;
                return donor;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
        }
        return null;
    }

    // ---- Cat-8 Therizinosaurus: closure import + clip-strip + texture + 3 EXE patches ----

    private bool TryImportTheri(RandomizationContext context, RoomFile room, int victimIdx,
                               out IReadOnlyList<ExePatchRequest> patches, out string note)
    {
        patches = Array.Empty<ExePatchRequest>();
        var d = FindTheriDonor();
        if (d is null) { note = "no Therizinosaurus donor"; return false; }

        // Import into a FRESH parse of the room, not the shared instance: the geometry mutation happens before
        // the clip-strip/ceiling/texture checks that can fail, so committing only on success (via the output
        // override) guarantees a skip never leaves the runner writing a half-imported room.
        var work = RoomFile.Read(room.Stage, room.Room, room.OriginalBytes);

        // Geometry: closure import, auto clip-strip to fit ≤ the resident-pool floor (defect A).
        int vanillaLen = work.RdtBuffer.Length;
        int maxDonor = SpeciesImporter.ResidentPoolFloor - ((vanillaLen + 3) & ~3);
        var geoDonor = d.Donor;
        if (d.Donor.Blocks.Bytes.Length > maxDonor)
        {
            try
            {
                geoDonor = SpeciesImporter.ExtractDonorClosureStripped(
                    d.Rdt, d.Record, maxDonor, TheriProtectedClips, out int dropped, out _);
                if (dropped > SpeciesImporter.MaxClipStripDropCount)
                {
                    note = $"needs {dropped} clips dropped (> {SpeciesImporter.MaxClipStripDropCount} quality limit)";
                    return false;
                }
            }
            catch (InvalidOperationException ex) { note = $"too big for the Theri even after clip-strip: {ex.Message}"; return false; }
        }
        work.ImportSpecies(geoDonor, victimIdx);
        if (work.RdtBuffer.Length > SpeciesImporter.ResidentPoolFloor)
        {
            note = $"RDT {work.RdtBuffer.Length} B over the resident-pool floor {SpeciesImporter.ResidentPoolFloor} B";
            return false;
        }

        // Texture: fixed-column overwrite-or-append on the serialized bytes → output override (post-serialization).
        byte[] geo = work.Write();
        byte[] final;
        try { final = TextureImporter.ImportSpeciesTexture(geo, d.File, TheriTpages, TheriClut); }
        catch (InvalidOperationException ex) { note = $"texture import failed: {ex.Message}"; return false; }
        context.SetRoomOutput(room, final);

        // EXE patches: cat8 AI-slot (so the spawn dispatches to the real Theri AI), the cat8 hit-reaction fix
        // (sourced from the donor's RDT) + walker NULL-guard, and the per-room enemy SE retarget (Theri sounds).
        patches = new[]
        {
            ExePatchRequest.CatSlot(room.Stage, 8, ExePatcher.TheriCat8HandlerVa),
            ExePatchRequest.Cat8HitReaction(d.Stage, d.Room, d.FileName),
            ExePatchRequest.RoomEnemySe(room.Stage, room.Room, donorStage: 6, donorRoom: 5),
        };
        note = "cat8 closure + texture + 3 exe patches";
        return true;
    }

    /// <summary>Find a stage-6 Therizinosaurus donor (preferring st603, the canonical reaction host) whose model
    /// is closure-extractable and whose (640,0) texture is present. Cached.</summary>
    private TheriDonor? FindTheriDonor()
    {
        if (_theriSearched) return _theri;
        _theriSearched = true;
        foreach (var room in _corpus.OrderByDescending(r => r.Stage == 6 && r.Room == 0x03))
        {
            if (room.Stage != 6) continue; // reaction stream + native cat8 host live in stage 6
            var rec = room.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == DinoSpecies.Therizinosaurus);
            if (rec is null) continue;
            try
            {
                var donor = SpeciesImporter.ExtractDonorClosure(room.RdtBuffer, rec);
                _ = TextureImporter.ExtractSpeciesTexture(room.OriginalBytes, TheriTpages, TheriClut); // skin must be present
                _theri = new TheriDonor(donor, room.OriginalBytes, room.RdtBuffer, rec, room.Stage, room.Room,
                    $"st{room.Stage}{room.Room:X2}.dat");
                return _theri;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
        }
        return null;
    }

    private static IEnumerable<int> ResourceHeads(RoomFile room)
    {
        foreach (var e in room.Enemies)
        {
            if (e.OriginalModelPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalModelPtr - SpeciesImporter.PsxBase);
            if (e.OriginalMotionPtr >= SpeciesImporter.PsxBase)
                yield return (int)(e.OriginalMotionPtr - SpeciesImporter.PsxBase);
        }
    }

    private static bool Fail(out string note, string message) { note = message; return false; }

    private sealed record TheriDonor(
        SpeciesDonorMulti Donor, byte[] File, byte[] Rdt, EnemyRecord Record, int Stage, int Room, string FileName);
}
