using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;

namespace DinoRand.ApClient;

internal interface IApProtocolSessionFactory
{
    IApProtocolSession CreateSession(string host, int port);
}

internal interface IApProtocolSession
{
    IReadOnlyList<ApReceivedItem> AllItemsReceived { get; }
    ApLoginResult TryConnectAndLogin(string gameName, string slotName, string? password);
    void CompleteLocationChecks(IReadOnlyList<long> locationIds);
    void SetGoalAchieved();
    void Disconnect(TimeSpan timeout);
}

internal sealed record ApLoginResult(bool Success, string? Error, ApLoginSnapshot? Snapshot);

internal sealed record ApLoginSnapshot(
    string SeedName,
    int Slot,
    int LogicVersion,
    string? GoalRoom,
    IReadOnlyDictionary<long, int> Placements,
    IReadOnlyDictionary<long, int> ItemIdToGameId,
    IReadOnlySet<long> CheckedLocations);

internal readonly record struct ApReceivedItem(long ItemId, int Player);

internal sealed class ArchipelagoProtocolSessionFactory : IApProtocolSessionFactory
{
    public IApProtocolSession CreateSession(string host, int port) =>
        new ArchipelagoProtocolSession(ArchipelagoSessionFactory.CreateSession(host, port));
}

internal sealed class ArchipelagoProtocolSession : IApProtocolSession
{
    private readonly ArchipelagoSession _session;

    public ArchipelagoProtocolSession(ArchipelagoSession session) => _session = session;

    public IReadOnlyList<ApReceivedItem> AllItemsReceived =>
        _session.Items.AllItemsReceived
            .Select(item => new ApReceivedItem(item.ItemId, item.Player))
            .ToArray();

    public ApLoginResult TryConnectAndLogin(string gameName, string slotName, string? password)
    {
        var result = _session.TryConnectAndLogin(
            gameName, slotName, ItemsHandlingFlags.AllItems, password: password);
        if (result is not LoginSuccessful ok)
        {
            var errors = result is LoginFailure fail
                ? string.Join("; ", fail.Errors)
                : result.ToString();
            return new ApLoginResult(false, errors, null);
        }

        var slotData = ok.SlotData;
        return new ApLoginResult(
            true,
            null,
            new ApLoginSnapshot(
                _session.RoomState.Seed,
                ok.Slot,
                Convert.ToInt32(slotData["logic_version"]),
                Convert.ToString(slotData["goal_room"]),
                ApSession.ParseIntMap(slotData, "placements"),
                ApSession.ParseIntMap(slotData, "item_ids"),
                _session.Locations.AllLocationsChecked.ToHashSet()));
    }

    public void CompleteLocationChecks(IReadOnlyList<long> locationIds) =>
        _session.Locations.CompleteLocationChecks(locationIds.ToArray());

    public void SetGoalAchieved() => _session.SetGoalAchieved();

    public void Disconnect(TimeSpan timeout) => _session.Socket.DisconnectAsync().Wait(timeout);
}
