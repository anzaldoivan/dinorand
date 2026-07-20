using DinoRand.FileFormats.Compression;

namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// Builds a cross-character DC2 <c>WEP_P</c> package: the weapon OWNER's file (so the weapon id keeps
/// its fire tail, sound, SE and <c>.text</c>-baked fire descriptors) carrying the TARGET character's
/// own body model, so "Dylan fires Regina's handgun" renders as Dylan.
/// Decision record: <c>docs/decisions/dc2/models/DC2-CROSS-CHAR-WEAPON-MODEL-SWAP.md</c> §5.
///
/// <para><b>Layout</b> (decompressed LZSS0 entry, loads at <c>0x662500</c>): <c>[0x00,0x0C)</c> three
/// absolute pointers (<c>[0]=0x662648</c> fixed), <c>[0x0C,<see cref="HeadEnd"/>)</c> geometry head,
/// then the fire tail. Head and tail are disjoint fixed regions, so the raw head splice is
/// size-preserving — which matters because the tail's descriptor VAs are baked into <c>.text</c> as
/// <c>push imm32</c> and must not move. This also sidesteps the dcmtool mesh-re-derive budget
/// overflow that blocks the glTF route (<c>DC2-CHARACTER-SKIN-SWAP-PLAN.md</c>:41).</para>
///
/// <para><b>The 0x3550 boundary is not universal.</b> Measured 2026-07-19 across the pristine build:
/// <c>[0x3550,0x3800)</c> is <c>0xCD</c> filler slack in most files, but the four large weapons spill
/// real geometry past it (WEP_P008 <c>0x3580</c>, WEP_P103 <c>0x37a0</c>, WEP_P104 <c>0x35a0</c>,
/// WEP_P109 <c>0x35e0</c>). For those, splicing the (shorter) target head leaves a stranded fragment
/// of the owner's model behind, so <see cref="Build"/> refills <c>[HeadEnd, ownerGeometryEnd)</c> with
/// <c>0xCD</c> to match what a small model looks like. Harmless iff the engine walks the model from the
/// header pointers rather than linearly — <b>unwitnessed in-game</b>; those pairs are flagged
/// <c>HeadGraftSafe = false</c> in <c>Dc2CrossCharWeaponPatch.Pairs</c>.</para>
/// </summary>
public static class Dc2WeaponGraft
{
    /// <summary>End of the geometry head — the splice boundary (head is <c>[0x00,0x3550)</c>).</summary>
    public const int HeadEnd = 0x3550;

    /// <summary>End of the filler-slack window; the fire tail begins here.</summary>
    private const int SlackEnd = 0x3800;

    /// <summary>MSVC uninitialized fill — what unused model slack holds in every stock WEP_P.</summary>
    private const byte Filler = 0xCD;

    /// <summary>Entry indices carrying the character's BODY skin in the 7-entry WEP_P shape
    /// <c>SOUND, DATA, TEXTURE, PALETTE, TEXTURE, PALETTE, LZSS0</c>.
    ///
    /// <para>Only entries 2/3 — measured 2026-07-20 across all nine stock weapon files, entry 2 is
    /// 65536 B and entry 3 is 512 B in <b>every</b> one (the character body texture + palette), while
    /// entries 4/5 vary per weapon (20480/32768 and 96/64/192) because they are the <b>weapon's own
    /// effect</b> texture + palette, referenced by the fire descriptors in the tail. Grafting 4/5
    /// (as DC2-CROSS-CHAR-WEAPON-MODEL-SWAP.md §5 originally prescribed) swaps a weapon's explosion
    /// texture for the donor character's — live-witnessed as corrupted Missile Pod explosion
    /// graphics, 2026-07-20. They must stay with the weapon owner.</para></summary>
    private static readonly int[] SkinEntries = { 2, 3 };

    /// <summary>Offset just past the last non-filler byte of the model blob, i.e. where this file's
    /// geometry actually ends. <c>&lt;= <see cref="HeadEnd"/></c> means the head splice is clean.</summary>
    public static int GeometryEnd(ReadOnlySpan<byte> package)
    {
        var blob = Blob(package, out _, out _);
        int end = SlackEnd;
        while (end > 0x0C && blob[end - 1] == Filler) end--;
        return end;
    }

    /// <summary>Build the grafted package: <paramref name="ownerPackage"/> (the weapon's own file) with
    /// its blob geometry head and skin entries replaced by <paramref name="geometryPackage"/>'s (the
    /// target character's body).</summary>
    public static byte[] Build(ReadOnlySpan<byte> ownerPackage, ReadOnlySpan<byte> geometryPackage)
    {
        var ownerBlob = Blob(ownerPackage, out var ownerPkg, out int blobIndex);
        var geomBlob = Blob(geometryPackage, out var geomPkg, out _);

        if (ownerBlob.Length < SlackEnd || geomBlob.Length < HeadEnd)
            throw new InvalidDataException("WEP_P model blob is too small to carry a geometry head.");

        // head from the target character, everything from HeadEnd on from the weapon owner
        var spliced = ownerBlob.ToArray();
        geomBlob.AsSpan(0, HeadEnd).CopyTo(spliced.AsSpan());

        // a larger owner model spills past the boundary; refill its orphaned remainder with filler
        int ownerEnd = SlackEnd;
        while (ownerEnd > 0x0C && ownerBlob[ownerEnd - 1] == Filler) ownerEnd--;
        for (int i = HeadEnd; i < ownerEnd; i++) spliced[i] = Filler;

        var payload = Lzss.Compress(spliced);
        if (!Lzss.Decompress(payload).AsSpan().SequenceEqual(spliced))
            throw new InvalidDataException("LZSS round-trip failed for the grafted WEP_P blob.");

        var result = PackageRepacker.ReplaceEntryDc2(ownerPackage, blobIndex, payload);
        foreach (var i in SkinEntries)
        {
            var e = geomPkg.Entries[i];
            if (ownerPkg.Entries[i].Type != e.Type)
                throw new InvalidDataException("WEP_P entry layout mismatch between the two packages.");
            result = PackageRepacker.ReplaceEntryDc2(
                result, i, geometryPackage.Slice(e.PayloadOffset, (int)e.DeclaredSize));
        }
        return result;
    }

    private static byte[] Blob(ReadOnlySpan<byte> package, out GianPackage pkg, out int index)
    {
        pkg = GianPackage.TryParse(package)
              ?? throw new InvalidDataException("not a recognized Gian package");
        index = pkg.Entries.Count - 1;   // the model blob is the last entry in every WEP_P
        var e = pkg.Entries[index];
        if (e.Type is not (GianEntryType.Lzss0 or GianEntryType.Lzss2))
            throw new InvalidDataException("WEP_P package does not end with a compressed model blob.");
        return Lzss.Decompress(package.Slice(e.PayloadOffset, (int)e.DeclaredSize));
    }
}
