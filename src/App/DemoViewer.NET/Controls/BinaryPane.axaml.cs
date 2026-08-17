#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.Controls;

/// <summary>
///     Reusable hex viewer (F3.2) — the single, adopted hex surface. Implements the
///     windowed-virtualization paging that lets very large buffers render without
///     overloading Avalonia's layout engine, and tunnels byte-cell clicks to the VM
///     to drive the reverse byte → node loop (F5.2).
///     <para>
///         Backed by <see cref="HarvestHexViewModel" /> via DataContext. Replaces the
///         former <c>HarvestHexControl</c>; the VM is unchanged.
///     </para>
/// </summary>
public partial class BinaryPane : UserControl
{
    private bool _adjustingWindow;
    private ScrollViewer? _scrollViewer;
    private HarvestHexViewModel? _vm;

    /// <summary>Initializes a new <see cref="BinaryPane" /> instance.</summary>
    public BinaryPane()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        HexList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
        // F5.2 — tunnel byte-cell clicks to the VM. Tunneling so it fires before the
        // ListBox consumes the press for selection.
        HexList.AddHandler(PointerPressedEvent, OnHexPointerPressed, RoutingStrategies.Tunnel);
        // v0.6.0 code-color promotion: the depth ramp resolves HexSwatch* tokens ONCE per theme
        // (attach + live switch) — never per cell, which is the hot path.
        AttachedToVisualTree += (_, _) => ApplyHexPalette();
        ActualThemeVariantChanged += (_, _) => ApplyHexPalette();
    }

    // Resolves the 4-tier ramp from the theme (fallbacks = the historical Dark values) and
    // re-materializes the visible rows. L0–L2 reuse the pre-existing HexSwatch* tokens; L3 is the
    // v0.6.0 HexSwatchAncestorDeep addition.
    private void ApplyHexPalette()
    {
        HarvestHexViewModel.SetPalette(
            new SolidColorBrush(ThemeColors.Get("HexSwatchSelected", ActualThemeVariant, "#CC4C9EF5")),
            new SolidColorBrush(ThemeColors.Get("HexSwatchParent", ActualThemeVariant, "#8855BB8A")),
            new SolidColorBrush(ThemeColors.Get("HexSwatchAncestor", ActualThemeVariant, "#55C07C28")),
            new SolidColorBrush(ThemeColors.Get("HexSwatchAncestorDeep", ActualThemeVariant, "#33907890")));
        _vm?.RepaintHighlights();
    }

    private ScrollViewer? GetScrollViewer() =>
        _scrollViewer ??= HexList.FindDescendantOfType<ScrollViewer>();

    // ── DataContext wiring ────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as HarvestHexViewModel;
        _scrollViewer = null; // force re-discovery after context swap

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    // ── Byte-cell click → reverse byte → node mapping (F5.2) ──────────────────

    private void OnHexPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        // Walk up from the click source to the nearest element whose DataContext is a cell.
        for (Visual? v = e.Source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: HarvestHexCell cell })
            {
                if (cell.IsValid)
                {
                    _vm.RaiseByteClicked(cell.AbsoluteOffset);
                }

                return;
            }
        }
    }

    // ── Auto-advance / retreat on scroll ──────────────────────────────────────

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_adjustingWindow || _vm is null)
        {
            return;
        }

        ScrollViewer? sv = GetScrollViewer();
        if (sv is null || sv.Extent.Height <= 0 || HexList.ItemCount <= 0)
        {
            return;
        }

        double rowHeight = sv.Extent.Height / HexList.ItemCount;
        double topRow = sv.Offset.Y / rowHeight;
        double bottomRow = (sv.Offset.Y + sv.Viewport.Height) / rowHeight;

        if (bottomRow >= HarvestHexViewModel.WindowRows - HarvestHexViewModel.ChunkRows / 2
            && _vm.CanScrollForward)
        {
            ShiftWindow(true, sv);
        }
        else if (topRow <= HarvestHexViewModel.ChunkRows / 2.0 && _vm.CanScrollBack)
        {
            ShiftWindow(false, sv);
        }
    }

    // ── TargetRowIndex → ListBox scroll ──────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HarvestHexViewModel.TargetRowIndex) && _vm is not null)
        {
            ScrollToRow(_vm.TargetRowIndex);
        }
    }

    private void ScrollToRow(int rowIndex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ScrollViewer? sv = GetScrollViewer();
            if (sv is null || HexList.ItemCount <= 0 || sv.Extent.Height <= 0)
            {
                return;
            }

            double rowHeight = sv.Extent.Height / HexList.ItemCount;
            double targetY = rowIndex * rowHeight - sv.Viewport.Height / 2.0;
            sv.Offset = sv.Offset.WithY(Math.Max(0, targetY));
        }, DispatcherPriority.Render);
    }

    // ── Window shift with scroll compensation ────────────────────────────────

    private void ShiftWindow(bool advance, ScrollViewer sv)
    {
        _adjustingWindow = true;
        double savedOffset = sv.Offset.Y;

        if (advance)
        {
            _vm!.AdvanceWindow();
        }
        else
        {
            _vm!.RetreatWindow();
        }

        int chunkRows = HarvestHexViewModel.ChunkRows;
        Dispatcher.UIThread.Post(() =>
        {
            ScrollViewer? sv2 = GetScrollViewer();
            if (sv2 is not null && HexList.ItemCount > 0)
            {
                double rh = sv2.Extent.Height / HexList.ItemCount;
                double adjustment = chunkRows * rh;
                double newOffset = advance
                    ? Math.Max(0, savedOffset - adjustment)
                    : Math.Min(sv2.Extent.Height - sv2.Viewport.Height, savedOffset + adjustment);
                sv2.Offset = sv2.Offset.WithY(newOffset);
            }

            _adjustingWindow = false;
        }, DispatcherPriority.Render);
    }
}
