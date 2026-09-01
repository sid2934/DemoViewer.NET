namespace DemoViewer.NET.Debugging;

/// <summary>
///     What kind of condition a breakpoint stops on.
///     Tier 1 covers user-visible navigation primitives; Tier 3 covers parser-internal
///     state for diagnosing entity-decode failures (S9-style).
/// </summary>
public enum BreakpointKind
{
    // ── Tier 1: user-visible navigation ──
    /// <summary>Break when about to display the demo frame with this exact frame number.</summary>
    FrameNumber,

    /// <summary>Break at the first frame whose ServerTick equals this value.</summary>
    TickNumber,

    /// <summary>Break at any frame containing a game event with this name (e.g. "player_death").</summary>
    GameEventName,

    /// <summary>Break at every round_start, round_end, or both depending on payload.</summary>
    RoundTransition,

    // ── Tier 3: parser internals ──
    /// <summary>Break when EntityTracker is about to process the Nth svc_PacketEntities message.</summary>
    PacketIndex,

    /// <summary>Break the first time EntityTracker.LastEntityError transitions from null → set.</summary>
    ParserDecodeError,

    /// <summary>Break each time EntityTracker.DeltaUnknownCount increments.</summary>
    DeltaOnUnknown
}

/// <summary>
///     A user-set debugger stop point. Session-only by design: breakpoints are deliberately
///     not persisted across runs ("Tier 1 + 3, session-only"). Each instance has a stable
///     <see cref="Id" /> for UI list selection.
/// </summary>
public sealed class Breakpoint
{
    private static int _nextId;

    /// <summary>One-line display string for UI lists.</summary>
    public string DisplayText => Kind switch
    {
        BreakpointKind.FrameNumber => $"Frame #{IntValue}",
        BreakpointKind.TickNumber => $"Tick {IntValue}",
        BreakpointKind.GameEventName => $"Event: {StringValue}",
        BreakpointKind.RoundTransition => "Round transition",
        BreakpointKind.PacketIndex => $"Packet #{IntValue}",
        BreakpointKind.ParserDecodeError => "First decode error",
        BreakpointKind.DeltaOnUnknown => "Delta on unknown entity",
        _ => Kind.ToString()
    };

    /// <summary>Whether this breakpoint can fire. Disabled breakpoints are kept in the list but skipped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Times this breakpoint has fired since being added.</summary>
    public int HitCount { get; set; }

    /// <summary>Id.</summary>
    public int Id { get; } = Interlocked.Increment(ref _nextId);

    /// <summary>
    ///     Numeric value attached to the breakpoint: frame number, tick number, packet index.
    ///     Zero for kinds that don't carry a number (GameEventName uses <see cref="StringValue" />).
    /// </summary>
    public int IntValue { get; init; }

    /// <summary>Kind.</summary>
    public required BreakpointKind Kind { get; init; }

    /// <summary>
    ///     String value attached to the breakpoint: game event name, etc.
    /// </summary>
    public string? StringValue { get; init; }

    /// <inheritdoc />
    public override string ToString() => DisplayText;
}
