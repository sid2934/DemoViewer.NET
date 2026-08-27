#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Hud;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Frames;

/// <summary>
///     A scene-frame source backed by a <b>private</b> checkpoint-replay tracker over a parsed demo.
///     <para>
///         <b>Private is the contract, not an implementation detail</b> (design §5.7: "export never
///         touches the shared app clock"). This type builds its own <see cref="EntitySeekService" /> over
///         <c>() =&gt; new EntityTracker()</c>; it never uses <c>MainViewModel.CreateTracker</c> (which
///         wires the interactive Tier-3 debugger and UI dispatch) and never publishes its tracker through
///         <c>PlaybackController.PublishTracker</c>. A CLI render, a bench run and an export can therefore
///         all run while the app is playing, without perturbing it.
///     </para>
///     <para>
///         <b>Sequential access is O(1).</b> Frames are stepped with
///         <see cref="EntityTracker.AdvanceOneFrame" />; only a rewind pays a from-zero re-seed, and
///         <c>throwOnNonSequentialAccess</c> turns that into a failure for callers (tests, the export
///         session) that must not silently take a 100× cost.
///     </para>
/// </summary>
public sealed class TrackerFrameSource : ISceneFrameSource, IPreparableFrameSource, IDisposable
{
    private readonly SceneFrameBuilder _builder;
    private readonly Func<EntityTracker> _createTracker;
    private readonly int _endFrame;
    private readonly int _fps;
    private readonly IReadOnlyList<DemoFrame> _frames;
    private readonly TrackerSceneSnapshot _snapshot = new();
    private readonly double _speed;
    private readonly int _startTick;
    private readonly bool _throwOnNonSequentialAccess;
    private readonly int _tickRate;

    private int _cursor = -1;
    private int[] _demoIndexByFrame = [];
    private bool _disposed;
    private EntityTracker? _tracker;

    /// <summary>Creates a source over a parsed demo's frame list.</summary>
    /// <param name="frames">The immutable post-parse frame list; read-only, shared safely.</param>
    /// <param name="builder">Turns tracker state into a <see cref="Scene2DFrame" />. Owned by the caller.</param>
    /// <param name="startFrame">First demo frame; seeded via <c>SeekToFrameNoSnapshot</c>.</param>
    /// <param name="endFrame">Inclusive last demo frame.</param>
    /// <param name="fps">Output frame rate. With <paramref name="speed" /> it fixes
    ///     <c>SceneTime.DeltaSeconds = speed / fps</c> (design §5.1 determinism).</param>
    /// <param name="speed">Playback-rate multiplier; 1 is realtime.</param>
    /// <param name="tickRate">The demo's tick rate; values ≤ 0 are treated as 64.</param>
    /// <param name="createTracker">Defaults to <c>() =&gt; new EntityTracker()</c>.
    ///     NEVER <c>MainViewModel.CreateTracker</c>.</param>
    /// <param name="throwOnNonSequentialAccess">true in tests: a non-monotonic caller fails instead of
    ///     silently paying a re-seed per frame.</param>
    public TrackerFrameSource(IReadOnlyList<DemoFrame> frames, SceneFrameBuilder builder,
        int startFrame, int endFrame, int fps, double speed, int tickRate,
        Func<EntityTracker>? createTracker = null, bool throwOnNonSequentialAccess = false)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speed);

        if (frames.Count == 0)
        {
            throw new ArgumentException("The demo has no frames.", nameof(frames));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(startFrame, frames.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(endFrame, startFrame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(endFrame, frames.Count);

        _frames = frames;
        _builder = builder;
        StartFrame = startFrame;
        _endFrame = endFrame;
        _fps = fps;
        _speed = speed;
        _tickRate = tickRate > 0 ? tickRate : 64;
        _createTracker = createTracker ?? (static () => new EntityTracker());
        _throwOnNonSequentialAccess = throwOnNonSequentialAccess;
        _startTick = frames[startFrame].ServerTick;

        FrameCount = OutputFrameCount(frames, startFrame, endFrame, fps, speed, _tickRate);
    }

    /// <summary>
    ///     How many output frames a demo range produces at a given rate — the same arithmetic the
    ///     constructor uses, exposed so a caller can size an <c>ExportRequest</c> without building a
    ///     source first. A dialog that computed its own frame count would eventually disagree with the
    ///     source, and the disagreement would show up as a GIF cap that refuses one length and encodes
    ///     another.
    /// </summary>
    /// <param name="frames">The parsed frame list.</param>
    /// <param name="startFrame">First demo frame, inclusive.</param>
    /// <param name="endFrame">Last demo frame, inclusive.</param>
    /// <param name="fps">Output frame rate.</param>
    /// <param name="speed">Playback-rate multiplier.</param>
    /// <param name="tickRate">Demo tick rate; values ≤ 0 are treated as 64.</param>
    public static int OutputFrameCount(IReadOnlyList<DemoFrame> frames, int startFrame, int endFrame,
        int fps, double speed, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speed);

        if (frames.Count == 0 || startFrame < 0 || endFrame < startFrame || endFrame >= frames.Count)
        {
            return 0;
        }

        int rate = tickRate > 0 ? tickRate : 64;
        double ticksPerOutputFrame = speed * rate / fps;
        long tickSpan = frames[endFrame].ServerTick - frames[startFrame].ServerTick;
        return tickSpan <= 0 || ticksPerOutputFrame <= 0
            ? 1
            : 1 + (int)Math.Floor(tickSpan / ticksPerOutputFrame);
    }

    /// <summary>The number of output frames this source produces.</summary>
    public int FrameCount { get; }

    /// <summary>The demo frame index output frame 0 lands on.</summary>
    public int StartFrame { get; }

    /// <summary>True once <see cref="Prepare" /> has seeded the tracker.</summary>
    public bool IsPrepared => _tracker is not null;

    /// <inheritdoc />
    public bool NeedsPreparation => _tracker is null;

    /// <summary>Drops the private tracker. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tracker = null;
        _demoIndexByFrame = [];
    }

    /// <summary>
    ///     The one-time from-zero replay to <see cref="StartFrame" />, plus the output-frame → demo-frame
    ///     index map. Blocking and CPU-bound; call it off the UI thread.
    /// </summary>
    /// <param name="ct">Cancels the replay between frames.</param>
    public void Prepare(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_tracker is not null)
        {
            return;
        }

        BuildIndexMap(ct);

        ct.ThrowIfCancellationRequested();
        SeekResult seek = new EntitySeekService(_createTracker).SeekToFrameNoSnapshot(StartFrame, _frames);
        _tracker = seek.Tracker;
        _cursor = StartFrame;
    }

    /// <summary>The injected clock for one output frame.</summary>
    /// <param name="frameIndex">Source-relative output frame index, 0-based.</param>
    public SceneTime TimeAt(int frameIndex)
    {
        int demoIndex = DemoFrameIndexOf(frameIndex);
        int tick = _frames[demoIndex].ServerTick;
        return new SceneTime(
            tick,
            demoIndex,
            (tick - _startTick) / (double)_tickRate,
            _speed / _fps,
            frameIndex == 0);
    }

    /// <summary>
    ///     The scene at one output frame. Sequential access is O(1); a rewind re-seeds from zero unless
    ///     the source was built with <c>throwOnNonSequentialAccess</c>.
    /// </summary>
    /// <param name="frameIndex">Source-relative output frame index, 0-based.</param>
    /// <exception cref="InvalidOperationException">A rewind on a strict source.</exception>
    public Scene2DFrame FrameAt(int frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int demoIndex = DemoFrameIndexOf(frameIndex);

        if (_tracker is null)
        {
            Prepare(CancellationToken.None);
        }

        if (demoIndex < _cursor)
        {
            if (_throwOnNonSequentialAccess)
            {
                throw new InvalidOperationException(
                    $"non-sequential access: output frame {frameIndex} maps to demo frame {demoIndex}, " +
                    $"which is behind the cursor at {_cursor}. A rewind costs a full re-seed.");
            }

            SeekResult seek = new EntitySeekService(_createTracker).SeekToFrameNoSnapshot(demoIndex, _frames);
            _tracker = seek.Tracker;
            _cursor = demoIndex;
            _builder.Reset();
        }

        EntityTracker tracker = _tracker!;
        while (_cursor < demoIndex)
        {
            tracker.AdvanceOneFrame(_frames[++_cursor]);
        }

        _snapshot.Refresh(tracker);

        SceneFrameInput input = new()
        {
            Players = _snapshot.Players,
            Entities = _snapshot.Entities,
            FrameIndex = demoIndex,
            Tick = _frames[demoIndex].ServerTick,
            TickRate = _tickRate,
            CurtimeSeconds = _frames[demoIndex].ServerTick / (double)_tickRate,
            LabelForSlot = _snapshot.LabelForSlot,
            SteamIdForSlot = _snapshot.SteamIdForSlot,
            MapName = MapName,
            Radars = Radars
        };

        Scene2DFrame built = _builder.Build(in input);
        LastGameInfo = built.GameInfo;
        LastRoster = _builder.LastRoster;
        return built;
    }

    /// <summary>
    ///     The round/score state of the frame <see cref="FrameAt" /> built most recently, or
    ///     <see cref="SceneGameInfo.Empty" /> before the first one.
    ///     <para>
    ///         It exists because a HUD clock is a <b>function of tick</b>
    ///         (<c>IHudDataSource</c>), and an export's only reader of game rules is this source: a front
    ///         end that closed over its own live frame instead would burn a frozen scoreboard into the
    ///         video (the app), or none at all (the CLI). Both did.
    ///     </para>
    ///     <para>
    ///         <b>Reading it from a clock delegate is ordered correctly by construction.</b>
    ///         <c>SceneExportSession.RunAsync</c> is strictly <c>TimeAt</c> → <c>FrameAt</c> →
    ///         <c>Advance</c> → <c>Render</c> for each output frame, and <c>ClockLayer</c> asks its data
    ///         source during <c>Advance</c> — so the last frame built here is always the frame being
    ///         drawn. A caller that renders out of that order gets the previous frame's scoreboard, which
    ///         is why this is a property on the source rather than a hidden global.
    ///     </para>
    /// </summary>
    public SceneGameInfo LastGameInfo { get; private set; } = SceneGameInfo.Empty;

    /// <summary>
    ///     The player cards of the frame <see cref="FrameAt" /> built most recently, or empty before the
    ///     first one — <c>hud.roster</c>'s half of what <see cref="LastGameInfo" /> is to <c>hud.clock</c>.
    ///     <para>
    ///         Ordered correctly for the same reason as <see cref="LastGameInfo" />: a HUD layer is a
    ///         function of tick and asks its data source during <c>Advance</c>. Wire it as the roster
    ///         half of a <c>TimelineHudDataSource</c>:
    ///         <c>new TimelineHudDataSource(kills, rate, _ =&gt; ClockReading.From(src.LastGameInfo),
    ///         rosterAt: _ =&gt; src.LastRoster)</c>.
    ///     </para>
    ///     <para>
    ///         <b>Borrowed, not copied</b> — it is the builder's pooled list, valid until the next
    ///         <see cref="FrameAt" /> on this source, exactly like the <c>Scene2DFrame</c> beside it.
    ///     </para>
    /// </summary>
    public IReadOnlyList<HudPlayerRow> LastRoster { get; private set; } = [];

    /// <summary>The map name stamped onto every built frame. Set before the first <see cref="FrameAt" />.</summary>
    public string? MapName { get; set; }

    /// <summary>
    ///     The decoded radar art stamped onto every built frame, from a loaded map bundle. Set before the
    ///     first <see cref="FrameAt" />; leaving it null renders the synthetic grid instead of the map
    ///     image, which is what a demo-backed render looks like with no assets on disk.
    /// </summary>
    public IReadOnlyList<MapRadarImage>? Radars { get; set; }

    /// <summary>The demo frame index one output frame maps to.</summary>
    /// <param name="frameIndex">Source-relative output frame index, 0-based.</param>
    public int DemoFrameIndexOf(int frameIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, FrameCount);

        if (_demoIndexByFrame.Length == 0)
        {
            BuildIndexMap(CancellationToken.None);
        }

        return _demoIndexByFrame[frameIndex];
    }

    /// <summary>
    ///     Binary search over <c>ServerTick</c> (monotone by construction). Returns the <b>first</b> frame
    ///     carrying the tick when one does, otherwise the last frame before it; -1 when the tick lies
    ///     outside the demo entirely.
    /// </summary>
    /// <param name="frames">The parsed frame list.</param>
    /// <param name="serverTick">The tick to resolve.</param>
    public static int FrameIndexForTick(IReadOnlyList<DemoFrame> frames, int serverTick)
    {
        ArgumentNullException.ThrowIfNull(frames);

        if (frames.Count == 0 || serverTick < frames[0].ServerTick ||
            serverTick > frames[^1].ServerTick)
        {
            return -1;
        }

        // Lower bound: the first index whose tick is >= serverTick.
        int lo = 0;
        int hi = frames.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (frames[mid].ServerTick < serverTick)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        // Exact hit → the first frame with that tick (several frames can share one). Otherwise the tick
        // falls between two frames, and the frame that was current at that instant is the one before.
        return lo < frames.Count && frames[lo].ServerTick == serverTick ? lo : lo - 1;
    }

    private void BuildIndexMap(CancellationToken ct)
    {
        int[] map = new int[FrameCount];
        double ticksPerOutputFrame = _speed * _tickRate / _fps;
        int cursor = StartFrame;

        for (int i = 0; i < FrameCount; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            double targetTick = _startTick + i * ticksPerOutputFrame;

            // Forward-only scan: the map is monotone, so the whole build is one pass over the window
            // rather than FrameCount binary searches.
            while (cursor + 1 <= _endFrame && _frames[cursor + 1].ServerTick <= targetTick)
            {
                cursor++;
            }

            map[i] = cursor;
        }

        _demoIndexByFrame = map;
    }
}
