namespace DinoRand.FileFormats.Stage;

/// <summary>Internal species/resource mutations behind the <see cref="RoomFile"/> façade.</summary>
internal static class RoomSpeciesEditor
{
    internal static void Import(RoomFile room, SpeciesDonor donor, int enemyIndex)
    {
        if (enemyIndex < 0 || enemyIndex >= room.Enemies.Count)
            throw new ArgumentOutOfRangeException(nameof(enemyIndex));

        var result = SpeciesImporter.Import(room.RdtBuffer, donor.Model, donor.Motion);
        room.ReplaceRdtBuffer(result.Rdt);

        var e = room.Enemies[enemyIndex];
        e.ModelPtr = result.ModelPtr;
        e.MotionPtr = result.MotionPtr;
        e.Category = donor.Category;
        e.SpeciesBoneCount = EnemySkeleton.ReadBoneCount(donor.Model.Bytes, SpeciesImporter.PsxBase);
    }

    internal static void Import(RoomFile room, SpeciesDonorMulti donor, int enemyIndex)
    {
        if (enemyIndex < 0 || enemyIndex >= room.Enemies.Count)
            throw new ArgumentOutOfRangeException(nameof(enemyIndex));

        var result = SpeciesImporter.ImportRangeSet(room.RdtBuffer, donor.Blocks);
        room.ReplaceRdtBuffer(result.Rdt);

        var e = room.Enemies[enemyIndex];
        e.ModelPtr = result.ModelPtr;
        e.MotionPtr = result.MotionPtr;
        e.Category = donor.Category;
        e.SpeciesBoneCount = EnemySkeleton.ReadBoneCount(room.RdtBuffer, result.ModelPtr);
    }

    internal static void Overlay(RoomFile room, SpeciesDonorMulti donor, int enemyIndex)
    {
        if (enemyIndex < 0 || enemyIndex >= room.Enemies.Count)
            throw new ArgumentOutOfRangeException(nameof(enemyIndex));
        var victim = room.Enemies[enemyIndex];
        const uint psx = SpeciesImporter.PsxBase;

        var otherHeads = new List<int>();
        for (int i = 0; i < room.Enemies.Count; i++)
        {
            if (i == enemyIndex) continue;
            var e = room.Enemies[i];
            if (e.OriginalModelPtr == victim.OriginalModelPtr || e.OriginalMotionPtr == victim.OriginalMotionPtr)
                throw new InvalidOperationException(
                    "victim geometry is shared by another placement; overlay would corrupt the survivor");
            if (e.OriginalModelPtr >= psx) otherHeads.Add((int)(e.OriginalModelPtr - psx));
            if (e.OriginalMotionPtr >= psx) otherHeads.Add((int)(e.OriginalMotionPtr - psx));
        }

        int modelHead = (int)(victim.OriginalModelPtr - psx);
        int motionHead = (int)(victim.OriginalMotionPtr - psx);
        var region = SpeciesImporter.OverlayRegion(room.RdtBuffer, modelHead, motionHead, otherHeads)
            ?? throw new InvalidOperationException(
                "victim resource is not a single contiguous region (model/motion interleaved with live data); cannot overlay");

        var result = SpeciesImporter.PlaceRangeSetSplit(
            room.RdtBuffer, donor.Blocks, region.Lo, region.Hi - region.Lo);
        room.ReplaceRdtBuffer(result.Rdt);

        victim.ModelPtr = result.ModelPtr;
        victim.MotionPtr = result.MotionPtr;
        victim.Category = donor.Category;
        victim.SpeciesBoneCount = EnemySkeleton.ReadBoneCount(room.RdtBuffer, result.ModelPtr);
    }

    internal static IReadOnlyList<VramRect>? VictimReclaimableRects(RoomFile room, int enemyIndex)
    {
        var victim = room.Enemies[enemyIndex];
        var victimTex = SpeciesImporter.TryExtractTexture(
            room.OriginalBytes, room.RdtBuffer, victim.OriginalModelPtr);
        if (victimTex is null) return null;

        var victimTpages = victimTex.TpageCodes.ToHashSet();
        for (int j = 0; j < room.Enemies.Count; j++)
        {
            if (j == enemyIndex) continue;
            try
            {
                var (tpages, clut) = TextureImporter.ReadModelTextureCodes(
                    room.RdtBuffer, room.Enemies[j].OriginalModelPtr);
                if (clut == victimTex.ClutCode || tpages.Any(victimTpages.Contains)) return null;
            }
            catch { return null; }
        }
        return new[] { victimTex.Texture.Dst, victimTex.Palette.Dst };
    }
}
