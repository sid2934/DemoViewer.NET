#region

using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Utility;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The Utility Book map: an icon where each landing group goes off, and, when a group is focused, a
///     disc where each throw position stands with the flight drawn from it to the landing. Each end draws on
///     the pane of its own floor and a flight is split across floors by <see cref="TrailGeometry" />, so a
///     throw from a lower floor onto an upper one reads on both.
///     <para>
///         <see cref="LandingsAt" /> and <see cref="ThrowsAt" /> are the host's hit test over the same geometry
///         the layer draws, so what is clicked is what is under the pointer by construction. Items that stack
///         within a disc carry a count badge, and the host cycles through them on repeated clicks.
///     </para>
/// </summary>
public sealed class UtilityMapLayer : ISceneLayer
{
    /// <summary>Landing icon radius in screen pixels for a group of two throws.</summary>
    public const float LandingRadius = 11f;

    /// <summary>The icon grows by this much per doubling of throws, up to <see cref="LandingRadiusMax" />.</summary>
    public const float LandingGrowth = 2f;

    /// <summary>The largest a landing icon grows.</summary>
    public const float LandingRadiusMax = 17f;

    /// <summary>Throw position disc radius in screen pixels.</summary>
    public const float ThrowRadius = SceneDefaults.MarkerRadius + 1f;

    /// <summary>Extra pixels around a disc that still count as a hit.</summary>
    public const float HitSlop = 4f;

    private readonly UtilityMapDocument _document;
    private readonly SKPaint _fill;
    private readonly IIconSource? _icons;
    private readonly SKPaint _line;
    private readonly List<(int Start, int End)> _runs = [];
    private readonly SKPaint _stroke;
    private readonly SKFont _font;

    /// <summary>Creates the layer over a document. Neither is owned.</summary>
    /// <param name="document">What to draw.</param>
    /// <param name="icons">The baked grenade art, or null to draw letters.</param>
    public UtilityMapLayer(UtilityMapDocument document, IIconSource? icons = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _icons = icons;
        _fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        _stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
        _line = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        _font = new SKFont { Size = 11f, Embolden = true };
    }

    /// <inheritdoc />
    public string Id => SceneLayerIds.Utility;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    public int Order => 115;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.PerCamera;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => _document.Version;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    /// <summary>A landing icon's radius for its throw count.</summary>
    /// <param name="throws">Throws in the group.</param>
    public static float RadiusFor(int throws) =>
        Math.Min(LandingRadiusMax, LandingRadius + LandingGrowth * MathF.Log2(Math.Max(2, throws) / 2f));

    /// <summary>Every landing group under a pane-local point, topmost first; empty when none.</summary>
    /// <param name="document">The document the layer draws.</param>
    /// <param name="transform">The pane's world to screen transform.</param>
    /// <param name="belongsHere">The pane's floor test, the same one the layer draws with.</param>
    /// <param name="local">The pane-local point.</param>
    public static IReadOnlyList<UtilityLanding> LandingsAt(UtilityMapDocument document, ViewportTransform transform,
        Func<double, bool> belongsHere, SKPoint local)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(belongsHere);
        List<UtilityLanding> hits = [];
        for (int i = document.Landings.Count - 1; i >= 0; i--)
        {
            UtilityLanding landing = document.Landings[i];
            if (!belongsHere(landing.Z))
            {
                continue;
            }

            (double x, double y) = transform.WorldToScreen(landing.X, landing.Y);
            float r = RadiusFor(landing.Throws) + HitSlop;
            if (Square(local.X - x) + Square(local.Y - y) <= r * r)
            {
                hits.Add(landing);
            }
        }

        return hits;
    }

    /// <summary>
    ///     Every throw position under a pane-local point, topmost first; empty when none. A jump-throw and a
    ///     standard throw from one spot stack exactly, so a click has to be able to reach both.
    /// </summary>
    /// <param name="document">The document the layer draws.</param>
    /// <param name="transform">The pane's world to screen transform.</param>
    /// <param name="belongsHere">The pane's floor test.</param>
    /// <param name="local">The pane-local point.</param>
    public static IReadOnlyList<UtilityThrow> ThrowsAt(UtilityMapDocument document, ViewportTransform transform,
        Func<double, bool> belongsHere, SKPoint local)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(belongsHere);
        List<UtilityThrow> hits = [];
        for (int i = document.Throws.Count - 1; i >= 0; i--)
        {
            UtilityThrow position = document.Throws[i];
            if (!belongsHere(position.Z))
            {
                continue;
            }

            (double x, double y) = transform.WorldToScreen(position.X, position.Y);
            const float r = ThrowRadius + HitSlop;
            if (Square(local.X - x) + Square(local.Y - y) <= r * r)
            {
                hits.Add(position);
            }
        }

        return hits;
    }

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        bool focus = _document.HasFocus;

        // Flights first, then the throw discs, then the landing icons on top: a flight ends under its icon.
        foreach (UtilityThrow position in _document.Throws)
        {
            DrawFlight(canvas, position, in ctx);
        }

        for (int i = 0; i < _document.Throws.Count; i++)
        {
            UtilityThrow position = _document.Throws[i];
            if (ctx.BelongsHere(position.Z))
            {
                DrawThrow(canvas, position, in ctx);
                if (i == LastStacked(_document.Throws, i, in ctx) && Stacked(_document.Throws, i, in ctx) is > 1 and var count)
                {
                    DrawBadge(canvas, position.X, position.Y, ThrowRadius, count, 255, in ctx);
                }
            }
        }

        List<int> here = [];
        List<Disc> discs = [];
        for (int i = 0; i < _document.Landings.Count; i++)
        {
            UtilityLanding landing = _document.Landings[i];
            if (ctx.BelongsHere(landing.Z))
            {
                (double x, double y) = ctx.Transform.WorldToScreen(landing.X, landing.Y);
                here.Add(i);
                discs.Add(new Disc((float)x, (float)y, RadiusFor(landing.Throws), landing.Focused));
            }
        }

        (bool[] hidden, int[] counts) = Declutter(discs);
        for (int k = 0; k < here.Count; k++)
        {
            if (hidden[k])
            {
                continue;
            }

            UtilityLanding landing = _document.Landings[here[k]];
            byte alpha = focus && !landing.Focused ? (byte)90 : (byte)255;
            DrawLanding(canvas, landing, alpha, in ctx);
            if (counts[k] > 1)
            {
                DrawBadge(canvas, landing.X, landing.Y, RadiusFor(landing.Throws), counts[k], alpha, in ctx);
            }
        }
    }

    /// <summary>One landing icon on screen: centre, radius, and whether it must stay drawn (the focused one).</summary>
    public readonly record struct Disc(float X, float Y, float Radius, bool Keep);

    /// <summary>
    ///     Which landing icons a bigger one covers at this zoom. Discs come in draw order (smallest first); one
    ///     whose centre lies inside a later disc at least as big is not drawn, and that disc's badge counts it. The hit
    ///     test still finds a hidden icon under its cover, so repeated clicks reach it.
    /// </summary>
    /// <param name="discs">The icons on this pane in draw order.</param>
    /// <returns>Per disc: hidden, and how many icons its badge stands for (itself included).</returns>
    public static (bool[] Hidden, int[] Counts) Declutter(IReadOnlyList<Disc> discs)
    {
        ArgumentNullException.ThrowIfNull(discs);
        bool[] hidden = new bool[discs.Count];
        int[] counts = new int[discs.Count];
        for (int i = 0; i < discs.Count; i++)
        {
            counts[i] = 1;
        }

        // Top-down: covers are decided before what they cover, and a hidden disc covers nothing.
        for (int i = discs.Count - 1; i >= 0; i--)
        {
            if (discs[i].Keep)
            {
                continue;
            }

            for (int j = discs.Count - 1; j > i; j--)
            {
                if (!hidden[j] && discs[j].Radius >= discs[i].Radius && Square(discs[i].X - discs[j].X) + Square(discs[i].Y - discs[j].Y) <= Square(discs[j].Radius))
                {
                    hidden[i] = true;
                    counts[j] += counts[i];
                    break;
                }
            }
        }

        return (hidden, counts);
    }

    // How many items on this pane draw within a disc of item i, i included; a badge on the topmost of them
    // says a click there reaches more than the one on top.
    private static int Stacked<T>(IReadOnlyList<T> items, int i, in SceneRenderContext ctx) where T : notnull
    {
        (float X, float Y, float Z) at = Where(items[i]);
        (double ax, double ay) = ctx.Transform.WorldToScreen(at.X, at.Y);
        int count = 0;
        for (int j = 0; j < items.Count; j++)
        {
            (float X, float Y, float Z) other = Where(items[j]);
            if (!ctx.BelongsHere(other.Z))
            {
                continue;
            }

            (double bx, double by) = ctx.Transform.WorldToScreen(other.X, other.Y);
            if (Square(ax - bx) + Square(ay - by) <= Square(ThrowRadius))
            {
                count++;
            }
        }

        return count;
    }

    // The last-drawn (topmost) index among the items stacked on item i.
    private static int LastStacked<T>(IReadOnlyList<T> items, int i, in SceneRenderContext ctx) where T : notnull
    {
        (float X, float Y, float Z) at = Where(items[i]);
        (double ax, double ay) = ctx.Transform.WorldToScreen(at.X, at.Y);
        int last = i;
        for (int j = i + 1; j < items.Count; j++)
        {
            (float X, float Y, float Z) other = Where(items[j]);
            if (!ctx.BelongsHere(other.Z))
            {
                continue;
            }

            (double bx, double by) = ctx.Transform.WorldToScreen(other.X, other.Y);
            if (Square(ax - bx) + Square(ay - by) <= Square(ThrowRadius))
            {
                last = j;
            }
        }

        return last;
    }

    private static (float X, float Y, float Z) Where<T>(T item) => item switch
    {
        UtilityThrow t => (t.X, t.Y, t.Z),
        UtilityLanding l => (l.X, l.Y, l.Z),
        _ => (0, 0, 0)
    };

    private void DrawBadge(SKCanvas canvas, float worldX, float worldY, float radius, int count, byte alpha, in SceneRenderContext ctx)
    {
        (double x, double y) = ctx.Transform.WorldToScreen(worldX, worldY);
        SKPoint at = new((float)x + radius * 0.8f, (float)y - radius * 0.8f);
        string text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        float width = _font.MeasureText(text);
        float r = Math.Max(7f, width / 2 + 3f);
        _fill.Color = new SKColor(250, 250, 250, alpha);
        canvas.DrawCircle(at, r, _fill);
        _fill.Color = new SKColor(20, 22, 30, alpha);
        canvas.DrawText(text, at.X - width / 2, at.Y + _font.Size * 0.35f, _font, _fill);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _line.Dispose();
        _font.Dispose();
    }

    private void DrawFlight(SKCanvas canvas, UtilityThrow position, in SceneRenderContext ctx)
    {
        IReadOnlyList<GrenadeTrailPoint> points = position.Trajectory;
        if (points.Count < 2)
        {
            return;
        }

        TrailGeometry.FloorSegmentRuns(points, in ctx, _runs);
        SKColor team = TeamColour(position.Team, in ctx);
        _line.Color = team.WithAlpha(position.Selected ? (byte)235 : (byte)150);
        _line.StrokeWidth = position.Selected ? 3f : 2f;
        foreach ((int start, int end) in _runs)
        {
            using SKPath path = new();
            for (int i = start; i <= end; i++)
            {
                (double x, double y) = ctx.Transform.WorldToScreen(points[i].X, points[i].Y);
                if (i == start)
                {
                    path.MoveTo((float)x, (float)y);
                }
                else
                {
                    path.LineTo((float)x, (float)y);
                }
            }

            canvas.DrawPath(path, _line);
        }
    }

    private void DrawThrow(SKCanvas canvas, UtilityThrow position, in SceneRenderContext ctx)
    {
        (double x, double y) = ctx.Transform.WorldToScreen(position.X, position.Y);
        SKPoint at = new((float)x, (float)y);
        _fill.Color = TeamColour(position.Team, in ctx);
        canvas.DrawCircle(at, ThrowRadius, _fill);
        _stroke.Color = position.Selected ? SKColors.White : SKColors.Black.WithAlpha(180);
        _stroke.StrokeWidth = position.Selected ? 2.5f : 1.5f;
        canvas.DrawCircle(at, ThrowRadius, _stroke);
        if (position.JumpThrow)
        {
            _stroke.StrokeWidth = 1.5f;
            _stroke.Color = _fill.Color;
            canvas.DrawCircle(at, ThrowRadius + 3.5f, _stroke);
        }
    }

    private void DrawLanding(SKCanvas canvas, UtilityLanding landing, byte alpha, in SceneRenderContext ctx)
    {
        (double x, double y) = ctx.Transform.WorldToScreen(landing.X, landing.Y);
        SKPoint at = new((float)x, (float)y);
        float r = RadiusFor(landing.Throws);

        _fill.Color = new SKColor(20, 22, 30, (byte)(alpha * 0.85f));
        canvas.DrawCircle(at, r, _fill);
        _stroke.Color = (landing.Focused ? SKColors.White : new SKColor(230, 230, 240)).WithAlpha(alpha);
        _stroke.StrokeWidth = landing.Focused ? 2.5f : 1.5f;
        canvas.DrawCircle(at, r, _stroke);

        float side = r * 1.3f;
        if (_icons?.Lookup(landing.IconKey, side * ctx.RenderScaling) is { } image)
        {
            float w = side * image.Width / Math.Max(1f, image.Height);
            SKRect dest = new(at.X - w / 2, at.Y - side / 2, at.X + w / 2, at.Y + side / 2);
            using SKPaint paint = new() { Color = SKColors.White.WithAlpha(alpha) };
            canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            return;
        }

        _fill.Color = SKColors.White.WithAlpha(alpha);
        string text = landing.Letter.ToString();
        float width = _font.MeasureText(text);
        canvas.DrawText(text, at.X - width / 2, at.Y + _font.Size * 0.35f, _font, _fill);
    }

    private static SKColor TeamColour(int team, in SceneRenderContext ctx) => team switch
    {
        3 => ctx.Palette.TeamCt,
        2 => ctx.Palette.TeamT,
        _ => new SKColor(200, 200, 200)
    };

    private static double Square(double v) => v * v;
}
