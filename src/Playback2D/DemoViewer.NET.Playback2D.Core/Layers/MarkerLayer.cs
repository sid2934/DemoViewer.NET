#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     One team-coloured disc per player, with a yaw stub, an event-driven ring and an initials label.
///     Port of <c>DrawMarker</c> (viewport lines 1155-1205); the smoothing half of
///     <c>AdvanceMarkers</c> lives in the shared <see cref="MarkerSmoother" />.
///     <para>
///         <b>Draw position is smoothed; level assignment is raw</b> (parity invariant 3). A dot glides
///         between pushes, but the band it is drawn on is decided by the sampled Z — otherwise a player
///         crossing floors would visibly slide off one band before appearing on the other.
///     </para>
/// </summary>
public sealed class MarkerLayer : ISceneLayer
{
    private readonly SKPaint _fill;
    private readonly SKPaint _heading;
    private readonly SKPaint _label;
    private readonly SKPaint _ring;
    private readonly MarkerSmoother _smoother;
    private readonly TextBlobCache _text;
    private readonly bool _ownsText;

    /// <summary>Creates the layer.</summary>
    /// <param name="smoother">The shared marker smoothing. A fresh one when null.</param>
    /// <param name="text">The shared blob cache. A private one when null, disposed with the layer.</param>
    public MarkerLayer(MarkerSmoother? smoother = null, TextBlobCache? text = null)
    {
        _smoother = smoother ?? new MarkerSmoother();
        _ownsText = text is null;
        _text = text ?? new TextBlobCache();

        _fill = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        _heading = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };
        _ring = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        _label = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    /// <summary>The shared smoothing state.</summary>
    public MarkerSmoother Smoother => _smoother;

    /// <summary>
    ///     Whether to draw the initials labels. Off is what the byte-exact golden tier renders with —
    ///     text metrics are a review gate, not an assert (plan decision D-17), so Tier A takes text out
    ///     of the comparison entirely rather than loosening the tolerance for everything else.
    /// </summary>
    public bool DrawLabels { get; set; } = true;

    /// <inheritdoc />
    public string Id => SceneLayerIds.Markers;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.World;

    /// <inheritdoc />
    public int Order => 40;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <summary>The smoothed draw position for a slot — the pre-v2 test hook, same name and shape.</summary>
    /// <param name="slot">Roster slot.</param>
    public (float X, float Y)? SmoothedMarkerPosition(int slot) => _smoother.Position(slot);

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => _smoother.AdvanceOnce(in time, frame);

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        IReadOnlyList<PlayerMarker> markers = ctx.Frame.Markers;
        for (int i = 0; i < markers.Count; i++)
        {
            PlayerMarker marker = markers[i];
            if (!ctx.BelongsHere(marker.WorldZ))
            {
                continue;
            }

            DrawMarker(canvas, marker, in ctx);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fill.Dispose();
        _heading.Dispose();
        _ring.Dispose();
        _label.Dispose();
        if (_ownsText)
        {
            _text.Dispose();
        }
    }

    private void DrawMarker(SKCanvas canvas, PlayerMarker marker, in SceneRenderContext ctx)
    {
        (float dx, float dy) = _smoother.Position(marker.Slot) ?? (marker.WorldX, marker.WorldY);
        (double sx, double sy) = ctx.Transform.WorldToScreen(dx, dy);
        float cx = (float)sx, cy = (float)sy;
        const float radius = SceneDefaults.MarkerRadius;

        SKColor teamFill = ctx.Palette.TeamFill(marker.Team);

        // Heading stub from YAW (not velocity), drawn behind the disc so the disc occludes its root.
        if (marker.IsAlive)
        {
            double yawRad = marker.YawDegrees * Math.PI / 180.0;
            // World yaw 0 = +X (east); screen Y is inverted, hence the minus on the sine.
            float tipX = cx + (float)(Math.Cos(yawRad) * (radius + SceneDefaults.MarkerHeadingLength));
            float tipY = cy - (float)(Math.Sin(yawRad) * (radius + SceneDefaults.MarkerHeadingLength));
            _heading.Color = teamFill;
            canvas.DrawLine(cx, cy, tipX, tipY, _heading);
        }

        // Disc fill — hollow when dead (the pre-v2 code filled with Brushes.Transparent, which draws
        // nothing; skipping the call is the same pixels and one less blend).
        if (marker.IsAlive)
        {
            _fill.Color = teamFill;
            canvas.DrawCircle(cx, cy, radius, _fill);
        }

        SKColor ringColour = marker.Ring switch
        {
            RingState.Shooting => ctx.Palette.RingShooting,
            RingState.TakingDamage => ctx.Palette.RingDamage,
            RingState.Blinded => ctx.Palette.RingBlinded,
            RingState.Dead => ctx.Palette.RingDead,
            _ => RingColourForTeam(marker.Team, ctx.Palette)
        };

        byte alpha = (byte)Math.Clamp(marker.RingAlpha * 255, 0, 255);
        _ring.Color = ringColour.WithAlpha(alpha);
        _ring.StrokeWidth = marker.Ring == RingState.Team ? 1.5f : 2.5f;
        canvas.DrawCircle(cx, cy, radius, _ring);

        if (!DrawLabels || _text.Get(marker.Label, SceneDefaults.MarkerLabelSize) is not { } shaped)
        {
            return;
        }

        _label.Color = marker.IsAlive ? SKColors.Black : ctx.Palette.Label;
        (float ox, float oy) = shaped.OriginForCentre(cx, cy);
        canvas.DrawText(shaped.Blob, ox, oy, _label);
    }

    private static SKColor RingColourForTeam(int team, ScenePalette palette) => team switch
    {
        2 => palette.MarkerRingT,
        3 => palette.MarkerRingCt,
        _ => palette.MarkerRingNeutral
    };
}
