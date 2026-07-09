using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Dc2WeaponCatalogFlagsPatch — repairs the DC2 item-catalog flags (0x704260 → file 0xE9260, +0xA)
/// to their PSX source-of-truth values (docs/reference/dc2/weapon/DC2-WEAPON-CATALOG-SOURCE-OF-TRUTH.md,
/// data/dc2/weapon-catalog.json). Synthetic exe: real build length with the catalog region laid in.
/// </summary>
public class Dc2WeaponCatalogFlagsPatchTests
{
    // PSX-truth flags (SLUS_012.79 @ 0x80019770) — identical to Dino2.exe pristine 0x704260.
    private static readonly (byte Id, ushort Flags)[] Truth =
    {
        (0x00, 0x004b), (0x01, 0x004a), (0x02, 0x0049), (0x03, 0x002a), (0x04, 0x004a), (0x05, 0x002b),
        (0x06, 0x0029), (0x07, 0x0049), (0x08, 0x0029), (0x09, 0x004a), (0x0a, 0x00cc), (0x0b, 0x004c),
        (0x10, 0x0053), (0x11, 0x00d2), (0x12, 0x00d1), (0x13, 0x0053), (0x14, 0x0053), (0x15, 0x00d4),
    };

    // The 7 ids the external weapon-shuffle tool corrupted on the user's live install (SoT §3).
    private static readonly (byte Id, ushort Live)[] LiveCorruption =
    {
        (0x03, 0x0053), (0x04, 0x004c), (0x07, 0x0053), (0x09, 0x002a),
        (0x0b, 0x0053), (0x13, 0x004a), (0x14, 0x004a),
    };

    private const int CatalogFo = 0xE9260;
    private static int FlagsFo(byte id) => CatalogFo + id * 12 + 0xA;
    private static int UFo(byte id) => CatalogFo + id * 12 + 8;

    /// <summary>Pristine synthetic exe: catalog flags = PSX-truth; icon U/V bytes set to sentinels so
    /// tests can prove the repair never touches them.</summary>
    private static byte[] MakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        foreach (var (id, flags) in Truth)
        {
            int f = FlagsFo(id);
            exe[f] = (byte)flags; exe[f + 1] = (byte)(flags >> 8);
            exe[UFo(id)] = 0xAB;      // iconU sentinel
            exe[UFo(id) + 1] = 0xCD;  // iconV sentinel
        }
        return exe;
    }

    private static ushort Read(byte[] exe, byte id) => (ushort)(exe[FlagsFo(id)] | (exe[FlagsFo(id) + 1] << 8));

    [Fact]
    public void PristineExe_IsPristine_ApplyIsNoOp()
    {
        var exe = MakeExe();
        var before = (byte[])exe.Clone();
        Assert.True(Dc2WeaponCatalogFlagsPatch.IsPristine(exe));
        Assert.Equal(0, Dc2WeaponCatalogFlagsPatch.Apply(exe));
        Assert.Equal(before, exe); // byte-identical on a clean exe
    }

    [Fact]
    public void Apply_RestoresCorruptedAntitankWideBit()
    {
        // The reported bug: id 0x09 flags flipped 0x4A -> 0x2A sets the wide-icon bit (0x20), so the
        // antitank icon blits 128px and overdraws the neighbour tile. Repair clears the wide bit.
        var exe = MakeExe();
        exe[FlagsFo(0x09)] = 0x2A; exe[FlagsFo(0x09) + 1] = 0x00;
        Assert.False(Dc2WeaponCatalogFlagsPatch.IsPristine(exe));
        Assert.Equal(Dc2WeaponCatalogFlagsPatch.WideIconBit, (ushort)(Read(exe, 0x09) & Dc2WeaponCatalogFlagsPatch.WideIconBit));

        int repaired = Dc2WeaponCatalogFlagsPatch.Apply(exe);

        Assert.True(repaired >= 1);
        Assert.Equal(0x004A, Read(exe, 0x09));
        Assert.Equal(0, Read(exe, 0x09) & Dc2WeaponCatalogFlagsPatch.WideIconBit); // narrow again
        Assert.True(Dc2WeaponCatalogFlagsPatch.IsPristine(exe));
    }

    [Fact]
    public void Apply_RestoresAllSevenLiveCorruptedIds()
    {
        var exe = MakeExe();
        foreach (var (id, live) in LiveCorruption)
        {
            exe[FlagsFo(id)] = (byte)live; exe[FlagsFo(id) + 1] = (byte)(live >> 8);
        }
        Assert.Equal(LiveCorruption.Select(c => c.Id).OrderBy(x => x),
                     Dc2WeaponCatalogFlagsPatch.CorruptedIds(exe).OrderBy(x => x));

        Assert.Equal(LiveCorruption.Length, Dc2WeaponCatalogFlagsPatch.Apply(exe));

        foreach (var (id, truth) in Truth)
            Assert.Equal(truth, Read(exe, id));
        Assert.Empty(Dc2WeaponCatalogFlagsPatch.CorruptedIds(exe));
    }

    [Fact]
    public void Apply_TouchesOnlyFlagBytes_NeverIconUV()
    {
        // Boundary: the icon (U,V) table is already pristine and must never be rewritten by the flag
        // repair — only the 2 flag bytes per id may change.
        var exe = MakeExe();
        exe[FlagsFo(0x09)] = 0x2A; // corrupt antitank flags only
        Dc2WeaponCatalogFlagsPatch.Apply(exe);
        foreach (var (id, _) in Truth)
        {
            Assert.Equal(0xAB, exe[UFo(id)]);     // iconU untouched
            Assert.Equal(0xCD, exe[UFo(id) + 1]); // iconV untouched
        }
    }

    [Fact]
    public void ReadFlags_IsLittleEndianWord()
    {
        var exe = MakeExe();
        Assert.Equal(0x004A, Dc2WeaponCatalogFlagsPatch.ReadFlags(exe, 0x09));
        Assert.Equal(0x00CC, Dc2WeaponCatalogFlagsPatch.ReadFlags(exe, 0x0A));
    }

    [Fact]
    public void Validate_RefusesWrongLength()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Dc2WeaponCatalogFlagsPatch.Validate(new byte[100]));
        Assert.Contains("unexpected length", ex.Message);
    }
}
