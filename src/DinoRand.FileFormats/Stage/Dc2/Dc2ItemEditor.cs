using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage.Dc2;

/// <summary>
/// Fail-closed editor for the two statically proven DC2 room-script item levers:
/// SAT-5/op-<c>0x35</c> TAKE records and kind-1 op-<c>0x2c</c> scripted gives.
/// The caller supplies the generated fixture pins; this class revalidates every structural and
/// site-local field before rewriting only the literal 16-bit catalog item operand.
/// </summary>
public static class Dc2ItemEditor
{
    public enum ItemRecordClass
    {
        Op35Take,
        Op2cGive,
    }

    public enum ItemRewriteClass
    {
        Health,
        GenericKey,
        SpecialKey2f,
    }

    /// <summary>A pinned op-0x05 literal in the contiguous push run preceding the item opcode.</summary>
    public sealed record OperandPin(
        int BlockOffset,
        byte Mode,
        short ExpectedValue,
        int PushOffset,
        int ValueOffset);

    /// <summary>All fixture identity and immutable site operands required to authorize one rewrite.</summary>
    public sealed record ItemSiteSpec(
        string SourceId,
        string RoomId,
        int RoutineOrdinal,
        int VmDirectoryIndex,
        IReadOnlyList<int> VmDirectoryIndices,
        int RoutineStart,
        int OpOffset,
        byte Opcode,
        ItemRecordClass RecordClass,
        int ExpectedItemId,
        ItemRewriteClass RewriteClass,
        OperandPin ItemOperand,
        OperandPin P3Operand,
        OperandPin Flag5Operand,
        OperandPin CleanupOperand,
        OperandPin? KindOperand,
        OperandPin? SlotOperand);

    /// <summary>One requested catalog-ID replacement at a fully pinned physical source.</summary>
    public sealed record ItemEdit(ItemSiteSpec Site, int NewItemId);

    /// <summary>
    /// Validate the complete room batch against the decompressed script before changing anything,
    /// then return a freshly repacked package. The input buffer is never mutated.
    /// </summary>
    public static byte[] ApplyEdits(
        ReadOnlySpan<byte> packageBytes,
        string roomId,
        IReadOnlyList<ItemEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(roomId);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
            return packageBytes.ToArray();

        if (edits.Any(edit => !StringComparer.Ordinal.Equals(edit.Site.RoomId, roomId))
            || edits.Select(edit => edit.Site.RoomId).Distinct(StringComparer.Ordinal).Count() != 1)
            throw Refuse($"batch room identity does not match caller room {roomId}");

        var duplicateSource = edits.GroupBy(edit => edit.Site.SourceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateSource is not null)
            throw Refuse($"duplicate source id {duplicateSource.Key}");

        for (int i = 0; i < edits.Count; i++)
        for (int j = i + 1; j < edits.Count; j++)
            if (RangesOverlap(edits[i].Site.ItemOperand.ValueOffset, edits[j].Site.ItemOperand.ValueOffset))
                throw Refuse("duplicate or overlapping item operand offsets");

        byte[] blob = Dc2ScdBlob.Decompress(packageBytes);
        var script = ReadScriptIdentity(blob);

        // Atomic validation pass: no blob byte changes until every edit has passed.
        foreach (var edit in edits)
            ValidateEdit(blob, script, roomId, edit);

        foreach (var edit in edits)
            BinaryPrimitives.WriteInt16LittleEndian(
                blob.AsSpan(edit.Site.ItemOperand.ValueOffset, 2), checked((short)edit.NewItemId));

        return Dc2ScdBlob.RepackWithBlob(packageBytes, blob);
    }

    private static void ValidateEdit(byte[] blob, ScriptIdentity script, string roomId, ItemEdit edit)
    {
        ItemSiteSpec site = edit.Site;
        if (string.IsNullOrWhiteSpace(site.SourceId))
            throw Refuse("source id is empty");

        string expectedSourceId = $"{roomId}:r{site.VmDirectoryIndex}:op{site.Opcode:x2}@0x{site.OpOffset:x}";
        if (!StringComparer.Ordinal.Equals(site.SourceId, expectedSourceId))
            throw Refuse($"source id {site.SourceId} does not match pinned identity {expectedSourceId}");

        if (site.RoutineOrdinal < 0 || site.RoutineOrdinal >= script.SortedStarts.Length
            || script.SortedStarts[site.RoutineOrdinal] != site.RoutineStart)
            throw Refuse($"{site.SourceId}: wrong routine ordinal/start");

        int[] actualIndices = script.Directory
            .Select((start, index) => (start, index))
            .Where(value => value.start == site.RoutineStart)
            .Select(value => value.index)
            .ToArray();
        if (!actualIndices.SequenceEqual(site.VmDirectoryIndices)
            || !actualIndices.Contains(site.VmDirectoryIndex))
            throw Refuse($"{site.SourceId}: wrong VM directory index/indices");

        int routineEnd = site.RoutineOrdinal + 1 < script.SortedStarts.Length
            ? script.SortedStarts[site.RoutineOrdinal + 1]
            : script.SectionEnd;
        if (site.OpOffset < site.RoutineStart || site.OpOffset + 2 > routineEnd)
            throw Refuse($"{site.SourceId}: opcode is outside its pinned routine");

        var pushRun = PushRunAt(blob, site.RoutineStart, routineEnd, site.OpOffset);
        if (blob[site.OpOffset] != site.Opcode)
            throw Refuse($"{site.SourceId}: opcode mismatch");

        switch (site.RecordClass)
        {
            case ItemRecordClass.Op35Take:
                if (site.Opcode != 0x35 || site.KindOperand is not null || site.SlotOperand is null)
                    throw Refuse($"{site.SourceId}: op35 record-class contract mismatch");
                ValidatePin(blob, pushRun, site.ItemOperand, 0x10, "item");
                ValidatePin(blob, pushRun, site.P3Operand, 0x0c, "p3");
                ValidatePin(blob, pushRun, site.Flag5Operand, 0x14, "flag5");
                ValidatePin(blob, pushRun, site.CleanupOperand, 0x04, "cleanup-secondary");
                ValidatePin(blob, pushRun, site.SlotOperand, 0x40, "slot");
                break;

            case ItemRecordClass.Op2cGive:
                if (site.Opcode != 0x2c || site.KindOperand is null || site.SlotOperand is not null)
                    throw Refuse($"{site.SourceId}: op2c record-class contract mismatch");
                ValidatePin(blob, pushRun, site.KindOperand, 0x10, "kind");
                if (site.KindOperand.ExpectedValue != 1)
                    throw Refuse($"{site.SourceId}: only kind-1 op2c gives are supported");
                ValidatePin(blob, pushRun, site.ItemOperand, 0x0c, "item");
                ValidatePin(blob, pushRun, site.P3Operand, 0x08, "p3");
                ValidatePin(blob, pushRun, site.Flag5Operand, 0x04, "flag5");
                ValidatePin(blob, pushRun, site.CleanupOperand, 0x00, "cleanup");
                break;

            default:
                throw Refuse($"{site.SourceId}: unsupported record class");
        }

        if (site.ItemOperand.ExpectedValue != site.ExpectedItemId)
            throw Refuse($"{site.SourceId}: expected item id disagrees with its operand pin");

        ItemRewriteClass oldClass = ClassifyItem(site.ExpectedItemId);
        ItemRewriteClass newClass = ClassifyItem(edit.NewItemId);
        if (oldClass != site.RewriteClass || newClass != site.RewriteClass)
            throw Refuse($"{site.SourceId}: cross-class or unsupported item rewrite");
        if (site.RewriteClass == ItemRewriteClass.SpecialKey2f
            && (site.ExpectedItemId != 0x2f || edit.NewItemId != 0x2f))
            throw Refuse($"{site.SourceId}: item 0x2f is an isolated rewrite class");
    }

    private static ItemRewriteClass ClassifyItem(int itemId) => itemId switch
    {
        >= 0x1a and <= 0x1d or 0x1f => ItemRewriteClass.Health,
        >= 0x21 and <= 0x2e or >= 0x30 and <= 0x34 => ItemRewriteClass.GenericKey,
        0x2f => ItemRewriteClass.SpecialKey2f,
        0x1e => throw Refuse("item 0x1e is excluded because kind-1 GiveItem remaps it on Easy"),
        _ => throw Refuse($"item 0x{itemId:x} has no supported rewrite class"),
    };

    private static void ValidatePin(
        byte[] blob,
        IReadOnlyDictionary<int, int> pushRun,
        OperandPin pin,
        int requiredBlockOffset,
        string name)
    {
        if (pin.BlockOffset != requiredBlockOffset || pin.Mode != 0)
            throw Refuse($"{name} operand has the wrong block offset or is nonliteral");
        if (!pushRun.TryGetValue(requiredBlockOffset, out int actualPush) || actualPush != pin.PushOffset)
            throw Refuse($"{name} operand is not the pinned member of the contiguous push run");
        if (pin.PushOffset < 0 || pin.PushOffset + 4 > blob.Length
            || pin.ValueOffset != pin.PushOffset + 2)
            throw Refuse($"{name} operand offsets are invalid");
        if (blob[pin.PushOffset] != 0x05 || blob[pin.PushOffset + 1] != pin.Mode)
            throw Refuse($"{name} operand push opcode/mode mismatch");
        short actual = BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(pin.ValueOffset, 2));
        if (actual != pin.ExpectedValue)
            throw Refuse($"{name} operand expected 0x{pin.ExpectedValue & 0xffff:x4}, found 0x{actual & 0xffff:x4}");
    }

    private static Dictionary<int, int> PushRunAt(
        byte[] blob,
        int routineStart,
        int routineEnd,
        int targetOffset)
    {
        var contiguousPushes = new List<int>();
        int offset = routineStart;
        while (offset < routineEnd)
        {
            if (offset == targetOffset)
            {
                var result = new Dictionary<int, int>();
                for (int i = 0; i < contiguousPushes.Count; i++)
                    result[(contiguousPushes.Count - 1 - i) * 4] = contiguousPushes[i];
                return result;
            }

            byte opcode = blob[offset];
            int length = OpcodeLength(opcode);
            if (length == 0 || offset + length > routineEnd)
                break;
            if (opcode == 0x05)
                contiguousPushes.Add(offset);
            else
                contiguousPushes.Clear();
            offset += length;
            if (opcode == 0x04)
                break;
        }
        throw Refuse($"opcode offset 0x{targetOffset:x} is not an instruction boundary in the pinned routine");
    }

    private static int OpcodeLength(byte opcode) => opcode switch
    {
        0x01 or 0x02 or 0x03 => 6,
        0x04 => 2,
        0x05 or 0x06 or 0x07 or 0x08 or 0x19 => 4,
        >= 0x10 and <= 0x59 => 2,
        _ => 0,
    };

    private static ScriptIdentity ReadScriptIdentity(byte[] blob)
    {
        if (blob.Length < 0x80)
            throw Refuse("SCD blob is too short for the directory");
        uint slot5 = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x14, 4));
        if (slot5 < Dc2ScdBlob.BlobBaseVa
            || slot5 >= Dc2ScdBlob.BlobBaseVa + (uint)blob.Length)
            throw Refuse("slot-5 script directory entry is out of range");
        int sectionStart = (int)(slot5 - Dc2ScdBlob.BlobBaseVa);
        int sectionEnd = blob.Length;
        for (int offset = 0; offset < 0x80; offset += 4)
        {
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(offset, 4));
            if (pointer < Dc2ScdBlob.BlobBaseVa
                || pointer >= Dc2ScdBlob.BlobBaseVa + (uint)blob.Length)
                continue;
            int candidate = (int)(pointer - Dc2ScdBlob.BlobBaseVa);
            if (candidate > sectionStart && candidate < sectionEnd)
                sectionEnd = candidate;
        }

        int opBase = sectionStart + 0x1c;
        int sectionSize = sectionEnd - sectionStart;
        if (opBase < 0 || opBase + 4 > sectionEnd)
            throw Refuse("slot-5 section has no routine directory");

        var directory = new List<int>();
        int bound = sectionSize;
        for (int index = 0; index * 4 < bound; index++)
        {
            int entryOffset = opBase + index * 4;
            if (entryOffset + 4 > sectionEnd)
                break;
            uint relative = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(entryOffset, 4));
            if (relative >= sectionSize)
                break;
            directory.Add(checked(opBase + (int)relative));
            bound = Math.Min(bound, (int)relative);
        }
        if (directory.Count == 0)
            throw Refuse("slot-5 routine directory is empty");
        if (directory.Any(start => start < opBase || start >= sectionEnd))
            throw Refuse("slot-5 routine start is out of range");

        return new ScriptIdentity(
            directory.ToArray(),
            directory.Distinct().OrderBy(value => value).ToArray(),
            sectionEnd);
    }

    private static bool RangesOverlap(int first, int second)
        => first < second + 2 && second < first + 2;

    private static InvalidOperationException Refuse(string message)
        => new($"DC2 item edit refused: {message}");

    private sealed record ScriptIdentity(int[] Directory, int[] SortedStarts, int SectionEnd);
}
