#region

using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     The two-point shape tools: Line, Arrow, Rect and Ellipse, one class constructed per kind
///     (step-authoring.md §3.2). Press anchors the first point, moves rubber-band the second through the
///     session's wet stroke, and release commits ONE element whose <c>Points</c> is exactly
///     <c>[first, last]</c>, so a shape costs one Ctrl+Z like a stroke does.
///     <para>
///         Space and envelope are resolved exactly as <see cref="DrawTool" /> resolves them, at press
///         time: a shape started on a tracked player follows them, and one started on a floor stays
///         on that floor. Shift constrains a line or arrow to 45° steps and a rect or ellipse to a
///         square. A tap that never left the press point commits nothing: a zero-size shape is not a
///         mark anyone meant to make, unlike the pen's deliberate dot.
///     </para>
/// </summary>
public sealed class ShapeTool : IPointerTool
{
    // How far, in SCREEN pixels, the pointer has to travel before a release commits. Screen and not
    // world: at full zoom-out one pixel is tens of world units, and the question is whether the hand
    // moved, not how big the result is on the map.
    private const float TapSlopPixels = 3f;

    private readonly AnnotationKind _kind;
    private TimeEnvelope _envelope = TimeEnvelope.Static;
    private IDisposable? _gesture;
    private float _tapSlopWorld;

    /// <summary>Creates the tool for one shape kind.</summary>
    /// <param name="kind">Line, Arrow, Rect or Ellipse.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any other tool kind.</exception>
    public ShapeTool(ToolKind kind)
    {
        if (ToolKinds.ShapeKindOf(kind) is not { } shape)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "A shape tool is Line, Arrow, Rect or Ellipse.");
        }

        Kind = kind;
        _kind = shape;
    }

    /// <inheritdoc />
    public ToolKind Kind { get; }

    /// <inheritdoc />
    public bool OnPressed(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (e.Pane is not { } pane)
        {
            return false;
        }

        AnnotationSession session = s.Session;
        _gesture = session.Document.BeginGesture("shape");
        _envelope = session.EnvelopeForNewElement(s.CurrentTick);
        _tapSlopWorld = (float)(TapSlopPixels * s.WorldUnitsPerPixel(pane));

        InkPoint first = new(e.World.X, e.World.Y, e.Pressure);
        SpaceRef space = DrawTool.ResolveSpace(pane, in e, s);
        session.Wet.Begin(session.StyleFor(e.Button), space, pane.LevelId, first, kind: _kind);
        session.Wet.ReplaceLast(first);

        s.RequestRender();
        return true;
    }

    /// <inheritdoc />
    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        WetStroke wet = s.Session.Wet;
        if (!wet.IsActive)
        {
            return;
        }

        // Only the newest position matters to a rubber band, so the coalesced samples are ignored.
        wet.ReplaceLast(Constrain(wet.Points[0], e.World.X, e.World.Y, e.Pressure, e.Modifiers));
        s.RequestRender();
    }

    /// <inheritdoc />
    public void OnReleased(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        AnnotationSession session = s.Session;
        WetStroke wet = session.Wet;
        if (!wet.IsActive)
        {
            CloseGesture();
            return;
        }

        InkPoint first = wet.Points[0];
        InkPoint last = Constrain(first, e.World.X, e.World.Y, e.Pressure, e.Modifiers);

        float dx = last.X - first.X;
        float dy = last.Y - first.Y;
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) > _tapSlopWorld)
        {
            AnnotationElement element = new(
                Guid.NewGuid(),
                _kind,
                wet.Style,
                wet.Space,
                _envelope,
                [first, last],
                null);

            session.Document.Apply(new DocDelta.Add(element, session.Document.Elements.Count));
        }

        CloseGesture();
        wet.Clear();
        s.RequestRender();
    }

    /// <inheritdoc />
    public void OnCancelled(IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        AnnotationSession session = s.Session;
        session.Document.BailToMark();
        CloseGesture();
        session.Wet.Clear();
        s.RequestRender();
    }

    /// <summary>
    ///     The second point, with Shift's constraint applied: a line or arrow snaps its direction to the
    ///     nearest 45° and keeps its length, a rect or ellipse takes the larger side on both axes and keeps
    ///     the quadrant the pointer is in.
    /// </summary>
    /// <param name="first">The anchored first point.</param>
    /// <param name="x">Pointer world X.</param>
    /// <param name="y">Pointer world Y.</param>
    /// <param name="pressure">Pointer pressure, carried through.</param>
    /// <param name="modifiers">The modifiers at this event.</param>
    internal InkPoint Constrain(InkPoint first, float x, float y, float pressure, ToolModifiers modifiers)
    {
        if ((modifiers & ToolModifiers.Shift) == 0)
        {
            return new InkPoint(x, y, pressure);
        }

        float dx = x - first.X;
        float dy = y - first.Y;

        if (_kind is AnnotationKind.Line or AnnotationKind.Arrow)
        {
            double length = Math.Sqrt((double)dx * dx + (double)dy * dy);
            double step = Math.PI / 4;
            double angle = Math.Round(Math.Atan2(dy, dx) / step) * step;
            return new InkPoint(first.X + (float)(length * Math.Cos(angle)),
                first.Y + (float)(length * Math.Sin(angle)), pressure);
        }

        float side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new InkPoint(first.X + (dx < 0 ? -side : side), first.Y + (dy < 0 ? -side : side), pressure);
    }

    private void CloseGesture()
    {
        _gesture?.Dispose();
        _gesture = null;
    }
}
