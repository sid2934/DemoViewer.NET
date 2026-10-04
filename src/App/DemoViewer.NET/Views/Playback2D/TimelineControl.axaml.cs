#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using DemoViewer.NET.Modules.Playback2D.Timeline;

#endregion

namespace DemoViewer.NET.Views.Playback2D;

/// <summary>
///     The scrub / rounds / markers chrome docked under the 2D viewport. Pure plumbing: the code-behind owns
///     only pointer capture and the width handshake, and every seek goes out as a view-model request that
///     the tab forwards to the shared clock. The control never moves playback itself.
/// </summary>
public partial class TimelineControl : UserControl
{
    private readonly Panel? _scrubBar;
    private bool _scrubbing;

    public TimelineControl()
    {
        InitializeComponent();
        _scrubBar = this.FindControl<Panel>("ScrubBar");

        // The layout math is in pixels, so the view-model needs the bar's width. Pushing it here (rather
        // than binding it) keeps the re-layout on the size change and off every property read.
        if (_scrubBar is not null)
        {
            _scrubBar.SizeChanged += OnScrubSizeChanged;
        }
    }

    private Playback2DTimelineViewModel? ViewModel => DataContext as Playback2DTimelineViewModel;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnScrubSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.PixelWidth = e.NewSize.Width;
        }
    }

    private void OnScrubPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || _scrubBar is null)
        {
            return;
        }

        _scrubbing = true;
        e.Pointer.Capture(_scrubBar);
        vm.RequestSeek(e.GetPosition(_scrubBar).X);
        e.Handled = true;
    }

    private void OnScrubMoved(object? sender, PointerEventArgs e)
    {
        if (ViewModel is not { } vm || _scrubBar is null)
        {
            return;
        }

        double x = e.GetPosition(_scrubBar).X;
        if (_scrubbing)
        {
            // Raw pushes are safe: the host's 150 ms debounce plus latest-wins coalescing (and LiveSync's
            // own settle downstream) absorb a drag burst.
            vm.RequestSeek(x);
        }
        else
        {
            vm.UpdateHover(x);
        }
    }

    private void OnScrubReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_scrubbing)
        {
            return;
        }

        _scrubbing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnScrubExited(object? sender, PointerEventArgs e) => ViewModel?.ClearHover();

    /// <summary>The band menu opened by the last right press, or null. For tests; the control keeps no other hold on it.</summary>
    internal ContextMenu? LastBandMenu { get; private set; }

    // A round band seeks to its FIRST frame, not to the pixel under the cursor. The press goes through
    // the view-model so a tag band can also pick its tag for Label Mode. A right press is the band's menu
    // instead, whatever the contributors offer for it, and does not seek.
    private void OnBandPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control { DataContext: TimelineBandViewModel band } control)
        {
            return;
        }

        if (e.GetCurrentPoint(control).Properties.IsRightButtonPressed)
        {
            IReadOnlyList<MenuEntry> entries = vm.MenuFor(band);
            if (entries.Count > 0)
            {
                List<MenuItem> items = new(entries.Count);
                foreach (MenuEntry entry in entries)
                {
                    MenuItem item = new() { Header = entry.Header };
                    item.Click += (_, _) => entry.Run();
                    items.Add(item);
                }

                ContextMenu menu = new() { ItemsSource = items };
                LastBandMenu = menu;
                menu.Open(control);
            }

            e.Handled = true;
            return;
        }

        vm.PressBand(band);
        e.Handled = true;
    }

    // Empty lane: bands mark their own presses handled, so what reaches here missed every band.
    private void OnLanePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || ViewModel is not { } vm || sender is not Control lane
            || !e.GetCurrentPoint(lane).Properties.IsLeftButtonPressed)
        {
            return;
        }

        vm.RequestLaneLabel(e.GetPosition(lane).X);
        e.Handled = true;
    }

    private bool _draggingStart;

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle)
        {
            return;
        }

        _draggingStart = handle.Name == "EditStartHandle";
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control handle || !ReferenceEquals(e.Pointer.Captured, handle)
            || this.FindControl<Panel>("LaneHost") is not { } lane)
        {
            return;
        }

        vm.DragEditEdge(_draggingStart, e.GetPosition(lane).X);
        e.Handled = true;
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
