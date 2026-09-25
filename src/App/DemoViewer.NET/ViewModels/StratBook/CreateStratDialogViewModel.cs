#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.ViewModels.StratBook;

/// <summary>
///     Everything the review knows before the walk: the round, the demo, and what Team Identity and Round Facts
///     say about it. Built on the UI thread by the 2D tab; nothing in it is read from a live object afterwards.
/// </summary>
/// <param name="Map">The map, the parser's spelling.</param>
/// <param name="Round"><c>ClipRound.Number</c>.</param>
/// <param name="FreezeEndTick">The round's freeze-end, frame clock.</param>
/// <param name="WindowEndTick">The next round's freeze-end, or null for the last round.</param>
/// <param name="DemoSha256">The demo's content hash, for <c>origin</c>; null when not computed.</param>
/// <param name="FileName">The demo's file name, for <c>origin</c>.</param>
/// <param name="Facts">The round's Round Facts row, or null: rows wait on CS2DemoKit #54 and nothing here needs them.</param>
/// <param name="OurKey">Our side's SteamID64s from Team Identity (the side key, else the me accounts); empty when unknown.</param>
/// <param name="Owner">The book the strat goes to: our side's team when Team Identity names one, else <c>me</c>.</param>
/// <param name="OwnerLabel">The book, as the review shows it.</param>
/// <param name="Epoch">The book-default key: our side's roster id for a team, <c>me</c> for the user's book, null for none.</param>
/// <param name="LevelMinZFor">A world Z's level key, from the tab's levels.</param>
public sealed record StratCaptureRequest(
    string Map,
    int Round,
    int FreezeEndTick,
    int? WindowEndTick,
    string? DemoSha256,
    string? FileName,
    RoundFacts? Facts,
    IReadOnlyCollection<string> OurKey,
    StratOwner Owner,
    string OwnerLabel,
    string? Epoch,
    Func<double, double> LevelMinZFor);

/// <summary>
///     Create Strat From Round's review (step-authoring.md §3.9): walks the round off the UI thread, then shows the
///     side, the slot map and the step list before anything is saved. Changing the side or a slot rebuilds the step
///     list from the same capture; nothing walks the demo twice.
///     <para>
///         <b>Side.</b> Team Identity's key decides it when one side at the freeze-end holds most of it; otherwise the
///         picker starts empty and says so (a side picker suffices where Team Identity has no team). <b>Slots.</b>
///         Strat pin, then the book default for the demo's epoch, then controller-slot order (strat-model.md §3.5);
///         the first mapping of an epoch seeds the book's default.
///     </para>
/// </summary>
public sealed partial class CreateStratDialogViewModel : ViewModelBase, IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Action<Action> _post;
    private readonly StratCaptureRequest _request;
    private readonly StratStore _store;
    private readonly Func<DateTime> _utcNow;
    private bool _rebuilding;

    [ObservableProperty]
    private bool _drawThrowArrows = true;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string? _selectedSide;

    [ObservableProperty]
    private string _sideNote = "";

    [ObservableProperty]
    private string _statusLine;

    /// <param name="request">The round and what is known about it.</param>
    /// <param name="walk">The tracker walk; <see cref="RoundCaptureWalker.Walk" /> over the open demo in the app.</param>
    /// <param name="store">The strat store the strat is committed to.</param>
    /// <param name="post">Marshals the walk's result onto the UI thread; synchronous when omitted.</param>
    /// <param name="utcNow">The creation time's clock.</param>
    public CreateStratDialogViewModel(StratCaptureRequest request, Func<IProgress<double>, CancellationToken, RoundCapture> walk,
        StratStore store, Action<Action>? post = null, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(walk);
        ArgumentNullException.ThrowIfNull(store);
        _request = request;
        _store = store;
        _post = post ?? (action => action());
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _name = DefaultName(request);
        _statusLine = string.Create(CultureInfo.InvariantCulture, $"reading round {request.Round}…");

        // Off the UI thread: the walk is O(frames before the round) and a late round takes seconds (§3.9).
        CancellationToken ct = _cancel.Token;
        Progress = new WalkProgress(this);
        Walking = Task.Run(() => walk(Progress, ct), ct).ContinueWith(t => _post(() => OnWalked(t)), TaskScheduler.Default);
    }

    /// <summary>The pane's title.</summary>
    public string Title => string.Create(CultureInfo.InvariantCulture, $"Create strat from round {_request.Round}");

    /// <summary>Where the strat goes.</summary>
    public string BookLine => "Book: " + _request.OwnerLabel;

    public static IReadOnlyList<string> SideOptions { get; } = [StratVocabulary.SideT, StratVocabulary.SideCt];

    /// <summary>A..E and who plays each; empty until the walk is back and a side is chosen.</summary>
    public ObservableCollection<SlotChoiceViewModel> Slots { get; } = [];

    /// <summary>The steps the strat will hold, one line each, on the round clock.</summary>
    public ObservableCollection<string> StepLines { get; } = [];

    /// <summary>The walk's result, or null while it runs or when it failed.</summary>
    public RoundCapture? Capture { get; private set; }

    public bool IsWalking => Capture is null && !Walking.IsCompleted;

    /// <summary>True when there is something to save: a capture, a side and a name.</summary>
    public bool CanCreate => Capture is not null && SelectedSide is not null && !string.IsNullOrWhiteSpace(Name);

    /// <summary>The walk, for tests to await; completes after its result was posted.</summary>
    internal Task Walking { get; }

    internal IProgress<double> Progress { get; }

    /// <summary>The strat the review saved, after <see cref="CreateCommand" />.</summary>
    public StratDocument? Created { get; private set; }

    /// <summary>The user closed the review, or a strat was created; the host drops the pane.</summary>
    public event Action? Closed;

    /// <summary>A strat was committed; its id.</summary>
    public event Action<Guid>? StratCreated;

    /// <inheritdoc />
    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }

    /// <summary>The side Team Identity's key puts us on at the freeze-end: the side holding most of it, or null.</summary>
    /// <param name="freezeEnd">The freeze-end's live pawns.</param>
    /// <param name="ourKey">Our SteamID64s.</param>
    public static int? OurSideAt(IEnumerable<CapturedPawn> freezeEnd, IReadOnlyCollection<string> ourKey)
    {
        ArgumentNullException.ThrowIfNull(freezeEnd);
        ArgumentNullException.ThrowIfNull(ourKey);
        if (ourKey.Count == 0)
        {
            return null;
        }

        HashSet<string> key = new(ourKey, StringComparer.Ordinal);
        List<CapturedPawn> pawns = [.. freezeEnd];
        int t = pawns.Count(p => p.Team == 2 && key.Contains(p.SteamId.ToString(CultureInfo.InvariantCulture)));
        int ct = pawns.Count(p => p.Team == 3 && key.Contains(p.SteamId.ToString(CultureInfo.InvariantCulture)));
        return t == ct ? null : t > ct ? 2 : 3;
    }

    /// <summary>The reviewed options: the chosen side, the slot rows as the token map, the round length, the arrows.</summary>
    public StratCaptureOptions? Options()
    {
        if (Capture is not { } capture || SideOf(SelectedSide) is not { } side)
        {
            return null;
        }

        Dictionary<int, string> tokens = [];
        foreach (SlotChoiceViewModel row in Slots)
        {
            if (row.Selected is { } player)
            {
                tokens[player.PlayerSlot] = row.Slot;
            }
        }

        int opponent = 0;
        foreach (CapturedPawn pawn in capture.FreezeEnd.Pawns.Where(p => p.Team != side).OrderBy(p => p.PlayerSlot))
        {
            if (opponent < StratVocabulary.OpponentSlots.Count)
            {
                tokens[pawn.PlayerSlot] = StratVocabulary.OpponentSlots[opponent++];
            }
        }

        return new StratCaptureOptions(side, tokens, StratClock.RoundSecondsFor(_request.Facts?.RoundTimeSeconds, null),
            DrawThrowArrows, _request.LevelMinZFor);
    }

    /// <summary>
    ///     Commits the strat as revision 1, seeds the book's slot default for the epoch when it has none, and hands
    ///     the id to the host. A refused save leaves the pane open with the reason.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        if (Capture is not { } capture || Options() is not { } options)
        {
            return;
        }

        StratOrigin origin = new() { DemoSha256 = _request.DemoSha256 ?? "", Round = _request.Round, FileName = _request.FileName };
        StratDocument document = StratFromRound.Document(capture, options, _request.Owner, _request.Map, Name.Trim(), origin,
            _request.Facts, _utcNow());
        StratSaveResult saved = _store.Save(document, [], string.Create(CultureInfo.InvariantCulture, $"created from round {_request.Round}"));
        if (!saved.Saved)
        {
            StatusLine = "strat could not be saved: " + saved.Reason;
            return;
        }

        SeedBookDefault(options);
        Created = document;
        StratCreated?.Invoke(document.Id);
        Close();
    }

    /// <summary>Stops the walk if it is still running and closes the pane; nothing is saved.</summary>
    [RelayCommand]
    private void Close()
    {
        _cancel.Cancel();
        Closed?.Invoke();
    }

    partial void OnSelectedSideChanged(string? value)
    {
        RebuildSlots();
        CreateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreate));
    }

    partial void OnDrawThrowArrowsChanged(bool value) => RebuildSteps();

    partial void OnNameChanged(string value)
    {
        CreateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreate));
    }

    private void OnWalked(Task<RoundCapture> walked)
    {
        if (walked.IsCanceled || _cancel.IsCancellationRequested)
        {
            return;
        }

        if (walked.IsFaulted || walked.Result.Moments.Count == 0 || walked.Result.FreezeEnd.Pawns.Count == 0)
        {
            StatusLine = "this round could not be read";
            OnPropertyChanged(nameof(IsWalking));
            return;
        }

        Capture = walked.Result;
        int? ours = OurSideAt(Capture.FreezeEnd.Pawns, _request.OurKey);
        SideNote = ours is null ? "pick the side you played; Team Identity has no team for this demo" : "side from Team Identity";
        StatusLine = "";
        OnPropertyChanged(nameof(IsWalking));
        OnPropertyChanged(nameof(Capture));

        // Set last: the side's change handler builds the slots and the steps from the capture.
        if (ours is { } side)
        {
            SelectedSide = side == 2 ? StratVocabulary.SideT : StratVocabulary.SideCt;
        }
        else
        {
            RebuildSlots();
        }

        CreateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreate));
    }

    // One row per slot, each offering our side's players at the freeze-end, seeded by the §3.5 precedence.
    private void RebuildSlots()
    {
        _rebuilding = true;
        try
        {
            foreach (SlotChoiceViewModel row in Slots)
            {
                row.SelectionChanged -= OnSlotChosen;
            }

            Slots.Clear();
            if (Capture is not { } capture || SideOf(SelectedSide) is not { } side)
            {
                StepLines.Clear();
                return;
            }

            List<CapturedPawn> ours = [.. capture.FreezeEnd.Pawns.Where(p => p.Team == side).OrderBy(p => p.PlayerSlot)];
            List<PlayerOption> options = [.. ours.Select(p => new PlayerOption(p.PlayerSlot, p.SteamId, Label(p)))];
            IReadOnlyDictionary<char, ulong> map = StratFromRound.SlotMap(ours, null, BookDefaults());
            Dictionary<int, string> tokens = StratFromRound.Tokens(capture.FreezeEnd.Pawns, side, map);

            foreach (string slot in StratVocabulary.Slots)
            {
                PlayerOption? chosen = options.FirstOrDefault(o => tokens.GetValueOrDefault(o.PlayerSlot) == slot);
                SlotChoiceViewModel row = new(slot, options, chosen);
                row.SelectionChanged += OnSlotChosen;
                Slots.Add(row);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        RebuildSteps();
    }

    // A player chosen for a second slot leaves the first, which takes the player the second slot had: a swap,
    // so the map stays one player per slot without a validation message.
    private void OnSlotChosen(SlotChoiceViewModel row, PlayerOption? previous)
    {
        if (_rebuilding)
        {
            return;
        }

        _rebuilding = true;
        try
        {
            foreach (SlotChoiceViewModel other in Slots)
            {
                if (!ReferenceEquals(other, row) && row.Selected is { } player && other.Selected?.PlayerSlot == player.PlayerSlot)
                {
                    other.Selected = previous;
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }

        RebuildSteps();
    }

    private void RebuildSteps()
    {
        StepLines.Clear();
        if (Capture is not { } capture || Options() is not { } options)
        {
            return;
        }

        foreach (StratStep step in StratFromRound.Steps(capture, options))
        {
            StepLines.Add(Describe(step));
        }
    }

    // The epoch's defaults are written once, by the first mapping made for it; later ones leave them alone so a
    // default the user edited in the Strat Book is never overwritten by a capture.
    private void SeedBookDefault(StratCaptureOptions options)
    {
        if (_request.Epoch is not { } epoch || Capture is not { } capture)
        {
            return;
        }

        Services.Strats.StratBook book = _store.LoadBook(_request.Owner);
        if (book.SlotDefaults.ContainsKey(epoch))
        {
            return;
        }

        Dictionary<string, string> slots = new(StringComparer.Ordinal);
        foreach ((int playerSlot, string token) in options.Tokens)
        {
            if (StratVocabulary.Slots.Contains(token) && capture.FreezeEnd.PawnIn(playerSlot) is { SteamId: > 0 } pawn)
            {
                slots[token] = pawn.SteamId.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (slots.Count > 0)
        {
            book.SlotDefaults[epoch] = slots;
            _store.SaveBook(book);
        }
    }

    private Dictionary<string, string>? BookDefaults() =>
        _request.Epoch is { } epoch && _store.LoadBook(_request.Owner).SlotDefaults.TryGetValue(epoch, out Dictionary<string, string>? slots)
            ? slots
            : null;

    private static int? SideOf(string? side) => side switch
    {
        StratVocabulary.SideT => 2,
        StratVocabulary.SideCt => 3,
        _ => null
    };

    // "1:46.7  C throw smoke from SideAlley": the call-sheet order, readable without the canvas.
    private static string Describe(StratStep step)
    {
        List<string> parts =
            [StratClock.Format(step.AtSeconds), step.Actor + " " + step.Verb + (step.Utility is { } utility ? " " + utility.Kind : "")];
        if (step.From?.Place is { } from)
        {
            parts.Add("from " + from);
        }

        if (step.To?.Place is { } to)
        {
            parts.Add("to " + to);
        }

        parts.Add(step.Positions.Count == 1 ? "1 token" : string.Create(CultureInfo.InvariantCulture, $"{step.Positions.Count} tokens"));
        return string.Join("  ", parts);
    }

    private static string Label(CapturedPawn pawn) =>
        string.IsNullOrWhiteSpace(pawn.Name)
            ? string.Create(CultureInfo.InvariantCulture, $"player {pawn.PlayerSlot + 1}")
            : DisplayText.Sanitize(pawn.Name);

    private static string DefaultName(StratCaptureRequest request) =>
        string.Create(CultureInfo.InvariantCulture, $"Round {request.Round}")
        + (string.IsNullOrEmpty(request.FileName) ? "" : " of " + Path.GetFileNameWithoutExtension(request.FileName));

    // Reports land on the UI thread through the post, and only while the walk is still the pane's.
    private sealed class WalkProgress(CreateStratDialogViewModel owner) : IProgress<double>
    {
        private int _lastPercent = -1;

        public void Report(double value)
        {
            int percent = (int)Math.Round(Math.Clamp(value, 0, 1) * 100);
            if (Interlocked.Exchange(ref _lastPercent, percent) == percent)
            {
                return;
            }

            owner._post(() =>
            {
                if (owner.Capture is null && !owner._cancel.IsCancellationRequested)
                {
                    owner.StatusLine = string.Create(CultureInfo.InvariantCulture, $"reading round {owner._request.Round}… {percent}%");
                }
            });
        }
    }
}

/// <summary>One of our players as a slot row offers them.</summary>
/// <param name="PlayerSlot">The controller slot.</param>
/// <param name="SteamId">The SteamID64, or 0 for a bot.</param>
/// <param name="Label">The sanitised name.</param>
public sealed record PlayerOption(int PlayerSlot, ulong SteamId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A slot and the player the review put in it.</summary>
public sealed partial class SlotChoiceViewModel : ObservableObject
{
    [ObservableProperty]
    private PlayerOption? _selected;

    /// <param name="slot"><c>A</c> to <c>E</c>.</param>
    /// <param name="options">Our side's players.</param>
    /// <param name="selected">The seeded choice.</param>
    public SlotChoiceViewModel(string slot, IReadOnlyList<PlayerOption> options, PlayerOption? selected)
    {
        Slot = slot;
        Options = options;
        _selected = selected;
    }

    public string Slot { get; }

    public IReadOnlyList<PlayerOption> Options { get; }

    /// <summary>The row and the player it held before.</summary>
    public event Action<SlotChoiceViewModel, PlayerOption?>? SelectionChanged;

    partial void OnSelectedChanged(PlayerOption? oldValue, PlayerOption? newValue) => SelectionChanged?.Invoke(this, oldValue);
}
