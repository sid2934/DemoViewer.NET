#region

using System.Globalization;
using Avalonia;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Controls.Stats;

/// <summary>
///     One headline number on a circular track, the podium badge of the reference design. The arc length
///     is the value's position in its domain and the arc colour is its sentiment, so a glance gives both
///     "how much" and "is that good" without reading the number.
///     <para>
///         The arc starts at twelve o'clock and sweeps clockwise. A full sweep is clamped just short of
///         360 degrees, because a closed arc collapses start onto end and disappears; the track behind it
///         already reads as "full".
///     </para>
/// </summary>
public class RingGauge : StatPresenter
{
    /// <summary>Stroke width of both the track and the value arc.</summary>
    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Thickness), 3);

    /// <summary>Small text under the number (a metric name). Null draws none.</summary>
    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<RingGauge, string?>(nameof(Caption));

    /// <summary>The unfilled ring (<c>StatBarTrack</c>).</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(TrackBrush));

    /// <summary>Caption colour (<c>TextDim</c>). Null uses <c>Foreground</c>.</summary>
    public static readonly StyledProperty<IBrush?> CaptionBrushProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(CaptionBrush));

    /// <summary>Caption size relative to the value's font size.</summary>
    private const double CaptionScale = 0.72;

    /// <summary>A sweep this small is not worth a geometry; below it the ring reads as empty.</summary>
    private const double MinSweepDegrees = 0.5;

    static RingGauge()
    {
        AffectsRender<RingGauge>(ThicknessProperty, CaptionProperty, TrackBrushProperty,
            CaptionBrushProperty, BackgroundProperty);
    }

    /// <inheritdoc cref="ThicknessProperty" />
    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <inheritdoc cref="CaptionProperty" />
    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <inheritdoc cref="TrackBrushProperty" />
    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <inheritdoc cref="CaptionBrushProperty" />
    public IBrush? CaptionBrush
    {
        get => GetValue(CaptionBrushProperty);
        set => SetValue(CaptionBrushProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect content = new Rect(Bounds.Size).Deflate(Padding);
        if (content.Width <= 0 || content.Height <= 0)
        {
            return;
        }

        double stroke = Math.Max(1, Thickness);
        double radius = (Math.Min(content.Width, content.Height) - stroke) / 2;
        if (radius <= 0)
        {
            return;
        }

        Point centre = content.Center;

        if (Background is { } back)
        {
            context.DrawEllipse(back, null, centre, radius, radius);
        }

        if (TrackBrush is { } track)
        {
            context.DrawEllipse(null, new Pen(track, stroke), centre, radius, radius);
        }

        IBrush? accent = Accent ?? Foreground;
        if (accent is not null && Value.HasValue)
        {
            DrawArc(context, new Pen(accent, stroke, lineCap: PenLineCap.Round), centre, radius, Fraction);
        }

        DrawLabels(context, content, centre, accent);
    }

    private void DrawLabels(DrawingContext context, Rect content, Point centre, IBrush? accent)
    {
        Typeface face = new(FontFamily, FontStyle, FontWeight);
        FormattedText? value = null;
        string display = DisplayText;
        if (display.Length > 0)
        {
            value = new FormattedText(display, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                face, FontSize, accent ?? Foreground);
        }

        FormattedText? caption = null;
        if (!string.IsNullOrEmpty(Caption))
        {
            caption = new FormattedText(Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                face, FontSize * CaptionScale, CaptionBrush ?? Foreground);
        }

        double stackHeight = (value?.Height ?? 0) + (caption?.Height ?? 0);
        double y = centre.Y - (stackHeight / 2);

        if (value is not null)
        {
            context.DrawText(value, new Point(centre.X - (value.Width / 2), y));
            y += value.Height;
        }

        if (caption is not null && caption.Width <= content.Width)
        {
            context.DrawText(caption, new Point(centre.X - (caption.Width / 2), y));
        }
    }

    private static void DrawArc(DrawingContext context, IPen pen, Point centre, double radius,
        double fraction)
    {
        double sweep = Math.Clamp(fraction, 0, 1) * 360.0;
        if (sweep <= MinSweepDegrees)
        {
            return;
        }

        sweep = Math.Min(sweep, 359.99);
        Point start = PointOnCircle(centre, radius, -90);
        Point end = PointOnCircle(centre, radius, -90 + sweep);

        StreamGeometry geo = new();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geo);
    }

    private static Point PointOnCircle(Point c, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        return new Point(c.X + (radius * Math.Cos(rad)), c.Y + (radius * Math.Sin(rad)));
    }
}
