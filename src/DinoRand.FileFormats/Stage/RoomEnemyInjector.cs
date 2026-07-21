using System.Buffers.Binary;

namespace DinoRand.FileFormats.Stage;

/// <summary>Internal enemy-record authoring and script injection behind the <see cref="RoomFile"/> façade.</summary>
internal static class RoomEnemyInjector
{
    internal static RoomFile.TexturedImportResult ImportSpeciesGroupTextured(
        RoomFile room, SpeciesDonor donor, int victimIndex,
        IReadOnlyList<(short X, short Y, short Z, short Rotation)> extraMembers,
        byte[]? groupSetupRecord)
    {
        var tex = room.ImportSpeciesTextured(donor, victimIndex);
        if (extraMembers is null || extraMembers.Count == 0) return tex;

        if (room.Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject group members");

        var member0 = room.Enemies[victimIndex];
        uint modelPtr = member0.ModelPtr, motionPtr = member0.MotionPtr;
        byte category = member0.Category;
        int member0Offset = member0.FileOffset;

        room.Script.ApplyEnemyEdits(room.RdtBuffer, room.Enemies);

        var usedSlots = new HashSet<int>(room.Enemies.Select(e => (int)e.Slot));
        byte killFlag = PickFreeKillFlag(room);

        int setupLen = groupSetupRecord?.Length ?? 0;
        var blob = new byte[setupLen + extraMembers.Count * EnemyRecord.Length];
        groupSetupRecord?.CopyTo(blob, 0);
        byte slot = 0;
        for (int i = 0; i < extraMembers.Count; i++)
        {
            while (usedSlots.Contains(slot) && slot < 0xff) slot++;
            usedSlots.Add(slot);
            var (x, y, z, rot) = extraMembers[i];
            BuildEnemyRecord(category, slot, killFlag, x, y, z, rot, modelPtr, motionPtr)
                .CopyTo(blob, setupLen + i * EnemyRecord.Length);
        }

        room.ReplaceRdtBuffer(ScriptInjector.Insert(room.RdtBuffer, member0Offset, blob));
        room.MarkStructurallyEdited();
        room.Reparse();
        return tex;
    }

    internal static (EnemyRecord Enemy, RoomFile.TexturedImportResult Texture) AddEnemyTextured(
        RoomFile room, SpeciesDonor donor, short x, short y, short z, short rotation,
        byte? slot, byte? killFlag, EnemyAuthoring authoring)
    {
        var added = AddEnemy(room, donor, x, y, z, rotation, slot, killFlag, authoring);
        return (added, room.StageTexture(donor.Texture, added.ModelPtr));
    }

    internal static EnemyRecord AddEnemy(
        RoomFile room, SpeciesDonor donor, short x, short y, short z, short rotation,
        byte? slot, byte? killFlag, EnemyAuthoring authoring)
    {
        if (room.Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject");

        byte chosenSlot = slot ?? PickFreeSlot(room);
        byte chosenKill = killFlag ?? PickFreeKillFlag(room);

        var imp = SpeciesImporter.Import(room.RdtBuffer, donor.Model, donor.Motion);
        room.ReplaceRdtBuffer(imp.Rdt);

        if (!ScriptInjector.TryReadFuncTable(room.RdtBuffer, out _, out var starts) || starts.Count == 0)
            throw new InvalidOperationException("room has no readable init subroutine");
        int o = InitInsertOffset(room.RdtBuffer, starts);
        if (o < 0)
            throw new InvalidOperationException("init subroutine has no interior insertion point");

        var record = BuildEnemyRecord(donor.Category, chosenSlot, chosenKill, x, y, z, rotation,
                                      imp.ModelPtr, imp.MotionPtr, authoring);
        if (authoring.ActivateBehavior is byte behavior)
        {
            var (b3, blob) = ResolveActivation(room, behavior, authoring);
            record = AppendActivationPair(record, chosenSlot, behavior, b3, blob);
        }
        room.ReplaceRdtBuffer(ScriptInjector.Insert(room.RdtBuffer, o, record));

        room.MarkStructurallyEdited();
        room.Reparse();
        return room.Enemies.First(e => e.FileOffset == o);
    }

    internal static EnemyRecord AddEnemyAt(
        RoomFile room, SpeciesDonor donor, int rdtOffset, short x, short y, short z, short rotation,
        byte? slot, byte? killFlag, EnemyAuthoring authoring)
        => InjectAt(room, donor, rdtOffset, x, y, z, rotation, slot, killFlag, authoring);

    internal static (uint Model, uint Motion)? LoadedModelFor(RoomFile room, DinoSpecies species)
    {
        var e = room.Enemies.FirstOrDefault(x => x.IsRandomizableDino && x.Species == species);
        return e is null ? null : (e.OriginalModelPtr, e.OriginalMotionPtr);
    }

    internal static (EnemyRecord Enemy, RoomFile.TexturedImportResult Texture) AddEnemyAtTextured(
        RoomFile room, SpeciesDonor donor, int rdtOffset, short x, short y, short z, short rotation,
        byte? slot, byte? killFlag, EnemyAuthoring authoring)
    {
        bool reused = LoadedModelFor(room, donor.Species) is not null;
        var added = AddEnemyAt(room, donor, rdtOffset, x, y, z, rotation, slot, killFlag, authoring);
        return reused
            ? (added, new RoomFile.TexturedImportResult(RoomFile.TextureImportOutcome.Reused, null, null))
            : (added, room.StageTexture(donor.Texture, added.ModelPtr));
    }

    internal static EnemyRecord AddEnemyStanding(
        RoomFile room, SpeciesDonor donor, short x, short y, short z, short rotation,
        byte? slot, byte? killFlag)
    {
        if (room.Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject");
        int o = InjectionSiteClassifier.StandingSite(room.RdtBuffer);
        if (o < 0)
            throw new InvalidOperationException(
                "room has no standing (active+persistent) injection site (no flag-gated init branch-target " +
                "spawn block); use AddEnemyEncounter for a one-shot event spawn, or AddEnemy for an inert prop");
        return InjectAt(room, donor, o, x, y, z, rotation, slot, killFlag);
    }

    internal static EnemyRecord AddEnemyEncounter(
        RoomFile room, SpeciesDonor donor, short x, short y, short z, short rotation,
        byte? slot, byte? killFlag)
    {
        if (room.Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject");
        int o = InjectionSiteClassifier.EncounterSite(room.RdtBuffer);
        if (o < 0)
            throw new InvalidOperationException(
                "room has no event (active one-shot) injection site (no non-init subroutine)");
        return InjectAt(room, donor, o, x, y, z, rotation, slot, killFlag);
    }

    private static EnemyRecord InjectAt(
        RoomFile room, SpeciesDonor donor, int rdtOffset, short x, short y, short z, short rotation,
        byte? slot = null, byte? killFlag = null, EnemyAuthoring authoring = default)
    {
        if (room.Script is not { ParsedCleanly: true })
            throw new InvalidOperationException("room script did not parse cleanly; cannot inject");

        byte chosenSlot = slot ?? PickFreeSlot(room);
        byte chosenKill = killFlag ?? PickFreeKillFlag(room);

        uint modelPtr, motionPtr;
        if (LoadedModelFor(room, donor.Species) is { } loaded)
            (modelPtr, motionPtr) = loaded;
        else
        {
            var imp = SpeciesImporter.Import(room.RdtBuffer, donor.Model, donor.Motion);
            room.ReplaceRdtBuffer(imp.Rdt);
            (modelPtr, motionPtr) = (imp.ModelPtr, imp.MotionPtr);
        }

        int sub = ScriptInjector.SubroutineAtBoundary(room.RdtBuffer, rdtOffset);
        if (sub < 0)
            throw new InvalidOperationException(
                $"offset 0x{rdtOffset:x} is not a clean opcode boundary inside any subroutine; " +
                "injecting there would split an instruction (pick an offset from the room's decoded script)");

        if (ScriptCfg.IsControlOpcode(room.RdtBuffer[rdtOffset]))
            throw new InvalidOperationException(
                $"offset 0x{rdtOffset:x} is a control-flow opcode (0x{room.RdtBuffer[rdtOffset]:x2}); splicing a " +
                "record there derails the SCD VM — pick a plain opcode boundary, not a branch/loop/return");

        var record = BuildEnemyRecord(donor.Category, chosenSlot, chosenKill, x, y, z, rotation,
                                      modelPtr, motionPtr, authoring);
        if (authoring.ActivateBehavior is byte behavior)
        {
            var (b3, blob) = ResolveActivation(room, behavior, authoring);
            record = AppendActivationPair(record, chosenSlot, behavior, b3, blob);
        }
        room.ReplaceRdtBuffer(ScriptInjector.Insert(room.RdtBuffer, rdtOffset, record));

        room.MarkStructurallyEdited();
        room.Reparse();
        return room.Enemies.First(e => e.FileOffset == rdtOffset);
    }

    private static int InitInsertOffset(ReadOnlySpan<byte> rdt, IReadOnlyList<int> starts)
    {
        int s0 = starts[0];
        int e0 = starts.Count > 1 ? starts[1] : rdt.Length;

        int safe = ScriptCfg.SafeInsertOffset(rdt, s0, e0);
        if (safe > 0) return safe;

        int best = -1, pos = s0;
        while (pos < e0)
        {
            int len = DcOpcodes.Length(rdt, pos);
            if (len <= 0 || pos + len > e0) break;
            if (pos > s0 && (pos & 3) == 0 && !ScriptCfg.IsControlOpcode(rdt[pos])) best = pos;
            pos += len;
        }
        return best;
    }

    private static byte PickFreeSlot(RoomFile room)
    {
        var used = new HashSet<int>(room.Enemies.Select(e => (int)e.Slot));
        byte s = 0;
        while (used.Contains(s) && s < 0xff) s++;
        return s;
    }

    private static byte PickFreeKillFlag(RoomFile room)
    {
        var used = new HashSet<int>(room.Enemies.Select(e => (int)e.KillFlag));
        byte f = 0;
        while (used.Contains(f) && f < 0xff) f++;
        return f;
    }

    private static byte[] BuildEnemyRecord(
        byte category, byte slot, byte killFlag, short x, short y, short z, short rotation,
        uint modelPtr, uint motionPtr, EnemyAuthoring authoring = default)
    {
        var r = new byte[DcOpcodes.EnemyLength];
        r[0] = DcOpcodes.Enemy;
        r[EnemyRecord.SlotOffset] = slot;
        r[EnemyRecord.CategoryOffset] = category;
        r[EnemyRecord.AiParamOffset] = authoring.AiParam;
        r[EnemyRecord.KillFlagOffset] = killFlag;
        r[EnemyRecord.BirthModeOffset] = authoring.BirthMode;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(EnemyRecord.MaxHpOffset, 2), authoring.MaxHp);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(EnemyRecord.PosXOffset, 2), x);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(EnemyRecord.PosYOffset, 2), y);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(EnemyRecord.PosZOffset, 2), z);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(EnemyRecord.RotationOffset, 2), rotation);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(EnemyRecord.ModelOffset, 4), modelPtr);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(EnemyRecord.MotionOffset, 4), motionPtr);
        return r;
    }

    private static byte[] AppendActivationPair(
        byte[] record, byte slot, byte behavior, byte b3, byte[] blob)
    {
        if (blob.Length != 8)
            throw new ArgumentException($"op 0x3a operand blob must be 8 bytes, got {blob.Length}", nameof(blob));
        var r = new byte[record.Length + 16];
        record.CopyTo(r, 0);
        int o = record.Length;
        r[o] = 0x22; r[o + 1] = 0x02; r[o + 2] = slot;
        r[o + 4] = 0x3a; r[o + 6] = behavior; r[o + 7] = b3;
        blob.CopyTo(r, o + 8);
        return r;
    }

    private static (byte B3, byte[] Blob) ResolveActivation(
        RoomFile room, byte behavior, EnemyAuthoring authoring)
    {
        if (authoring.ActivateBlob is { } explicitBlob)
            return (authoring.ActivateB3 ?? 0, explicitBlob);
        var native = FindNative3a(room, behavior) ?? FindNative3a(room, null);
        return (authoring.ActivateB3 ?? native?.B3 ?? 0, native?.Blob ?? new byte[8]);
    }

    private static (byte B3, byte[] Blob)? FindNative3a(RoomFile room, byte? behavior)
    {
        if (!ScriptInjector.TryReadFuncTable(room.RdtBuffer, out _, out var starts)) return null;
        for (int i = 0; i < starts.Count; i++)
        {
            int s = starts[i], e = i + 1 < starts.Count ? starts[i + 1] : room.RdtBuffer.Length;
            for (int pos = s; pos < e;)
            {
                int len = DcOpcodes.Length(room.RdtBuffer, pos);
                if (len <= 0 || pos + len > e) break;
                if (room.RdtBuffer[pos] == 0x3a && (behavior is null || room.RdtBuffer[pos + 2] == behavior))
                    return (room.RdtBuffer[pos + 3], room.RdtBuffer.AsSpan(pos + 4, 8).ToArray());
                pos += len;
            }
        }
        return null;
    }
}
