#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Layers;

/// <summary>
///     The export HUD's scoreboard strip: round number, T/CT score, and the main countdown — the round
///     clock, or the C4 detonation countdown once the bomb is ticking, exactly as <c>SceneGameInfo</c>
///     defines it.
///     <para>
///         <b>Off unless requested.</b> Registered by the export session only when
///         <c>ExportRequest.LayerIds</c> names <c>hud.clock</c>; an export never burns in a scoreboard by
///         accident.
///     </para>
///     <para>
///         <b>One pane, not one per pane.</b> The compositor renders every layer once per band, and a
///         clock repeated on each floor of a two-level Nuke export would be wrong. It draws only in the
///         band whose top edge is the host's, which is exactly one pane under any tiling layout — and is
///         also the single-pane case, whose default snapshot has a zero rectangle.
///     </para>
///     <para>
///         <b>No per-frame shaping</b> (design §6): strings are composed into two small keyed caches and
///         shaped once by <see cref="TextBlobCache" />.
///     </para>
/// </summary>
public sealed class ClockLayer : ISceneLayer
{
    private readonly Dictionary<CountdownKey, string> _countdowns = new(256);
    private readonly IHudDataSource _data;
    private readonly bool _ownsText;
    private readonly SKPaint _paint;
    private readonly Dictionary<ScoreKey, string> _scores = new(64);
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

        string score = ScoreLine(_snapshot);
        string countdown = CountdownLine(_snapshot);

        ShapedText? scoreText = _text.Get(score, _style.FontSizePx);
        ShapedText? countdownText = _text.Get(countdown, _style.FontSizePx * 1.35f);
        if (scoreText is not { } scoreShaped || countdownText is not { } countdownShaped)
        {
            return;
        }

        float centreX = ctx.PaneBounds.MidX;
        float panelW = Math.Max(scoreShaped.Width, countdownShaped.Width) + _style.MarginPx * 2;
        float panelH = scoreShaped.Height + countdownShaped.Height + _style.MarginPx * 2.2f;
        float panelTop = _style.MarginPx * 0.5f;

        _paint.Color = new SKColor(_style.PanelArgb);
        canvas.DrawRoundRect(
            new SKRect(centreX - panelW / 2, panelTop, centreX + panelW / 2, panelTop + panelH),
            4f, 4f, _paint);

        _paint.Color = new SKColor(_style.TextArgb);
        (float sx, float sy) = scoreShaped.OriginForTopLeft(
            centreX - scoreShaped.Width / 2, panelTop + _style.MarginPx * 0.6f);
        canvas.DrawText(scoreShaped.Blob, sx, sy, _paint);

        // A ticking C4 owns the main countdown and is drawn in the bomb colour, because "0:34" meaning
        // "the round ends" and "0:34" meaning "the site goes up" are not the same number.
        _paint.Color = _snapshot.BombTicking ? ctx.Palette.BombDetonation : new SKColor(_style.TextArgb);
        (float cx, float cy) = countdownShaped.OriginForTopLeft(
            centreX - countdownShaped.Width / 2,
            panelTop + _style.MarginPx * 0.6f + scoreShaped.Height + _style.MarginPx * 0.5f);
        canvas.DrawText(countdownShaped.Blob, cx, cy, _paint);
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
    ///     True for the one pane whose top edge is the host's. A default (single-pane) snapshot has a zero
    ///     rectangle, so the un-banded render path draws too.
    /// </summary>
    /// <param name="ctx">The pane being drawn.</param>
    internal static bool IsTopBand(SceneRenderContext ctx) => ctx.Pane.ViewportRect.Top <= 0.5f;

    private string ScoreLine(HudSnapshot snapshot)
    {
        ScoreKey key = new(snapshot.RoundNumber, snapshot.TScore, snapshot.CtScore);
        if (_scores.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        string line = string.Create(CultureInfo.InvariantCulture,
            $"Round {snapshot.RoundNumber}    T {snapshot.TScore} : {snapshot.CtScore} CT");
        _scores[key] = line;
        return line;
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

    private readonly record struct ScoreKey(string Round, int T, int Ct);

    private readonly record struct CountdownKey(int Seconds, bool BombTicking);
}
