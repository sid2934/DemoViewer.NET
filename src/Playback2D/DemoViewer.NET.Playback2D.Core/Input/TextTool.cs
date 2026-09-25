#region

using DemoViewer.NET.Playback2D.Core.Annotations;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     The text label tool (step-authoring.md §3.2). Press places the anchor: it commits a
///     <see cref="AnnotationKind.Text" /> element with one point and an empty string, then asks the host
///     for an editor through <see cref="IToolServices.RequestTextEdit" />. The host hands the typed string
///     back through <see cref="CompleteEdit" />.
///     <para>
///         <b>The undo mark stays open across the edit.</b> The add and the typed text land in ONE
///         gesture, so a label costs one Ctrl+Z, and an empty result is rolled back to the mark rather
///         than removed after the fact, so a cancelled label leaves no undo entry at all. The document
///         refuses undo while the mark is open, which is also what stops Ctrl+Z from reaching the ink
///         while the editor has the keyboard.
///     </para>
/// </summary>
public sealed class TextTool : IPointerTool
{
    private AnnotationDocument? _document;
    private IDisposable? _gesture;

    /// <inheritdoc />
    public ToolKind Kind => ToolKind.Text;

    /// <summary>The element whose text is being typed, or null when no edit is open.</summary>
    public Guid? EditingElementId { get; private set; }

    /// <inheritdoc />
    public bool OnPressed(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (e.Pane is not { } pane)
        {
            return false;
        }

        // A press while an edit is still open means the host never reported back (the editor lost its
        // view, say). Close it with what the element holds, which for an untouched label is nothing,
        // so it is rolled back rather than left as an invisible element.
        if (EditingElementId is not null)
        {
            CompleteEdit(s, null);
        }

        AnnotationSession session = s.Session;
        AnnotationDocument document = session.Document;
        _gesture = document.BeginGesture("text");
        _document = document;

        AnnotationElement element = new(
            Guid.NewGuid(),
            AnnotationKind.Text,
            session.StyleFor(e.Button),
            DrawTool.ResolveSpace(pane, in e, s),
            session.EnvelopeForNewElement(s.CurrentTick),
            [new InkPoint(e.World.X, e.World.Y, e.Pressure)],
            "");

        document.Apply(new DocDelta.Add(element, document.Elements.Count));
        EditingElementId = element.Id;

        s.RequestTextEdit(element.Id);
        s.RequestRender();
        return true;
    }

    /// <inheritdoc />
    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
    }

    /// <inheritdoc />
    public void OnReleased(in ToolPointerEvent e, IToolServices s)
    {
        // Nothing: the gesture belongs to the editor from here and ends in CompleteEdit.
    }

    /// <inheritdoc />
    public void OnCancelled(IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);
        CompleteEdit(s, null);
    }

    /// <summary>
    ///     Ends the open edit. A non-blank <paramref name="text" /> replaces the element's text inside the
    ///     gesture that added it; a blank or null one rolls the gesture back, so the element is gone and no
    ///     undo entry is pushed. Returns false when no edit was open.
    /// </summary>
    /// <param name="s">Host services.</param>
    /// <param name="text">What was typed, or null to cancel.</param>
    public bool CompleteEdit(IToolServices s, string? text)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (EditingElementId is not { } id)
        {
            return false;
        }

        AnnotationDocument? document = _document;
        EditingElementId = null;
        _document = null;

        // The document is checked by identity: a demo swap mid-edit re-points the session, and a string
        // typed for the old demo must not land in the new one. Reset has already dropped the mark there.
        if (document is not null && ReferenceEquals(document, s.Session.Document)
                                 && document.TryGet(id, out AnnotationElement element))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                document.BailToMark();
            }
            else
            {
                document.Apply(new DocDelta.Replace(id, element with
                {
                    Text = text
                }));
            }
        }

        _gesture?.Dispose();
        _gesture = null;
        s.RequestRender();
        return true;
    }
}
