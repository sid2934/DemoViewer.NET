#region

using System.Globalization;
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
    private Playback2DTabViewModel? _boundViewModel;

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

        // Tunnel, not bubble: transport keys must win over whatever inside the playback surface has
        // focus (an overlay CheckBox would otherwise eat Space; the player-card ListBox would eat
        // Up/Down). Skipped while a text input has focus so a future in-tab field still types.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // Clicking the map focuses the surface, so the keymap starts working without a Tab press.
        AddHandler(PointerPressedEvent, OnSurfacePointerPressed, RoutingStrategies.Tunnel);

        Focusable = true;
        DataContextChanged += OnDataContextChanged;
        BindViewModel();
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => BindViewModel();

    // The VM is assigned after construction (and can be replaced), so the subscriptions are re-aimed
    // here rather than in the ctor. Unsubscribing the PREVIOUS instance is what keeps a rebuilt tab from
    // driving a stale view.
    private void BindViewModel()
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.FollowSlotChanged -= OnFollowSlotChanged;
            _boundViewModel.FitRequested -= OnFitRequested;
        }

        _boundViewModel = DataContext as Playback2DTabViewModel;

        if (_boundViewModel is not null)
        {
            _boundViewModel.FollowSlotChanged += OnFollowSlotChanged;
            _boundViewModel.FitRequested += OnFitRequested;

            // The View is DESTROYED on deactivation and rebuilt from the descriptor's ViewFactory on every
            // activation, while the tab VM is cached (WorkspaceTabDescriptor.Activate / .Deactivate). The
            // follow target therefore survives in the VM — card highlight, "requested" chip, FollowStatus —
            // over a fresh viewport that defaults to Fit. Re-projecting it here is what makes the VM the
            // single source of truth rather than half of one.
            if (_boundViewModel.FollowedSlot >= 0)
            {
                OnFollowSlotChanged(_boundViewModel.FollowedSlot);
            }
        }
    }

    private void OnFitRequested() => _viewport?.FitToExtent();

    // Mirrors the VM's single follow funnel onto the control. Setting FollowSlot implies FollowPlayer
    // mode; -1 clears the follow and re-fits.
    private void OnFollowSlotChanged(int slot)
    {
        if (slot < 0)
        {
            if (_viewport is not null)
            {
                _viewport.FollowSlot = -1;
            }

            SetMode(CameraMode.Fit);
            return;
        }

        if (_viewport is not null)
        {
            _viewport.FollowSlot = slot;
        }

        string display = _boundViewModel?.SelectedPlayer?.Name is { Length: > 0 } name
            ? name
            : slot.ToString(CultureInfo.InvariantCulture);

        if (_modeLabel is not null)
        {
            _modeLabel.Text = $"mode: Follow {display}";
        }

        if (_mapApproxNote is not null)
        {
            _mapApproxNote.IsVisible = false;
        }
    }

    private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsFocused)
        {
            Focus();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not Playback2DTabViewModel vm)
        {
            return;
        }

        // A focused text input owns every key: the tab has none today, but swallowing Space in a future
        // in-tab field is exactly the kind of bug a tunneling handler introduces silently.
        if (IsTextInputFocused())
        {
            return;
        }

        // toolActive is false throughout A1 — there is no pointer tool yet. B2 passes the router's state
        // here and its tool-scoped bindings then shadow Space / Esc without editing the table.
        if (Playback2DKeymap.TryResolve(e, false, out Playback2DAction action))
        {
            e.Handled = vm.ExecuteAction(action);
        }
    }

    private bool IsTextInputFocused()
    {
        object? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox or AutoCompleteBox;
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
            item.Click += (_, _) => FollowSlot(slot);
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

    // The SplitButton submenu pick goes through the VM's follow funnel like every other path; the viewport
    // mirror and the mode label are then driven by OnFollowSlotChanged, so a menu pick and a card pick
    // produce identical state.
    private void FollowSlot(int slot) =>
        (DataContext as Playback2DTabViewModel)?.NotifyFollowSlotChanged(slot);
}
