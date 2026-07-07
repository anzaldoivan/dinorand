namespace DinoRand.FileFormats.Stage;

/// <summary>
/// A Dino Crisis enemy species class, recovered from the enemy's loaded <b>skeleton</b>
/// (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.14). The enemy <c>0x20</c> record's model pointer
/// (<see cref="EnemyRecord.ModelOffset"/>) targets a model block embedded in the same
/// decompressed RDT buffer; that block's <b>bone count</b> (dword at
/// <see cref="EnemySkeleton.BoneCountOffset"/>) plus its bone parent-hierarchy is a stable
/// per-species invariant.
///
/// <para>The skeleton topology is <b>1:1 with the AI category</b>
/// (<see cref="EnemyRecord.Category"/>) — every bone count maps to exactly one category and vice
/// versa (cat1↔15, cat2↔21, cat3↔20, cat4↔10, cat5↔7, cat7↔18, cat8↔22) — so the category byte and
/// the model skeleton are two independent witnesses of the same model class.</para>
///
/// <para><b>Species ≠ AI category (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.23).</b> The seven model classes are
/// <i>not</i> seven biological species: the DC1 enemy roster has a single large theropod, the
/// <b>Tyrannosaurus</b>, but the engine ships it as <b>two model classes</b> — the 20-bone cat-3 boss
/// (placed as a scripted set-piece in every "T-Rex room": 0201/0403/0600/060A/060C/0611/0613) and a
/// distinct 10-bone cat-4 rig used only in Chief's Room (0202). Both decode to
/// <see cref="Tyrannosaurus"/>. (Established by roster count — no sixth large-dino name exists in the
/// wiki — and a full-corpus census in which every 20-bone room is wiki-labelled "Tyrannosaurus".) So
/// <see cref="FromBoneCount"/> maps the model class to a <b>species</b>, while
/// <see cref="EnemySkeleton.SpeciesForCategory"/> maps the AI category to the same species; the two
/// agreeing is the self-check (<see cref="EnemyRecord.SpeciesMatchesCategory"/>).</para>
/// </summary>
public enum DinoSpecies
{
    /// <summary>Model skeleton did not decode to a known species class.</summary>
    Unknown = 0,

    /// <summary>15-bone skeleton, AI category 1 — the standard Velociraptor (the workhorse enemy,
    /// 33 rooms). FAQ "Raptor".</summary>
    Velociraptor = 1,

    /// <summary>21-bone skeleton, AI category 2 — a distinct raptor-class rig (rooms
    /// 0109/010D/0308/030A/0401; the FAQ still calls these "Raptor"). Heaviest skeleton. The stage-5
    /// <b>"Blue Raptor"</b> is this species, live-confirmed in 0511 (entity+0x27=2 and model+0x14=21
    /// bones; it dies normally and sets its group-4 kill-flag, so "Blue" is palette only and just
    /// handgun-tanky — not immune or distinct; docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.26).</summary>
    RaptorHeavy = 2,

    /// <summary>The Tyrannosaurus — DC1's single large theropod, shipped as <b>two</b> model classes
    /// (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.23): the 20-bone AI-category-3 boss placed as a scripted set-piece in
    /// every "T-Rex room" (0201/0403/0600/060A/060C/0611/0613), and a distinct 10-bone AI-category-4 rig
    /// used only in Chief's Room (0202). <see cref="EnemySkeleton.FromBoneCount"/> maps both 20 and 10
    /// here; <see cref="EnemySkeleton.SpeciesForCategory"/> maps both category 3 and 4 here. The
    /// enum value (3 is intentionally absent) no longer equals the AI category — species and category
    /// are decoupled. Never randomized (set-piece; <see cref="EnemyRecord.IsRandomizableDino"/>).</summary>
    Tyrannosaurus = 4,

    /// <summary>7-bone skeleton, AI category 5 — the small swarming dinosaur (identical instances
    /// sharing one pointer; rooms 0307/040A/040B). Simplest skeleton.</summary>
    Swarm = 5,

    /// <summary>18-bone skeleton, AI category 7 — the Pteranodon flyer (rooms 0400/0405/0407).</summary>
    Pteranodon = 7,

    /// <summary>22-bone skeleton, AI category 8 — Therizinosaurus. Placed only via the second enemy
    /// opcode <c>0x59</c> (not <c>0x20</c>), in stage-6 rooms 0603/0604/0605/0606/0608. Recovered from
    /// the live native spawn + file census (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.20–22). Heaviest skeleton.</summary>
    Therizinosaurus = 8,
}

/// <summary>
/// Decoder for the enemy model <b>skeleton</b> embedded in a decompressed RDT buffer, and the
/// skeleton→species map. See <see cref="DinoSpecies"/> and docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.14.
///
/// <para>The model block (target of <see cref="EnemyRecord.ModelOffset"/>, after subtracting the
/// RDT PSX base) is laid out by the engine's model-setup routine <c>0x416b79</c>:</para>
/// <list type="bullet">
/// <item>+0x00..+0x0f — four PSX pointers (relocated at load);</item>
/// <item>+0x14 — <b>bone count</b> (dword);</item>
/// <item>+0x18 — bone array, stride <see cref="BoneStride"/>=0x14: int16 x/y/z at +0/+2/+4,
/// signed parent index at +6 (&lt;0 = root), flag byte at +7, then per-bone mesh pointers.</item>
/// </list>
/// The bone count and the parent column are pose-independent; only the bone positions vary between
/// model instances of one species (rest-pose proportions), so species is keyed off the bone count.
/// </summary>
public static class EnemySkeleton
{
    /// <summary>PSX base the RDT (and the embedded model blocks) relocate against; model
    /// pointers are offsets into the same decompressed buffer once this is subtracted.</summary>
    public const uint PsxRdtBase = 0x80100000;

    /// <summary>Offset of the bone-count dword within a model block.</summary>
    public const int BoneCountOffset = 0x14;

    /// <summary>Offset of the bone array within a model block.</summary>
    public const int BoneArrayOffset = 0x18;

    /// <summary>Per-bone stride within the bone array.</summary>
    public const int BoneStride = 0x14;

    /// <summary>Sanity ceiling for a decoded bone count (the real classes are 7..21).</summary>
    private const int MaxBones = 64;

    /// <summary>
    /// Read the skeleton bone count for the model at file-form <paramref name="modelPtr"/>
    /// (<c>0x8010xxxx</c>) from the decompressed RDT <paramref name="buffer"/>. Returns 0 when the
    /// pointer does not resolve to an in-buffer model block with a sane bone count.
    /// </summary>
    public static int ReadBoneCount(ReadOnlySpan<byte> buffer, uint modelPtr)
    {
        if (modelPtr < PsxRdtBase) return 0;
        long off = modelPtr - PsxRdtBase;
        if (off < 0 || off + BoneCountOffset + 4 > buffer.Length) return 0;
        uint count = (uint)(buffer[(int)off + BoneCountOffset]
                            | (buffer[(int)off + BoneCountOffset + 1] << 8)
                            | (buffer[(int)off + BoneCountOffset + 2] << 16)
                            | (buffer[(int)off + BoneCountOffset + 3] << 24));
        if (count == 0 || count > MaxBones) return 0;
        // The bone array must also fit inside the buffer for this to be a real model block.
        long arrayEnd = off + BoneArrayOffset + (long)count * BoneStride;
        if (arrayEnd > buffer.Length) return 0;
        return (int)count;
    }

    /// <summary>Map a decoded model skeleton bone count to its <see cref="DinoSpecies"/>. Both the
    /// 20-bone boss rig and the 10-bone Chief's-Room rig decode to <see cref="DinoSpecies.Tyrannosaurus"/>
    /// (docs/reference/dc1/_registries/STATIC-SCD-RE.md cont.23) — species is the biological animal, not the model class.</summary>
    public static DinoSpecies FromBoneCount(int boneCount) => boneCount switch
    {
        15 => DinoSpecies.Velociraptor,
        21 => DinoSpecies.RaptorHeavy,
        20 => DinoSpecies.Tyrannosaurus, // 20-bone cat-3 T-Rex boss (cont.23)
        10 => DinoSpecies.Tyrannosaurus, // 10-bone cat-4 T-Rex variant, Chief's Room only
        7 => DinoSpecies.Swarm,
        18 => DinoSpecies.Pteranodon,
        22 => DinoSpecies.Therizinosaurus,
        _ => DinoSpecies.Unknown,
    };

    /// <summary>
    /// Map an enemy's AI <see cref="EnemyRecord.Category"/> byte to the <see cref="DinoSpecies"/> it
    /// implies. The category↔bone-count correspondence is 1:1 across the corpus, so this is the
    /// category-side witness of the species; it agrees with <see cref="FromBoneCount"/> for every real
    /// placement (<see cref="EnemyRecord.SpeciesMatchesCategory"/>). Categories 3 and 4 both map to
    /// <see cref="DinoSpecies.Tyrannosaurus"/> — the boss and Chief's-Room rigs (docs/reference/dc1/_registries/STATIC-SCD-RE.md
    /// cont.23). Unknown for a category with no placed enemy in the corpus.
    /// </summary>
    public static DinoSpecies SpeciesForCategory(int category) => category switch
    {
        1 => DinoSpecies.Velociraptor,
        2 => DinoSpecies.RaptorHeavy,
        3 => DinoSpecies.Tyrannosaurus, // 20-bone boss
        4 => DinoSpecies.Tyrannosaurus, // 10-bone Chief's-Room rig
        5 => DinoSpecies.Swarm,
        7 => DinoSpecies.Pteranodon,
        8 => DinoSpecies.Therizinosaurus,
        _ => DinoSpecies.Unknown,
    };

    /// <summary>Decode the species of an enemy from its model pointer and the RDT buffer.</summary>
    public static DinoSpecies Decode(ReadOnlySpan<byte> buffer, uint modelPtr)
        => FromBoneCount(ReadBoneCount(buffer, modelPtr));
}
