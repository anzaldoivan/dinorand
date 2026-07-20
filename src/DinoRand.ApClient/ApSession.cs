using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Newtonsoft.Json.Linq;

namespace DinoRand.ApClient;

/// <summary>Parsed connection outcome: everything the install step and poll engine need.</summary>
public sealed record ApConnection(
    string SeedName,
    int Slot,
    int LogicVersion,
    string GoalRoom,
    /// <summary>AP location id → DC1 game item id, or <see cref="ApSession.OtherWorldMarker"/>
    /// for another world's item (slot_data `placements`).</summary>
    IReadOnlyDictionary<long, int> Placements,
    /// <summary>AP item id → DC1 game item id (slot_data `item_ids`).</summary>
    IReadOnlyDictionary<long, int> ItemIdToGameId,
    IReadOnlySet<long> CheckedLocations);

/// <summary>
/// Thin wrapper over <c>Archipelago.MultiClient.Net</c> (AP-CLIENT-PLAN.md §1): game
/// "Dino Crisis 1", items_handling 0b111. Protocol behavior is the library's contract — this
/// class only adapts it to the game-id domain the rest of the client speaks.
/// </summary>
public sealed class ApSession : IDisposable
{
    public const string GameName = "Dino Crisis 1";
    /// <summary>slot_data placement value for "another world's item" (apworld OTHER_WORLD_MARKER).</summary>
    public const int OtherWorldMarker = -1;

    private ArchipelagoSession? _session;
    private ApConnection? _connection;

    public ApConnection Connection => _connection
        ?? throw new InvalidOperationException("not connected");

    /// <summary>Connect + login; throws with the server's errors on refusal.</summary>
    public ApConnection Connect(string host, int port, string slotName, string? password)
    {
        var session = ArchipelagoSessionFactory.CreateSession(host, port);
        var result = session.TryConnectAndLogin(
            GameName, slotName, ItemsHandlingFlags.AllItems, password: password);
        if (result is not LoginSuccessful ok)
        {
            var errors = result is LoginFailure fail ? string.Join("; ", fail.Errors) : result.ToString();
            throw new InvalidOperationException($"AP login failed: {errors}");
        }

        var slotData = ok.SlotData;
        int logicVersion = Convert.ToInt32(slotData["logic_version"]);
        string goalRoom = Convert.ToString(slotData["goal_room"]) ?? "060d";
        var placements = ParseIntMap(slotData, "placements");
        var itemIds = ParseIntMap(slotData, "item_ids");

        _session = session;
        _connection = new ApConnection(
            session.RoomState.Seed,
            ok.Slot,
            logicVersion,
            goalRoom,
            placements,
            itemIds,
            session.Locations.AllLocationsChecked.ToHashSet());
        return _connection;
    }

    // internal for the slot_data contract tests — the only nontrivial parsing this wrapper owns.
    internal static Dictionary<long, int> ParseIntMap(IDictionary<string, object> slotData, string key)
    {
        if (!slotData.TryGetValue(key, out var raw) || raw is not JObject obj)
            throw new InvalidOperationException(
                $"slot_data has no '{key}' — the room was generated with a pre-client apworld "
                + "(regenerate with the current dino_crisis_1.apworld)");
        return obj.Properties().ToDictionary(p => long.Parse(p.Name), p => p.Value.Value<int>());
    }

    /// <summary>The full server item list, resolved to game ids. Unknown AP ids (foreign-game
    /// ids can never appear here; a mapping gap would mean contract drift) are skipped.</summary>
    public IReadOnlyList<ReceivedGameItem> ReceivedItems()
    {
        var s = _session ?? throw new InvalidOperationException("not connected");
        int ownSlot = Connection.Slot;
        var result = new List<ReceivedGameItem>();
        var all = s.Items.AllItemsReceived;
        for (int i = 0; i < all.Count; i++)
        {
            var item = all[i];
            if (Connection.ItemIdToGameId.TryGetValue(item.ItemId, out int gameId))
                result.Add(new ReceivedGameItem(i, gameId, item.Player == ownSlot));
        }
        return result;
    }

    public void SendChecks(IEnumerable<long> locationIds)
    {
        var ids = locationIds.ToArray();
        if (ids.Length == 0) return;
        (_session ?? throw new InvalidOperationException("not connected"))
            .Locations.CompleteLocationChecks(ids);
    }

    public void SetGoalAchieved() =>
        (_session ?? throw new InvalidOperationException("not connected"))
            .SetGoalAchieved();

    public void Disconnect()
    {
        try { _session?.Socket.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); }
        catch { /* socket already gone — nothing to clean */ }
        _session = null;
        _connection = null;
    }

    public void Dispose() => Disconnect();
}
