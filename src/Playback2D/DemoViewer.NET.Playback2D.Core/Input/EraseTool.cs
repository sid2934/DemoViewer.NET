#region

using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     The stroke-level eraser. Press and every subsequent move hit-test the eraser disc against the
///     document and remove whatever it touches; the whole drag is ONE undo entry, and a drag that touched
///     nothing pushes none at all (plan decision D4).
///     <para>
///         <b>There is no pixel erase.</b> Design §5.4 defers it explicitly, and a stroke-level eraser is
///         what keeps the document a list of vector elements that can be re-rendered at any zoom, at any
///         export resolution, by any of B4/C1's headless paths.
///     </para>
/// </summary>
public sealed class EraseTool : IPointerTool
{
    private readonly List<Guid> _hits = new(16);
    private readonly HashSet<Guid> _erased = [];

    private IDisposable? _gesture;

    /// <inheritdoc />
    public ToolKind Kind => ToolKind.Erase;

    /// <inheritdoc />
    public bool OnPressed(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (e.Pane is null)
        {
            return false;
        }

        _erased.Clear();
        _gesture = s.Session.Document.BeginGesture("erase");
        EraseAt(in e, s);
        return true;
    }

    /// <inheritdoc />
    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (_gesture is null)
        {
            return;
        }

        EraseAt(in e, s);
    }

    /// <inheritdoc />
    public void OnReleased(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        EraseAt(in e, s);
        CloseGesture();
        _erased.Clear();
        s.RequestRender();
    }

    /// <inheritdoc />
    public void OnCancelled(IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        s.Session.Document.BailToMark();
        CloseGesture();
        _erased.Clear();
        s.RequestRender();
    }

    private void EraseAt(in ToolPointerEvent e, IToolServices s)
    {
        AnnotationSession session = s.Session;
        int count = AnnotationHitTester.HitTestAll(session.Document, e.World.X, e.World.Y,
            session.EraserWorldRadius, _hits);
        if (count == 0)
        {
            return;
        }

        bool removed = false;
        for (int i = 0; i < _hits.Count; i++)
        {
            // Dedupe within the gesture: a slow drag hit-tests the same stroke on every sample, and a
            // second Remove for an id already gone is a no-op the document would not record anyway —
            // but skipping it here keeps the gesture's step list proportional to what was erased.
            if (!_erased.Add(_hits[i]))
            {
                continue;
            }

            session.Document.Apply(new DocDelta.Remove(_hits[i]));
            removed = true;
        }

        if (removed)
        {
            s.RequestRender();
        }
    }

    private void CloseGesture()
    {
        _gesture?.Dispose();
        _gesture = null;
    }
}
