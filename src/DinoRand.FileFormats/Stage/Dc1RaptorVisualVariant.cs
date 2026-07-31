using System.Security.Cryptography;

namespace DinoRand.FileFormats.Stage;

/// <summary>Corpus evidence for one manually registered DC1 Raptor visual palette.</summary>
public sealed record Dc1RaptorVisualVariantEvidence(
    string VariantId,
    string PaletteFamily,
    DinoSpecies Species,
    int Category,
    int BoneCount,
    int DonorStage,
    int DonorRoom,
    ushort ClutCode,
    string Provenance);

/// <summary>Manual/evidence registry for DC1 Raptor palette variants.</summary>
public sealed class Dc1RaptorVisualVariantRegistry
{
    public const string RaptorHeavyPaletteFamily = "RaptorHeavy";

    // No stable visual census is available in this checkout, so automatic selection stays off.
    public static bool AutomaticSelectionEnabled => false;

    private readonly Dictionary<string, Dc1RaptorVisualVariantEvidence> _variants =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Dc1RaptorVisualVariantEvidence> Variants => _variants.Values;

    public void Register(Dc1RaptorVisualVariantEvidence evidence)
    {
        Validate(evidence);
        if (!_variants.TryAdd(evidence.VariantId, evidence))
            throw new ArgumentException($"visual variant '{evidence.VariantId}' is already registered",
                nameof(evidence));
    }

    public Dc1RaptorVisualVariantEvidence Get(string variantId)
        => _variants.TryGetValue(variantId, out var evidence)
            ? evidence
            : throw new KeyNotFoundException($"visual variant '{variantId}' is not registered");

    public static void Validate(Dc1RaptorVisualVariantEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(evidence.VariantId)
            || string.IsNullOrWhiteSpace(evidence.PaletteFamily)
            || string.IsNullOrWhiteSpace(evidence.Provenance))
            throw new ArgumentException("visual variant identity and provenance are required", nameof(evidence));
        if (evidence.DonorStage is < 1 or > 12 || evidence.DonorRoom is < 0 or > 0xff)
            throw new ArgumentException("visual variant donor room is outside the DC1 room domain", nameof(evidence));
        if (evidence.Species != DinoSpecies.RaptorHeavy
            || evidence.PaletteFamily != RaptorHeavyPaletteFamily
            || evidence.Category != 2
            || evidence.BoneCount != 21)
            throw new ArgumentException("DC1 Raptor visual variants are restricted to RaptorHeavy evidence",
                nameof(evidence));
    }
}

/// <summary>Fields proven safe for authored DC1 0x20 and inline 0x59 enemy records.</summary>
public readonly record struct Dc1RaptorVisualVariantAuthoring(
    ushort? MaxHp = null,
    byte? AiParam = null,
    byte? BirthMode = null)
{
    public bool IsEmpty => MaxHp is null && AiParam is null && BirthMode is null;
}

public sealed record Dc1RaptorVisualVariantProfile(
    Dc1RaptorVisualVariantEvidence Evidence,
    Dc1RaptorVisualVariantAuthoring Authoring)
{
    public int DonorStage => Evidence.DonorStage;
    public int DonorRoom => Evidence.DonorRoom;
    public ushort ClutCode => Evidence.ClutCode;
}

/// <summary>Approved manually selected DC1 Raptor profiles.</summary>
public static class Dc1RaptorVisualVariantProfiles
{
    public const int BlueDonorStage = 5;
    public const int BlueDonorRoom = 0x08;
    public const int BlueDonorLength = 0x8C000;
    public const string BlueDonorSha256 =
        "F6775BEBB1509870C57DB4D8729A98AD63599CBB1EEB1C8EA8679116384C8773";

    public static Dc1RaptorVisualVariantProfile Blue { get; } = new(
        new Dc1RaptorVisualVariantEvidence(
            "blue", Dc1RaptorVisualVariantRegistry.RaptorHeavyPaletteFamily,
            DinoSpecies.RaptorHeavy, 2, 21, BlueDonorStage, BlueDonorRoom,
            TextureImporter.MakeClut(768, 511),
            "ST508 Blue profile; category 2 / 21-bone RaptorHeavy"),
        new Dc1RaptorVisualVariantAuthoring(AiParam: 6));
}

public enum Dc1RaptorVisualVariantOutcome
{
    Applied,
    Unsupported,
}

public readonly record struct Dc1RaptorVisualVariantAuthoringResult(
    Dc1RaptorVisualVariantOutcome Outcome,
    string Note);

public sealed record Dc1RaptorVisualVariantPaletteResult(
    Dc1RaptorVisualVariantOutcome Outcome,
    byte[]? PatchedBytes,
    string Note);

/// <summary>Applies only the author fields proven by the current DC1 record model.</summary>
public static class Dc1RaptorVisualVariantEditor
{
    public static Dc1RaptorVisualVariantPaletteResult TryApplyPalette(
        ReadOnlySpan<byte> target, RoomFile donor,
        EnemyRecord enemy, Dc1RaptorVisualVariantEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(enemy);
        ArgumentNullException.ThrowIfNull(donor);
        try { Dc1RaptorVisualVariantRegistry.Validate(evidence); }
        catch (ArgumentException ex)
        {
            return new(Dc1RaptorVisualVariantOutcome.Unsupported, null, ex.Message);
        }

        if (enemy.Species != DinoSpecies.RaptorHeavy || enemy.Category != 2)
            return new(Dc1RaptorVisualVariantOutcome.Unsupported, null,
                "target is not a RaptorHeavy record");
        if (donor.Stage != evidence.DonorStage || donor.Room != evidence.DonorRoom)
            return new(Dc1RaptorVisualVariantOutcome.Unsupported, null,
                "palette donor is not the evidence-pinned room");
        if (evidence.DonorStage != Dc1RaptorVisualVariantProfiles.BlueDonorStage
            || evidence.DonorRoom != Dc1RaptorVisualVariantProfiles.BlueDonorRoom
            || donor.OriginalBytes.Length != Dc1RaptorVisualVariantProfiles.BlueDonorLength
            || !Convert.ToHexString(SHA256.HashData(donor.OriginalBytes)).Equals(
                Dc1RaptorVisualVariantProfiles.BlueDonorSha256, StringComparison.OrdinalIgnoreCase))
            return new(Dc1RaptorVisualVariantOutcome.Unsupported, null,
                "palette donor bytes are not the pinned ST508 GOG donor");

        // CopySpeciesPalette returns a fresh package and does not mutate either input.
        var patched = TextureImporter.CopySpeciesPalette(target, donor.OriginalBytes, evidence.ClutCode);
        return new(Dc1RaptorVisualVariantOutcome.Applied, patched,
            $"{evidence.VariantId} palette from ST{evidence.DonorStage:X}{evidence.DonorRoom:X2}");
    }

    public static Dc1RaptorVisualVariantAuthoringResult TryAuthor(
        EnemyRecord enemy, Dc1RaptorVisualVariantAuthoring authoring)
    {
        ArgumentNullException.ThrowIfNull(enemy);
        if (enemy.Species != DinoSpecies.RaptorHeavy || enemy.Category != 2)
            return new(Dc1RaptorVisualVariantOutcome.Unsupported,
                "target is not a RaptorHeavy record");
        if (authoring.IsEmpty)
            return new(Dc1RaptorVisualVariantOutcome.Applied, "no authored fields");
        if (enemy.Opcode == DcOpcodes.Enemy2 && (authoring.MaxHp is not null || authoring.BirthMode is not null))
            return new(Dc1RaptorVisualVariantOutcome.Unsupported,
                "0x59 records only have a proven safe inline AI authoring field");
        if (enemy.Opcode is not DcOpcodes.Enemy and not DcOpcodes.Enemy2)
            return new(Dc1RaptorVisualVariantOutcome.Unsupported, "unsupported enemy opcode");

        if (authoring.MaxHp is ushort hp) enemy.MaxHp = hp;
        if (authoring.AiParam is byte ai) enemy.AiParam = ai;
        if (authoring.BirthMode is byte birth) enemy.BirthMode = birth;
        return new(Dc1RaptorVisualVariantOutcome.Applied,
            enemy.Opcode == DcOpcodes.Enemy2 ? "authored proven 0x59 AI field" : "authored proven 0x20 fields");
    }

    public static Dc1RaptorVisualVariantPaletteResult TryApplyProfile(
        RoomFile target, RoomFile donor,
        EnemyRecord enemy, Dc1RaptorVisualVariantProfile profile)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(donor);
        ArgumentNullException.ThrowIfNull(profile);
        var donorCheck = TryApplyPalette(target.OriginalBytes, donor, enemy, profile.Evidence);
        if (donorCheck.Outcome != Dc1RaptorVisualVariantOutcome.Applied)
            return donorCheck;
        var authoring = TryAuthor(enemy, profile.Authoring);
        if (authoring.Outcome != Dc1RaptorVisualVariantOutcome.Applied)
            return new(authoring.Outcome, null, authoring.Note);
        var palette = TryApplyPalette(target.Write(), donor, enemy, profile.Evidence);
        if (palette.Outcome != Dc1RaptorVisualVariantOutcome.Applied)
            return palette;
        return palette with { Note = $"{profile.Evidence.VariantId}: palette + {authoring.Note}" };
    }
}
