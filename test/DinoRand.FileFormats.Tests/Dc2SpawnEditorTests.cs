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

    // ---- synthetic package (always runs — no game files needed) ---------------------------------

    private static byte[] SyntheticPackage() => SyntheticRoom.Dc2Room(0); // 300-byte SCD blob

    [Fact]
    public void WriteOperand_Synthetic_RoundTripsThroughRepack()
    {
        var package = SyntheticPackage();
        var edited = Dc2SpawnEditor.WriteOperand(package, 100, -12345);

        Assert.Equal(-12345, Dc2SpawnEditor.ReadOperandFromPackage(edited, 100));
        // only the 2 operand bytes changed
        var before = Dc2DoorEditor.DecompressScdBlob(package);
        var after = Dc2DoorEditor.DecompressScdBlob(edited);
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
            if (i != 100 && i != 101)
                Assert.Equal(before[i], after[i]);
    }

    [Fact]
    public void WriteByte_Synthetic_ChangesExactlyOneByte()
    {
        var package = SyntheticPackage();
        var edited = Dc2SpawnEditor.WriteByte(package, 55, 0xEE);

        Assert.Equal(0xEE, Dc2SpawnEditor.ReadByteFromPackage(edited, 55));
        var before = Dc2DoorEditor.DecompressScdBlob(package);
        var after = Dc2DoorEditor.DecompressScdBlob(edited);
        for (int i = 0; i < before.Length; i++)
            if (i != 55)
                Assert.Equal(before[i], after[i]);
    }

    [Fact]
    public void OperandBounds_AreExact()
    {
        var package = SyntheticPackage();
        int len = Dc2DoorEditor.DecompressScdBlob(package).Length; // 300

        // word: off+2 <= len is the boundary
        Assert.Equal(Dc2SpawnEditor.ReadOperandFromPackage(package, len - 2),
                     Dc2SpawnEditor.ReadOperandFromPackage(package, len - 2)); // len-2 legal
        _ = Dc2SpawnEditor.WriteOperand(package, len - 2, 7);
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2SpawnEditor.WriteOperand(package, len - 1, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2SpawnEditor.WriteOperand(package, -1, 7));

        // byte: off < len is the boundary
        _ = Dc2SpawnEditor.WriteByte(package, len - 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2SpawnEditor.WriteByte(package, len, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2SpawnEditor.ReadByteFromPackage(package, len));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2SpawnEditor.ReadByteFromPackage(package, -1));
    }

    [Fact]
    public void ApplyEdits_BatchesWordsAndBytes_InOneRepack()
    {
        var package = SyntheticPackage();
        var edited = Dc2SpawnEditor.ApplyEdits(package,
            wordEdits: new[] { (10, (short)1234), (20, (short)-1) },
            byteEdits: new[] { (30, (byte)0x7F) });

        Assert.Equal(1234, Dc2SpawnEditor.ReadOperandFromPackage(edited, 10));
        Assert.Equal(-1, Dc2SpawnEditor.ReadOperandFromPackage(edited, 20));
        Assert.Equal(0x7F, Dc2SpawnEditor.ReadByteFromPackage(edited, 30));
    }

    [Fact]
    public void ApplyEdits_RejectsAnyOutOfRangeEdit()
    {
        var package = SyntheticPackage();
        int len = Dc2DoorEditor.DecompressScdBlob(package).Length;

        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2SpawnEditor.ApplyEdits(package,
            new[] { (len - 1, (short)0) }, Array.Empty<(int, byte)>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Dc2SpawnEditor.ApplyEdits(package,
            Array.Empty<(int, short)>(), new[] { (len, (byte)0) }));
    }

    [Fact]
    public void Editors_RejectNonGianAndBlobLessPackages()
    {
        // Dc2ScdBlob's two refusal branches, via the public editor surface:
        // not a Gian package at all…
        Assert.Throws<InvalidDataException>(() => Dc2SpawnEditor.WriteOperand(new byte[64], 0, 1));
        // …and a Gian package with no LZSS0 SCD blob entry (a DC1-shaped room).
        var noBlob = SyntheticRoom.Dc1Room(
            Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(), Array.Empty<SyntheticRoom.Enemy>());
        Assert.Throws<InvalidDataException>(() => Dc2SpawnEditor.WriteOperand(noBlob, 0, 1));
        Assert.Throws<InvalidDataException>(() => Dc2DoorEditor.DecompressScdBlob(noBlob));
    }
}
