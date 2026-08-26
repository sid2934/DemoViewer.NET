#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The export HUD's player cards: T down one edge of the frame, CT down the other, each card carrying
///     the tag, health, armour, weapon, cash and K/D of one player — the strip that makes a 720p export
///     read as a broadcast clip rather than as dots on a map (plan D3b item 3.1.1).
///     <para>
///         <b>Off unless requested</b>, like the other two HUD layers: registered only when
///         <c>ExportRequest.LayerIds</c> names <c>hud.roster</c>, and skipped outright when no HUD source
///         was supplied to feed it.
///     </para>
///     <para>
///         <b>One pane, not one per pane</b> — <see cref="ClockLayer.IsTopBand" />, for the same reason the
///         clock uses it: the compositor renders every layer once per band, and a roster repeated on each
///         floor of a two-level Nuke export would be five players claiming to be in two places.
///     </para>
///     <para>
///         <b>It yields to the map.</b> The cards are sized against the pane, not against the style, and
///         when even a shrunk card would take a fifth of the width or a row would fall under legibility the
///         layer draws <i>nothing</i>. A roster that swallows the radar is worse than no roster, and the
///         64×48 fixture renders in the export suite are exactly that case.
///     </para>
///     <para>
///         <b>No per-frame shaping</b> (design §6): every composed number is memoised by value, so a
///         steady-state frame reuses ten cards' worth of blobs out of <see cref="TextBlobCache" />'s LRU
///         instead of re-shaping forty strings and evicting the rest of the HUD.
///     </para>
/// </summary>
public sealed class RosterLayer : ISceneLayer
{
    // Below this a card is a coloured smear, not information — the layer withdraws instead.
    private const float MinCardWidthPx = 96f;
    private const float MinRowHeightPx = 22f;

    // A card only earns its second line (weapon · K/D · cash) once the first line and the bars have
    // their own room. Under it the card degrades to tag + health + bars rather than overlapping itself.
    private const float TwoLineRowHeightPx = 40f;

    // The most of a pane's width one column of cards may take. Two columns therefore never cost the map
    // more than a third of the frame.
    private const float MaxWidthFraction = 0.16f;

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
        float cardW = Math.Min(_style.RosterCardWidthPx, paneW * MaxWidthFraction);
        if (cardW < MinCardWidthPx)
        {
            return;
        }

        // Height is fitted, not assumed: ten players on a short pane get shorter cards rather than a
        // column that runs off the bottom of the video.
        float gap = _style.RosterRowGapPx;
        float usable = paneH - (_style.MarginPx * 2);
        float rowH = Math.Min(_style.RosterRowHeightPx, (usable - ((tallest - 1) * gap)) / tallest);
        if (rowH < MinRowHeightPx)
        {
            return;
        }

        float leftX = ctx.PaneBounds.Left + _style.MarginPx;
        float rightX = ctx.PaneBounds.Right - _style.MarginPx - cardW;

        DrawColumn(canvas, ctx, roster, 2, leftX, cardW, rowH, gap, tCount, paneH, true);
        DrawColumn(canvas, ctx, roster, 3, rightX, cardW, rowH, gap, ctCount, paneH, false);
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

    // One side's column, centred vertically so a 4-v-5 round reads as two balanced strips rather than
    // two columns hanging off the top edge.
    private void DrawColumn(SKCanvas canvas, SceneRenderContext ctx, IReadOnlyList<HudPlayerRow> roster,
        int team, float x, float cardW, float rowH, float gap, int count, float paneH, bool accentLeft)
    {
        if (count == 0)
        {
            return;
        }

        float totalH = (count * rowH) + ((count - 1) * gap);
        float y = ctx.PaneBounds.Top + ((paneH - totalH) / 2);
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
        // of pointing into it — and a dead player's stripe fades rather than disappearing, because a card
        // with no colour at all reads as "not on a team".
        _paint.Color = row.IsAlive ? teamColor : teamColor.WithAlpha(0x55);
        canvas.DrawRect(accentLeft ? x : x + w - accentW, y, accentW, h, _paint);

        float innerLeft = x + (accentLeft ? accentW : 0) + padX;
        float innerRight = x + w - (accentLeft ? 0 : accentW) - padX;
        uint nameArgb = row.IsAlive ? _style.TextArgb : _style.DimTextArgb;

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
                canvas.DrawRect(innerLeft, ty + (tag.Ascent / 2f) - 0.5f, tag.Width, 1f, _paint);
            }
        }

        if (_text.Get(Small(Math.Clamp(row.Health, 0, 100)), nameSize) is { } health)
        {
            nameHeight = Math.Max(nameHeight, health.Height);
            _paint.Color = row.IsAlive ? teamColor : new SKColor(_style.DimTextArgb);
            (float hx, float hy) = health.OriginForTopLeft(innerRight - health.Width, lineTop);
            canvas.DrawText(health.Blob, hx, hy, _paint);
        }

        // ── the bars: health over armour, both fractions of the same track width ────────────────────
        float barLeft = innerLeft;
        float barW = innerRight - innerLeft;
        float barTop = lineTop + nameHeight + 2f;

        DrawBar(canvas, barLeft, barTop, barW, 4f, row.IsAlive ? row.Health / 100f : 0f, teamColor);
        DrawBar(canvas, barLeft, barTop + 5f, barW, 2f, row.IsAlive ? row.Armor / 100f : 0f,
            new SKColor(_style.ArmorArgb).WithAlpha(row.HasHelmet ? (byte)0xFF : (byte)0x99));

        // ── line 2: weapon · K/D · cash, packed right to left so the weapon is what gets clipped ────
        float secondTop = barTop + 9f;
        if (secondTop + (_style.FontSizePx * 0.78f) > y + h - padY + 2f)
        {
            return; // a short card keeps the identity line and drops the detail line
        }

        float cursor = innerRight;
        if (_text.Get(Money(row.Money), smallSize) is { } money)
        {
            _paint.Color = new SKColor(row.IsAlive ? _style.MoneyArgb : _style.DimTextArgb);
            (float mx, float my) = money.OriginForTopLeft(cursor - money.Width, secondTop);
            canvas.DrawText(money.Blob, mx, my, _paint);
            cursor -= money.Width + 6f;
        }

        if (_text.Get(KillsDeaths(row.Kills, row.Deaths), smallSize) is { } kd)
        {
            _paint.Color = new SKColor(_style.DimTextArgb);
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
                ? new SKColor(_style.MoneyArgb)
                : new SKColor(_style.MoneyArgb).WithAlpha(0x77);
            canvas.DrawRoundRect(new SKRect(cursor - chip, secondTop + 3f, cursor, secondTop + 3f + chip),
                1.5f, 1.5f, _paint);
            cursor -= chip + 6f;
        }

        if (_text.Get(row.Weapon, smallSize) is { } weapon && innerLeft + weapon.Width <= cursor)
        {
            _paint.Color = new SKColor(_style.DimTextArgb);
            (float wx, float wy) = weapon.OriginForTopLeft(innerLeft, secondTop);
            canvas.DrawText(weapon.Blob, wx, wy, _paint);
        }
    }

    private void DrawBar(SKCanvas canvas, float x, float y, float w, float h, float fraction, SKColor fill)
    {
        _paint.Color = new SKColor(_style.TrackArgb);
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
