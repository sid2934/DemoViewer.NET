#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Query;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The Query Canvas tokens: one team-coloured disc per placed <see cref="QueryToken" />, drawn on
///     the pane whose level the token was dropped on, with the slot number as tally strokes inside the
///     disc. An unresolved token (no place within reach of the drop) is drawn hollow.
///     <para>
///         <b>No text.</b> The disc is a marker-sized shape and the slot is tally strokes rather than a
///         glyph, so a golden of a query fixture is held to the unrelaxed pixel gate on every platform:
///         the glyph allowance <c>GoldenTolerance.ForLabelledFrame</c> spends is denominated in marker
///         labels, and a query fixture has no markers. The place name belongs to the rail, which is
///         Avalonia text and never in a golden.
///     </para>
///     <para>
///         Pane assignment goes through <c>MapSpace.IdForAnchor</c> like world-anchored ink, not through
///         a Z band test: a token stores the level key it was dropped on and has no world Z of its own.
///     </para>
/// </summary>
public sealed class QueryTokenLayer : ISceneLayer
{
    /// <summary>Disc radius in screen pixels: a little larger than a player marker so the two never read as one.</summary>
    public const float Radius = SceneDefaults.MarkerRadius + 3f;

    private readonly QueryCanvasDocument _document;
    private readonly SKPaint _fill;
    private readonly SKPaint _mark;
    private readonly SKPaint _ring;

    /// <summary>Creates the layer over a document. The document is shared with the tool and not owned.</summary>
    /// <param name="document">The slots to draw.</param>
    public QueryTokenLayer(QueryCanvasDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;

        _fill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        _ring = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true
        };
        _mark = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
    }

    /// <summary>The document being drawn.</summary>
    public QueryCanvasDocument Document => _document;

    /// <inheritdoc />
    public string Id => SceneLayerIds.Query;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    /// <remarks>Above the ink (100) and the floor caption: a token the user is dragging must never disappear under a stroke.</remarks>
    public int Order => 120;

    /// <inheritdoc />
    /// <remarks>Ten discs at most; recording a picture to save that would cost more than it saves.</remarks>
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => _document.Version;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        IReadOnlyList<QueryToken> tokens = _document.Placed;
        for (int i = 0; i < tokens.Count; i++)
        {
            QueryToken token = tokens[i];
            if (!ctx.IsSingleLevel && LevelIdFor(in ctx, token.LevelMinZ) != ctx.Pane.LevelId)
            {
                continue;
            }

            Draw(canvas, in token, in ctx);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fill.Dispose();
        _ring.Dispose();
        _mark.Dispose();
    }

    /// <summary>
    ///     The pane-local screen position a token draws at, so a hit test and the picture agree by
    ///     construction.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <param name="transform">The pane's world to screen transform.</param>
    public static SKPoint ScreenPosition(in QueryToken token, ViewportTransform transform)
    {
        (double x, double y) = transform.WorldToScreen(token.WorldX, token.WorldY);
        return new SKPoint((float)x, (float)y);
    }

    // The space's answer, never Z's: after a floor is lost and re-found the carried id differs from
    // the minting key, and a token keyed by Z alone would draw on whichever floor owns the old key.
    private static MapLevelId LevelIdFor(in SceneRenderContext ctx, double levelMinZ) =>
        ctx.Levels is { } space ? space.IdForAnchor(levelMinZ) : MapSpace.IdForZMin(levelMinZ);

    private void Draw(SKCanvas canvas, in QueryToken token, in SceneRenderContext ctx)
    {
        SKPoint at = ScreenPosition(in token, ctx.Transform);
        SKColor team = token.Side == QuerySide.Ct ? ctx.Palette.TeamCt : ctx.Palette.TeamT;
        SKColor ring = token.Side == QuerySide.Ct ? ctx.Palette.MarkerRingCt : ctx.Palette.MarkerRingT;

        if (token.IsResolved)
        {
            _fill.Color = team;
            canvas.DrawCircle(at, Radius, _fill);
        }

        _ring.Color = ring;
        canvas.DrawCircle(at, Radius, _ring);

        // Tally strokes for the slot: one to four uprights, the fifth a diagonal across them. Black on
        // a filled disc like a marker's initials; the team colour on a hollow one so it stays visible
        // over the radar.
        _mark.Color = token.IsResolved ? SKColors.Black : team;
        int count = token.Slot + 1;
        int uprights = Math.Min(count, 4);
        const float step = 3.5f;
        const float half = 4.5f;
        float left = at.X - (uprights - 1) * step / 2f;
        for (int i = 0; i < uprights; i++)
        {
            float x = left + i * step;
            canvas.DrawLine(x, at.Y - half, x, at.Y + half, _mark);
        }

        if (count == 5)
        {
            canvas.DrawLine(left - step / 2f, at.Y + half, left + 3 * step + step / 2f, at.Y - half, _mark);
        }
    }
}
