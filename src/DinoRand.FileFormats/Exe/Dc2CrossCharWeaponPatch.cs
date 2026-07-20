using System.Buffers.Binary;
using System.Text;

namespace DinoRand.FileFormats.Exe;

/// <summary>Which character receives a weapon they do not natively own.</summary>
public enum Dc2CrossCharTarget
{
    Regina = 0,
    Dylan = 1,
}

/// <summary>One (target character, weapon) cross-serve: <paramref name="OwnerFile"/> supplies the
/// weapon's behaviour tail, <paramref name="GeometryFile"/> the target's own body, and
/// <paramref name="GraftFile"/> is the generated package that carries both.</summary>
/// <param name="HeadGraftSafe">False when the owner's geometry spills past the
/// <c>Dc2WeaponGraft.HeadEnd</c> splice boundary — built, but not yet witnessed in-game.</param>
public sealed record Dc2CrossCharPair(
    Dc2CrossCharTarget Target,
    byte WeaponId,
    string OwnerFile,
    string GeometryFile,
    string GraftFile,
    bool HeadGraftSafe);

/// <summary>
/// Byte-level, reversible <c>Dino2.exe</c> patch behind "Enable Cross Character Weapons": lets Regina
/// and Dylan wield each other's weapons while still rendering with their own model.
/// Decision record: <c>docs/decisions/dc2/models/DC2-CROSS-CHAR-WEAPON-MODEL-SWAP.md</c>.
///
/// <para><b>Why the exe needs touching at all.</b> File resolution is active-character keyed:
/// <c>0x71B230[charBase + weaponId]</c> with Regina <c>0x1E9</c> / Dylan <c>0x1F9</c>
/// (<c>LoadWeaponFiles 0x482420</c>). A character's non-owned weapon has a NULL slot, and a NULL slot
/// makes the loader skip (<c>0x404297 je</c>) leaving the previous model resident — the stale-blob
/// crash class. Filling the slot with a real file removes it. Damage and fire dispatch are id-keyed
/// with no character read (<c>0x47B000</c> → <c>[weaponId*4 + 0x730E30]</c>, damage table
/// <c>0x730920</c>), so behaviour follows the id and is correct by construction.</para>
///
/// <para><b>Three edits per pair:</b> repoint the NULL catalog slot at a new filename string (written
/// into verified <c>.data</c> slack), and OR the target's owner bit into the item catalog
/// <c>0x704260[id]+0xA</c> so the weapon appears in that character's ring. SUB weapons need only the
/// owner bit: they resolve as <c>0x213 + subIndex</c> with no character term
/// (<c>DC2-WEAPON-SYSTEM-DECODE.md</c> §1c), so both characters already share one file.</para>
///
/// <para>The owner bit is OR-ed in and cleared on restore, which is exact for the weapons listed here
/// (their target bit is clear in every pristine build). Bits are never touched on the dual-owned or
/// David-owned ids. Works on an in-memory <c>byte[]</c>; file I/O and the pristine <c>.bak</c> are the
/// installer's concern. Restores only its own slices so it composes with the other Dino2.exe levers.</para>
/// </summary>
public static class Dc2CrossCharWeaponPatch
{
    /// <summary>File offset of file-catalog slot 0 (VA <c>0x71B230</c>) — the same master filename
    /// table the BGM shuffle permutes.</summary>
    public const int CatalogBaseOffset = Dc2MusicTablePatch.TableBaseOffset;

    /// <summary>File offset of item-catalog id 0 (VA <c>0x704260</c>), 12 bytes per id, flags u16 at +0xA.</summary>
    public const int ItemFlagsOffset = 0xE9260;

    /// <summary>Free <c>.data</c> slack the graft filenames are written into (VA <c>0x71BC00</c>);
    /// measured 192 zero bytes in the pristine build, and 8 names need 104.</summary>
    public const int StringSlackOffset = 0x100C00;
    public const int StringSlackLength = 192;

    private const uint StringSlackVa = 0x71BC00;
    private const int ReginaBase = 0x1E9;
    private const int DylanBase = 0x1F9;


    /// <summary>The eight MAIN cross-serves. Derived from the pristine WEP_P grid: a weapon is
    /// single-owner exactly where one character's slot holds a file and the other's is NULL
    /// (0x00/0x05 exist for both, 0x0A/0x0B are David's). Geometry donors are the two body models
    /// proven to fit the head region: WEP_P101 (Dylan) and WEP_P002 (Regina).</summary>
    public static IReadOnlyList<Dc2CrossCharPair> Pairs { get; } = new[]
    {
        // → Dylan (Regina-owned mains)
        New(Dc2CrossCharTarget.Dylan, 0x02, "WEP_P002.DAT", "WEP_P102.DAT", safe: true),   // Hand gun (in-game proven)
        New(Dc2CrossCharTarget.Dylan, 0x06, "WEP_P006.DAT", "WEP_P106.DAT", safe: true),   // Submachine gun
        New(Dc2CrossCharTarget.Dylan, 0x07, "WEP_P007.DAT", "WEP_P107.DAT", safe: true),   // Heavy Machine gun
        New(Dc2CrossCharTarget.Dylan, 0x08, "WEP_P008.DAT", "WEP_P108.DAT", safe: false),  // Missile Pod  (spills to 0x3580)
        // → Regina (Dylan-owned mains)
        New(Dc2CrossCharTarget.Regina, 0x01, "WEP_P101.DAT", "WEP_P001.DAT", safe: true),  // Shotgun
        New(Dc2CrossCharTarget.Regina, 0x03, "WEP_P103.DAT", "WEP_P003.DAT", safe: false), // Rocket Launcher (0x37a0)
        New(Dc2CrossCharTarget.Regina, 0x04, "WEP_P104.DAT", "WEP_P004.DAT", safe: false), // Solid Cannon    (0x35a0)
        New(Dc2CrossCharTarget.Regina, 0x09, "WEP_P109.DAT", "WEP_P009.DAT", safe: false), // Antitank Rifle  (0x35e0)
    };

    /// <summary>Single-owner SUB weapons — Machete (Dylan) and Large Stungun (Regina). Character-
    /// independent file resolution, so these need the owner bit only.</summary>
    public static IReadOnlyList<byte> SubWeaponIds { get; } = new byte[] { 0x11, 0x12 };

    private static Dc2CrossCharPair New(Dc2CrossCharTarget target, byte id, string owner, string graft, bool safe)
        => new(target, id, owner,
               target == Dc2CrossCharTarget.Dylan ? "WEP_P101.DAT" : "WEP_P002.DAT", graft, safe);

    /// <summary>Catalog slot for a (character, weapon id) pair — the <c>charBase + weaponId</c> idiom.</summary>
    public static int CatalogSlot(Dc2CrossCharTarget target, byte weaponId)
        => (target == Dc2CrossCharTarget.Dylan ? DylanBase : ReginaBase) + weaponId;


    /// <summary>Filename a catalog slot currently resolves to, or null if the slot is NULL/unresolvable.</summary>
    public static string? ReadCatalogName(byte[] exe, int slot)
    {
        uint va = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(CatalogBaseOffset + slot * 4, 4));
        if (va < Dc2MusicTablePatch.DataSectionVa
            || va >= Dc2MusicTablePatch.DataSectionVa + Dc2MusicTablePatch.DataSectionSize) return null;
        int off = Dc2MusicTablePatch.DataSectionOffset + (int)(va - Dc2MusicTablePatch.DataSectionVa);
        int end = Array.IndexOf(exe, (byte)0, off);
        if (end < 0 || end == off || end - off > 64) return null;
        return Encoding.ASCII.GetString(exe, off, end - off);
    }

    /// <summary>The u16 owner/class flags for a weapon id (item catalog <c>0x704260[id]+0xA</c>).</summary>
    public static ushort ReadOwnerFlags(ReadOnlySpan<byte> exe, byte weaponId)
        => Dc2OwnerBits.Read(exe, weaponId);

    private static Dc2WeaponOwner TargetOwner(Dc2CrossCharTarget t)
        => t == Dc2CrossCharTarget.Dylan ? Dc2WeaponOwner.Dylan : Dc2WeaponOwner.Regina;

    /// <summary>Owner a SUB weapon's cross-serve target needs (the character that does NOT own it).</summary>
    private static Dc2WeaponOwner SubTargetOwner(byte id)
        => id == 0x11 ? Dc2WeaponOwner.Regina : Dc2WeaponOwner.Dylan;

    private static bool RightBuild(ReadOnlySpan<byte> exe) => exe.Length == Dc2WpGatePatch.ExpectedLength;

    private static bool SlackIsFree(ReadOnlySpan<byte> exe)
    {
        foreach (var b in exe.Slice(StringSlackOffset, StringSlackLength))
            if (b != 0x00) return false;
        return true;
    }

    /// <summary>True iff this is the pristine build ready to patch: correct length, every cross slot
    /// still NULL, and the string slack still free. Only inspects this lever's own sites, so it stays
    /// true when other Dino2.exe patches are already applied.</summary>
    public static bool IsRecognizedPristine(ReadOnlySpan<byte> exe)
    {
        if (!RightBuild(exe) || !SlackIsFree(exe)) return false;
        foreach (var p in Pairs)
        {
            int off = CatalogBaseOffset + CatalogSlot(p.Target, p.WeaponId) * 4;
            if (BinaryPrimitives.ReadUInt32LittleEndian(exe.Slice(off, 4)) != 0) return false;
        }
        return true;
    }

    /// <summary>True iff this exe already carries the cross-character lever.</summary>
    public static bool IsApplied(ReadOnlySpan<byte> exe)
    {
        if (!RightBuild(exe)) return false;
        var buf = exe.ToArray();
        foreach (var p in Pairs)
            if (ReadCatalogName(buf, CatalogSlot(p.Target, p.WeaponId)) != p.GraftFile) return false;
        return true;
    }

    /// <summary>Apply all three edits for every pair, in place. Throws
    /// <see cref="InvalidOperationException"/> (leaving <paramref name="exe"/> untouched) unless
    /// <see cref="IsRecognizedPristine"/>; an already-applied file is a no-op.</summary>
    public static void Apply(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (IsApplied(exe)) return;
        if (!IsRecognizedPristine(exe))
            throw new InvalidOperationException(
                "Dino2.exe is not the recognized pristine build the cross-character-weapon lever targets "
                + "(wrong length, a cross slot already repointed, or the .data string slack is occupied); "
                + "refusing to patch.");

        int cursor = StringSlackOffset;
        foreach (var p in Pairs)
        {
            var name = Encoding.ASCII.GetBytes(p.GraftFile);
            if (cursor + name.Length + 1 > StringSlackOffset + StringSlackLength)
                throw new InvalidOperationException("out of .data slack for the graft filename strings.");
            name.CopyTo(exe, cursor);
            uint va = StringSlackVa + (uint)(cursor - StringSlackOffset);
            cursor += name.Length + 1;   // keep the NUL terminator

            BinaryPrimitives.WriteUInt32LittleEndian(
                exe.AsSpan(CatalogBaseOffset + CatalogSlot(p.Target, p.WeaponId) * 4, 4), va);
            Dc2OwnerBits.Grant(exe, p.WeaponId, TargetOwner(p.Target));
        }

        foreach (var id in SubWeaponIds)
            Dc2OwnerBits.Grant(exe, id, SubTargetOwner(id));
    }

    /// <summary>Revert this lever's own slices (cross slots → NULL, owner bits cleared, slack → zero),
    /// leaving every other Dino2.exe patch intact.</summary>
    public static void Restore(byte[] exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (!RightBuild(exe))
            throw new InvalidOperationException("Dino2.exe wrong length; refusing to restore.");
        if (!IsApplied(exe)) return;

        foreach (var p in Pairs)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                exe.AsSpan(CatalogBaseOffset + CatalogSlot(p.Target, p.WeaponId) * 4, 4), 0u);
            Dc2OwnerBits.Revoke(exe, p.WeaponId, TargetOwner(p.Target));
        }
        foreach (var id in SubWeaponIds)
            Dc2OwnerBits.Revoke(exe, id, SubTargetOwner(id));

        exe.AsSpan(StringSlackOffset, StringSlackLength).Clear();
    }
}
