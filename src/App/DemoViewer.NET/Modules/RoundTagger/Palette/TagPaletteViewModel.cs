#region

using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.Modules.RoundTagger.Palette;

/// <summary>One palette button as the panel shows it: its hotkey parsed once, its caption and its colour.</summary>
public sealed class TagPaletteButtonViewModel
{
    internal TagPaletteButtonViewModel(TagPaletteButton button)
    {
        Button = button;
        HasHotkey = TagPaletteHotkey.TryParse(button.Hotkey, out Key key, out KeyModifiers modifiers, out _);
        Key = key;
        Modifiers = modifiers;
        HotkeyText = HasHotkey ? Playback2DKeymap.Format(key, modifiers) : "";
        Swatch = button.ColorArgb is { } argb ? new SolidColorBrush(Color.FromUInt32(argb)) : null;
    }

    public TagPaletteButton Button { get; }

    public string Caption => Button.Caption;

    public bool HasHotkey { get; }

    public Key Key { get; }

    public KeyModifiers Modifiers { get; }

    /// <summary>The hotkey as the keymap spells gestures ("1", "Ctrl+Shift+W"), or "".</summary>
    public string HotkeyText { get; }

    /// <summary>The code's colour, or null to leave the button unpainted.</summary>
    public IBrush? Swatch { get; }

    /// <summary>Whether a pressed key is this button's hotkey.</summary>
    public bool Matches(Key key, KeyModifiers modifiers) =>
        HasHotkey && TagPaletteHotkey.Matches(Key, Modifiers, key, modifiers);
}

/// <summary>
///     The Tag Palette (plan §3, tag-store.md §3.4 and §3.11): the dockable panel in the 2D Playback tab that
///     tags the demo being watched, every edit written through the tab's <see cref="TagSession" />.
///     <para>
///         <b>The panel flow.</b> Tagging starts on the palette's codes. A code press opens an instance at the
///         playhead, <c>leadSeconds</c> before it to <c>lagSeconds</c> after, clamped to the round when the
///         palette says so, and reveals only the panel its <c>then</c> names; a label press adds one label and
///         reveals that panel's <c>then</c>. When the flow runs out the instance is written as ONE batch, so a
///         single undo removes the code and its labels together (tag-store.md §3.9). Until then it is
///         pending: shown here, not yet on the timeline.
///     </para>
///     <para>
///         <b>Sticky labels.</b> A label pressed in one of the palette's <c>stickyGroups</c> is remembered for
///         the rest of the session and re-applied to every new instance until cleared; a panel whose group
///         already has a sticky value is answered from it and skipped. Only the session remembers: the stored
///         label is an ordinary label.
///     </para>
///     <para>
///         <b>Keys.</b> While <see cref="IsFocused" />, <see cref="TryHandleKey" /> takes the keymap's
///         palette-scoped rows first and then the open panel's hotkeys, ahead of the tab's tool and always
///         scopes (overview correction 21). Every key the palette owns is data (the palette file) or a
///         <see cref="Playback2DKeymap" /> row, so a user rebinds them in Settings like any other 2D key.
///     </para>
///     <para>UI-thread affine like the session it edits.</para>
/// </summary>
public sealed partial class TagPaletteViewModel : ObservableObject, IDisposable
{
    private readonly Func<int> _playhead;
    private readonly Action<Action> _post;
    private readonly TagSession _session;

    // Group → value, in the order the values were set, so the sticky line reads in the order they were set.
    private readonly List<KeyValuePair<string, string>> _sticky = [];
    private readonly TagPaletteStore _store;
    private readonly Func<int> _tickRate;

    [ObservableProperty]
    private bool _isEditingNote;

    [ObservableProperty]
    private bool _isFocused;

    private Playback2DKeymapProfile _keymap = Playback2DKeymapProfile.Default;
    private Guid? _lastId;

    [ObservableProperty]
    private string _noteDraft = "";

    private TagInstance? _pending;

    /// <summary>Creates the palette over a session.</summary>
    /// <param name="session">The tab's tag session; every write goes through it.</param>
    /// <param name="store">The palettes on offer; null offers the built-in alone.</param>
    /// <param name="playhead">The current frame-clock tick.</param>
    /// <param name="tickRate">The demo's tick rate, used when the document's clock does not carry one.</param>
    /// <param name="post">Marshals the session's change notifications onto the UI thread; null runs inline.</param>
    public TagPaletteViewModel(TagSession session, TagPaletteStore? store, Func<int> playhead,
        Func<int>? tickRate = null, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(playhead);
        _session = session;
        _store = store ?? new TagPaletteStore(null);
        _playhead = playhead;
        _tickRate = tickRate ?? (() => 64);
        _post = post ?? (action => action());
        Palette = _store.Default;
        ShowPanel(Palette.Root);

        _session.Changed += OnSessionChanged;
        _store.Reloaded += OnStoreReloaded;
    }

    /// <summary>The palette tagging with, never null.</summary>
    public TagPaletteDefinition Palette { get; private set; }

    /// <summary>Every palette on offer, for the picker.</summary>
    public IReadOnlyList<TagPaletteDefinition> Palettes => _store.Palettes;

    /// <summary>The picker's selection. Setting it switches palettes and raises <see cref="PaletteChosen" />.</summary>
    public TagPaletteDefinition? SelectedPalette
    {
        get => Palette;
        set
        {
            if (value is not null && !ReferenceEquals(value, Palette))
            {
                SelectPalette(value.Id);
                PaletteChosen?.Invoke(Palette.Id);
            }
        }
    }

    /// <summary>The panel whose buttons are showing: the root, or the one the flow revealed.</summary>
    public TagPalettePanel? CurrentPanel { get; private set; }

    /// <summary>The open panel's buttons.</summary>
    public ObservableCollection<TagPaletteButtonViewModel> Buttons { get; } = [];

    /// <summary>What the open panel asks: "codes", or the label group the flow wants next.</summary>
    public string PanelTitle => CurrentPanel is { IsLabels: true } panel
        ? string.IsNullOrEmpty(panel.Group) ? "label" : panel.Group
        : "codes";

    /// <summary>Whether a code press is waiting for its labels.</summary>
    public bool HasPending => _pending is not null;

    /// <summary>The instance being made, as one line, or "".</summary>
    public string PendingText => _pending is null ? "" : Describe(_pending);

    /// <summary>The last instance this palette wrote, as one line, or "" once it is gone (undone).</summary>
    public string LastText => LastInstance() is { } last ? Describe(last) : "";

    /// <summary>The sticky labels, "opponent=Vitality · map=de_nuke", or "".</summary>
    public string StickyText => string.Join(" · ", _sticky.Select(s => $"{s.Key}={s.Value}"));

    public bool HasSticky => _sticky.Count > 0;

    /// <summary>The session's one line: where tags are saved, or why they are not.</summary>
    public string StatusText => _session.StatusText;

    /// <summary>Why a palette file was skipped or what it shadows, one line each, or "".</summary>
    public string DiagnosticsText => string.Join("\n", _store.Diagnostics);

    public bool HasDiagnostics => _store.Diagnostics.Count > 0;

    /// <summary>The palette's keys from the resolved keymap, so a rebind shows the user's gesture.</summary>
    public string HintText =>
        $"{Gesture(Playback2DAction.FocusTagPalette)} focus · {Gesture(Playback2DAction.TagPaletteBack)} back · "
        + $"{Gesture(Playback2DAction.TagNote)} note · {Gesture(Playback2DAction.TagClearSticky)} clear sticky · "
        + $"{Gesture(Playback2DAction.Undo)} undo";

    /// <summary>Raised when the user picks a palette, with its id, so the tab can persist the choice.</summary>
    public event Action<string>? PaletteChosen;

    /// <inheritdoc />
    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _store.Reloaded -= OnStoreReloaded;
    }

    /// <summary>Switches to the palette with this id (the built-in when none has it). Finishes any pending tag first.</summary>
    /// <param name="id">A palette id.</param>
    public void SelectPalette(string? id)
    {
        Finish();
        Palette = _store.Resolve(id);
        ShowPanel(Palette.Root);
        OnPropertyChanged(nameof(Palette));
        OnPropertyChanged(nameof(SelectedPalette));
    }

    /// <summary>Pushes the tab's resolved keymap in, for <see cref="TryHandleKey" /> and the hint line.</summary>
    /// <param name="keymap">The profile the tab routes through.</param>
    public void ApplyKeymap(Playback2DKeymapProfile keymap)
    {
        _keymap = keymap ?? Playback2DKeymapProfile.Default;
        OnPropertyChanged(nameof(HintText));
    }

    /// <summary>Gives the palette the keyboard. False without an attached demo: there is nothing to tag.</summary>
    public bool Focus()
    {
        if (_session.Document is null)
        {
            return false;
        }

        IsFocused = true;
        return true;
    }

    /// <summary>Takes the keyboard back from the palette, writing any pending tag first.</summary>
    public void Leave()
    {
        Finish();
        CancelNote();
        IsFocused = false;
    }

    /// <summary>
    ///     Routes a key while the palette has focus: the keymap's palette-scoped rows, then the open panel's
    ///     hotkeys. False (the key falls through to the tab) when unfocused, while the note is being typed,
    ///     or when nothing here claims it.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">The modifiers held.</param>
    public bool TryHandleKey(Key key, KeyModifiers modifiers)
    {
        if (!IsFocused || IsEditingNote)
        {
            return false;
        }

        if (_keymap.TryResolveInScope(Playback2DBindingScope.WhenPaletteFocused, key, modifiers,
                out Playback2DAction action))
        {
            return Execute(action);
        }

        foreach (TagPaletteButtonViewModel button in Buttons)
        {
            if (button.Matches(key, modifiers))
            {
                return Press(button.Button);
            }
        }

        return false;
    }

    /// <summary>Runs one of the palette-scoped keymap actions. False for any other action.</summary>
    /// <param name="action">The action.</param>
    public bool Execute(Playback2DAction action) => action switch
    {
        Playback2DAction.TagPaletteBack => Back(),
        Playback2DAction.TagNote => BeginNote(),
        Playback2DAction.TagClearSticky => ClearSticky(),
        _ => false
    };

    /// <summary>The note box's two keys: Enter keeps the note, Esc drops the edit. False for any other key.</summary>
    /// <param name="key">The key pressed in the note box.</param>
    public bool TryHandleNoteKey(Key key)
    {
        if (!IsEditingNote)
        {
            return false;
        }

        switch (key)
        {
            case Key.Enter:
                return CommitNote();
            case Key.Escape:
                CancelNote();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Presses a button of the open panel: a code starts a tag, a label adds to the one being made.</summary>
    /// <param name="button">A button of <see cref="CurrentPanel" />.</param>
    public bool Press(TagPaletteButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (CurrentPanel is not { } panel || !panel.Buttons.Contains(button) || _session.Document is null)
        {
            return false;
        }

        if (panel.IsLabels)
        {
            if (_pending is null)
            {
                ShowPanel(Palette.Root);
                return false;
            }

            string group = panel.Group ?? "";
            AddLabel(_pending, group, button.Value ?? "");
            if (Palette.IsSticky(group))
            {
                SetSticky(group, button.Value ?? "");
            }

            Advance(panel.Then);
            return true;
        }

        // A new code finishes the previous tag with the labels it has: a press is never lost.
        Finish();
        _pending = NewInstance(button);
        Advance(button.Then);
        return true;
    }

    /// <summary>
    ///     Esc: writes a pending tag with the labels it has and returns to the codes; on the codes with nothing
    ///     pending, leaves the palette.
    /// </summary>
    public bool Back()
    {
        if (_pending is not null || !ReferenceEquals(CurrentPanel, Palette.Root))
        {
            Finish();
            ShowPanel(Palette.Root);
            return true;
        }

        IsFocused = false;
        return true;
    }

    /// <summary>Writes the pending tag now, if there is one, and returns to the codes.</summary>
    public void Finish()
    {
        if (_pending is not { } instance)
        {
            return;
        }

        _pending = null;
        if (_session.Document is { } document)
        {
            foreach ((string group, string value) in _sticky)
            {
                if (!instance.Labels.Any(l => string.Equals(l.Group, group, StringComparison.Ordinal)))
                {
                    instance.Labels.Add(new TagLabel(group, value));
                }
            }

            // Advisory, like the document says: the palette this session tagged with last. Written before
            // the apply so the same save carries it.
            document.Palette = Palette.Id;
            _session.Apply(new TagDelta.Batch([new TagDelta.Add(instance)]));
            _lastId = instance.Id;
        }

        ShowPanel(Palette.Root);
        RaiseInstanceText();
    }

    /// <summary>Opens the note box on the tag being made, else on the last one written. False when there is neither.</summary>
    public bool BeginNote()
    {
        string? current = _pending is { } pending ? pending.Note : LastInstance()?.Note;
        if (_pending is null && LastInstance() is null)
        {
            return false;
        }

        NoteDraft = current ?? "";
        IsEditingNote = true;
        return true;
    }

    /// <summary>Keeps the note: on the pending tag, or as its own undoable edit of the last one. Blank removes it.</summary>
    public bool CommitNote()
    {
        if (!IsEditingNote)
        {
            return false;
        }

        IsEditingNote = false;
        string? note = string.IsNullOrWhiteSpace(NoteDraft) ? null : NoteDraft.Trim();
        if (_pending is { } pending)
        {
            pending.Note = note;
            RaiseInstanceText();
            return true;
        }

        if (LastInstance() is not { } last)
        {
            return false; // undone while the box was open
        }

        if (string.Equals(last.Note, note, StringComparison.Ordinal))
        {
            return true; // unchanged: no undo entry for nothing
        }

        TagInstance edited = last.Clone();
        edited.Note = note;
        _session.Apply(new TagDelta.Replace(last.Id, edited));
        return true;
    }

    /// <summary>Closes the note box without changing anything.</summary>
    public void CancelNote()
    {
        IsEditingNote = false;
        NoteDraft = "";
    }

    /// <summary>Forgets every sticky label. False when there were none.</summary>
    public bool ClearSticky()
    {
        if (_sticky.Count == 0)
        {
            return false;
        }

        _sticky.Clear();
        OnPropertyChanged(nameof(StickyText));
        OnPropertyChanged(nameof(HasSticky));
        return true;
    }

    /// <summary>
    ///     The span a code press makes: <paramref name="leadSeconds" /> before the playhead to
    ///     <paramref name="lagSeconds" /> after, in the document's ticks, clamped to the round the playhead is
    ///     in when <paramref name="clampToRound" />, and to the parse's first and last tick when known.
    ///     A round runs from its start to the tick before the next one starts, the reel's rule; before the
    ///     first round (warmup) there is nothing to clamp to.
    /// </summary>
    /// <param name="playhead">Frame-clock tick.</param>
    /// <param name="leadSeconds">Seconds before.</param>
    /// <param name="lagSeconds">Seconds after.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <param name="rounds">The demo's rounds, frame clock, or null.</param>
    /// <param name="clampToRound">Whether to keep the span inside the playhead's round.</param>
    /// <param name="firstTick">The parse's first tick, or 0 when unknown.</param>
    /// <param name="lastTick">The parse's last tick, or 0 when unknown.</param>
    public static (int FromTick, int ToTick) SpanFor(int playhead, double leadSeconds, double lagSeconds,
        int tickRate, IReadOnlyList<CachedRound>? rounds, bool clampToRound, int firstTick = 0, int lastTick = 0)
    {
        int rate = tickRate > 0 ? tickRate : 64;
        int from = playhead - (int)Math.Round(Math.Max(0, leadSeconds) * rate);
        int to = playhead + (int)Math.Round(Math.Max(0, lagSeconds) * rate);

        if (clampToRound && rounds is { Count: > 0 })
        {
            CachedRound? current = null;
            CachedRound? next = null;
            foreach (CachedRound round in rounds.OrderBy(r => r.StartTickFrameClock))
            {
                if (round.StartTickFrameClock <= playhead)
                {
                    current = round;
                }
                else
                {
                    next = round;
                    break;
                }
            }

            if (current is not null)
            {
                from = Math.Max(from, current.StartTickFrameClock);
                if (next is not null)
                {
                    to = Math.Min(to, next.StartTickFrameClock - 1);
                }
            }
        }

        from = Math.Max(from, Math.Max(0, firstTick));
        if (lastTick > 0)
        {
            to = Math.Min(to, lastTick);
        }

        // A playhead outside the parse's own range can leave nothing between the clamps; the store refuses
        // an inverted span, so the tag shrinks to one tick rather than being lost.
        return (from, Math.Max(from, to));
    }

    [RelayCommand]
    private void PressButton(TagPaletteButtonViewModel? button)
    {
        if (button is not null)
        {
            Press(button.Button);
        }
    }

    [RelayCommand]
    private void ReloadPalettes()
    {
        string id = Palette.Id;
        _store.Reload();
        SelectPalette(id);
    }

    private TagInstance NewInstance(TagPaletteButton button)
    {
        TagDocument document = _session.Document!;
        int tickRate = document.Clock.TickRate > 0 ? document.Clock.TickRate : _tickRate();
        (int from, int to) = SpanFor(_playhead(), button.LeadSeconds, button.LagSeconds, tickRate, _session.Rounds,
            Palette.ClampToRound, document.Clock.FirstTick, document.Clock.LastTick);

        return new TagInstance
        {
            Id = Guid.NewGuid(),
            Code = button.Code ?? "",
            FromTick = from,
            ToTick = to,
            Source = TagSources.Human
        };
    }

    // Reveals the panel a press leads to. A panel whose group has a sticky value is answered from it and
    // passed over, so a sticky group is asked once per session; when the flow runs out the tag is written.
    private void Advance(string? then)
    {
        string? next = then;
        while (next is not null && Palette.PanelById(next) is { } panel && _pending is not null)
        {
            string group = panel.Group ?? "";
            if (Palette.IsSticky(group) && StickyValue(group) is { } value)
            {
                AddLabel(_pending, group, value);
                next = panel.Then;
                continue;
            }

            ShowPanel(panel);
            RaiseInstanceText();
            return;
        }

        Finish();
    }

    private static void AddLabel(TagInstance instance, string group, string value)
    {
        if (!instance.Labels.Any(l => string.Equals(l.Group, group, StringComparison.Ordinal)
                                      && string.Equals(l.Value, value, StringComparison.Ordinal)))
        {
            instance.Labels.Add(new TagLabel(group, value));
        }
    }

    private string? StickyValue(string group)
    {
        foreach (KeyValuePair<string, string> sticky in _sticky)
        {
            if (string.Equals(sticky.Key, group, StringComparison.Ordinal))
            {
                return sticky.Value;
            }
        }

        return null;
    }

    private void SetSticky(string group, string value)
    {
        _sticky.RemoveAll(s => string.Equals(s.Key, group, StringComparison.Ordinal));
        _sticky.Add(new KeyValuePair<string, string>(group, value));
        OnPropertyChanged(nameof(StickyText));
        OnPropertyChanged(nameof(HasSticky));
    }

    private void ShowPanel(TagPalettePanel? panel)
    {
        CurrentPanel = panel;
        Buttons.Clear();
        if (panel is not null)
        {
            foreach (TagPaletteButton button in panel.Buttons)
            {
                Buttons.Add(new TagPaletteButtonViewModel(button));
            }
        }

        OnPropertyChanged(nameof(CurrentPanel));
        OnPropertyChanged(nameof(PanelTitle));
    }

    private TagInstance? LastInstance() =>
        _lastId is { } id ? _session.Document?.Instances.FirstOrDefault(i => i.Id == id) : null;

    private string Gesture(Playback2DAction action) =>
        _keymap.GestureText(action) is { Length: > 0 } text ? text : "unbound";

    private static string Describe(TagInstance instance)
    {
        string labels = string.Join(", ", instance.Labels.Select(l => l.Group.Length == 0 ? l.Value : $"{l.Group}={l.Value}"));
        string text = labels.Length == 0 ? instance.Code : $"{instance.Code} · {labels}";
        return instance.Note is { Length: > 0 } note ? $"{text} · “{note}”" : text;
    }

    private void RaiseInstanceText()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(LastText));
    }

    // The session raises Changed from the thread pool after a save; the panel's bindings are UI state.
    private void OnSessionChanged() => _post(() =>
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastText));
    });

    private void OnStoreReloaded() => _post(() =>
    {
        OnPropertyChanged(nameof(Palettes));
        OnPropertyChanged(nameof(DiagnosticsText));
        OnPropertyChanged(nameof(HasDiagnostics));
    });
}
