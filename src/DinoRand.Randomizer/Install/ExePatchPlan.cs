using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinoRand.Randomizer.Install;

/// <summary>
/// The kinds of EXE patch a randomization pass can request, each mapping 1:1 to an existing
/// <see cref="GameInstaller"/> method. Flat (non-polymorphic) so the sidecar JSON stays trivially
/// serializable and versionable. See docs/decisions/dc1/enemies/CROSS-SPECIES-PASS-PLAN.md §4a.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExePatchKind
{
    /// <summary>Set a stage's installed AI-handler category slot to a donor handler
    /// (<see cref="GameInstaller.PatchExeCatSlot"/>).</summary>
    CatSlot,

    /// <summary>Cave the cat-8 hit-reaction descriptor stream from a normal Theri donor room and repoint the
    /// reaction tables + install the walker NULL-guard (<see cref="GameInstaller.PatchExeCat8HitReaction"/>).</summary>
    Cat8HitReaction,

    /// <summary>Retarget a room's enemy SE block to a donor species' set
    /// (<see cref="GameInstaller.PatchExeRoomEnemySe"/>).</summary>
    RoomEnemySe,
}

/// <summary>
/// One declared EXE edit. Passes never touch <c>DINO.exe</c>; they append these to
/// <see cref="RandomizationContext.ExePatchRequests"/>, the runner serializes them to
/// <c>exe-patch-plan.json</c> beside the room <c>.dat</c>s, and <see cref="GameInstaller"/> applies them at
/// install time (additive, exe backed up once, reversed by <see cref="GameInstaller.Restore"/>). Only the
/// fields relevant to <see cref="Kind"/> are set; the rest keep their defaults.
/// </summary>
public sealed record ExePatchRequest(
    ExePatchKind Kind,
    int Stage = 0,
    int Room = 0,
    int Category = 0,
    uint HandlerVa = 0,
    string? DonorRoomFile = null,
    int DonorStage = 0,
    int DonorRoom = 0)
{
    /// <summary>A cat-slot repoint of <paramref name="stage"/>'s installed AI record (e.g. cat8 = Theri).</summary>
    public static ExePatchRequest CatSlot(int stage, int category, uint handlerVa)
        => new(ExePatchKind.CatSlot, Stage: stage, Category: category, HandlerVa: handlerVa);

    /// <summary>The cat-8 hit-reaction fix, sourcing the descriptor stream from a normal Theri room .dat
    /// (e.g. st603) found in the mod/data dir at install time — identified by <paramref name="donorRoomFile"/>
    /// and its <paramref name="donorStage"/>/<paramref name="donorRoom"/> (to read its RDT).</summary>
    public static ExePatchRequest Cat8HitReaction(int donorStage, int donorRoom, string donorRoomFile)
        => new(ExePatchKind.Cat8HitReaction, DonorStage: donorStage, DonorRoom: donorRoom, DonorRoomFile: donorRoomFile);

    /// <summary>Retarget room (<paramref name="stage"/>,<paramref name="room"/>)'s enemy SE to the donor room's set.</summary>
    public static ExePatchRequest RoomEnemySe(int stage, int room, int donorStage, int donorRoom)
        => new(ExePatchKind.RoomEnemySe, Stage: stage, Room: room, DonorStage: donorStage, DonorRoom: donorRoom);
}

/// <summary>
/// The serialized set of EXE patches a run requested, written as <c>exe-patch-plan.json</c> into the output
/// (mod) dir next to the room <c>.dat</c>s so the rooms and their required EXE edits travel together. Absent
/// when a run patched nothing (so a room-only install stays exactly as before).
/// </summary>
public sealed record ExePatchPlan(int Version, IReadOnlyList<ExePatchRequest> Requests)
{
    /// <summary>Current schema version; bump on any breaking field change.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The sidecar file name written into the mod dir.</summary>
    public const string FileName = "exe-patch-plan.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static ExePatchPlan FromJson(string json)
        => JsonSerializer.Deserialize<ExePatchPlan>(json, JsonOpts)
           ?? throw new InvalidOperationException("exe-patch-plan.json deserialized to null.");

    /// <summary>Write the plan to <c>&lt;modDir&gt;\exe-patch-plan.json</c>.</summary>
    public void Write(string modDir) => File.WriteAllText(Path.Combine(modDir, FileName), ToJson());

    /// <summary>Read the plan from <paramref name="dir"/>, or <c>null</c> when no sidecar is present.</summary>
    public static ExePatchPlan? TryRead(string dir)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return null;
        return FromJson(File.ReadAllText(path));
    }
}
