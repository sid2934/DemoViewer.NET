#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     A trivial smoke layer: a world-space grid plus one filled disc per marker, proving the render
///     pipeline draws end to end. It draws no text, so it needs no font and produces byte-identical
///     output on a CI container with no fontconfig.
///     <para>
///         Deliberately <c>internal</c> (with <c>InternalsVisibleTo</c> for the test project) so it can
///         never become a production dependency. <c>SceneLayerCatalog</c> does not register it, and
///         nothing in Pipeline names this type; its remaining callers are <c>SceneGoldenTests</c>'
///         single-pane smoke render, <c>SceneRendererTests</c>, <c>SceneSmokeRenderTests</c> and the GPU
///         parity harness, all of which construct it directly and all of which want a layer with no font
///         and no state.
///     </para>
/// </summary>
internal sealed class DebugGridLayer : ISceneLayer
{
    /// <summary>One CS2 cell width, matching the pre-v2 viewport's grid step.</summary>
    internal const double GridStepWorld = 512;

    private const int MajorEvery = 4;
    private const float MarkerRadiusPx = 12f;

    /// <inheritdoc />
    public string Id => "playback2d.debuggrid";

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Underlay;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        canvas.Clear(ctx.Palette.Background);
        DrawGrid(canvas, ctx);
        DrawMarkers(canvas, ctx);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static void DrawGrid(SKCanvas canvas, SceneRenderContext ctx)
    {
        ViewportTransform t = ctx.Transform;
        SKRect bounds = ctx.PaneBounds;

        (double worldLeft, double worldTop) = t.ScreenToWorld(bounds.Left, bounds.Top);
        (double worldRight, double worldBottom) = t.ScreenToWorld(bounds.Right, bounds.Bottom);

        // Screen Y grows downward while world Y grows upward, so the screen-top corner is the world MAX.
        double minX = Math.Min(worldLeft, worldRight);
        double maxX = Math.Max(worldLeft, worldRight);
        double minY = Math.Min(worldTop, worldBottom);
        double maxY = Math.Max(worldTop, worldBottom);

        // A degenerate transform can map the whole pane to a sliver of world; cap the line count so a
        // bad camera is a blank frame rather than a hang.
        const int maxLines = 512;
        if ((maxX - minX) / GridStepWorld > maxLines || (maxY - minY) / GridStepWorld > maxLines)
        {
            return;
        }

        using SKPaint minor = new();
        minor.Color = ctx.Palette.MinorGrid;
        minor.StrokeWidth = ctx.Palette.Strokes.MinorGrid;
        minor.IsStroke = true;

        using SKPaint major = new();
        major.Color = ctx.Palette.MajorGrid;
        major.StrokeWidth = ctx.Palette.Strokes.MajorGrid;
        major.IsStroke = true;

        long firstX = (long)Math.Floor(minX / GridStepWorld);
        long lastX = (long)Math.Ceiling(maxX / GridStepWorld);
        for (long i = firstX; i <= lastX; i++)
        {
            double worldX = i * GridStepWorld;
            (double sx, _) = t.WorldToScreen(worldX, 0);
            float x = PixelCentre(sx);
            canvas.DrawLine(x, bounds.Top, x, bounds.Bottom, i % MajorEvery == 0 ? major : minor);
        }

        long firstY = (long)Math.Floor(minY / GridStepWorld);
        long lastY = (long)Math.Ceiling(maxY / GridStepWorld);
        for (long i = firstY; i <= lastY; i++)
        {
            double worldY = i * GridStepWorld;
            (_, double sy) = t.WorldToScreen(0, worldY);
            float y = PixelCentre(sy);
            canvas.DrawLine(bounds.Left, y, bounds.Right, y, i % MajorEvery == 0 ? major : minor);
        }
    }

    /// <summary>
    ///     Snaps a 1px un-antialiased stroke onto the centre of the pixel column or row it falls in.
    ///     <para>
    ///         Without this, a line whose screen coordinate lands on an exact integer covers two pixels by
    ///         exactly half each, and which one wins is a rasteriser tie-break: software raster picks the
    ///         right/lower pixel and ANGLE picks the left/upper one. That is a 1px displacement with no
    ///         defensible answer, and the cross-backend parity suite found it on the origin cross of every
    ///         fixture.
    ///     </para>
    ///     <para>
    ///         Snapping changes nothing anywhere else: an un-antialiased hairline already resolves to the
    ///         pixel containing its coordinate, which is the pixel this centres it in. The committed CPU
    ///         goldens are byte-identical either way.
    ///     </para>
    /// </summary>
    /// <param name="screen">The line's screen coordinate along the axis it is perpendicular to.</param>
    private static float PixelCentre(double screen) => MathF.Floor((float)screen) + 0.5f;

    private static void DrawMarkers(SKCanvas canvas, SceneRenderContext ctx)
    {
        using SKPaint fill = new();
        fill.IsAntialias = true;

        foreach (PlayerMarker marker in ctx.Frame.Markers)
        {
            if (!ctx.BelongsHere(marker.WorldZ))
            {
                continue;
            }

            (double sx, double sy) = ctx.Transform.WorldToScreen(marker.WorldX, marker.WorldY);
            fill.Color = marker.Ring == RingState.Dead
                ? ctx.Palette.RingDead
                : ctx.Palette.TeamFill(marker.Team);
            canvas.DrawCircle((float)sx, (float)sy, MarkerRadiusPx, fill);
        }
    }
}
