using DinoRand.FileFormats.Stage;

namespace DinoRand.Randomizer.Definitions;

/// <summary>
/// One Dino Crisis emergency (storage) box: its id, the room it sits in and how many Plugs
/// (<see cref="GameDefinition.PlugItemId"/>) the player must spend to open it. The id is the box record's
/// byte <c>+0x1C</c> (<see cref="EmergencyBoxRecord"/>): the index of its contents record in the exe and the
/// source of its colour and unlock flag. Boxes are optional storage, not progression gates, so they never
/// affect beatability — but the randomizer tracks the plug economy (<see cref="Logic.PlugEconomy"/>).
/// Full per-difficulty contents live in <c>data/dc1/emergency-boxes.json</c>.
/// docs/decisions/cross/ITEM-RANDO-PLAN.md §7.4.
/// </summary>
public sealed record EmergencyBox(int BoxId, int RoomCode, int PlugCost, string Name)
{
    public EmergencyBoxColour Colour => EmergencyBoxRecord.ColourOf(BoxId);
}
