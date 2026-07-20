using DinoRand.Randomizer.Dc2;

namespace DinoRand.Randomizer.Spoiler;

/// <summary>
/// The single owner of the shareable <c>DINO-{base64url}</c> run-identity wire format
/// (docs/decisions/cross/SPOILER-LOG-PLAN.md §3.2 D): seed + the seed-encoded <see cref="RandomizerConfig"/>
/// subset. Relocated verbatim from the Avalonia <c>AppSeed</c> (which now delegates here) so the
/// CLI and the runners — which cannot reference a UI project — can print/parse the same string.
/// The byte layout is UNCHANGED and locked by <c>AppSeedTests</c> + <c>SeedStringTests</c>.
/// </summary>
/// <remarks>
/// Encoding is 10, 11, 16, 17, 22 or 23 bytes (legacy 6-, 8-, and 9-byte payloads still parse;
/// byte 22 = the DC2 starting-weapon byte, present only when the option is on: bit7 = enabled,
/// bits 0–2 = Dylan selection (0 = random, n = DylanWeaponIds[n−1]), bits 3–5 = Regina selection;
/// bytes 17–21 = the DC2 raptor-tier block, present only when non-default: byte 17 = enable bit7 +
/// colour-mode bit5 (0=RoomTier, 1=MixedTiers) + (blue-combo-threshold−1) bits 0–4;
/// bytes 18–21 = variant-weight nibbles V0..V7, low first;
/// byte 16 bits 0-1 = the Regina character skin, bit 2 = DC2 shop shuffle, bit 3 = DC2 elevator
/// puzzle-code scramble, bit 4 = DC2 stungun-circuit shuffle, bit 5 = DC2 Key-Plate terminal re-key;
/// present only when non-default):
///   bytes 0–3  Seed.Value (int, little-endian)
///   byte 4     config flags — bit0=Items, bit1=Enemies, bit2=Doors, bit3=StartingInventory,
///              bit4=ShuffleKeyItems, bit5=ReplaceItemPool, bit6=EnemyHp (DC1),
///              bit7=ShuffleKeyItemsIntoPickups (DC1 key-item scatter)
///   byte 5     EnemyDifficulty scaled to 0–255
///   byte 6     RatioAmmo (0–31)
///   byte 7     RatioHealth in bits 0–4 (0–31), AmmoQuantity in bits 5–7 (0–7)
///   byte 8     WeaponUpgradeChance nibble (round(chance*15)), PreUpgradedWeaponChance nibble
///   byte 9     EnabledWeaponFamilies in bits 0–2
///   byte 10    AmmoReduction in bits 0–2; omitted when 0 unless the DC2 block is present
///   bytes 11–15  DC2 enemy-distribution block (ENEMY-DISTRIBUTION-PLAN.md D6), omitted when
///              all-default: byte 11 = mode nibble (0=Weighted, n=Fixed with species
///              Dc2CanonicalSpecies[n−1]) | setpiece bit4 | boss bit5 | character-skin bits 6–7
///              (Dc2CharacterSkin: 0=Stock, 1=Gail, 2=Rick, 3=Random); bytes 12–15 = eight
///              weight nibbles in Dc2CanonicalSpecies order, LOW nibble first (8th reserved).
/// Back-compat: shorter historical payloads decode with the defaults their era implied, so an
/// old shared seed reproduces its original run. See the original AppSeed remarks for the full
/// per-era table.
/// </remarks>
public static class SeedString
{
    /// <summary>The DC2 species whose weights the seed's nibbles carry, in FROZEN canonical order
    /// (ascending TYPE at freeze time). A wire-format constant: never reorder or remove — a new
    /// weighable species APPENDS here, or old seeds would decode with shifted weights. Locked
    /// against data/dc2/enemy-distribution.json by AppSeedTests.</summary>
    public static readonly IReadOnlyList<int> Dc2CanonicalSpecies =
        new[] { 0x02, 0x03, 0x06, 0x07, 0x08, 0x09, 0x0e };

    private static readonly Lazy<Dc2EnemyDistribution> Dc2Distribution =
        new(Dc2EnemyDistribution.LoadEmbedded);

    private static readonly Lazy<Dc2RaptorTierTable> Dc2RaptorTiers =
        new(Dc2RaptorTierTable.LoadEmbedded);

    /// <summary>Encode a run identity. Byte-identical to the historical <c>AppSeed.ToString()</c>.</summary>
    public static string Encode(Seed seed, RandomizerConfig config)
    {
        // DC2 enemy-distribution block (bytes 11–15): emitted only when something in it is
        // non-default, so every pre-feature config keeps its historical byte-identical seed string.
        int fixedIdx = config.Dc2EnemyMode == Dc2EnemyDistributionMode.Fixed
                       && config.Dc2FixedSpeciesType is int pin
            ? IndexOfSpecies(pin) : -1;
        var effectiveWeights = Dc2Distribution.Value.EffectiveWeights(config.Dc2SpeciesWeights);
        bool weightsDefault = Dc2CanonicalSpecies.All(t =>
            effectiveWeights.GetValueOrDefault(t) == Dc2Distribution.Value.DefaultWeights.GetValueOrDefault(t));
        bool dc2Block = fixedIdx >= 0 || config.IncludeDc2SetpieceEnemies
                        || config.IncludeDc2BossEnemies || !weightsDefault
                        || config.Dc2CharacterSkin != Dc2.Passes.Dc2CharacterSkin.Stock;
        bool reginaByte = config.Dc2ReginaSkin != Dc2.Passes.Dc2CharacterSkin.Stock
                          || config.Dc2ShuffleShop          // byte 16: Regina skin bits 0-1, shop bit 2,
                          || config.Dc2ScramblePuzzleCodes  // puzzle codes bit 3,
                          || config.Dc2ShuffleCircuits      // circuits bit 4,
                          || config.Dc2RekeyPlateDoor      // plate-door re-key bit 5,
                          || config.Dc2CrossCharWeapons    // cross-character weapons bit 6,
                          || config.Dc2RandomizeWeapons;   // randomized weapon ownership bit 7
        if (reginaByte) dc2Block = true; // byte 16 needs the block's fixed positions

        // Raptor tier block (bytes 17–21, RAPTOR-TIER-RE.md §4): byte 17 = enable bit7 +
        // (comboThreshold−1) in bits 0–4; bytes 18–21 = eight variant-weight nibbles (V0..V7, LOW
        // nibble first). Emitted only when non-default; forces the earlier fixed positions.
        var effTierWeights = Dc2RaptorTiers.Value.EffectiveWeights(config.Dc2RaptorTierWeights);
        bool tierWeightsDefault = Dc2RaptorTiers.Value.DefaultWeights.All(kv =>
            effTierWeights.GetValueOrDefault(kv.Key) == kv.Value);
        int comboThreshold = Math.Clamp(config.Dc2BlueRaptorComboThreshold, 1, 20);
        bool raptorBlock = config.Dc2RandomizeRaptorTiers || !tierWeightsDefault || comboThreshold != 20
                           || config.Dc2RaptorColourMode != Dc2RaptorColourMode.RoomTier;
        if (raptorBlock) { dc2Block = true; reginaByte = true; }

        // Starting-weapon byte (byte 22, DC2-STARTING-LOADOUT-PLAN.md): bit7 = enabled, bits 0–2 =
        // Dylan selection (0 = random, n = DylanWeaponIds[n−1]), bits 3–5 = Regina selection (same
        // scheme). Emitted only when the option is on; forces the earlier fixed positions.
        bool startWeaponByte = config.Dc2RandomizeStartWeapon;
        if (startWeaponByte) { dc2Block = true; reginaByte = true; raptorBlock = true; }

        // 11 bytes only when ammo is reduced; otherwise 10, so every non-reducing config keeps its
        // historical byte-identical seed string. The DC2 block needs fixed byte positions, so its
        // presence forces byte 10 (even when 0).
        byte ammoReduction = Math.Min((byte)7, config.AmmoReduction);
        var bytes = new byte[startWeaponByte ? 23 : raptorBlock ? 22 : reginaByte ? 17 : dc2Block ? 16 : ammoReduction > 0 ? 11 : 10];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), seed.Value);
        bytes[4] = (byte)(
            (config.RandomizeItems             ? 1 : 0) |
            (config.RandomizeEnemies           ? 2 : 0) |
            (config.RandomizeDoors             ? 4 : 0) |
            (config.RandomizeStartingInventory ? 8 : 0) |
            (config.ShuffleKeyItems            ? 16 : 0) |
            (config.ReplaceItemPool            ? 32 : 0) |
            (config.RandomizeEnemyHp           ? 64 : 0) |
            (config.ShuffleKeyItemsIntoPickups ? 128 : 0));
        bytes[5] = (byte)Math.Round(Math.Clamp(config.EnemyDifficulty, 0, 1) * 255);
        bytes[6] = Math.Min((byte)31, config.RatioAmmo);
        bytes[7] = (byte)((Math.Min((byte)31, config.RatioHealth)) |
                          ((Math.Min((byte)7, config.AmmoQuantity)) << 5));
        int weaponNibble = (int)Math.Round(Math.Clamp(config.WeaponUpgradeChance, 0, 1) * 15);
        int preUpgNibble = (int)Math.Round(Math.Clamp(config.PreUpgradedWeaponChance, 0, 1) * 15);
        bytes[8] = (byte)((weaponNibble & 0x0f) | ((preUpgNibble & 0x0f) << 4));
        bytes[9] = (byte)((int)config.EnabledWeaponFamilies & 0x07);
        if (ammoReduction > 0 || dc2Block)
            bytes[10] = (byte)(ammoReduction & 0x07);
        if (dc2Block)
        {
            bytes[11] = (byte)(
                (fixedIdx >= 0 ? (fixedIdx + 1) & 0x0f : 0) |
                (config.IncludeDc2SetpieceEnemies ? 0x10 : 0) |
                (config.IncludeDc2BossEnemies     ? 0x20 : 0) |
                (((int)config.Dc2CharacterSkin & 0x03) << 6));   // character skin, bits 6–7
            for (int i = 0; i < Dc2CanonicalSpecies.Count; i++)
            {
                int nibble = Math.Min(effectiveWeights.GetValueOrDefault(Dc2CanonicalSpecies[i]),
                                      Dc2DonorPicker.MaxWeight);
                bytes[12 + i / 2] |= (byte)(i % 2 == 0 ? nibble : nibble << 4);
            }
            if (reginaByte)
                bytes[16] = (byte)(((int)config.Dc2ReginaSkin & 0x03)             // Regina skin, bits 0-1
                                   | (config.Dc2ShuffleShop ? 0x04 : 0)           // shop shuffle, bit 2
                                   | (config.Dc2ScramblePuzzleCodes ? 0x08 : 0)   // puzzle codes, bit 3
                                   | (config.Dc2ShuffleCircuits ? 0x10 : 0)       // circuits, bit 4
                                   | (config.Dc2RekeyPlateDoor ? 0x20 : 0)        // plate-door re-key, bit 5
                                   | (config.Dc2CrossCharWeapons ? 0x40 : 0)      // cross-char weapons, bit 6
                                   | (config.Dc2RandomizeWeapons ? 0x80 : 0));    // randomized weapons, bit 7
            if (raptorBlock)
            {
                bytes[17] = (byte)((config.Dc2RandomizeRaptorTiers ? 0x80 : 0)
                                   | (config.Dc2RaptorColourMode == Dc2RaptorColourMode.MixedTiers ? 0x20 : 0)
                                   | ((comboThreshold - 1) & 0x1f));
                for (int v = 0; v <= Dc2RaptorTierTable.MaxVariant; v++)
                {
                    int nibble = Math.Min(effTierWeights.GetValueOrDefault(v), Dc2DonorPicker.MaxWeight);
                    bytes[18 + v / 2] |= (byte)(v % 2 == 0 ? nibble : nibble << 4);
                }
                if (startWeaponByte)
                    bytes[22] = (byte)(0x80
                        | (SelectionOf(FileFormats.Exe.Dc2StartingLoadoutPatch.DylanWeaponIds, config.Dc2DylanStartWeaponId) & 0x07)
                        | ((SelectionOf(FileFormats.Exe.Dc2StartingLoadoutPatch.ReginaWeaponIds, config.Dc2ReginaStartWeaponId) & 0x07) << 3)
                        | (config.Dc2AddAndEquipStartWeapon ? 0x40 : 0));   // bit 6 = add-and-equip (full band)
            }
        }
        var b64 = Convert.ToBase64String(bytes).TrimEnd('=')
                         .Replace('+', '-').Replace('/', '_');
        return $"DINO-{b64}";
    }

    /// <summary>Parse a <c>DINO-…</c> string back into its seed + seed-encoded config subset.
    /// Behavior identical to the historical <c>AppSeed.TryParse</c> (which delegates here).</summary>
    public static bool TryParse(string s, out Seed seed, out RandomizerConfig config)
    {
        seed = null!;
        config = null!;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (!s.StartsWith("DINO-", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var b64 = s[5..].Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '='));
            if (bytes.Length is not (6 or 8 or 9 or 10 or 11 or 16 or 17 or 22 or 23)) return false;
            var seedVal = BitConverter.ToInt32(bytes, 0);
            var flags = bytes[4];
            var diff = bytes[5] / 255.0;

            // Pie-chart fields live in bytes 6–7; absent in a legacy 6-byte seed, where they default
            // (ReplaceItemPool=true, ratios/quantity=0) so the run is identical to before the bump.
            bool hasPie = bytes.Length >= 8;
            byte ratioAmmo   = hasPie ? bytes[6] : (byte)0;
            byte ratioHealth = hasPie ? (byte)(bytes[7] & 0x1f) : (byte)0;
            byte ammoQty     = hasPie ? (byte)(bytes[7] >> 5) : (byte)0;
            bool replacePool = !hasPie || (flags & 32) != 0;

            bool hasWeapon = bytes.Length >= 9;
            double weaponUpgradeChance     = hasWeapon ? (bytes[8] & 0x0f) / 15.0 : 1.0;
            double preUpgradedWeaponChance = hasWeapon ? (bytes[8] >> 4) / 15.0 : 0.0;

            var enabledFamilies = bytes.Length >= 10
                ? (Definitions.WeaponFamily)(bytes[9] & 0x07)
                : Definitions.WeaponFamily.All;

            byte ammoReduction = bytes.Length >= 11 ? (byte)(bytes[10] & 0x07) : (byte)0;

            // DC2 enemy-distribution block, bytes 11–15 (ENEMY-DISTRIBUTION-PLAN.md D6); absent in a
            // pre-distribution seed (<16 bytes), where everything defaults (Weighted mode, curated
            // weights, both donor toggles off) so the run is byte-identical to before the bump.
            var dc2Mode = Dc2EnemyDistributionMode.Weighted;
            int? dc2FixedSpecies = null;
            bool dc2Setpiece = false, dc2Boss = false;
            var dc2Skin = Dc2.Passes.Dc2CharacterSkin.Stock;
            var dc2ReginaSkin = Dc2.Passes.Dc2CharacterSkin.Stock;
            bool dc2ShuffleShop = false, dc2PuzzleCodes = false, dc2Circuits = false, dc2RekeyPlate = false;
            bool dc2CrossCharWeapons = false, dc2RandomizeWeapons = false;
            IReadOnlyDictionary<int, byte>? dc2Weights = null;
            if (bytes.Length >= 16)
            {
                int modeNibble = bytes[11] & 0x0f;
                if (modeNibble is >= 1 && modeNibble <= Dc2CanonicalSpecies.Count)
                {
                    dc2Mode = Dc2EnemyDistributionMode.Fixed;
                    dc2FixedSpecies = Dc2CanonicalSpecies[modeNibble - 1];
                }
                else if (modeNibble != 0)
                {
                    return false; // reserved mode value from a future format — refuse, don't guess
                }
                dc2Setpiece = (bytes[11] & 0x10) != 0;
                dc2Boss     = (bytes[11] & 0x20) != 0;
                dc2Skin     = (Dc2.Passes.Dc2CharacterSkin)(bytes[11] >> 6);
                if (bytes.Length >= 17)
                {
                    dc2ReginaSkin = (Dc2.Passes.Dc2CharacterSkin)(bytes[16] & 0x03);
                    dc2ShuffleShop = (bytes[16] & 0x04) != 0;
                    dc2PuzzleCodes = (bytes[16] & 0x08) != 0;
                    dc2Circuits = (bytes[16] & 0x10) != 0;
                    dc2RekeyPlate = (bytes[16] & 0x20) != 0;
                    dc2CrossCharWeapons = (bytes[16] & 0x40) != 0;
                    dc2RandomizeWeapons = (bytes[16] & 0x80) != 0;
                }

                var weights = new Dictionary<int, byte>(Dc2CanonicalSpecies.Count);
                for (int i = 0; i < Dc2CanonicalSpecies.Count; i++)
                {
                    byte b = bytes[12 + i / 2];
                    weights[Dc2CanonicalSpecies[i]] = (byte)((i % 2 == 0 ? b : b >> 4) & 0x0f);
                }
                // Nibbles equal to the curated defaults decode to null (= "use defaults"), so a
                // block emitted only for the mode/toggle bits round-trips back to a default-weight
                // config instead of pinning today's registry numbers into the config object.
                var defaults = Dc2Distribution.Value.DefaultWeights;
                bool allDefault = weights.All(kv =>
                    defaults.TryGetValue(kv.Key, out var d) && d == kv.Value);
                dc2Weights = allDefault ? null : weights;
            }

            // Raptor tier block, bytes 17–21 (RAPTOR-TIER-RE.md §4); absent in a pre-feature seed,
            // where everything defaults (pass off, curated tier weights, vanilla combo threshold).
            bool raptorTiers = false;
            int blueCombo = 20;
            var raptorColourMode = Dc2RaptorColourMode.RoomTier;
            IReadOnlyDictionary<int, byte>? tierWeights = null;
            if (bytes.Length >= 22)
            {
                raptorTiers = (bytes[17] & 0x80) != 0;
                raptorColourMode = (bytes[17] & 0x20) != 0
                    ? Dc2RaptorColourMode.MixedTiers : Dc2RaptorColourMode.RoomTier;
                blueCombo = (bytes[17] & 0x1f) + 1;
                var tw = new Dictionary<int, byte>(Dc2RaptorTierTable.MaxVariant + 1);
                for (int v = 0; v <= Dc2RaptorTierTable.MaxVariant; v++)
                {
                    byte b = bytes[18 + v / 2];
                    tw[v] = (byte)((v % 2 == 0 ? b : b >> 4) & 0x0f);
                }
                var tierDefaults = Dc2RaptorTiers.Value.DefaultWeights;
                tierWeights = tw.All(kv => tierDefaults.TryGetValue(kv.Key, out var d) && d == kv.Value)
                    ? null : tw;
            }

            // Starting-weapon byte, byte 22 (DC2-STARTING-LOADOUT-PLAN.md); absent in a pre-feature
            // seed, where the option defaults off.
            bool startWeapon = false, addAndEquip = false;
            byte? dylanStartId = null, reginaStartId = null;
            if (bytes.Length >= 23)
            {
                startWeapon = (bytes[22] & 0x80) != 0;
                addAndEquip = (bytes[22] & 0x40) != 0; // bit 6 = add-and-equip → full band selectable
                if (!TryIdOf(FileFormats.Exe.Dc2StartingLoadoutPatch.DylanWeaponIds, bytes[22] & 0x07, addAndEquip, out dylanStartId)
                    || !TryIdOf(FileFormats.Exe.Dc2StartingLoadoutPatch.ReginaWeaponIds, (bytes[22] >> 3) & 0x07, addAndEquip, out reginaStartId))
                    return false; // reserved selection value from a future format — refuse, don't guess
            }

            config = new RandomizerConfig
            {
                RandomizeItems             = (flags & 1) != 0,
                RandomizeEnemies           = (flags & 2) != 0,
                RandomizeDoors             = (flags & 4) != 0,
                RandomizeStartingInventory = (flags & 8) != 0,
                ShuffleKeyItems            = (flags & 16) != 0,
                RandomizeEnemyHp           = (flags & 64) != 0,
                ShuffleKeyItemsIntoPickups = (flags & 128) != 0,
                ReplaceItemPool            = replacePool,
                EnemyDifficulty            = diff,
                RatioAmmo                  = ratioAmmo,
                RatioHealth                = ratioHealth,
                AmmoQuantity               = ammoQty,
                AmmoReduction              = ammoReduction,
                WeaponUpgradeChance        = weaponUpgradeChance,
                PreUpgradedWeaponChance    = preUpgradedWeaponChance,
                EnabledWeaponFamilies      = enabledFamilies,
                Dc2EnemyMode               = dc2Mode,
                Dc2FixedSpeciesType        = dc2FixedSpecies,
                IncludeDc2SetpieceEnemies  = dc2Setpiece,
                IncludeDc2BossEnemies      = dc2Boss,
                Dc2SpeciesWeights          = dc2Weights,
                Dc2CharacterSkin           = dc2Skin,
                Dc2ReginaSkin              = dc2ReginaSkin,
                Dc2ShuffleShop             = dc2ShuffleShop,
                Dc2ScramblePuzzleCodes     = dc2PuzzleCodes,
                Dc2ShuffleCircuits         = dc2Circuits,
                Dc2RekeyPlateDoor          = dc2RekeyPlate,
                Dc2CrossCharWeapons        = dc2CrossCharWeapons,
                Dc2RandomizeWeapons        = dc2RandomizeWeapons,
                Dc2RandomizeRaptorTiers    = raptorTiers,
                Dc2RaptorColourMode        = raptorColourMode,
                Dc2RaptorTierWeights       = tierWeights,
                Dc2BlueRaptorComboThreshold = blueCombo,
                Dc2RandomizeStartWeapon    = startWeapon,
                Dc2AddAndEquipStartWeapon  = addAndEquip,
                Dc2DylanStartWeaponId      = dylanStartId,
                Dc2ReginaStartWeaponId     = reginaStartId,
            };
            seed = new Seed(seedVal);
            return true;
        }
        catch { return false; }
    }

    private static int IndexOfSpecies(int type)
    {
        for (int i = 0; i < Dc2CanonicalSpecies.Count; i++)
            if (Dc2CanonicalSpecies[i] == type) return i;
        return -1;
    }

    /// <summary>id → byte-22 selection: 0 = random (null / not in band), n = band index + 1.</summary>
    internal static int SelectionOf(byte[] band, byte? id)
        => id is { } v ? Array.IndexOf(band, v) + 1 : 0;

    /// <summary>byte-22 selection → id: 0 = random (null); out-of-range = reserved (false). In
    /// replace mode a starting main must be an owned MAIN or it bricks the weapon menu (div-0 at
    /// 0x496EAC), so a shared seed must not carry one — see
    /// <see cref="FileFormats.Exe.Dc2StartingLoadoutPatch.IsSelectableStartId"/>. In add-and-equip
    /// mode (byte-22 bit 6) the ring-guard makes the full band safe, so any in-band id is accepted.</summary>
    internal static bool TryIdOf(byte[] band, int selection, bool addAndEquip, out byte? id)
    {
        id = null;
        if (selection == 0) return true;
        if (selection > band.Length) return false;
        var v = band[selection - 1];
        if (!addAndEquip && !FileFormats.Exe.Dc2StartingLoadoutPatch.IsSelectableStartId(band, v)) return false;
        id = v;
        return true;
    }
}
