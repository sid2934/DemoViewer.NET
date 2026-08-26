namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     Routes pointer events to exactly one <see cref="IPointerTool" />.
///     <para>
///         <b>The gesture, not the selection, decides where a move goes.</b> The tool that took the press
///         keeps every move and the release, so switching tools or releasing Space mid-drag can never
///         hand half a stroke to the eraser.
///     </para>
///     <para>
///         <b>Hold-Space is sampled at press time only</b> (plan decision D3): it diverts the NEXT press
///         to <see cref="PanZoomTool" />, and a gesture already in flight is never hijacked — a
///         half-committed stroke is worse than a missed pan.
///     </para>
///     <para>
///         <b>Wheel is router-level</b> (D2): it always applies zoom-to-cursor to the pane under the
///         cursor whatever tool is selected, preserving the pre-v2 semantics byte for byte.
///     </para>
///     <para>
///         <b>The button is part of the routing decision</b> (D2 §3.4): middle and Ctrl+drag reach
///         <see cref="PanZoomTool" /> under every tool, and the right button reaches
///         <see cref="SecondaryTool" />. All three are read from the same press-time expression as
///         hold-Space, so none of them can re-route a gesture that is already in flight.
///     </para>
/// </summary>
public sealed class InputToolRouter
{
    private readonly PanZoomTool _panZoom;
    private readonly IToolServices _services;
    private readonly Dictionary<ToolKind, IPointerTool> _tools = [];

    private IPointerTool? _gestureTool;
    private ToolPointerButton _gestureButton;
    private ToolKind _selected = ToolKind.PanZoom;

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
    public IPointerTool Active => _tools.TryGetValue(_selected, out IPointerTool? tool) ? tool : _panZoom;

    /// <summary>The selected tool's kind.</summary>
    public ToolKind ActiveKind => _selected;

    /// <summary>
    ///     Whether the hold-to-pan modifier is down. Set by the host's key handlers; read only at press
    ///     time (D3).
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
    public bool IsGestureOpen => _gestureTool is not null;

    /// <summary>The tool owning the in-flight gesture, or null.</summary>
    public IPointerTool? GestureTool => _gestureTool;

    /// <summary>
    ///     True while a DRAWING tool is selected — what the app's keymap passes as its <c>toolActive</c>
    ///     flag, so the tool-scoped Space / Esc bindings shadow the transport ones only when they should.
    /// </summary>
    public bool IsDrawingToolActive => _selected is ToolKind.Draw or ToolKind.Erase;

    /// <summary>Raised after <see cref="SetActive" /> actually changed the selection.</summary>
    public event Action<ToolKind>? ActiveToolChanged;

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

        if (_selected == kind)
        {
            return;
        }

        CancelActive();
        _selected = kind;
        _services.Session.ActiveTool = kind;
        ActiveToolChanged?.Invoke(kind);
    }

    /// <summary>Routes a press. Returns true when a tool took the gesture.</summary>
    /// <param name="e">The pointer sample.</param>
    public bool OnPressed(in ToolPointerEvent e)
    {
        if (_gestureTool is not null)
        {
            // CHORDING IS NOT A GESTURE. A press from a DIFFERENT button while one is in flight is the
            // accidental middle-click halfway through a stroke; cancelling there would trade the ink for
            // a pan nobody asked for. The SAME button pressing again can only mean its release went
            // missing (a lost capture, a synthetic sequence), and that has to stay recoverable — so it
            // still closes the stale gesture rather than interleaving two.
            if (e.Button != _gestureButton)
            {
                return false;
            }

            CancelActive();
        }

        // Read ONCE, at press time (D3). Every clause here is a diversion to pan; the button→tool map
        // below is what the right button uses when nothing diverted it.
        bool divert = IsSpaceHeld
                      || (e.Modifiers & ToolModifiers.Space) != 0
                      || (PanOnMiddleButton && e.Button == ToolPointerButton.Middle)
                      || (PanOnControlDrag && (e.Modifiers & ToolModifiers.Control) != 0);

        IPointerTool tool = divert ? _panZoom : ToolForButton(e.Button);

        if (!tool.OnPressed(in e, _services))
        {
            return false;
        }

        _gestureTool = tool;
        _gestureButton = e.Button;
        return true;
    }

    /// <summary>Routes a move to whichever tool owns the gesture. A no-op when none does.</summary>
    /// <param name="e">The pointer sample.</param>
    public void OnMoved(in ToolPointerEvent e) => _gestureTool?.OnMoved(in e, _services);

    /// <summary>Routes a release and closes the gesture.</summary>
    /// <param name="e">The pointer sample.</param>
    public void OnReleased(in ToolPointerEvent e)
    {
        if (_gestureTool is not { } tool)
        {
            return;
        }

        _gestureTool = null;
        tool.OnReleased(in e, _services);
    }

    /// <summary>Zoom-to-cursor, under every tool (D2).</summary>
    /// <param name="e">The wheel sample.</param>
    public void OnWheel(in ToolWheelEvent e) => _panZoom.Wheel(in e, _services);

    /// <summary>Esc: cancels the in-flight gesture. A no-op when there is none.</summary>
    public void CancelActive()
    {
        if (_gestureTool is not { } tool)
        {
            return;
        }

        _gestureTool = null;
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
