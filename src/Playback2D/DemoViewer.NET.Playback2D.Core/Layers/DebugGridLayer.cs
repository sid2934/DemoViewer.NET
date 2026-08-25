#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The trivial smoke layer B0's exit criterion is proved with: a world-space grid plus one filled
///     disc per marker. It draws no text, so it needs no font and produces byte-identical output on a
///     CI container with no fontconfig.
///     <para>
///         Deliberately <c>internal</c> (with <c>InternalsVisibleTo</c> for the test project) so it can
///         never become a production dependency — the real layers land in B1.
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
            canvas.DrawLine((float)sx, bounds.Top, (float)sx, bounds.Bottom, i % MajorEvery == 0 ? major : minor);
        }

        long firstY = (long)Math.Floor(minY / GridStepWorld);
        long lastY = (long)Math.Ceiling(maxY / GridStepWorld);
        for (long i = firstY; i <= lastY; i++)
        {
            double worldY = i * GridStepWorld;
            (_, double sy) = t.WorldToScreen(0, worldY);
            canvas.DrawLine(bounds.Left, (float)sy, bounds.Right, (float)sy, i % MajorEvery == 0 ? major : minor);
        }
    }

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
