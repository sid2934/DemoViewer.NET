#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The export HUD's scoreboard strip: two team-coloured score boxes flanking the main countdown, the
///     round number beneath them, and, while a defuse is under way, the defuse-versus-detonation race,
///     all as <c>SceneGameInfo</c> defines it.
///     <para>
///         <b>Colour and size carry the hierarchy.</b> There is no bold face to reach for (see
///         <see cref="TextBlobCache" />), so the countdown is the largest thing on screen, each side's
///         score sits on its own team token, and the defuse line is drawn in the colour of whoever is
///         winning the race. Strings are composed into two small keyed caches and shaped once by
///         <see cref="TextBlobCache" />, so there is no per-frame shaping cost.
///     </para>
///     <para>
///         <b>Off unless requested, and drawn once per pane.</b> Registered by the export session only
///         when <c>ExportRequest.LayerIds</c> names <c>hud.clock</c>; an export never burns in a
///         scoreboard by accident. The compositor renders every layer once per band, so a clock repeated
///         on each floor of a two-level Nuke export would be wrong. It draws only in the band whose top
///         edge is the host's: exactly one pane under any tiling layout, plus the single-pane case, whose
///         default snapshot has a zero rectangle.
///     </para>
/// </summary>
public sealed class ClockLayer : ISceneLayer
{
    /// <summary>
    ///     Secondary HUD text: the round caption, a kill row's middle run, a card's weapon and K/D/A.
    ///     Shared by all three HUD layers because it is one typographic role, not three.
    /// </summary>
    internal const uint DimTextArgb = 0xFF9AA4AFu;

    // Text drawn ON a team-coloured fill; DrawScoreBox's caller says why it is near-black.
    private const uint OnTeamArgb = 0xFF12161Au;

    private readonly Dictionary<CountdownKey, string> _countdowns = new(256);
    private readonly IHudDataSource _data;
    private readonly Dictionary<int, string> _defuses = new(128);
    private readonly bool _ownsText;
    private readonly SKPaint _paint;
    private readonly Dictionary<int, string> _rounds = new(64);
    private readonly string[] _scores = new string[128];
    private readonly HudStyle _style;
    private readonly TextBlobCache _text;

    private HudSnapshot _snapshot = HudSnapshot.Empty;

    /// <summary>Creates the layer.</summary>
    /// <param name="data">The tick → HUD state function.</param>
    /// <param name="style">Colours and metrics; the shipped look when null.</param>
    /// <param name="text">A shared blob cache; a private one when null, disposed with the layer.</param>
    public ClockLayer(IHudDataSource data, HudStyle? style = null, TextBlobCache? text = null)
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
    public string Id => SceneLayerIds.HudClock;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Hud;

    /// <inheritdoc />
    public int Order => 70;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame)
    {
        // The read happens here, once, rather than once per pane in Render: At() is a pure function of
        // tick, but calling it three times a frame on a three-level map is three times the work for the
        // same answer.
        _snapshot = _data.At(time.Tick);
        return false;
    }

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (!IsTopBand(ctx))
        {
            return;
        }

        float scoreSize = _style.FontSizePx * 1.15f;
        float countdownSize = _style.FontSizePx * 1.75f;
        float captionSize = _style.FontSizePx * 0.8f;

        if (_text.Get(CountdownLine(_snapshot), countdownSize) is not { } clock ||
            _text.Get(Score(_snapshot.TScore), scoreSize) is not { } tScore ||
            _text.Get(Score(_snapshot.CtScore), scoreSize) is not { } ctScore)
        {
            return;
        }

        // ShapedText.Width is the ADVANCE and Height is one LINE BOX, so every rectangle below is derived
        // from the text's layout box rather than from one that happens to contain its pixels. The panel is
        // written as terms that sum to its own height, so it cannot drift out of agreement with what it
        // wraps.
        float padX = _style.MarginPx * 0.5f;
        float padY = _style.MarginPx * 0.5f;
        float gap = _style.MarginPx * 0.75f;

        float boxW = Math.Max(tScore.Width, ctScore.Width) + padX * 2;
        float boxH = tScore.Height + padY;
        float topRowH = Math.Max(boxH, clock.Height);

        ShapedText? caption = _text.Get(Round(_snapshot.RoundNumber), captionSize);
        ShapedText? defuse = DefuseLine(captionSize);

        float centreX = ctx.PaneBounds.MidX;
        float panelW = boxW * 2 + gap * 2 + clock.Width + _style.MarginPx * 2;

        // The two captions are narrower than the top row at every size the style can take, but the panel
        // is sized from what it actually holds. That observation stops being true the first time someone
        // localises "DEFUSING".
        panelW = Math.Max(panelW, Widest(caption, defuse) + _style.MarginPx * 2);

        float panelH = padY + topRowH
                            + (caption is { } cap ? cap.Height : 0)
                            + (defuse is { } def ? def.Height + 1f : 0)
                            + padY;
        float panelTop = _style.MarginPx * 0.5f;

        _paint.Color = new SKColor(_style.PanelArgb);
        canvas.DrawRoundRect(
            new SKRect(centreX - panelW / 2, panelTop, centreX + panelW / 2, panelTop + panelH),
            5f, 5f, _paint);

        // Score boxes are filled with the SAME team tokens the markers use, so "who is 7" needs no legend
        // and needs no second colour vocabulary to learn. The figure on them is near-black rather than the
        // panel's near-white: both tokens are light, and white-on-team is the unreadable pairing.
        float rowTop = panelTop + padY;
        float clockLeft = centreX - clock.Width / 2;
        DrawScoreBox(canvas, tScore, clockLeft - gap - boxW, rowTop, boxW, boxH, topRowH,
            ctx.Palette.TeamT);
        DrawScoreBox(canvas, ctScore, clockLeft + clock.Width + gap, rowTop, boxW, boxH, topRowH,
            ctx.Palette.TeamCt);

        // A ticking C4 owns the main countdown and is drawn in the bomb colour, because "0:34" meaning
        // "the round ends" and "0:34" meaning "the site goes up" are not the same number.
        _paint.Color = _snapshot.BombTicking ? ctx.Palette.BombDetonation : new SKColor(_style.TextArgb);
        (float cx, float cy) = clock.OriginForTopLeft(clockLeft, rowTop + (topRowH - clock.Height) / 2);
        canvas.DrawText(clock.Blob, cx, cy, _paint);

        float below = rowTop + topRowH;
        if (caption is { } roundCaption)
        {
            _paint.Color = new SKColor(DimTextArgb);
            (float rx, float ry) = roundCaption.OriginForTopLeft(
                centreX - roundCaption.Width / 2, below);
            canvas.DrawText(roundCaption.Blob, rx, ry, _paint);
            below += roundCaption.Height;
        }

        if (defuse is { } race)
        {
            // The race, decided rather than reported: a defuse that completes before the blow is drawn in
            // the defuse colour, one that does not in the detonation colour. Two numbers a viewer would
            // otherwise have to subtract in their head, in the frame they have to do it.
            // No countdown to race means no race — NaN loses every comparison, and painting a defuse red
            // because nothing is ticking would say the exact opposite of what is happening.
            bool wins = double.IsNaN(_snapshot.CountdownSeconds)
                        || _snapshot.DefuseSeconds <= _snapshot.CountdownSeconds;
            _paint.Color = wins ? ctx.Palette.BombDefuse : ctx.Palette.BombDetonation;
            (float dx, float dy) = race.OriginForTopLeft(centreX - race.Width / 2, below + 1f);
            canvas.DrawText(race.Blob, dx, dy, _paint);
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

    private static float Widest(ShapedText? a, ShapedText? b) =>
        Math.Max(a is { } first ? first.Width : 0, b is { } second ? second.Width : 0);

    // One side's score on its team token, vertically centred against the countdown beside it.
    private void DrawScoreBox(SKCanvas canvas, ShapedText score, float left, float rowTop, float boxW,
        float boxH, float rowH, SKColor team)
    {
        float top = rowTop + (rowH - boxH) / 2;
        _paint.Color = team;
        canvas.DrawRoundRect(new SKRect(left, top, left + boxW, top + boxH), 3f, 3f, _paint);

        _paint.Color = new SKColor(OnTeamArgb);
        (float x, float y) = score.OriginForTopLeft(
            left + (boxW - score.Width) / 2, top + (boxH - score.Height) / 2);
        canvas.DrawText(score.Blob, x, y, _paint);
    }

    /// <summary>
    ///     True for the one pane whose top edge is the host's. A default (single-pane) snapshot has a zero
    ///     rectangle, so the un-banded render path draws too.
    /// </summary>
    /// <param name="ctx">The pane being drawn.</param>
    internal static bool IsTopBand(SceneRenderContext ctx) => ctx.Pane.ViewportRect.Top <= 0.5f;

    // A score is a small non-negative integer, so the whole domain is an array — no dictionary, no
    // formatting past the first time a number is reached. Anything outside overtime's plausible range
    // formats every frame, which is the correct trade for an input that cannot happen in a real demo.
    private string Score(int score) => score is >= 0 && score < 128
        ? _scores[score] ??= score.ToString(CultureInfo.InvariantCulture)
        : score.ToString(CultureInfo.InvariantCulture);

    // RoundNumber arrives as display text ("13" or the "—" placeholder), so the caption is memoised on the
    // round it names rather than on the string: one entry per round, not one per distinct rendering.
    private string Round(string roundNumber)
    {
        if (!int.TryParse(roundNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out int round))
        {
            return "ROUND —";
        }

        if (_rounds.TryGetValue(round, out string? cached))
        {
            return cached;
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"ROUND {round}");
        _rounds[round] = text;
        return text;
    }

    // The defuse half of the race, at tenth-second resolution — a 5-second kit defuse rendered in whole
    // seconds spends a fifth of its life on each number, which is not a countdown a viewer can read.
    private ShapedText? DefuseLine(float sizePx)
    {
        double seconds = _snapshot.DefuseSeconds;
        if (!_snapshot.DefuseInProgress || double.IsNaN(seconds) || seconds < 0)
        {
            return null;
        }

        int tenths = (int)Math.Ceiling(seconds * 10);
        if (!_defuses.TryGetValue(tenths, out string? text))
        {
            if (_defuses.Count > 4096)
            {
                _defuses.Clear();
            }

            text = string.Create(CultureInfo.InvariantCulture, $"DEFUSING {tenths / 10}.{tenths % 10}");
            _defuses[tenths] = text;
        }

        return _text.Get(text, sizePx);
    }

    private string CountdownLine(HudSnapshot snapshot)
    {
        double seconds = snapshot.CountdownSeconds;
        if (double.IsNaN(seconds) || seconds < 0)
        {
            return "—";
        }

        // Whole seconds, rounded UP: a clock showing 0:00 while a round is still running reads as a bug.
        int whole = (int)Math.Ceiling(seconds);
        CountdownKey key = new(whole, snapshot.BombTicking);
        if (_countdowns.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
        if (_countdowns.Count > 4096)
        {
            // A demo cannot produce this many distinct clock strings; a caller feeding nonsense ticks
            // can. Bounded so a HUD cache can never be the reason an export runs out of memory.
            _countdowns.Clear();
        }

        _countdowns[key] = text;
        return text;
    }

    private readonly record struct CountdownKey(int Seconds, bool BombTicking);
}
