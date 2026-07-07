using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// Per-game DC2 enemy abstraction — the <b>highest-value BioRand port</b>
/// (docs/parity/BIORAND-REUSE-VALIDATION.md Q2, "recommended first port"). DC2's enemy is a swappable
/// <i>type id</i> resolving to an <b>external</b> <c>E*.DAT</c> model, so it fits BioRand's
/// <c>IEnemyHelper</c> pattern far better than DC1's resource-bound model — which is why this lives
/// only on the DC2 side and has no DC1 caller (keeps DC1 isolated, Q3).
///
/// <para>Adapted from BioRand's <c>IEnemyHelper</c> (MIT, © Ted John;
/// <c>ref/classic/IntelOrca.Biohazard.BioRand/IEnemyHelper.cs:5</c>) — a trimmed, reimplemented
/// subset (BioRand's interface is RE-format specific: <c>SceEmSetOpcode</c>, ESP sprite ids, etc.).</para>
/// </summary>
public interface IDc2EnemyHelper
{
    /// <summary>Human-readable name for an <c>E*.DAT</c> model slot (or a fallback for unknown ids).</summary>
    string GetEnemyName(int slot);

    /// <summary>The full DC2 creature catalog (13 <c>E*.DAT</c> models) the UI/pass can choose from.</summary>
    IReadOnlyList<Dc2SelectableEnemy> GetSelectableEnemies();

    /// <summary>Whether <paramref name="slot"/> is a valid enemy for <paramref name="room"/>.
    /// <c>[OPEN]</c> — the per-room enemy set is EXE/runtime-side, not in the room file
    /// (docs/reference/dc2/spawn/EXE-SPAWN-SYSTEM.md open #1); stub returns false.</summary>
    bool SupportsEnemySlot(Dc2RoomFile room, int slot);

    /// <summary>Max instances of <paramref name="slot"/> allowed at a given difficulty (0–1).
    /// Analogue of BioRand <c>GetEnemyTypeLimit</c>; difficulty/density scaling is TODO.</summary>
    int GetSlotLimit(double difficulty, int slot);

    /// <summary>Other slots that must also be present for <paramref name="slot"/> to work (BioRand
    /// <c>GetEnemyDependencies</c> analogue). Unknown for DC2; stub returns empty.</summary>
    IReadOnlyList<int> GetSlotDependencies(int slot);
}
