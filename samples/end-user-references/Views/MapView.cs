#region

using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using DemoViewer.NET.Models;

#endregion

namespace DemoViewer.NET.Views;

/// <summary>
///     Custom Avalonia control that renders player positions as coloured dots on a dark canvas.
///     Click a dot to select the corresponding <see cref="PlayerDot" />.
/// </summary>
public class MapView : Control
{
    // ── Brushes / pens (static, reused per frame) ─────────────────────────────

    private static readonly IBrush BgBrush = new SolidColorBrush(Color.Parse("#111820"));
    private static readonly IBrush CtBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly IBrush CtOutlineBrush = new SolidColorBrush(Color.Parse("#B3E5FC"));
    private static readonly IBrush DeadBrush = new SolidColorBrush(Color.Parse("#4A4A4A"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush HealthBg = new SolidColorBrush(Color.Parse("#333333"));

    private static readonly IBrush HealthFull = new SolidColorBrush(Color.Parse("#66BB6A"));
    private static readonly IBrush HealthLow = new SolidColorBrush(Color.Parse("#EF5350"));
    private static readonly IBrush HealthMid = new SolidColorBrush(Color.Parse("#FFA726"));

    private static readonly Typeface MonoFace = new("Consolas");
    // ── Avalonia Properties ────────────────────────────────────────────────────

    public static readonly StyledProperty<List<PlayerDot>?> PlayersProperty =
        AvaloniaProperty.Register<MapView, List<PlayerDot>?>(nameof(Players));

    public static readonly StyledProperty<PlayerDot?> SelectedPlayerProperty =
        AvaloniaProperty.Register<MapView, PlayerDot?>(nameof(SelectedPlayer),
            defaultBindingMode: BindingMode.TwoWay);

    private static readonly IBrush SelOutlineBrush = new SolidColorBrush(Colors.White);
    private static readonly IBrush TBrush = new SolidColorBrush(Color.Parse("#FFD54F"));
    private static readonly IBrush TOutlineBrush = new SolidColorBrush(Color.Parse("#FFF9C4"));
    private bool _hasTransform;
    private float _minX, _minY, _maxY;

    // ── Cached transform parameters (shared between Render and hit-test) ───────

    private double _scale, _offsetX, _offsetY;

    static MapView()
    {
        AffectsRender<MapView>(PlayersProperty, SelectedPlayerProperty);
        FocusableProperty.OverrideDefaultValue<MapView>(true);
    }

    public List<PlayerDot>? Players
    {
        get => GetValue(PlayersProperty);
        set => SetValue(PlayersProperty, value);
    }

    public PlayerDot? SelectedPlayer
    {
        get => GetValue(SelectedPlayerProperty);
        set => SetValue(SelectedPlayerProperty, value);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        Rect b = Bounds;
        List<PlayerDot>? players = Players;

        context.FillRectangle(BgBrush, new Rect(b.Size));

        if (players is null || players.Count == 0)
        {
            _hasTransform = false;
            FormattedText msg = new("No player positions — seek to a tick with active players",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MonoFace, 11, Brushes.DimGray);
            context.DrawText(msg, new Point((b.Width - msg.Width) / 2, (b.Height - msg.Height) / 2));
            return;
        }

        // ── Compute world bounding box ─────────────────────────────────────
        float minX = players.Min(p => p.WorldPos.X);
        float maxX = players.Max(p => p.WorldPos.X);
        float minY = players.Min(p => p.WorldPos.Y);
        float maxY = players.Max(p => p.WorldPos.Y);

        float padX = Math.Max((maxX - minX) * 0.15f, 200f);
        float padY = Math.Max((maxY - minY) * 0.15f, 200f);
        _minX = minX - padX;
        float maxXP = maxX + padX;
        _minY = minY - padY;
        _maxY = maxY + padY;

        double scaleX = b.Width / (maxXP - _minX);
        double scaleY = b.Height / (_maxY - _minY);
        _scale = Math.Min(scaleX, scaleY);
        _offsetX = (b.Width - (maxXP - _minX) * _scale) / 2;
        _offsetY = (b.Height - (_maxY - _minY) * _scale) / 2;
        _hasTransform = true;

        // ── World-origin crosshair ─────────────────────────────────────────
        Point origin = WorldToScreen(Vector3.Zero);
        Pen gridPen = new(GridBrush, 1);
        if (origin.X >= 0 && origin.X <= b.Width)
            context.DrawLine(gridPen, new Point(origin.X, 0), new Point(origin.X, b.Height));
        if (origin.Y >= 0 && origin.Y <= b.Height)
            context.DrawLine(gridPen, new Point(0, origin.Y), new Point(b.Width, origin.Y));

        // ── Draw players ───────────────────────────────────────────────────
        PlayerDot? selected = SelectedPlayer;

        foreach (PlayerDot p in players)
        {
            Point sp = WorldToScreen(p.WorldPos);
            bool isSel = ReferenceEquals(p, selected);
            double r = p.IsAlive ? 6.0 : 4.0;
            if (isSel) r += 2.0;

            IBrush fill = !p.IsAlive ? DeadBrush : p.TeamNum == 3 ? CtBrush : TBrush;
            IBrush outline = isSel ? SelOutlineBrush
                : !p.IsAlive ? DeadBrush
                : p.TeamNum == 3 ? CtOutlineBrush : TOutlineBrush;
            Pen pen = new(outline, isSel ? 2.0 : 1.5);

            context.DrawEllipse(fill, pen, sp, r, r);

            // ── Health bar ─────────────────────────────────────────────────
            if (p.IsAlive && p.Health > 0)
            {
                const double BarW = 16, BarH = 3;
                double barX = sp.X - BarW / 2;
                double barY = sp.Y - r - BarH - 2;
                double fillW = BarW * Math.Clamp(p.Health / 100.0, 0, 1);
                context.FillRectangle(HealthBg, new Rect(barX, barY, BarW, BarH));
                IBrush hBrush = p.Health > 50 ? HealthFull : p.Health > 25 ? HealthMid : HealthLow;
                if (fillW > 0)
                    context.FillRectangle(hBrush, new Rect(barX, barY, fillW, BarH));
            }

            // ── Label ──────────────────────────────────────────────────────
            string label = p.IsAlive ? p.DisplayName : $"{p.DisplayName} ✕";
            IBrush labelClr = !p.IsAlive ? Brushes.DimGray : isSel ? Brushes.White : Brushes.LightGray;
            FormattedText ft = new(label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoFace, 9, labelClr);
            context.DrawText(ft, new Point(sp.X - ft.Width / 2, sp.Y + r + 2));
        }
    }

    // ── Pointer handling ───────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_hasTransform) return;

        Point pt = e.GetPosition(this);
        List<PlayerDot>? players = Players;
        if (players is null)
        {
            SelectedPlayer = null;
            return;
        }

        PlayerDot? best = null;
        double bestDist = 12.0; // px hit radius

        foreach (PlayerDot p in players)
        {
            Point sp = WorldToScreen(p.WorldPos);
            double d = Math.Sqrt(Math.Pow(pt.X - sp.X, 2) + Math.Pow(pt.Y - sp.Y, 2));
            if (d < bestDist)
            {
                best = p;
                bestDist = d;
            }
        }

        SelectedPlayer = best;
        e.Handled = true;
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private Point WorldToScreen(Vector3 pos) => new(
        (pos.X - _minX) * _scale + _offsetX,
        (_maxY - pos.Y) * _scale + _offsetY); // Y axis is inverted
}
