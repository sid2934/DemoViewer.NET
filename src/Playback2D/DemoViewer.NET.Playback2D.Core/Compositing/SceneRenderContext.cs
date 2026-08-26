#region

using DemoViewer.NET.Playback2D.Core.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>
///     Everything a layer needs to draw one pane, and nothing it could mutate.
///     <para>
///         <b>One type, extended in place.</b> B1 adds <c>Pane</c> (<c>LevelPaneSnapshot</c>) and
///         <c>LevelIndexFor</c>; B3 adds <c>Levels</c> (<c>MapSpace</c>) and <c>LevelCrossings</c>.
///         Extending means adding members to <i>this</i> record, never declaring a second one.
///     </para>
/// </summary>
/// <param name="Frame">The frame being drawn. Valid only for the enclosing render call.</param>
/// <param name="Time">The frame's injected clock.</param>
/// <param name="Transform">World → pane-local screen.</param>
/// <param name="PaneBounds">Pane-local bounds; the origin is always (0,0).</param>
/// <param name="LevelIndex">-1 = a single pane showing all levels (the pre-v2 <c>sliceIndex &lt; 0</c>).</param>
/// <param name="LevelMinZ">Lower world Z of this pane's level band.</param>
/// <param name="LevelMaxZ">Upper world Z of this pane's level band.</param>
/// <param name="Purpose">
///     Why this scene is being rendered. <b>Reserved</b> — no layer branches on it and all three values
///     render identically; see <see cref="RenderPurpose" /> for why it is carried anyway.
/// </param>
/// <param name="Palette">Resolved theme colours and stroke widths.</param>
/// <param name="RenderScaling">Device pixels per DIP; exactly 1.0 offscreen.</param>
public readonly record struct SceneRenderContext(
    Scene2DFrame Frame,
    SceneTime Time,
    ViewportTransform Transform,
    SKRect PaneBounds,
    int LevelIndex,
    double LevelMinZ,
    double LevelMaxZ,
    RenderPurpose Purpose,
    ScenePalette Palette,
    float RenderScaling)
{
    /// <summary>
    ///     The pane being drawn, as captured at submission. Added by B1 (integrator correction 2);
    ///     default before the compositor's multi-pane path fills it.
    /// </summary>
    public LevelPaneSnapshot Pane { get; init; }

    /// <summary>
    ///     The level set this pane belongs to, or null on a context built without one (B0's fixtures,
    ///     a single-level render). Needed because <see cref="LevelIndexFor" /> must reproduce
    ///     <c>FloorSplitter.SliceIndexFor</c>'s nearest-band fallback, which a lone Z band cannot.
    /// </summary>
    public MapSpace? Levels { get; init; }

    /// <summary>
    ///     Which entities changed level on this frame, when the frame owner keeps a tracker. Added by
    ///     B3; null on a context built without one.
    ///     <para>
    ///         For layers holding <i>per-entity temporal state</i> — B1's marker smoothing (which reads
    ///         it through <c>MarkerSmoother.LevelCrossings</c> rather than here, because it mutates in
    ///         <c>Advance</c> where there is no context) and, from B2, entity-anchored annotations. A
    ///         layer whose content is a pure function of the frame does not need it: grenade trails carry
    ///         their own per-point Z and are split across bands at draw time by
    ///         <c>TrailGeometry.FloorSegmentRuns</c>.
    ///     </para>
    /// </summary>
    public LevelCrossingTracker? LevelCrossings { get; init; }

    /// <summary>True when this pane shows every level at once, so no Z filtering applies.</summary>
    public bool IsSingleLevel => LevelIndex < 0;

    /// <summary>
    ///     The level index a world Z belongs on. On a single-level pane this is the
    ///     <see cref="LevelIndex" /> sentinel itself, so the caller's equality test passes for every Z —
    ///     the pre-v2 <c>sliceIndex &lt; 0</c> rule, encoded once (parity invariant 1).
    /// </summary>
    /// <param name="worldZ">The content's world Z.</param>
    public int LevelIndexFor(double worldZ) =>
        IsSingleLevel ? LevelIndex : Levels?.LevelIndexFor(worldZ) ?? LevelIndex;

    /// <summary>
    ///     Whether world content at <paramref name="worldZ" /> belongs in this pane.
    ///     <para>
    ///         <b>This is an assignment test, not a band test.</b> The pre-v2 filter is
    ///         <c>_floors.SliceIndexFor(z) == sliceIndex</c>, and <c>SliceIndexFor</c> snaps a Z that
    ///         falls in a gap — or above the highest band — to the <i>nearest</i> band. A plain
    ///         <c>z ∈ [min, max)</c> test would make a player on a ramp, or a grenade arcing above the
    ///         map, belong to no pane at all and simply vanish. Parity invariant 1.
    ///     </para>
    /// </summary>
    /// <param name="worldZ">The content's world Z.</param>
    public bool BelongsHere(double worldZ) => IsSingleLevel || LevelIndexFor(worldZ) == LevelIndex;
}
