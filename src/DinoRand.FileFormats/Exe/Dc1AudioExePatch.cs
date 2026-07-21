using System.Buffers.Binary;
using static DinoRand.FileFormats.Exe.ExePatcher;

namespace DinoRand.FileFormats.Exe;

internal static class Dc1AudioExePatch
{
    internal static uint SeBlockVa(ReadOnlySpan<byte> exe, int stage, int room)
    {
        if (stage < 0) throw new ArgumentOutOfRangeException(nameof(stage), stage, "stage must be ≥ 0.");
        if (room is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(room), room, "room must be 0..31.");
        return ReadUInt32AtVa(exe, SeDirectoryBaseVa + (uint)(stage * 32 + room) * 4);
    }

    internal static string? ReadCStringAtVa(ReadOnlySpan<byte> exe, uint va, int maxLen = 64)
    {
        if (!IsFileBacked(va)) return null;
        int off = VaToFileOffset(va);
        int end = off;
        while (end < exe.Length && end - off < maxLen && exe[end] != 0) end++;
        if (end >= exe.Length || end - off >= maxLen || end == off) return null;
        return System.Text.Encoding.ASCII.GetString(exe.Slice(off, end - off));
    }

    internal static byte[] ExtractRoomDinoSubBlock(ReadOnlySpan<byte> exe, int stage, int room)
    {
        uint blockVa = SeBlockVa(exe, stage, room);
        if (!IsSeBlockVa(blockVa))
            throw new InvalidOperationException(
                $"donor SE block for st{stage}{room:X2} (VA 0x{blockVa:X}) is outside the manifest table " +
                $"[0x{SeManifestLoVa:X},0x{SeManifestHiVa:X}) — unexpected build/locale.");
        uint startVa = 0; int count = 0;
        for (uint va = blockVa; ; va += (uint)SeRecordStride)
        {
            uint namePtr = ReadUInt32AtVa(exe, va + (uint)SeRecordNameOffset);
            if (namePtr == 0) break; // block terminator
            string? nm = ReadCStringAtVa(exe, namePtr);
            bool dino = nm != null && nm.StartsWith(SeDinoPrefix, StringComparison.OrdinalIgnoreCase);
            if (dino) { if (startVa == 0) startVa = va; count++; }
            else if (startVa != 0) break; // dino run ended (dino records sit at the block tail)
        }
        if (startVa == 0) return Array.Empty<byte>();
        return exe.Slice(VaToFileOffset(startVa), count * SeRecordStride).ToArray();
    }

    internal static SeRetargetResult RetargetRoomDinoSe(Span<byte> exe, int stage, int room, ReadOnlySpan<byte> donorSubBlock)
    {
        if (donorSubBlock.Length == 0 || donorSubBlock.Length % SeRecordStride != 0)
            throw new ArgumentException(
                $"donor sub-block must be a non-empty multiple of 0x{SeRecordStride:X} bytes; got {donorSubBlock.Length}.",
                nameof(donorSubBlock));
        int donorCount = donorSubBlock.Length / SeRecordStride;

        uint blockVa = SeBlockVa(exe, stage, room);
        if (!IsSeBlockVa(blockVa))
            throw new InvalidOperationException(
                $"room st{stage}{room:X2} SE block (VA 0x{blockVa:X}) is outside the manifest table " +
                $"[0x{SeManifestLoVa:X},0x{SeManifestHiVa:X}) — unexpected build/locale.");

        // Find the room's contiguous dino sub-block (start + capacity).
        uint dinoStartVa = 0; int capacity = 0;
        for (uint va = blockVa; ; va += (uint)SeRecordStride)
        {
            uint namePtr = ReadUInt32AtVa(exe, va + (uint)SeRecordNameOffset);
            if (namePtr == 0) break;
            string? nm = ReadCStringAtVa(exe, namePtr);
            bool dino = nm != null && nm.StartsWith(SeDinoPrefix, StringComparison.OrdinalIgnoreCase);
            if (dino) { if (dinoStartVa == 0) dinoStartVa = va; capacity++; }
            else if (dinoStartVa != 0) break;
        }
        if (dinoStartVa == 0)
            throw new InvalidOperationException(
                $"room st{stage}{room:X2} has no enemy (se\\dino\\) SE records to retarget.");
        if (donorCount > capacity)
            throw new InvalidOperationException(
                $"donor SE sub-block ({donorCount} records) exceeds room st{stage}{room:X2} dino capacity ({capacity}).");

        int dinoStartOff = VaToFileOffset(dinoStartVa);
        donorSubBlock.CopyTo(exe.Slice(dinoStartOff, donorSubBlock.Length));

        // Early-terminate: zero the namePtr of the record right after the copied sub-block so the loader
        // stops there (the residual raptor records + the original pad after it are left unread).
        if (donorCount < capacity)
        {
            uint termVa = dinoStartVa + (uint)(donorCount * SeRecordStride) + (uint)SeRecordNameOffset;
            WriteUInt32(exe, VaToFileOffset(termVa), 0);
        }
        return new SeRetargetResult(blockVa, donorCount, capacity);
    }

    internal static uint BgmRecordVa(int id)
    {
        if (id is < 1 or > BgmRecordCount)
            throw new ArgumentOutOfRangeException(nameof(id), id, $"BGM id must be 1..{BgmRecordCount}.");
        return BgmCatalogBaseVa + (uint)(id - 1) * BgmRecordStride;
    }

    internal static BgmShuffleEntry[] ShuffleBgmCatalog(Span<byte> exe, int seed)
    {
        // Validate + snapshot the records we will move (ids 2..99).
        int n = BgmRecordCount - (BgmFirstShuffledId - 1);
        var ids = new int[n];
        var flags = new uint[n];
        var namePtr = new uint[n];
        var size = new uint[n];
        var name = new string?[n];
        for (int k = 0; k < n; k++)
        {
            int id = BgmFirstShuffledId + k;
            int off = VaToFileOffset(BgmRecordVa(id));
            uint recId = ReadUInt32(exe, off + BgmIdOffset);
            if (recId != (uint)id)
                throw new InvalidOperationException(
                    $"BGM catalog record {id} has id field 0x{recId:X} (expected {id}) — unexpected build/locale; refusing to shuffle.");
            uint np = ReadUInt32(exe, off + BgmNamePtrOffset);
            string? nm = ReadCStringAtVa(exe, np);
            if (nm is null || !nm.StartsWith(BgmNamePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"BGM catalog record {id} namePtr 0x{np:X} does not resolve to a '{BgmNamePrefix}' string " +
                    "— unexpected build/locale; refusing to shuffle.");
            ids[k] = id;
            flags[k] = ReadUInt32(exe, off + BgmFlagsOffset);
            namePtr[k] = np;
            size[k] = ReadUInt32(exe, off + BgmSizeOffset);
            name[k] = nm;
        }

        // Group record indices by flags class, then permute the {namePtr,size} pairs within each class.
        // Classes are processed in ascending flags order, members in ascending id order, so the result is a
        // pure deterministic function of (seed, table) — independent of dictionary iteration order.
        uint rng = (uint)seed;
        var newNamePtr = (uint[])namePtr.Clone();
        var newSize = (uint[])size.Clone();
        var newName = (string?[])name.Clone();
        foreach (uint cls in flags.Distinct().OrderBy(f => f))
        {
            var members = new List<int>();
            for (int k = 0; k < n; k++) if (flags[k] == cls) members.Add(k);
            // Fisher–Yates over the member slots: pick a source pair for each destination slot.
            int m = members.Count;
            var perm = new int[m];
            for (int i = 0; i < m; i++) perm[i] = i;
            for (int i = m - 1; i > 0; i--)
            {
                int j = (int)(NextRand(ref rng) % (uint)(i + 1));
                (perm[i], perm[j]) = (perm[j], perm[i]);
            }
            for (int i = 0; i < m; i++)
            {
                int dst = members[i], src = members[perm[i]];
                newNamePtr[dst] = namePtr[src];
                newSize[dst] = size[src];
                newName[dst] = name[src];
            }
        }

        // Write back the permuted pairs and build the change list.
        var result = new BgmShuffleEntry[n];
        for (int k = 0; k < n; k++)
        {
            int off = VaToFileOffset(BgmRecordVa(ids[k]));
            WriteUInt32(exe, off + BgmNamePtrOffset, newNamePtr[k]);
            WriteUInt32(exe, off + BgmSizeOffset, newSize[k]);
            result[k] = new BgmShuffleEntry(ids[k], flags[k], namePtr[k], size[k], newNamePtr[k], newSize[k], name[k], newName[k]);
        }
        return result;
    }

    internal static BgmShuffleEntry[] ApplyBgmCatalogPlan(Span<byte> exe, IReadOnlyList<int> sourceIds)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        int n = BgmRecordCount - (BgmFirstShuffledId - 1);
        if (sourceIds.Count != n)
            throw new ArgumentException($"BGM plan has {sourceIds.Count} assignments; expected {n}.", nameof(sourceIds));

        var flags = new uint[n];
        var namePtr = new uint[n];
        var size = new uint[n];
        var name = new string?[n];
        for (int k = 0; k < n; k++)
        {
            int id = BgmFirstShuffledId + k;
            int off = VaToFileOffset(BgmRecordVa(id));
            uint recId = ReadUInt32(exe, off + BgmIdOffset);
            if (recId != (uint)id)
                throw new InvalidOperationException(
                    $"BGM catalog record {id} has id field 0x{recId:X} (expected {id}) — unexpected build/locale; refusing to shuffle.");
            uint np = ReadUInt32(exe, off + BgmNamePtrOffset);
            string? nm = ReadCStringAtVa(exe, np);
            if (nm is null || !nm.StartsWith(BgmNamePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"BGM catalog record {id} namePtr 0x{np:X} does not resolve to a '{BgmNamePrefix}' string — unexpected build/locale; refusing to shuffle.");
            flags[k] = ReadUInt32(exe, off + BgmFlagsOffset);
            namePtr[k] = np;
            size[k] = ReadUInt32(exe, off + BgmSizeOffset);
            name[k] = nm;
        }

        if (sourceIds.Distinct().Count() != n)
            throw new ArgumentException("BGM plan is not a permutation.", nameof(sourceIds));
        var result = new BgmShuffleEntry[n];
        for (int k = 0; k < n; k++)
        {
            int source = sourceIds[k] - BgmFirstShuffledId;
            if (source is < 0 || source >= n || flags[source] != flags[k])
                throw new ArgumentException($"BGM plan assignment {k} crosses or leaves its flags class.", nameof(sourceIds));
            int id = BgmFirstShuffledId + k;
            int off = VaToFileOffset(BgmRecordVa(id));
            WriteUInt32(exe, off + BgmNamePtrOffset, namePtr[source]);
            WriteUInt32(exe, off + BgmSizeOffset, size[source]);
            result[k] = new BgmShuffleEntry(id, flags[k], namePtr[k], size[k], namePtr[source], size[source], name[k], name[source]);
        }
        return result;
    }
}
