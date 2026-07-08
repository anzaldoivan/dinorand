using DinoRand.FileFormats.Exe;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Dc2StartWeaponAppendPatch on a synthetic exe image: the real build's length with the stolen
/// <c>mov esi,3</c> (BE 03 00 00 00) laid in at the hook offset; the cave slack starts zeroed.
/// The patch keeps the default shotgun/handgun and appends the chosen weapon as an extra record so
/// the player can still switch to a one-handed main + sub-weapon (fixes the two-handed soft-lock).
/// </summary>
public class Dc2StartWeaponAppendPatchTests
{
    private const int Hook = Dc2StartWeaponAppendPatch.HookOffset;
    private const int Cave = Dc2StartWeaponAppendPatch.CaveOffset;
    private const byte DCanon = Dc2StartingLoadoutPatch.DylanCanonicalId;   // 0x01
    private const byte RCanon = Dc2StartingLoadoutPatch.ReginaCanonicalId;  // 0x02

    private static byte[] MakeExe()
    {
        var exe = new byte[Dc2WpGatePatch.ExpectedLength];
        new byte[] { 0xBE, 0x03, 0x00, 0x00, 0x00 }.CopyTo(exe, Hook); // mov esi,3 (stolen site)
        return exe;
    }

    // A 12-byte-record write block: mov byte[eax],1 ; mov byte[eax+1],id ; mov word[eax+8],cnt ;
    // mov word[eax+0xA],cnt ; add eax,0xC. (Slots are pre-zeroed in-game, so pad bytes aren't written.)
    private static byte[] Record(byte id, ushort cnt)
    {
        byte lo = (byte)cnt, hi = (byte)(cnt >> 8);
        return new byte[]
        {
            0xC6, 0x00, 0x01,
            0xC6, 0x40, 0x01, id,
            0x66, 0xC7, 0x40, 0x08, lo, hi,
            0x66, 0xC7, 0x40, 0x0A, lo, hi,
            0x83, 0xC0, 0x0C,
        };
    }

    [Fact]
    public void Apply_BothWeapons_InstallsHook_AndCaveWithBothRecords()
    {
        var exe = MakeExe();
        Dc2StartWeaponAppendPatch.Apply(exe, 0x05, 0x06); // flamethrower 300 / id06 1000

        Assert.True(Dc2StartWeaponAppendPatch.IsApplied(exe));
        // hook = jmp to the cave
        Assert.Equal(0xE9, exe[Hook]);
        int hookRel = BitConverter.ToInt32(exe, Hook + 1);
        Assert.Equal(Cave, Hook + 5 + hookRel);

        // cave: mov eax,edi ; Dylan record ; Regina record ; mov esi,3 ; jmp 0x496A83
        Assert.Equal(new byte[] { 0x89, 0xF8 }, exe.Skip(Cave).Take(2));
        Assert.Equal(Record(0x05, 300), exe.Skip(Cave + 2).Take(22));
        Assert.Equal(Record(0x06, 1000), exe.Skip(Cave + 2 + 22).Take(22));
        int tail = Cave + 2 + 44;
        Assert.Equal(new byte[] { 0xBE, 0x03, 0x00, 0x00, 0x00 }, exe.Skip(tail).Take(5));
        Assert.Equal(0xE9, exe[tail + 5]);
        int jmpRel = BitConverter.ToInt32(exe, tail + 6);
        Assert.Equal(0x96A83, (tail + 5) + 5 + jmpRel); // returns to the instruction after mov esi,3
    }

    [Fact]
    public void Apply_OnlyDylan_AppendsSingleRecord()
    {
        var exe = MakeExe();
        Dc2StartWeaponAppendPatch.Apply(exe, 0x03, RCanon); // Regina canonical → not appended

        Assert.Equal(new byte[] { 0x89, 0xF8 }, exe.Skip(Cave).Take(2));
        Assert.Equal(Record(0x03, 20), exe.Skip(Cave + 2).Take(22)); // rocket launcher = 20
        // mov esi,3 immediately follows the single record — no Regina block
        Assert.Equal(new byte[] { 0xBE, 0x03, 0x00, 0x00, 0x00 }, exe.Skip(Cave + 2 + 22).Take(5));
    }

    [Fact]
    public void Apply_BothCanonical_LeavesPristine()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        Dc2StartWeaponAppendPatch.Apply(exe, DCanon, RCanon);
        Assert.False(Dc2StartWeaponAppendPatch.IsApplied(exe));
        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void Restore_RoundTripsToPristine()
    {
        var exe = MakeExe();
        var pristine = (byte[])exe.Clone();
        Dc2StartWeaponAppendPatch.Apply(exe, 0x05, 0x06);
        Dc2StartWeaponAppendPatch.Restore(exe);
        Assert.Equal(pristine, exe);
    }

    [Fact]
    public void Apply_IsAbsolute_NotCompounding()
    {
        var exe = MakeExe();
        Dc2StartWeaponAppendPatch.Apply(exe, 0x05, 0x06);        // two records
        Dc2StartWeaponAppendPatch.Apply(exe, 0x09, RCanon);     // re-apply: only Dylan 0x09 now

        Assert.Equal(Record(0x09, 50), exe.Skip(Cave + 2).Take(22)); // antitank rifle = 50
        Assert.Equal(new byte[] { 0xBE, 0x03, 0x00, 0x00, 0x00 }, exe.Skip(Cave + 2 + 22).Take(5));
        // the old second (Regina) block is gone — cave region cleared before the rewrite
        int tail = Cave + 2 + 22 + 5 + 5; // after mov esi,3 + jmp
        Assert.All(exe.Skip(tail).Take(Cave + 64 - tail), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Apply_RefusesForeignHook_AndWrongLength()
    {
        var exe = MakeExe();
        exe[Hook] = 0xCC; // not the stolen mov esi,3 nor our jmp
        Assert.Throws<InvalidOperationException>(() => Dc2StartWeaponAppendPatch.Apply(exe, 0x05, 0x06));
        Assert.Throws<InvalidOperationException>(() => Dc2StartWeaponAppendPatch.Apply(new byte[100], 0x05, 0x06));
    }
}
