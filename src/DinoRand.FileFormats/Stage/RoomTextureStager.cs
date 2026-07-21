using DinoRand.FileFormats.Compression;

namespace DinoRand.FileFormats.Stage;

/// <summary>Internal ordered texture-entry staging behind the <see cref="RoomFile"/> façade.</summary>
internal sealed class RoomTextureStager
{
    private List<PackageRepacker.NewEntry>? _inserts;

    internal IReadOnlyList<VramRect> Rects
        => _inserts?.Select(e => e.Dst).ToList() ?? (IReadOnlyList<VramRect>)Array.Empty<VramRect>();

    internal void Add(RoomFile room, PackageRepacker.NewEntry entry)
    {
        _inserts ??= new List<PackageRepacker.NewEntry>();
        _inserts.Add(entry);
        room.MarkStructurallyEdited();
    }

    internal RoomFile.TexturedImportResult Stage(
        RoomFile room, TextureBlock? texture, uint importedModelPtr,
        IReadOnlyList<VramRect>? reclaim = null)
    {
        if (texture is null)
            return new RoomFile.TexturedImportResult(RoomFile.TextureImportOutcome.GeometryOnly, null, null);

        TextureBlock placed;
        var outcome = RoomFile.TextureImportOutcome.Relocated;
        try { placed = TextureImporter.PickFreeRegion(room.OriginalBytes, texture); }
        catch (InvalidOperationException)
        {
            if (reclaim is null)
                return new RoomFile.TexturedImportResult(RoomFile.TextureImportOutcome.GeometryOnly, null, null);
            try { placed = TextureImporter.PickFreeRegion(room.OriginalBytes, texture, reclaim); }
            catch (InvalidOperationException)
            {
                return new RoomFile.TexturedImportResult(RoomFile.TextureImportOutcome.GeometryOnly, null, null);
            }
            outcome = RoomFile.TextureImportOutcome.ReclaimedVictim;
        }

        var map = new Dictionary<ushort, ushort>(texture.TpageCodes.Count);
        for (int i = 0; i < texture.TpageCodes.Count; i++)
            map[texture.TpageCodes[i]] = placed.TpageCodes[i];
        TextureImporter.RewriteModelCodes(room.RdtBuffer, importedModelPtr, map, placed.ClutCode);

        _inserts ??= new List<PackageRepacker.NewEntry>();
        _inserts.Add(new(GianEntryType.Lzss2, placed.Texture.Dst, Lzss.Compress(placed.Texture.Pixels)));
        _inserts.Add(new(GianEntryType.Palette, placed.Palette.Dst, placed.Palette.Pixels));
        room.MarkStructurallyEdited();
        return new RoomFile.TexturedImportResult(outcome, placed.Texture.Dst, placed.Palette.Dst);
    }

    internal byte[] Apply(byte[] roomBytes)
        => _inserts is { Count: > 0 }
            ? PackageRepacker.InsertEntriesBeforeRdt(roomBytes, _inserts)
            : roomBytes;

    internal void Clear() => _inserts = null;
}
