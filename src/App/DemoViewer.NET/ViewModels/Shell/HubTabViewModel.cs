#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.ViewModels.Shell;

/// <summary>
///     A hub tab an extension declared: a rail of the sections that name it and the selected section's view.
///     The host owns all of it; the extension supplies only the declaration and the sections. The selected
///     section persists as the session's active tab id, and the rail's collapsed state per hub id.
/// </summary>
public sealed partial class HubTabViewModel : ObservableObject, IWorkspaceTabViewModel
{
    [ObservableProperty]
    private bool _isRailCollapsed;

    /// <param name="hubId">The id sections name as their host.</param>
    /// <param name="header">The strip header, which also names the rail's collapse buttons.</param>
    /// <param name="railLabel">The band over the rail.</param>
    public HubTabViewModel(string hubId, string header, string railLabel)
    {
        HubId = hubId;
        Header = header;
        RailLabel = railLabel;
    }

    /// <summary>The id sections name as their host.</summary>
    public string HubId { get; }

    /// <summary>The strip header.</summary>
    public string Header { get; }

    /// <summary>The band over the rail.</summary>
    public string RailLabel { get; }

    /// <summary>The collapse button's tooltip.</summary>
    public string CollapseTip => $"Collapse the {Header} rail";

    /// <summary>The expand button's tooltip.</summary>
    public string ExpandTip => $"Show the {Header} rail";

    /// <summary>The line shown when the gate has hidden every section.</summary>
    public string EmptyText => $"every {Header} section is hidden by the feature settings";

    /// <summary>The sections and the selection. The shell fills and gates the list; the view binds it.</summary>
    public TabSectionHost Sections { get; } = new(autoSelectFirst: true);

    [RelayCommand]
    private void ToggleRail() => IsRailCollapsed = !IsRailCollapsed;

    /// <inheritdoc />
    public void OnActivated(IModuleContext context) => Sections.OnHostActivated(context);

    /// <inheritdoc />
    public void OnDeactivated() => Sections.OnHostDeactivated();
}
