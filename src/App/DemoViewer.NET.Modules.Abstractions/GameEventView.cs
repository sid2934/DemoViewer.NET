namespace DemoViewer.NET.Modules.Abstractions;

/// <summary>
///     A read-only game event that occurred in a frame. Read-only projection of the parser's
///     decoded game event for event-driven modules (the 2D pilot is state-based and uses entity
///     deltas instead, but this is here for modules that need events).
/// </summary>
public sealed class GameEventView
{
    /// <summary>Event name, e.g. <c>"player_death"</c>, <c>"weapon_fire"</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    ///     The GAME tick the event fired at — the same clock as the playhead / <c>IPlaybackSnapshot.Tick</c>,
    ///     so a module can window-filter events against the current position. NOT the server tick: CS2
    ///     delivers some events (e.g. player_death) a constant <c>ServerStartTick</c> offset late, which would
    ///     misalign the display from when it actually happened.
    /// </summary>
    public int Tick { get; init; }

    /// <summary>Decoded event fields by name (boxed). Empty when the event carried no fields.</summary>
    public IReadOnlyDictionary<string, object?> Fields { get; init; } =
        new Dictionary<string, object?>();
}
