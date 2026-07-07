using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Install;

namespace DinoRand.Randomizer.Dc2.Passes;

/// <summary>Which character a DC2 protagonist renders as (character-skin swap). One enum serves
/// both Dylan's and Regina's dropdowns; <see cref="Stock"/> = that character's own model.</summary>
public enum Dc2CharacterSkin
{
    /// <summary>Stock — no swap (the character's own model).</summary>
    Stock = 0,
    /// <summary>Extra Crisis Gail (char 6): grafts WP75A/WP79A.</summary>
    Gail = 1,
    /// <summary>Extra Crisis Rick (char 5): grafts WP83A/WP84A.</summary>
    Rick = 2,
    /// <summary>The seed picks stock/Gail/Rick, deterministically (independent per character).</summary>
    Random = 3,
}

/// <summary>
/// DC2 character-skin swap — main-game Dylan renders as Extra Crisis <b>Gail</b> or <b>Rick</b>
/// (docs/reference/dc2/models/DC2-EXTRA-CRISIS-ROSTER-DECODE.md §7–9, in-game verified 2026-07-03).
///
/// <para><b>Mechanism (engine-native graft).</b> Gail/Rick have no models of their own: in Extra
/// Crisis they ride Dylan's WEP_P packages and the engine re-skins them per weapon with a
/// <c>WP&lt;n&gt;A.DAT</c> graft file (64KB texture + ~14KB geometry blob loaded AT the player model
/// base <c>0x662500</c>, overwriting only the model head — anims and the per-weapon fire-effect
/// tail stay Dylan's). This pass serves those graft files under Dylan's six main-game WP slots; the
/// paired <c>Dc2CharacterSkinInstaller</c> nops the flag-(9,0x33) gate so they always load. All six
/// slots must be covered because every weapon change reloads WEP_P (full Dylan) and re-applies the
/// slot's WP graft.</para>
///
/// <para><b>Why this replaces the withdrawn whole-file swap.</b> The old Regina ↔ Dylan whole-file
/// WEP_P swap crashed on weapon fire (per-weapon fire descriptors are reached via .text-baked VAs
/// into the package tail — docs/decisions/dc2/models/DC2-PLAYER-SWAP-FIRE-CRASH-RCA.md). The graft never touches the
/// tail, so that crash class does not apply: this swap is visual-only (weapon ids, behavior, damage
/// unchanged). Regina targets are out of scope pending a cross-rig eyeball (no graft assets exist
/// for her WP row — hers are texture-only).</para>
/// </summary>
public sealed class Dc2PlayerModelSwap : IDc2RandomizationPass
{
    public string Name => "dc2-character-skin";

    /// <summary>
    /// Per-skin graft plan: (Dylan WP slot → donor graft file). Slots = fileId <c>0x227+wid</c> for
    /// Dylan's weapon ids {0,1,3,4,5,9}; donor pairing mirrors the engine's own char-5/6 selects at
    /// <c>0x4826A5</c> (Gail: weapon 5 → WP75A else WP79A; Rick: weapon 3 → WP83A else WP84A).
    /// A data registry — Regina targets would append here after the cross-rig eyeball.
    /// </summary>
    public static readonly IReadOnlyDictionary<Dc2CharacterSkin, IReadOnlyList<(string Target, string Donor)>>
        SkinDonors = new Dictionary<Dc2CharacterSkin, IReadOnlyList<(string, string)>>
    {
        [Dc2CharacterSkin.Gail] = new[]
        {
            ("WP10A.DAT", "WP79A.DAT"),
            ("WP11A.DAT", "WP79A.DAT"),
            ("WP13A.DAT", "WP79A.DAT"),
            ("WP14A.DAT", "WP79A.DAT"),
            ("WP15A.DAT", "WP75A.DAT"),
            ("WP19A.DAT", "WP79A.DAT"),
        },
        [Dc2CharacterSkin.Rick] = new[]
        {
            ("WP10A.DAT", "WP84A.DAT"),
            ("WP11A.DAT", "WP84A.DAT"),
            ("WP13A.DAT", "WP83A.DAT"),
            ("WP14A.DAT", "WP84A.DAT"),
            ("WP15A.DAT", "WP84A.DAT"),
            ("WP19A.DAT", "WP84A.DAT"),
        },
    };

    /// <summary>
    /// Regina's graft plan: her WP row = fileId <c>0x21D+wid</c> for weapon ids {0,2,5,6,7,8}.
    /// Cross-rig (the grafts were authored against Dylan's rig) — in-game verified 2026-07-03:
    /// correct proportions, no crashes, weapons fine. KNOWN TECH DEBT: in-engine cutscenes render
    /// the live grafted player model, so the skin shows there too (cosmetic only; no fix path
    /// without decoding a per-cutscene model reload). Gail keeps his native weapon-5 WP75A pairing
    /// (Regina has weapon 5); Rick's weapon-3 WP83A never applies (she has no weapon 3).
    /// </summary>
    public static readonly IReadOnlyDictionary<Dc2CharacterSkin, IReadOnlyList<(string Target, string Donor)>>
        ReginaSkinDonors = new Dictionary<Dc2CharacterSkin, IReadOnlyList<(string, string)>>
    {
        [Dc2CharacterSkin.Gail] = new[]
        {
            ("WP00A.DAT", "WP79A.DAT"),
            ("WP02A.DAT", "WP79A.DAT"),
            ("WP05A.DAT", "WP75A.DAT"),
            ("WP06A.DAT", "WP79A.DAT"),
            ("WP07A.DAT", "WP79A.DAT"),
            ("WP08A.DAT", "WP79A.DAT"),
        },
        [Dc2CharacterSkin.Rick] = new[]
        {
            ("WP00A.DAT", "WP84A.DAT"),
            ("WP02A.DAT", "WP84A.DAT"),
            ("WP05A.DAT", "WP84A.DAT"),
            ("WP06A.DAT", "WP84A.DAT"),
            ("WP07A.DAT", "WP84A.DAT"),
            ("WP08A.DAT", "WP84A.DAT"),
        },
    };

    /// <summary>
    /// Per-skin menu-atlas donor: <c>CORE&lt;charcode&gt;.DAT</c> carries the localized pause-menu
    /// texture (face portrait + team plates + labels) for its character
    /// (docs/decisions/dc2/models/DC2-INVENTORY-UI-SWAP-PLAN.md, live-witnessed 2026-07-04). The engine keys the load
    /// on the char code, which the skin swap leaves at Dylan/Regina — so the swapped character's
    /// menu art must be carried into the target's CORE file.
    /// </summary>
    public static readonly IReadOnlyDictionary<Dc2CharacterSkin, string> MenuAtlasDonors =
        new Dictionary<Dc2CharacterSkin, string>
        {
            [Dc2CharacterSkin.Gail] = "CORE06.DAT",
            [Dc2CharacterSkin.Rick] = "CORE05.DAT",
        };

    /// <summary>Dylan's / Regina's own CORE files — the menu-atlas swap targets.</summary>
    public const string DylanCoreFile = "CORE01.DAT";
    public const string ReginaCoreFile = "CORE00.DAT";

    public bool IsEnabled(RandomizerConfig config)
        => config.Dc2CharacterSkin != Dc2CharacterSkin.Stock
           || config.Dc2ReginaSkin != Dc2CharacterSkin.Stock;

    /// <summary>Resolve <see cref="Dc2CharacterSkin.Random"/> to a concrete skin, deterministically
    /// from the seed (so a shared seed string reproduces the same character). Fixed choices resolve
    /// to themselves.</summary>
    public static Dc2CharacterSkin ResolveSkin(Dc2CharacterSkin skin, Seed seed, string stream = "dc2-character-skin")
        => skin == Dc2CharacterSkin.Random
            ? (Dc2CharacterSkin)seed.RngFor(stream).Next(0, 3)
            : skin;

    public void Apply(Dc2RandomizationContext context)
    {
        var dataDir = context.DataDir
            ?? throw new InvalidOperationException(
                $"{Name} needs the game Data dir (context.DataDir) to read the WP* graft files");

        var dylanSkin = ResolveSkin(context.Config.Dc2CharacterSkin, context.Seed);
        var reginaSkin = ResolveSkin(context.Config.Dc2ReginaSkin, context.Seed, "dc2-regina-skin");
        if (dylanSkin == Dc2CharacterSkin.Stock && reginaSkin == Dc2CharacterSkin.Stock)
        {
            context.Log($"[{Name}] random roll kept both stock characters — no files emitted");
            context.Spoiler.Section("Player character (DC2)", "Slot", "Skin")
                .AddNote("Dylan and Regina stock (random roll kept both)");
            return;
        }

        // Plan first, emit after: all donors must be present and valid before any is written,
        // so a broken install can never end up half-skinned.
        var plan = Plan(dataDir, dylanSkin, reginaSkin);
        foreach (var (fileName, bytes) in plan)
            context.Sink.EmitFile(fileName, bytes);
        context.Log($"[{Name}] Dylan → {dylanSkin}, Regina → {reginaSkin}: {plan.Count} files emitted "
                    + "(WP grafts + CORE menu atlas + voice bank; WP-gate exe patch applied at install)");
        context.Spoiler.Section("Player character (DC2)", "Slot", "Skin")
            .AddNote($"Dylan renders as {dylanSkin}, Regina as {reginaSkin} "
                     + $"({plan.Count} files: WP grafts + menu portrait/team plate + voice; cosmetic-only)");
    }

    /// <summary>
    /// Build the graft plan for <paramref name="skin"/> (must be Gail or Rick): each of Dylan's six
    /// WP slots serves its donor's pristine bytes, plus his CORE file with the donor's menu atlas
    /// (face portrait + SORT team plate). Validates every donor parses as a DC2 Gian package;
    /// throws (emitting nothing) if any donor is missing or unreadable.
    /// </summary>
    public static IReadOnlyList<(string FileName, byte[] Bytes)> Plan(string dataDir, Dc2CharacterSkin skin)
        => Plan(dataDir, skin, Dc2CharacterSkin.Stock);

    /// <summary>Combined plan for both characters (either may be Stock = no files for them).</summary>
    public static IReadOnlyList<(string FileName, byte[] Bytes)> Plan(
        string dataDir, Dc2CharacterSkin dylanSkin, Dc2CharacterSkin reginaSkin)
    {
        var plan = new List<(string, byte[])>();
        foreach (var (registry, skin, coreTarget) in new[]
                 {
                     (SkinDonors, dylanSkin, DylanCoreFile),
                     (ReginaSkinDonors, reginaSkin, ReginaCoreFile),
                 })
        {
            if (skin == Dc2CharacterSkin.Stock) continue;
            if (!registry.TryGetValue(skin, out var donors))
                throw new ArgumentOutOfRangeException(nameof(skin), skin, "no graft plan for this skin");
            foreach (var (target, donor) in donors)
                plan.Add((target, ReadPristineValidated(dataDir, donor)));
            plan.Add((coreTarget, BuildCoreFile(dataDir, coreTarget, MenuAtlasDonors[skin])));
        }
        return plan;
    }

    // Team-plate art inside the CORE menu-atlas texture: 4bpp strip 0 (64 B rows), byte columns
    // 48..63, rows 0..7 = "S.O.R.T." and rows 8..15 = "T.R.A.T." (identical bytes in all CORE
    // files; docs/decisions/dc2/models/DC2-INVENTORY-UI-SWAP-PLAN.md §4-5). The menu draws the TRAT row whenever the
    // char code is 1 (Dylan), so under a skin swap the SORT row is copied over it.
    private const int PlateRowStride = 64;
    private const int PlateColumnStart = 48;
    private const int PlateColumnCount = 16;
    private const int PlateRowCount = 8;

    // Menu NAME text: the inventory/status header name is 4bpp art in the same strip 0 — one glyph
    // run "REGINADYLAN" at pixel rows 40..45 (REGINA cols 24..56, DYLAN cols 57..85; 2 px/byte,
    // low nibble = even column). It is byte-identical in ALL CORE files (the EC donors CORE05/06
    // carry it too), and the drawer picks the sub-rect by char code at draw time — which is why the
    // whole-atlas donor copy leaves the name reading "DYLAN". No GAIL/RICK name art exists anywhere
    // (EC-select names are font-rendered), so the swap composes the donor's name from same-style
    // black-background glyphs already in the atlas: letters harvested from the name run itself plus
    // C/K from strip 1's "RECOVERY"/"KEY ITEM" labels (same ~6-row height).
    // docs/decisions/dc2/models/DC2-INVENTORY-UI-SWAP-PLAN.md §7.
    private const int NameRowTop = 40;
    private const int NameGlyphRows = 6;
    private const int DylanNameCol = 57;
    private const int DylanNameWidth = 29;
    private const int ReginaNameCol = 24;
    private const int ReginaNameWidth = 33;

    // Glyph cells in strip pixel space: (strip index, top row, left col, width).
    private static readonly IReadOnlyDictionary<char, (int Strip, int Row, int Col, int Width)> NameGlyphs =
        new Dictionary<char, (int, int, int, int)>
        {
            ['R'] = (0, NameRowTop, 24, 6),
            ['G'] = (0, NameRowTop, 36, 6),
            ['I'] = (0, NameRowTop, 42, 3),
            ['A'] = (0, NameRowTop, 51, 6),
            ['L'] = (0, NameRowTop, 69, 5),
            ['C'] = (1, 144, 16, 8), // from "RECOVERY"
            ['K'] = (1, 136, 0, 8),  // from "KEY ITEM"
        };

    /// <summary>
    /// Overwrite the character-name glyph span (rows 40..45 of strip 0) with <paramref name="word"/>
    /// composed from same-font glyphs elsewhere in the strip. Sources are snapshotted first because
    /// they can overlap the destination (e.g. Regina's span contains the R/G/I/A source cells).
    /// </summary>
    public static void ComposeMenuName(byte[] tex, int texOff, int destCol, int destWidth, string word)
    {
        var strips = tex.AsSpan(texOff, 0x8000).ToArray(); // strips 0-1 snapshot to read glyphs from

        static int GetPx(byte[] buf, int off, int x, int y)
        {
            byte b = buf[off + y * PlateRowStride + (x >> 1)];
            return (x & 1) == 0 ? b & 0xF : b >> 4;
        }
        static void SetPx(byte[] buf, int off, int x, int y, int v)
        {
            int i = off + y * PlateRowStride + (x >> 1);
            buf[i] = (byte)((x & 1) == 0 ? (buf[i] & 0xF0) | v : (buf[i] & 0x0F) | (v << 4));
        }

        for (int y = 0; y < NameGlyphRows; y++)
            for (int x = 0; x < destWidth; x++)
                SetPx(tex, texOff, destCol + x, NameRowTop + y, 0);

        int cursor = destCol;
        foreach (char ch in word)
        {
            var (strip, row, col, width) = NameGlyphs[ch];
            if (cursor + width > destCol + destWidth)
                throw new InvalidDataException($"menu name '{word}' does not fit its {destWidth}-px span");
            for (int y = 0; y < NameGlyphRows; y++)
                for (int x = 0; x < width; x++)
                    SetPx(tex, texOff, cursor + x, NameRowTop + y, GetPx(strips, strip * 0x4000, col + x, row + y));
            cursor += width + 1;
        }
    }

    /// <summary>
    /// Build the target character's CORE file carrying the donor's menu atlas AND voice bank:
    /// pristine target bytes with the donor's TEXTURE and PALETTE entry payloads copied over
    /// (same-size, in-place — CORE files share one atlas layout and only the face/plate art
    /// differs), the SORT team-plate row copied over the TRAT row so the team name matches
    /// Gail/Rick too, and finally the donor's SOUND entry (the character grunt/hurt RIFF-WAV bank,
    /// docs/decisions/dc2/voice/DC2-CHARACTER-VOICE-SFX-PLAN.md) swapped in via a repack — SOUND sizes differ per
    /// character, which is safe: the engine heap-stages the bank and resolves samples via
    /// bank-relative offsets, never baked addresses. DATA (per-char params, 120 B) stays the
    /// target's own.
    /// </summary>
    public static byte[] BuildCoreFile(string dataDir, string targetName, string donorName)
    {
        var target = ReadPristineValidated(dataDir, targetName);
        var donor = ReadPristineValidated(dataDir, donorName);
        var texOff = CopyEntry(GianEntryType.Texture);
        CopyEntry(GianEntryType.Palette);

        for (int y = 0; y < PlateRowCount; y++)
            Array.Copy(target, texOff + y * PlateRowStride + PlateColumnStart,
                       target, texOff + (y + PlateRowCount) * PlateRowStride + PlateColumnStart,
                       PlateColumnCount);

        // Menu name text follows the skin: compose GAIL/RICK over the target char's name span.
        ComposeMenuName(target, texOff,
            targetName == ReginaCoreFile ? ReginaNameCol : DylanNameCol,
            targetName == ReginaCoreFile ? ReginaNameWidth : DylanNameWidth,
            MenuAtlasDonors.Single(kv => kv.Value == donorName).Key == Dc2CharacterSkin.Gail
                ? "GAIL" : "RICK");

        // Voice bank: whole SOUND-entry swap (sizes differ → repack, in-place copy can't apply).
        var donorSound = GianPackage.TryParse(donor)!.Entries.Where(e => e.Type == GianEntryType.Sound).ToList();
        if (donorSound.Count != 1)
            throw new InvalidDataException(
                $"{donorName} has {donorSound.Count} SOUND entries (expected exactly 1) — refusing the voice swap");
        return PackageRepacker.ReplaceEntryDc2(
            target, GianEntryType.Sound,
            donor.AsSpan(donorSound[0].PayloadOffset, (int)donorSound[0].DeclaredSize));

        int CopyEntry(GianEntryType type)
        {
            // ReadPristineValidated already proved both parse as DC2 packages.
            var targetEntry = Single(GianPackage.TryParse(target)!, targetName);
            var donorEntry = Single(GianPackage.TryParse(donor)!, donorName);
            if (targetEntry.DeclaredSize != donorEntry.DeclaredSize)
                throw new InvalidDataException(
                    $"{donorName} {type} entry is {donorEntry.DeclaredSize} B but {targetName}'s is "
                    + $"{targetEntry.DeclaredSize} B — refusing the menu-atlas swap");
            Array.Copy(donor, donorEntry.PayloadOffset,
                       target, targetEntry.PayloadOffset, (int)targetEntry.DeclaredSize);
            return targetEntry.PayloadOffset;

            GianEntry Single(GianPackage pkg, string name)
            {
                var matches = pkg.Entries.Where(e => e.Type == type).ToList();
                return matches.Count == 1
                    ? matches[0]
                    : throw new InvalidDataException(
                        $"{name} has {matches.Count} {type} entries (expected exactly 1) — refusing "
                        + "the menu-atlas swap");
            }
        }
    }

    /// <summary>
    /// Read a graft donor's <b>pristine</b> bytes: prefer the installer backup
    /// (<c>.dinorand_backup\name</c>), then a tools-style <c>name.dinorand-bak</c> sibling, then the
    /// live file — so re-rolling over an installed swap always grafts the vanilla files, never
    /// compounds. Validates the bytes parse as a DC2 Gian package (content check, never size).
    /// </summary>
    private static byte[] ReadPristineValidated(string dataDir, string fileName)
    {
        string live = Path.Combine(dataDir, fileName);
        string installerBackup = Path.Combine(dataDir, GameInstaller.BackupDirName, fileName);
        string siblingBackup = live + ".dinorand-bak";

        string source = File.Exists(installerBackup) ? installerBackup
                      : File.Exists(siblingBackup) ? siblingBackup
                      : live;
        if (!File.Exists(source))
            throw new FileNotFoundException($"DC2 graft donor file missing: {live}", live);

        var bytes = File.ReadAllBytes(source);
        var pkg = GianPackage.TryParse(bytes);
        if (pkg is null || !pkg.IsDc2 || pkg.Entries.Count == 0)
            throw new InvalidDataException(
                $"{Path.GetFileName(source)} does not parse as a DC2 Gian package — refusing to swap");
        return bytes;
    }
}
