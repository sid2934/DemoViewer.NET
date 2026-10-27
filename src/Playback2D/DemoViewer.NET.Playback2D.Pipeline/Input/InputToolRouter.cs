#region

using DemoViewer.NET.Playback2D.Input;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Input;

/// <summary>
///     Routes pointer events to exactly one <see cref="IPointerTool" />.
///     <para>
///         <b>The gesture, not the selection, decides where a move goes.</b> The tool that took the press
///         keeps every move and the release, so switching tools or releasing Space mid-drag can never
///         hand half a stroke to the eraser. The button owns the whole gesture, press to release: a
///         press from another button is refused (<see cref="OnPressed" />) and so is that button's
///         RELEASE (<see cref="OnReleased" />). Chording is not a gesture at either end.
///     </para>
///     <para>
///         <b>Routing is decided once, at press time.</b> Hold-Space diverts the NEXT press to
///         <see cref="PanZoomTool" /> without hijacking a gesture already in flight. A half-committed
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
    private readonly Dictionary<string, IPointerTool> _tools = new(StringComparer.Ordinal);
    private ToolPointerButton _gestureButton;

    // The tools declaring ToolKind.Token, in registration order, for OnPressed's fall-through: the first
    // still registered wins. A list rather than one cached tool, and rather than scanning _tools, because
    // both of those lose: one cached tool never transfers when it unregisters, and a dictionary scan picks
    // whichever of two the enumeration order happened to yield.
    private readonly List<IPointerTool> _tokenTools = [];

    // First registered, still registered. Removing the holder hands it to the next, which is what makes
    // the rule survive an extension unloading.
    private IPointerTool? TokenFallThrough => _tokenTools.Count > 0 ? _tokenTools[0] : null;

    /// <summary>Creates a router over the host's services with pan/zoom as the permanent fallback.</summary>
    /// <param name="services">Host services handed to every tool.</param>
    /// <param name="panZoom">The pan/zoom tool. Owned by the caller; never disposed by the router.</param>
    public InputToolRouter(IToolServices services, PanZoomTool panZoom)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(panZoom);

        _services = services;
        _panZoom = panZoom;
        _tools[panZoom.Id] = panZoom;
    }

    /// <summary>The selected tool. Not necessarily the one owning an in-flight gesture.</summary>
    public IPointerTool Active => _tools.TryGetValue(ActiveToolId, out IPointerTool? tool) ? tool : _panZoom;

    /// <summary>The selected tool's id, which is what selection and registration key on.</summary>
    public string ActiveToolId { get; private set; } = ToolIds.Of(ToolKind.PanZoom);

    /// <summary>
    ///     The selected tool's category, for the toolbar and the ink session. Two contributed tools may
    ///     report the same one, so it does not identify the selection; <see cref="ActiveToolId" /> does.
    /// </summary>
    public ToolKind ActiveKind => Active.Kind;

    /// <summary>
    ///     Whether the hold-to-pan modifier is down. Set by the host's key handlers; read only at press
    ///     time.
    /// </summary>
    public bool IsSpaceHeld { get; set; }

    /// <summary>
    ///     The tool the RIGHT button routes to. <c>null</c>, the default, means "whatever
    ///     <see cref="Active" /> is", so a right-drag with the pen still draws; it is then the ink that
    ///     differs, through <c>AnnotationSession.StyleFor</c>. An unregistered kind falls back to
    ///     <see cref="Active" /> rather than to pan: a right-drag that silently panned would be the same
    ///     surprise this property exists to remove.
    ///     <para>
    ///         A kind and not an id: it arrives from the annotations panel and from a persisted settings
    ///         string, both of which only ever name a first-party tool.
    ///     </para>
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
    ///     <see cref="PanOnMiddleButton" />, and because a trackpad has no middle button.
    /// </summary>
    public bool PanOnControlDrag { get; set; } = true;

    /// <summary>Whether a gesture is in flight.</summary>
    public bool IsGestureOpen => GestureTool is not null;

    /// <summary>The tool owning the in-flight gesture, or null.</summary>
    public IPointerTool? GestureTool { get; private set; }

    /// <summary>
    ///     True while any tool but pan/zoom is selected: what the app's keymap passes as its
    ///     <c>toolActive</c> flag, so the tool-scoped Space / Esc bindings shadow the transport ones under
    ///     every authoring tool. The token tool drags as much as the pen does, and
    ///     Esc has to cancel that drag rather than stop playback. Wider than
    ///     <see cref="ToolKinds.IsAnnotationTool" />, which still answers "does this tool write ink".
    ///     <para>
    ///         Keyed on the id, not the category: a contributed tool reporting
    ///         <see cref="ToolKind.PanZoom" /> still drags, and Esc still has to reach it.
    ///     </para>
    /// </summary>
    public bool IsDrawingToolActive => !string.Equals(ActiveToolId, ToolIds.Of(ToolKind.PanZoom), StringComparison.Ordinal);

    // No ActiveToolChanged event: the selection round-trips through AnnotationsPanelViewModel's own
    // ObservableProperty, which is what the toolbar binds and what the View's ToolSelected wire drives
    // INTO this router. SetActive mirrors onto the session, which is the part anything downstream
    // actually reads.

    /// <summary>
    ///     Registers a tool under its <see cref="IPointerTool.Id" />. Registering the same instance again is
    ///     a no-op.
    /// </summary>
    /// <param name="tool">The tool.</param>
    /// <exception cref="ArgumentException">
    ///     Another tool is already registered under that id. Overwriting it silently is what let a
    ///     contributed tool replace a built-in and leave no trace of having done so.
    /// </exception>
    public void Register(IPointerTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        string id = tool.Id;
        ArgumentException.ThrowIfNullOrEmpty(id, nameof(tool));
        if (_tools.TryGetValue(id, out IPointerTool? current))
        {
            if (ReferenceEquals(current, tool))
            {
                return;
            }

            throw new ArgumentException($"A tool is already registered as '{id}'.", nameof(tool));
        }

        _tools[id] = tool;
        if (tool.Kind == ToolKind.Token)
        {
            _tokenTools.Add(tool);
        }
    }

    /// <summary>
    ///     Drops a registered tool, falling back to pan/zoom if it was active. An id registered to a
    ///     different tool since is left alone. Pan/zoom itself cannot be dropped.
    /// </summary>
    /// <param name="tool">The tool.</param>
    public void Unregister(IPointerTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (ReferenceEquals(tool, _panZoom) || !_tools.TryGetValue(tool.Id, out IPointerTool? current)
            || !ReferenceEquals(current, tool))
        {
            return;
        }

        if (string.Equals(ActiveToolId, tool.Id, StringComparison.Ordinal))
        {
            SetActive(ToolKind.PanZoom);
        }

        _tools.Remove(tool.Id);
        _tokenTools.Remove(tool);
    }

    /// <summary>Selects the first-party tool of this kind. The toolbar's and the keymap's way in.</summary>
    /// <param name="kind">The tool to select. A kind with no tool registered falls back to pan/zoom.</param>
    public void SetActive(ToolKind kind) => SetActive(ToolIds.Of(kind));

    /// <summary>
    ///     Selects a tool by id, cancelling any gesture in flight first: a half-drawn stroke must not be
    ///     completed by whichever tool the user just switched to. The only way to reach a contributed tool,
    ///     whose id no <see cref="ToolKind" /> names.
    /// </summary>
    /// <param name="toolId">The tool's id. An unregistered id falls back to pan/zoom.</param>
    public void SetActive(string toolId)
    {
        if (toolId is null || !_tools.ContainsKey(toolId))
        {
            toolId = ToolIds.Of(ToolKind.PanZoom);
        }

        if (string.Equals(ActiveToolId, toolId, StringComparison.Ordinal))
        {
            return;
        }

        CancelActive();
        ActiveToolId = toolId;

        // Only a host that writes ink has a session to mirror the selection into. A static map view has
        // none, and ActiveTool exists for the annotations panel, which that host does not show.
        if (_services is IInkToolServices ink)
        {
            ink.Session.ActiveTool = ActiveKind;
        }
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
            // lost capture, a synthetic sequence), and that has to stay recoverable, so it still closes
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

        // Under pan, a left press on a token drags the token; the token tool refuses a miss, and with no token
        // editor (the 2D tab) it refuses everything, so the press pans.
        if (ReferenceEquals(tool, _panZoom) && !divert && e.Button == ToolPointerButton.Left
            && TokenFallThrough is { } token && token.OnPressed(in e, _services))
        {
            GestureTool = token;
            _gestureButton = e.Button;
            return true;
        }

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
    ///     Routes a release. Returns true when it actually closed the gesture: the host drops pointer
    ///     capture on that answer and on nothing else.
    ///     <para>
    ///         <b>The mirror of <see cref="OnPressed" />'s chord refusal</b>, the half that was missing before.
    ///         Brushing the middle button halfway through a stroke and letting go is a release for a
    ///         button that owns nothing: closing here committed the stroke at the chord point and dropped
    ///         capture, so the rest of the drag drew nothing and the real left release was a no-op.
    ///     </para>
    ///     <para>
    ///         <see cref="ToolPointerButton.None" /> is read as "the gesture's own button". A release
    ///         reports which buttons are STILL down, so the host cannot always name the one that came up,
    ///         and a synthetic sequence often carries none at all: refusing those would strand every
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
        && _tools.TryGetValue(ToolIds.Of(kind), out IPointerTool? secondary)
            ? secondary
            : Active;
}
