namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     Drag-to-pan / wheel-to-zoom as an <see cref="IPointerTool" />. A thin wrapper over B1's
///     <see cref="PanZoomGesture" />, which is the single implementation of the camera math — this type
///     adds the tool protocol and nothing else, so pan behaviour cannot drift between the router path and
///     the host's own.
///     <para>
///         The permanent fallback tool: it is never disposed and it is what hold-Space diverts to
///         (plan decision D3).
///     </para>
/// </summary>
public sealed class PanZoomTool : IPointerTool
{
    private readonly PanZoomGesture _gesture = new();

    /// <inheritdoc />
    public ToolKind Kind => ToolKind.PanZoom;

    /// <summary>Whether a pan drag is in progress.</summary>
    public bool IsDragging => _gesture.IsDragging;

    /// <inheritdoc />
    public bool OnPressed(in ToolPointerEvent e, IToolServices s) =>
        _gesture.Press(e.Pane, e.Screen.X, e.Screen.Y);

    /// <inheritdoc />
    public void OnMoved(in ToolPointerEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (_gesture.Move(e.Screen.X, e.Screen.Y))
        {
            s.RequestRender();
        }
    }

    /// <inheritdoc />
    public void OnReleased(in ToolPointerEvent e, IToolServices s) => _gesture.Release();

    /// <inheritdoc />
    public void OnCancelled(IToolServices s) => _gesture.Release();

    /// <summary>
    ///     Zoom-to-cursor on the pane under the pointer. Called by <see cref="InputToolRouter.OnWheel" />
    ///     under EVERY tool, which is why it is not an <see cref="IPointerTool" /> member.
    /// </summary>
    /// <param name="e">The wheel sample.</param>
    /// <param name="s">Host services.</param>
    public void Wheel(in ToolWheelEvent e, IToolServices s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (_gesture.Wheel(e.Pane, e.Screen.X, e.Screen.Y, e.Delta))
        {
            s.RequestRender();
        }
    }
}
