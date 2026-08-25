#region

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
/// <param name="Purpose">Why this scene is being rendered.</param>
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
    /// <summary>True when this pane shows every level at once, so no Z filtering applies.</summary>
    public bool IsSingleLevel => LevelIndex < 0;

    /// <summary>
    ///     Whether world content at <paramref name="worldZ" /> belongs in this pane. Always true on a
    ///     single-level pane. The band is half-open at the top so a value exactly on a boundary lands in
    ///     exactly one pane.
    /// </summary>
    /// <param name="worldZ">The content's world Z.</param>
    public bool BelongsHere(double worldZ) =>
        IsSingleLevel || (worldZ >= LevelMinZ && worldZ < LevelMaxZ);
}
