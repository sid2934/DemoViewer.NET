#region

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.ViewModels.Playback;

/// <summary>
///     The strip-ready presentation surface for the demo-derived game-event filter
///     . It does NOT own the filter list: that single source of truth
///     is the shell's <c>GameEventFilters</c> (<see cref="GameEventFilterItem" />, populated from the
///     actual demo). This VM wraps that collection with the Select-all / Deselect-all commands and the
///     active-filter tooltip the nav strip's event-jump flyout binds to, replacing the
///     <c>SeekControlsViewModel</c>'s hardcoded-list equivalents.
///     <para>
///         The flyout's checkbox list binds to <see cref="Filters" /> (the same instance as the shell's
///         <c>GameEventFilters</c>), so toggling a checkbox here is exactly what
///         <c>SelectedSpecialFilter()</c> reads on the next event jump: one filter, one source.
///     </para>
/// </summary>
public sealed partial class EventFilterFlyoutViewModel : ObservableObject
{
    /// <summary>
    ///     Initializes the flyout over the shell-owned, demo-derived game-event filter collection.
    ///     Subscribes to per-item and collection changes so the tooltip stays live as the demo's events
    ///     are appended at load time and as the user toggles checkboxes.
    /// </summary>
    public EventFilterFlyoutViewModel(ObservableCollection<GameEventFilterItem> filters)
    {
        Filters = filters;
        Filters.CollectionChanged += OnFiltersCollectionChanged;
        foreach (GameEventFilterItem item in Filters)
        {
            item.PropertyChanged += OnItemChanged;
        }
    }

    /// <summary>
    ///     The demo-derived game-event filters (the shell's <c>GameEventFilters</c> instance). Each
    ///     item's <see cref="GameEventFilterItem.IsEnabled" /> drives the special-seek match set;
    ///     nothing enabled = "match any" (handled by the navigator).
    /// </summary>
    public ObservableCollection<GameEventFilterItem> Filters { get; }

    /// <summary>
    ///     Summarises the active filters for the event-jump button tooltip. Mirrors the retired
    ///     <c>SeekControlsViewModel.SpecialTooltip</c> wording so the affordance reads the same.
    /// </summary>
    public string FilterTooltip
    {
        get
        {
            List<string> active = Filters.Where(f => f.IsEnabled).Select(f => f.EventName).ToList();
            return active.Count switch
            {
                0 => "Jump to next/prev game event: any type (none selected = match any)",
                _ when active.Count == Filters.Count => "Jump to next/prev game event: all event types",
                1 => $"Jump to next/prev game event: {active[0]}",
                _ => $"Jump to next/prev game event: {string.Join(", ", active)}"
            };
        }
    }

    /// <summary>
    ///     A SHORT label for the NavStrip SEEK target chip. Where
    ///     <see cref="FilterTooltip" /> is a full sentence for the hover, this is the ≤~1-word chip text:
    ///     <c>Any event</c> (none / all selected → match-any), <c>Round</c> (exactly the
    ///     <c>round_*</c> preset: reproduces the removed round buttons' label), the single event name
    ///     when one type is selected, or <c>N events</c> for an arbitrary subset. Kept
    ///     <c>MaxWidth</c>-capped by the chip; see the design-system NavStrip contract.
    /// </summary>
    public string TargetSummary
    {
        get
        {
            int total = Filters.Count;
            List<GameEventFilterItem> enabled = Filters.Where(f => f.IsEnabled).ToList();

            // None or all enabled both mean "match any event type" (the navigator unions everything).
            if (enabled.Count == 0 || enabled.Count == total)
            {
                return "Any event";
            }

            // Round preset: exactly the round_* set is on: reproduces the removed round buttons' label.
            List<GameEventFilterItem> round = Filters.Where(IsRoundEvent).ToList();
            if (round.Count > 0 && enabled.Count == round.Count && enabled.All(IsRoundEvent))
            {
                return "Round";
            }

            return enabled.Count == 1 ? enabled[0].EventName : $"{enabled.Count} events";
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (GameEventFilterItem item in Filters)
        {
            item.IsEnabled = true;
        }
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (GameEventFilterItem item in Filters)
        {
            item.IsEnabled = false;
        }
    }

    // ── Target presets (SEEK consolidation) ───────────────────────────────────────
    // Each preset is a named quick-selection over the SAME demo-derived filter list; nothing enabled
    // falls through to the navigator's "match any". "Round" reproduces the removed NavPrev/NextRound
    // exactly (SemanticNavigator.PrevRound/NextRound key off the identical StartsWith("round_") union,
    // and CS2 GOTV round lifecycle is round_freeze_end / round_officially_ended, not round_start, so a
    // named preset is the discoverable way to reach rounds without knowing the exact event name).

    /// <summary>Preset: clear all selections → the navigator matches ANY game event.</summary>
    [RelayCommand]
    private void PresetAnyEvent() => ApplyPreset(static _ => false);

    /// <summary>Preset: select the <c>round_*</c> union (reproduces the removed round buttons).</summary>
    [RelayCommand]
    private void PresetRound() => ApplyPreset(IsRoundEvent);

    /// <summary>Preset: select kills (<c>player_death</c>).</summary>
    [RelayCommand]
    private void PresetKills() =>
        ApplyPreset(static n => string.Equals(n, "player_death", StringComparison.OrdinalIgnoreCase));

    /// <summary>Preset: select bomb events (<c>bomb_*</c>: planted / defused / begindefuse / etc.).</summary>
    [RelayCommand]
    private void PresetBomb() =>
        ApplyPreset(static n => n.StartsWith("bomb_", StringComparison.OrdinalIgnoreCase));

    private static bool IsRoundEvent(GameEventFilterItem item) => IsRoundEvent(item.EventName);

    private static bool IsRoundEvent(string name) =>
        name.StartsWith("round_", StringComparison.OrdinalIgnoreCase);

    private void ApplyPreset(Func<string, bool> match)
    {
        foreach (GameEventFilterItem item in Filters)
        {
            item.IsEnabled = match(item.EventName);
        }
    }

    private void OnFiltersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // New demo events are appended at load: subscribe to the new items so the summary stays live.
        if (e.NewItems is not null)
        {
            foreach (object o in e.NewItems)
            {
                if (o is GameEventFilterItem item)
                {
                    item.PropertyChanged += OnItemChanged;
                }
            }
        }

        RaiseSummaryChanged();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) =>
        RaiseSummaryChanged();

    private void RaiseSummaryChanged()
    {
        OnPropertyChanged(nameof(FilterTooltip));
        OnPropertyChanged(nameof(TargetSummary));
    }
}
