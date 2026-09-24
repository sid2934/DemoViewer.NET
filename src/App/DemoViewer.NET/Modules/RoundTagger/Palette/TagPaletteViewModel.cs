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
///     <para>
///         <b>Clicks on the map</b> (Click To Tag Position, tag-store.md §3.6). While the palette has focus a
///         left click on the map is a point for the tag being made, else for the last one written; the
///         tab resolves it (<see cref="TagPositionResolver" />) and hands it to <see cref="AttachPosition" />.
///         The second click on the same tag turns that point into a movement from it to the new one, and
///         the click after that starts a new point, so a tag can carry several of either.
///     </para>
///     <para>
///         <b>Label Mode</b> (plan §3). The second pass: the palette shows its labels panels instead of its
///         codes, and a label press adds to a tag that already exists, never making one. The target is the
///         tag picked on the Tag Track (<see cref="SelectForLabels" />) while it exists, else the tag under
///         the playhead that started last, so with overlapping tags the most recent one is labelled.
///         The panels start where the target's code leads (its button's <c>then</c>), else at the first
///         labels panel, follow each panel's <c>then</c> and come back to the start when the chain ends;
///         <see cref="Playback2DAction.TagLabelGroupNext" /> walks every labels panel for a group no chain
///         reaches. Each label is its own undoable edit, like a note or a click on a written tag. Sticky
///         labels are neither applied nor set: a second pass says exactly what it presses.
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

    // Label Mode's target as last shown, so a playhead move that keeps the same tag leaves the panel alone,
    // and its line as last raised, so the per-frame refresh raises nothing while the line stands still.
    private Guid? _labelTargetId;
    private string _labelTargetText = "";
    private Guid? _lastId;

    // The point the next click pairs with to make a movement: the tag it went on and the point itself.
    private (Guid Id, TagPosition Point)? _openPoint;

    [ObservableProperty]
    private string _noteDraft = "";

    private TagInstance? _pending;

    // The tag picked on the Tag Track for Label Mode; it wins over the playhead while it exists.
    private Guid? _selectedId;

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
    public string PanelTitle => CurrentPanel switch
    {
        { IsLabels: true } panel => string.IsNullOrEmpty(panel.Group) ? "label" : panel.Group,
        null when IsLabelMode => "no label groups in this palette",
        _ => "codes"
    };

    /// <summary>Whether the palette adds labels to existing tags instead of making new ones.</summary>
    public bool IsLabelMode { get; private set; }

    /// <summary>In Label Mode, the tag a label press goes on, as one line, or why there is none; else "".</summary>
    public string LabelTargetText => !IsLabelMode
        ? ""
        : LabelTarget() is { } target
            ? Describe(target)
            : "no tag under the playhead";

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
    public string HintText => IsLabelMode
        ? $"{Gesture(Playback2DAction.TagLabelMode)} back to tagging · "
          + $"{Gesture(Playback2DAction.TagLabelGroupNext)} next group · "
          + $"{Gesture(Playback2DAction.TagPaletteBack)} drop the pick, then leave · "
          + $"{Gesture(Playback2DAction.Undo)} undo · click a tag's band: label it, again: the next one there"
        : $"{Gesture(Playback2DAction.FocusTagPalette)} focus · {Gesture(Playback2DAction.TagPaletteBack)} back · "
          + $"{Gesture(Playback2DAction.TagNote)} note · {Gesture(Playback2DAction.TagClearSticky)} clear sticky · "
          + $"{Gesture(Playback2DAction.TagLabelMode)} label mode · "
          + $"{Gesture(Playback2DAction.Undo)} undo · click map: point, again: movement";

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
        ShowPanel(IsLabelMode ? LabelStartPanel(LabelTarget()) : Palette.Root);
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
        Playback2DAction.TagLabelMode => ToggleLabelMode(),
        Playback2DAction.TagLabelGroupNext => NextLabelGroup(),
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

        if (IsLabelMode)
        {
            return panel.IsLabels && LabelExisting(panel, button);
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
        // Label Mode: the first Esc drops the Tag Track pick (back to the playhead's tag), the next one
        // goes back to tagging.
        if (IsLabelMode)
        {
            if (_selectedId is not null)
            {
                _selectedId = null;
                RefreshLabelTarget();
                return true;
            }

            return SetLabelMode(false);
        }

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
    ///     Attaches a clicked point to the tag being made, else to the last one written. The first click on a
    ///     tag adds a position; the next one takes that position back out and writes a movement from it to
    ///     this point. On a pending tag the point rides the tag's one batch; on a written one each click is
    ///     its own undoable edit, so a Ctrl+Z takes back one click. False when there is no tag to put it on.
    /// </summary>
    /// <param name="position">The resolved point, from <see cref="TagPositionResolver" />.</param>
    public bool AttachPosition(TagPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (_session.Document is null)
        {
            return false;
        }

        if (_pending is { } pending)
        {
            AddPoint(pending, position);
            RaiseInstanceText();
            return true;
        }

        if (LastInstance() is not { } last)
        {
            return false;
        }

        TagInstance edited = last.Clone();
        AddPoint(edited, position);
        _session.Apply(new TagDelta.Replace(last.Id, edited));
        RaiseInstanceText();
        return true;
    }

    /// <summary>Switches Label Mode on or off. Turning it on writes a pending tag first.</summary>
    public bool ToggleLabelMode() => SetLabelMode(!IsLabelMode);

    /// <summary>
    ///     Turns Label Mode on or off. On, the palette shows the labels panels for the target; off, it
    ///     forgets the Tag Track pick and returns to the codes. False on the way in without an attached demo.
    /// </summary>
    /// <param name="on">Whether Label Mode should be on.</param>
    public bool SetLabelMode(bool on)
    {
        if (on == IsLabelMode)
        {
            return true;
        }

        if (on && _session.Document is null)
        {
            return false;
        }

        Finish();
        IsLabelMode = on;
        _selectedId = null;
        TagInstance? target = on ? LabelTarget() : null;
        _labelTargetId = target?.Id;
        ShowPanel(on ? LabelStartPanel(target) : Palette.Root);
        OnPropertyChanged(nameof(IsLabelMode));
        OnPropertyChanged(nameof(HintText));
        RaiseLabelTargetText();
        return true;
    }

    /// <summary>
    ///     Picks Label Mode's target from a Tag Track band: the run's first tag, or the one after the
    ///     current pick when it is already in this run, so clicking the same merged band again walks its
    ///     tags. False outside Label Mode or for an empty run.
    /// </summary>
    /// <param name="run">The ids of the tags in the clicked band, in the track's order.</param>
    public bool SelectForLabels(IReadOnlyList<Guid> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!IsLabelMode || run.Count == 0)
        {
            return false;
        }

        int at = _selectedId is { } current ? IndexOf(run, current) : -1;
        _selectedId = run[(at + 1) % run.Count];
        RefreshLabelTarget();
        return true;
    }

    /// <summary>
    ///     Re-resolves Label Mode's target after the playhead or the document moved, and when it is a
    ///     different tag restarts the panels at that tag's own. The tab calls it on every playhead update.
    /// </summary>
    public void RefreshLabelTarget()
    {
        if (!IsLabelMode)
        {
            return;
        }

        TagInstance? target = LabelTarget();
        if (target?.Id != _labelTargetId)
        {
            _labelTargetId = target?.Id;
            ShowPanel(LabelStartPanel(target));
        }

        RaiseLabelTargetText();
    }

    /// <summary>In Label Mode, shows the next labels panel in the palette's order, wrapping. False otherwise.</summary>
    public bool NextLabelGroup()
    {
        if (!IsLabelMode)
        {
            return false;
        }

        List<TagPalettePanel> panels = Palette.Panels.Where(p => p.IsLabels).ToList();
        if (panels.Count == 0)
        {
            return false;
        }

        int at = CurrentPanel is null ? -1 : panels.IndexOf(CurrentPanel);
        ShowPanel(panels[(at + 1) % panels.Count]);
        return true;
    }

    /// <summary>
    ///     The tag Label Mode labels when nothing is picked: of the tags whose span holds
    ///     <paramref name="tick" />, the one that started last, the first in the document on a tie. Null
    ///     when none does.
    /// </summary>
    /// <param name="document">The tag document.</param>
    /// <param name="tick">Frame-clock tick.</param>
    public static TagInstance? InstanceAt(TagDocument document, int tick)
    {
        ArgumentNullException.ThrowIfNull(document);
        TagInstanceRef? best = null;
        foreach (TagInstanceRef found in TagQuery.At(document, tick))
        {
            if (best is null || found.FromTick > best.FromTick)
            {
                best = found;
            }
        }

        return best is null ? null : document.Instances.FirstOrDefault(i => i.Id == best.Id);
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
    private void ToggleLabelModeButton() => ToggleLabelMode();

    [RelayCommand]
    private void NextLabelGroupButton() => NextLabelGroup();

    // A label press in Label Mode: one Replace of the target with the label added, so a Ctrl+Z takes back
    // one label and the instance count never moves. A label the tag already has writes nothing, and the
    // panels move on either way.
    private bool LabelExisting(TagPalettePanel panel, TagPaletteButton button)
    {
        if (LabelTarget() is not { } target)
        {
            return false;
        }

        TagInstance edited = target.Clone();
        AddLabel(edited, panel.Group ?? "", button.Value ?? "");
        if (edited.Labels.Count != target.Labels.Count)
        {
            _session.Apply(new TagDelta.Replace(target.Id, edited));
        }

        ShowPanel(Palette.PanelById(panel.Then) is { IsLabels: true } next ? next : LabelStartPanel(target));
        RaiseLabelTargetText();
        return true;
    }

    private TagInstance? LabelTarget()
    {
        if (_session.Document is not { } document)
        {
            return null;
        }

        if (_selectedId is { } id && document.Instances.FirstOrDefault(i => i.Id == id) is { } picked)
        {
            return picked;
        }

        return InstanceAt(document, _playhead());
    }

    // Where the target's own code leads, so a second pass asks what a first pass with this code would
    // have; else the palette's first labels panel. Null for a palette with no labels panels.
    private TagPalettePanel? LabelStartPanel(TagInstance? target)
    {
        if (target is not null)
        {
            foreach (TagPalettePanel panel in Palette.Panels.Where(p => !p.IsLabels))
            {
                foreach (TagPaletteButton button in panel.Buttons)
                {
                    if (string.Equals(button.Code, target.Code, StringComparison.Ordinal)
                        && Palette.PanelById(button.Then) is { IsLabels: true } then)
                    {
                        return then;
                    }
                }
            }
        }

        return Palette.Panels.FirstOrDefault(p => p.IsLabels);
    }

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] == id)
            {
                return i;
            }
        }

        return -1;
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

    // Pairs with the open point only when it is still the last point on this tag: an undo, a refresh or a
    // movement made since has taken it away, and a movement from a point the tag no longer holds would
    // invent one. Matched by value because the session's history holds copies, never the live objects.
    private void AddPoint(TagInstance instance, TagPosition position)
    {
        if (_openPoint is { } open && open.Id == instance.Id && instance.Positions.Count > 0
            && SamePoint(instance.Positions[^1], open.Point))
        {
            TagPosition from = instance.Positions[^1];
            instance.Positions.RemoveAt(instance.Positions.Count - 1);
            instance.Movements.Add(new TagMovement { From = from, To = position });
            _openPoint = null;
            return;
        }

        instance.Positions.Add(position);
        _openPoint = (instance.Id, position);
    }

    private static bool SamePoint(TagPosition a, TagPosition b) =>
        a.X.Equals(b.X) && a.Y.Equals(b.Y) && a.LevelMinZ.Equals(b.LevelMinZ) && a.Tick == b.Tick;

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
        if (instance.Positions.Count + instance.Movements.Count > 0)
        {
            IEnumerable<string> points = instance.Positions.Select(p => $"@{PlaceOf(p)}")
                .Concat(instance.Movements.Select(m => $"{PlaceOf(m.From)}→{PlaceOf(m.To)}"));
            text = $"{text} · {string.Join(", ", points)}";
        }

        return instance.Note is { Length: > 0 } note ? $"{text} · “{note}”" : text;
    }

    // An unresolved point still shows where it was clicked, so the line never hides a click.
    private static string PlaceOf(TagPosition position) =>
        position.Place ?? string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{position.X:0},{position.Y:0}");

    private void RaiseLabelTargetText()
    {
        string text = LabelTargetText;
        if (!string.Equals(text, _labelTargetText, StringComparison.Ordinal))
        {
            _labelTargetText = text;
            OnPropertyChanged(nameof(LabelTargetText));
        }
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
        RefreshLabelTarget(); // an undo can take back the target's label, or the target itself
    });

    private void OnStoreReloaded() => _post(() =>
    {
        OnPropertyChanged(nameof(Palettes));
        OnPropertyChanged(nameof(DiagnosticsText));
        OnPropertyChanged(nameof(HasDiagnostics));
    });
}
