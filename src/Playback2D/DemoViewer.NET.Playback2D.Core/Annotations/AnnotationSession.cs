#region

using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Annotations;

/// <summary>
///     The in-flight ("wet") stroke: raw samples the user is still drawing, not yet committed to the
///     document. Rendered by <c>AnnotationLayer</c> every frame while active and thrown away on commit —
///     the committed element is what the document and the picture cache see.
///     <para>
///         The sample buffer is pre-sized once and reused for the life of the session: a
///         <see cref="List{T}" /> that doubles as it grows would allocate mid-gesture, and the §6 budget
///         is zero bytes.
///     </para>
/// </summary>
public sealed class WetStroke
{
    private const int InitialCapacity = 4096;

    /// <summary>Boundary table capacity. A stroke with fifteen pauses fits without regrowing.</summary>
    private const int InitialRunCapacity = 32;

    // Where the authoring cadence gets a boundary (plan D7 §2): only where the SPEED changed, never one
    // per sample. What a viewer reads as "it is replaying me" is the pauses, and a stamp on every point
    // spends 400 near-identical deltas recording something nobody sees through a fading tail at 64 Hz.
    //
    // The test is this sample's gap against the mean gap of the run still open, on BOTH sides, so one
    // expression catches a stop and a marked acceleration alike.
    //
    // FACTOR 2 — the hand at least halved or doubled its speed; anything gentler is the smooth variation
    // inside one continuous motion, which §2 is explicit is invisible. FLOOR 32 ms — two DV frame-clock
    // ticks: two gaps closer than that quantize to the same tick offset in BuildTiming, so a boundary
    // there records a distinction the tick clock cannot express, and the ±4 ms jitter a 60 Hz event
    // stream carries would put one on every sample. Measured against synthetic strokes, the pair is
    // insensitive across floor 16–64 ms and factor 1.5–3: a continuous stroke lands on 2 boundaries and
    // a telestration with three pauses on 8, which is §2's own arithmetic.
    private const long SpeedChangeFactor = 2;
    private const long MinGapDeviationMs = 32;

    private readonly List<InkPoint> _points = new(InitialCapacity);

    // (index into _points, elapsed MILLISECONDS since the first sample). Milliseconds and not ticks:
    // the speed test above is a millisecond comparison, and quantizing on the way in would throw away
    // the resolution the pause detector runs on. The conversion happens exactly once, in BuildTiming.
    private List<CadenceMark>? _marks;
    private bool _recordCadence;
    private long _originMs;
    private long _anchorMs;
    private long _prevMs;
    private int _anchorIndex;
    private int _prevIndex;

    /// <summary>True between <see cref="Begin" /> and <see cref="Clear" />.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    ///     True while this stroke is accumulating its authoring cadence — decided at
    ///     <see cref="Begin" /> and carried from there, exactly as <see cref="Style" /> is.
    /// </summary>
    public bool IsRecordingCadence => _recordCadence;

    /// <summary>The paint the committed element will carry.</summary>
    public AnnotationStyle Style { get; private set; } = AnnotationStyle.Default;

    /// <summary>The anchor the committed element will carry.</summary>
    public SpaceRef Space { get; private set; } = new SpaceRef.World(0);

    /// <summary>
    ///     The level whose pane the gesture began on. The wet stroke is drawn ONLY there, so a drag that
    ///     wanders into the next band does not ghost onto a floor it was never drawn on.
    /// </summary>
    public MapLevelId? PaneLevelId { get; private set; }

    /// <summary>The raw samples so far, oldest first.</summary>
    public IReadOnlyList<InkPoint> Points => _points;

    /// <summary>Bumped on every change, so a consumer can skip re-deriving an unchanged outline.</summary>
    public int Version { get; private set; }

    /// <summary>Starts a stroke.</summary>
    /// <param name="style">The paint.</param>
    /// <param name="space">The anchor.</param>
    /// <param name="paneLevelId">The level whose pane the gesture began on.</param>
    /// <param name="first">The first sample.</param>
    /// <param name="recordCadence">
    ///     Whether to accumulate the authoring cadence for <see cref="EnvelopeMode.RealTime" />.
    /// </param>
    /// <param name="atMs">
    ///     The monotonic authoring clock at the first sample. Every offset is re-based here, so the
    ///     committed table starts at 0 whatever the clock's arbitrary origin happens to be.
    /// </param>
    public void Begin(in AnnotationStyle style, SpaceRef space, MapLevelId? paneLevelId, InkPoint first,
        bool recordCadence = false, long atMs = 0)
    {
        ArgumentNullException.ThrowIfNull(space);

        _points.Clear();
        _points.Add(first);
        Style = style;
        Space = space;
        PaneLevelId = paneLevelId;
        IsActive = true;
        BeginCadence(recordCadence, atMs);
        Version++;
    }

    /// <summary>Appends a sample unconditionally.</summary>
    /// <param name="point">The sample.</param>
    /// <param name="atMs">The monotonic authoring clock at this sample; ignored unless recording cadence.</param>
    public void Append(InkPoint point, long atMs = 0)
    {
        if (!IsActive)
        {
            return;
        }

        _points.Add(point);
        MarkCadence(_points.Count - 1, atMs);
        Version++;
    }

    /// <summary>
    ///     Appends a sample only when it is at least <paramref name="minDistanceWorld" /> from the last
    ///     one. Decimating on the way IN is what keeps a slow drag from storing a thousand coincident
    ///     samples that the outliner would then have to streamline away.
    /// </summary>
    /// <param name="point">The candidate sample.</param>
    /// <param name="minDistanceWorld">Minimum world-space separation.</param>
    /// <param name="atMs">The monotonic authoring clock at this sample; ignored unless recording cadence.</param>
    /// <returns>True when the sample was kept.</returns>
    public bool TryAppend(InkPoint point, float minDistanceWorld, long atMs = 0)
    {
        if (!IsActive)
        {
            return false;
        }

        if (_points.Count > 0 && minDistanceWorld > 0)
        {
            InkPoint last = _points[^1];
            float dx = point.X - last.X;
            float dy = point.Y - last.Y;
            if (dx * dx + dy * dy < minDistanceWorld * minDistanceWorld)
            {
                return false;
            }
        }

        _points.Add(point);

        // INSIDE the append, past the spacing filter, so the index in the table is an index into
        // _points. A rejected sample is one the committed element never had, and timing it would slide
        // every later boundary off the point it describes.
        MarkCadence(_points.Count - 1, atMs);
        Version++;
        return true;
    }

    /// <summary>
    ///     The cadence accumulated since <see cref="Begin" />, as the committed element's
    ///     <see cref="StrokeTiming" />. <see cref="StrokeTiming.Instant" /> when this stroke was not
    ///     recording one or ended before a second boundary existed — a tap is a dot, and a dot has no
    ///     cadence to replay.
    /// </summary>
    /// <param name="ticksPerSecond">DV frame-clock ticks per second the offsets are expressed in.</param>
    public StrokeTiming BuildTiming(int ticksPerSecond)
    {
        if (!_recordCadence || _marks is null || ticksPerSecond <= 0)
        {
            return StrokeTiming.Instant;
        }

        // The LAST sample is always a boundary: it is what DurationTicks means, and without it the
        // closing run would be extrapolated from wherever the last speed change happened to land.
        if (_marks[^1].SampleIndex != _prevIndex)
        {
            _marks.Add(new CadenceMark(_prevIndex, _prevMs));
        }

        if (_marks.Count < 2 || _prevMs <= 0)
        {
            return StrokeTiming.Instant;
        }

        TimingRun[] runs = new TimingRun[_marks.Count];
        for (int i = 0; i < runs.Length; i++)
        {
            CadenceMark mark = _marks[i];
            runs[i] = new TimingRun(mark.SampleIndex, ToTicks(mark.ElapsedMs, ticksPerSecond));
        }

        return new StrokeTiming(runs, runs[^1].TickOffset);
    }

    /// <summary>Ends the stroke and drops its samples. The capacity is kept.</summary>
    public void Clear()
    {
        if (!IsActive && _points.Count == 0)
        {
            return;
        }

        _points.Clear();
        _marks?.Clear();
        _recordCadence = false;
        IsActive = false;
        PaneLevelId = null;
        Version++;
    }

    /// <summary>
    ///     Rebases a world anchor across a level-set rebuild (plan risk S5). A rebuild that lands
    ///     mid-gesture would otherwise leave the wet stroke pointing at a level that no longer exists.
    /// </summary>
    /// <param name="zMinMap">Old quantized level ZMin → new quantized level ZMin.</param>
    public void RemapWorldLevel(IReadOnlyDictionary<double, double> zMinMap)
    {
        ArgumentNullException.ThrowIfNull(zMinMap);

        if (Space is not SpaceRef.World world || !zMinMap.TryGetValue(world.LevelMinZ, out double target))
        {
            return;
        }

        // The ANCHOR is rebased; the pane identity is NOT re-derived. A level that survives a rebuild
        // carries its id however far its band drifted (LevelSetChange.Remapped is identity by
        // construction), and a level that did not survive has no pane for the stroke to be drawn in — so
        // re-minting an id out of the new ZMin could only ever produce a WRONG one, which is exactly
        // what it did once MapSpace.Mint had bumped a colliding key.
        Space = new SpaceRef.World(target);
        Version++;
    }

    // The table is allocated on the first RealTime stroke and kept for the life of the session: a user
    // who never draws in RealTime never pays for it, and one who does never pays twice. Clearing rather
    // than re-allocating is the same discipline the sample buffer above already runs on.
    private void BeginCadence(bool record, long atMs)
    {
        _recordCadence = record;
        if (!record)
        {
            return;
        }

        _marks ??= new List<CadenceMark>(InitialRunCapacity);
        _marks.Clear();
        _marks.Add(new CadenceMark(0, 0));
        _originMs = atMs;
        _anchorIndex = 0;
        _anchorMs = 0;
        _prevIndex = 0;
        _prevMs = 0;
    }

    private void MarkCadence(int index, long atMs)
    {
        if (!_recordCadence || _marks is null)
        {
            return;
        }

        // Re-based at the press and clamped monotonic. A stamp that went backwards — an unstamped
        // Append, or a host clock that could not stay monotonic — repeats the previous instant rather
        // than inverting the table, which is StrokeTiming's own "degrade, never throw" contract.
        long elapsed = Math.Max(atMs - _originMs, _prevMs);

        int runSamples = _prevIndex - _anchorIndex;
        if (runSamples > 0)
        {
            long mean = (_prevMs - _anchorMs) / runSamples;
            long gap = elapsed - _prevMs;

            if (gap > (mean * SpeedChangeFactor) + MinGapDeviationMs
                || mean > (gap * SpeedChangeFactor) + MinGapDeviationMs)
            {
                // A PAIR, not one boundary. The run is closed BEFORE the change and re-opened AFTER it,
                // so the change itself is a single two-boundary segment that the reader interpolates
                // across exactly. Emitting only the closing half would leave the following run running
                // straight THROUGH the pause — and stalling across a pause is the entire feature.
                //
                // It is also §2's arithmetic: two entries for a continuous stroke, eight for one with
                // three pauses, which only adds up if a boundary comes in twos.
                _marks.Add(new CadenceMark(_prevIndex, _prevMs));
                _marks.Add(new CadenceMark(index, elapsed));
                _anchorIndex = index;
                _anchorMs = elapsed;
            }
        }

        _prevIndex = index;
        _prevMs = elapsed;
    }

    // Round to the NEAREST tick, not down: truncation biases every offset toward the start of the
    // stroke by up to a tick, which across a whole table reads as a replay that runs slightly fast.
    private static int ToTicks(long elapsedMs, int ticksPerSecond) =>
        (int)(((elapsedMs * ticksPerSecond) + 500) / 1000);

    /// <summary>One accumulated boundary, in the millisecond domain the speed test runs in.</summary>
    /// <param name="SampleIndex">Index into <see cref="Points" />.</param>
    /// <param name="ElapsedMs">Milliseconds since the first sample.</param>
    private readonly record struct CadenceMark(int SampleIndex, long ElapsedMs);
}

/// <summary>
///     The shared mutable seam between the pointer tools, the render layer and the UI — the "annotation
///     session" design §5.5 names in <c>IToolServices</c>. It owns the document, the wet stroke, the
///     current paint and the envelope template new elements are stamped with.
///     <para>
///         Deliberately NOT a view-model: Core has no Avalonia, and the tools have to be drivable from a
///         direct-execution test with no dispatcher.
///     </para>
/// </summary>
public sealed class AnnotationSession
{
    /// <summary>Default eraser radius in world units. About a player disc wide at fit zoom.</summary>
    public const float DefaultEraserWorldRadius = 48f;

    /// <summary>Default entity-anchor capture radius in world units.</summary>
    public const float DefaultAnchorWorldRadius = 96f;

    /// <summary>
    ///     Default SECONDARY ink: a cool blue nobody can confuse with the amber primary. Two pens whose
    ///     colours look alike would make the right button's whole point invisible.
    /// </summary>
    public const uint DefaultSecondaryColorArgb = 0xFF29B6F6;

    /// <summary>
    ///     DV frame-clock ticks per second, for turning a <see cref="EnvelopeMode.RealTime" /> stroke's
    ///     authoring milliseconds into the tick offsets it carries.
    ///     <para>
    ///         A CONSTANT rather than a reading, because on this side of the seam there is nothing to
    ///         read: <c>IToolServices</c> exposes the playhead and no rate, <c>SceneTime</c> carries a
    ///         frame delta and a <c>DemoSeconds</c> but not the divisor either was derived from, and the
    ///         two real sources — <c>ITimelineData.TickRate</c> and <c>ClockIdentity.TickRate</c> — are a
    ///         timeline concern and a Pipeline type. Every other tick quantity in this class already
    ///         assumes the same 64 (<see cref="HoldTicks" />'s "320 ≈ 5 s"), so a literal here is
    ///         consistent rather than novel. On a 128-tick parse a RealTime stroke replays at half speed;
    ///         the fix is one rate on <c>IToolServices</c>, not a second literal somewhere else.
    ///     </para>
    /// </summary>
    public const int DvTicksPerSecond = 64;

    /// <summary>Creates a session over a document.</summary>
    /// <param name="document">The document this session edits.</param>
    public AnnotationSession(AnnotationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
    }

    /// <summary>The document being edited.</summary>
    public AnnotationDocument Document { get; }

    /// <summary>The stroke currently under the pointer.</summary>
    public WetStroke Wet { get; } = new();

    /// <summary>The paint new elements are stamped with. The PRIMARY (left) button's ink.</summary>
    public AnnotationStyle Style { get; set; } = AnnotationStyle.Default;

    /// <summary>
    ///     The paint the RIGHT button stamps. Width and opacity track <see cref="Style" /> in the app —
    ///     only the colour differs — so a two-pen user never has to keep two widths in step.
    /// </summary>
    public AnnotationStyle SecondaryStyle { get; set; } =
        AnnotationStyle.Default with { ColorArgb = DefaultSecondaryColorArgb };

    /// <summary>
    ///     The tool the right button routes to, mirrored here for the UI's benefit exactly as
    ///     <see cref="ActiveTool" /> is: the panel edits the session, and the host pushes this onto
    ///     <c>InputToolRouter.SecondaryTool</c>. Null = whatever the active tool is.
    /// </summary>
    public ToolKind? SecondaryTool { get; set; }

    /// <summary>
    ///     The envelope template for new elements. Only consulted directly in
    ///     <see cref="EnvelopeMode.Custom" />; the other two modes derive one from the current tick.
    ///     Composed through <see cref="SetCustomWindow" /> rather than assigned field by field, so the
    ///     panel and the settings seed cannot disagree about what a window means.
    /// </summary>
    public TimeEnvelope NewElementEnvelope { get; set; } = TimeEnvelope.Static;

    /// <summary>How <see cref="EnvelopeForNewElement" /> builds the envelope.</summary>
    public EnvelopeMode DefaultVisibility { get; set; } = EnvelopeMode.Always;

    /// <summary>Lead-in length for <see cref="EnvelopeMode.Fade" />.</summary>
    public int FadeInTicks { get; set; } = 8;

    /// <summary>Lead-out length for <see cref="EnvelopeMode.Fade" />.</summary>
    public int FadeOutTicks { get; set; } = 16;

    /// <summary>Fully-opaque hold for <see cref="EnvelopeMode.Fade" />. 320 ≈ 5 s at 64 tick.</summary>
    public int HoldTicks { get; set; } = 320;

    /// <summary>The tool the router currently routes to. Mirrored here for the UI's benefit.</summary>
    public ToolKind ActiveTool { get; set; } = ToolKind.PanZoom;

    /// <summary>When true, a stroke started near a player anchors to that player's SteamId.</summary>
    public bool AnchorToEntities { get; set; }

    /// <summary>How close to a marker a press must be to capture an entity anchor, in world units.</summary>
    public float AnchorWorldRadius { get; set; } = DefaultAnchorWorldRadius;

    /// <summary>The eraser disc radius in world units.</summary>
    public float EraserWorldRadius { get; set; } = DefaultEraserWorldRadius;

    /// <summary>
    ///     Minimum world-space separation between kept samples, as a fraction of the stroke width. 0.35
    ///     keeps a stroke smooth while dropping the jitter a stationary pointer generates.
    /// </summary>
    public float SampleSpacingFactor { get; set; } = 0.35f;

    // WetChanged / NotifyWetChanged were DELETED here (D6 §3 dead surface). The event was raised four
    // times per stroke by DrawTool and subscribed by nothing, in production or in a test — and every one
    // of those four raises was immediately followed by the caller's own `s.RequestRender()`, so a
    // subscriber added later would have repainted a surface that had just been invalidated anyway. The
    // choice was "subscribe or delete"; adding the subscriber would have made every pointer sample
    // repaint twice, which is the opposite of what §6's budget asks for.

    /// <summary>The world-space sample spacing filter for the current style.</summary>
    public float SampleSpacingWorld => SampleSpacingFor(Style);

    /// <summary>
    ///     The world-space sample spacing filter for a given paint. A gesture filters against the ink it
    ///     is actually laying down — the right button's, when that is what took the press — not against
    ///     whatever the toolbar happens to be showing.
    /// </summary>
    /// <param name="style">The paint the stroke is being drawn with.</param>
    public float SampleSpacingFor(in AnnotationStyle style) =>
        Math.Max(0f, style.WidthWorld) * SampleSpacingFactor;

    /// <summary>
    ///     The paint a button stamps: right → <see cref="SecondaryStyle" />, everything else →
    ///     <see cref="Style" />. The one place the button→ink map lives, so a tool that forgets to ask is
    ///     a compile-time visible omission rather than ink that silently comes out the wrong colour.
    /// </summary>
    /// <param name="button">The button that took the press.</param>
    public AnnotationStyle StyleFor(ToolPointerButton button) =>
        button == ToolPointerButton.Right ? SecondaryStyle : Style;

    /// <summary>
    ///     Stamps <see cref="NewElementEnvelope" /> with an explicit tick window, taking the ramps from
    ///     the session's current <see cref="FadeInTicks" /> / <see cref="FadeOutTicks" />. The ONE
    ///     composer for <see cref="EnvelopeMode.Custom" />.
    ///     <para>
    ///         Both bounds are real ticks, never null: "open at one end" is what
    ///         <see cref="EnvelopeMode.Always" /> and <see cref="EnvelopeMode.Fade" /> already are, and a
    ///         sentinel in a spin box would cost every user clarity to serve a case neither of them
    ///         leaves uncovered. An inverted window collapses to a zero-length one rather than throwing.
    ///     </para>
    /// </summary>
    /// <param name="fromTick">First fully-opaque DV frame-clock tick.</param>
    /// <param name="untilTick">Last fully-opaque DV frame-clock tick.</param>
    public void SetCustomWindow(int fromTick, int untilTick) =>
        NewElementEnvelope = TimeEnvelope.Static.PinnedTo(
            fromTick, untilTick - fromTick, FadeInTicks, FadeOutTicks);

    /// <summary>
    ///     The envelope a new element gets, resolved against the playhead.
    ///     <para>
    ///         <paramref name="currentTick" /> is a DV FRAME-CLOCK tick, never a CS2 server tick — the
    ///         LiveSync servo bends the playhead between 0.75× and 1.5×, so a CS2 anchor would drift
    ///         against what the user was looking at when they drew.
    ///     </para>
    /// </summary>
    /// <param name="currentTick">The playhead, in DV frame-clock ticks.</param>
    public TimeEnvelope EnvelopeForNewElement(int currentTick) => DefaultVisibility switch
    {
        // RealTime is Fade's envelope, deliberately and not by omission. D7 §3 renders each SECTION
        // through this very trapezoid shifted by the offset it was drawn at, so the element-level window
        // is the one a Fade element would have had — and HoldTicks then keeps its meaning per section:
        // a hold that outlasts the draw shows the whole stroke at once and dissolves it from the start,
        // and one that does not makes the stroke chase its own tail. The same control gives both.
        EnvelopeMode.Fade or EnvelopeMode.RealTime =>
            TimeEnvelope.Static.PinnedTo(currentTick, HoldTicks, FadeInTicks, FadeOutTicks),
        EnvelopeMode.Custom => NewElementEnvelope,
        _ => TimeEnvelope.Static
    };
}
