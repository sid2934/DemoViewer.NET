#region

using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     A <see cref="SettingsPageContribution" /> the shell tracks. <see cref="ViewModel" /> and
///     <see cref="Content" /> stay null until <see cref="FeatureId" /> first resolves on: a pack's page
///     must not build while the pack is off, so the factories run at most once, on the
///     first <see cref="EnsureBuilt" /> call that sees the gate on.
/// </summary>
public sealed partial class MountedSettingsPage : ObservableObject
{
    private readonly SettingsPageContribution _contribution;

    internal MountedSettingsPage(SettingsPageContribution contribution)
    {
        _contribution = contribution;
        Id = contribution.Id;
        Header = contribution.Header;
        Order = contribution.Order;
        Keywords = contribution.Keywords;
        FeatureId = contribution.FeatureId;
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

    /// <summary>The built VM, or null before <see cref="EnsureBuilt" /> has run. Exposed so <c>SettingsViewModel.Dispose</c> can dispose it.</summary>
    [ObservableProperty]
    private ViewModelBase? _viewModel;

    /// <summary>The built View, DataContext already set to <see cref="ViewModel" />, or null before <see cref="EnsureBuilt" /> has run.</summary>
    [ObservableProperty]
    private Control? _content;

    /// <summary>True while the page shows: its gate resolves on AND the current search filter matches it.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>True once <see cref="ViewModel" />/<see cref="Content" /> are built.</summary>
    public bool IsBuilt => ViewModel is not null;

    /// <summary>Runs the contribution's factories once. Idempotent; a call after the first no-ops.</summary>
    internal void EnsureBuilt()
    {
        if (ViewModel is not null)
        {
            return;
        }

        ViewModelBase viewModel = _contribution.ViewModelFactory();
        Control view = _contribution.ViewFactory();
        view.DataContext = viewModel;
        ViewModel = viewModel;
        Content = view;
    }
}
