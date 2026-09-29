#region

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.UtilityBook;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>What the lineup picker hands back on confirm.</summary>
/// <param name="UtilityKind">The lineup's strat utility kind.</param>
/// <param name="LineupId">The lineup's primary id.</param>
/// <param name="Technique">The technique key, or null under the grid grouping.</param>
public sealed record LineupPick(string UtilityKind, Guid LineupId, string? Technique);

/// <summary>One throw position of the focused landing group, as the picker's list shows it.</summary>
/// <param name="Key">The Utility Book's position key (<see cref="UtilityBookTabViewModel.PositionKey" />).</param>
/// <param name="Label">How it is thrown.</param>
/// <param name="CountText">How often, and in how many demos.</param>
public sealed record LineupPickerPosition(string Key, string Label, string CountText);

/// <summary>
///     The visual lineup picker of a throw step (Strat Editor: visual lineup picker): the Utility Book's own map
///     view model, locked to the strat's map and filtered to a strat utility kind. A click on a landing group
///     shows where it is thrown from, one position per technique; a click on a position selects it, with its
///     clip when one is rendered. Confirm hands back the kind, lineup and technique; cancel hands back nothing.
///     Either way the map view model is disposed.
/// </summary>
public sealed partial class LineupPickerViewModel : ObservableObject, IDisposable
{
    private readonly Action<LineupPick?> _close;
    private bool _closed;
    private bool _syncing;

    [ObservableProperty]
    private string _kind;

    [ObservableProperty]
    private LineupPickerPosition? _selectedPosition;

    /// <param name="map">A Utility Book view model locked to the strat's map; the picker owns and disposes it.</param>
    /// <param name="utilityKind">The step's utility kind, or <see cref="StratEditorViewModel.None" /> for smoke.</param>
    /// <param name="current">The step's lineup, resolved, or null.</param>
    /// <param name="technique">The step's technique, or null for the most thrown.</param>
    /// <param name="close">Called once with the pick, or null on cancel.</param>
    public LineupPickerViewModel(UtilityBookTabViewModel map, string utilityKind, StratLineupChoice? current, string? technique,
        Action<LineupPick?> close)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(close);
        Map = map;
        _close = close;
        _kind = current?.UtilityKind
                ?? (StratVocabulary.UtilityKinds.Contains(utilityKind) ? utilityKind : StratVocabulary.UtilityKinds[0]);
        Map.PropertyChanged += OnMapPropertyChanged;
        Map.QueryKinds = StratLineupCatalog.GrenadeKindsFor(_kind);
        if (current is not null)
        {
            Map.Reveal(current.Id, technique);
        }

        Rebuild();
    }

    /// <summary>The Utility Book view model the map host draws.</summary>
    public UtilityBookTabViewModel Map { get; }

    public static IReadOnlyList<string> Kinds { get; } = StratVocabulary.UtilityKinds;

    public string Title => "Pick a lineup on " + Map.MapName;

    /// <summary>The focused landing group's positions, most thrown first.</summary>
    public ObservableCollection<LineupPickerPosition> Positions { get; } = [];

    public bool HasPositions => Positions.Count > 0;

    /// <summary>The selected position's card: style, use counts and the clip.</summary>
    public LineupDetail? Detail => Map.Detail;

    public bool CanConfirm => Map.Detail is not null;

    /// <summary>What to do next, or why there is nothing to pick.</summary>
    public string HintLine =>
        !Map.HasGroups ? $"no {Kind} lineups on {Map.MapName} yet: index grenades on Match Overview or in Settings"
        : Map.FocusedGroup is { } group ? $"{group.Title}: pick where it is thrown from"
        : "click where the grenade lands";

    /// <inheritdoc />
    public void Dispose()
    {
        Map.PropertyChanged -= OnMapPropertyChanged;
        Map.Dispose();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (Map.Detail is not { } detail || Map.FocusedGroup is not { } group)
        {
            return;
        }

        Close(new LineupPick(StratLineupCatalog.UtilityKindOf(group.Kind), detail.Lineup.Id, detail.Technique?.Key));
    }

    [RelayCommand]
    private void Cancel() => Close(null);

    private void Close(LineupPick? pick)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Dispose();
        _close(pick);
    }

    partial void OnKindChanged(string value)
    {
        Map.QueryKinds = StratLineupCatalog.GrenadeKindsFor(value);
        OnPropertyChanged(nameof(HintLine));
    }

    partial void OnSelectedPositionChanged(LineupPickerPosition? value)
    {
        if (!_syncing && value is not null)
        {
            Map.SelectThrow(value.Key);
        }
    }

    private void OnMapPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UtilityBookTabViewModel.FocusedGroup) or nameof(UtilityBookTabViewModel.Detail)
            or nameof(UtilityBookTabViewModel.HasGroups))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _syncing = true;
        try
        {
            List<LineupPickerPosition> positions = Map.FocusedGroup is { } group
                ?
                [
                    .. UtilityBookTabViewModel.Positions(group).OrderByDescending(p => p.Throws.Count)
                        .Select(p => new LineupPickerPosition(p.Key,
                            p.Technique?.Label ?? (p.JumpThrow ? "jump-throw" : "standard throw"),
                            CountText(p.Throws.Count, p.Throws.Select(t => t.Demo.Sha256 ?? t.Demo.StableKey).Distinct(StringComparer.Ordinal).Count())))
                ]
                : [];
            if (!Positions.SequenceEqual(positions))
            {
                Positions.Clear();
                foreach (LineupPickerPosition position in positions)
                {
                    Positions.Add(position);
                }
            }

            string? selectedKey = Map.Detail is { } detail
                ? detail.Technique is { } technique
                    ? UtilityBookTabViewModel.PositionKey(detail.Lineup, technique)
                    : UtilityBookTabViewModel.LineupKey(detail.Lineup)
                : null;
            SelectedPosition = Positions.FirstOrDefault(p => p.Key == selectedKey);
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(HasPositions));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(HintLine));
        OnPropertyChanged(nameof(Title));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private static string CountText(int throws, int demos) => string.Create(CultureInfo.InvariantCulture,
        $"{throws} {(throws == 1 ? "throw" : "throws")} in {demos} {(demos == 1 ? "demo" : "demos")}");
}
