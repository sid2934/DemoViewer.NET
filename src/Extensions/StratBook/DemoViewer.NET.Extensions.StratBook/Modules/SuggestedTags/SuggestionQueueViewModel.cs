#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RoundTagger.Review;
using DemoViewer.NET.Services.Generated;
using DemoViewer.NET.Services.Tags;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>One pending proposal as the queue lists it.</summary>
public sealed partial class SuggestionRowViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    internal SuggestionRowViewModel(ProposalEntry entry, int tickRate)
    {
        Entry = entry;
        TagProposal p = entry.Proposal;
        int rate = tickRate > 0 ? tickRate : 64;
        Title = ProposalTrack.LabelOf(p);
        SideText = ProposalIds.SideName(p.Side);
        RoundText = string.Create(CultureInfo.InvariantCulture, $"r{p.Round}");
        SecondText = entry.RoundStartTick > 0
            ? SuggestionQueueViewModel.Clock((p.TriggerTick - entry.RoundStartTick) / (double)rate)
            : "";
        ConfidenceText = p.Confidence.ToString("0.00", CultureInfo.InvariantCulture);
        FactorsText = p.Factors.Count == 0
            ? "no factors"
            : string.Join(" × ", p.Factors.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Key} {f.Value:0.00}")));
        EvidenceText = string.Join('\n', p.Evidence.Select(e => e.Text));
        ReasonText = p.Evidence.Count == 0 ? "" : string.Join("; ", p.Evidence.Take(2).Select(e => e.Text));
        Step = ProposalTrack.StepOf(p.Confidence);
        State = entry.State;
    }

    public GeneratedState State { get; }

    public bool IsNew => State == GeneratedState.New;

    public bool IsDismissed => State == GeneratedState.Dismissed;

    /// <summary>"accepted" or "dismissed" on a settled row, else empty.</summary>
    public string StateText => State switch
    {
        GeneratedState.Accepted => "accepted",
        GeneratedState.Dismissed => "dismissed",
        _ => ""
    };

    public ProposalEntry Entry { get; }

    public TagProposal Proposal => Entry.Proposal;

    /// <summary><c>code@site</c>.</summary>
    public string Title { get; }

    public string SideText { get; }

    public string RoundText { get; }

    /// <summary>The trigger, as time since freeze end; empty when the file did not carry the round start.</summary>
    public string SecondText { get; }

    public string ConfidenceText { get; }

    /// <summary>The confidence's named factors, for the hover.</summary>
    public string FactorsText { get; }

    /// <summary>Why it fired, one line each.</summary>
    public string EvidenceText { get; }

    /// <summary>Why it fired in plain words, the first two evidence lines; the score's factors stay in the hover.</summary>
    public string ReasonText { get; }

    public bool HasReason => ReasonText.Length > 0;

    public ConfidenceStep Step { get; }

    public bool IsHigh => Step == ConfidenceStep.High;

    public bool IsLow => Step == ConfidenceStep.Low;
}

/// <summary>
///     The Proposal Queue: the open demo's pending proposals in round order,
///     docked with the Tag Palette in the 2D Playback tab, filtered by detector and minimum confidence,
///     with the keyboard flow the keymap routes here through <see cref="Execute" />.
///     <para>
///         <b>Selection is the mode.</b> Nothing is selected until a row, a band on the Suggested track or the
///         Review button picks one. Only then do J / K walk the queue instead of the Situations result set
///         (the keymap's <see cref="Playback2DBindingScope.WhenSuggestionSelected" /> scope), and only then
///         do Y, N, Enter and Ctrl+Y act; with no selection they are inert. A verdict
///         advances to the next pending proposal; the last one leaves the queue empty and the selection
///         with it.
///     </para>
///     <para>
///         <b>Verdicts go through the service</b>, which writes the Tag Store (the instance, then the verdict).
///         The open demo's tag session holds that document, so an accepted tag reaches the Tag Track through
///         the store's routing without this class touching the session. UI-thread affine.
///     </para>
/// </summary>
public sealed partial class SuggestionQueueViewModel : ObservableObject, IDisposable
{
    /// <summary>The detector filter's "no filter" entry.</summary>
    public const string AllDetectors = "all";

    /// <summary>The round filter's "no filter" entry.</summary>
    public const string AllRounds = "all rounds";

    private readonly Action<Action> _post;
    private readonly Action<int> _seekToTick;
    private readonly SuggestedTagsService? _service;
    private readonly Func<int> _tickRate;
    private readonly ProposalTrack _track;
    private readonly Action<bool>? _writeBackground;

    [ObservableProperty]
    private string _detectorFilter = AllDetectors;

    private bool _isBackgroundOn;

    private bool _disposed;

    // Unsaved edits per proposal, for the session: walking away with J or K keeps what was typed.
    private readonly Dictionary<string, TagEditorDraft> _drafts = new(StringComparer.Ordinal);

    [ObservableProperty]
    private bool _isConfirmingAcceptAll;

    [ObservableProperty]
    private string _roundFilter = AllRounds;

    [ObservableProperty]
    private double _minConfidence;

    // Accepted and dismissed proposals: hidden unless on.
    [ObservableProperty]
    private bool _showSettled;

    private int _settledCount;

    private ProposalSet? _set;

    private SuggestionRowViewModel? _selected;

    private string? _sha256;

    [ObservableProperty]
    private string _statusText = "";

    /// <param name="service">The engine, or null on a host without one: the queue then stays empty.</param>
    /// <param name="track">The Suggested track the pending set is mirrored onto.</param>
    /// <param name="seekToTick">Moves the shared clock; the tab's funnel, so LiveSync sees the seek.</param>
    /// <param name="tickRate">The open demo's tick rate, for the seconds the editor and rows speak in.</param>
    /// <param name="post">UI-thread marshal for the service's change event.</param>
    /// <param name="readBackground">The persisted library-sweep opt-in; off when null.</param>
    /// <param name="writeBackground">Persists the opt-in; the toggle does nothing lasting when null.</param>
    public SuggestionQueueViewModel(
        SuggestedTagsService? service,
        ProposalTrack track,
        Action<int> seekToTick,
        Func<int> tickRate,
        Action<Action> post,
        Func<bool>? readBackground = null,
        Action<bool>? writeBackground = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(seekToTick);
        ArgumentNullException.ThrowIfNull(tickRate);
        ArgumentNullException.ThrowIfNull(post);
        _service = service;
        _track = track;
        _seekToTick = seekToTick;
        _tickRate = tickRate;
        _post = post;
        _writeBackground = writeBackground;
        _isBackgroundOn = readBackground?.Invoke() ?? false;
        if (_service is not null)
        {
            _service.Changed += OnServiceChanged;
        }
    }

    /// <summary>The pending proposals the filters let through, in round order.</summary>
    public ObservableCollection<SuggestionRowViewModel> Rows { get; } = [];

    /// <summary><see cref="AllRounds" /> and every round with a pending proposal.</summary>
    public ObservableCollection<string> RoundFilters { get; } = [AllRounds];

    /// <summary>The editor open on the selected proposal, or null.</summary>
    public TagEditorViewModel? Editor { get; private set; }

    /// <summary>True while the editor is open.</summary>
    public bool IsEditing => Editor is not null;

    /// <summary>The palette's codes and label groups for the editor; empty when the host has no palette.</summary>
    public Func<TagVocabulary>? Vocabulary { get; set; }

    /// <summary>The shared clock's tick, for the editor's "set here" buttons.</summary>
    public Func<int>? Playhead { get; set; }

    /// <summary>Raised when the editor opens or closes.</summary>
    public event Action? EditorChanged;
    /// <summary>The detector filter's choices: <see cref="AllDetectors" /> then every detector id.</summary>
    public IReadOnlyList<string> DetectorFilters { get; } = [AllDetectors, .. ProposalDetection.All.Select(d => d.Id)];

    /// <summary>The demo the queue shows, or null.</summary>
    public string? DemoPath { get; private set; }

    /// <summary>Every pending proposal of the demo, before the filters.</summary>
    public int PendingCount => _set?.Pending.Count ?? 0;

    /// <summary>The header's counter, "Suggested: 12".</summary>
    public string HeaderText => string.Create(CultureInfo.InvariantCulture, $"Suggested: {PendingCount}");

    /// <summary>True on the browser host: the header says "session only", the annotation panel's words.</summary>
    public bool IsSessionOnly => _service is not null && !_service.IsPersistent;

    /// <summary>The selected proposal's row, or null. A selection seeks to the proposal's start.</summary>
    public SuggestionRowViewModel? Selected
    {
        get => _selected;
        set => Select(value, true);
    }

    /// <summary>Whether a proposal is selected: the keymap's suggestion scope and the verdict keys follow it.</summary>
    public bool HasSelection => _selected is not null;

    /// <summary>
    ///     The library sweep: whether every demo with round facts gets its proposals built in the
    ///     background, not only the open one. Persisted as the Strat Book's <c>suggestedTags.background</c> setting.
    /// </summary>
    public bool IsBackgroundOn
    {
        get => _isBackgroundOn;
        set
        {
            if (!SetProperty(ref _isBackgroundOn, value))
            {
                return;
            }

            // Turning the sweep on is only useful if the scheduler hears about it: the backlog is
            // derived, so one re-check submits every demo that now wants a build.
            _writeBackground?.Invoke(value);
            if (value)
            {
                _service?.Passes?.RecheckAll();
            }
        }
    }

    /// <summary>The Accept All confirm's question.</summary>
    public string ConfirmText =>
        string.Create(CultureInfo.InvariantCulture, $"Accept {AcceptAllCount} suggestions? Ctrl+Y again to confirm");

    private int AcceptAllCount => Rows.Count(r => r.IsNew);

    /// <summary>"Show settled (n)": the accepted and dismissed proposals under the filters.</summary>
    public string SettledLabel => GeneratedInbox.SettledLabel(_settledCount);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_service is not null)
        {
            _service.Changed -= OnServiceChanged;
        }
    }

    /// <summary>Shows a demo's proposals, or nothing for null. The hash keys the verdicts when the index has none yet.</summary>
    /// <param name="demoPath">The open demo.</param>
    /// <param name="sha256">Its content hash, if the caller has one.</param>
    public void Attach(string? demoPath, string? sha256)
    {
        bool sameDemo = string.Equals(DemoPath, demoPath, StringComparison.OrdinalIgnoreCase);
        DemoPath = demoPath;
        _sha256 = sha256 ?? (sameDemo ? _sha256 : null);
        if (!sameDemo)
        {
            CancelModes();
            Select(null, false);
        }

        Reload();
    }

    /// <summary>Re-reads the demo's proposals and verdicts, keeping the selection when its proposal is still pending.</summary>
    public void Reload()
    {
        _set = _service is not null && DemoPath is { } path ? _service.Load(path, _sha256) : null;
        _track.SetProposals(_set?.Pending.Select(e => e.Proposal) ?? []);
        List<string> rounds =
        [
            AllRounds,
            .. (_set?.Pending ?? []).Select(e => e.Proposal.Round).Distinct().Order()
                .Select(r => string.Create(CultureInfo.InvariantCulture, $"round {r}"))
        ];
        if (!RoundFilters.SequenceEqual(rounds, StringComparer.Ordinal))
        {
            string keep = RoundFilter;
            RoundFilters.Clear();
            foreach (string r in rounds)
            {
                RoundFilters.Add(r);
            }

            RoundFilter = RoundFilters.Contains(keep) ? keep : AllRounds;
        }

        RebuildRows();
        StatusText = Describe();
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(IsSessionOnly));
    }

    /// <summary>Selects the first pending row: the header's Review button, the way into the keyboard flow.</summary>
    [RelayCommand]
    private void Review() => Select(Rows.FirstOrDefault(r => r.IsNew), true);

    /// <summary>Drops the selection, which hands J / K back to the Situations walk.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        CancelModes();
        Select(null, false);
    }

    /// <summary>Asks the service to build this demo's proposals now, whatever the background setting.</summary>
    [RelayCommand]
    private void Detect()
    {
        if (_service is null || DemoPath is not { } path)
        {
            return;
        }

        if (!_service.CanDetect(path))
        {
            StatusText = "This demo has no round facts yet, and every detector needs them.";
            return;
        }

        _service.Request(path);
        StatusText = "Detecting…";
    }

    /// <summary>Picks the first pending proposal a band on the Suggested track holds.</summary>
    /// <param name="proposalIds">The band's proposals, in the track's order.</param>
    public void SelectFromTrack(IReadOnlyList<string> proposalIds)
    {
        ArgumentNullException.ThrowIfNull(proposalIds);
        if (proposalIds.Count == 0)
        {
            return;
        }

        // A press on the band already holding the selection walks to its next member.
        int at = _selected is null ? -1 : IndexOf(proposalIds, _selected.Proposal.Id);
        string id = proposalIds[(at + 1) % proposalIds.Count];
        if (Rows.FirstOrDefault(r => r.Proposal.Id == id) is { } row)
        {
            Select(row, false); // the band press seeks to the band's start already
        }
    }

    /// <summary>
    ///     The keymap's six actions. False when the action does nothing here, so the key stays unhandled:
    ///     every one of them with no selection, and the walk at either end.
    /// </summary>
    /// <param name="action">The resolved action.</param>
    public bool Execute(Playback2DAction action)
    {
        if (_service is null || _selected is null)
        {
            return false;
        }

        switch (action)
        {
            case Playback2DAction.SuggestionNext:
                CancelModes();
                return Step(1);
            case Playback2DAction.SuggestionPrev:
                CancelModes();
                return Step(-1);
            case Playback2DAction.SuggestionAccept:
                CancelModes();
                return Verdict(accept: true);
            case Playback2DAction.SuggestionReject:
                CancelModes();
                return Verdict(accept: false);
            case Playback2DAction.SuggestionEdit:
                IsConfirmingAcceptAll = false;
                BeginEdit();
                return true;
            case Playback2DAction.SuggestionAcceptAll:
                CloseEditor(keepDraft: true);
                if (!IsConfirmingAcceptAll)
                {
                    IsConfirmingAcceptAll = true;
                    OnPropertyChanged(nameof(ConfirmText));
                    return true;
                }

                ConfirmAcceptAll();
                return true;
            default:
                return false;
        }
    }

    // The editor's save: accepts with the edit (edited, or recoded when the code changed) and advances.
    private bool SaveDraft(SuggestionRowViewModel row, TagEditorDraft draft)
    {
        if (_service is null || DemoPath is not { } path)
        {
            return false;
        }

        TagInstanceEdit edit = new(draft.FromTick, draft.ToTick, draft.Labels, draft.Note,
            draft.Code, draft.Positions.Count > 0 ? draft.Positions : null);
        string? next = NextIdAfter(row);
        if (!_service.Accept(path, row.Proposal.Id, edit, _sha256))
        {
            return false;
        }

        _drafts.Remove(row.Proposal.Id);
        CloseEditor(keepDraft: false);
        Advance(next);
        return true;
    }

    /// <summary>Closes the editor without a verdict (Esc); what was typed stays as this proposal's draft.</summary>
    [RelayCommand]
    private void CancelEdit() => CloseEditor(keepDraft: true);

    private void CloseEditor(bool keepDraft)
    {
        if (Editor is null)
        {
            return;
        }

        if (keepDraft && _selected is { } row && Editor.Snapshot() is { } draft)
        {
            _drafts[row.Proposal.Id] = draft;
        }

        Editor = null;
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(IsEditing));
        EditorChanged?.Invoke();
    }

    /// <summary>The confirm bar's Accept button.</summary>
    [RelayCommand]
    private void ConfirmAcceptAll()
    {
        IsConfirmingAcceptAll = false;
        if (_service is null || DemoPath is not { } path)
        {
            return;
        }

        int accepted = _service.AcceptAll(path, MinConfidence,
            DetectorFilter == AllDetectors ? null : DetectorFilter, _sha256, RoundOfFilter());
        Reload();
        Select(Rows.FirstOrDefault(), true);
        StatusText = string.Create(CultureInfo.InvariantCulture, $"Accepted {accepted} suggestions.");
    }

    /// <summary>The confirm bar's Cancel button.</summary>
    [RelayCommand]
    private void CancelAcceptAll() => IsConfirmingAcceptAll = false;

    /// <summary>The row's buttons: accept this one.</summary>
    [RelayCommand]
    private void AcceptRow(SuggestionRowViewModel? row)
    {
        if (row is not null)
        {
            Select(row, false);
            Verdict(accept: true);
        }
    }

    /// <summary>A dismissed row's Restore: offers the proposal again.</summary>
    [RelayCommand]
    private void RestoreRow(SuggestionRowViewModel? row)
    {
        if (_service is null || DemoPath is not { } path || row is not { IsDismissed: true })
        {
            return;
        }

        if (!_service.Restore(path, row.Proposal.Id, _sha256))
        {
            StatusText = "The restore could not be written.";
            return;
        }

        Reload();
    }

    /// <summary>The row's buttons: dismiss this one.</summary>
    [RelayCommand]
    private void RejectRow(SuggestionRowViewModel? row)
    {
        if (row is not null)
        {
            Select(row, false);
            Verdict(accept: false);
        }
    }

    /// <summary><c>m:ss</c> for seconds since freeze end.</summary>
    /// <param name="seconds">Seconds, not negative.</param>
    internal static string Clock(double seconds)
    {
        int whole = Math.Max(0, (int)Math.Floor(seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
    }

    partial void OnDetectorFilterChanged(string value) => RebuildRows();

    partial void OnRoundFilterChanged(string value) => RebuildRows();

    partial void OnMinConfidenceChanged(double value) => RebuildRows();

    partial void OnShowSettledChanged(bool value) => RebuildRows();

    private bool Step(int direction)
    {
        int index = _selected is null ? -1 : Rows.IndexOf(_selected);
        int target = index + direction;
        if (target < 0 || target >= Rows.Count)
        {
            return false; // the ends do not wrap, like the result walk
        }

        Select(Rows[target], true);
        return true;
    }

    private bool Verdict(bool accept)
    {
        if (_service is null || DemoPath is not { } path || _selected is not { } row)
        {
            return false;
        }

        if (!row.IsNew)
        {
            StatusText = $"This suggestion is already {row.StateText}.";
            return true;
        }

        string? next = NextIdAfter(row);
        bool written = accept
            ? _service.Accept(path, row.Proposal.Id, null, _sha256)
            : _service.Reject(path, row.Proposal.Id, _sha256);
        if (!written)
        {
            StatusText = accept ? "The tag could not be written." : "The dismissal could not be written.";
            return true; // the key was ours; the status says why nothing moved
        }

        Advance(next);
        return true;
    }

    // After a verdict: the next row if there was one, else the one before, else nothing selected. The
    // service raised Changed, but that is posted; the reload here is what the next key press sees.
    private void Advance(string? nextId)
    {
        Reload();
        SuggestionRowViewModel? next = nextId is null ? null : Rows.FirstOrDefault(r => r.Proposal.Id == nextId);
        Select(next ?? Rows.LastOrDefault(), true);
    }

    private string? NextIdAfter(SuggestionRowViewModel row)
    {
        int index = Rows.IndexOf(row);
        return index >= 0 && index + 1 < Rows.Count ? Rows[index + 1].Proposal.Id : null;
    }

    private void Select(SuggestionRowViewModel? row, bool seek)
    {
        if (ReferenceEquals(row, _selected))
        {
            if (row is not null && seek)
            {
                _seekToTick(row.Proposal.FromTick);
            }

            return;
        }

        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = row;
        if (row is not null)
        {
            row.IsSelected = true;
            if (seek)
            {
                _seekToTick(row.Proposal.FromTick);
            }
        }
        else
        {
            CancelModes();
        }

        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void RebuildRows()
    {
        string? keep = _selected?.Proposal.Id;
        int rate = _tickRate();
        Rows.Clear();
        _settledCount = 0;
        foreach (ProposalEntry entry in _set?.Entries ?? [])
        {
            TagProposal p = entry.Proposal;
            if (p.Confidence < MinConfidence
                || (DetectorFilter != AllDetectors && !string.Equals(p.Detector, DetectorFilter, StringComparison.Ordinal))
                || (RoundOfFilter() is { } round && p.Round != round))
            {
                continue;
            }

            if (entry.State != GeneratedState.New)
            {
                _settledCount++;
                if (!ShowSettled)
                {
                    continue;
                }
            }

            Rows.Add(new SuggestionRowViewModel(entry, rate));
        }

        _selected = null;
        SuggestionRowViewModel? kept = keep is null ? null : Rows.FirstOrDefault(r => r.Proposal.Id == keep);
        if (kept is not null)
        {
            kept.IsSelected = true;
            _selected = kept;
        }
        else
        {
            CancelModes();
        }

        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(SettledLabel));
    }

    private void BeginEdit()
    {
        if (_selected is not { } row)
        {
            return;
        }

        TagProposal p = row.Proposal;
        TagEditorDraft draft = _drafts.TryGetValue(p.Id, out TagEditorDraft? kept)
            ? kept.Clone()
            : new TagEditorDraft
            {
                Code = p.Code,
                FromTick = p.FromTick,
                ToTick = p.ToTick,
                Labels = [.. p.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new TagLabel(l.Key, l.Value))]
            };
        Editor = new TagEditorViewModel($"Suggested: {row.Title} ({row.RoundText})", draft,
            Vocabulary?.Invoke() ?? TagVocabulary.Empty, _tickRate(), row.Entry.RoundStartTick,
            Playhead ?? (() => p.FromTick), null, d => SaveDraft(row, d), null, () => CloseEditor(keepDraft: true));
        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(IsEditing));
        EditorChanged?.Invoke();
    }

    // The round the filter names, or null for every round.
    private int? RoundOfFilter() =>
        RoundFilter.StartsWith("round ", StringComparison.Ordinal)
        && int.TryParse(RoundFilter.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int round)
            ? round
            : null;

    private void CancelModes()
    {
        CloseEditor(keepDraft: true);
        IsConfirmingAcceptAll = false;
    }

    private string Describe()
    {
        if (_service is null || DemoPath is null)
        {
            return "";
        }

        if (_set is { VerdictsUnreadable: true })
        {
            return "The verdicts file for this demo cannot be read, so nothing is offered until it is fixed or removed.";
        }

        if (_set is null || _set.Entries.Count == 0)
        {
            return _service.IsDetecting ? "Detecting…" : "No suggestions for this demo yet.";
        }

        return PendingCount == 0 ? "Every suggestion has a verdict." : "";
    }

    private void OnServiceChanged(string path) =>
        _post(() =>
        {
            if (!_disposed && string.Equals(path, DemoPath, StringComparison.OrdinalIgnoreCase))
            {
                Reload();
            }
        });

    private static int IndexOf(IReadOnlyList<string> ids, string id)
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
}
