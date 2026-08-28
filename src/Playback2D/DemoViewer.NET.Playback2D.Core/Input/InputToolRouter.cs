namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     Routes pointer events to exactly one <see cref="IPointerTool" />.
///     <para>
///         <b>The gesture, not the selection, decides where a move goes.</b> The tool that took the press
///         keeps every move and the release, so switching tools or releasing Space mid-drag can never
///         hand half a stroke to the eraser. The button owns the whole gesture, press to release: a
///         press from another button is refused (<see cref="OnPressed" />) and so is that button's
///         RELEASE (<see cref="OnReleased" />) — chording is not a gesture at either end.
///     </para>
///     <para>
///         <b>Routing is decided once, at press time.</b> Hold-Space diverts the NEXT press to
///         <see cref="PanZoomTool" /> without hijacking a gesture already in flight — a half-committed
///         stroke is worse than a missed pan. Middle and Ctrl+drag reach <see cref="PanZoomTool" /> under
///         every tool, and the right button reaches <see cref="SecondaryTool" />; all three are read from
///         the same press-time expression as hold-Space, so none of them can re-route a gesture already
///         in flight. Wheel is router-level: it always applies zoom-to-cursor to the pane under the
///         cursor whatever tool is selected, preserving the pre-v2 semantics byte for byte.
///     </para>
/// </summary>
public sealed class InputToolRouter
{
    private readonly PanZoomTool _panZoom;
    private readonly IToolServices _services;
    private readonly Dictionary<ToolKind, IPointerTool> _tools = [];
    private ToolPointerButton _gestureButton;

    /// <summary>Creates a router over the host's services with pan/zoom as the permanent fallback.</summary>
    /// <param name="services">Host services handed to every tool.</param>
    /// <param name="panZoom">The pan/zoom tool. Owned by the caller; never disposed by the router.</param>
    public InputToolRouter(IToolServices services, PanZoomTool panZoom)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(panZoom);

        _services = services;
        _panZoom = panZoom;
        _tools[ToolKind.PanZoom] = panZoom;
    }

    /// <summary>The selected tool. Not necessarily the one owning an in-flight gesture.</summary>
    public IPointerTool Active => _tools.TryGetValue(ActiveKind, out IPointerTool? tool) ? tool : _panZoom;

    /// <summary>The selected tool's kind.</summary>
    public ToolKind ActiveKind { get; private set; } = ToolKind.PanZoom;

    /// <summary>
    ///     Whether the hold-to-pan modifier is down. Set by the host's key handlers; read only at press
    ///     time.
    /// </summary>
    public bool IsSpaceHeld { get; set; }

    /// <summary>
    ///     The tool the RIGHT button routes to. <c>null</c> — the default — means "whatever
    ///     <see cref="Active" /> is", so a right-drag with the pen still draws; it is then the ink that
    ///     differs, through <c>AnnotationSession.StyleFor</c>. An unregistered kind falls back to
    ///     <see cref="Active" /> rather than to pan: a right-drag that silently panned would be the same
    ///     surprise this property exists to remove.
    /// </summary>
    public ToolKind? SecondaryTool { get; set; }

    /// <summary>
    ///     Whether the middle button always pans. On by default: every map tool in the genre pans on
    ///     middle-drag, and a drawing tool that takes the wheel button hostage has no way back to the
    ///     view except putting the pen down.
    /// </summary>
    public bool PanOnMiddleButton { get; set; } = true;

    /// <summary>
    ///     Whether Ctrl+drag always pans. On by default, for the same reason as
    ///     <see cref="PanOnMiddleButton" /> — and because a trackpad has no middle button.
    /// </summary>
    public bool PanOnControlDrag { get; set; } = true;

    /// <summary>Whether a gesture is in flight.</summary>
    public bool IsGestureOpen => GestureTool is not null;

    /// <summary>The tool owning the in-flight gesture, or null.</summary>
    public IPointerTool? GestureTool { get; private set; }

    /// <summary>
    ///     True while a DRAWING tool is selected — what the app's keymap passes as its <c>toolActive</c>
    ///     flag, so the tool-scoped Space / Esc bindings shadow the transport ones only when they should.
    /// </summary>
    public bool IsDrawingToolActive => ActiveKind is ToolKind.Draw or ToolKind.Erase;

    // No ActiveToolChanged event: the selection round-trips through AnnotationsPanelViewModel's own
    // ObservableProperty, which is what the toolbar binds and what the View's ToolSelected wire drives
    // INTO this router. SetActive mirrors onto the session, which is the part anything downstream
    // actually reads.

    /// <summary>Registers a tool. Replacing a registered kind is allowed (a test double, a re-wire).</summary>
    /// <param name="tool">The tool.</param>
    public void Register(IPointerTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Kind] = tool;
    }

    /// <summary>
    ///     Selects a tool, cancelling any gesture in flight first — a half-drawn stroke must not be
    ///     completed by whichever tool the user just switched to.
    /// </summary>
    /// <param name="kind">The tool to select. An unregistered kind falls back to pan/zoom.</param>
    public void SetActive(ToolKind kind)
    {
        if (!_tools.ContainsKey(kind))
        {
            kind = ToolKind.PanZoom;
        }

        if (ActiveKind == kind)
        {
            return;
        }

        CancelActive();
        ActiveKind = kind;
        _services.Session.ActiveTool = kind;
    }

    /// <summary>Routes a press. Returns true when a tool took the gesture.</summary>
    /// <param name="e">The pointer sample.</param>
    public bool OnPressed(in ToolPointerEvent e)
    {
        if (GestureTool is not null)
        {
            // CHORDING IS NOT A GESTURE. A press from a DIFFERENT button while one is in flight is the
            // accidental middle-click halfway through a stroke; cancelling there would trade the ink for
            // an unintended pan. The SAME button pressing again can only mean its release went missing (a
            // lost capture, a synthetic sequence), and that has to stay recoverable — so it still closes
            // the stale gesture rather than interleaving two.
            if (e.Button != _gestureButton)
            {
                return false;
            }

            CancelActive();
        }

        // Read ONCE, at press time. Every clause here is a diversion to pan; the button→tool map below
        // is what the right button uses when nothing diverted it.
        bool divert = IsSpaceHeld
                      || (e.Modifiers & ToolModifiers.Space) != 0
                      || PanOnMiddleButton && e.Button == ToolPointerButton.Middle
                      || PanOnControlDrag && (e.Modifiers & ToolModifiers.Control) != 0;

        IPointerTool tool = divert ? _panZoom : ToolForButton(e.Button);

        if (!tool.OnPressed(in e, _services))
        {
            return false;
        }

        GestureTool = tool;
        _gestureButton = e.Button;
        return true;
    }

    /// <summary>Routes a move to whichever tool owns the gesture. A no-op when none does.</summary>
    /// <param name="e">The pointer sample.</param>
    public void OnMoved(in ToolPointerEvent e) => GestureTool?.OnMoved(in e, _services);

    /// <summary>
    ///     Routes a release. Returns true when it actually closed the gesture — the host drops pointer
    ///     capture on that answer and on nothing else.
    ///     <para>
    ///         <b>The mirror of <see cref="OnPressed" />'s chord refusal</b>, and the half D2 forgot.
    ///         Brushing the middle button halfway through a stroke and letting go is a release for a
    ///         button that owns nothing: closing here committed the stroke at the chord point and dropped
    ///         capture, so the rest of the drag drew nothing and the real left release was a no-op.
    ///     </para>
    ///     <para>
    ///         <see cref="ToolPointerButton.None" /> is read as "the gesture's own button". A release
    ///         reports which buttons are STILL down, so the host cannot always name the one that came up,
    ///         and a synthetic sequence often carries none at all — refusing those would strand every
    ///         gesture open instead.
    ///     </para>
    /// </summary>
    /// <param name="e">The pointer sample.</param>
    public bool OnReleased(in ToolPointerEvent e)
    {
        if (GestureTool is not { } tool)
        {
            return false;
        }

        if (e.Button != ToolPointerButton.None && e.Button != _gestureButton)
        {
            return false;
        }

        GestureTool = null;
        tool.OnReleased(in e, _services);
        return true;
    }

    /// <summary>Zoom-to-cursor, under every tool.</summary>
    /// <param name="e">The wheel sample.</param>
    public void OnWheel(in ToolWheelEvent e) => _panZoom.Wheel(in e, _services);

    /// <summary>Esc: cancels the in-flight gesture. A no-op when there is none.</summary>
    public void CancelActive()
    {
        if (GestureTool is not { } tool)
        {
            return;
        }

        GestureTool = null;
        tool.OnCancelled(_services);
    }

    // The right button's tool, or the selected one. An unregistered SecondaryTool degrades to Active
    // instead of throwing: it arrives from a persisted settings string, and a hand-edited typo must not
    // be able to take the pointer away from the user.
    private IPointerTool ToolForButton(ToolPointerButton button) =>
        button == ToolPointerButton.Right
        && SecondaryTool is { } kind
        && _tools.TryGetValue(kind, out IPointerTool? secondary)
            ? secondary
            : Active;
}
