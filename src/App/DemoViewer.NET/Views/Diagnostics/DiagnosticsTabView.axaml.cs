#region

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.ViewModels.Diagnostics;

#endregion

namespace DemoViewer.NET.Views.Diagnostics;

/// <summary>
///     Diagnostics tab view. The two pieces of code-behind both need the visual tree:
///     the Copy button resolves <c>TopLevel.Clipboard</c> (with a manual-copy fallback when the
///     browser rejects it), and the runtime capture listener is disposed on detach so it never
///     outlives the tab (listener-lifetime constraint).
/// </summary>
public partial class DiagnosticsTabView : UserControl
{
    /// <summary>Initializes a new <see cref="DiagnosticsTabView" /> instance.</summary>
    public DiagnosticsTabView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    // Tab activation: TabControl raises AttachedToVisualTree when this tab's content loads. Refresh
    // the always-on rows so a user opening the tab gets fresh system/session info.
    private void OnAttached(object? sender, EventArgs e)
    {
        if (DataContext is DiagnosticsTabViewModel vm)
        {
            vm.Refresh();
        }
    }

    // The real TabControl unloads inactive tab content; tear the runtime listener down with the
    // visual tree so it never outlives the tab.
    private void OnDetached(object? sender, EventArgs e)
    {
        if (DataContext is DiagnosticsTabViewModel vm)
        {
            vm.DetachRuntimeListeners();
        }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiagnosticsTabViewModel vm)
        {
            return;
        }

        string text = vm.BuildCopyText();
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            vm.ShowClipboardFallback(text);
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // WASM clipboard write is gesture/permission-gated and can reject; fall back to the
            // read-only TextBox the user can Ctrl+C.
            vm.ShowClipboardFallback(text);
        }
    }
}
