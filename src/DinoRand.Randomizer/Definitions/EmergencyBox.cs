namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// One Dino Crisis emergency (storage) box: the room it sits in and how many Plugs
/// (<see cref="GameDefinition.PlugItemId"/>) the player must spend to open it. Boxes are optional
/// storage, not progression gates, so they never affect beatability — but the randomizer tracks the
/// plug economy so it can surface when a seed would leave reachable boxes unopenable
/// (<see cref="Logic.PlugEconomy"/>). Full per-difficulty contents live in
/// <c>data/dc1/emergency-boxes.json</c> (reference catalog, FAQ-sourced); the engine only needs the
/// room + plug cost. docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4.
/// </summary>
public sealed record EmergencyBox(int RoomCode, int PlugCost, string Name);
