#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.ViewModels.EntityTracking;

/// <summary>
///     Left-rail class browser (F8.6). Lists classId → class-name pairs from
///     <see cref="EntityTracker.ClassIdMap" /> (A2), filterable by name, with selection
///     acting as a class filter on the entity list. A null selection means "all classes".
/// </summary>
public sealed partial class ClassBrowserViewModel : ObservableObject
{
    private List<ClassBrowserItem> _allItems = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private ClassBrowserItem? _selectedClass;

    /// <summary>Classes.</summary>
    public ObservableCollection<ClassBrowserItem> Classes { get; } = [];

    /// <summary>Fired when the class filter changes (null = show all classes).</summary>
    public event Action<string?>? ClassFilterChanged;

    /// <summary>Clear.</summary>
    public void Clear()
    {
        _allItems = [];
        SelectedClass = null;
        Classes.Clear();
    }

    /// <summary>Repopulates the rail from the tracker's class registry (sorted by name).</summary>
    public void Rebuild(EntityTracker? tracker)
    {
        string? previouslySelected = SelectedClass?.ClassName;

        _allItems = tracker is null
            ? []
            : tracker.ClassIdMap
                .Select(kv => new ClassBrowserItem
                {
                    ClassId = kv.Key,
                    ClassName = kv.Value
                })
                .OrderBy(c => c.ClassName, StringComparer.Ordinal)
                .ToList();

        ApplyFilter();

        // Preserve the prior selection across rebuilds so the entity-list filter sticks.
        if (previouslySelected is not null)
        {
            SelectedClass = Classes.FirstOrDefault(c => c.ClassName == previouslySelected);
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<ClassBrowserItem> filtered = string.IsNullOrWhiteSpace(Filter)
            ? _allItems
            : _allItems.Where(c => c.ClassName.Contains(Filter, StringComparison.OrdinalIgnoreCase));

        Classes.Clear();
        foreach (ClassBrowserItem item in filtered)
        {
            Classes.Add(item);
        }
    }

    [RelayCommand]
    private void ClearSelection() => SelectedClass = null;

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedClassChanged(ClassBrowserItem? value)
        => ClassFilterChanged?.Invoke(value?.ClassName);
}

/// <summary>One class-registry row: id + name.</summary>
public sealed class ClassBrowserItem
{
    /// <summary>Class id.</summary>
    public int ClassId { get; init; }

    /// <summary>Class name.</summary>
    public string ClassName { get; init; } = "";

    /// <summary>Display.</summary>
    public string Display => $"{ClassId,4}  {ClassName}";
}
