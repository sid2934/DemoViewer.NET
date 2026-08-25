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

        SpaceRef space = ResolveSpace(pane, in e, s);
        session.Wet.Begin(session.Style, space, pane.LevelId,
            new InkPoint(e.World.X, e.World.Y, e.Pressure));

        session.NotifyWetChanged();
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

        float spacing = session.SampleSpacingWorld;
        bool appended = false;

        // Coalesced samples first, oldest-first: they happened BEFORE the primary point, and appending
        // them after it would fold the stroke back on itself on every fast drag.
        ReadOnlySpan<InkPoint> intermediate = e.Intermediate;
        for (int i = 0; i < intermediate.Length; i++)
        {
            appended |= session.Wet.TryAppend(intermediate[i], spacing);
        }

        appended |= session.Wet.TryAppend(new InkPoint(e.World.X, e.World.Y, e.Pressure), spacing);

        if (appended)
        {
            session.NotifyWetChanged();
        }

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

        session.Wet.TryAppend(new InkPoint(e.World.X, e.World.Y, e.Pressure), session.SampleSpacingWorld);

        IReadOnlyList<InkPoint> samples = session.Wet.Points;
        if (samples.Count > 0)
        {
            // A tap with no movement is a DOT, not nothing: two coincident samples is what the outliner
            // needs to produce its circular cap, and dropping the stroke instead would make a deliberate
            // point-marking gesture silently do nothing.
            InkPoint[] points = samples.Count >= 2
                ? [.. samples]
                : [samples[0], samples[0]];

            AnnotationElement element = new(
                Guid.NewGuid(),
                AnnotationKind.Freehand,
                session.Wet.Style,
                session.Wet.Space,
                _envelope,
                points,
                null);

            session.Document.Apply(new DocDelta.Add(element, session.Document.Elements.Count));
        }

        CloseGesture();
        session.Wet.Clear();
        session.NotifyWetChanged();
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
        session.NotifyWetChanged();
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
