#region

using DemoViewer.NET.Playback2D.Input;
using DemoViewer.NET.Playback2D.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Tools;

/// <summary>
///     A pointer tool on a 2D map. A press it accepts makes it the owner of the gesture: it then sees every
///     move and the release, or a cancel when the gesture is abandoned. Called on the UI thread.
/// </summary>
public interface IMapTool
{
    /// <summary>A press. Return true to own the gesture; false leaves the press to the map.</summary>
    /// <param name="e">The press.</param>
    /// <param name="context">The map the tool is on.</param>
    bool OnPressed(in MapToolEvent e, IMapToolContext context);

    /// <summary>A move during a gesture this tool owns.</summary>
    /// <param name="e">The move.</param>
    /// <param name="context">The map the tool is on.</param>
    void OnMoved(in MapToolEvent e, IMapToolContext context);

    /// <summary>The release that ends a gesture this tool owns.</summary>
    /// <param name="e">The release.</param>
    /// <param name="context">The map the tool is on.</param>
    void OnReleased(in MapToolEvent e, IMapToolContext context);

    /// <summary>The gesture this tool owns was abandoned: undo what it had started.</summary>
    /// <param name="context">The map the tool is on.</param>
    void OnCancelled(IMapToolContext context);
}

/// <summary>What a tool may ask of the map it is on.</summary>
public interface IMapToolContext
{
    /// <summary>The pane under a point of the view, or null.</summary>
    /// <param name="screen">A point in the view, in device-independent pixels.</param>
    LevelPane? PaneAt(SKPoint screen);

    /// <summary>A world point as a point of the view, through a pane's camera.</summary>
    /// <param name="pane">The pane.</param>
    /// <param name="world">The world point.</param>
    SKPoint WorldToScreen(LevelPane pane, SKPoint world);

    /// <summary>
    ///     A point of the view as a world point, through the camera of the pane under it, or null outside
    ///     every pane. The inverse of <see cref="WorldToScreen" />, and what hit-testing a token needs.
    ///     <para>
    ///         Defaulted, not abstract: every implementor already hands out panes through
    ///         <see cref="PaneAt" /> and a pane carries its own camera and viewport, so the body is derivable
    ///         and a context written before this member keeps compiling.
    ///     </para>
    /// </summary>
    /// <param name="screen">A point in the view, in device-independent pixels.</param>
    SKPoint? ScreenToWorld(SKPoint screen)
    {
        if (PaneAt(screen) is not { } pane)
        {
            return null;
        }

        (double x, double y) = pane.Camera.Current.ScreenToWorld(
            screen.X - pane.ViewportRect.Left, screen.Y - pane.ViewportRect.Top);
        return new SKPoint((float)x, (float)y);
    }

    /// <summary>World units one view pixel covers in a pane, for hit radii that stay the same size on screen.</summary>
    /// <param name="pane">The pane.</param>
    double WorldUnitsPerPixel(LevelPane pane);

    /// <summary>
    ///     The tick the map is showing, or null when this context has no clock. Nullable rather than
    ///     defaulted to a number: a tick of zero is an answer a caller cannot tell apart from a real one,
    ///     and the contexts that cannot answer are doubles rather than the host.
    /// </summary>
    int? CurrentTick => null;

    /// <summary>
    ///     The token editor the map's tokens are moved through, or null when there is none. Already nullable
    ///     on <c>IToolServices</c>, so null is a state the contract models rather than a gap this member
    ///     introduces.
    /// </summary>
    ITokenEditor? Tokens => null;

    /// <summary>Asks for a repaint after the tool changed what a layer draws.</summary>
    void RequestRender();
}

/// <summary>A pointer sample a tool sees.</summary>
public readonly struct MapToolEvent
{
    /// <summary>The pane under the pointer, or null outside every pane.</summary>
    public LevelPane? Pane { get; init; }

    /// <summary>The pointer in the view, in device-independent pixels.</summary>
    public SKPoint Screen { get; init; }

    /// <summary>The pointer relative to the pane's top-left corner.</summary>
    public SKPoint PaneLocal { get; init; }

    /// <summary>The world point under the pointer on <see cref="Pane" />; zero with no pane.</summary>
    public SKPoint World { get; init; }

    /// <summary>The button the gesture belongs to.</summary>
    public MapToolButton Button { get; init; }

    /// <summary>The keys held.</summary>
    public MapToolModifiers Modifiers { get; init; }
}

/// <summary>A pointer button.</summary>
public enum MapToolButton
{
    /// <summary>No button, as on a move with nothing held.</summary>
    None,

    /// <summary>The primary button.</summary>
    Left,

    /// <summary>The secondary button.</summary>
    Right,

    /// <summary>The middle button. The map pans on it before any tool is asked.</summary>
    Middle
}

/// <summary>Keys held during a pointer sample.</summary>
[Flags]
public enum MapToolModifiers
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Control. The map pans on a Control drag before any tool is asked.</summary>
    Control = 2,

    /// <summary>Alt.</summary>
    Alt = 4,

    /// <summary>Space, the hold-to-pan modifier. The map pans while it is held, before any tool is asked.</summary>
    Space = 8
}
