#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview;

/// <summary>One entry of a filter bar list: what it reads as and what it narrows.</summary>
/// <param name="label">What the list shows.</param>
/// <param name="apply">Narrows a filter to this entry; the "any" entry returns it unchanged.</param>
public sealed class PlayerFilterChoice(string label, Func<PlayerFilter, PlayerFilter> apply)
{
    /// <summary>What the list shows.</summary>
    public string Label { get; } = label;

    internal PlayerFilter Apply(PlayerFilter filter) => apply(filter);

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
///     The Player Overview's filter bar: the window (the last 10, 30 or 100 matches, a date range or all), the
///     map, the mode or source and the side at the end. One per tab; on <see cref="Changed" /> the tab recomputes
///     every panel from <see cref="Filter" />. The map and mode lists hold only what the subject has played.
/// </summary>
public sealed partial class PlayerOverviewFilterViewModel : ObservableObject
{
    private const string Any = "Any";
    private const string DateRangeLabel = "Date range";

    private bool _quiet;

    [ObservableProperty]
    private DateTime? _from;

    [ObservableProperty]
    private PlayerFilterChoice _map;

    [ObservableProperty]
    private PlayerFilterChoice _mode;

    [ObservableProperty]
    private PlayerFilterChoice _side;

    [ObservableProperty]
    private DateTime? _to;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDateRange))]
    private PlayerFilterChoice _window;

    public PlayerOverviewFilterViewModel()
    {
        Windows =
        [
            new PlayerFilterChoice("All matches", f => f),
            new PlayerFilterChoice("Last 10", f => f with { Last = 10 }),
            new PlayerFilterChoice("Last 30", f => f with { Last = 30 }),
            new PlayerFilterChoice("Last 100", f => f with { Last = 100 }),
            new PlayerFilterChoice(DateRangeLabel, f => f)
        ];
        Sides =
        [
            new PlayerFilterChoice(Any, f => f),
            new PlayerFilterChoice("CT", f => f with { Side = 3 }),
            new PlayerFilterChoice("T", f => f with { Side = 2 })
        ];
        _window = Windows[0];
        _side = Sides[0];
        _map = Maps[0];
        _mode = Modes[0];
        Filter = PlayerFilter.All;
    }

    /// <summary>The window choices: all matches, the last 10, 30 or 100, or a date range.</summary>
    public IReadOnlyList<PlayerFilterChoice> Windows { get; }

    /// <summary>Any side, or the side the subject ended on.</summary>
    public IReadOnlyList<PlayerFilterChoice> Sides { get; }

    /// <summary>Any map, then each map the subject has played, by name.</summary>
    public ObservableCollection<PlayerFilterChoice> Maps { get; } = [new(Any, f => f)];

    /// <summary>Any mode, then each game mode, or source where a demo has no mode, the subject has played.</summary>
    public ObservableCollection<PlayerFilterChoice> Modes { get; } = [new(Any, f => f)];

    /// <summary>True when the window is a date range: the two date pickers show.</summary>
    public bool IsDateRange => Window.Label == DateRangeLabel;

    /// <summary>The selection as a filter; <see cref="PlayerFilter.All" /> when nothing is narrowed.</summary>
    public PlayerFilter Filter { get; private set; }

    /// <summary>True when the selection leaves out any demo.</summary>
    public bool IsFiltered => Filter != PlayerFilter.All;

    /// <summary>Raised on the UI thread when the user changes the selection.</summary>
    public event Action? Changed;

    /// <summary>
    ///     Lists the maps and modes of <paramref name="matches" />. A selection the new list still holds is kept;
    ///     one it does not falls back to any. <paramref name="reset" /> clears the whole selection, for a new
    ///     subject. Raises nothing: the caller is refreshing already.
    /// </summary>
    /// <param name="matches">The subject's demos.</param>
    /// <param name="reset">True to start from no filter.</param>
    internal void SetOptions(IReadOnlyList<PlayerMatch> matches, bool reset)
    {
        _quiet = true;
        try
        {
            if (reset)
            {
                Window = Windows[0];
                Side = Sides[0];
                From = null;
                To = null;
            }

            string map = reset ? Any : Map.Label;
            string mode = reset ? Any : Mode.Label;
            Replace(Maps, matches
                .Select(m => m.Row.Map)
                .Where(m => !string.IsNullOrEmpty(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(m => (Label: DemoEntry.PrettifyMap(m), Map: m!))
                .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
                .Select(m => new PlayerFilterChoice(m.Label, f => f with { Map = m.Map })));
            Replace(Modes, matches
                .Select(m => ModeChoice(m.Row))
                .OfType<PlayerFilterChoice>()
                .DistinctBy(c => c.Label, StringComparer.Ordinal)
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase));
            Map = Maps.FirstOrDefault(c => c.Label == map) ?? Maps[0];
            Mode = Modes.FirstOrDefault(c => c.Label == mode) ?? Modes[0];
        }
        finally
        {
            _quiet = false;
        }

        Filter = Build();
        OnPropertyChanged(nameof(IsFiltered));
    }

    /// <summary>Clears every narrowing.</summary>
    [RelayCommand]
    private void Reset()
    {
        _quiet = true;
        Window = Windows[0];
        Side = Sides[0];
        Map = Maps[0];
        Mode = Modes[0];
        From = null;
        To = null;
        _quiet = false;
        Apply();
    }

    partial void OnWindowChanged(PlayerFilterChoice value) => Apply();

    partial void OnMapChanged(PlayerFilterChoice value) => Apply();

    partial void OnModeChanged(PlayerFilterChoice value) => Apply();

    partial void OnSideChanged(PlayerFilterChoice value) => Apply();

    partial void OnFromChanged(DateTime? value) => Apply();

    partial void OnToChanged(DateTime? value) => Apply();

    private void Apply()
    {
        // A ComboBox clears its selection when its list is replaced; null is not a choice, so it reads as any.
        if (_quiet || Window is null || Map is null || Mode is null || Side is null)
        {
            return;
        }

        PlayerFilter filter = Build();
        if (filter == Filter)
        {
            return;
        }

        Filter = filter;
        OnPropertyChanged(nameof(IsFiltered));
        Changed?.Invoke();
    }

    // The days are the user's local calendar days; the match time is UTC.
    private PlayerFilter Build()
    {
        PlayerFilter filter = Side.Apply(Mode.Apply(Map.Apply(Window.Apply(PlayerFilter.All))));
        if (IsDateRange)
        {
            filter = filter with
            {
                FromTicks = From is { } from ? DayStart(from) : null,
                BeforeTicks = To is { } to ? DayStart(to.AddDays(1)) : null
            };
        }

        return filter;
    }

    private static long DayStart(DateTime day) => DateTime.SpecifyKind(day.Date, DateTimeKind.Local).ToUniversalTime().Ticks;

    private static PlayerFilterChoice? ModeChoice(DemoCacheIndexEntry row)
    {
        string label = HistoryRowItem.ModeOf(row);
        if (!string.IsNullOrEmpty(row.GameMode))
        {
            string mode = row.GameMode;
            return new PlayerFilterChoice(label, f => f with { GameMode = mode, Source = null });
        }

        return (row.MatchSource ?? row.SourceKind) is { Length: > 0 } source and not "Unknown"
            ? new PlayerFilterChoice(label, f => f with { Source = source, GameMode = null })
            : null;
    }

    private static void Replace(ObservableCollection<PlayerFilterChoice> list, IEnumerable<PlayerFilterChoice> choices)
    {
        while (list.Count > 1)
        {
            list.RemoveAt(list.Count - 1);
        }

        foreach (PlayerFilterChoice choice in choices)
        {
            list.Add(choice);
        }
    }
}
