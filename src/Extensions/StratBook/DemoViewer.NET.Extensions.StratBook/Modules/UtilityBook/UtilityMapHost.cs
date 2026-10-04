#region

using Avalonia;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Utility;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using DemoViewer.NET.ViewModels.UtilityBook;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>
///     The Utility Book map: a <see cref="MapSceneHost" /> with the <see cref="UtilityMapLayer" /> over the
///     radar. A drag pans, the wheel zooms, and a left click that did not move hits the same geometry the
///     layer draws: a throw position of the focused group first, then a landing icon, else empty map, which
///     steps back one level.
/// </summary>
public sealed class UtilityMapHost : MapSceneHost
{
    // A press that moves further than this before the release is a pan, not a click.
    private const double ClickSlopPx = 4;

    private SkiaIconSource? _icons;
    private UtilityMapLayer? _layer;
    private Point? _pressAt;
    private UtilityBookTabViewModel? _vm;

    /// <inheritdoc />
    protected override void AddLayers(SceneCompositor compositor)
    {
        // A rebuilt scene has lost the view model's layer; the next attach adds it again.
        _layer = null;
        _vm = null;
    }

    /// <inheritdoc />
    protected override void OnEscape() => _vm?.Back();

    /// <inheritdoc />
    protected override void AttachDataContext(object? dataContext)
    {
        UtilityBookTabViewModel? vm = dataContext as UtilityBookTabViewModel;
        if (ReferenceEquals(_vm, vm))
        {
            return;
        }

        if (_vm is not null)
        {
            _vm.MapChanged -= OnMapChanged;
            _vm.Document.Changed -= OnDocumentChanged;
        }

        _vm = vm;
        WithCompositor(compositor =>
        {
            if (_layer is not null)
            {
                compositor.Remove(SceneLayerIds.Utility);
                _layer = null;
            }

            if (vm is not null)
            {
                _icons ??= new SkiaIconSource();
                _layer = new UtilityMapLayer(vm.Document, _icons);
                compositor.Add(_layer);
            }
        });

        if (vm is null)
        {
            _icons?.Dispose();
            _icons = null;
            return;
        }

        vm.MapChanged += OnMapChanged;
        vm.Document.Changed += OnDocumentChanged;
        OnMapChanged();
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _pressAt = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
        base.OnPointerPressed(e);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerReleased(e);
        if (_pressAt is not { } pressed || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        _pressAt = null;
        Point released = e.GetPosition(this);
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
        if (_vm is null || PaneAtHostPoint(x, y) is not { } pane)
        {
            return;
        }

        SKPoint local = new(x - pane.ViewportRect.Left, y - pane.ViewportRect.Top);
        MapSpace space = LevelSpace;
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
        MapSpace space = LevelSpace;
        foreach (LevelPane pane in Panes.Panes)
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

    private void OnDocumentChanged() => InvalidateVisual();

    private void OnMapChanged() => BindMap(_vm?.MapName ?? "", _vm?.MapAsset);
}
