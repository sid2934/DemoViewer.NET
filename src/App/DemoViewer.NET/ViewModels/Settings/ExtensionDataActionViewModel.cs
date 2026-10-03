#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     One pack's "delete extension data" row (item 24): available whether the pack is on or off, since
///     deleting while off is the main use. Arm counts what is there and shows the confirmation; Confirm
///     runs the delete; Cancel drops back to idle without touching anything.
/// </summary>
public sealed partial class ExtensionDataActionViewModel(IPackDataRemoval removal, string label) : ObservableObject
{
    /// <summary>The extension's name, read off its own <c>FeatureCatalog</c> row.</summary>
    public string Label { get; } = label;

    /// <summary>True while counting or deleting; every command is disabled meanwhile.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True once Arm has counted something and the confirmation is up.</summary>
    [ObservableProperty]
    private bool _isConfirming;

    /// <summary>The confirmation's body: the user-work stores by label and size, or null before Arm runs.</summary>
    [ObservableProperty]
    private string? _confirmationText;

    /// <summary>The last status or result line, or null.</summary>
    [ObservableProperty]
    private string? _statusText;

    /// <summary>Counts what is on disk and shows the confirmation, or says there is nothing to delete.</summary>
    [RelayCommand]
    private async Task Arm()
    {
        IsBusy = true;
        StatusText = "Counting…";
        try
        {
            PackDataInventory inventory = await removal.InventoryAsync();
            if (inventory.TotalBytes == 0 && inventory.Items.All(i => i.FileCount == 0))
            {
                IsConfirming = false;
                StatusText = "Nothing to delete.";
                return;
            }

            ConfirmationText = BuildConfirmation(inventory);
            IsConfirming = true;
            StatusText = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Drops the confirmation without deleting anything.</summary>
    [RelayCommand]
    private void Cancel()
    {
        IsConfirming = false;
        ConfirmationText = null;
        StatusText = null;
    }

    /// <summary>Deletes everything the confirmation named.</summary>
    [RelayCommand]
    private async Task Confirm()
    {
        IsConfirming = false;
        IsBusy = true;
        StatusText = "Deleting…";
        try
        {
            PackDataRemovalResult result = await removal.DeleteAsync();
            StatusText = result.Ran
                ? $"Deleted {result.Removed.Items.Sum(i => i.FileCount)} files ({FormatBytes(result.Removed.TotalBytes)})."
                : "Cancelled: the extension was turned back on before this ran.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildConfirmation(PackDataInventory inventory)
    {
        StoreInventoryItem[] userWork = [.. inventory.UserWorkItems.Where(i => i.FileCount > 0)];
        string names = userWork.Length == 0
            ? "No data you wrote by hand."
            : string.Join("\n", userWork.Select(i => $"{i.Descriptor.Label}: {FormatBytes(i.Bytes)}"));
        return $"This permanently deletes:\n{names}\n\nCaches will be rebuilt from your library in the background.";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value < 10 && unit > 0 ? $"{value:0.0} {units[unit]}" : $"{value:0} {units[unit]}";
    }
}
