#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Library;

#endregion

namespace DemoViewer.NET.ViewModels.Library;

/// <summary>
///     One contributed <see cref="LibraryFilter" /> hosted by the Library (item 22): a ComboBox's items and
///     selection. Kept as one stable instance across a data refresh (<see cref="Rebuild" />) so the bound
///     ComboBox is never recreated; only a gate transition adds or removes the instance itself.
/// </summary>
public sealed partial class LibraryFilterViewModel : ObservableObject
{
    private Func<DemoEntry, string, bool> _matches;
    private bool _suppress;

    internal LibraryFilterViewModel(LibraryFilter filter, Action onSelectionChanged)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(onSelectionChanged);
        OnSelectionChanged = onSelectionChanged;
        Label = filter.Label;
        _matches = filter.Matches;
        Items = [.. filter.Items];
        _selected = Items.Count > 0 ? Items[0] : new LibraryFilterItem("", "All");
    }

    private Action OnSelectionChanged { get; }

    /// <summary>The filter's own label (e.g. "Team").</summary>
    public string Label { get; private set; }

    /// <summary>The choices, item 0 conventionally the "" (no filter) choice.</summary>
    public ObservableCollection<LibraryFilterItem> Items { get; } = [];

    [ObservableProperty]
    private LibraryFilterItem _selected;

    partial void OnSelectedChanged(LibraryFilterItem value)
    {
        if (!_suppress)
        {
            OnSelectionChanged();
        }
    }

    /// <summary>Whether <paramref name="entry" /> passes the current selection. Always true for the "" choice.</summary>
    public bool Matches(DemoEntry entry) => Selected.Key.Length == 0 || _matches(entry, Selected.Key);

    /// <summary>True while a real choice (not "") is selected: drives the host's "Clear" affordance.</summary>
    public bool IsActive => Selected.Key.Length > 0;

    // Rebuilds the items from a freshly-read contribution and keeps the selection by key (a rename changes
    // Display, never Key). Suppressed so the ComboBox's transient null write during Clear()+Add() does not
    // reach OnSelectionChanged; the final reassignment below runs un-suppressed so it fires exactly once.
    internal void Rebuild(LibraryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _matches = filter.Matches;
        Label = filter.Label;
        string keepKey = Selected.Key;

        _suppress = true;
        Items.Clear();
        foreach (LibraryFilterItem item in filter.Items)
        {
            Items.Add(item);
        }

        _suppress = false;
        Selected = Items.FirstOrDefault(i => i.Key == keepKey) ?? Items.FirstOrDefault() ?? new LibraryFilterItem("", "All");
    }

    /// <summary>Resets the selection to the "" (All) choice without firing <see cref="OnSelectionChanged" />.</summary>
    internal void Reset()
    {
        _suppress = true;
        Selected = Items.FirstOrDefault() ?? Selected;
        _suppress = false;
    }
}
