using System.Buffers.Binary;
using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using DinoRand.FileFormats.Stage.Dc2;
using Xunit;

namespace DinoRand.FileFormats.Tests;

public class Dc2ItemEditorTests
{
    private const uint BaseVa = 0x005e0000;
    private const string Room = "ST777";

    [Fact]
    public void ApplyEdits_ValidatedMixedRecordBatch_ChangesOnlyItemWords()
    {
        var (package, op35, op2c) = MakeRoom();
        var originalPackage = package.ToArray();
        var originalBlob = Decompress(package);

        var edited = Dc2ItemEditor.ApplyEdits(package, Room, new[]
        {
            new Dc2ItemEditor.ItemEdit(op35, 0x2d),
            new Dc2ItemEditor.ItemEdit(op2c, 0x1d),
        });

        Assert.Equal(originalPackage, package);
        var editedBlob = Decompress(edited);
        Assert.Equal(originalBlob.Length, editedBlob.Length);
        Assert.Equal(0x2d, BinaryPrimitives.ReadInt16LittleEndian(editedBlob.AsSpan(op35.ItemOperand.ValueOffset, 2)));
        Assert.Equal(0x1d, BinaryPrimitives.ReadInt16LittleEndian(editedBlob.AsSpan(op2c.ItemOperand.ValueOffset, 2)));

        var changed = Enumerable.Range(0, originalBlob.Length)
            .Where(i => originalBlob[i] != editedBlob[i])
            .ToArray();
        Assert.Equal(new[]
        {
            op35.ItemOperand.ValueOffset,
            op2c.ItemOperand.ValueOffset,
        }, changed);
    }

    [Fact]
    public void ApplyEdits_RejectsWrongOrMixedRoomIdentity()
    {
        var (package, op35, op2c) = MakeRoom();
        Assert.Throws<InvalidOperationException>(() =>
            Dc2ItemEditor.ApplyEdits(package, "ST778", new[] { new Dc2ItemEditor.ItemEdit(op35, 0x2d) }));
        Assert.Throws<InvalidOperationException>(() =>
            Dc2ItemEditor.ApplyEdits(package, Room, new[]
            {
                new Dc2ItemEditor.ItemEdit(op35, 0x2d),
                new Dc2ItemEditor.ItemEdit(op2c with { RoomId = "ST778" }, 0x1d),
            }));
    }

    [Fact]
    public void ApplyEdits_RejectsDuplicateIdentityAndOverlappingOffsets()
    {
        var (package, op35, op2c) = MakeRoom();
        Assert.Throws<InvalidOperationException>(() =>
            Dc2ItemEditor.ApplyEdits(package, Room, new[]
            {
                new Dc2ItemEditor.ItemEdit(op35, 0x2d),
                new Dc2ItemEditor.ItemEdit(op2c with { SourceId = op35.SourceId }, 0x1d),
            }));
        Assert.Throws<InvalidOperationException>(() =>
            Dc2ItemEditor.ApplyEdits(package, Room, new[]
            {
                new Dc2ItemEditor.ItemEdit(op35, 0x2d),
                new Dc2ItemEditor.ItemEdit(op2c with
                {
                    ItemOperand = op2c.ItemOperand with
                    {
                        PushOffset = op35.ItemOperand.PushOffset,
                        ValueOffset = op35.ItemOperand.ValueOffset,
                    },
                }, 0x1d),
            }));
    }

    [Fact]
    public void ApplyEdits_RejectsWrongRoutineAndOpcodeIdentity()
    {
        var (package, op35, _) = MakeRoom();
        Reject(package, op35 with { RoutineOrdinal = 1 }, 0x2d);
        Reject(package, op35 with { VmDirectoryIndex = 1 }, 0x2d);
        Reject(package, op35 with { VmDirectoryIndices = new[] { 0, 1 } }, 0x2d);
        Reject(package, op35 with { RoutineStart = op35.RoutineStart + 4 }, 0x2d);
        Reject(package, op35 with { OpOffset = op35.OpOffset - 4 }, 0x2d);
        Reject(package, op35 with { Opcode = 0x2c }, 0x2d);
        Reject(package, op35 with { RecordClass = Dc2ItemEditor.ItemRecordClass.Op2cGive }, 0x2d);
        Reject(package, op35 with { SourceId = "ST777:r0:op35@0xdead" }, 0x2d);
    }

    [Fact]
    public void ApplyEdits_RejectsUnpinnedOrNonLiteralOperands()
    {
        var (package, op35, op2c) = MakeRoom();
        Reject(package, op35 with
        {
            ItemOperand = op35.ItemOperand with { Mode = 1 },
        }, 0x2d);
        Reject(package, op35 with
        {
            ItemOperand = op35.ItemOperand with { BlockOffset = 12 },
        }, 0x2d);
        Reject(package, op35 with
        {
            ItemOperand = op35.ItemOperand with { ExpectedValue = 0x2d },
        }, 0x2d);
        Reject(package, op35 with
        {
            P3Operand = op35.P3Operand with { ExpectedValue = 2 },
        }, 0x2d);
        Reject(package, op35 with
        {
            CleanupOperand = op35.CleanupOperand with { PushOffset = op35.CleanupOperand.PushOffset - 4 },
        }, 0x2d);
        Reject(package, op2c with
        {
            KindOperand = op2c.KindOperand! with { ExpectedValue = 2 },
        }, 0x1d);
    }

    [Fact]
    public void ApplyEdits_RejectsUnsupportedOrCrossClassCatalogIds()
    {
        var (package, op35, op2c) = MakeRoom();
        Reject(package, op35, 0x1a); // generic key -> health
        Reject(package, op35, 0x01); // unsupported weapon class
        Reject(package, op35, 0x2f); // isolated special key
        Reject(package, op2c, 0x21); // health -> key
        Reject(package, op2c, 0x1e); // Easy remap target
        Reject(package, op2c with
        {
            ExpectedItemId = 0x1e,
            ItemOperand = op2c.ItemOperand with { ExpectedValue = 0x1e },
        }, 0x1d);
    }

    [Fact]
    public void ApplyEdits_Special2fClass_AllowsOnlyIdentityPreservingWrite()
    {
        var (package, op35, _) = MakeRoom();
        var special = op35 with
        {
            ExpectedItemId = 0x2f,
            RewriteClass = Dc2ItemEditor.ItemRewriteClass.SpecialKey2f,
            ItemOperand = op35.ItemOperand with { ExpectedValue = 0x2f },
        };
        var blob = Decompress(package);
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(special.ItemOperand.ValueOffset, 2), 0x2f);
        package = Repack(package, blob);

        var edited = Dc2ItemEditor.ApplyEdits(package, Room,
            new[] { new Dc2ItemEditor.ItemEdit(special, 0x2f) });
        Assert.Equal(Decompress(package), Decompress(edited));
        Reject(package, special, 0x2e);
    }

    private static void Reject(byte[] package, Dc2ItemEditor.ItemSiteSpec site, int newItemId)
        => Assert.Throws<InvalidOperationException>(() =>
            Dc2ItemEditor.ApplyEdits(package, Room,
                new[] { new Dc2ItemEditor.ItemEdit(site, newItemId) }));

    private static (byte[] Package, Dc2ItemEditor.ItemSiteSpec Op35, Dc2ItemEditor.ItemSiteSpec Op2c) MakeRoom()
    {
        var blob = new byte[0x180];
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x14, 4), BaseVa + 0x80);
        const int opBase = 0x9c;
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(opBase, 4), 0x08);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(opBase + 4, 4), 0x80);

        const int r0 = 0xa4;
        var op35Pins = WritePushRun(blob, r0, 17, new Dictionary<int, short>
        {
            [64] = 6, [20] = 10, [16] = 0x2e, [12] = 1, [4] = 0,
        });
        int op35Off = r0 + 17 * 4;
        blob[op35Off] = 0x35;
        blob[op35Off + 1] = 0;
        blob[op35Off + 2] = 0x04;

        const int r1 = 0x11c;
        var op2cPins = WritePushRun(blob, r1, 5, new Dictionary<int, short>
        {
            [16] = 1, [12] = 0x1a, [8] = 7, [4] = 22, [0] = 3,
        });
        int op2cOff = r1 + 5 * 4;
        blob[op2cOff] = 0x2c;
        blob[op2cOff + 1] = 0;
        blob[op2cOff + 2] = 0x04;

        var op35 = new Dc2ItemEditor.ItemSiteSpec(
            $"{Room}:r0:op35@0x{op35Off:x}", Room, 0, 0, new[] { 0 }, r0,
            op35Off, 0x35, Dc2ItemEditor.ItemRecordClass.Op35Take, 0x2e,
            Dc2ItemEditor.ItemRewriteClass.GenericKey,
            op35Pins[16], op35Pins[12], op35Pins[20], op35Pins[4],
            KindOperand: null, SlotOperand: op35Pins[64]);
        var op2c = new Dc2ItemEditor.ItemSiteSpec(
            $"{Room}:r1:op2c@0x{op2cOff:x}", Room, 1, 1, new[] { 1 }, r1,
            op2cOff, 0x2c, Dc2ItemEditor.ItemRecordClass.Op2cGive, 0x1a,
            Dc2ItemEditor.ItemRewriteClass.Health,
            op2cPins[12], op2cPins[8], op2cPins[4], op2cPins[0],
            KindOperand: op2cPins[16], SlotOperand: null);

        return (Package(blob), op35, op2c);
    }

    private static Dictionary<int, Dc2ItemEditor.OperandPin> WritePushRun(
        byte[] blob, int start, int count, IReadOnlyDictionary<int, short> values)
    {
        var result = new Dictionary<int, Dc2ItemEditor.OperandPin>();
        for (int i = 0; i < count; i++)
        {
            int push = start + i * 4;
            int blockOffset = (count - 1 - i) * 4;
            short value = values.GetValueOrDefault(blockOffset);
            blob[push] = 0x05;
            blob[push + 1] = 0;
            BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(push + 2, 2), value);
            result[blockOffset] = new Dc2ItemEditor.OperandPin(blockOffset, 0, value, push, push + 2);
        }
        return result;
    }

    private static byte[] Package(byte[] blob)
        => SyntheticRoom.Package(GianPackage.Dc2EntrySize,
            (GianEntryType.Lzss0, Lzss.Compress(blob)),
            (GianEntryType.Data, new byte[12]));

    private static byte[] Decompress(byte[] package)
    {
        var parsed = GianPackage.TryParse(package)!;
        var entry = parsed.Entries.Single(e => e.Type == GianEntryType.Lzss0);
        return Lzss.Decompress(package.AsSpan(entry.PayloadOffset, (int)entry.DeclaredSize));
    }

    private static byte[] Repack(byte[] package, byte[] blob)
    {
        var parsed = GianPackage.TryParse(package)!;
        int index = parsed.Entries.ToList().FindIndex(e => e.Type == GianEntryType.Lzss0);
        return PackageRepacker.ReplaceEntryDc2(package, index, Lzss.Compress(blob));
    }
}
