#region

using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Input;

/// <summary>Which pointer tool owns the surface. Persisted by name in <c>Playback2DSettings.LastTool</c>.</summary>
public enum ToolKind
{
    /// <summary>Drag to pan, wheel to zoom. The permanent fallback.</summary>
    PanZoom,

    /// <summary>Freehand ink.</summary>
    Draw,

    /// <summary>Stroke-level eraser.</summary>
    Erase
}

/// <summary>Which physical button produced the event.</summary>
public enum ToolPointerButton
{
    /// <summary>No button (a hover move).</summary>
    None,

    /// <summary>Primary.</summary>
    Left,

    /// <summary>Secondary.</summary>
    Right,

    /// <summary>Wheel button.</summary>
    Middle
}

/// <summary>Keyboard modifiers active at the moment of the event.</summary>
[Flags]
public enum ToolModifiers
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Control.</summary>
    Control = 2,

    /// <summary>Alt.</summary>
    Alt = 4,

    /// <summary>Space — the hold-to-pan modifier (plan decision D3).</summary>
    Space = 8
}

/// <summary>
///     One pointer sample, already resolved to a pane and to world coordinates by the host. A
///     <c>ref struct</c> so the coalesced sample span never has to be copied onto the heap: a fast drag
///     delivers dozens of intermediate points per event, and allocating an array for each would blow the
///     §6 budget on the exact frames where it matters most.
/// </summary>
public readonly ref struct ToolPointerEvent
{
    /// <summary>The pane under the pointer, or null when it is over no band.</summary>
    public LevelPane? Pane { get; init; }

    /// <summary>Host-relative position.</summary>
    public SKPoint Screen { get; init; }

    /// <summary>Position relative to <see cref="Pane" />'s rectangle — the zoom anchor.</summary>
    public SKPoint PaneLocal { get; init; }

    /// <summary>World-space position within <see cref="Pane" />.</summary>
    public SKPoint World { get; init; }

    /// <summary>Stylus pressure 0..1; 0.5 when the device reports none.</summary>
    public float Pressure { get; init; }

    /// <summary>The button that produced this event.</summary>
    public ToolPointerButton Button { get; init; }

    /// <summary>Modifiers at the time of the event.</summary>
    public ToolModifiers Modifiers { get; init; }

    /// <summary>
    ///     Coalesced samples since the previous event, in WORLD space, oldest-first and EXCLUDING
    ///     <see cref="World" /> itself. May be empty. Feeding these to the ink is what makes a fast
    ///     stroke smooth on a 60 Hz surface and a 1000 Hz digitiser.
    /// </summary>
    public ReadOnlySpan<InkPoint> Intermediate { get; init; }
}

/// <summary>One wheel notch. A plain record struct: a wheel event carries no coalesced samples.</summary>
/// <param name="Pane">The pane under the cursor, or null.</param>
/// <param name="Screen">Host-relative cursor position.</param>
/// <param name="PaneLocal">Cursor position relative to <paramref name="Pane" />'s rectangle.</param>
/// <param name="Delta">Wheel delta; positive zooms in.</param>
/// <param name="Modifiers">Modifiers at the time of the event.</param>
public readonly record struct ToolWheelEvent(
    LevelPane? Pane,
    SKPoint Screen,
    SKPoint PaneLocal,
    double Delta,
    ToolModifiers Modifiers);

/// <summary>
///     A pointer tool — design §5.5 verbatim: four methods and <b>no wheel member</b>. Wheel is
///     router-level (plan decision D2) because zoom-to-cursor is universal drawing-app behaviour that no
///     tool should be able to take away.
/// </summary>
public interface IPointerTool
{
    /// <summary>Which tool this is.</summary>
    ToolKind Kind { get; }

    /// <summary>Handles a press. Returns true when the tool took the gesture.</summary>
    /// <param name="e">The pointer sample.</param>
    /// <param name="s">Host services.</param>
    bool OnPressed(in ToolPointerEvent e, IToolServices s);

    /// <summary>Handles a move while this tool owns the gesture.</summary>
    /// <param name="e">The pointer sample.</param>
    /// <param name="s">Host services.</param>
    void OnMoved(in ToolPointerEvent e, IToolServices s);

    /// <summary>Handles the release that ends the gesture.</summary>
    /// <param name="e">The pointer sample.</param>
    /// <param name="s">Host services.</param>
    void OnReleased(in ToolPointerEvent e, IToolServices s);

    /// <summary>Abandons the gesture without committing anything (Esc, or a tool switch mid-drag).</summary>
    /// <param name="s">Host services.</param>
    void OnCancelled(IToolServices s);
}

/// <summary>
///     Everything a tool needs from the host, and nothing that would tie it to Avalonia. This is the seam
///     that lets <c>DrawTool</c> and <c>EraseTool</c> be exercised in a direct-execution test with no
///     window, no dispatcher and no platform (design §11).
/// </summary>
public interface IToolServices
{
    /// <summary>The annotation session the tools mutate.</summary>
    AnnotationSession Session { get; }

    /// <summary>
    ///     The playhead in <b>DV frame-clock ticks</b>. Never a CS2 server tick: the LiveSync servo bends
    ///     the playhead between 0.75× and 1.5×, so a CS2-tick anchor drifts against what the user saw.
    /// </summary>
    int CurrentTick { get; }

    /// <summary>The pane under a host-space point, or null.</summary>
    /// <param name="screen">Host-relative position.</param>
    LevelPane? PaneAt(SKPoint screen);

    /// <summary>Host-space point → world space through a pane's camera.</summary>
    /// <param name="pane">The pane.</param>
    /// <param name="screen">Host-relative position.</param>
    SKPoint ScreenToWorld(LevelPane pane, SKPoint screen);

    /// <summary>World-space point → host space through a pane's camera.</summary>
    /// <param name="pane">The pane.</param>
    /// <param name="world">World position.</param>
    SKPoint WorldToScreen(LevelPane pane, SKPoint world);

    /// <summary>World units covered by one screen pixel in this pane. For screen-relative thresholds.</summary>
    /// <param name="pane">The pane.</param>
    double WorldUnitsPerPixel(LevelPane pane);

    /// <summary>
    ///     Finds the nearest player marker within <paramref name="worldRadius" /> of a world point and
    ///     reports the offset from it, for entity-anchored telestration.
    /// </summary>
    /// <param name="pane">The pane the point is in — used to filter markers to this level.</param>
    /// <param name="world">The world point.</param>
    /// <param name="worldRadius">Capture radius in world units.</param>
    /// <param name="steamId">The anchored player's SteamId.</param>
    /// <param name="dx">World X offset from the player to <paramref name="world" />.</param>
    /// <param name="dy">World Y offset from the player to <paramref name="world" />.</param>
    bool TryResolveEntityAnchor(LevelPane pane, SKPoint world, float worldRadius,
        out ulong steamId, out float dx, out float dy);

    /// <summary>
    ///     Where an existing element's stored samples are actually DRAWN in this pane, as a world-space
    ///     offset — the same resolution <c>AnnotationLayer</c> performs every frame.
    ///     <para>
    ///         Returns false when the element does not render in this pane at all: a
    ///         <see cref="SpaceRef.World" /> anchored to another floor, or a
    ///         <see cref="SpaceRef.Entity" /> whose player is absent or dead. That is what stops the
    ///         eraser from deleting a callout the user cannot see — on a stacked map both floors are on
    ///         screen at once, and the same world XY exists in both.
    ///     </para>
    /// </summary>
    /// <param name="pane">The pane the pointer is in.</param>
    /// <param name="element">The element being tested.</param>
    /// <param name="offsetX">World X offset applied to the element's samples when drawn here.</param>
    /// <param name="offsetY">World Y offset applied to the element's samples when drawn here.</param>
    bool TryResolveDrawOffset(LevelPane pane, AnnotationElement element,
        out float offsetX, out float offsetY);

    /// <summary>Asks the host to repaint. Coalesced by the host; safe to call per sample.</summary>
    void RequestRender();
}
