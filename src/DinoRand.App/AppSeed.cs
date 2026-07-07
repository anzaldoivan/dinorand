#nullable enable
using DinoRand.Randomizer;
using DinoRand.Randomizer.Definitions;

namespace DinoRand.App;

/// <summary>
/// A shareable run identity: the <see cref="Seed"/> plus the full
/// <see cref="RandomizerConfig"/> packed into a short <c>DINO-{base64url}</c> string
/// (~12 chars), BioRand-style. Anyone pasting the string back reproduces the same run.
/// </summary>
/// <remarks>
/// Encoding is 10 or 11 bytes (legacy 6-, 8-, and 9-byte payloads still parse — see below):
///   bytes 0–3  Seed.Value (int, little-endian)
///   byte 4     config flags — bit0=Items, bit1=Enemies, bit2=Doors, bit3=StartingInventory,
///              bit4=ShuffleKeyItems, bit5=ReplaceItemPool
///   byte 5     EnemyDifficulty scaled to 0–255
///   byte 6     RatioAmmo (0–31)
///   byte 7     RatioHealth in bits 0–4 (0–31), AmmoQuantity in bits 5–7 (0–7)
///   byte 8     WeaponUpgradeChance in bits 0–3 (round(chance*15), decoded /15.0),
///              PreUpgradedWeaponChance in bits 4–7 (round(chance*15), decoded /15.0)
///   byte 9     EnabledWeaponFamilies in bits 0–2 (bit0=Handgun, bit1=Shotgun, bit2=GrenadeGun;
///              set = enabled — the WeaponFamily flag values)
///   byte 10    AmmoReduction in bits 0–2 (0–7) — the "less ammo" side of the quantity dial. OMITTED
///              when 0 (the default), so a seed that doesn't reduce ammo stays a byte-identical 10-byte
///              payload; only a reducing config emits the 11th byte.
/// Back-compat: a 6-byte payload (pre-pie seeds) decodes with ReplaceItemPool=true and
/// RatioAmmo=RatioHealth=AmmoQuantity=0; a payload shorter than 9 bytes (pre-weapon-knob seeds)
/// decodes with WeaponUpgradeChance=1.0 and PreUpgradedWeaponChance=0.0; a payload shorter than 10
/// bytes (pre-weapon-family seeds) decodes with EnabledWeaponFamilies=All; a payload shorter than 11
/// bytes (pre-ammo-reduction seeds) decodes with AmmoReduction=0 — so an old shared seed reproduces
/// its original run.
/// (DinoRand's <see cref="Seed"/> is a 32-bit int, so 4 bytes — not the 8 a ulong would need.)
/// </remarks>
public sealed class AppSeed
{
    public Seed Seed { get; }
    public RandomizerConfig Config { get; }

    private AppSeed(Seed seed, RandomizerConfig config) { Seed = seed; Config = config; }

    public static AppSeed Random()
        => new(Seed.Random(), new RandomizerConfig());

    public static bool TryParse(string s, out AppSeed result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (!s.StartsWith("DINO-", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var b64 = s[5..].Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '='));
            if (bytes.Length is not (6 or 8 or 9 or 10 or 11)) return false;
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

            // Weapon-upgrade knobs live in byte 8; absent in a pre-weapon-knob seed (<9 bytes), where
            // they default (WeaponUpgradeChance=1.0, PreUpgradedWeaponChance=0.0) so the run is identical
            // to before the bump. Each nibble holds round(chance*15) and decodes as nibble/15.0.
            bool hasWeapon = bytes.Length >= 9;
            double weaponUpgradeChance     = hasWeapon ? (bytes[8] & 0x0f) / 15.0 : 1.0;
            double preUpgradedWeaponChance = hasWeapon ? (bytes[8] >> 4) / 15.0 : 0.0;

            // Per-family weapon toggles live in byte 9 (low 3 bits = the WeaponFamily flag values);
            // absent in a pre-weapon-family seed (<10 bytes), where every family is enabled (= All) so
            // the run is byte-identical to before the bump.
            var enabledFamilies = bytes.Length >= 10
                ? (WeaponFamily)(bytes[9] & 0x07)
                : WeaponFamily.All;

            // Ammo reduction lives in byte 10 (low 3 bits); absent in a pre-ammo-reduction seed (<11 bytes),
            // where it defaults to 0 (no reduction) so the run is byte-identical to before the bump.
            byte ammoReduction = bytes.Length >= 11 ? (byte)(bytes[10] & 0x07) : (byte)0;

            var config = new RandomizerConfig
            {
                RandomizeItems             = (flags & 1) != 0,
                RandomizeEnemies           = (flags & 2) != 0,
                RandomizeDoors             = (flags & 4) != 0,
                RandomizeStartingInventory = (flags & 8) != 0,
                ShuffleKeyItems            = (flags & 16) != 0,
                ReplaceItemPool            = replacePool,
                EnemyDifficulty            = diff,
                RatioAmmo                  = ratioAmmo,
                RatioHealth                = ratioHealth,
                AmmoQuantity               = ammoQty,
                AmmoReduction              = ammoReduction,
                WeaponUpgradeChance        = weaponUpgradeChance,
                PreUpgradedWeaponChance    = preUpgradedWeaponChance,
                EnabledWeaponFamilies      = enabledFamilies,
            };
            result = new AppSeed(new Seed(seedVal), config);
            return true;
        }
        catch { return false; }
    }

    public AppSeed WithConfig(RandomizerConfig config) => new(Seed, config);
    public AppSeed WithNewSeed() => new(Seed.Random(), Config);

    public override string ToString()
    {
        // 11 bytes only when ammo is reduced; otherwise 10, so every non-reducing config keeps its
        // historical byte-identical seed string (the 11th byte is a pure additive extension).
        byte ammoReduction = (byte)Math.Min((byte)7, Config.AmmoReduction);
        var bytes = new byte[ammoReduction > 0 ? 11 : 10];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), Seed.Value);
        bytes[4] = (byte)(
            (Config.RandomizeItems             ? 1 : 0) |
            (Config.RandomizeEnemies           ? 2 : 0) |
            (Config.RandomizeDoors             ? 4 : 0) |
            (Config.RandomizeStartingInventory ? 8 : 0) |
            (Config.ShuffleKeyItems            ? 16 : 0) |
            (Config.ReplaceItemPool            ? 32 : 0));
        bytes[5] = (byte)Math.Round(Math.Clamp(Config.EnemyDifficulty, 0, 1) * 255);
        bytes[6] = (byte)Math.Min((byte)31, Config.RatioAmmo);
        bytes[7] = (byte)((Math.Min((byte)31, Config.RatioHealth)) |
                          ((Math.Min((byte)7, Config.AmmoQuantity)) << 5));
        int weaponNibble = (int)Math.Round(Math.Clamp(Config.WeaponUpgradeChance, 0, 1) * 15);
        int preUpgNibble = (int)Math.Round(Math.Clamp(Config.PreUpgradedWeaponChance, 0, 1) * 15);
        bytes[8] = (byte)((weaponNibble & 0x0f) | ((preUpgNibble & 0x0f) << 4));
        bytes[9] = (byte)((int)Config.EnabledWeaponFamilies & 0x07);
        if (ammoReduction > 0)
            bytes[10] = (byte)(ammoReduction & 0x07);
        var b64 = Convert.ToBase64String(bytes).TrimEnd('=')
                         .Replace('+', '-').Replace('/', '_');
        return $"DINO-{b64}";
    }
}
