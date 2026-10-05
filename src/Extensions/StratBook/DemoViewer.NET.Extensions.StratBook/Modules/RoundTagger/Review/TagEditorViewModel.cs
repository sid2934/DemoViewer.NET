#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Review;

/// <summary>What a tag or a suggestion is while it is being edited: every field the editor can change.</summary>
public sealed class TagEditorDraft
{
    public string Code { get; set; } = "";

    /// <summary>Frame-clock start, inclusive.</summary>
    public int FromTick { get; set; }

    /// <summary>Frame-clock end, inclusive.</summary>
    public int ToTick { get; set; }

    /// <summary>Human labels, in order; a group may repeat.</summary>
    public List<TagLabel> Labels { get; set; } = [];

    public string? Note { get; set; }

    public List<TagPosition> Positions { get; set; } = [];

    /// <summary>A deep copy, so an editor never writes through to what it was opened on.</summary>
    public TagEditorDraft Clone() => new()
    {
        Code = Code,
        FromTick = FromTick,
        ToTick = ToTick,
        Labels = [.. Labels.Select(l => new TagLabel(l.Group, l.Value))],
        Note = Note,
        Positions = [.. Positions.Select(p => new TagPosition { X = p.X, Y = p.Y, LevelMinZ = p.LevelMinZ, Tick = p.Tick, Place = p.Place, PlaceSource = p.PlaceSource })]
    };

    /// <summary>The draft of a written tag.</summary>
    /// <param name="instance">The tag.</param>
    public static TagEditorDraft From(TagInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new TagEditorDraft
        {
            Code = instance.Code,
            FromTick = instance.FromTick,
            ToTick = instance.ToTick,
            Labels = [.. instance.Labels],
            Note = instance.Note,
            Positions = [.. instance.Positions]
        }.Clone();
    }
}

/// <summary>A palette's words: the codes a tag can carry, and the label groups with their values.</summary>
/// <param name="Codes">Every code the palette's code panels offer, in palette order.</param>
/// <param name="Groups">Each label panel's group with the values its buttons offer.</param>
public sealed record TagVocabulary(IReadOnlyList<string> Codes, IReadOnlyList<(string Group, IReadOnlyList<string> Values)> Groups)
{
    public static TagVocabulary Empty { get; } = new([], []);

    /// <summary>The vocabulary a palette definition carries.</summary>
    /// <param name="palette">The palette.</param>
    public static TagVocabulary From(TagPaletteDefinition palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        List<string> codes =
        [
            .. palette.Panels.Where(p => !p.IsLabels).SelectMany(p => p.Buttons).Select(b => b.Code)
                .OfType<string>().Where(c => c.Length > 0).Distinct(StringComparer.Ordinal)
        ];
        List<(string, IReadOnlyList<string>)> groups = [];
        foreach (IGrouping<string, TagPalettePanel> panel in palette.Panels.Where(p => p.IsLabels && p.Group is { Length: > 0 })
                     .GroupBy(p => p.Group!, StringComparer.Ordinal))
        {
            groups.Add((panel.Key, [.. panel.SelectMany(p => p.Buttons).Select(b => b.Value).OfType<string>().Distinct(StringComparer.Ordinal)]));
        }

        return new TagVocabulary(codes, groups);
    }
}

/// <summary>One label on the tag being edited.</summary>
public sealed class TagLabelChip(string group, string value)
{
    public string Group { get; } = group;

    public string Value { get; } = value;

    public string Text => $"{Group}: {Value}";
}

/// <summary>One map position on the tag being edited.</summary>
public sealed class TagPositionRow(TagPosition position)
{
    public TagPosition Position { get; } = position;

    public string Text => Position.Place is { Length: > 0 } place
        ? place
        : string.Create(CultureInfo.InvariantCulture, $"({Position.X:0}, {Position.Y:0})");
}

/// <summary>
///     The one editor for a suggestion, a written tag and a new tag: the code, the span in seconds on the round
///     clock with "set to the playhead" for each end, the labels as chips from the palette's vocabulary with a
///     free value for anything the palette does not name, the note, and the positions. The owner decides what
///     saving and deleting do; the editor only validates and hands back a draft.
///     <para>
///         The span is checked here: the end not before the start, both inside the parse, and both inside the
///         start's round when the palette clamps to rounds. Facts are not editable: they are the parser's.
///     </para>
/// </summary>
public sealed partial class TagEditorViewModel : ObservableObject
{
    /// <summary>Any value, when the chosen group is one the palette does not list.</summary>
    public const string AnyValue = "";

    private readonly Func<TagEditorDraft, int, int, (int From, int To)> _bounds;
    private readonly Action? _cancel;
    private readonly Action? _delete;
    private readonly int _origin;
    private readonly Func<int> _playhead;
    private readonly Func<TagEditorDraft, bool> _save;
    private readonly int _tickRate;
    private readonly TagVocabulary _vocabulary;

    [ObservableProperty]
    private string _fromText = "";

    [ObservableProperty]
    private string _newGroup = "";

    [ObservableProperty]
    private string _newValue = "";

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private string _problem = "";

    [ObservableProperty]
    private string? _selectedCode;

    [ObservableProperty]
    private string _toText = "";

    /// <param name="title">What is being edited ("Suggested: execute", "Tag: A execute", "New round label").</param>
    /// <param name="draft">The starting values; copied, never written through.</param>
    /// <param name="vocabulary">The palette's codes and label groups.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <param name="originTick">The tick the seconds count from: the round's start.</param>
    /// <param name="playhead">The shared clock's tick, for the "set here" buttons.</param>
    /// <param name="bounds">Clamps a span (the draft, from, to) to the parse and, when the palette asks, the round.</param>
    /// <param name="save">Writes the draft; false keeps the editor open with a problem line.</param>
    /// <param name="delete">Deletes the tag; null when there is nothing written to delete.</param>
    /// <param name="cancel">Closes the editor without writing.</param>
    public TagEditorViewModel(string title, TagEditorDraft draft, TagVocabulary vocabulary, int tickRate, int originTick,
        Func<int> playhead, Func<TagEditorDraft, int, int, (int From, int To)>? bounds, Func<TagEditorDraft, bool> save,
        Action? delete, Action? cancel)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(playhead);
        ArgumentNullException.ThrowIfNull(save);
        Title = title;
        _vocabulary = vocabulary;
        _tickRate = tickRate > 0 ? tickRate : 64;
        _origin = originTick;
        _playhead = playhead;
        _bounds = bounds ?? ((_, from, to) => (from, to));
        _save = save;
        _delete = delete;
        _cancel = cancel;

        Codes = [.. vocabulary.Codes];
        if (draft.Code.Length > 0 && !Codes.Contains(draft.Code))
        {
            Codes.Add(draft.Code); // a detector's code the palette does not list is still this tag's code
        }

        _selectedCode = draft.Code.Length > 0 ? draft.Code : null;
        _fromText = Seconds(draft.FromTick);
        _toText = Seconds(draft.ToTick);
        _note = draft.Note ?? "";
        foreach (TagLabel label in draft.Labels)
        {
            Labels.Add(new TagLabelChip(label.Group, label.Value));
        }

        foreach (TagPosition position in draft.Positions)
        {
            Positions.Add(new TagPositionRow(position));
        }

        Groups = [.. vocabulary.Groups.Select(g => g.Group).Concat(draft.Labels.Select(l => l.Group)).Distinct(StringComparer.Ordinal)];
    }

    public string Title { get; }

    /// <summary>The palette's codes, plus the draft's own when the palette lacks it.</summary>
    public ObservableCollection<string> Codes { get; }

    /// <summary>The label groups a chip can be added in: the palette's, and any the draft already carries.</summary>
    public ObservableCollection<string> Groups { get; }

    /// <summary>The values the palette offers for <see cref="NewGroup" />; empty for a group it does not list, where the value is free text.</summary>
    public IReadOnlyList<string> ValuesForGroup =>
        _vocabulary.Groups.FirstOrDefault(g => string.Equals(g.Group, NewGroup, StringComparison.Ordinal)).Values ?? [];

    public ObservableCollection<TagLabelChip> Labels { get; } = [];

    public ObservableCollection<TagPositionRow> Positions { get; } = [];

    public bool CanDelete => _delete is not null;

    public bool HasPositions => Positions.Count > 0;

    public bool HasProblem => Problem.Length > 0;

    /// <summary>Adds a map position: the host routes a map click here while the editor is open.</summary>
    /// <param name="position">The resolved position.</param>
    public void AddPosition(TagPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        Positions.Add(new TagPositionRow(position));
        OnPropertyChanged(nameof(HasPositions));
    }

    /// <summary>The draft as the fields stand, or null with a problem line when they do not parse.</summary>
    public TagEditorDraft? Snapshot()
    {
        if (!TryParseSeconds(FromText, out double from) || !TryParseSeconds(ToText, out double to))
        {
            Problem = "Start and end are seconds from the round start, like 21 or 21.5.";
            return null;
        }

        return new TagEditorDraft
        {
            Code = SelectedCode ?? "",
            FromTick = _origin + (int)Math.Round(from * _tickRate),
            ToTick = _origin + (int)Math.Round(to * _tickRate),
            Labels = [.. Labels.Select(c => new TagLabel(c.Group, c.Value))],
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
            Positions = [.. Positions.Select(p => p.Position)]
        };
    }

    [RelayCommand]
    private void SetStartHere() => FromText = Seconds(_playhead());

    [RelayCommand]
    private void SetEndHere() => ToText = Seconds(_playhead());

    [RelayCommand]
    private void AddLabel()
    {
        string group = NewGroup.Trim();
        string value = NewValue.Trim();
        if (group.Length == 0 || value.Length == 0)
        {
            Problem = "A label needs a group and a value.";
            return;
        }

        if (Labels.Any(l => l.Group == group && l.Value == value))
        {
            return;
        }

        Labels.Add(new TagLabelChip(group, value));
        if (!Groups.Contains(group))
        {
            Groups.Add(group);
        }

        NewValue = "";
        Problem = "";
    }

    [RelayCommand]
    private void RemoveLabel(TagLabelChip? chip)
    {
        if (chip is not null)
        {
            Labels.Remove(chip);
        }
    }

    [RelayCommand]
    private void RemovePosition(TagPositionRow? row)
    {
        if (row is not null)
        {
            Positions.Remove(row);
            OnPropertyChanged(nameof(HasPositions));
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (Snapshot() is not { } draft)
        {
            return;
        }

        if (draft.Code.Length == 0)
        {
            Problem = "Pick a code.";
            return;
        }

        if (draft.ToTick < draft.FromTick)
        {
            Problem = "The end is before the start.";
            return;
        }

        (int from, int to) = _bounds(draft, draft.FromTick, draft.ToTick);
        draft.FromTick = from;
        draft.ToTick = Math.Max(from, to);
        Problem = "";
        if (!_save(draft))
        {
            Problem = "The tag could not be written.";
        }
    }

    [RelayCommand]
    private void Delete() => _delete?.Invoke();

    [RelayCommand]
    private void Cancel() => _cancel?.Invoke();

    partial void OnNewGroupChanged(string value) => OnPropertyChanged(nameof(ValuesForGroup));

    partial void OnFromTextChanged(string value) => OnPropertyChanged(nameof(CurrentSpan));

    partial void OnToTextChanged(string value) => OnPropertyChanged(nameof(CurrentSpan));

    /// <summary>The span as the boxes stand, in ticks, or null while either box does not parse. The timeline draws it.</summary>
    public (int From, int To)? CurrentSpan =>
        TryParseSeconds(FromText, out double from) && TryParseSeconds(ToText, out double to)
            ? (_origin + (int)Math.Round(from * _tickRate), _origin + (int)Math.Round(to * _tickRate))
            : null;

    /// <summary>Sets the span from ticks: the timeline's handles drag it.</summary>
    /// <param name="fromTick">Start tick.</param>
    /// <param name="toTick">End tick.</param>
    public void SetSpan(int fromTick, int toTick)
    {
        FromText = Seconds(fromTick);
        ToText = Seconds(Math.Max(fromTick, toTick));
    }

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    /// <summary>
    ///     The span a tag may take: inside the parse and, when <paramref name="clampToRound" />, inside the
    ///     round the start falls in. The same rule the palette applies to a new tag.
    /// </summary>
    /// <param name="from">Start tick.</param>
    /// <param name="to">End tick.</param>
    /// <param name="rounds">The demo's rounds, frame clock, or null.</param>
    /// <param name="clampToRound">Whether to keep the span inside its round.</param>
    /// <param name="firstTick">The parse's first tick, or 0.</param>
    /// <param name="lastTick">The parse's last tick, or 0 when unknown.</param>
    public static (int From, int To) Clamp(int from, int to, IReadOnlyList<LibraryRound>? rounds, bool clampToRound,
        int firstTick, int lastTick)
    {
        if (clampToRound && RoundBounds(rounds, from) is { } round)
        {
            from = Math.Max(from, round.Start);
            if (round.End is { } end)
            {
                to = Math.Min(to, end);
            }
        }

        from = Math.Max(from, Math.Max(0, firstTick));
        if (lastTick > 0)
        {
            to = Math.Min(to, lastTick);
            from = Math.Min(from, lastTick);
        }

        return (from, Math.Max(from, to));
    }

    /// <summary>The round a tick falls in: its start and, when another round follows, the tick before that one's start.</summary>
    /// <param name="rounds">The demo's rounds, frame clock, or null.</param>
    /// <param name="tick">The tick.</param>
    public static (int Number, int Start, int? End)? RoundBounds(IReadOnlyList<LibraryRound>? rounds, int tick)
    {
        if (rounds is not { Count: > 0 })
        {
            return null;
        }

        LibraryRound? current = null;
        LibraryRound? next = null;
        foreach (LibraryRound round in rounds.OrderBy(r => r.StartTick))
        {
            if (round.StartTick <= tick)
            {
                current = round;
            }
            else
            {
                next = round;
                break;
            }
        }

        return current is null ? null : (current.Number, current.StartTick, next is null ? null : next.StartTick - 1);
    }

    private string Seconds(int tick) =>
        ((tick - _origin) / (double)_tickRate).ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryParseSeconds(string text, out double seconds) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
}
