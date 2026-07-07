using DinoRand.FileFormats.Stage.Dc2;

namespace DinoRand.Randomizer.Dc2;

/// <summary>
/// Stub <see cref="IDc2EnemyHelper"/> seeded with the 13 external enemy model files
/// (<c>E00,E10,E20,E30,E31,E32,E40,E50,E60,E70,E80,E90,EA0</c> — docs/reference/dc2/_registries/KNOWLEDGE-AND-QUESTIONS.md
/// K11). Slot id = the <c>E*.DAT</c> number (high nibble = enemy type — docs/reference/dc2/enemies/EXE-ENEMY-TABLE.md).
///
/// <para><b>Confidence:</b> only two groupings are locked — <c>EA0</c>=Compsognathus and
/// <c>E30/E31/E32</c>=Velociraptor colour variants; the other 9 creature names are <c>[SUSPECTED]</c>
/// (the within-9 mapping is OPEN&#160;#6, though each species' model RAM base is decoded — K27). The
/// room-allow / limit / dependency methods are no-ops until the EXE actor-creation path is mapped
/// (docs/reference/dc2/spawn/EXE-SPAWN-SYSTEM.md open #1) — so this helper currently <i>describes the catalog</i> but
/// performs no swaps.</para>
/// </summary>
public sealed class Dc2EnemyHelper : IDc2EnemyHelper
{
    // Creature↔E-file map CORRECTED to the LIVE captures (K43 E00=Velociraptor, K44 E90=Oviraptor /
    // E60=Allosaurus / E10=Tyrannosaurus / E50=Inostrancevia, K46 E40=Giganotosaurus, + this session's
    // live ST202 Oviraptor swap). The prior table (Ovi=E00, Raptor=E30/31/32, T-Rex=E80…) predated the
    // captures and was wrong — retracted. Slot id = the E*.DAT number; Confidence per
    // docs/reference/dc2/_registries/KNOWLEDGE-AND-QUESTIONS.md (KNOWN = live-captured). Colours are arbitrary UI tags.
    private static readonly IReadOnlyList<Dc2SelectableEnemy> Catalog = new[]
    {
        new Dc2SelectableEnemy("Velociraptor",   "ForestGreen", new[] { 0x00 }, Confidence.Known),     // E00 K43
        new Dc2SelectableEnemy("Tyrannosaurus",  "DarkRed",     new[] { 0x10 }, Confidence.Known),     // E10 K44
        new Dc2SelectableEnemy("Pteranodon",     "SkyBlue",     new[] { 0x20 }, Confidence.Known),     // E20 K62c (live cave ST202; FLYER)
        new Dc2SelectableEnemy("Mosasaurus",     "Navy",        new[] { 0x30 }, Confidence.Known),     // E30; AQUATIC — crashes as a land spawn (live ST202 2026-06-30)
        new Dc2SelectableEnemy("E31 (unresolved)", "Gray",      new[] { 0x31 }, Confidence.Open),
        new Dc2SelectableEnemy("E32 (unresolved)", "Gray",      new[] { 0x32 }, Confidence.Open),
        new Dc2SelectableEnemy("Giganotosaurus", "Maroon",      new[] { 0x40 }, Confidence.Known),     // E40 K46
        new Dc2SelectableEnemy("Inostrancevia",  "SlateGray",   new[] { 0x50 }, Confidence.Known),     // E50 K44/K45
        new Dc2SelectableEnemy("Allosaurus",     "Firebrick",   new[] { 0x60 }, Confidence.Known),     // E60 K44
        new Dc2SelectableEnemy("Triceratops",    "SaddleBrown", new[] { 0x70 }, Confidence.Known),     // E70 K62 (live cave ST202)
        new Dc2SelectableEnemy("E80 (unresolved)", "Gray",      new[] { 0x80 }, Confidence.Open),
        new Dc2SelectableEnemy("Oviraptor",      "Khaki",       new[] { 0x90 }, Confidence.Known),     // E90 K44 + live ST202
        new Dc2SelectableEnemy("Compsognathus",  "YellowGreen", new[] { 0xA0 }, Confidence.Suspected), // EA0 inference
    };

    public IReadOnlyList<Dc2SelectableEnemy> GetSelectableEnemies() => Catalog;

    public string GetEnemyName(int slot) =>
        Catalog.FirstOrDefault(e => e.Slots.Contains(slot))?.Name ?? $"E{slot:X2} (unknown)";

    // TODO(dc2): real per-room allow-list. DC2 has no in-room spawn record, so the per-room enemy SET
    // must come from the EXE / runtime capture (docs/reference/dc2/spawn/EXE-SPAWN-SYSTEM.md open #1,
    // RUNTIME-CAPTURE-PLAN.md), NOT from `room`. Returning false keeps every swap a no-op for now.
    public bool SupportsEnemySlot(Dc2RoomFile room, int slot) => false;

    // TODO(dc2): difficulty/density scaling (BioRand GetEnemyTypeLimit analogue).
    public int GetSlotLimit(double difficulty, int slot) => 0;

    // TODO(dc2): DC2 has no known cross-model dependency (DC1 G-Adult/G-Embryo style); empty for now.
    public IReadOnlyList<int> GetSlotDependencies(int slot) => Array.Empty<int>();
}
