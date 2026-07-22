namespace DinoRand.ApClient;

/// <summary>
/// Static <c>DINO.exe</c> virtual addresses the AP runtime client polls/writes. The GOG build has
/// imagebase 0x400000, no ASLR, VA==RVA — addresses are identical every launch (EXE-SYMBOLS
/// header; AP-CLIENT-INVESTIGATION §2). Every entry is byte-cited in
/// <c>docs/reference/dc1/_registries/EXE-SYMBOLS.md</c>; the taken-flag rows are cont.81.
/// </summary>
public static class Dc1Symbols
{
    /// <summary>Process name to attach to (no extension), per D2.</summary>
    public const string ProcessName = "DINO";

    /// <summary>Game-state global; 3 = normal gameplay (EXE-SYMBOLS :291). The poll engine is
    /// hard-gated on this — no reads/writes during menus/transitions/cutscenes.</summary>
    public const uint GameStateVa = 0x6D3E68;
    public const uint GameStateGameplay = 3;

    /// <summary>Current-room word — the literal 16-bit room id, e.g. 0x0103 (REGION-INDEX-MAP §3,
    /// CE-witnessed; writer 0x449B4C).</summary>
    public const uint CurrentRoomVa = 0x6DD8F0;

    /// <summary>Flag group 7 bank — per-pickup TAKEN flags, indexed by the record's take index
    /// (word[rec+0x20]); 256 bits (0x643008 table, exe-read). Cont.81.</summary>
    public const uint Group7BankVa = 0x6DD950;
    public const int Group7BankBytes = 32;

    /// <summary>Flag group 11 bank — item OWNERSHIP bits, indexed by item id ("owns item N",
    /// EXE-SYMBOLS :297/:514); 256 bits. Key/weapon/file grants assert bits here.</summary>
    public const uint Group11BankVa = 0x6DD9D8;
    public const int Group11BankBytes = 32;

    /// <summary>Supply/inventory array — ten 4-byte slots [id, qty, class, 0]
    /// (= scratchpad0 0x6D3E40 + 0x9EBC; EXE-SYMBOLS :298).</summary>
    public const uint SupplyArrayVa = 0x6DDCFC;
    public const int SupplySlotCount = 10;

    /// <summary>Supply capacity byte (scratchpad0 + 0x9AEE = 0x6DD92E; new-game init writes 0x0A).</summary>
    public const uint SupplyCapacityVa = 0x6DD92E;

    /// <summary>AddItem range gate (pickup handler 0x440739, cont.81): only ids in
    /// [0x10, 0x24) enter the supply array; everything else grants via flag(11, id) only.</summary>
    public const int ConsumableIdMin = 0x10;
    public const int ConsumableIdEnd = 0x24;

    /// <summary>The item id installed at locations holding another world's item (slot_data
    /// placement value <c>OTHER_WORLD_MARKER</c>): 0x78 = the first "Ancient Type Regina" costume
    /// part — defined name (pickup dialog safe), no AddItem (outside the consumable band), no
    /// door/script logic reads its flag(11) bit, not in the AP item pool, and one bit can never
    /// complete the 14-part costume. Picking it up fires the taken flag (the check) and grants
    /// nothing that matters.</summary>
    public const byte OtherWorldMarkerItemId = 0x78;

    /// <summary>Item property table (12-byte records; referenced by AddItem 0x445048 and
    /// new-game init 0x441033). Byte +1 of a record = the id's max per-slot stack.</summary>
    public const uint ItemPropertyTableVa = 0x64EFC0;
    public const int ItemPropertyStride = 12;

    /// <summary>Per-id descriptor class byte AddItem stores at slot+2 — exact mirror of the
    /// DINO.exe helper 0x483270 (jump table 0x4832DB, cont.81): ids 0x12..0x1F mapped, all
    /// other ids → 1.</summary>
    public static byte SupplyClassOf(int itemId) => itemId switch
    {
        0x12 => 1, 0x13 => 4, 0x14 => 7, 0x15 => 29,
        0x16 => 1, 0x17 => 1, 0x18 => 1, 0x19 => 1, 0x1A => 1, 0x1B => 1,
        0x1C => 4, 0x1D => 7, 0x1E => 10, 0x1F => 29,
        _ => 1,
    };
}
