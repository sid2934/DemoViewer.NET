#region

using System.Text;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The export HUD's kill feed: up to six rows in the top-right corner, with the same modifier glyphs
///     the XAML feed shows — headshot, wallbang, no-scope, through-smoke, blind, airborne, flash assist.
///     <para>
///         <b>The rows are not computed here.</b> They come from the same
///         <c>KillFeedTimeline.Window</c> the view-model calls, through an <see cref="IHudDataSource" />,
///         which is what makes design risk 8 (the XAML feed and the exported feed drifting apart)
///         structurally impossible for the row <i>set</i>. This layer only decides how a row looks.
///     </para>
///     <para>
///         Like <see cref="ClockLayer" />, it is opt-in and draws in the topmost band only.
///     </para>
/// </summary>
public sealed class KillFeedLayer : ISceneLayer
{
    /// <summary>Rows drawn at most, matching the view-model's own window (<c>KillFeedTimeline</c>).</summary>
    public const int MaxRows = 6;

    private readonly StringBuilder _builder = new(96);
    private readonly IHudDataSource _data;
    private readonly bool _ownsText;
    private readonly SKPaint _paint;
    private readonly Dictionary<KillFeedRow, string> _rendered = new(256);
    private readonly HudStyle _style;
    private readonly TextBlobCache _text;

    private HudSnapshot _snapshot = HudSnapshot.Empty;

    /// <summary>Creates the layer.</summary>
    /// <param name="data">The tick → HUD state function.</param>
    /// <param name="style">Colours and metrics; the shipped look when null.</param>
    /// <param name="text">A shared blob cache; a private one when null, disposed with the layer.</param>
    public KillFeedLayer(IHudDataSource data, HudStyle? style = null, TextBlobCache? text = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _style = style ?? new HudStyle();
        _ownsText = text is null;
        _text = text ?? new TextBlobCache();
        _paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
    }

    /// <inheritdoc />
    public string Id => SceneLayerIds.HudKillFeed;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Hud;

    /// <inheritdoc />
    public int Order => 80;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        _snapshot = _data.At(time.Tick);
        return false;
    }

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (!ClockLayer.IsTopBand(ctx))
        {
            return;
        }

        IReadOnlyList<KillFeedRow> rows = _snapshot.KillRows;
        int count = Math.Min(rows.Count, MaxRows);
        if (count == 0)
        {
            return;
        }

        float right = ctx.PaneBounds.Right - _style.MarginPx;
        float y = _style.MarginPx;
        float lineHeight = _style.FontSizePx * 1.75f;

        // Oldest first, top to bottom — the same order the XAML feed stacks them in.
        int first = rows.Count - count;
        for (int i = first; i < rows.Count; i++)
        {
            if (_text.Get(Compose(rows[i]), _style.FontSizePx) is not { } shaped)
            {
                continue;
            }

            float padX = _style.MarginPx * 0.55f;
            float padY = _style.MarginPx * 0.3f;

            _paint.Color = new SKColor(_style.PanelArgb);
            canvas.DrawRoundRect(
                new SKRect(right - shaped.Width - padX * 2, y - padY,
                    right, y + shaped.Height + padY),
                3f, 3f, _paint);

            _paint.Color = new SKColor(_style.TextArgb);
            (float x, float baseline) = shaped.OriginForTopLeft(right - shaped.Width - padX, y);
            canvas.DrawText(shaped.Blob, x, baseline, _paint);

            y += lineHeight;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _paint.Dispose();
        if (_ownsText)
        {
            _text.Dispose();
        }
    }

    /// <summary>
    ///     The one-line form of a kill row. Public so the snapshot test can assert the export's text
    ///     against the same row the XAML feed binds, rather than against a picture.
    /// </summary>
    /// <param name="row">The kill to render.</param>
    public static string Format(KillFeedRow row) => Build(new StringBuilder(96), row);

    // Composed once per distinct row and kept: a demo has a few hundred kills, six of which are on
    // screen, and re-composing six strings every frame is exactly the per-frame allocation §6 forbids.
    private string Compose(KillFeedRow row)
    {
        if (_rendered.TryGetValue(row, out string? cached))
        {
            return cached;
        }

        string composed = Build(_builder, row);
        _rendered[row] = composed;
        return composed;
    }

    private static string Build(StringBuilder builder, KillFeedRow row)
    {
        builder.Clear();
        builder.Append(row.Attacker);

        if (!string.IsNullOrEmpty(row.Assister))
        {
            builder.Append(" +").Append(row.Assister);
            if (row.AssistedFlash)
            {
                builder.Append('⚡'); // high voltage — the flash-assist glyph the XAML feed uses
            }
        }

        builder.Append("  ").Append(row.Weapon);

        Append(builder, row.Headshot, " HS");
        Append(builder, row.Penetrated, " WB");
        Append(builder, row.NoScope, " NS");
        Append(builder, row.ThroughSmoke, " ≈");      // ≈ through smoke
        Append(builder, row.AttackerBlind, " ✱");     // ✱ killer was blind
        Append(builder, row.AttackerInAir, " ↑");     // ↑ killer airborne

        builder.Append("  →  ").Append(row.Victim);   // →
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, bool condition, string token)
    {
        if (condition)
        {
            builder.Append(token);
        }
    }
}
