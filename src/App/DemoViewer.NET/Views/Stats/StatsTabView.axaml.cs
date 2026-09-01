#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.Views.Stats;

/// <summary>
///     Stats tab view. Code-behind exists only for the export folder pickers, which need the
///     visual-tree <see cref="TopLevel" /> that a ViewModel deliberately has no access to, and for
///     the player-details open gestures (double-tap is a routed event, not a bindable
///     command; the context-menu item goes through the same guarded command). All table/sort logic
///     lives in <see cref="StatsTabViewModel" />.
/// </summary>
public partial class StatsTabView : UserControl
{
    /// <summary>Initializes the view.</summary>
    public StatsTabView()
    {
        InitializeComponent();
    }

    // ── Player-details open gestures ──────────────────────────────────────────

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) => OpenDetailsFrom(sender);

    private void OnRowDetailsClick(object? sender, RoutedEventArgs e) => OpenDetailsFrom(sender);

    private void OpenDetailsFrom(object? sender)
    {
        if (DataContext is not StatsTabViewModel vm || sender is not Control { DataContext: StatsRow row })
        {
            return;
        }

        vm.OpenPlayerDetailsCommand.Execute(row);
        if (vm.IsPlayerDetailsOpen)
        {
            // Focus the overlay so its Escape KeyBinding is on the active input path.
            PlayerDetailsHost.Focus();
        }
    }

    private void OnExportCsvClick(object? sender, RoutedEventArgs e) => _ = ExportAsync("csv");

    private void OnExportJsonClick(object? sender, RoutedEventArgs e) => _ = ExportAsync("json");

    private async Task ExportAsync(string formatId)
    {
        if (DataContext is not StatsTabViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = $"Export stats as {formatId.ToUpperInvariant()} — choose a folder",
                AllowMultiple = false
            });

        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } dir)
        {
            vm.StatusMessage = vm.ExportTo(dir, formatId);
        }
    }
}
