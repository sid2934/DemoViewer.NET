#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     The Start row above step 1 (docs/strat-format.md, "The start"): one line saying where the tokens begin and what
///     starts the strat, opened to one location field per token. Not a step: it has no time, verb or lines.
/// </summary>
public sealed partial class StratStartRow : ObservableObject
{
    private readonly StratEditorViewModel _owner;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Why the last start edit was not made (the spawns still being read, a step placing the token); empty otherwise.</summary>
    [ObservableProperty]
    private string _note = "";

    private Guid? _loaded;

    internal StratStartRow(StratEditorViewModel owner)
    {
        _owner = owner;
        Own = [.. StratVocabulary.Slots.Select(s => new StratStartSlotRow(owner, s))];
        Opponents = [.. StratVocabulary.OpponentSlots.Select(s => new StratStartSlotRow(owner, s))];
    }

    /// <summary>A to E.</summary>
    public ObservableCollection<StratStartSlotRow> Own { get; }

    /// <summary>O1 to O5.</summary>
    public ObservableCollection<StratStartSlotRow> Opponents { get; }

    /// <summary>What the collapsed row says: <c>spawn</c>, <c>as captured</c>, each player's place, or that it has none.</summary>
    public string Summary { get; private set; } = "";

    /// <summary>The collapsed row's one line: the start, then what starts the strat when it says.</summary>
    public string Line { get; private set; } = "";

    /// <summary>The start's kind as the row names it.</summary>
    public string KindText { get; private set; } = "";

    /// <summary>The glyph of the expand button.</summary>
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>Whether the strat's spawns are known, so the row can put the tokens back in them.</summary>
    public bool CanUseSpawns => _owner.Spawns?.Invoke() is not null;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Every token back at the spawns, a <c>spawn</c> start, one undo entry.</summary>
    [RelayCommand]
    private void UseSpawns() => _owner.UseSpawnStart();

    internal void Load(StratDocument document, CalloutResolver places, StratSpawns? spawns)
    {
        StratStart? start = StratStartBlock.Effective(document, spawns);
        if (_loaded != document.Id)
        {
            _loaded = document.Id;
            Note = "";
        }

        KindText = start is null ? "" : document.Start is null && start.Kind == StratStart.CustomKind ? "from step 1" : start.Kind switch
        {
            StratStart.SpawnKind => "spawn",
            StratStart.CapturedKind => "captured",
            _ => "custom"
        };
        Summary = StratStartPhrasing.Text(document, places, spawns) ?? "no one placed: tokens appear at their first step";
        Line = document.Trigger?.Text is { Length: > 0 } trigger ? Summary + " · " + trigger : Summary;
        foreach (StratStartSlotRow row in Own.Concat(Opponents))
        {
            row.Load(start, places);
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Line));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(CanUseSpawns));
    }
}

/// <summary>One token's start in the opened Start row: a single location field with a map pick.</summary>
public sealed partial class StratStartSlotRow : ObservableObject
{
    private readonly StratEditorViewModel _owner;
    private bool _loading;

    [ObservableProperty]
    private IReadOnlyList<PlaceRef> _value = [];

    internal StratStartSlotRow(StratEditorViewModel owner, string slot)
    {
        _owner = owner;
        Slot = slot;
        Target = StratStartBlock.FieldFor(slot);
    }

    public string Slot { get; }

    /// <summary>What the field writes and the map pick arms.</summary>
    public StratLocationField Target { get; }

    /// <summary>The owner's words for the map's places.</summary>
    public CalloutResolver Callouts => _owner.Places;

    /// <summary>The level a typed coordinate takes when the field holds no point.</summary>
    public double? CurrentLevelMinZ => _owner.DefaultLevelMinZ;

    internal void Load(StratStart? start, CalloutResolver places)
    {
        _loading = true;
        try
        {
            Value = StratStartBlock.For(start, Slot) is { } entry ? [StratStartBlock.AsLocation(entry)] : [];
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(Callouts));
    }

    partial void OnValueChanged(IReadOnlyList<PlaceRef> value)
    {
        if (!_loading)
        {
            _owner.WriteLocation(Target, value);
        }
    }
}
