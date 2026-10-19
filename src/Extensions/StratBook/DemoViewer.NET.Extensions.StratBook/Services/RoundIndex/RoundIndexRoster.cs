#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

/// <summary>A slot's player as its last <c>player_connect</c> or <c>player_disconnect</c> named it.</summary>
/// <param name="SteamId64">The event's <c>xuid</c>; 0 for a bot.</param>
/// <param name="Name">The event's player name.</param>
public readonly record struct RosterEntry(ulong SteamId64, string Name);

/// <summary>
///     Who held each slot by the connect and disconnect events. The parse's roster is the final one and drops a
///     player whose slot frees, so a player who left mid-match is named from here instead.
/// </summary>
public static class RoundIndexRoster
{
    /// <summary>The two events read.</summary>
    public static IReadOnlyList<string> Events { get; } = ["player_connect", "player_disconnect"];

    /// <summary>The slots of a held parse's connect and disconnect events, the last one per slot.</summary>
    public static IReadOnlyDictionary<int, RosterEntry> Of(IEnumerable<GameEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        Dictionary<int, RosterEntry> slots = [];
        foreach (GameEvent evt in events)
        {
            Fold(slots, evt);
        }

        return slots;
    }

    /// <summary>Folds one event into <paramref name="slots" />; any other event is ignored.</summary>
    public static void Fold(Dictionary<int, RosterEntry> slots, GameEvent evt)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt.Payload)
        {
            case PlayerConnectEvent connect:
                slots[connect.UserId] = new RosterEntry(connect.Xuid, connect.Name);
                break;
            case PlayerDisconnectEvent disconnect:
                slots[disconnect.UserId] = new RosterEntry(disconnect.Xuid, disconnect.Name);
                break;
        }
    }
}

/// <summary>The round index pass's collector: <see cref="RoundIndexRoster" /> off the visit's rules run.</summary>
public sealed class RoundIndexRosterCollector : IFrameCollector
{
    /// <summary>The collector's id.</summary>
    public const string CollectorId = "net.demoviewer.pack.stratbook.roundindex.roster";

    /// <summary>Bumped when what the collector puts changes; part of the round index fingerprint.</summary>
    public const int Version = 1;

    private readonly Dictionary<int, RosterEntry> _slots = [];

    /// <summary>The registration: no providers or classes, the two events.</summary>
    public static CollectorContribution Contribution { get; } =
        new(CollectorId, RoundIndexEvaluator.EvaluatorId, Version, [], [], () => new RoundIndexRosterCollector())
        {
            GameEvents = RoundIndexRoster.Events
        };

    /// <inheritdoc />
    public void OnFrame(IFrameReadView view)
    {
        foreach (NetMessage message in view.Frame.DecodedMessages)
        {
            if (message is GameEventMessage { DecodedEvent: { } evt })
            {
                RoundIndexRoster.Fold(_slots, evt);
            }
        }
    }

    /// <inheritdoc />
    public void Finish(ICollectorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Put(new Collected(_slots));
    }

    /// <summary>What the collector puts.</summary>
    /// <param name="Slots">The last connect or disconnect per slot.</param>
    public sealed record Collected(IReadOnlyDictionary<int, RosterEntry> Slots);
}
