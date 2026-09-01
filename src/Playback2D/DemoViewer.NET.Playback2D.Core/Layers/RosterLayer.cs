#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The export HUD's player cards: T down one edge of the frame, CT down the other, each card carrying
///     the tag, health, armour, weapon, cash and K/D of one player: the strip that makes a 720p export
///     read as a broadcast clip rather than as dots on a map.
///     <para>
///         <b>Off unless requested, and drawn once per pane.</b> Registered only when
///         <c>ExportRequest.LayerIds</c> names <c>hud.roster</c>, and skipped outright when no HUD source
///         was supplied to feed it. Like <see cref="ClockLayer.IsTopBand" />, it renders once per band
///         rather than once per pane, so a roster on a two-level Nuke export isn't five players claiming
///         to be in two places.
///     </para>
///     <para>
///         <b>It yields to the map, cheaply.</b> Cards are sized against the pane, not against the style,
///         and when even a shrunk card would take a fifth of the width or a row would fall under
///         legibility the layer draws nothing: a roster that swallows the radar is worse than no roster,
///         the case the 64×48 fixture renders exercise. Every composed number is memoised by value, so a
///         steady-state frame reuses ten cards' worth of blobs out of <see cref="TextBlobCache" />'s LRU
///         instead of re-shaping forty strings and evicting the rest of the HUD.
///     </para>
/// </summary>
public sealed class RosterLayer : ISceneLayer
{
    // Below this a card is a coloured smear, not information: the layer withdraws instead.
    private const float MinCardWidthPx = 96f;
    private const float MinRowHeightPx = 22f;

    // A card only earns its second line (weapon · K/D · cash) once the first line and the bars have
    // their own room. Under it the card degrades to tag + health + bars rather than overlapping itself.
    private const float TwoLineRowHeightPx = 40f;

    // The most of a pane's width one column of cards may take. Two columns therefore never cost the map
    // more than a third of the frame.
    private const float MaxWidthFraction = 0.16f;

    // Preferred card metrics: ceilings, not commitments. The width is clamped against the pane and the
    // height is fitted to the tallest side.
    private const float CardWidthPx = 160f;
    private const float RowHeightPx = 46f;
    private const float RowGapPx = 5f;

    private const uint MoneyArgb = 0xFF7BC96Fu; // cash figure
    private const uint ArmorArgb = 0xFF8FA3B8u; // armour bar; brighter with a helmet
    private const uint TrackArgb = 0x66000000u; // the unfilled remainder of a health/armour bar

    private readonly IHudDataSource _data;
    private readonly Dictionary<(int Kills, int Deaths), string> _kd = new(64);
    private readonly Dictionary<int, string> _money = new(64);
    private readonly bool _ownsText;
    private readonly SKPaint _paint;
    private readonly string[] _small = new string[101];
    private readonly HudStyle _style;
    private readonly TextBlobCache _text;

    private HudSnapshot _snapshot = HudSnapshot.Empty;

    /// <summary>Creates the layer.</summary>
    /// <param name="data">The tick → HUD state function.</param>
    /// <param name="style">Colours and metrics; the shipped look when null.</param>
    /// <param name="text">A shared blob cache; a private one when null, disposed with the layer.</param>
    public RosterLayer(IHudDataSource data, HudStyle? style = null, TextBlobCache? text = null)
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
    public string Id => SceneLayerIds.HudRoster;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Hud;

    /// <inheritdoc />
    public int Order => 65;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        // Once per frame, not once per pane: At() is pure in tick, and asking it three times on a
        // three-level map is three windowings for one answer.
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

        IReadOnlyList<HudPlayerRow> roster = _snapshot.Roster;
        if (roster.Count == 0)
        {
            return;
        }

        int tCount = Count(roster, 2);
        int ctCount = Count(roster, 3);
        int tallest = Math.Max(tCount, ctCount);
        if (tallest == 0)
        {
            return; // a roster of spectators and coaches has no side to line up against an edge
        }

        float paneW = ctx.PaneBounds.Width;
        float paneH = ctx.PaneBounds.Height;
        float cardW = Math.Min(CardWidthPx, paneW * MaxWidthFraction);
        if (cardW < MinCardWidthPx)
        {
            return;
        }

        // Height is fitted, not assumed: ten players on a short pane get shorter cards rather than a
        // column that runs off the bottom of the video.
        float gap = RowGapPx;
        float usable = paneH - _style.MarginPx * 2;
        float rowH = Math.Min(RowHeightPx, (usable - (tallest - 1) * gap) / tallest);
        float top = ctx.PaneBounds.Top + (paneH - ColumnHeight(tallest, rowH, gap)) / 2;

        // ── the kill feed's band ─────────────────────────────────────────────────────────────────────
        // hud.killfeed owns the top-right corner of this same pane and is Order 80 against this layer's
        // 65, so wherever they meet the feed paints over the cards. On a pane tall enough for a centred
        // roster to clear it (anything from about 552 px with the shipped style), this is a no-op and
        // nothing below runs. On a short one, and a 1280×720 two-level stacked export is one, the strips
        // move into the band underneath the feed and shrink to fit it.
        //
        // BOTH columns move, not only CT's: only the right column can actually collide, but two strips at
        // different heights is not a layout, and the roster's whole shape is a matched pair framing the
        // map. The reservation is taken whether or not the feed is mounted, because a layer cannot see
        // its siblings, and the cost of taking it when it is absent is a shorter card on a small pane,
        // against a corner of the video that is unreadable when it is present.
        float feedTop = ctx.PaneBounds.Top + KillFeedLayer.ReservedBandHeight(_style);
        if (top < feedTop)
        {
            float band = ctx.PaneBounds.Bottom - _style.MarginPx - feedTop;
            float shrunk = Math.Min(rowH, (band - (tallest - 1) * gap) / tallest);

            // …and the reservation YIELDS when honouring it would cost the roster its existence. On a
            // pane so short that a legible column does not fit under the feed at all, an 800×420
            // two-level export leaves 39 px, there is no non-overlapping layout to find, and
            // withdrawing would silently drop the cards on every small pane whether or not a feed is
            // even mounted. Overlap on a pane that has no answer is a degradation; a roster that
            // vanishes because of a layer that is not there is a second defect.
            if (shrunk >= MinRowHeightPx)
            {
                rowH = shrunk;
                top = feedTop + (band - ColumnHeight(tallest, rowH, gap)) / 2;
            }
        }

        if (rowH < MinRowHeightPx)
        {
            return;
        }

        float leftX = ctx.PaneBounds.Left + _style.MarginPx;
        float rightX = ctx.PaneBounds.Right - _style.MarginPx - cardW;

        float tallestH = ColumnHeight(tallest, rowH, gap);
        DrawColumn(canvas, ctx, roster, 2, leftX, cardW, rowH, gap, tCount, top, tallestH, true);
        DrawColumn(canvas, ctx, roster, 3, rightX, cardW, rowH, gap, ctCount, top, tallestH, false);
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

    // A column of `count` cards, gaps included. The one place the stack's height is expressed, so the
    // centring above and the per-side centring below cannot drift apart.
    private static float ColumnHeight(int count, float rowH, float gap) =>
        count * rowH + (count - 1) * gap;

    private static int Count(IReadOnlyList<HudPlayerRow> roster, int team)
    {
        int count = 0;
        for (int i = 0; i < roster.Count; i++)
        {
            if (roster[i].Team == team)
            {
                count++;
            }
        }

        return count;
    }

    // One side's column, centred within the band the caller reserved so a 4-v-5 round reads as two
    // balanced strips rather than two columns hanging off the top edge. Centring against the TALLEST
    // side's stack rather than against the pane is what keeps the two sides sharing one centre line once
    // the kill feed has pushed the band down.
    private void DrawColumn(SKCanvas canvas, SceneRenderContext ctx, IReadOnlyList<HudPlayerRow> roster,
        int team, float x, float cardW, float rowH, float gap, int count, float bandTop, float bandHeight,
        bool accentLeft)
    {
        if (count == 0)
        {
            return;
        }

        float y = bandTop + (bandHeight - ColumnHeight(count, rowH, gap)) / 2;
        SKColor teamColor = ctx.Palette.TeamFill(team);

        for (int i = 0; i < roster.Count; i++)
        {
            HudPlayerRow row = roster[i];
            if (row.Team != team)
            {
                continue;
            }

            DrawCard(canvas, row, x, y, cardW, rowH, teamColor, accentLeft);
            y += rowH + gap;
        }
    }

    private void DrawCard(SKCanvas canvas, HudPlayerRow row, float x, float y, float w, float h,
        SKColor teamColor, bool accentLeft)
    {
        const float accentW = 3f;
        float padX = 6f;
        float padY = Math.Min(4f, h * 0.1f);

        _paint.Color = new SKColor(_style.PanelArgb);
        canvas.DrawRoundRect(new SKRect(x, y, x + w, y + h), 3f, 3f, _paint);

        // The side stripe sits on the OUTER edge of each column, so the two strips frame the map instead
        // of pointing into it, and a dead player's stripe fades rather than disappearing, because a card
        // with no colour at all reads as "not on a team".
        _paint.Color = row.IsAlive ? teamColor : teamColor.WithAlpha(0x55);
        canvas.DrawRect(accentLeft ? x : x + w - accentW, y, accentW, h, _paint);

        float innerLeft = x + (accentLeft ? accentW : 0) + padX;
        float innerRight = x + w - (accentLeft ? 0 : accentW) - padX;
        uint nameArgb = row.IsAlive ? _style.TextArgb : ClockLayer.DimTextArgb;

        float nameSize = _style.FontSizePx * 0.95f;
        float smallSize = _style.FontSizePx * 0.78f;

        // ── line 1: tag, and the health figure that is the one number a viewer actually tracks ─────
        float lineTop = y + padY;
        float nameHeight = 0;

        if (_text.Get(row.Name, nameSize) is { } tag)
        {
            nameHeight = tag.Height;
            _paint.Color = new SKColor(nameArgb);
            (float tx, float ty) = tag.OriginForTopLeft(innerLeft, lineTop);
            canvas.DrawText(tag.Blob, tx, ty, _paint);

            if (!row.IsAlive)
            {
                // A rule through the tag, not a glyph: the embedded face carries no dingbats worth
                // relying on, and a line is legible at every card size this layer will draw.
                canvas.DrawRect(innerLeft, ty + tag.Ascent / 2f - 0.5f, tag.Width, 1f, _paint);
            }
        }

        if (_text.Get(Small(Math.Clamp(row.Health, 0, 100)), nameSize) is { } health)
        {
            nameHeight = Math.Max(nameHeight, health.Height);
            _paint.Color = row.IsAlive ? teamColor : new SKColor(ClockLayer.DimTextArgb);
            (float hx, float hy) = health.OriginForTopLeft(innerRight - health.Width, lineTop);
            canvas.DrawText(health.Blob, hx, hy, _paint);
        }

        // ── the bars: health over armour, both fractions of the same track width ────────────────────
        float barLeft = innerLeft;
        float barW = innerRight - innerLeft;
        float barTop = lineTop + nameHeight + 2f;

        DrawBar(canvas, barLeft, barTop, barW, 4f, row.IsAlive ? row.Health / 100f : 0f, teamColor);
        DrawBar(canvas, barLeft, barTop + 5f, barW, 2f, row.IsAlive ? row.Armor / 100f : 0f,
            new SKColor(ArmorArgb).WithAlpha(row.HasHelmet ? (byte)0xFF : (byte)0x99));

        // ── line 2: weapon · K/D · cash, packed right to left so the weapon is what gets clipped ────
        float secondTop = barTop + 9f;
        if (secondTop + _style.FontSizePx * 0.78f > y + h - padY + 2f)
        {
            return; // a short card keeps the identity line and drops the detail line
        }

        float cursor = innerRight;
        if (_text.Get(Money(row.Money), smallSize) is { } money)
        {
            _paint.Color = new SKColor(row.IsAlive ? MoneyArgb : ClockLayer.DimTextArgb);
            (float mx, float my) = money.OriginForTopLeft(cursor - money.Width, secondTop);
            canvas.DrawText(money.Blob, mx, my, _paint);
            cursor -= money.Width + 6f;
        }

        if (_text.Get(KillsDeaths(row.Kills, row.Deaths), smallSize) is { } kd)
        {
            _paint.Color = new SKColor(ClockLayer.DimTextArgb);
            (float kx, float ky) = kd.OriginForTopLeft(cursor - kd.Width, secondTop);
            canvas.DrawText(kd.Blob, kx, ky, _paint);
            cursor -= kd.Width + 6f;
        }

        // The kit is a CT-only fact and the single most decision-relevant one on the card, so it gets a
        // chip of its own rather than a suffix on a string that might be clipped away.
        if (row.HasDefuser)
        {
            float chip = 6f;
            _paint.Color = row.IsAlive
                ? new SKColor(MoneyArgb)
                : new SKColor(MoneyArgb).WithAlpha(0x77);
            canvas.DrawRoundRect(new SKRect(cursor - chip, secondTop + 3f, cursor, secondTop + 3f + chip),
                1.5f, 1.5f, _paint);
            cursor -= chip + 6f;
        }

        if (_text.Get(row.Weapon, smallSize) is { } weapon && innerLeft + weapon.Width <= cursor)
        {
            _paint.Color = new SKColor(ClockLayer.DimTextArgb);
            (float wx, float wy) = weapon.OriginForTopLeft(innerLeft, secondTop);
            canvas.DrawText(weapon.Blob, wx, wy, _paint);
        }
    }

    private void DrawBar(SKCanvas canvas, float x, float y, float w, float h, float fraction, SKColor fill)
    {
        _paint.Color = new SKColor(TrackArgb);
        canvas.DrawRect(x, y, w, h, _paint);

        float filled = w * Math.Clamp(fraction, 0f, 1f);
        if (filled <= 0)
        {
            return;
        }

        _paint.Color = fill;
        canvas.DrawRect(x, y, filled, h, _paint);
    }

    // 0..100 as strings, filled on demand. Health is the one card figure that changes on every damage
    // event, and formatting it per player per frame is precisely the allocation §6 forbids.
    private string Small(int value) => _small[value] ??= value.ToString(CultureInfo.InvariantCulture);

    private string Money(int money)
    {
        if (_money.TryGetValue(money, out string? cached))
        {
            return cached;
        }

        // Bounded for the same reason ClockLayer bounds its countdowns: a demo produces a few hundred
        // distinct cash figures, a caller feeding nonsense produces unboundedly many.
        if (_money.Count > 4096)
        {
            _money.Clear();
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"${money}");
        _money[money] = text;
        return text;
    }

    private string KillsDeaths(int kills, int deaths)
    {
        (int Kills, int Deaths) key = (kills, deaths);
        if (_kd.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        if (_kd.Count > 4096)
        {
            _kd.Clear();
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"{kills}/{deaths}");
        _kd[key] = text;
        return text;
    }
}
