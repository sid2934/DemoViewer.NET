#region

using System.Diagnostics.CodeAnalysis;
using DemoViewer.NET.Playback2D.Core.Cameras;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Levels;

/// <summary>
///     One level's on-screen band: which level it shows, where on the host it sits, which camera rig
///     drives it, and the camera itself.
///     <para>
///         Lifetime is owned by <see cref="PaneSet" /> and identity is <see cref="MapLevelId" />, not
///         array position — insert a lower floor and the upper floor's pane keeps its pan, zoom and
///         manual override (design risk 5).
///     </para>
/// </summary>
public sealed class LevelPane
{
    private ViewportTransform _epochTransform;
    private SKRect _viewportRect;

    /// <summary>
    ///     The camera that renders this pane. A public <b>field</b> by contract (design §5.3): B2's
    ///     <c>PanZoomTool</c> mutates it in place through a <c>ref</c>, and a property returning a copy
    ///     of this struct would make panning silently do nothing.
    /// </summary>
    [SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
        Justification = "Deliberate: SliceCamera is a mutable struct that pointer tools update in place. " +
                        "A property would hand out a copy and break pan/zoom — design §5.3, plan correction 4.")]
    public SliceCamera Camera;

    /// <summary>Creates a pane for a level with a starting camera and rig.</summary>
    /// <param name="level">The level this pane shows.</param>
    /// <param name="camera">The initial camera.</param>
    /// <param name="rig">The rig driving it.</param>
    public LevelPane(MapLevel level, SliceCamera camera, ICameraRig rig)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(rig);
        Level = level;
        Camera = camera;
        Rig = rig;
        _epochTransform = camera.Current;
    }

    /// <summary>The level being shown. Re-pointed (never mutated) by a <see cref="MapSpace" /> rebuild.</summary>
    public MapLevel Level { get; set; }

    /// <summary>The rig computing this pane's camera target.</summary>
    public ICameraRig Rig { get; set; }

    /// <summary>
    ///     Host-space rectangle this pane occupies. Assigning a different rectangle bumps
    ///     <see cref="CameraEpoch" />, because every cached pane-local picture is invalid at a new size.
    /// </summary>
    public SKRect ViewportRect
    {
        get => _viewportRect;
        set
        {
            if (_viewportRect == value)
            {
                return;
            }

            _viewportRect = value;
            CameraEpoch++;
        }
    }

    /// <summary>Position from the bottom: 0 is the lowest level. Assigned by <see cref="PaneSet" />.</summary>
    public int LevelIndex { get; internal set; }

    /// <summary>
    ///     Bumped whenever the camera has moved far enough that a pane-local cached picture would be
    ///     visibly wrong, or the pane resized. <c>LayerPictureCache</c> keys <c>PerCamera</c> entries on
    ///     it — a per-pixel comparison would re-record every frame, and no comparison at all would
    ///     freeze the radar mid-pan.
    /// </summary>
    public int CameraEpoch { get; internal set; }

    /// <summary>
    ///     The space this pane's level belongs to, for rigs that need floor assignment
    ///     (<c>FitAliveRig</c>, <c>FollowPlayerRig</c>). Assigned by <see cref="PaneSet" />.
    /// </summary>
    public MapSpace? Space { get; internal set; }

    /// <summary>
    ///     How many panes are currently arranged. The pre-v2 rigs only filter by floor when there is
    ///     more than one band (<c>_cameras.Length > 1</c>, lines 762 and 806) — a single-band render
    ///     frames every player regardless of Z, and that is a parity behaviour, not an optimisation.
    /// </summary>
    public int PaneCount { get; internal set; } = 1;

    /// <summary>Convenience: this pane's level id.</summary>
    public MapLevelId LevelId => Level.Id;

    /// <summary>
    ///     Re-evaluates <see cref="CameraEpoch" /> against the camera's current transform, bumping it
    ///     when the move is material. Called by <see cref="PaneSet" /> after the camera advance and
    ///     after a pointer gesture — the camera is a public field, so the pane cannot observe writes to
    ///     it on its own.
    /// </summary>
    /// <returns>True when the epoch was bumped.</returns>
    public bool SyncCameraEpoch()
    {
        SliceCamera probe = new(_epochTransform);
        if (probe.IsSettledAt(Camera.Current))
        {
            return false;
        }

        _epochTransform = Camera.Current;
        CameraEpoch++;
        return true;
    }

    /// <summary>An immutable copy of everything the render thread is allowed to see about this pane.</summary>
    public LevelPaneSnapshot Snapshot() =>
        new(Level.Id, LevelIndex, Level, Camera.Current, ViewportRect, CameraEpoch);
}

/// <summary>
///     The render thread's view of one pane: value copies only, captured on the UI thread at submission.
///     The mutable <see cref="LevelPane" /> never crosses the thread boundary (plan §5.8).
/// </summary>
/// <param name="LevelId">The pane's level identity.</param>
/// <param name="LevelIndex">Position from the bottom; 0 is lowest.</param>
/// <param name="Level">
///     The level itself. A reference, but every mutable member on it (<c>Radar</c>) is rebound only
///     under the render gate.
/// </param>
/// <param name="Transform">World → pane-local screen for this frame.</param>
/// <param name="ViewportRect">Host-space rectangle of the band.</param>
/// <param name="CameraEpoch">Cache key component for <c>PerCamera</c> pictures.</param>
public readonly record struct LevelPaneSnapshot(
    MapLevelId LevelId,
    int LevelIndex,
    MapLevel Level,
    ViewportTransform Transform,
    SKRect ViewportRect,
    int CameraEpoch);
