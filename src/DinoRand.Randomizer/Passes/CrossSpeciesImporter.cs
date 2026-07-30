using DinoRand.FileFormats.Exe;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Passes;

public interface ICrossSpeciesImporter
{
    IReadOnlyCollection<DinoSpecies> AvailableDonors { get; }
    bool TryImport(RandomizationContext context, RoomFile room, int victimIdx, ExoticSpeciesDef def,
                   out IReadOnlyList<ExePatchRequest> patches, out string note);
}

public interface IPlannedCrossSpeciesImporter
{
    CrossSpeciesImportPlan? Plan(RoomFile room, int victimIdx, DinoSpecies species);
    CrossSpeciesPreflight Preflight(CrossSpeciesImportPlan plan);
    CrossSpeciesImportResult Apply(CrossSpeciesImportPlan plan, CrossSpeciesPreflight preflight);
}

public sealed class CrossSpeciesImportPlan
{
    internal CrossSpeciesImportPlan(RoomFile room, int victimIdx, ExoticSpeciesDef definition,
                                    SpeciesDonor? grounded, CrossSpeciesImporter.TheriDonor? theri)
    {
        Room = room;
        VictimIndex = victimIdx;
        Definition = definition;
        Grounded = grounded;
        Theri = theri;
    }

    public RoomFile Room { get; }
    public int VictimIndex { get; }
    public ExoticSpeciesDef Definition { get; }
    internal SpeciesDonor? Grounded { get; }
    internal CrossSpeciesImporter.TheriDonor? Theri { get; }
}

public sealed record CrossSpeciesPreflight(
    bool Success,
    string Note,
    byte[]? RoomBytes,
    IReadOnlyList<ExePatchRequest> Requests)
{
    public static CrossSpeciesPreflight Refused(string note)
        => new(false, note, null, Array.Empty<ExePatchRequest>());
}

public sealed record CrossSpeciesImportResult(
    byte[] RoomBytes,
    IReadOnlyList<ExePatchRequest> Requests,
    string Note);

/// <summary>Shared plan, preflight, and apply pipeline for bulk and single-room imports.</summary>
public sealed class CrossSpeciesImporter : ICrossSpeciesImporter, IPlannedCrossSpeciesImporter
{
    private static readonly ushort[] TheriTpages = { 0x8a, 0x9a };
    private const ushort TheriClut = 0x7ff0;
    private static readonly int[] TheriProtectedClips = { 1, 12, 13, 15, 19, 48 };

    private readonly IReadOnlyList<RoomFile> _corpus;
    private readonly int? _maxDonorOverride;
    private readonly Dictionary<DinoSpecies, SpeciesDonor> _grounded = new();
    private TheriDonor? _theri;
    private bool _theriSearched;

    public CrossSpeciesImporter(IReadOnlyList<RoomFile> corpus, int? maxDonorOverride = null)
    {
        _corpus = corpus;
        _maxDonorOverride = maxDonorOverride;
    }

    public IReadOnlyCollection<DinoSpecies> AvailableDonors
    {
        get
        {
            var donors = new HashSet<DinoSpecies>();
            foreach (var def in ExoticSpeciesCatalog.Enabled)
                if (HasDonor(def)) donors.Add(def.Species);
            return donors;
        }
    }

    public CrossSpeciesImportPlan? Plan(RoomFile room, int victimIdx, DinoSpecies species)
    {
        ArgumentNullException.ThrowIfNull(room);

        var def = ExoticSpeciesCatalog.For(species);
        if (def is not { Enabled: true } || !def.AllowsStage(room.Stage)) return null;
        if (victimIdx < 0 || victimIdx >= room.Enemies.Count) return null;
        if (!room.Enemies[victimIdx].IsRandomizableDino
            || room.Enemies[victimIdx].Species != DinoSpecies.Velociraptor) return null;

        return def.Kind switch
        {
            PlacementKind.SameCategoryNoPatch => FindGroundedDonor(species) is { } donor
                ? new(room, victimIdx, def, donor, null) : null,
            PlacementKind.Cat8Therizinosaurus => FindTheriDonor() is { } theri
                ? new(room, victimIdx, def, null, theri) : null,
            _ => null,
        };
    }

    public CrossSpeciesPreflight Preflight(CrossSpeciesImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            var work = RoomFile.Read(plan.Room.Stage, plan.Room.Room, plan.Room.Write());
            return plan.Definition.Kind switch
            {
                PlacementKind.SameCategoryNoPatch => PreflightGrounded(plan, work),
                PlacementKind.Cat8Therizinosaurus => PreflightTheri(plan, work),
                _ => CrossSpeciesPreflight.Refused($"{plan.Definition.Species} is disabled"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return CrossSpeciesPreflight.Refused(ex.Message);
        }
    }

    public CrossSpeciesImportResult Apply(CrossSpeciesImportPlan plan, CrossSpeciesPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(preflight);
        if (!preflight.Success || preflight.RoomBytes is null)
            throw new InvalidOperationException($"cannot apply refused cross-species import: {preflight.Note}");

        var committed = RoomFile.Read(plan.Room.Stage, plan.Room.Room, preflight.RoomBytes);
        if (!committed.ParsedCleanly)
            throw new InvalidOperationException("cross-species import serialized to an unparseable room");
        plan.Room.AdoptSerialized(preflight.RoomBytes);
        return new(preflight.RoomBytes, preflight.Requests, preflight.Note);
    }

    public bool TryImport(RandomizationContext context, RoomFile room, int victimIdx, ExoticSpeciesDef def,
                          out IReadOnlyList<ExePatchRequest> patches, out string note)
    {
        patches = Array.Empty<ExePatchRequest>();
        var plan = Plan(room, victimIdx, def.Species);
        if (plan is null) { note = "plan refused"; return false; }
        var preflight = Preflight(plan);
        if (!preflight.Success) { note = preflight.Note; return false; }
        var result = Apply(plan, preflight);
        if (def.Kind == PlacementKind.Cat8Therizinosaurus)
            context.SetRoomOutput(room, result.RoomBytes);
        patches = result.Requests;
        note = result.Note;
        return true;
    }

    private CrossSpeciesPreflight PreflightGrounded(CrossSpeciesImportPlan plan, RoomFile work)
    {
        if (plan.Grounded is null) return CrossSpeciesPreflight.Refused("no grounded donor");
        if (!work.TryImportSpeciesTexturedAtomic(plan.Grounded, plan.VictimIndex,
                SpeciesImporter.ResidentPoolFloor, out var fit, out var texture))
        {
            var reason = fit.Limiting == ImportFitConstraint.RdtBudget
                ? $"RDT refusal: {fit.Reason}"
                : $"texture/VRAM refusal: {fit.Reason}";
            return CrossSpeciesPreflight.Refused(reason);
        }

        var note = texture!.Outcome switch
        {
            RoomFile.TextureImportOutcome.Relocated => "geometry + texture relocated",
            RoomFile.TextureImportOutcome.ReclaimedVictim => "geometry + texture (victim column reclaimed)",
            _ => throw new InvalidOperationException($"atomic grounded import returned {texture.Outcome}"),
        };
        return new(true, note, work.Write(), Array.Empty<ExePatchRequest>());
    }

    private CrossSpeciesPreflight PreflightTheri(CrossSpeciesImportPlan plan, RoomFile work)
    {
        var donor = plan.Theri ?? throw new InvalidOperationException("no Therizinosaurus donor");
        int maxDonor = SpeciesImporter.ResidentPoolFloor - ((work.RdtBuffer.Length + 3) & ~3);
        if (_maxDonorOverride is { } forced && forced < maxDonor) maxDonor = forced;
        var geoDonor = donor.Donor;
        if (donor.Donor.Blocks.Bytes.Length > maxDonor)
        {
            geoDonor = SpeciesImporter.ExtractDonorClosureStripped(
                donor.Rdt, donor.Record, maxDonor, TheriProtectedClips, out int dropped, out _);
            if (dropped > SpeciesImporter.MaxClipStripDropCount)
                return CrossSpeciesPreflight.Refused(
                    $"needs {dropped} clips dropped (> {SpeciesImporter.MaxClipStripDropCount} quality limit)");
        }

        work.ImportSpecies(geoDonor, plan.VictimIndex);
        if (work.RdtBuffer.Length > SpeciesImporter.ResidentPoolFloor)
            return CrossSpeciesPreflight.Refused(
                $"RDT {work.RdtBuffer.Length} B over the resident-pool floor {SpeciesImporter.ResidentPoolFloor} B");

        byte[] final = TextureImporter.ImportSpeciesTexture(work.Write(), donor.File, TheriTpages, TheriClut);
        var requests = new List<ExePatchRequest>();
        if (plan.Room.Stage is 1 or 2)
            requests.Add(ExePatchRequest.CatSlot(plan.Room.Stage, 8, ExePatcher.TheriCat8HandlerVa));
        requests.Add(ExePatchRequest.Cat8HitReaction(donor.Stage, donor.Room, donor.FileName));
        requests.Add(ExePatchRequest.RoomEnemySe(plan.Room.Stage, plan.Room.Room, 6, 5));
        return new(true, "cat8 closure + texture + exe patches", final, requests);
    }

    private bool HasDonor(ExoticSpeciesDef def) => def.Kind switch
    {
        PlacementKind.Cat8Therizinosaurus => FindTheriDonor() is not null,
        PlacementKind.SameCategoryNoPatch => FindGroundedDonor(def.Species) is not null,
        _ => false,
    };

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

    private TheriDonor? FindTheriDonor()
    {
        if (_theriSearched) return _theri;
        _theriSearched = true;
        foreach (var room in _corpus.OrderByDescending(r => r.Stage == 6 && r.Room == 0x03))
        {
            if (room.Stage != 6) continue;
            var rec = room.Enemies.FirstOrDefault(e => e.IsRandomizableDino && e.Species == DinoSpecies.Therizinosaurus);
            if (rec is null) continue;
            try
            {
                var donor = SpeciesImporter.ExtractDonorClosure(room.RdtBuffer, rec);
                _ = TextureImporter.ExtractSpeciesTexture(room.OriginalBytes, TheriTpages, TheriClut);
                _theri = new(donor, room.OriginalBytes, room.RdtBuffer, rec, room.Stage, room.Room,
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

    internal sealed record TheriDonor(
        SpeciesDonorMulti Donor, byte[] File, byte[] Rdt, EnemyRecord Record,
        int Stage, int Room, string FileName);
}
