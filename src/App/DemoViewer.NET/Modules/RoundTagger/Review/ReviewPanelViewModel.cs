#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.RoundTagger.Palette;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.Modules.RoundTagger.Review;

/// <summary>One written tag, as the Labels list shows it.</summary>
public sealed partial class TagRowViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    internal TagRowViewModel(TagInstance instance, int roundStart, int tickRate)
    {
        Instance = instance;
        int rate = tickRate > 0 ? tickRate : 64;
        Title = instance.Code;
        RoundText = instance.Round is { } round ? string.Create(CultureInfo.InvariantCulture, $"r{round}") : "";
        ClockText = SuggestionQueueViewModel.Clock(Math.Max(0, instance.FromTick - roundStart) / (double)rate);
        LabelsText = string.Join(", ", instance.Labels.Select(l => $"{l.Group}: {l.Value}"));
        SourceText = instance.Source == TagSources.Suggested ? "suggested" : "";
    }

    public TagInstance Instance { get; }

    public Guid Id => Instance.Id;

    public string Title { get; }

    public string RoundText { get; }

    /// <summary>The start, as time since the round started.</summary>
    public string ClockText { get; }

    public string LabelsText { get; }

    /// <summary>"suggested" for a tag that came from an accepted suggestion, else empty.</summary>
    public string SourceText { get; }
}

/// <summary>
///     Review mode's panel: the Suggested tab (the queue) and the Labels tab (every tag written on this demo),
///     and the one editor for whichever is being changed. A suggestion's editor lives in the queue, so its
///     draft survives J and K; a written tag's editor and a new round label's live here. Deletes and edits go
///     through the tag session, so Ctrl+Z undoes them like any other tagging.
///     <para>
///         Deleting a tag that came from an accepted suggestion does not bring the suggestion back: its verdict
///         stays, and a verdict is what keeps a suggestion from being offered again.
///     </para>
/// </summary>
public sealed partial class ReviewPanelViewModel : ObservableObject, IDisposable
{
    /// <summary>The Suggested tab's index.</summary>
    public const int SuggestedTab = 0;

    /// <summary>The Labels tab's index.</summary>
    public const int LabelsTab = 1;

    private readonly TagPaletteViewModel _palette;
    private readonly Func<int> _playhead;
    private readonly Action<int> _seek;
    private readonly TagSession _session;
    private readonly Func<int> _tickRate;
    private bool _disposed;
    private TagEditorViewModel? _editor;

    [ObservableProperty]
    private int _selectedTab;

    private TagRowViewModel? _selectedLabel;

    /// <param name="session">The open demo's tags.</param>
    /// <param name="queue">The suggestion queue shown in the Suggested tab.</param>
    /// <param name="palette">The palette whose codes and label groups the editor offers.</param>
    /// <param name="playhead">The shared clock's tick.</param>
    /// <param name="tickRate">The open demo's tick rate.</param>
    /// <param name="seek">Moves the shared clock; the tab's funnel, so LiveSync sees the seek.</param>
    public ReviewPanelViewModel(TagSession session, SuggestionQueueViewModel queue, TagPaletteViewModel palette,
        Func<int> playhead, Func<int> tickRate, Action<int> seek)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(palette);
        _session = session;
        Queue = queue;
        _palette = palette;
        _playhead = playhead;
        _tickRate = tickRate;
        _seek = seek;
        Queue.Vocabulary = Vocabulary;
        Queue.Playhead = playhead;
        Queue.EditorChanged += OnEditorsChanged;
        _session.Changed += RebuildLabels;
        RebuildLabels();
    }

    public SuggestionQueueViewModel Queue { get; }

    /// <summary>The demo's written tags, in time order.</summary>
    public ObservableCollection<TagRowViewModel> Labels { get; } = [];

    public bool HasLabels => Labels.Count > 0;

    /// <summary>The label list's heading, with the count.</summary>
    public string LabelsHeader => string.Create(CultureInfo.InvariantCulture, $"Labels: {Labels.Count}");

    /// <summary>The editor on screen: a suggestion's when one is open, else a tag's or a new round label's.</summary>
    public TagEditorViewModel? ActiveEditor => Queue.Editor ?? _editor;

    public bool HasEditor => ActiveEditor is not null;

    /// <summary>True when a demo's tags are attached, so there is something to label.</summary>
    public bool CanLabel => _session.Document is not null;

    /// <summary>The selected written tag: selecting one seeks to its start and opens its editor.</summary>
    public TagRowViewModel? SelectedLabel
    {
        get => _selectedLabel;
        set
        {
            if (ReferenceEquals(value, _selectedLabel))
            {
                return;
            }

            if (_selectedLabel is not null)
            {
                _selectedLabel.IsSelected = false;
            }

            _selectedLabel = value;
            OnPropertyChanged();
            if (value is null)
            {
                return;
            }

            value.IsSelected = true;
            _seek(value.Instance.FromTick);
            OpenEditor(value.Instance);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Queue.EditorChanged -= OnEditorsChanged;
        _session.Changed -= RebuildLabels;
    }

    /// <summary>A map click while an editor is open adds a position to it. False with no editor open.</summary>
    /// <param name="position">The resolved position.</param>
    public bool AddPosition(TagPosition position)
    {
        if (ActiveEditor is not { } editor)
        {
            return false;
        }

        editor.AddPosition(position);
        return true;
    }

    /// <summary>Opens the editor on a written tag, found by id; false when this demo has no such tag.</summary>
    /// <param name="id">The tag's id.</param>
    public bool EditTag(Guid id)
    {
        if (_session.Document?.Instances.FirstOrDefault(i => i.Id == id) is not { } instance)
        {
            return false;
        }

        SelectedTab = LabelsTab;
        SelectedLabel = Labels.FirstOrDefault(r => r.Id == id);
        if (SelectedLabel is null)
        {
            OpenEditor(instance);
        }

        return true;
    }

    /// <summary>Opens the editor on a new tag over a span: no code yet, the user picks one.</summary>
    /// <param name="fromTick">Start tick.</param>
    /// <param name="toTick">End tick.</param>
    /// <param name="title">The editor's title.</param>
    public bool NewTag(int fromTick, int toTick, string title)
    {
        if (_session.Document is null)
        {
            return false;
        }

        TagEditorDraft draft = new() { FromTick = fromTick, ToTick = Math.Max(fromTick, toTick) };
        int origin = TagEditorViewModel.RoundBounds(_session.Rounds, fromTick)?.Start ?? 0;
        SetEditor(new TagEditorViewModel(title, draft, Vocabulary(), _tickRate(), origin, _playhead, Bounds,
            Add, null, CloseEditor));
        SelectedTab = LabelsTab;
        return true;
    }

    /// <summary>A label over the whole round under the playhead: its start to the tick before the next round.</summary>
    [RelayCommand]
    private void LabelRound()
    {
        int playhead = _playhead();
        if (TagEditorViewModel.RoundBounds(_session.Rounds, playhead) is not { } round)
        {
            return;
        }

        int end = round.End ?? (_session.Document?.Clock.LastTick is > 0 and var last ? last : playhead);
        NewTag(round.Start, end, string.Create(CultureInfo.InvariantCulture, $"New label: round {round.Number}"));
    }

    /// <summary>A label starting at the playhead, ten seconds long; the user sets the end.</summary>
    [RelayCommand]
    private void LabelHere()
    {
        int playhead = _playhead();
        int rate = _tickRate() > 0 ? _tickRate() : 64;
        NewTag(playhead, playhead + 10 * rate, "New label");
    }

    /// <summary>The Suggested tab is showing. Settable from its tab button.</summary>
    public bool IsSuggestedTab
    {
        get => SelectedTab == SuggestedTab;
        set
        {
            if (value)
            {
                SelectedTab = SuggestedTab;
            }
        }
    }

    /// <summary>The Labels tab is showing. Settable from its tab button.</summary>
    public bool IsLabelsTab
    {
        get => SelectedTab == LabelsTab;
        set
        {
            if (value)
            {
                SelectedTab = LabelsTab;
            }
        }
    }

    /// <summary>Shows the Labels tab.</summary>
    public void ShowLabels() => SelectedTab = LabelsTab;

    /// <summary>A row of the Labels list was clicked.</summary>
    [RelayCommand]
    private void SelectLabel(TagRowViewModel? row) => SelectedLabel = row;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsSuggestedTab));
        OnPropertyChanged(nameof(IsLabelsTab));
    }

    private void OpenEditor(TagInstance instance)
    {
        TagEditorDraft draft = TagEditorDraft.From(instance);
        int origin = TagEditorViewModel.RoundBounds(_session.Rounds, instance.FromTick)?.Start ?? 0;
        Guid id = instance.Id;
        SetEditor(new TagEditorViewModel($"Label: {instance.Code}", draft, Vocabulary(), _tickRate(), origin, _playhead,
            Bounds, d => Replace(id, d), () => Delete(id), CloseEditor));
    }

    private TagVocabulary Vocabulary() => TagVocabulary.From(_palette.Palette);

    // Inside the parse and, when the palette clamps, inside the start's round: the palette's own rule.
    private (int From, int To) Bounds(TagEditorDraft draft, int from, int to) =>
        TagEditorViewModel.Clamp(from, to, _session.Rounds, _palette.Palette.ClampToRound,
            _session.Document?.Clock.FirstTick ?? 0, _session.Document?.Clock.LastTick ?? 0);

    private bool Replace(Guid id, TagEditorDraft draft)
    {
        if (_session.Document?.Instances.FirstOrDefault(i => i.Id == id) is not { } current)
        {
            return false;
        }

        TagInstance next = current.Clone();
        next.Code = draft.Code;
        next.FromTick = draft.FromTick;
        next.ToTick = draft.ToTick;
        next.Round = TagSession.RoundAt(_session.Rounds, draft.FromTick);
        next.Labels = [.. draft.Labels];
        next.Note = draft.Note;
        next.Positions = [.. draft.Positions];
        next.ModifiedUtc = DateTime.UtcNow;
        next.FactsStamp = null; // the span may be in another round now: the session restamps the facts
        next.Facts = [];
        try
        {
            _session.Apply(new TagDelta.Replace(id, next));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }

        CloseEditor();
        return true;
    }

    private bool Add(TagEditorDraft draft)
    {
        DateTime now = DateTime.UtcNow;
        TagInstance instance = new()
        {
            Id = Guid.NewGuid(),
            Code = draft.Code,
            FromTick = draft.FromTick,
            ToTick = draft.ToTick,
            CreatedUtc = now,
            ModifiedUtc = now,
            Source = TagSources.Human,
            Labels = [.. draft.Labels],
            Note = draft.Note,
            Positions = [.. draft.Positions]
        };
        try
        {
            _session.Apply(new TagDelta.Add(instance));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }

        CloseEditor();
        return true;
    }

    private void Delete(Guid id)
    {
        try
        {
            _session.Apply(new TagDelta.Remove(id));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return;
        }

        CloseEditor();
    }

    private void SetEditor(TagEditorViewModel editor)
    {
        _editor = editor;
        OnEditorsChanged();
    }

    [RelayCommand]
    private void CloseEditor()
    {
        _editor = null;
        if (_selectedLabel is not null)
        {
            _selectedLabel.IsSelected = false;
            _selectedLabel = null;
            OnPropertyChanged(nameof(SelectedLabel));
        }

        OnEditorsChanged();
    }

    private void OnEditorsChanged()
    {
        OnPropertyChanged(nameof(ActiveEditor));
        OnPropertyChanged(nameof(HasEditor));
    }

    private void RebuildLabels()
    {
        Guid? keep = _selectedLabel?.Id;
        Labels.Clear();
        int rate = _tickRate();
        foreach (TagInstance instance in (_session.Document?.Instances ?? []).OrderBy(i => i.FromTick).ThenBy(i => i.Code, StringComparer.Ordinal))
        {
            int roundStart = TagEditorViewModel.RoundBounds(_session.Rounds, instance.FromTick)?.Start ?? 0;
            TagRowViewModel row = new(instance, roundStart, rate);
            if (keep == instance.Id)
            {
                row.IsSelected = true;
                _selectedLabel = row;
            }

            Labels.Add(row);
        }

        if (keep is not null && Labels.All(r => r.Id != keep))
        {
            _selectedLabel = null;
            OnPropertyChanged(nameof(SelectedLabel));
        }

        OnPropertyChanged(nameof(HasLabels));
        OnPropertyChanged(nameof(LabelsHeader));
        OnPropertyChanged(nameof(CanLabel));
    }
}
