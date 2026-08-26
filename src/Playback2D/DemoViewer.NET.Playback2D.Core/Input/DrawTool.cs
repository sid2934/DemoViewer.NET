#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     Freehand ink. Press opens an undo mark, moves accumulate raw samples into the session's wet
///     stroke, and release commits ONE element — so a 400-sample stroke costs exactly one Ctrl+Z.
///     <para>
///         <b>The anchor is chosen at press time and never revisited.</b> A stroke started on a player
///         (with entity anchoring on) follows them by SteamId for the rest of the demo; otherwise it is
///         pinned to the level the pane is showing, keyed by that level's <i>quantized</i> lower Z so a
///         later floor-split rebuild still finds it (plan correction 10).
///     </para>
/// </summary>
public sealed class DrawTool : IPointerTool
{
    private IDisposable? _gesture;
    private TimeEnvelope _envelope = TimeEnvelope.Static;

    // The previous event's reading of the authoring clock — the START of the span this event's coalesced
    // batch is spread across. It lives on the tool rather than on the wet stroke because it tracks
    // EVENTS, and the wet stroke only ever sees the samples the spacing filter kept.
    private long _lastEventMs;

    /// <inheritdoc />
    public ToolKind Kind => ToolKind.Draw;

    /// <inheritdoc />
    public bool OnPressed(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (e.Pane is not { } pane)
        {
            return false;
        }

        AnnotationSession session = s.Session;
        _gesture = session.Document.BeginGesture("draw");
        _envelope = session.EnvelopeForNewElement(s.CurrentTick);
        _lastEventMs = s.NowMilliseconds;

        // The BUTTON picks the ink, and the wet stroke carries it from here on: the toolbar can change
        // colour mid-drag and the committed element still has the paint the gesture started with.
        //
        // Whether there is a CADENCE to record is captured in the same breath and for the same reason:
        // the accumulator has to start at the first sample, so a toolbar flip mid-drag must not get to
        // decide retroactively whether this stroke had one.
        SpaceRef space = ResolveSpace(pane, in e, s);
        session.Wet.Begin(session.StyleFor(e.Button), space, pane.LevelId,
            new InkPoint(e.World.X, e.World.Y, e.Pressure),
            session.DefaultVisibility == EnvelopeMode.RealTime, _lastEventMs);

        s.RequestRender();
        return true;
    }

    /// <inheritdoc />
    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        AnnotationSession session = s.Session;
        if (!session.Wet.IsActive)
        {
            return;
        }

        long nowMs = s.NowMilliseconds;
        float spacing = session.SampleSpacingFor(session.Wet.Style);

        // Coalesced samples first, oldest-first: they happened BEFORE the primary point, and appending
        // them after it would fold the stroke back on itself on every fast drag.
        //
        // They are spread EVENLY across the interval since the previous event, because a batch carries
        // no times of its own: Avalonia (11.3.12) stamps the EVENT and never the sample —
        // PointerEventArgs.Timestamp is a ulong of milliseconds, PointerPoint exposes only
        // Pointer/Position/Properties, and even RawPointerPoint carries no time. That costs nothing:
        // a 60 Hz event arrives 16.7 ms after the last and one DV tick is 15.625 ms, so the whole
        // interpolation happens strictly BELOW the quantization floor BuildTiming rounds to. It is not
        // an approximation of something that could otherwise have been measured.
        ReadOnlySpan<InkPoint> intermediate = e.Intermediate;
        for (int i = 0; i < intermediate.Length; i++)
        {
            long atMs = _lastEventMs + ((nowMs - _lastEventMs) * (i + 1) / (intermediate.Length + 1));
            session.Wet.TryAppend(intermediate[i], spacing, atMs);
        }

        session.Wet.TryAppend(new InkPoint(e.World.X, e.World.Y, e.Pressure), spacing, nowMs);
        _lastEventMs = nowMs;

        // Unconditional, as it always effectively was: the repaint below runs whether or not the spacing
        // filter kept the sample, so the `appended` flag existed only to gate AnnotationSession's
        // WetChanged — an event nothing ever subscribed to (D6 §3), now deleted.
        s.RequestRender();
    }

    /// <inheritdoc />
    public void OnReleased(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        AnnotationSession session = s.Session;
        if (!session.Wet.IsActive)
        {
            CloseGesture();
            return;
        }

        // No _lastEventMs update here: the release is the LAST event of the gesture, and the next press
        // seeds it again. Carrying it forward would be a value nothing can read.
        session.Wet.TryAppend(new InkPoint(e.World.X, e.World.Y, e.Pressure),
            session.SampleSpacingFor(session.Wet.Style), s.NowMilliseconds);

        IReadOnlyList<InkPoint> samples = session.Wet.Points;
        if (samples.Count > 0)
        {
            // A tap with no movement is a DOT, not nothing: two coincident samples is what the outliner
            // needs to produce its circular cap, and dropping the stroke instead would make a deliberate
            // point-marking gesture silently do nothing.
            InkPoint[] points = samples.Count >= 2
                ? [.. samples]
                : [samples[0], samples[0]];

            // Null for every mode but RealTime, and that is what keeps the rest byte-identical: the DTO
            // writes WhenWritingNull, so an element without a cadence emits no field at all and the
            // pinned v1 schema sample does not move.
            StrokeTiming? timing = session.Wet.IsRecordingCadence
                ? session.Wet.BuildTiming(AnnotationSession.DvTicksPerSecond)
                : null;

            AnnotationElement element = new(
                Guid.NewGuid(),
                AnnotationKind.Freehand,
                session.Wet.Style,
                session.Wet.Space,
                _envelope,
                points,
                null,
                timing);

            session.Document.Apply(new DocDelta.Add(element, session.Document.Elements.Count));
        }

        CloseGesture();
        session.Wet.Clear();
        s.RequestRender();
    }

    /// <inheritdoc />
    public void OnCancelled(IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        AnnotationSession session = s.Session;

        // BailToMark BEFORE disposing the handle: disposing first would push the (empty) gesture and
        // leave nothing for the bail to roll back.
        session.Document.BailToMark();
        CloseGesture();
        session.Wet.Clear();
        s.RequestRender();
    }

    private void CloseGesture()
    {
        _gesture?.Dispose();
        _gesture = null;
    }

    private static SpaceRef ResolveSpace(LevelPane pane, in ToolPointerEvent e, IToolServices s)
    {
        AnnotationSession session = s.Session;

        if (session.AnchorToEntities
            && s.TryResolveEntityAnchor(pane, e.World, session.AnchorWorldRadius,
                out ulong steamId, out float dx, out float dy)
            && steamId != 0)
        {
            return new SpaceRef.Entity(steamId, dx, dy);
        }

        return new SpaceRef.World(MapSpace.QuantizeZ(pane.Level.ZMin));
    }
}
