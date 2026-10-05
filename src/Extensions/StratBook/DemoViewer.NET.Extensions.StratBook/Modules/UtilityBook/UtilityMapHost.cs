#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Utility;
using DemoViewer.NET.ViewModels.UtilityBook;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     The Utility Book map: a <see cref="MapView" /> with the <see cref="UtilityMapLayer" /> over the radar.
///     A drag pans, the wheel zooms, and a left click that did not move hits the same geometry the layer
///     draws: a throw position of the focused group first, then a landing icon, else empty map, which steps
///     back one level.
/// </summary>
public sealed class UtilityMapHost : Decorator
{
    // A press that moves further than this before the release is a pan, not a click.
    private const double ClickSlopPx = 4;

    private readonly MapView _map = new();
    private MapIcons? _icons;
    private IDisposable? _layer;
    private Point? _pressAt;
    private UtilityBookTabViewModel? _vm;

    /// <summary>Creates the map.</summary>
    public UtilityMapHost()
    {
        Child = _map;
        _map.EscapePressed += (_, _) => _vm?.Back();
        _map.AddHandler(PointerPressedEvent, OnMapPressed, RoutingStrategies.Bubble, true);
        _map.AddHandler(PointerReleasedEvent, OnMapReleased, RoutingStrategies.Bubble, true);
    }

    /// <summary>The map view this map is built on. For tests.</summary>
    internal MapView Map => _map;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null)
        {
            Attach(DataContext as UtilityBookTabViewModel);
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as UtilityBookTabViewModel);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
    }

    private void Attach(UtilityBookTabViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.MapChanged -= OnMapChanged;
            _vm.Document.Changed -= OnDocumentChanged;
        }

        // The layer leaves the scene before its icons are released.
        _layer?.Dispose();
        _layer = null;
        _vm = vm;
        if (vm is null)
        {
            _icons?.Dispose();
            _icons = null;
            return;
        }

        _layer = _map.AddLayer(SceneLayerIds.Utility, () => new UtilityMapLayer(vm.Document, _icons ??= new MapIcons()));
        vm.MapChanged += OnMapChanged;
        vm.Document.Changed += OnDocumentChanged;
        OnMapChanged();
    }

    private void OnMapPressed(object? sender, PointerPressedEventArgs e) =>
        _pressAt = e.GetCurrentPoint(_map).Properties.IsLeftButtonPressed ? e.GetPosition(_map) : null;

    private void OnMapReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressAt is not { } pressed || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        _pressAt = null;
        Point released = e.GetPosition(_map);
        if (Math.Abs(released.X - pressed.X) > ClickSlopPx || Math.Abs(released.Y - pressed.Y) > ClickSlopPx)
        {
            return;
        }

        Click((float)released.X, (float)released.Y);
    }

    /// <summary>
    ///     Resolves a click at a host point, the way a real release does. Internal so a headless test can
    ///     drive the hit test without synthesising pointer events.
    /// </summary>
    /// <param name="x">Host X.</param>
    /// <param name="y">Host Y.</param>
    internal void Click(float x, float y)
    {
        if (_vm is null || _map.PaneAt(new Point(x, y)) is not { } pane || _map.Space is not { } space)
        {
            return;
        }

        SKPoint local = new(x - pane.ViewportRect.Left, y - pane.ViewportRect.Top);
        bool Belongs(double z) => pane.LevelIndex < 0 || space.Levels.Count <= 1 || space.LevelIndexFor(z) == pane.LevelIndex;

        IReadOnlyList<UtilityThrow> throws = UtilityMapLayer.ThrowsAt(_vm.Document, pane.Camera.Current, Belongs, local);
        if (throws.Count > 0)
        {
            // A second click on a stack moves to the next one under the pointer; on a lone disc it toggles.
            string? current = throws.FirstOrDefault(t => t.Selected)?.Id;
            string next = Next([.. throws.Select(t => t.Id)], current);
            _vm.CardOnLeft = x > Bounds.Width / 2;
            if (throws.Count > 1 && current is not null)
            {
                _vm.SelectThrow(next);
            }
            else
            {
                _vm.ClickThrow(next);
            }

            return;
        }

        IReadOnlyList<UtilityLanding> landings = UtilityMapLayer.LandingsAt(_vm.Document, pane.Camera.Current, Belongs, local);
        if (landings.Count > 0)
        {
            string? current = landings.Where(l => l.Focused).Select(l => l.Id).FirstOrDefault();
            string next = Next([.. landings.Select(l => l.Id)], current);
            if (landings.Count > 1 && current is not null)
            {
                _vm.FocusLanding(next);
            }
            else
            {
                _vm.ClickLanding(next);
            }
        }
        else if (_vm.HasFocus)
        {
            _vm.Back();
        }
    }

    // The id after the current one in hit order, wrapping; the topmost when nothing under the pointer is current.
    private static string Next(IReadOnlyList<string> ids, string? current)
    {
        int at = current is null ? -1 : ids.ToList().IndexOf(current);
        return ids[(at + 1) % ids.Count];
    }

    /// <summary>The host point a world point draws at on the pane of its floor, or null. For tests.</summary>
    /// <param name="worldX">World X.</param>
    /// <param name="worldY">World Y.</param>
    /// <param name="worldZ">World Z.</param>
    internal Point? HostPointOf(float worldX, float worldY, float worldZ)
    {
        if (_map.Space is not { } space)
        {
            return null;
        }

        foreach (LevelPane pane in _map.Panes)
        {
            if (pane.LevelIndex >= 0 && space.Levels.Count > 1 && space.LevelIndexFor(worldZ) != pane.LevelIndex)
            {
                continue;
            }

            (double x, double y) = pane.Camera.Current.WorldToScreen(worldX, worldY);
            return new Point(x + pane.ViewportRect.Left, y + pane.ViewportRect.Top);
        }

        return null;
    }

    private void OnDocumentChanged() => _map.Invalidate();

    private void OnMapChanged() => _map.MapName = _vm?.MapName is { Length: > 0 } map ? map : null;
}
