#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.ViewModels.EntityTracking;

/// <summary>
///     Delta-per-tick log for the selected entity. Shows the fields that changed
///     between the previous-tick snapshot and the current tick as "field: prev → curr"
///     rows. Capped at <see cref="Capacity" /> rows so a chatty entity can't grow unbounded.
/// </summary>
public sealed partial class DeltaLogViewModel : ObservableObject
{
    private const int Capacity = 1000;

    [ObservableProperty]
    private bool _hasEntries;

    [ObservableProperty]
    private string _headerText = "Delta log";

    /// <summary>Entries.</summary>
    public ObservableCollection<DeltaLogEntry> Entries { get; } = [];

    /// <summary>Clear.</summary>
    public void Clear()
    {
        Entries.Clear();
        HeaderText = "Delta log";
        HasEntries = false;
    }

    /// <summary>
    ///     Rebuilds the log from the changed-field set of the currently selected entity.
    ///     <paramref name="changes" /> is (field, prev, curr) for each differing field.
    /// </summary>
    public void Show(int tick, string entityLabel, IReadOnlyList<(string Field, string Prev, string Curr)> changes)
    {
        Entries.Clear();

        int count = changes.Count < Capacity ? changes.Count : Capacity;
        for (int i = 0; i < count; i++)
        {
            (string field, string prev, string curr) = changes[i];
            Entries.Add(new DeltaLogEntry
            {
                Tick = tick,
                Field = field,
                Prev = prev,
                Curr = curr
            });
        }

        HeaderText = changes.Count > 0
            ? $"Δ tick {tick} — {entityLabel}  ({changes.Count} changed)"
            : $"Δ tick {tick} — {entityLabel}  (no changes)";
        HasEntries = Entries.Count > 0;
    }
}

/// <summary>One field-delta row: "field: prev → curr" at a tick.</summary>
public sealed class DeltaLogEntry
{
    /// <summary>Curr.</summary>
    public string Curr { get; init; } = "";

    /// <summary>Field.</summary>
    public string Field { get; init; } = "";

    /// <summary>Prev.</summary>
    public string Prev { get; init; } = "";

    /// <summary>Summary.</summary>
    public string Summary => $"{Field}: {Prev} → {Curr}";

    /// <summary>Tick.</summary>
    public int Tick { get; init; }
}
