#region

using Avalonia;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The active camera mode for the 2D viewport. Applies per pane where applicable; a manual
///     pan/zoom on a pane pauses the auto-mode for that pane (<c>SliceCamera.ManualOverride</c>).
/// </summary>
public enum CameraMode
{
    /// <summary>Fit all observed positions once; static until changed (the original behaviour).</summary>
    Fit,

    /// <summary>Continuously + smoothly keep all ALIVE players in view, lerped per render frame.</summary>
    Alive,

    /// <summary>Fix the view to the all-demo observed extent (an approximation, no real radar metadata).</summary>
    Map,

    /// <summary>Smoothly keep the view centred on one selected player; holds last-known if they die/orphan.</summary>
    FollowPlayer
}

/// <summary>
///     What the view's mode selector, follow funnel and Fit button drive the mounted surface through.
/// </summary>
public interface IPlayback2DSurface
{
    /// <summary>The active camera mode.</summary>
    CameraMode Mode { get; set; }

    /// <summary>The followed roster slot; -1 clears. Setting it implies follow mode.</summary>
    int FollowSlot { set; }

    /// <summary>Re-frames to the observed extent and clears manual overrides.</summary>
    void FitToExtent();
}

/// <summary>
///     The level half of the viewport contract: what the level strip drives and reads.
///     <para>
///         Deliberately <b>separate</b> from <see cref="IPlayback2DSurface" />. A surface with no level
///         identity must not bind the strip at all, rather than satisfy it with stubs that let the strip
///         appear over something that cannot honour a single one of them.
///     </para>
/// </summary>
public interface ILevelSurface
{
    /// <summary>The resolved level set. Live; subscribe to <see cref="LevelStateChanged" />.</summary>
    MapSpace Levels { get; }

    /// <summary>Stacked bands, or one pane showing <see cref="ActiveLevelId" />.</summary>
    LevelDisplayMode DisplayMode { get; set; }

    /// <summary>Whether the shown level tracks the followed player.</summary>
    bool AutoLevelFollow { get; set; }

    /// <summary>The level a single-pane layout is showing.</summary>
    MapLevelId ActiveLevelId { get; }

    /// <summary>Pins a level, switching to a single pane and turning AutoFollow off.</summary>
    /// <param name="id">The level to show.</param>
    void PickLevel(MapLevelId id);

    /// <summary>Raised when the level set, the active level or the display mode changed.</summary>
    event Action? LevelStateChanged;
}

/// <summary>
///     The ANNOTATION half of the viewport contract: what the toolbar, the keymap's tool-scoped rows and
///     the ink gestures need from the mounted surface. Implemented only by the v2 host.
///     <para>
///         <b>
///             A gate says whether the user is ALLOWED to draw; this says whether there is anything to
///             draw ON.
///         </b>
///         Binding the toolbar's visibility to the feature gate instead renders the whole
///         docked tool row over a surface with no router, no ink layer and no gesture to cancel. Picking
///         Draw then flips <c>IsDrawingToolActive</c>, so the keymap's <c>WhenToolActive</c> scope wins
///         and <c>Space</c>/<c>Esc</c> resolve to handlers that fall through a concrete-type check and
///         return without setting <c>Handled</c>: play/pause dies with no visible cause. This is a
///         CAPABILITY the mounted surface either satisfies or does not, asked once at bind time.
///     </para>
/// </summary>
public interface IAnnotationSurface
{
    /// <summary>Selects the active pointer tool.</summary>
    /// <param name="kind">The tool.</param>
    void SetActiveTool(ToolKind kind);

    /// <summary>Hold-to-pan. The view sets it from the pan key.</summary>
    /// <param name="held">Whether the pan key is down.</param>
    void SetSpacePanHeld(bool held);

    /// <summary>Abandons whatever gesture is in flight.</summary>
    void CancelActiveGesture();

    /// <summary>
    ///     Raised when the text tool placed a label and wants its string typed: the host-space point the
    ///     label's top-left sits at, and the em size in screen pixels at the current zoom. The view owns
    ///     the editor; the surface only says where.
    /// </summary>
    event Action<Point, double>? TextEditRequested;

    /// <summary>
    ///     Hands the editor's result back: the typed string, or null when the edit was cancelled. A blank
    ///     result removes the label without an undo entry.
    /// </summary>
    /// <param name="text">The typed string, or null.</param>
    void CompleteTextEdit(string? text);
}

