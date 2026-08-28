#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Hud;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Hud;

/// <summary>
///     An <see cref="IHudDataSource" /> over a pre-built kill timeline plus a caller-supplied clock
///     function — the production implementation of "HUD state is a pure function of tick".
///     <para>
///         The clock half is a delegate rather than a second timeline because its source differs by
///         caller: the app already has <c>SceneGameInfo</c> per frame, while <c>dv2d</c> derives it from
///         the tracker. Both hand this type the same tuple, so the exported clock and the XAML clock
///         cannot disagree about what "round 13, 1:55" means. The roster half is a delegate for the same
///         reason: the production reader is one expression, <c>rosterAt: _ =&gt; src.LastRoster</c>, over
///         the export's own <c>TrackerFrameSource</c>. Left null the roster is empty and
///         <c>hud.roster</c> draws nothing, which is what a fixture render and a clock-only export both
///         want.
///     </para>
///     <para>
///         <b>One list, reused.</b> <see cref="At" /> caches its last answer by tick (design §6), so the
///         three HUD layers asking for the same frame do the windowing once and neither the window nor
///         the snapshot allocates per frame.
///     </para>
/// </summary>
public sealed class TimelineHudDataSource : IHudDataSource
{
    private readonly IReadOnlyList<KillFeedRow> _allKills;
    private readonly Func<int, ClockReading> _clockAt;
    private readonly int _maxRows;
    private readonly Func<int, IReadOnlyList<HudPlayerRow>>? _rosterAt;
    private readonly int _tickRate;
    private readonly List<KillFeedRow> _window;
    private readonly int _windowSeconds;

    private HudSnapshot _cached = HudSnapshot.Empty;
    private bool _hasCached;

    /// <summary>Creates a source.</summary>
    /// <param name="allKills">Every kill in the demo. Not copied; must not change under this type.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    /// <param name="clockAt">Round/score/countdown at a tick. Must be pure.</param>
    /// <param name="windowSeconds">How long a kill row stays visible.</param>
    /// <param name="maxRows">Row ceiling.</param>
    /// <param name="rosterAt">
    ///     Player cards at a tick, or null for no roster. Must be pure. Trailing and optional because this
    ///     type's two callers each construct it in one expression, and a required parameter would have made
    ///     "I do not draw <c>hud.roster</c>" something a caller has to say out loud.
    /// </param>
    public TimelineHudDataSource(IReadOnlyList<KillFeedRow> allKills, int tickRate,
        Func<int, ClockReading> clockAt,
        int windowSeconds = KillFeedTimeline.DefaultWindowSeconds,
        int maxRows = KillFeedTimeline.DefaultMaxRows,
        Func<int, IReadOnlyList<HudPlayerRow>>? rosterAt = null)
    {
        ArgumentNullException.ThrowIfNull(allKills);
        ArgumentNullException.ThrowIfNull(clockAt);

        _allKills = allKills;
        _tickRate = tickRate > 0 ? tickRate : 64;
        _clockAt = clockAt;
        _windowSeconds = windowSeconds;
        _maxRows = maxRows;
        _rosterAt = rosterAt;
        _window = new List<KillFeedRow>(Math.Max(4, maxRows));
    }

    /// <summary>
    ///     How many times the kill window has been rebuilt. Test hook: "the windowing is cached, the
    ///     readers are not" is the whole contract of <see cref="At" /> and there is no other way to see
    ///     the first half of it.
    /// </summary>
    internal int WindowingsForTest { get; private set; }

    /// <inheritdoc />
    public HudSnapshot At(int tick)
    {
        // The WINDOWING is cached by tick, and only the windowing. It is the expensive half and it is
        // genuinely a pure function of tick; the clock and the roster are not — they are readers over
        // whatever frame the source built most recently, and CS2 emits SEVERAL demo frames per tick, so
        // two consecutive output frames can share one tick while the state behind those readers has
        // moved on. Caching the whole snapshot by tick alone meant the second of those frames drew the
        // first one's cards and the first one's scoreboard — invisible in the app, where the roster is
        // the builder's pooled list, but not in an export: SceneFrameBuilder double-buffers, so the
        // stale snapshot holds the OTHER slot's list, still carrying the previous frame.
        //
        // Re-asking costs two delegate calls and nothing else — both readers hand back state the frame
        // source already computed, and ClockReading.From memoises the one string it formats.
        if (!_hasCached || _cached.Tick != tick)
        {
            KillFeedTimeline.Window(_allKills, tick, _tickRate, _window, _windowSeconds, _maxRows);
            WindowingsForTest++;
        }

        ClockReading clock = _clockAt(tick);

        // Borrowed straight through, never copied: the reader hands back the frame source's own pooled
        // list, whose lifetime is already the one HudSnapshot documents for KillRows.
        IReadOnlyList<HudPlayerRow> roster = _rosterAt?.Invoke(tick) ?? [];

        _cached = new HudSnapshot(tick, clock.Round, clock.TScore, clock.CtScore, clock.CountdownSeconds,
            clock.BombTicking, clock.Defusing, clock.DefuseSeconds, _window, roster);
        _hasCached = true;
        return _cached;
    }
}

/// <summary>
///     The clock half of a <see cref="HudSnapshot" />, as the caller's own state answers it. A named
///     struct rather than a tuple so the seven fields cannot be swapped at a call site.
/// </summary>
/// <param name="Round">Display text for the round number.</param>
/// <param name="TScore">T-side score.</param>
/// <param name="CtScore">CT-side score.</param>
/// <param name="CountdownSeconds">Main countdown remaining, or <c>NaN</c>.</param>
/// <param name="BombTicking">True while a live ticking C4 owns the countdown.</param>
/// <param name="Defusing">True while a defuse is under way.</param>
/// <param name="DefuseSeconds">Defuse-completion remaining, or <c>NaN</c>.</param>
public readonly record struct ClockReading(
    string Round,
    int TScore,
    int CtScore,
    double CountdownSeconds,
    bool BombTicking,
    bool Defusing,
    double DefuseSeconds)
{
    // Round numbers as strings, filled on demand. int.ToString allocates, and this runs once per HUD
    // layer per frame — three times a frame on a full export stack, since TimelineHudDataSource.At
    // re-asks its readers every time (see its own comment). A match has a few dozen rounds; the array
    // covers every one of them and overtime besides. A benign race on a slot costs two identical
    // strings, never a wrong one.
    private static readonly string[] _roundText = new string[128];

    /// <summary>The reading for a tick with no game-rules state. Renders placeholders.</summary>
    public static ClockReading Unknown { get; } = new("—", 0, 0, double.NaN, false, false, double.NaN);

    /// <summary>Projects a scene's own game info onto this shape — what the app and the CLI both do.</summary>
    /// <param name="info">The frame's game info.</param>
    public static ClockReading From(SceneGameInfo info) => new(
        RoundText(info.RoundNumber),
        info.TScore,
        info.CtScore,
        info.RoundSeconds,
        info.BombTicking,
        info.DefuseInProgress,
        info.DefuseSeconds);

    private static string RoundText(int round) => round switch
    {
        <= 0 => "—",
        < 128 => _roundText[round] ??= round.ToString(CultureInfo.InvariantCulture),
        _ => round.ToString(CultureInfo.InvariantCulture)
    };
}
