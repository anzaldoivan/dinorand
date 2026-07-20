namespace DinoRand.ApClient;

/// <summary>
/// One poll tick of the DC1 companion (AP-CLIENT-PLAN.md §1 "Poll engine"): hard-gated on
/// gameplay state <c>[0x6D3E68]==3</c> — no reads or writes during menus/transitions/cutscenes.
/// Pure orchestration over the seams so it is testable with a fake <see cref="IGameMemory"/>
/// and a callback-backed sender; the CLI owns the timer loop and the real session.
/// </summary>
public sealed class Dc1PollEngine
{
    private readonly IGameMemory _mem;
    private readonly Dc1CheckTracker _tracker;
    private readonly IReadOnlyDictionary<string, long> _apIdByName;
    private readonly ushort _goalRoomWord;
    private bool _goalSent;

    public Dc1PollEngine(IGameMemory mem, Dc1ClientChecks checks, string goalRoom)
    {
        _mem = mem;
        _tracker = new Dc1CheckTracker(checks);
        _apIdByName = checks.Locations.ToDictionary(l => l.Name, l => l.ApId);
        _goalRoomWord = Convert.ToUInt16(goalRoom, 16);
    }

    /// <summary>What one tick decided (empty collections when the gameplay gate was closed).</summary>
    public sealed record TickResult(
        bool InGameplay,
        IReadOnlyList<long> NewCheckIds,
        IReadOnlyList<MemWrite> GrantWrites,
        int AppliedThrough,
        bool GoalReached);

    /// <summary>
    /// Evaluate one poll: read the taken-flag bank → newly-checked AP ids (minus
    /// <paramref name="alreadySent"/>); plan grants for <paramref name="received"/>; detect goal.
    /// The caller applies the result: send checks, write grants, persist state.
    /// </summary>
    public TickResult Tick(IReadOnlyList<ReceivedGameItem> received, int appliedThrough,
        IReadOnlySet<long> alreadySent)
    {
        if (_mem.ReadU32(Dc1Symbols.GameStateVa) != Dc1Symbols.GameStateGameplay)
            return new TickResult(false, Array.Empty<long>(), Array.Empty<MemWrite>(), appliedThrough, false);

        // checks
        var newIds = new List<long>();
        Span<byte> g7 = stackalloc byte[Dc1Symbols.Group7BankBytes];
        if (_mem.Read(Dc1Symbols.Group7BankVa, g7))
        {
            foreach (var name in _tracker.Checked(g7))
            {
                long id = _apIdByName[name];
                if (!alreadySent.Contains(id)) newIds.Add(id);
            }
        }

        // grants
        IReadOnlyList<MemWrite> writes = Array.Empty<MemWrite>();
        int applied = appliedThrough;
        var g11 = new byte[Dc1Symbols.Group11BankBytes];
        var supply = new byte[Dc1Symbols.SupplySlotCount * 4];
        byte? cap = _mem.ReadU8(Dc1Symbols.SupplyCapacityVa);
        if (cap is > 0 && _mem.Read(Dc1Symbols.Group11BankVa, g11) && _mem.Read(Dc1Symbols.SupplyArrayVa, supply))
        {
            var snapshot = new Dc1InventorySnapshot(g11, supply, cap.Value);
            (writes, applied) = Dc1GrantPlanner.Plan(received, appliedThrough, snapshot, MaxStackOf);
        }

        bool goal = !_goalSent && _mem.ReadU16(Dc1Symbols.CurrentRoomVa) == _goalRoomWord;
        if (goal) _goalSent = true;

        return new TickResult(true, newIds, writes, applied, goal);
    }

    /// <summary>Per-id max stack from the live item property table (0x64EFC0+id*12+1).</summary>
    private byte? MaxStackOf(int id) =>
        _mem.ReadU8(Dc1Symbols.ItemPropertyTableVa + (uint)(id * Dc1Symbols.ItemPropertyStride) + 1);

    /// <summary>Apply planned grant writes; false if any write failed (retry next tick).</summary>
    public bool Apply(IReadOnlyList<MemWrite> writes)
    {
        bool ok = true;
        foreach (var w in writes)
            ok &= _mem.Write(w.Va, w.Bytes);
        return ok;
    }
}
