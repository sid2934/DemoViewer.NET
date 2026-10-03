#region

using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     A <see cref="SettingsPageContribution" /> built once, at Settings construction (item 14): the VM
///     and the View the contribution's own factories produced, with the View's <c>DataContext</c> already
///     set to the VM. <see cref="IsVisible" /> is the only thing <see cref="SettingsViewModel" /> keeps
///     recomputing, from the gate and the search filter; everything else here is immutable for the life of
///     one Settings open.
/// </summary>
public sealed partial class MountedSettingsPage : ObservableObject
{
    internal MountedSettingsPage(SettingsPageContribution contribution, ViewModelBase viewModel, Control content)
    {
        Id = contribution.Id;
        Header = contribution.Header;
        Order = contribution.Order;
        Keywords = contribution.Keywords;
        FeatureId = contribution.FeatureId;
        ViewModel = viewModel;
        Content = content;
    }

    /// <summary>The contribution's own id. A lookup key for tests, never shown.</summary>
    public string Id { get; }

    /// <summary>The section header text.</summary>
    public string Header { get; }

    /// <summary>Sort key among a pack's own contributed pages.</summary>
    public int Order { get; }

    /// <summary>Fed into the settings search alongside the built-in section keywords.</summary>
    public string Keywords { get; }

    /// <summary>The gate id the page shows under.</summary>
    public string? FeatureId { get; }

    /// <summary>The built VM. Exposed so <c>SettingsViewModel.Dispose</c> can dispose it if it is one.</summary>
    public ViewModelBase ViewModel { get; }

    /// <summary>The built View, DataContext already set to <see cref="ViewModel" />.</summary>
    public Control Content { get; }

    /// <summary>True while the page shows: its gate resolves on AND the current search filter matches it.</summary>
    [ObservableProperty]
    private bool _isVisible;
}
