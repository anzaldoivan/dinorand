using DinoRand.FileFormats.Stage.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// Enemy-spawn literal-operand edit on a real DC2 room (rebirth <c>ST102.DAT</c> — the jungle raptor
/// room). Operates on an in-memory COPY; the game file is never written. Proves the T10 write
/// primitive end-to-end: read → rewrite a spawn operand → recompress → repack → re-parse →
/// re-decompress. Offsets are the byte-cited values pinned by <c>tools/dc2_re/edit_spawn.py</c> into
/// <c>data/dc2/spawn-graph.json</c> (ST102 routine-2 = the three literal-TYPE Velociraptor spawns).
///
/// <para>Resolves <c>ST102.DAT</c> from the canonical rebirth install; absent → no-ops (the "no game
/// files → skip" convention).</para>
/// </summary>
public class Dc2SpawnEditorTests
{
    // rebirth ST102, routine-2 spawn-0 (TYPE 0x02 = Velociraptor): blob offsets of its literal operands.
    private const int Slot0Off = 0xBFFA; // SLOT  = 3
    private const int Type0Off = 0xBFFE; // TYPE  = 2  (Velociraptor)
    private const int PosX0Off = 0xC002; // posX  = 11544
    private const int PosY0Off = 0xC006; // posY  = 0
    // routine-2 spawn-2 carries a negative posY (proves signed handling).
    private const int PosY2Off = 0xC066; // posY  = -2000

    // Pristine-aware: the live file may be a randomizer install (see PristineRooms).
    private static byte[]? LoadSt102() => PristineRooms.TryLoad("ST102.DAT");

    [Fact]
    public void ReadOperand_St102Raptor_DecodesKnownValues()
    {
        var package = LoadSt102();
        if (package is null) return;

        var blob = Dc2DoorEditor.DecompressScdBlob(package); // shared SCD blob accessor
        Assert.Equal(3, Dc2SpawnEditor.ReadOperand(blob, Slot0Off));
        Assert.Equal(2, Dc2SpawnEditor.ReadOperand(blob, Type0Off));      // Velociraptor
        Assert.Equal(11544, Dc2SpawnEditor.ReadOperand(blob, PosX0Off));
        Assert.Equal(0, Dc2SpawnEditor.ReadOperand(blob, PosY0Off));
        Assert.Equal(-2000, Dc2SpawnEditor.ReadOperand(blob, PosY2Off)); // signed
    }

    [Fact]
    public void WriteOperand_MovesRaptorX_ChangesOnlyTheTwoBytes()
    {
        var package = LoadSt102();
        if (package is null) return;

        var originalBlob = Dc2DoorEditor.DecompressScdBlob(package);
        const short newX = 9000;

        var edited = Dc2SpawnEditor.WriteOperand(package, PosX0Off, newX);

        // 1. input not mutated.
        Assert.Equal(11544, Dc2SpawnEditor.ReadOperand(originalBlob, PosX0Off));

        // 2. edited package re-parses to a same-length blob with the new value.
        var editedBlob = Dc2DoorEditor.DecompressScdBlob(edited);
        Assert.Equal(originalBlob.Length, editedBlob.Length);
        Assert.Equal(newX, Dc2SpawnEditor.ReadOperand(editedBlob, PosX0Off));

        // 3. only the 2 operand bytes changed.
        for (int i = 0; i < originalBlob.Length; i++)
            if (i != PosX0Off && i != PosX0Off + 1)
                Assert.Equal(originalBlob[i], editedBlob[i]);
    }

    [Fact]
    public void WriteOperand_TypeSwap_RaptorToGiganotosaurus()
    {
        var package = LoadSt102();
        if (package is null) return;

        // TYPE 0x02 (Velociraptor) -> 0x06 (Giganotosaurus). Exercises the species lever; whether the
        // donor model is resident is the randomizer's concern, not the editor's.
        var edited = Dc2SpawnEditor.WriteOperand(package, Type0Off, 0x06);

        var editedBlob = Dc2DoorEditor.DecompressScdBlob(edited);
        Assert.Equal(6, Dc2SpawnEditor.ReadOperand(editedBlob, Type0Off));
        // The other two raptor spawns' TYPE bytes are untouched (still 2).
        Assert.Equal(2, Dc2SpawnEditor.ReadOperand(editedBlob, 0xC02E)); // routine-2 spawn-1 TYPE
        Assert.Equal(2, Dc2SpawnEditor.ReadOperand(editedBlob, 0xC05E)); // routine-2 spawn-2 TYPE
    }

    [Fact]
    public void WriteOperand_RoundTripsNegativeValue()
    {
        var package = LoadSt102();
        if (package is null) return;

        var edited = Dc2SpawnEditor.WriteOperand(package, PosY2Off, -1500);
        var editedBlob = Dc2DoorEditor.DecompressScdBlob(edited);
        Assert.Equal(-1500, Dc2SpawnEditor.ReadOperand(editedBlob, PosY2Off));
    }

    [Fact]
    public void ReadOperand_RejectsOutOfRangeOffset()
    {
        var blob = new byte[10];
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2SpawnEditor.ReadOperand(blob, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2SpawnEditor.ReadOperand(blob, -1));
    }

}
