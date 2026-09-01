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
    private readonly HashSet<Guid> _erased = [];
    private readonly List<Guid> _hits = new(16);

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

        if (_gesture is not null)
        {
            EraseAt(in e, s);
        }

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

    // Only what the pane under the pointer actually DRAWS, at the position it draws it. Testing the
    // whole document against the raw stored samples erased the other floor's callout from a band it was
    // never visible in, could not reach an entity-anchored stroke where the user could see it, and
    // deleted strokes their envelope had faded out entirely. Topmost-first: the document draws
    // oldest-first, so the LAST element is the one the user means.
    private void EraseAt(in ToolPointerEvent e, IToolServices s)
    {
        if (e.Pane is not { } pane)
        {
            return;
        }

        AnnotationSession session = s.Session;
        int tick = s.CurrentTick;
        float radius = session.EraserWorldRadius;

        _hits.Clear();
        IReadOnlyList<AnnotationElement> elements = session.Document.Elements;
        for (int i = elements.Count - 1; i >= 0; i--)
        {
            AnnotationElement element = elements[i];
            if (element.Time.OpacityAt(tick) * element.Style.Opacity <= 0.001)
            {
                continue;
            }

            if (!s.TryResolveDrawOffset(pane, element, out float offsetX, out float offsetY))
            {
                continue;
            }

            if (AnnotationHitTester.HitTest(element, e.World.X - offsetX, e.World.Y - offsetY, radius))
            {
                _hits.Add(element.Id);
            }
        }

        if (_hits.Count == 0)
        {
            return;
        }

        bool removed = false;
        for (int i = 0; i < _hits.Count; i++)
        {
            // Dedupe within the gesture: a slow drag hit-tests the same stroke on every sample, and a
            // second Remove for an id already gone is a no-op the document would not record anyway,
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
