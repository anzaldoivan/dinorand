namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// The registry of games the frontend can target — the data-first home for the DC1/DC2 selector
/// (docs/decisions/cross/GAME-SELECTOR-PLAN.md §4). The Avalonia ComboBox binds to <see cref="All"/>; routing and
/// settings-restore resolve a stored <see cref="GameDefinition.Id"/> via <see cref="FromId"/>. Mirrors
/// BioRand's per-game module list, but keyed by string id (DinoRand's <see cref="GameDefinition.Id"/>
/// idiom) rather than a magic int. Adding a game later is one entry here, not a new code path.
/// </summary>
public static class GameCatalog
{
    /// <summary>All selectable games, in display order. Incomplete games (e.g. the DC2 stub) appear here
    /// too — <see cref="GameDefinition.IsImplemented"/> gates them out of the live pipeline.</summary>
    public static IReadOnlyList<GameDefinition> All { get; } = new GameDefinition[]
    {
        new DinoCrisis1(),
        new DinoCrisis2(),
    };

    /// <summary>The default selection: the first game whose pipeline is ready to run. Keyed off
    /// <see cref="GameDefinition.IsImplemented"/> (not merely <c>All[0]</c>) so prepending an unfinished
    /// game can never make the UI default to a stub.</summary>
    public static GameDefinition Default { get; } = All.First(g => g.IsImplemented);

    /// <summary>Resolve a stored game id to its definition, or <c>null</c> if unknown (e.g. a settings
    /// file from a build that listed a game since removed — the caller falls back to <see cref="Default"/>).</summary>
    public static GameDefinition? FromId(string? id) =>
        All.FirstOrDefault(g => string.Equals(g.Id, id, System.StringComparison.OrdinalIgnoreCase));
}
