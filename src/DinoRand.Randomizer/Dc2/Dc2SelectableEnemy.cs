namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// A UI-facing DC2 enemy choice: a creature mapped to one or more <c>E*.DAT</c> model
/// <see cref="Slots"/>. This is the data shape the enemy randomizer assigns to a room.
///
/// <para>Adapted from BioRand's <c>SelectableEnemy</c> (MIT, © Ted John;
/// <c>ref/classic/IntelOrca.Biohazard.BioRand/SelectableEnemy.cs</c>) — reimplemented, not copied,
/// to fit DinoRand's per-game convention. BioRand's <c>byte[] Types</c> becomes <see cref="Slots"/>
/// (DC2 model ids); a <see cref="Confidence"/> tag is added because most DC2 creature↔file
/// assignments are still <c>[SUSPECTED]</c> (docs/reference/dc2/enemies/EXE-ENEMY-TABLE.md, OPEN&#160;#6).</para>
/// </summary>
public sealed record Dc2SelectableEnemy(
    string Name,
    string Colour,
    IReadOnlyList<int> Slots,
    Confidence Confidence = Confidence.Suspected);

/// <summary>How firmly a DC2 fact is established (mirrors the docs' KNOWN/SUSPECTED/OPEN flags).</summary>
public enum Confidence
{
    /// <summary>Byte-cited / locked grouping.</summary>
    Known,
    /// <summary>Inferred from analogy or partial evidence; unproven.</summary>
    Suspected,
    /// <summary>Unanswered.</summary>
    Open,
}
