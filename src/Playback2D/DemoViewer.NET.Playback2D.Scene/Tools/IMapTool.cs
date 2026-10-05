#region

using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Tools;

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

    /// <summary>World units one view pixel covers in a pane, for hit radii that stay the same size on screen.</summary>
    /// <param name="pane">The pane.</param>
    double WorldUnitsPerPixel(LevelPane pane);

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
    Alt = 4
}
