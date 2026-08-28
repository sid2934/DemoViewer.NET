#region

using System.Diagnostics.CodeAnalysis;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>
///     Drag-to-pan and wheel-to-zoom, hit-tested to the pane under the cursor. Port of the four pointer
///     handlers (viewport lines 406-460), with the Avalonia event types replaced by plain coordinates.
///     <para>
///         <b>The gesture stays bound to the pane it began on.</b> A drag that starts on the upper band
///         and wanders into the lower one keeps panning the upper band — otherwise a fast drag across a
///         band boundary yanks two floors at once.
///     </para>
///     <para>
///         It lives in <c>…Core.Input</c> rather than the App because B2's <c>PanZoomTool</c> wraps it
///         and that tool is Core's; putting the gesture App-side would invert the dependency.
///         <c>IPointerTool</c> and the router are B2's — B1 deliberately ships no competing tool
///         abstraction (plan decision D-10).
///     </para>
/// </summary>
public sealed class PanZoomGesture
{
    private const double ZoomStep = 1.1;
    private float _lastX, _lastY;

    /// <summary>Whether a drag is in progress.</summary>
    public bool IsDragging => DragPane is not null;

    /// <summary>The pane the current drag began on, or null.</summary>
    public LevelPane? DragPane { get; private set; }

    /// <summary>Begins a drag on the pane under the point.</summary>
    /// <param name="panes">The arranged panes.</param>
    /// <param name="x">Host X.</param>
    /// <param name="y">Host Y.</param>
    /// <returns>True when a pane was captured.</returns>
    public bool Press(PaneSet panes, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(panes);
        return Press(panes.PaneAt(x, y), x, y);
    }

    /// <summary>
    ///     Begins a drag on a pane the caller has already hit-tested. B2's <c>PanZoomTool</c> takes this
    ///     overload: the router resolves the pane once per event and hands it to whichever tool owns the
    ///     gesture, so re-running the hit test here would be both redundant and a second answer.
    /// </summary>
    /// <param name="pane">The pane under the cursor, or null.</param>
    /// <param name="x">Host X.</param>
    /// <param name="y">Host Y.</param>
    /// <returns>True when a pane was captured.</returns>
    public bool Press(LevelPane? pane, float x, float y)
    {
        DragPane = pane;
        _lastX = x;
        _lastY = y;
        return DragPane is not null;
    }

    /// <summary>Pans the captured pane by the movement since the last call.</summary>
    /// <param name="x">Host X.</param>
    /// <param name="y">Host Y.</param>
    /// <returns>True when the camera moved and the host should repaint.</returns>
    public bool Move(float x, float y)
    {
        if (DragPane is not { } pane)
        {
            return false;
        }

        pane.Camera.Current = pane.Camera.Current.WithPanDelta(x - _lastX, y - _lastY);
        pane.Camera.ManualOverride = true; // a manual pan pauses this pane's auto camera
        pane.SyncCameraEpoch();
        _lastX = x;
        _lastY = y;
        return true;
    }

    /// <summary>Ends the drag. Safe to call when none is in progress.</summary>
    public void Release() => DragPane = null;

    /// <summary>
    ///     Zooms the pane under the cursor about the <b>pane-local</b> cursor position — the band's
    ///     transform has the band's height as its viewport, so using host coordinates would zoom about
    ///     the wrong point on every band but the top one (viewport line 451).
    /// </summary>
    /// <param name="panes">The arranged panes.</param>
    /// <param name="x">Host X.</param>
    /// <param name="y">Host Y.</param>
    /// <param name="delta">Wheel delta; positive zooms in.</param>
    /// <returns>True when the camera changed.</returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Instance by contract: B2 wraps this gesture as an IPointerTool, whose wheel " +
                        "member is an instance method on the tool. Making it static here would force " +
                        "the wrapper to reach through the type name and would break the day the zoom " +
                        "anchor becomes stateful.")]
    public bool Wheel(PaneSet panes, float x, float y, double delta)
    {
        ArgumentNullException.ThrowIfNull(panes);
        return Wheel(panes.PaneAt(x, y), x, y, delta);
    }

    /// <summary>
    ///     Zooms a pane the caller has already hit-tested, about the host-space cursor position. The
    ///     overload B2's router calls (wheel is router-level, never a tool member — plan decision D2).
    /// </summary>
    /// <param name="pane">The pane under the cursor, or null.</param>
    /// <param name="x">Host X.</param>
    /// <param name="y">Host Y.</param>
    /// <param name="delta">Wheel delta; positive zooms in.</param>
    /// <returns>True when the camera changed.</returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Instance by contract, for the same reason as the PaneSet overload above.")]
    public bool Wheel(LevelPane? pane, float x, float y, double delta)
    {
        if (pane is null)
        {
            return false;
        }

        double factor = delta > 0 ? ZoomStep : 1 / ZoomStep;
        pane.Camera.Current = pane.Camera.Current.ZoomAbout(
            x - pane.ViewportRect.Left, y - pane.ViewportRect.Top, factor);
        pane.Camera.ManualOverride = true;
        pane.SyncCameraEpoch();
        return true;
    }
}
