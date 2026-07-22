using DinoRand.ApClient;
using DinoRand.FileFormats.Stage;
using DinoRand.Randomizer.Definitions;
using DinoRand.Randomizer.Ap;
using DinoRand.Randomizer.Install;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// AP runtime client, DC1 v1 (docs/decisions/cross/AP-CLIENT-PLAN.md): predicate evaluator,
/// grant planner and the slot_data→record-patch translation against synthetic memory
/// snapshots; the placement installer against the real install (env-gated, skips on CI).
/// </summary>
public class ApClientTests
{
    [Fact]
    public void Dc1Runner_RejectsObsoleteLogicBeforeInstallation_WithUpgradeGuidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Dc1ApRunner.ValidateLogicVersion(2));
        Assert.Contains("regenerate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("v3", ex.Message, StringComparison.OrdinalIgnoreCase);
        Dc1ApRunner.ValidateLogicVersion(3);
    }

    // ---- embedded checks contract -------------------------------------------------------

    [Fact]
    public void EmbeddedChecks_Load_154Locations_UniqueApIds_UniqueNonExcludedFlags()
    {
        var checks = Dc1ClientChecks.LoadEmbedded();
        Assert.Equal(154, checks.Locations.Count);
        Assert.Equal(154, checks.Locations.Select(l => l.ApId).Distinct().Count());

        var seen = new Dictionary<int, string>();
        foreach (var loc in checks.Locations.Where(l => !l.Excluded))
        {
            Assert.NotEmpty(loc.Predicate.AnyOf);
            foreach (int f in loc.Predicate.AnyOf)
            {
                Assert.InRange(f, 1, 255);
                Assert.False(seen.ContainsKey(f), $"flag 7:{f} shared by {seen.GetValueOrDefault(f)} and {loc.Name}");
                seen[f] = loc.Name;
            }
            // installer plan coherence: every record writes exactly the predicate's flags
            Assert.All(loc.Records, r => Assert.Contains(r.Take, loc.Predicate.AnyOf));
        }
    }

    [Fact]
    public void EmbeddedChecks_ExcludedLocations_AreTheSharedOrPoisonedTail()
    {
        var checks = Dc1ClientChecks.LoadEmbedded();
        var excluded = checks.Locations.Where(l => l.Excluded).ToList();
        Assert.Equal(12, excluded.Count);
        Assert.All(excluded, l => Assert.True(l.Class is "pinned-shared" or "poisoned", l.Class));
    }

    // ---- predicate evaluator ------------------------------------------------------------

    [Fact]
    public void CheckTracker_FiresOnAnySetFlag_AndMatchesEngineBitAddressing()
    {
        var bank = new byte[32];
        Assert.False(Dc1CheckTracker.IsBitSet(bank, 137));
        bank[137 >> 3] |= 1 << (137 & 7); // engine: bit idx&31 of dword idx>>5 == this byte form
        Assert.True(Dc1CheckTracker.IsBitSet(bank, 137));
        Assert.False(Dc1CheckTracker.IsBitSet(bank, 136));
        Assert.False(Dc1CheckTracker.IsBitSet(bank, 300)); // out of bank

        var checks = Dc1ClientChecks.LoadEmbedded();
        var tracker = new Dc1CheckTracker(checks);
        var target = checks.Locations.First(l => !l.Excluded);
        var bank2 = new byte[32];
        int flag = target.Predicate.AnyOf[0];
        bank2[flag >> 3] |= (byte)(1 << (flag & 7));
        var hit = tracker.Checked(bank2);
        Assert.Contains(target.Name, hit);
        // uniqueness: only shared-group (excluded) members may co-fire on one flag
        Assert.All(hit.Where(n => n != target.Name),
            n => Assert.True(checks.Locations.First(l => l.Name == n).Excluded));
    }

    // ---- grant planner ------------------------------------------------------------------

    private static Dc1InventorySnapshot EmptySnapshot() =>
        new(new byte[32], new byte[40], 10);

    [Fact]
    public void GrantPlanner_KeyItem_AssertsOwnershipBit_Idempotently()
    {
        var items = new[] { new ReceivedGameItem(0, 0x3A, FromOwnSlot: false) }; // Key Card Lv. A
        var (writes, applied) = Dc1GrantPlanner.Plan(items, -1, EmptySnapshot());

        var w = Assert.Single(writes);
        Assert.Equal(Dc1Symbols.Group11BankVa + (0x3A >> 3), w.Va);
        Assert.Equal(1 << (0x3A & 7), w.Bytes[0]);
        Assert.Equal(0, applied);

        // already-owned: no write, still idempotent
        var bank = new byte[32];
        bank[0x3A >> 3] = (byte)(1 << (0x3A & 7));
        (writes, _) = Dc1GrantPlanner.Plan(items, 0, new Dc1InventorySnapshot(bank, new byte[40], 10));
        Assert.Empty(writes);
    }

    [Fact]
    public void GrantPlanner_OwnWorldItems_AreNeverReappliedAsConsumables()
    {
        // The engine granted this at pickup (AddItem ran in-process) — only the index advances.
        var items = new[] { new ReceivedGameItem(0, 0x18, FromOwnSlot: true) };
        var (writes, applied) = Dc1GrantPlanner.Plan(items, -1, EmptySnapshot());
        Assert.Empty(writes);
        Assert.Equal(0, applied);
    }

    [Fact]
    public void GrantPlanner_Consumable_AppendsLikeAddItem_FreeSlotIsQtyZero()
    {
        // slot 0 occupied by 3× An. Darts S (id 0x12, class 1); id byte of slot 1 left dirty
        // with qty 0 — AddItem 0x445048 treats qty==0 as free regardless of the id byte.
        var supply = new byte[40];
        supply[0] = 0x12; supply[1] = 3; supply[2] = 1;
        supply[4] = 0x77; supply[5] = 0;
        var snap = new Dc1InventorySnapshot(new byte[32], supply, 10);

        var items = new[] { new ReceivedGameItem(0, 0x1D, FromOwnSlot: false) }; // Med Pak M
        var (writes, applied) = Dc1GrantPlanner.Plan(items, -1, snap);

        var w = Assert.Single(writes);
        Assert.Equal(Dc1Symbols.SupplyArrayVa + 4, w.Va);
        Assert.Equal(new byte[] { 0x1D, 1, Dc1Symbols.SupplyClassOf(0x1D), 0 }, w.Bytes);
        Assert.Equal(0, applied);
    }

    [Fact]
    public void GrantPlanner_Consumable_StacksOnIdAndClassMatch_UpToPerIdMax()
    {
        var supply = new byte[40];
        supply[0] = 0x16; supply[1] = 15; supply[2] = 1; // 15× 9mm Parabellum
        var snap = new Dc1InventorySnapshot(new byte[32], supply, 10);
        var items = new[] { new ReceivedGameItem(0, 0x16, FromOwnSlot: false) };

        var (writes, _) = Dc1GrantPlanner.Plan(items, -1, snap, maxStackOf: _ => 30);
        var w = Assert.Single(writes);
        Assert.Equal(Dc1Symbols.SupplyArrayVa, w.Va);
        Assert.Equal(16, w.Bytes[1]);

        // at the per-id max the slot cannot stack → falls to a free slot
        supply[1] = 30;
        (writes, _) = Dc1GrantPlanner.Plan(items, -1,
            new Dc1InventorySnapshot(new byte[32], supply, 10), maxStackOf: _ => 30);
        w = Assert.Single(writes);
        Assert.Equal(Dc1Symbols.SupplyArrayVa + 4, w.Va);
        Assert.Equal(1, w.Bytes[1]);
    }

    [Fact]
    public void GrantPlanner_FullArray_QueuesAndDoesNotAdvanceIndex()
    {
        var supply = new byte[40];
        for (int s = 0; s < 10; s++) { supply[s * 4] = (byte)(0x20); supply[s * 4 + 1] = 99; }
        var snap = new Dc1InventorySnapshot(new byte[32], supply, 10);
        var items = new[]
        {
            new ReceivedGameItem(0, 0x1D, FromOwnSlot: false), // stuck — array full
            new ReceivedGameItem(1, 0x3A, FromOwnSlot: false), // key item, but ordered AFTER the stuck one
        };
        var (writes, applied) = Dc1GrantPlanner.Plan(items, -1, snap, maxStackOf: _ => 99);
        Assert.Equal(-1, applied);                       // retry next poll from the start
        var w = Assert.Single(writes);                   // the key assert still lands (state-based)
        Assert.Equal(Dc1Symbols.Group11BankVa + (0x3A >> 3), w.Va);
    }

    [Fact]
    public void GrantPlanner_AppliedThrough_SkipsAlreadyAppliedIndices()
    {
        var items = new[]
        {
            new ReceivedGameItem(0, 0x1D, FromOwnSlot: false),
            new ReceivedGameItem(1, 0x1D, FromOwnSlot: false),
        };
        // index 0 already applied — only one more Med Pak lands
        var (writes, applied) = Dc1GrantPlanner.Plan(items, 0, EmptySnapshot());
        var w = Assert.Single(writes);
        Assert.Equal(1, w.Bytes[1]);
        Assert.Equal(1, applied);
    }

    // ---- poll engine against a synthetic memory snapshot --------------------------------

    private sealed class FakeMemory : IGameMemory
    {
        public readonly Dictionary<uint, byte> Bytes = new();
        public bool IsAttached => true;

        public bool Read(uint va, Span<byte> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = Bytes.GetValueOrDefault((uint)(va + i));
            return true;
        }

        public bool Write(uint va, ReadOnlySpan<byte> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                Bytes[(uint)(va + i)] = buffer[i];
            return true;
        }

        public void SetU32(uint va, uint v) { var b = BitConverter.GetBytes(v); Write(va, b); }
        public void SetU16(uint va, ushort v) { var b = BitConverter.GetBytes(v); Write(va, b); }
    }

    [Fact]
    public void PollEngine_GatesEverythingOnGameplayState()
    {
        var mem = new FakeMemory();
        mem.SetU32(Dc1Symbols.GameStateVa, 6); // room transition — hard gate closed
        var engine = new Dc1PollEngine(mem, Dc1ClientChecks.LoadEmbedded(), "060d");
        var tick = engine.Tick(new[] { new ReceivedGameItem(0, 0x3A, false) }, -1, new HashSet<long>());
        Assert.False(tick.InGameplay);
        Assert.Empty(tick.NewCheckIds);
        Assert.Empty(tick.GrantWrites);
        Assert.False(tick.GoalReached);
    }

    [Fact]
    public void PollEngine_ReportsCheck_Grant_AndGoal_InGameplay()
    {
        var checks = Dc1ClientChecks.LoadEmbedded();
        var target = checks.Locations.First(l => !l.Excluded);
        var mem = new FakeMemory();
        mem.SetU32(Dc1Symbols.GameStateVa, Dc1Symbols.GameStateGameplay);
        mem.SetU16(Dc1Symbols.CurrentRoomVa, 0x060D);
        mem.Bytes[Dc1Symbols.SupplyCapacityVa] = 10;
        int flag = target.Predicate.AnyOf[0];
        mem.Bytes[Dc1Symbols.Group7BankVa + (uint)(flag >> 3)] = (byte)(1 << (flag & 7));

        var engine = new Dc1PollEngine(mem, checks, "060d");
        var tick = engine.Tick(new[] { new ReceivedGameItem(0, 0x3A, false) }, -1, new HashSet<long>());

        Assert.True(tick.InGameplay);
        Assert.Contains(target.ApId, tick.NewCheckIds);
        Assert.True(tick.GoalReached);
        Assert.NotEmpty(tick.GrantWrites);
        Assert.True(engine.Apply(tick.GrantWrites));
        Assert.NotEqual(0, mem.Bytes.GetValueOrDefault(Dc1Symbols.Group11BankVa + (0x3A >> 3)));

        // second tick: check already sent, goal already reported → quiet
        var tick2 = engine.Tick(new[] { new ReceivedGameItem(0, 0x3A, false) }, 0,
            new HashSet<long> { target.ApId });
        Assert.Empty(tick2.NewCheckIds);
        Assert.False(tick2.GoalReached);
        Assert.Empty(tick2.GrantWrites);
    }

    // ---- placement installer against the real install (env-gated; CI skips) -------------

    [Fact]
    public void RealInstall_ApPlacementInstaller_WritesIdAndTakeIndex()
    {
        var root = Environment.GetEnvironmentVariable("DINORAND_DC1_DIR");
        if (string.IsNullOrEmpty(root)) return; // install-gated: CI skips
        var dataDir = new DinoCrisis1().GetDataDir(root);
        if (dataDir is null) return;

        var checks = Dc1ClientChecks.LoadEmbedded();
        // a rekeyed location proves both the id write and a real take rewrite in one shot
        var target = checks.Locations.First(l => l.Class == "rekeyed");
        var rec = target.Records[0];
        var patches = new[]
        {
            new ApPlacementInstaller.RecordPatch(
                rec.Room,
                rec.RecOffset,
                new Dc1ItemRecordClass(
                    checked((byte)rec.ExpectedOpcode),
                    checked((byte)rec.ExpectedSubtype),
                    rec.ExpectedLength),
                rec.VanillaItemId,
                rec.VanillaAmount,
                checked((ushort)rec.VanillaTake),
                Dc1Symbols.OtherWorldMarkerItemId,
                1,
                checked((ushort)rec.Take),
                Visual: null),
        };

        string outDir = Path.Combine(Path.GetTempPath(), $"dinorand_ap_test_{Guid.NewGuid():N}");
        try
        {
            var result = ApPlacementInstaller.WriteRooms(dataDir, outDir, patches);
            Assert.Equal(1, result.RoomsWritten);

            int stage = Convert.ToInt32(target.Room[..2], 16);
            int room = Convert.ToInt32(target.Room[2..], 16);
            var written = RoomFile.ReadFromFile(stage, room,
                Path.Combine(outDir, $"st{stage:x}{room:x2}.dat"));
            var item = written.Items.Single(i => i.FileOffset == rec.RecOffset);
            Assert.Equal(Dc1Symbols.OtherWorldMarkerItemId, item.ItemId);
            Assert.Equal((ushort)rec.Take, item.TakeIndex);
            Assert.NotEqual((ushort)rec.VanillaTake, item.TakeIndex); // it really was a rekey
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    // ---- slot_data parsing (ApSession.ParseIntMap — the only nontrivial logic the session
    // wrapper owns; everything else is the library's contract) ---------------------------------

    [Fact]
    public void ParseIntMap_ParsesStringKeyedIds_IncludingMarkerValues()
    {
        var slotData = new Dictionary<string, object>
        {
            ["placements"] = Newtonsoft.Json.Linq.JObject.Parse(
                """{"230817792": 48, "230817793": -1, "230817794": 120}"""),
        };
        var map = ApSession.ParseIntMap(slotData, "placements");
        Assert.Equal(3, map.Count);
        Assert.Equal(48, map[230817792]);
        Assert.Equal(ApSession.OtherWorldMarker, map[230817793]);
        Assert.Equal(120, map[230817794]);
    }

    [Fact]
    public void ParseIntMap_MissingKey_FailsWithRegenerateGuidance()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ApSession.ParseIntMap(new Dictionary<string, object>(), "placements"));
        Assert.Contains("pre-client apworld", ex.Message); // points the user at the actual fix
    }

    [Fact]
    public void ParseIntMap_WrongShape_FailsInsteadOfMisreading()
    {
        // A pre-contract-v2 room could carry any shape here — never coerce, always refuse.
        var slotData = new Dictionary<string, object> { ["placements"] = "not a map" };
        Assert.Throws<InvalidOperationException>(() => ApSession.ParseIntMap(slotData, "placements"));
    }

    // ---- crash-safe state file (GUI-SHUTDOWN-AND-CANCEL-PLAN.md S5) --------------------------
    // A torn state file fails LoadOrNew's parse and resets AppliedThrough to -1, which re-grants
    // already-delivered consumables. So the write must swap atomically, never truncate in place.

    [Fact]
    public void SlotState_Save_RoundTripsAndLeavesNoTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dinorand_apstate_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "ap_state_Regina.json");
        try
        {
            var state = new ApSlotState { SeedName = "SEED", SlotName = "Regina", AppliedThrough = 7 };
            state.SentLocationIds.Add(230817792);
            state.Save(path);                        // first write: no existing target
            state.AppliedThrough = 9;
            state.Save(path);                        // overwrite: the File.Replace path

            var loaded = ApSlotState.LoadOrNew(path, "SEED", "Regina");
            Assert.Equal(9, loaded.AppliedThrough);
            Assert.Equal(new List<long> { 230817792 }, loaded.SentLocationIds);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));   // the swap cleaned up after itself
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SlotState_Save_NeverTouchesTheCommittedFileUntilTheNewOneIsFullyWritten()
    {
        // The committed file must only ever be replaced by a COMPLETE one. Proven by making the
        // staging write fail (a directory squats on the temp path): the save throws, and the
        // previously-committed state is still there and still parses. A naive
        // File.WriteAllText(path, …) would instead have overwritten the real file in place — it
        // would not throw here at all, so this test discriminates the two implementations.
        var dir = Path.Combine(Path.GetTempPath(), "dinorand_apstate_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "ap_state_Regina.json");
        try
        {
            new ApSlotState { SeedName = "SEED", SlotName = "Regina", AppliedThrough = 4 }.Save(path);
            Directory.CreateDirectory(path + ".tmp");   // staging write can no longer succeed

            var next = new ApSlotState { SeedName = "SEED", SlotName = "Regina", AppliedThrough = 99 };
            // IOException or UnauthorizedAccessException depending on the platform — the load-bearing
            // part is that it fails LOUDLY instead of silently overwriting the committed file.
            Assert.ThrowsAny<SystemException>(() => next.Save(path));

            var loaded = ApSlotState.LoadOrNew(path, "SEED", "Regina");
            Assert.Equal(4, loaded.AppliedThrough);   // NOT 99, and NOT -1 → no consumable re-grant
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
