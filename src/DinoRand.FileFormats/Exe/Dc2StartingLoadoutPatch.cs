namespace DinoRand.FileFormats.Exe;

/// <summary>
/// The DC2 starting-loadout lever (docs/decisions/dc2/loadout/DC2-STARTING-LOADOUT-PLAN.md): rewrites the
/// main-weapon id inside the new-game bootstrap immediates of <c>Dino2.exe</c>.
///
/// <para><b>Surface.</b> Two coupled sites. (1) The bootstrap <c>0x450D60</c> (EXE-SYMBOLS.md)
/// sets each character's equip word — <c>hi = subIndex</c>, <c>lo = weaponId</c>. Regina's is
/// a true immediate (<c>mov word [eax+0x10B6],0x0202</c>, VA <c>0x450E96</c>). Dylan's flows
/// through <c>edx = 0x0101</c> (VA <c>0x450D68</c>) — but that immediate is DUAL-USE: it is
/// first stored to <c>scene+0x1090</c>, the new-game START ROOM (I3 RCA: patching it moved the
/// spawn to ST0103/ST0104). So Dylan's id is patched at the two downstream equip-word stores
/// instead, via same-length instruction rewrites (<see cref="DylanEquipSites"/>), and the edx
/// immediate is always kept canonical. Only weaponId bytes are configurable — the subIndex
/// bytes stay <c>0x01</c>/<c>0x02</c>, so Machete and the Large Stun Gun stay in the loadout
/// by construction. (2) The new-game inventory init <c>0x496750</c> stocks the
/// weapon-inventory array <c>scene+0x10BC</c> from hardcoded stack templates; the character's
/// main-weapon record id there must follow the equip word or the new weapon has no record
/// (0 ammo, status screen unchanged — the first I3 witness failure mode).</para>
///
/// <para><b>Pool.</b> Cross-character ids are NO-GO (NULL WEP_P grid slots, per-char actor
/// code — DC2-WEAPON-SYSTEM-DECODE.md §4); ids are range-checked against each character's own
/// band. Id 0 (unarmed base package) is excluded. A picked starting main must additionally be an
/// <b>owned MAIN</b> (see <see cref="SelectableDylanIds"/>) or the weapon menu div-0s;
/// <see cref="Apply"/> refuses non-owned-mains unless <c>allowUnsafe</c>. Byte-cites validated by
/// <c>tools/dc2_re/start_loadout_probe.py</c> and <c>weapon_catalog_probe.py</c>.</para>
///
/// <para><b>Non-compounding.</b> Ids are absolute values, not deltas; <see cref="Validate"/>
/// accepts only a pristine-or-previously-patched recognized build (all site bytes fixed except
/// the two weapon-id bytes, which must sit in-band), mirroring
/// <see cref="Dc2MusicTablePatch"/>'s safety stance.</para>
/// </summary>
public static class Dc2StartingLoadoutPatch
{
    /// <summary>File offset of the <c>mov edx,0x0101</c> imm lo byte (VA <c>0x450D69</c>).
    /// NEVER patched anymore: the same immediate is stored to <c>scene+0x1090</c> — the
    /// new-game START ROOM word — before it feeds the equip words (I3 RCA: patching it booted
    /// the game into ST0103/ST0104). Kept only to recognize/repair legacy-lever installs.</summary>
    public const int LegacyDylanWeaponIdOffset = 0x50D69;

    /// <summary>File offset of Regina's weapon-id byte (imm16 lo of the <c>0x0202</c> store, VA <c>0x450E9D</c>).</summary>
    public const int ReginaWeaponIdOffset = 0x50E9D;

    public const byte DylanCanonicalId = 0x01;  // shotgun
    public const byte ReginaCanonicalId = 0x02; // handgun

    /// <summary>Valid weapon-id bands (the character's own WEP_P grid slots; id 0 = unarmed excluded).
    /// WIRE FORMAT: seed byte 22 encodes indices into these arrays — never reorder/remove.</summary>
    public static readonly byte[] DylanWeaponIds = { 0x01, 0x03, 0x04, 0x05, 0x09 };
    public static readonly byte[] ReginaWeaponIds = { 0x02, 0x05, 0x06, 0x07, 0x08 };

    /// <summary>Ids whose FIRE path has been witnessed clean in-game (I3 table). Random picks
    /// draw only from these; explicit picks of other in-band ids are allowed (they are the
    /// witness tool) but the caller should warn.</summary>
    public static readonly byte[] DylanFireWitnessedIds = { 0x01, 0x03 };
    public static readonly byte[] ReginaFireWitnessedIds = { 0x02 };

    /// <summary>The starting mains per character a UI should <b>offer</b> in replace mode, under the
    /// PRISTINE catalog. (The actual replace-mode permit is a <b>live</b> owned-MAIN check against the
    /// exe's catalog in <see cref="Apply"/> via <see cref="IsLiveOwnedMain"/> — this static set is the
    /// pristine-offering hint / seed-decode set, not the runtime gate, because the catalog can be
    /// externally shuffled.)
    /// <para><b>Pristine re-audit (2026-07-05, DC2-WEAPON-SYSTEM-INVESTIGATION.md §3):</b> against the
    /// true pristine catalog (<c>0x704260</c>, verified vs the 2002 <c>Dino2.exe.wpgate-bak</c>) EVERY
    /// band id is already an <b>owned MAIN</b> (ids 01–0b are MAINs; only 10–15 are SUBs) — so the
    /// weapon-ring div-0 (<c>0x496D70</c> → <c>idiv 0x1000/mainCount</c> at <c>0x496EAC</c>) does NOT
    /// occur for any band id in replace mode. (The earlier "04/06/09 are SUBs / 05 is Regina-only"
    /// classification came from a NON-pristine exe state and was wrong.) These sets are therefore the
    /// full owned-main band, minus only the <i>fire</i>-unsafe residual: id <c>0x07</c> (Grenade Gun)
    /// empties its own record on fire → post-fire div-0 (CE gate I1), so it stays out of replace mode
    /// (add-and-equip's ring guard neutralizes it). Grounded by <c>tools/dc2_re/weapon_catalog_probe.py</c>.</para>
    /// <para>id <c>0x05</c> is the sole <b>dual-owned</b> (R+D) main — offered here, but picking it also
    /// arms the other character (see <see cref="CrossCharacterSharedMainIds"/>), so it is dropped from
    /// random draws and warned on explicit pick. Ids beyond <see cref="DylanFireWitnessedIds"/>/
    /// <see cref="ReginaFireWitnessedIds"/> are menu-safe but fire-unwitnessed (the installer warns
    /// "may crash on fire"). The wire bands (<see cref="DylanWeaponIds"/>/<see cref="ReginaWeaponIds"/>)
    /// stay frozen for seed byte-22 index stability — only these derived safe sets change.</para></summary>
    public static readonly byte[] SelectableDylanIds = { 0x01, 0x03, 0x04, 0x05, 0x09 };
    public static readonly byte[] SelectableReginaIds = { 0x02, 0x05, 0x06, 0x08 };

    /// <summary>Main-weapon ids the catalog marks as owned by BOTH playable characters (flags bits
    /// R<c>0x01</c> AND D<c>0x02</c> set — id <c>0x05</c> is the only such MAIN in this build). The
    /// new-game inventory array <c>scene+0x10BC</c> is shared and the weapon ring filters it by the
    /// current character's ownership bit, so a dual-owned id placed in one character's template slot
    /// ALSO appears as an extra main in the OTHER character's ring (loading that character's own
    /// <c>WEP_P&lt;n&gt;</c>). Random draws exclude these so one character's roll can't silently arm
    /// the other; an explicit pick is still allowed (with a warning). Grounded against the pristine
    /// exe by <c>tools/dc2_re/weapon_catalog_probe.py</c> (id 05 flags <c>0x2B</c> = MAIN + R + D).</summary>
    public static readonly byte[] CrossCharacterSharedMainIds = { 0x05 };

    /// <summary>True iff <paramref name="id"/> is a safe starting main for the character whose wire
    /// band is <paramref name="band"/> (owned MAIN, minus the fire-empty residual). Lets callers
    /// that hold only the frozen band (e.g. the seed decoder) apply the rule without the exe.</summary>
    public static bool IsSelectableStartId(byte[] band, byte id) =>
        (ReferenceEquals(band, DylanWeaponIds) ? SelectableDylanIds : SelectableReginaIds).Contains(id);

    // Build fingerprint: the bootstrap instruction bytes, with -1 masking the patchable
    // weapon-id bytes. Byte-verified against the rebirth exe (start_loadout_probe.py).
    // The 0x50D68 imm lo byte is masked ONLY to recognize legacy-lever installs (Validate
    // band-checks it); Apply/Restore always rewrite it to the canonical 0x01.
    private static readonly (int Offset, short[] Bytes)[] Fingerprint =
    {
        (0x50D68, new short[] { 0xBA, -1, 0x01, 0x00, 0x00 }),                          // room+equip imm (dual-use!)
        (0x50E96, new short[] { 0x66, 0xC7, 0x80, 0xB6, 0x10, 0x00, 0x00, -1, 0x02 }),  // Regina preset
        (0x50EB7, new short[] { 0x66, 0xC7, 0x82, 0xBA, 0x10, 0x00, 0x00, 0x0A, 0x05 }),// David preset
        (0x50E7D, new short[] { 0xC6, 0x80, 0x8F, 0x10, 0x00, 0x00, 0x01 }),            // char byte := 1
    };

    // ---- Inventory-template sites inside the new-game inventory init 0x496750 ----------------
    // (docs/decisions/dc2/loadout/DC2-STARTING-LOADOUT-PLAN.md I3 decode; CE-witnessed: scene+0x10BC drives the
    // status screen and the ammo HUD.) The init copies hardcoded stack templates of 12-byte
    // records {present, id, pad2, pad4, count, countMax}; the equipped weapon only has ammo /
    // shows on the status screen if its record exists, so the id bytes must follow the equip
    // lever. Counts are left canonical: the new weapon inherits the replaced record's ammo.
    //
    // Dylan's shotgun record writes present+id via two adjacent register movs (bl = 1); we
    // rewrite the pair as ONE same-length `mov word [esp+disp], (id<<8)|1` + NOPs, which turns
    // the id into a patchable immediate. Regina's handgun id is already an immediate.

    /// <summary>One rewritable Dylan site: the original register-sourced instruction bytes and
    /// the same-length imm-form replacement (id byte at <see cref="IdOffset"/>).</summary>
    private readonly record struct DylanSite(int Offset, byte[] Original, byte[] Patched, int IdOffset);

    // Dylan equip-word writes in the bootstrap, register-sourced from the dual-use edx.
    // Rewritten as `mov word [eax+disp], (subIndex 0x01 << 8)|id` — eax holds the scene ptr
    // at both sites (loaded at 0x450E78 / 0x450E91) — leaving edx (= start room 0x0101) alone.
    private static readonly DylanSite[] DylanEquipSites =
    {
        // 0x450E84: mov ecx,[0x876DB8]; mov [ecx+0x10B4], dx  ->  mov word [eax+0x10B4], imm16
        new(0x50E84,
            new byte[] { 0x8B, 0x0D, 0xB8, 0x6D, 0x87, 0x00, 0x66, 0x89, 0x91, 0xB4, 0x10, 0x00, 0x00 },
            new byte[] { 0x66, 0xC7, 0x80, 0xB4, 0x10, 0x00, 0x00, 0x01, 0x01, 0x90, 0x90, 0x90, 0x90 },
            IdOffset: 7),
        // 0x450E9F: mov ecx,[0x876DB8]; mov eax,0x4B0; mov [ecx+0x10B8], dx
        //   ->      mov word [eax+0x10B8], imm16; mov eax,0x4B0 (preserved — feeds +0x11F8/+0x11FC)
        new(0x50E9F,
            new byte[] { 0x8B, 0x0D, 0xB8, 0x6D, 0x87, 0x00, 0xB8, 0xB0, 0x04, 0x00, 0x00, 0x66, 0x89, 0x91, 0xB8, 0x10, 0x00, 0x00 },
            new byte[] { 0x66, 0xC7, 0x80, 0xB8, 0x10, 0x00, 0x00, 0x01, 0x01, 0xB8, 0xB0, 0x04, 0x00, 0x00, 0x90, 0x90, 0x90, 0x90 },
            IdOffset: 7),
    };

    private static readonly DylanSite[] DylanTemplateSites =
    {
        // template A (mode 0), esp+0xC0: disp32 forms, 7+7 bytes -> 10-byte imm mov + 4 NOPs
        new(0x96786,
            new byte[] { 0x88, 0x9C, 0x24, 0xC0, 0x00, 0x00, 0x00, 0x88, 0x9C, 0x24, 0xC1, 0x00, 0x00, 0x00 },
            new byte[] { 0x66, 0xC7, 0x84, 0x24, 0xC0, 0x00, 0x00, 0x00, 0x01, 0x00, 0x90, 0x90, 0x90, 0x90 },
            IdOffset: 9),
        // template B (mode 1), esp+0x54: disp8 forms, 4+4 bytes -> 7-byte imm mov + 1 NOP
        new(0x968B6,
            new byte[] { 0x88, 0x5C, 0x24, 0x54, 0x88, 0x5C, 0x24, 0x55 },
            new byte[] { 0x66, 0xC7, 0x44, 0x24, 0x54, 0x01, 0x00, 0x90 },
            IdOffset: 6),
        // template C (else mode), esp+0x0C
        new(0x969E9,
            new byte[] { 0x88, 0x5C, 0x24, 0x0C, 0x88, 0x5C, 0x24, 0x0D },
            new byte[] { 0x66, 0xC7, 0x44, 0x24, 0x0C, 0x01, 0x00, 0x90 },
            IdOffset: 6),
    };

    /// <summary>Regina handgun-record id immediates (one per template): the last byte of
    /// <c>mov byte [esp+disp], 2</c> at VAs 0x4967BA / 0x4968D5 / 0x4969FE.</summary>
    private static readonly int[] ReginaTemplateIdOffsets = { 0x967C1, 0x968D9, 0x96A02 };

    private static readonly (int Offset, byte[] Bytes)[] ReginaTemplateFingerprint =
    {
        (0x967BA, new byte[] { 0xC6, 0x84, 0x24, 0xCD, 0x00, 0x00, 0x00 }), // imm follows
        (0x968D5, new byte[] { 0xC6, 0x44, 0x24, 0x61 }),
        (0x969FE, new byte[] { 0xC6, 0x44, 0x24, 0x19 }),
    };

    // ---- Per-weapon starting-ammo override (the swapped main's count/countMax) -----------------
    // A swapped-in main otherwise inherits the template SLOT's fixed count (100 on Normal / 200 on
    // Easy), so e.g. a rocket launcher shows 100 rounds. The game's own canonical per-weapon ammo
    // lives in the weapon table 0x71D7E8[id] (fields +4 = count, +6 = countMax; read by the
    // give-weapon path 0x4090EC at 0x409105/0x409111). Each mode template's R0 (Dylan) / R1 (Regina)
    // record writes count and countMax as two consecutive u16 stores to contiguous fields
    // (record+8 / record+0xA); we rewrite that pair as ONE dword-immediate store. Canonical
    // shotgun/handgun are NOT overridden — they keep the template's deliberate 100/200 start amount.

    // ---- Live catalog ownership (menu-safety is exe-state-dependent) ---------------------------
    // The weapon-ring div-0 fires when the current character has ZERO owned present mains. Whether an
    // id is an owned MAIN is read from the item catalog 0x704260, and that catalog is NOT invariant —
    // an external weapon-shuffle tool can rewrite the MAIN/SUB/owner flags (observed on a live install:
    // ids 03/04/09 flipped owner/class). So a hardcoded "safe set" can be wrong; replace-mode Apply
    // instead checks the picked id against THIS exe's live catalog. add-and-equip's ring guard
    // (Dc2WeaponRingGuardPatch) makes the div-0 impossible regardless of catalog state and bypasses this.

    /// <summary>File offset of the item catalog (VA <c>0x704260</c> → <c>.rdata</c> raw for the
    /// recognized build); 12 B/id, flags u16 at +0xA (MAIN <c>0x08</c>, owner Dylan <c>0x02</c> /
    /// Regina <c>0x01</c>). Grounded by <c>tools/dc2_re/weapon_catalog_probe.py</c>.</summary>
    private const int CatalogFileOffset = 0xE9260;
    private const byte FlagMain = 0x08, OwnerDylan = 0x02, OwnerRegina = 0x01;

    /// <summary>Ids that empty their own record on fire → post-fire div-0 even as a valid owned main
    /// (CE gate I1). Refused in replace mode regardless of catalog; add-and-equip's ring guard covers them.</summary>
    private static readonly byte[] FireEmptyIds = { 0x07 };

    /// <summary>True iff <paramref name="id"/> is, in THIS exe's live catalog, a MAIN owned by
    /// <paramref name="ownerBit"/> — so it keeps the current character's weapon-ring mainCount ≥ 1.</summary>
    private static bool IsLiveOwnedMain(byte[] exe, byte id, byte ownerBit)
    {
        int off = CatalogFileOffset + id * 12 + 0xA;
        ushort flags = (ushort)(exe[off] | (exe[off + 1] << 8));
        return (flags & FlagMain) != 0 && (flags & ownerBit) != 0;
    }

    /// <summary>True iff <paramref name="id"/> is safe to APPEND-and-equip on THIS exe without the
    /// cross-character stale-blob crash (DC2-STARTING-LOADOUT-CROSS-CHAR-RCA.md, crash site 0x4bf073).
    /// Add-and-equip appends BOTH characters' picks into the single shared inventory array
    /// (<see cref="Dc2StartWeaponAppendPatch"/>), so an appended weapon can surface in the OTHER
    /// character's weapon menu. If that character does not own its <c>WEP_P</c> grid slot
    /// (<c>0x71B230</c> = band membership) the load returns nothing and equipping it dereferences a
    /// stale/garbage weapon object. The ring guard only covers the menu div-0, not this. Safe iff the
    /// id is catalog-classed MAIN (so it lands in the ownership-filtered main ring, not an unfiltered
    /// sub menu) AND every character the LIVE catalog marks as an owner also owns its grid slot — i.e.
    /// no character can menu-reach it with a NULL slot. Keys on the live catalog: byte-identical to the
    /// prior full-band behaviour on a pristine exe; only a shuffled/corrupted catalog narrows the set.
    /// A dual-owned MAIN whose grid slot BOTH characters own (id 0x05) stays safe.</summary>
    public static bool IsCrossSafeAddAndEquipMain(byte[] exe, byte id)
    {
        int off = CatalogFileOffset + id * 12 + 0xA;
        ushort flags = (ushort)(exe[off] | (exe[off + 1] << 8));
        if ((flags & FlagMain) == 0) return false;
        if ((flags & OwnerDylan) != 0 && !DylanWeaponIds.Contains(id)) return false;
        if ((flags & OwnerRegina) != 0 && !ReginaWeaponIds.Contains(id)) return false;
        return true;
    }

    /// <summary>One count/countMax store-pair rewrite: the two original u16 register stores and the
    /// same-length <c>mov dword [esp+disp], imm32</c> + NOP replacement (imm32 at <see cref="ImmOffset"/>).</summary>
    private readonly record struct CountSite(int Offset, byte[] Original, byte[] Patched, int ImmOffset);

    // Dylan main record (R0) count+countMax stores; mode-0 uses disp32 (16 B), mode-1/else disp8 (10 B).
    private static readonly CountSite[] DylanCountSites =
    {
        new(0x967A3, Convert.FromHexString("6689bc24c80000006689bc24ca000000"),
                     Convert.FromHexString("c78424c8000000" + "00000000" + "9090909090"), 7),
        new(0x968C7, Convert.FromHexString("668974245c668974245e"),
                     Convert.FromHexString("c744245c" + "00000000" + "9090"), 4),
        new(0x96950, Convert.FromHexString("66897424146689742416"),
                     Convert.FromHexString("c7442414" + "00000000" + "9090"), 4),
    };

    // Regina main record (R1) count+countMax stores.
    private static readonly CountSite[] ReginaCountSites =
    {
        new(0x967D1, Convert.FromHexString("6689bc24d40000006689bc24d6000000"),
                     Convert.FromHexString("c78424d4000000" + "00000000" + "9090909090"), 7),
        new(0x968E3, Convert.FromHexString("66897c246866897c246a"),
                     Convert.FromHexString("c7442468" + "00000000" + "9090"), 4),
        new(0x9695A, Convert.FromHexString("66897424206689742422"),
                     Convert.FromHexString("c7442420" + "00000000" + "9090"), 4),
    };

    /// <summary>Current (dylanId, reginaId) weapon ids. Validates first. Dylan's id comes from
    /// the equip-site rewrite when present, else the legacy imm byte (covers pristine and both
    /// legacy lever generations).</summary>
    public static (byte DylanId, byte ReginaId) Read(byte[] exe)
    {
        Validate(exe);
        var site = DylanEquipSites[0];
        byte dylan = exe.AsSpan(site.Offset, site.Original.Length).SequenceEqual(site.Original)
            ? exe[LegacyDylanWeaponIdOffset]
            : exe[site.Offset + site.IdOffset];
        return (dylan, exe[ReginaWeaponIdOffset]);
    }

    /// <summary>True iff both starting weapons are the canonical shotgun/handgun.</summary>
    public static bool IsCanonical(byte[] exe)
    {
        var (d, r) = Read(exe);
        return d == DylanCanonicalId && r == ReginaCanonicalId;
    }

    /// <summary>
    /// Set the starting main weapons. Ids must be in the character's own band
    /// (<see cref="DylanWeaponIds"/> / <see cref="ReginaWeaponIds"/>); subweapon bytes are
    /// never touched. Validates first and writes nothing on failure.
    /// </summary>
    /// <param name="addAndEquip">When true, the owned-MAIN guard is skipped: the ring-builder div-0
    /// (0x496EAC/0x496ED1) is neutralized externally by <see cref="Dc2WeaponRingGuardPatch"/>, and every
    /// id in each character's own band is WEP_P-loadable, so any in-band id is a safe starting pick.
    /// The in-band range check and id-0 exclusion still apply. The installer only passes true after it
    /// has applied the ring guard to the same exe. See DC2-WEAPON-SYSTEM-INVESTIGATION.md §3.</param>
    public static void Apply(byte[] exe, byte dylanId, byte reginaId, bool allowUnsafe = false, bool addAndEquip = false)
    {
        Validate(exe);
        // Band range-check is UNCONDITIONAL (allowUnsafe/addAndEquip do not cross it): each character's
        // band is exactly its non-NULL WEP_P grid slots (0x71B230). A cross-character id (e.g. Regina's
        // handgun on Dylan) has a NULL slot → LoadWeaponFiles loads nothing → stale-blob baked-VA crash.
        // Cross-character weapons are decoded-POSSIBLE (grid repoint) but gated on CE test X1 — TECH DEBT,
        // see docs/decisions/cross/ROADMAP.md + DC2-WEAPON-SYSTEM-INVESTIGATION.md §5.
        if (!DylanWeaponIds.Contains(dylanId))
            throw new ArgumentOutOfRangeException(nameof(dylanId),
                $"0x{dylanId:X2} is not a Dylan weapon id ({Band(DylanWeaponIds)}).");
        if (!ReginaWeaponIds.Contains(reginaId))
            throw new ArgumentOutOfRangeException(nameof(reginaId),
                $"0x{reginaId:X2} is not a Regina weapon id ({Band(ReginaWeaponIds)}).");
        // Replace-mode menu-safety: the sole starting main must be an owned MAIN in THIS exe's live
        // catalog (else the ring empties → div-0 at 0x496EAC) and not a fire-empty id. Canonical is
        // always allowed (the restore target). add-and-equip / allowUnsafe bypass.
        if (!addAndEquip && !allowUnsafe && dylanId != DylanCanonicalId
            && (!IsLiveOwnedMain(exe, dylanId, OwnerDylan) || FireEmptyIds.Contains(dylanId)))
            throw new ArgumentOutOfRangeException(nameof(dylanId),
                $"0x{dylanId:X2} is not a safe replace-mode starting main for Dylan: it is not a live "
                + "owned MAIN (or it empties its own record on fire), so as the sole starting weapon it "
                + "empties the weapon menu ring → div-0 at 0x496EAC. Enable add-and-equip for the full "
                + "band safely, or pass allowUnsafe to force it.");
        if (!addAndEquip && !allowUnsafe && reginaId != ReginaCanonicalId
            && (!IsLiveOwnedMain(exe, reginaId, OwnerRegina) || FireEmptyIds.Contains(reginaId)))
            throw new ArgumentOutOfRangeException(nameof(reginaId),
                $"0x{reginaId:X2} is not a safe replace-mode starting main for Regina: it is not a live "
                + "owned MAIN (or it empties its own record on fire), so as the sole starting weapon it "
                + "empties the weapon menu ring → div-0 at 0x496EAC. Enable add-and-equip for the full "
                + "band safely, or pass allowUnsafe to force it.");
        // Add-and-equip appends BOTH characters' picks into the SHARED inventory array
        // (Dc2StartWeaponAppendPatch), so a pick the LIVE catalog exposes to the OTHER character — who
        // may not own its WEP_P grid slot — is the cross-character stale-blob crash on equip
        // (DC2-STARTING-LOADOUT-CROSS-CHAR-RCA.md, site 0x4bf073). The ring guard only covers the menu
        // div-0, NOT this, so add-and-equip must still refuse a cross-exposed pick (was: bypassed).
        if (addAndEquip && !allowUnsafe && dylanId != DylanCanonicalId && !IsCrossSafeAddAndEquipMain(exe, dylanId))
            throw new ArgumentOutOfRangeException(nameof(dylanId),
                $"0x{dylanId:X2} is not a cross-safe add-and-equip main for Dylan on this exe: its live "
                + "catalog flags expose it in the other character's weapon menu, but they lack its WEP_P "
                + "grid slot → equipping it crashes (stale-blob at 0x4bf073). Restore the item catalog, "
                + "pick a single-owner main, or pass allowUnsafe to force it.");
        if (addAndEquip && !allowUnsafe && reginaId != ReginaCanonicalId && !IsCrossSafeAddAndEquipMain(exe, reginaId))
            throw new ArgumentOutOfRangeException(nameof(reginaId),
                $"0x{reginaId:X2} is not a cross-safe add-and-equip main for Regina on this exe: its live "
                + "catalog flags expose it in the other character's weapon menu, but they lack its WEP_P "
                + "grid slot → equipping it crashes (stale-blob at 0x4bf073). Restore the item catalog, "
                + "pick a single-owner main, or pass allowUnsafe to force it.");
        // The 0x50D69 imm is the START ROOM (dual-use) — always canonical; repairs legacy installs.
        exe[LegacyDylanWeaponIdOffset] = DylanCanonicalId;
        exe[ReginaWeaponIdOffset] = reginaId;

        // Equip word carries the chosen weapon; the inventory TEMPLATE records + count stores stay
        // PRISTINE. The chosen weapon is APPENDED as an extra record (Dc2StartWeaponAppendPatch), so
        // the default shotgun/handgun record survives: the player keeps a one-handed main to pair with
        // the sub-weapon (fixes the two-handed soft-lock) and the default survives every char-switch
        // restock. Template/count sites are written to pristine here so this also repairs any legacy
        // replace-lever install (which had overwritten them).
        foreach (var site in DylanEquipSites)
        {
            if (dylanId == DylanCanonicalId)
                site.Original.CopyTo(exe, site.Offset);   // canonical id: back to the stock code
            else
            {
                site.Patched.CopyTo(exe, site.Offset);
                exe[site.Offset + site.IdOffset] = dylanId;
            }
        }
        foreach (var site in DylanTemplateSites) site.Original.CopyTo(exe, site.Offset);
        foreach (int off in ReginaTemplateIdOffsets) exe[off] = ReginaCanonicalId;
        foreach (var s in DylanCountSites.Concat(ReginaCountSites)) s.Original.CopyTo(exe, s.Offset);
    }

    /// <summary>Rewrite the equip bytes and inventory-template sites to canonical (the undo;
    /// byte-identical to pristine at every site this lever owns, touches nothing else).</summary>
    public static void RestoreCanonical(byte[] exe)
    {
        Validate(exe);
        exe[LegacyDylanWeaponIdOffset] = DylanCanonicalId;
        exe[ReginaWeaponIdOffset] = ReginaCanonicalId;
        foreach (var site in DylanEquipSites.Concat(DylanTemplateSites))
            site.Original.CopyTo(exe, site.Offset);
        foreach (int off in ReginaTemplateIdOffsets)
            exe[off] = ReginaCanonicalId;
        foreach (var s in DylanCountSites.Concat(ReginaCountSites))
            s.Original.CopyTo(exe, s.Offset);
    }

    /// <summary>
    /// Throw <see cref="InvalidOperationException"/> unless <paramref name="exe"/> is the
    /// recognized build (exact length, <see cref="Dc2WpGatePatch.ExpectedLength"/>) whose
    /// bootstrap sites match the fingerprint and whose weapon-id bytes are in-band — i.e.
    /// pristine or previously patched by this lever, never anything else.
    /// </summary>
    public static void Validate(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (exe.Length != Dc2WpGatePatch.ExpectedLength)
            throw new InvalidOperationException(
                $"Dino2.exe has unexpected length {exe.Length} (expected {Dc2WpGatePatch.ExpectedLength}) — unrecognized build; refusing to touch the starting loadout.");
        foreach (var (offset, bytes) in Fingerprint)
            for (int i = 0; i < bytes.Length; i++)
                if (bytes[i] >= 0 && exe[offset + i] != bytes[i])
                    throw new InvalidOperationException(
                        $"new-game bootstrap bytes at file 0x{offset + i:X} differ from the recognized build — refusing to touch the starting loadout.");
        if (!DylanWeaponIds.Contains(exe[LegacyDylanWeaponIdOffset]) || !ReginaWeaponIds.Contains(exe[ReginaWeaponIdOffset]))
            throw new InvalidOperationException(
                "bootstrap weapon-id byte is outside the known band — not this lever's work; refusing to touch the starting loadout.");

        // Equip + inventory-template sites: each must be the pristine register-sourced bytes or
        // this lever's imm rewrite with an in-band id; Regina imms must be in-band. Anything
        // else is a foreign edit — refuse.
        foreach (var site in DylanEquipSites.Concat(DylanTemplateSites))
        {
            if (exe.AsSpan(site.Offset, site.Original.Length).SequenceEqual(site.Original))
                continue;
            bool patchedShape = true;
            for (int i = 0; i < site.Patched.Length; i++)
                if (i != site.IdOffset && exe[site.Offset + i] != site.Patched[i]) { patchedShape = false; break; }
            if (!patchedShape || !DylanWeaponIds.Contains(exe[site.Offset + site.IdOffset]))
                throw new InvalidOperationException(
                    $"inventory-template bytes at file 0x{site.Offset:X} differ from the recognized build — refusing to touch the starting loadout.");
        }
        for (int k = 0; k < ReginaTemplateFingerprint.Length; k++)
        {
            var (off, bytes) = ReginaTemplateFingerprint[k];
            if (!exe.AsSpan(off, bytes.Length).SequenceEqual(bytes)
                || !ReginaWeaponIds.Contains(exe[ReginaTemplateIdOffsets[k]]))
                throw new InvalidOperationException(
                    $"inventory-template bytes at file 0x{off:X} differ from the recognized build — refusing to touch the starting loadout.");
        }

        // Count-override sites: each must be the pristine store-pair or this lever's dword-imm rewrite
        // (prefix + NOP tail fixed; the 4-byte imm is free). Anything else is a foreign edit — refuse.
        foreach (var s in DylanCountSites.Concat(ReginaCountSites))
        {
            if (exe.AsSpan(s.Offset, s.Original.Length).SequenceEqual(s.Original))
                continue;
            bool patchedShape = true;
            for (int i = 0; i < s.Patched.Length; i++)
                if ((i < s.ImmOffset || i >= s.ImmOffset + 4) && exe[s.Offset + i] != s.Patched[i])
                    { patchedShape = false; break; }
            if (!patchedShape)
                throw new InvalidOperationException(
                    $"inventory-template count bytes at file 0x{s.Offset:X} differ from the recognized build — refusing to touch the starting loadout.");
        }
    }

    private static string Band(byte[] ids) => string.Join("/", ids.Select(i => $"0x{i:X2}"));
}
