using System.Buffers.Binary;
using DinoRand.FileFormats.Compression;
using DinoRand.FileFormats.Stage;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The reusable enemy-import budget/fit helper (<see cref="EnemyImportBudget"/>): the single check the
/// Theri swap and the novel-species add path share for "does this donor fit this room?" across BOTH the
/// RDT-size constraint (<see cref="SpeciesImporter.ResidentPoolFloor"/> /
/// <see cref="SpeciesImporter.EngineRoomRdtCeiling"/>, with the entangled-donor clip-strip) and the VRAM
/// constraint (<see cref="TextureImporter.PickFreeRegion"/>). Synthetic tests pin the boundaries; the
/// real swap behaviour is covered by the gated <c>SpeciesImporterTests</c>.
/// </summary>
public class EnemyImportBudgetTests
{
    private const uint B = SpeciesImporter.PsxBase; // 0x80100000

    private static void PutU32(byte[] buf, int off, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), v);

    /// <summary>A synthetic Theri-like closure (same shape as <c>SpeciesImporterTests.SyntheticTheriLikeClosure</c>):
    /// header tail-pointer @0x00 caps the resource at 0x1F0; model [0x40,0x60); a 3-entry clip table @0x60 over
    /// clips 0:[0x80)(0x20) 1:[0xA0)(0x80) 2:[0x120)(0xD0 to 0x1F0). Full closure [0x40,0x1F0) = 0x1B0 (432) B.</summary>
    private static byte[] SyntheticClosure()
    {
        var rdt = new byte[0x200];
        PutU32(rdt, 0x00, B + 0x1F0);   // header dword -> RDT tail (resource ceiling = 0x1F0)
        PutU32(rdt, 0x44, B + 0x50);    // model [0x40,0x60): one internal ptr (closed)
        PutU32(rdt, 0x60, B + 0x80);    // clip table @0x60: 3 entries
        PutU32(rdt, 0x64, B + 0xA0);
        PutU32(rdt, 0x68, B + 0x120);
        PutU32(rdt, 0x80, B + 0x90);    // each clip: clip+0 -> clip+0x10
        PutU32(rdt, 0xA0, B + 0xB0);
        PutU32(rdt, 0x120, B + 0x130);
        return rdt;
    }

    // ---- RDT budget (entangled / closure donor: the Theri path) --------------------------------

    [Fact]
    public void Closure_FitsUnstripped_WhenDonorUnderBudget()
    {
        var rdt = SyntheticClosure();
        var fit = EnemyImportBudget.Evaluate(rdt, B + 0x40, B + 0x60,
            targetRdtLength: 0x100, maxDonorBytes: 0x1000, maxClipDrops: 8,
            protectedClips: Array.Empty<int>(),
            targetPackage: ReadOnlySpan<byte>.Empty, donorTexture: null, out var prepared);

        Assert.True(fit.Fits);
        Assert.Equal(0, fit.DroppedClips);
        Assert.Equal(ImportFitConstraint.None, fit.Limiting);
        Assert.NotNull(prepared);
        Assert.Equal(0x1B0, prepared!.Bytes.Length);                 // un-stripped full closure
        Assert.Equal(0x100 + 0x1B0, fit.ResultRdtLength);            // append at align4(0x100) + blob
    }

    [Fact]
    public void Closure_FitsOnlyAfterStrip_DropsLargestClip()
    {
        var rdt = SyntheticClosure();
        // Budget 224 (0xE0): must free 432-224=208 = exactly clip 2 (0xD0). One drop, within the cap.
        var fit = EnemyImportBudget.Evaluate(rdt, B + 0x40, B + 0x60,
            targetRdtLength: 0x100, maxDonorBytes: 224, maxClipDrops: 8,
            protectedClips: Array.Empty<int>(),
            targetPackage: ReadOnlySpan<byte>.Empty, donorTexture: null, out var prepared);

        Assert.True(fit.Fits);
        Assert.Equal(1, fit.DroppedClips);
        Assert.Equal(0xD0, fit.DroppedBytes);
        Assert.Equal(ImportFitConstraint.ClipStrip, fit.Limiting);
        Assert.NotNull(prepared);
        Assert.Equal(224, prepared!.Bytes.Length);
        Assert.Equal(0x100 + 224, fit.ResultRdtLength);
    }

    [Fact]
    public void Closure_Refused_WhenStripExceedsDropCap()
    {
        var rdt = SyntheticClosure();
        // Same 224 budget needs 1 drop, but a cap of 0 forbids any drop -> refused on the quality bound.
        var fit = EnemyImportBudget.Evaluate(rdt, B + 0x40, B + 0x60,
            targetRdtLength: 0x100, maxDonorBytes: 224, maxClipDrops: 0,
            protectedClips: Array.Empty<int>(),
            targetPackage: ReadOnlySpan<byte>.Empty, donorTexture: null, out var prepared);

        Assert.False(fit.Fits);
        Assert.Equal(ImportFitConstraint.ClipStripBudget, fit.Limiting);
        Assert.Equal(1, fit.DroppedClips);              // surfaces how many it would have needed
        Assert.Null(prepared);
    }

    [Fact]
    public void Closure_Refused_WhenEvenMaxStripCannotFreeEnough()
    {
        var rdt = SyntheticClosure();
        // Droppable = clips 1+2 = 0x80+0xD0 = 0x150 (336); a 0x40 (64) budget needs 432-64=368 freed -> impossible.
        var fit = EnemyImportBudget.Evaluate(rdt, B + 0x40, B + 0x60,
            targetRdtLength: 0x100, maxDonorBytes: 0x40, maxClipDrops: 8,
            protectedClips: Array.Empty<int>(),
            targetPackage: ReadOnlySpan<byte>.Empty, donorTexture: null, out var prepared);

        Assert.False(fit.Fits);
        Assert.Equal(ImportFitConstraint.RdtBudget, fit.Limiting);
        Assert.Null(prepared);
    }

    // ---- RDT budget (single-range donor: the novel --add-enemy import) -------------------------

    /// <summary>A minimal closed single-range <see cref="SpeciesBlock"/> of <paramref name="len"/> bytes
    /// (no internal pointers), headed at source 0x100.</summary>
    private static SpeciesBlock Block(int len)
        => new(new byte[len], 0x100, Array.Empty<int>());

    private static SpeciesDonor TexturedRaptorHeavyDonor(int modelLength = 0x1e0)
    {
        const int bones = 21;
        const int packet = 0x1c0;
        var model = new byte[modelLength];
        if (modelLength > packet + 0x10)
        {
            PutU32(model, EnemySkeleton.BoneCountOffset, bones);
            PutU32(model, EnemySkeleton.BoneArrayOffset + 8, B + 0x100 + packet);
            model[packet + 0x0a] = 0xf0;
            model[packet + 0x0b] = 0x7f;
            model[packet + 0x0e] = 0x8a;
        }
        var texture = new TextureBlock(
            new VramBlock(0, GianEntryType.Lzss2, new VramRect(640, 0, 64, 512), new byte[64 * 512 * 2]),
            new VramBlock(1, GianEntryType.Palette, new VramRect(768, 511, 256, 1), new byte[512]),
            new ushort[] { 0x8a }, 0x7ff0);
        return new SpeciesDonor(
            DinoSpecies.RaptorHeavy, 2,
            new SpeciesBlock(model, 0x100, modelLength > packet + 0x10 ? new[] { EnemySkeleton.BoneArrayOffset + 8 } : Array.Empty<int>()),
            Block(0x20))
        { Texture = texture };
    }

    [Fact]
    public void SingleRange_Fits_UnderEngineCeiling()
    {
        var fit = EnemyImportBudget.Evaluate(Block(0x100), Block(0x80),
            targetRdtLength: 0x1000, rdtCeiling: SpeciesImporter.EngineRoomRdtCeiling,
            targetPackage: ReadOnlySpan<byte>.Empty, donorTexture: null);

        Assert.True(fit.Fits);
        Assert.Equal(ImportFitConstraint.None, fit.Limiting);
        // model appended at align4(0x1000)=0x1000 -> 0x1100; motion at align4(0x1100)=0x1100 -> 0x1180.
        Assert.Equal(0x1180, fit.ResultRdtLength);
    }

    [Fact]
    public void SingleRange_Refused_WhenAppendCrossesEngineCeiling()
    {
        // A room already near the hard ceiling: appending the model overruns 0x60000 -> refused.
        int near = SpeciesImporter.EngineRoomRdtCeiling - 4;
        var fit = EnemyImportBudget.Evaluate(Block(0x100), Block(0x80),
            targetRdtLength: near, rdtCeiling: SpeciesImporter.EngineRoomRdtCeiling,
            targetPackage: ReadOnlySpan<byte>.Empty, donorTexture: null);

        Assert.False(fit.Fits);
        Assert.Equal(ImportFitConstraint.RdtBudget, fit.Limiting);
        Assert.True(fit.ResultRdtLength > SpeciesImporter.EngineRoomRdtCeiling);
    }

    // ---- VRAM constraint (PickFreeRegion) ------------------------------------------------------

    private static byte[] BuildPackage(params (GianEntryType type, int x, int y, int w, int h, byte[] payload)[] e)
    {
        static int Align(int v) => (v + 2047) & ~2047;
        var buf = new byte[2048 + e.Sum(x => Align(x.payload.Length))];
        int pos = 2048;
        for (int i = 0; i < e.Length; i++)
        {
            int hdr = i * 16;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(hdr, 4), (uint)e[i].type);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(hdr + 4, 4), (uint)e[i].payload.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(hdr + 8, 2), (ushort)e[i].x);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(hdr + 10, 2), (ushort)e[i].y);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(hdr + 12, 2), (ushort)e[i].w);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(hdr + 14, 2), (ushort)e[i].h);
            e[i].payload.CopyTo(buf, pos);
            pos += Align(e[i].payload.Length);
        }
        return buf;
    }

    /// <summary>The Theri texture donor block (X=640 column + (768,511) palette), extracted from a
    /// synthetic donor package — a real movable <see cref="TextureBlock"/> to relocate.</summary>
    private static TextureBlock DonorTexture()
    {
        var donor = BuildPackage(
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(new byte[64 * 512 * 2])),
            (GianEntryType.Palette, 768, 511, 256, 1, new byte[256 * 1 * 2]),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));
        return TextureImporter.ExtractSpeciesTexture(donor, new ushort[] { 0x8a, 0x9a }, 0x7ff0);
    }

    [Fact]
    public void Vram_Fits_WhenFreeColumnExists()
    {
        var tex = DonorTexture();
        // Target with only a tiny bg far from the relocation columns -> a free X>=512 column + palette row exist.
        var target = BuildPackage(
            (GianEntryType.Lzss2, 0, 0, 64, 64, Lzss.Compress(new byte[64 * 64 * 2])),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));

        var fit = EnemyImportBudget.Evaluate(Block(0x100), Block(0x80),
            targetRdtLength: 0x1000, rdtCeiling: SpeciesImporter.EngineRoomRdtCeiling,
            targetPackage: target, donorTexture: tex);

        Assert.True(fit.Fits);
        Assert.Equal(ImportFitConstraint.None, fit.Limiting);
        Assert.NotNull(fit.TextureRect);
        Assert.NotNull(fit.PaletteRect);
    }

    [Fact]
    public void Vram_Refused_WhenNoFreeRegion()
    {
        var tex = DonorTexture();
        // Target whose VRAM occupies the entire relocation band X[512,1024) Y[0,512) -> no free column.
        var target = BuildPackage(
            (GianEntryType.Lzss2, 512, 0, 512, 512, Lzss.Compress(new byte[64])),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));

        var fit = EnemyImportBudget.Evaluate(Block(0x100), Block(0x80),
            targetRdtLength: 0x1000, rdtCeiling: SpeciesImporter.EngineRoomRdtCeiling,
            targetPackage: target, donorTexture: tex);

        Assert.False(fit.Fits);
        Assert.Equal(ImportFitConstraint.Vram, fit.Limiting);
        Assert.Null(fit.TextureRect);
    }

    [Fact]
    public void Vram_ReclaimsVictimColumn_WhenFreshRelocationIsFull()
    {
        var tex = DonorTexture();
        var target = BuildPackage(
            (GianEntryType.Lzss2, 512, 0, 64, 512, Lzss.Compress(new byte[64])),
            (GianEntryType.Lzss2, 576, 0, 64, 512, Lzss.Compress(new byte[64])),
            (GianEntryType.Lzss2, 640, 0, 64, 512, Lzss.Compress(new byte[64])),
            (GianEntryType.Lzss2, 704, 0, 64, 512, Lzss.Compress(new byte[64])),
            (GianEntryType.Palette, 768, 511, 256, 1, new byte[512]),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));

        var fit = EnemyImportBudget.Evaluate(Block(0x100), Block(0x80),
            targetRdtLength: 0x1000, rdtCeiling: SpeciesImporter.ResidentPoolFloor,
            targetPackage: target, donorTexture: tex,
            reclaimable: new[] { new VramRect(640, 0, 64, 512), new VramRect(768, 511, 256, 1) },
            requireTexture: true);

        Assert.True(fit.Fits);
        Assert.Equal(RoomFile.TextureImportOutcome.ReclaimedVictim, fit.TextureOutcome);
        Assert.Equal(new VramRect(640, 0, 64, 512), fit.TextureRect);
    }

    [Fact]
    public void Vram_RequiredTextureMissing_IsRefusedAsTextureFailure()
    {
        var fit = EnemyImportBudget.Evaluate(Block(0x100), Block(0x80),
            targetRdtLength: 0x1000, rdtCeiling: SpeciesImporter.ResidentPoolFloor,
            targetPackage: ReadOnlySpan<byte>.Empty, donorTexture: null,
            requireTexture: true);

        Assert.False(fit.Fits);
        Assert.Equal(ImportFitConstraint.Vram, fit.Limiting);
        Assert.Contains("texture", fit.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AtomicGroundedImport_RdtRefusalLeavesRoomByteIdentical()
    {
        var raw = SyntheticRoom.Dc1Room(Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(),
            new[] { new SyntheticRoom.Enemy(DcOpcodes.Enemy, 1, 15) });
        var room = RoomFile.Read(3, 1, raw);
        var donor = TexturedRaptorHeavyDonor(SpeciesImporter.ResidentPoolFloor);
        var before = room.Write();

        bool imported = room.TryImportSpeciesTexturedAtomic(
            donor, 0, SpeciesImporter.ResidentPoolFloor, out var fit, out var texture);

        Assert.False(imported);
        Assert.Equal(ImportFitConstraint.RdtBudget, fit.Limiting);
        Assert.Null(texture);
        Assert.Equal(before, room.Write());
        Assert.Equal(DinoSpecies.Velociraptor, room.Enemies[0].Species);
    }

    [Fact]
    public void AtomicGroundedImport_GeometryOnlyRefusalLeavesRoomByteIdentical()
    {
        var raw = SyntheticRoom.Dc1Room(Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(),
            new[] { new SyntheticRoom.Enemy(DcOpcodes.Enemy, 1, 15) });
        var room = RoomFile.Read(3, 1, raw);
        var donor = TexturedRaptorHeavyDonor() with { Texture = null };
        var before = room.Write();

        bool imported = room.TryImportSpeciesTexturedAtomic(
            donor, 0, SpeciesImporter.ResidentPoolFloor, out var fit, out var texture);

        Assert.False(imported);
        Assert.Equal(ImportFitConstraint.Vram, fit.Limiting);
        Assert.Null(texture);
        Assert.Equal(before, room.Write());
        Assert.Equal(DinoSpecies.Velociraptor, room.Enemies[0].Species);
    }

    [Fact]
    public void AtomicGroundedImport_RelocatedTextureCommitsFinalRoomOnce()
    {
        var raw = SyntheticRoom.Dc1Room(Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(),
            new[] { new SyntheticRoom.Enemy(DcOpcodes.Enemy, 1, 15) });
        var room = RoomFile.Read(3, 1, raw);

        bool imported = room.TryImportSpeciesTexturedAtomic(
            TexturedRaptorHeavyDonor(), 0, SpeciesImporter.ResidentPoolFloor, out var fit, out var texture);

        Assert.True(imported);
        Assert.True(fit.Fits);
        Assert.Equal(RoomFile.TextureImportOutcome.Relocated, fit.TextureOutcome);
        Assert.Equal(RoomFile.TextureImportOutcome.Relocated, texture!.Outcome);
        Assert.True(room.RdtBuffer.Length <= SpeciesImporter.ResidentPoolFloor);
        var reread = RoomFile.Read(room.Stage, room.Room, room.Write());
        Assert.True(reread.ParsedCleanly);
        Assert.Equal(DinoSpecies.RaptorHeavy, reread.Enemies[0].Species);
        Assert.True(reread.Enemies[0].SpeciesMatchesCategory);
    }

    [Fact]
    public void AtomicGroundedImport_ReclaimedVictimTextureCommitsFinalRoomOnce()
    {
        var raw = SyntheticRoom.Dc1Room(Array.Empty<SyntheticRoom.Item>(), Array.Empty<SyntheticRoom.Door>(),
            new[] { new SyntheticRoom.Enemy(DcOpcodes.Enemy, 1, 15) });
        var seeded = RoomFile.Read(3, 1, raw);
        var first = seeded.ImportSpeciesTextured(TexturedRaptorHeavyDonor(), 0);
        Assert.Equal(RoomFile.TextureImportOutcome.Relocated, first.Outcome);
        seeded = RoomFile.Read(3, 1, seeded.Write());

        var occupied = Enumerable.Range(0, 4)
            .Select(i => 512 + i * 64)
            .Where(x => x != first.TextureRect!.Value.X)
            .Select(x => new PackageRepacker.NewEntry(
                GianEntryType.Lzss2, new VramRect(x, 0, 64, 512), Lzss.Compress(new byte[64])))
            .ToArray();
        var room = RoomFile.Read(3, 1, PackageRepacker.InsertEntriesBeforeRdt(seeded.Write(), occupied));

        bool imported = room.TryImportSpeciesTexturedAtomic(
            TexturedRaptorHeavyDonor(), 0, SpeciesImporter.ResidentPoolFloor, out var fit, out var texture);

        Assert.True(imported);
        Assert.True(fit.Fits);
        Assert.Equal(RoomFile.TextureImportOutcome.ReclaimedVictim, fit.TextureOutcome);
        Assert.Equal(RoomFile.TextureImportOutcome.ReclaimedVictim, texture!.Outcome);
        var reread = RoomFile.Read(room.Stage, room.Room, room.Write());
        Assert.True(reread.ParsedCleanly);
        var importedEnemy = Assert.Single(reread.Enemies);
        var codes = TextureImporter.ReadModelTextureCodes(reread.RdtBuffer, importedEnemy.ModelPtr);
        Assert.NotNull(TextureImporter.ExtractSpeciesTexture(reread.OriginalBytes, codes.Tpages, codes.Clut));
    }

    [Fact]
    public void RdtConstraint_TakesPrecedenceOverVram()
    {
        var tex = DonorTexture();
        // RDT append overruns the ceiling AND there is no free VRAM: the RDT (checked first) is the limiter.
        var target = BuildPackage(
            (GianEntryType.Lzss2, 512, 0, 512, 512, Lzss.Compress(new byte[64])),
            (GianEntryType.Unknown, 0, 0, 0, 0, new byte[] { 0x62 }));

        var fit = EnemyImportBudget.Evaluate(Block(0x100), Block(0x80),
            targetRdtLength: SpeciesImporter.EngineRoomRdtCeiling - 4,
            rdtCeiling: SpeciesImporter.EngineRoomRdtCeiling,
            targetPackage: target, donorTexture: tex);

        Assert.False(fit.Fits);
        Assert.Equal(ImportFitConstraint.RdtBudget, fit.Limiting);
    }
}
