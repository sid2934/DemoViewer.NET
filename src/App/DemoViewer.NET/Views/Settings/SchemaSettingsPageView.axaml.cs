#region

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DemoViewer.NET.ViewModels.Settings;

#endregion

namespace DemoViewer.NET.Views.Settings;

/// <summary>The host-rendered page for an extension's settings schema.</summary>
public partial class SchemaSettingsPageView : UserControl
{
    /// <summary>Builds the view.</summary>
    public SchemaSettingsPageView() => InitializeComponent();

    // The folder picker needs the window's storage provider, which only the view can reach.
    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not FolderSettingRow row
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = row.Label,
            AllowMultiple = false
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            row.Path = path;
        }
    }
}
