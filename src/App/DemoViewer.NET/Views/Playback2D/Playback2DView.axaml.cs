#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DemoViewer.NET.Modules.Playback2D;

#endregion

namespace DemoViewer.NET.Views.Playback2D;

/// <summary>
///     The 2D Playback tab's View. DataContext is the descriptor's
///     <see cref="Playback2DTabViewModel" />. Hosts the custom-drawn <see cref="Playback2DViewport" /> plus
///     the camera-mode selector (#2): a <see cref="SplitButton" /> whose main action is apply-once Fit and
///     whose dropdown caret opens the MODE menu (Fit / Alive / Map / Follow Player). The Follow Player
///     submenu is populated on open from the VM's current players; Map surfaces its approximation caveat.
/// </summary>
public partial class Playback2DView : UserControl
{
    private readonly MenuItem? _followMenuItem;
    private readonly TextBlock? _mapApproxNote;
    private readonly TextBlock? _modeLabel;
    private readonly MenuFlyout? _modeMenuFlyout;
    private readonly Playback2DViewport? _viewport;

    public Playback2DView()
    {
        InitializeComponent();
        _viewport = this.FindControl<Playback2DViewport>("Viewport");
        _followMenuItem = this.FindControl<MenuItem>("FollowMenuItem");
        _modeLabel = this.FindControl<TextBlock>("ModeLabel");
        _mapApproxNote = this.FindControl<TextBlock>("MapApproxNote");

        // The MenuFlyout is not a Control (no FindControl), so reach it off the SplitButton's Flyout.
        _modeMenuFlyout = this.FindControl<SplitButton>("ModeButton")?.Flyout as MenuFlyout;

        // Rebuild the Follow-Player submenu each time the mode menu opens, so it reflects the live roster.
        if (_modeMenuFlyout is not null)
        {
            _modeMenuFlyout.Opened += OnModeMenuOpened;
        }

        // Right-click the mode button also opens the mode menu (in addition to the dropdown caret), so the
        // "Fit button → mode selector" works as a context menu too.
        SplitButton? modeButton = this.FindControl<SplitButton>("ModeButton");
        if (modeButton is not null)
        {
            modeButton.AddHandler(PointerReleasedEvent, OnModeButtonPointerReleased,
                RoutingStrategies.Tunnel);
        }
    }

    private void OnModeButtonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || _modeMenuFlyout is null
                                                           || sender is not Control anchor)
        {
            return;
        }

        _modeMenuFlyout.ShowAt(anchor);
        e.Handled = true;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Left-click the main button = apply-once Fit (the original behaviour); also resets the mode to Fit.
    private void OnFitClick(object? sender, RoutedEventArgs e)
    {
        _viewport?.FitToExtent();
        SetMode(CameraMode.Fit);
    }

    private void OnModeFit(object? sender, RoutedEventArgs e)
    {
        _viewport?.FitToExtent();
        SetMode(CameraMode.Fit);
    }

    private void OnModeAlive(object? sender, RoutedEventArgs e) => SetMode(CameraMode.Alive);

    private void OnModeMap(object? sender, RoutedEventArgs e) => SetMode(CameraMode.Map);

    private void SetMode(CameraMode mode)
    {
        if (_viewport is not null)
        {
            _viewport.Mode = mode;
        }

        if (_modeLabel is not null)
        {
            _modeLabel.Text = $"mode: {ModeName(mode)}";
        }

        if (_mapApproxNote is not null)
        {
            // Only an approximation when the map doesn't publish real bounds; with m_vMinimapMins/Maxs
            // present Map frames the actual playable extent, so drop the caveat.
            bool realBounds = (DataContext as Playback2DTabViewModel)?.MapBounds is not null;
            _mapApproxNote.IsVisible = mode == CameraMode.Map && !realBounds;
        }
    }

    private static string ModeName(CameraMode mode) => mode switch
    {
        CameraMode.Fit => "Fit",
        CameraMode.Alive => "Alive",
        CameraMode.Map => "Map (approx.)",
        CameraMode.FollowPlayer => "Follow",
        _ => mode.ToString()
    };

    // Populate the Follow-Player submenu from the VM's current players each time the menu opens.
    private void OnModeMenuOpened(object? sender, EventArgs e)
    {
        if (_followMenuItem is null || DataContext is not Playback2DTabViewModel vm)
        {
            return;
        }

        List<MenuItem> items = new();
        foreach (FollowablePlayer p in vm.FollowablePlayers)
        {
            MenuItem item = new()
            {
                Header = p.Display
            };
            int slot = p.Slot;
            item.Click += (_, _) => FollowSlot(slot, p.Display);
            items.Add(item);
        }

        if (items.Count == 0)
        {
            items.Add(new MenuItem
            {
                Header = "(no players)",
                IsEnabled = false
            });
        }

        _followMenuItem.ItemsSource = items;
    }

    private void FollowSlot(int slot, string display)
    {
        if (_viewport is not null)
        {
            _viewport.FollowSlot = slot; // selecting a player implies FollowPlayer mode
        }

        // Surface the pick as an observable (live-sync mirrors it to CS2 spectating).
        (DataContext as Playback2DTabViewModel)?.NotifyFollowSlotChanged(slot);

        if (_modeLabel is not null)
        {
            _modeLabel.Text = $"mode: Follow {display}";
        }

        if (_mapApproxNote is not null)
        {
            _mapApproxNote.IsVisible = false;
        }
    }
}
