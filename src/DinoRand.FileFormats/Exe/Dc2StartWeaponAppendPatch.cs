namespace DinoRand.FileFormats.Exe;

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch that <b>appends</b> the randomizer's chosen starting
/// weapon(s) as extra records in the new-game weapon inventory, <b>without removing the default
/// shotgun/handgun</b>. Companion to <see cref="Dc2StartingLoadoutPatch"/> (which sets the equip word):
/// the loadout patch equips the chosen weapon, this patch gives it an inventory record so the default
/// one-handed weapon survives. Decision record:
/// <c>docs/decisions/dc2/loadout/DC2-STARTING-LOADOUT-PLAN.md</c>.
///
/// <para><b>Why.</b> The v1 lever <i>replaced</i> the default record with the chosen weapon. A
/// two-handed main (e.g. the flamethrower) then blocks the sub-weapon (Machete / Large Stun Gun),
/// which is required to progress — and with the one-handed default gone the player can't switch to a
/// one-handed-main + sub setup → soft-lock. Keeping the default record (pristine template) and
/// appending the chosen weapon fixes it, and because the default main is always present the
/// weapon-ring builder's <c>mainCount ≥ 1</c> unconditionally (the <c>0x496EAC</c> div-0 can't occur).</para>
///
/// <para><b>How.</b> The new-game inventory init's three mode branches converge on one <c>rep movsd</c>
/// at VA <c>0x496A7C</c> that copies the stack template into the live 20-slot array <c>scene+0x10BC</c>;
/// after it <c>edi</c> points at the first free (zeroed) record slot. This patch steals the 5-byte
/// <c>mov esi,3</c> at VA <c>0x496A7E</c> (fall-through-only ⇒ boundary-safe) with a <c>jmp</c> to a
/// cave that writes a 12-byte record <c>{present=1, id, …, count, countMax}</c> per non-canonical pick
/// into <c>[eax]</c> (a scratch copy of <c>edi</c> — <c>eax</c> is reloaded at the return site, so
/// <c>edi</c> is left untouched), re-does the stolen <c>mov esi,3</c>, and jumps back to <c>0x496A83</c>.
/// The ring builder walks all 20 slots by the <c>present</c> flag, so the appended record is
/// auto-discovered — no record count to bump. Slots are pre-zeroed (<c>rep stosd</c> at <c>0x4969B7</c>),
/// so only <c>present/id/count/countMax</c> are written.</para>
///
/// <para><b>Safety.</b> Non-ASLR exe (imagebase <c>0x400000</c>, no <c>.reloc</c>), so the cave's
/// absolute jumps need no fix-up and VA = file offset + <c>0x400000</c> (fo == rva in <c>.text</c>).
/// The cave sits in <c>.text</c> zero slack just past the weapon-ring-guard cave
/// (<see cref="Dc2WeaponRingGuardPatch.CaveOffset"/> + its length), so the two compose. Absolute /
/// idempotent: <see cref="Apply"/> rewrites the hook + cave wholesale (clears the region first, so a
/// re-apply with fewer records leaves no stale bytes). Both picks canonical ⇒ nothing to append ⇒
/// <see cref="Restore"/>. Works on an in-memory <c>byte[]</c>; file I/O + the pristine <c>.bak</c> are
/// the installer's concern.</para>
/// </summary>
public static class Dc2StartWeaponAppendPatch
{
    /// <summary>File offset of the stolen <c>mov esi,3</c> (VA <c>0x496A7E</c>), just after the
    /// inventory-init <c>rep movsd</c> — <c>edi</c> = first free record slot of <c>scene+0x10BC</c>.</summary>
    public const int HookOffset = 0x96A7E;

    /// <summary>File offset of the append cave (VA <c>0x4E7470</c>) — <c>.text</c> zero slack just past
    /// the weapon-ring-guard cave (<c>0xE7440</c> + 42 = <c>0xE746A</c>).</summary>
    public const int CaveOffset = 0xE7470;

    private static readonly byte[] HookOriginal = { 0xBE, 0x03, 0x00, 0x00, 0x00 }; // mov esi, 3
    private const int ReturnOffset = 0x96A83;  // instruction after the stolen mov esi,3 (VA 0x496A83)
    private const int CaveClearLength = 64;    // >= max cave (mov eax,edi + 2 records + mov esi,3 + jmp = 56)

    // Canonical starting count/countMax per weapon id (weapon table 0x71D7E8[id], +4/+6), grounded by
    // tools/dc2_re/weapon_catalog_probe.py. The appended record carries the weapon's own ammo; only
    // non-canonical mains are ever appended, so every id below has an entry.
    private static readonly Dictionary<byte, ushort> WeaponStartCounts = new()
    {
        [0x03] = 20, [0x04] = 50, [0x05] = 300, [0x06] = 1000, [0x07] = 300, [0x08] = 20, [0x09] = 50,
    };
    private static ushort StartCount(byte id) => WeaponStartCounts.GetValueOrDefault(id, (ushort)100);

    private static bool RightBuild(ReadOnlySpan<byte> exe) => exe.Length == Dc2WpGatePatch.ExpectedLength;

    // jmp <cave> — the imagebase cancels, so the rel32 is a pure file-offset delta.
    private static readonly byte[] HookJmp = BuildJmp(HookOffset, CaveOffset);

    private static byte[] BuildJmp(int fromOffset, int toOffset)
    {
        var b = new byte[5];
        b[0] = 0xE9;
        BitConverter.GetBytes(toOffset - (fromOffset + 5)).CopyTo(b, 1);
        return b;
    }

    /// <summary>True iff this exe already carries the append hook.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
        => RightBuild(exe) && exe.Slice(HookOffset, 5).SequenceEqual(HookJmp);

    /// <summary>Install the append hook + cave for the non-canonical picks. Both picks canonical ⇒
    /// <see cref="Restore"/> (nothing to append). Throws (leaving <paramref name="exe"/> untouched)
    /// on a wrong-length buffer or a foreign edit at the hook site.</summary>
    public static void Apply(byte[] exe, byte dylanId, byte reginaId)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException(
                $"Dino2.exe has unexpected length {exe.Length} (expected {Dc2WpGatePatch.ExpectedLength}); refusing to append a starting weapon.");
        if (!exe.AsSpan(HookOffset, 5).SequenceEqual(HookOriginal) && !IsApplied(exe))
            throw new InvalidOperationException(
                $"new-game inventory-init bytes at file 0x{HookOffset:X} differ from the recognized build; refusing to append a starting weapon.");

        bool appendD = dylanId != Dc2StartingLoadoutPatch.DylanCanonicalId;
        bool appendR = reginaId != Dc2StartingLoadoutPatch.ReginaCanonicalId;
        if (!appendD && !appendR) { Restore(exe); return; }

        var cave = BuildCave(appendD ? dylanId : (byte?)null, appendR ? reginaId : (byte?)null);
        exe.AsSpan(CaveOffset, CaveClearLength).Clear(); // absolute: no stale bytes from a longer prior cave
        cave.CopyTo(exe, CaveOffset);
        HookJmp.CopyTo(exe, HookOffset);
    }

    /// <summary>Revert the hook to <c>mov esi,3</c> and clear the cave slack. No-op if not applied.</summary>
    public static void Restore(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException("Dino2.exe wrong length; refusing to restore.");
        if (!IsApplied(exe)) return;
        HookOriginal.CopyTo(exe, HookOffset);
        exe.AsSpan(CaveOffset, CaveClearLength).Clear();
    }

    // mov eax,edi ; [record blocks] ; mov esi,3 (stolen) ; jmp 0x496A83.
    private static byte[] BuildCave(byte? dylanId, byte? reginaId)
    {
        var b = new List<byte> { 0x89, 0xF8 }; // mov eax, edi  (edi = free slot; eax is dead scratch)
        if (dylanId is byte d) EmitRecord(b, d);
        if (reginaId is byte r) EmitRecord(b, r);
        b.AddRange(HookOriginal);              // mov esi, 3  (re-do the stolen instruction)
        int jmpOffset = CaveOffset + b.Count;  // file offset of the jmp about to be appended
        b.AddRange(BuildJmp(jmpOffset, ReturnOffset));
        return b.ToArray();
    }

    // One 12-byte record at [eax], then advance eax by 12 (the trailing add is harmless after the last).
    private static void EmitRecord(List<byte> b, byte id)
    {
        ushort c = StartCount(id);
        byte lo = (byte)c, hi = (byte)(c >> 8);
        b.AddRange(new byte[] { 0xC6, 0x00, 0x01 });                // mov byte [eax], 1        (present)
        b.AddRange(new byte[] { 0xC6, 0x40, 0x01, id });            // mov byte [eax+1], id
        b.AddRange(new byte[] { 0x66, 0xC7, 0x40, 0x08, lo, hi });  // mov word [eax+8], count
        b.AddRange(new byte[] { 0x66, 0xC7, 0x40, 0x0A, lo, hi });  // mov word [eax+0xA], countMax
        b.AddRange(new byte[] { 0x83, 0xC0, 0x0C });                // add eax, 0x0C
    }
}
