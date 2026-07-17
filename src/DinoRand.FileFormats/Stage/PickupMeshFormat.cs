using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage;

/// <summary>One texture reference a pickup ground mesh carries: a <c>{tpage, clut}</c> word pair plus
/// the UV bounding box (in texels, u8) of every prim that samples it. The prims of one mesh almost
/// always share a single texref; two occur (Core Parts). STATIC-SCD-RE cont.73.</summary>
public readonly record struct PickupTexref(ushort Tpage, ushort Clut, int UMin, int UMax, int VMin, int VMax);

/// <summary>A parsed pickup ground mesh (the <c>rec+0x24</c> target of a visible key/weapon pickup,
/// op23-node model-header layout): tri/quad prim counts, total byte size and texrefs.</summary>
public sealed record PickupMesh(int TriCount, int QuadCount, int Size, IReadOnlyList<PickupTexref> Texrefs);

/// <summary>
/// The DC1 pickup ground-mesh format (STATIC-SCD-RE cont.73; Lever B of
/// docs/decisions/dc1/items/PICKUP-GROUND-MODEL-FEASIBILITY.md). Header (0xC bytes):
/// <c>{u32 triPtr, u32 quadPtr, u16 triCount @+8, u16 quadCount @+0xA}</c>, file-form
/// <c>0x80100000</c>-based pointers, contiguous layout (<c>triPtr = hdr+0xC</c>,
/// <c>quadPtr = triPtr + 40*triCount</c>). Tri prim (40 B): 3×<c>{s16 x,y,z; u8 u,v}</c> +
/// <c>{u16 tpage, u16 clut}</c> + 3×<c>{u8 r,g,b, code}</c>; quad prim (52 B) the same with 4
/// vertices/colors. Color-0's code byte is a GP0-style command: <c>0x34/0x36</c> textured gouraud
/// tri, <c>0x3c/0x3e</c> textured gouraud quad — every prim in the corpus is textured.
/// </summary>
public static class PickupMeshFormat
{
    public const int HeaderSize = 0xC;
    public const int TriPrimSize = 40;
    public const int QuadPrimSize = 52;

    private const uint PsxBase = RoomScript.PsxRdtBase;

    // Per-prim field offsets: texref word pair, and color-0's command code byte.
    private const int TriTexrefOffset = 0x18, TriCodeOffset = 0x1f, TriVertices = 3;
    private const int QuadTexrefOffset = 0x20, QuadCodeOffset = 0x27, QuadVertices = 4;

    private static bool IsTriCode(byte c) => c is 0x34 or 0x36;
    private static bool IsQuadCode(byte c) => c is 0x3c or 0x3e;

    /// <summary>
    /// Parse the pickup mesh at <paramref name="offset"/> in a decompressed RDT. Fails (returns
    /// <c>false</c>) on any structural violation — header pointers not matching the contiguous
    /// layout, prims out of bounds, or a color-0 code byte outside the known command set — so an
    /// arbitrary pointer target is never misread as a mesh (the donor catalog's validity gate).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> rdt, int offset, out PickupMesh? mesh)
    {
        mesh = null;
        if (offset < 0 || offset + HeaderSize > rdt.Length) return false;

        uint triPtr = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(offset, 4));
        uint quadPtr = BinaryPrimitives.ReadUInt32LittleEndian(rdt.Slice(offset + 4, 4));
        int triCount = BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(offset + 8, 2));
        int quadCount = BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(offset + 0xa, 2));

        if (triCount + quadCount == 0) return false;
        if (triPtr != PsxBase + (uint)offset + HeaderSize) return false;
        if (quadPtr != triPtr + (uint)(triCount * TriPrimSize)) return false;

        int size = HeaderSize + triCount * TriPrimSize + quadCount * QuadPrimSize;
        if (offset + size > rdt.Length) return false;

        // Accumulate the UV bbox per distinct {tpage, clut} pair, in file order.
        var order = new List<(ushort Tpage, ushort Clut)>();
        var boxes = new Dictionary<(ushort, ushort), (int UMin, int UMax, int VMin, int VMax)>();

        int pos = offset + HeaderSize;
        for (int i = 0; i < triCount + quadCount; i++)
        {
            bool tri = i < triCount;
            int texrefOff = tri ? TriTexrefOffset : QuadTexrefOffset;
            int vertices = tri ? TriVertices : QuadVertices;
            byte code = rdt[pos + (tri ? TriCodeOffset : QuadCodeOffset)];
            if (tri ? !IsTriCode(code) : !IsQuadCode(code)) return false;

            ushort tpage = BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(pos + texrefOff, 2));
            ushort clut = BinaryPrimitives.ReadUInt16LittleEndian(rdt.Slice(pos + texrefOff + 2, 2));
            var key = (tpage, clut);
            if (!boxes.TryGetValue(key, out var b))
            {
                order.Add(key);
                b = (int.MaxValue, int.MinValue, int.MaxValue, int.MinValue);
            }
            for (int j = 0; j < vertices; j++)
            {
                int u = rdt[pos + j * 8 + 6], v = rdt[pos + j * 8 + 7];
                b = (Math.Min(b.UMin, u), Math.Max(b.UMax, u), Math.Min(b.VMin, v), Math.Max(b.VMax, v));
            }
            boxes[key] = b;
            pos += tri ? TriPrimSize : QuadPrimSize;
        }

        var texrefs = order
            .Select(k => new PickupTexref(k.Tpage, k.Clut,
                                          boxes[k].UMin, boxes[k].UMax, boxes[k].VMin, boxes[k].VMax))
            .ToArray();
        mesh = new PickupMesh(triCount, quadCount, size, texrefs);
        return true;
    }

    /// <summary>Copy the mesh at <paramref name="offset"/> out as a standalone blob (validated by
    /// <see cref="TryParse"/> first).</summary>
    public static byte[] ExtractBlob(ReadOnlySpan<byte> rdt, int offset)
    {
        if (!TryParse(rdt, offset, out var mesh))
            throw new InvalidOperationException($"no valid pickup mesh at RDT offset 0x{offset:X}");
        return rdt.Slice(offset, mesh!.Size).ToArray();
    }

    /// <summary>
    /// Prepare an extracted blob for its new home: rewrite the header's two self-pointers for the
    /// blob living at file-form <paramref name="newModelPtr"/>, and replace each prim's
    /// <c>{tpage, clut}</c> pair via <paramref name="texrefMap"/> (donor coords → the re-uploaded
    /// coords). Geometry, UVs and colors are untouched — the texture sub-rect keeps its intra-page
    /// position at the new column, so the donor UVs stay valid by construction.
    /// </summary>
    public static void RebaseAndRetarget(byte[] blob, uint newModelPtr,
        IReadOnlyDictionary<(ushort Tpage, ushort Clut), (ushort Tpage, ushort Clut)>? texrefMap)
    {
        int triCount = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(8, 2));
        int quadCount = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0xa, 2));

        uint triPtr = newModelPtr + HeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0, 4), triPtr);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4, 4), triPtr + (uint)(triCount * TriPrimSize));

        if (texrefMap is null || texrefMap.Count == 0) return;

        void Retarget(int p, int texrefOff)
        {
            ushort tpage = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p + texrefOff, 2));
            ushort clut = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p + texrefOff + 2, 2));
            if (!texrefMap.TryGetValue((tpage, clut), out var to)) return;
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(p + texrefOff, 2), to.Tpage);
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(p + texrefOff + 2, 2), to.Clut);
        }

        int pos = HeaderSize;
        for (int i = 0; i < triCount; i++, pos += TriPrimSize) Retarget(pos, TriTexrefOffset);
        for (int i = 0; i < quadCount; i++, pos += QuadPrimSize) Retarget(pos, QuadTexrefOffset);
    }
}
