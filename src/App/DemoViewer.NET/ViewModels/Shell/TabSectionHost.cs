#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.ViewModels.Shell;

/// <summary>
///     The sections a host tab shows in place of strip tabs: the Strat Book rail and the Library's Teams
///     view. Holds the gate-enabled sections in rail order and the selected one, and drives each section's
///     <see cref="WorkspaceTabDescriptor.Activate" /> / <see cref="WorkspaceTabDescriptor.Deactivate" /> the
///     way the shell drives strip tabs: a section is active only while it is selected AND its host tab is,
///     so the inactive-content-unload invariant and the VM lifecycle contract hold one level down.
///     <para>
///         The shell owns the descriptor set and the gate. It calls <see cref="Reconcile" /> with the enabled,
///         sorted sections at build time and on every gate change; this class never re-runs a module's
///         <c>CreateTabs</c>, so a section's cached VM survives a gate flip exactly as a strip tab's does.
///     </para>
/// </summary>
public sealed partial class TabSectionHost : ObservableObject
{
    private readonly bool _autoSelectFirst;
    private IModuleContext? _context;
    private bool _hostActive;

    [ObservableProperty]
    private WorkspaceTabDescriptor? _selectedSection;

    /// <param name="autoSelectFirst">
    ///     True when the host always shows a section (the Strat Book rail), so an empty selection falls to the
    ///     first one; false when no selection means the host's own body (the Library's demo browser).
    /// </param>
    public TabSectionHost(bool autoSelectFirst)
    {
        _autoSelectFirst = autoSelectFirst;
        Sections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSections));
    }

    /// <summary>The enabled sections in rail order. Bound by the host view.</summary>
    public ObservableCollection<WorkspaceTabDescriptor> Sections { get; } = [];

    /// <summary>True while at least one section is enabled.</summary>
    public bool HasSections => Sections.Count > 0;

    /// <summary>
    ///     Reconciles <see cref="Sections" /> to the enabled set by identity, never a rebuild: a now-disabled
    ///     section is deactivated and removed (its neighbour is selected first when it was the selected one),
    ///     a now-enabled one is inserted at its <see cref="WorkspaceTabDescriptor.Order" /> position.
    /// </summary>
    /// <param name="desired">The enabled sections, already sorted by Order.</param>
    public void Reconcile(IReadOnlyList<WorkspaceTabDescriptor> desired)
    {
        HashSet<string> desiredIds = new(desired.Select(d => d.TabId), StringComparer.Ordinal);

        foreach (WorkspaceTabDescriptor removed in Sections.Where(s => !desiredIds.Contains(s.TabId)).ToList())
        {
            if (ReferenceEquals(SelectedSection, removed))
            {
                SelectedSection = ChooseNeighbor(removed, desired);
            }

            ExtensionTabs.Deactivate(removed);
            Sections.Remove(removed);
        }

        foreach (WorkspaceTabDescriptor add in desired)
        {
            if (Sections.Any(s => ReferenceEquals(s, add)))
            {
                continue;
            }

            int index = 0;
            while (index < Sections.Count && Sections[index].Order <= add.Order)
            {
                index++;
            }

            Sections.Insert(index, add);
        }

        if (SelectedSection is null && _autoSelectFirst && Sections.Count > 0)
        {
            SelectedSection = Sections[0];
        }
    }

    /// <summary>Selects the section with this id and says whether it is here (enabled) at all.</summary>
    /// <param name="tabId">The section's persisted tab id.</param>
    public bool TrySelect(string? tabId)
    {
        if (tabId is not { Length: > 0 } || Sections.FirstOrDefault(s => s.TabId == tabId) is not { } section)
        {
            return false;
        }

        SelectedSection = section;
        return true;
    }

    /// <summary>True when this id names one of the sections the host was given, enabled or not.</summary>
    /// <param name="tabId">The section's persisted tab id.</param>
    public bool Holds(string? tabId) => tabId is { Length: > 0 } && Sections.Any(s => s.TabId == tabId);

    /// <summary>The host tab became the selected strip tab: the selected section goes live with it.</summary>
    /// <param name="context">The module context the shell activates tabs with.</param>
    public void OnHostActivated(IModuleContext context)
    {
        _context = context;
        _hostActive = true;
        if (SelectedSection is { } section)
        {
            ExtensionTabs.Activate(section, context);
        }
    }

    /// <summary>The host tab stopped being selected: the selected section goes dormant with it.</summary>
    public void OnHostDeactivated()
    {
        _hostActive = false;
        ExtensionTabs.Deactivate(SelectedSection);
    }

    partial void OnSelectedSectionChanged(WorkspaceTabDescriptor? oldValue, WorkspaceTabDescriptor? newValue)
    {
        ExtensionTabs.Deactivate(oldValue);
        if (_hostActive && _context is { } context && newValue is not null)
        {
            ExtensionTabs.Activate(newValue, context);
        }

        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>True while a section is selected; false means the host shows its own body.</summary>
    public bool HasSelection => SelectedSection is not null;

    // The nearest still-enabled section with a lower Order, else the first; null when nothing is left
    // and the host has a body of its own to fall back to.
    private WorkspaceTabDescriptor? ChooseNeighbor(WorkspaceTabDescriptor removed,
        IReadOnlyList<WorkspaceTabDescriptor> desired)
    {
        if (desired.Count == 0)
        {
            return null;
        }

        WorkspaceTabDescriptor? nearestLower = null;
        foreach (WorkspaceTabDescriptor candidate in desired)
        {
            if (candidate.Order < removed.Order)
            {
                nearestLower = candidate;
            }
            else
            {
                break;
            }
        }

        return nearestLower ?? (_autoSelectFirst ? desired[0] : null);
    }
}
