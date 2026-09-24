#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Services.Review;

#endregion

namespace DemoViewer.NET.ViewModels.Review;

/// <summary>
///     The Review tab: the <see cref="ReviewQueue" /> as a list a reviewer walks top to bottom. Title
///     cards open sections; each clip shows its demo, its range on the demo's clock, the note the
///     surface that queued it wrote, and the one-line question the reviewer answers. A click opens 2D
///     playback at the clip's first tick through the same seek seam Result Cards use.
///     <para>
///         <b>Rows are reconciled, not rebuilt.</b> Every edit in a row writes through to the queue,
///         and the queue's <see cref="ReviewQueue.Changed" /> comes back here. When the ids are the
///         same ones in the same order, each row takes its entry in place, so the text box being typed
///         in keeps its focus and caret; only a structural change (an add, a remove, a move) rebuilds
///         the list.
///     </para>
///     <para>
///         <b>A manual pick</b> is a clip around the playhead of the demo the workspace has open, read
///         from the module context the tab was last activated with; with no demo open there is nothing
///         to pick and the action says so.
///     </para>
/// </summary>
public sealed partial class ReviewQueueTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>What the browser host says about a queue that does not outlive the tab.</summary>
    public const string BrowserNote = "session only: this browser tab forgets the queue when it reloads";

    /// <summary>How far either side of the playhead a manual pick reaches.</summary>
    public const int PickSeconds = 5;

    private readonly Func<ISituationPlayback?> _playback;
    private readonly ReviewQueue _queue;

    private IModuleContext? _context;

    /// <summary>The inline guard on Clear: armed by the button, never a modal.</summary>
    [ObservableProperty]
    private bool _showClearConfirm;

    /// <summary>What the last open or pick did, when it did not simply work.</summary>
    [ObservableProperty]
    private string _statusLine = "";

    /// <param name="queue">The shared queue.</param>
    /// <param name="playback">The seek seam a clip opens through; null on a host with no 2D tab.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    public ReviewQueueTabViewModel(ReviewQueue queue, Func<ISituationPlayback?>? playback = null, bool? isBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
        _playback = playback ?? (() => null);
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();
        _queue.Changed += Reconcile;
        Reconcile();
    }

    /// <summary>True on the WASM head: the queue lives for the session only.</summary>
    public bool IsBrowser { get; }

    /// <summary>The queue, in order: title cards and clips.</summary>
    public ObservableCollection<ReviewRowViewModel> Rows { get; } = [];

    /// <summary>There is anything queued.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>"12 clips · 3 sections · 4 demos", or empty with nothing queued.</summary>
    public string HeaderLine
    {
        get
        {
            List<ReviewRowViewModel> clips = [.. Rows.Where(r => r.IsClip)];
            if (Rows.Count == 0)
            {
                return "";
            }

            int sections = Rows.Count - clips.Count;
            int demos = clips.Select(r => r.Entry.DemoPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return string.Create(CultureInfo.InvariantCulture,
                $"{Plural(clips.Count, "clip")} · {Plural(sections, "section")} · {Plural(demos, "demo")}");
        }
    }

    /// <summary>Why the queue file was not read, or null; while set the file is left alone.</summary>
    public string? FileProblem => _queue.FileProblem;

    /// <summary>There is a file problem to show.</summary>
    public bool HasFileProblem => _queue.FileProblem is not null;

    /// <inheritdoc />
    public void OnActivated(IModuleContext context)
    {
        _context = context;
        AddAtPlayheadCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    public void OnDeactivated()
    {
    }

    /// <inheritdoc />
    public void Dispose() => _queue.Changed -= Reconcile;

    /// <summary>Opens 2D playback at the clip's first tick; a title card opens nothing.</summary>
    /// <param name="row">The row clicked.</param>
    public async Task OpenAsync(ReviewRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.IsClip)
        {
            return;
        }

        string file = Path.GetFileName(row.Entry.DemoPath);
        if (_playback() is not { } playback)
        {
            StatusLine = "no 2D playback on this host";
            return;
        }

        try
        {
            StatusLine = await playback.SeekAsync(row.Entry.DemoPath, row.Entry.FromTick)
                ? ""
                : $"could not open {file}";
        }
        catch (Exception ex)
        {
            StatusLine = $"could not open {file}: {ex.Message}";
        }
    }

    internal void Move(ReviewRowViewModel row, int delta) => _queue.Move(row.Entry.Id, delta);

    internal void Remove(ReviewRowViewModel row) => _queue.Remove([row.Entry.Id]);

    internal void EditText(ReviewRowViewModel row, ReviewTextField field, string value)
    {
        switch (field)
        {
            case ReviewTextField.Title:
                _queue.SetTitle(row.Entry.Id, value);
                break;
            case ReviewTextField.Note:
                _queue.SetNote(row.Entry.Id, value);
                break;
            case ReviewTextField.Question:
                _queue.SetQuestion(row.Entry.Id, value);
                break;
        }
    }

    /// <summary>A new title card at the end.</summary>
    [RelayCommand]
    private void AddSection() => _queue.AddSection("New section");

    private bool CanAddAtPlayhead() => _context is { HasDemo: true, DemoPath.Length: > 0 };

    /// <summary>A clip <see cref="PickSeconds" /> either side of the open demo's playhead.</summary>
    [RelayCommand(CanExecute = nameof(CanAddAtPlayhead))]
    private void AddAtPlayhead()
    {
        if (_context is not { HasDemo: true, DemoPath: { Length: > 0 } path } context)
        {
            StatusLine = "open a demo to pick a clip at its playhead";
            return;
        }

        int rate = context.TickRate > 0 ? context.TickRate : 64;
        int tick = context.CurrentTick;
        ReviewEntry clip = ReviewEntry.Clip(path, tick - PickSeconds * rate, tick + PickSeconds * rate,
            $"picked at {Clock(tick, rate)}", ReviewSources.Manual, rate, context.DemoSha256);
        StatusLine = _queue.Add([clip]) == 0 ? "that clip is already queued" : "";
    }

    /// <summary>Arms the Clear confirmation.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void Clear() => ShowClearConfirm = true;

    /// <summary>Empties the queue, the Reels tray's clips with it, once confirmed.</summary>
    [RelayCommand]
    private void ConfirmClear()
    {
        ShowClearConfirm = false;
        _queue.Clear();
    }

    /// <summary>Dismisses the Clear confirmation.</summary>
    [RelayCommand]
    private void CancelClear() => ShowClearConfirm = false;

    /// <summary>"m:ss" from a frame-clock tick.</summary>
    /// <param name="tick">Frame-clock tick.</param>
    /// <param name="tickRate">The demo's tick rate; 64 when unknown.</param>
    public static string Clock(int tick, int tickRate)
    {
        int whole = (int)Math.Floor(Math.Max(0, tick) / (double)(tickRate > 0 ? tickRate : 64));
        return string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:D2}");
    }

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

    private void Reconcile()
    {
        IReadOnlyList<ReviewEntry> entries = _queue.Entries;
        bool sameShape = entries.Count == Rows.Count && entries.Select(e => e.Id).SequenceEqual(Rows.Select(r => r.Entry.Id));
        if (sameShape)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Rows[i].Apply(entries[i]);
            }
        }
        else
        {
            Rows.Clear();
            foreach (ReviewEntry entry in entries)
            {
                Rows.Add(new ReviewRowViewModel(this, entry));
            }
        }

        // Positions and section counts move with any structural change, and are cheap to recompute.
        int position = 0;
        ReviewRowViewModel? section = null;
        int inSection = 0;
        foreach (ReviewRowViewModel row in Rows)
        {
            if (row.IsSection)
            {
                section?.SetClipCount(inSection);
                section = row;
                inSection = 0;
                continue;
            }

            row.SetPosition(++position);
            inSection++;
        }

        section?.SetClipCount(inSection);

        if (Rows.Count == 0)
        {
            ShowClearConfirm = false;
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HeaderLine));
        ClearCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>Which text a row edit writes.</summary>
public enum ReviewTextField
{
    Title,
    Note,
    Question
}

/// <summary>
///     One row of the Review tab: a title card or a clip. The editable texts write through to the
///     queue on commit; <see cref="Apply" /> takes the queue's value back without overwriting a text
///     that differs from it only by the trimming the queue does, so a trailing space being typed is
///     not eaten.
/// </summary>
public sealed partial class ReviewRowViewModel : ViewModelBase
{
    private readonly ReviewQueueTabViewModel _owner;

    /// <summary>A clip's question, or empty.</summary>
    [ObservableProperty]
    private string _question = "";

    /// <summary>A clip's note, or a title card's subtitle.</summary>
    [ObservableProperty]
    private string _note = "";

    /// <summary>A title card's title.</summary>
    [ObservableProperty]
    private string _title = "";

    private bool _applying;

    /// <param name="owner">The tab the row belongs to.</param>
    /// <param name="entry">The queue entry.</param>
    public ReviewRowViewModel(ReviewQueueTabViewModel owner, ReviewEntry entry)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        _owner = owner;
        Entry = entry;
        Apply(entry);
    }

    /// <summary>The entry as the queue last had it.</summary>
    public ReviewEntry Entry { get; private set; }

    public bool IsClip => Entry.Kind == ReviewEntryKind.Clip;

    public bool IsSection => Entry.Kind == ReviewEntryKind.Section;

    /// <summary>The clip's number in the queue, one-based, counting clips only; 0 on a title card.</summary>
    public int Position { get; private set; }

    /// <summary>"4 clips" under a title card.</summary>
    public string ClipCountText { get; private set; } = "";

    /// <summary>The demo's file name.</summary>
    public string DemoLabel => Path.GetFileName(Entry.DemoPath);

    /// <summary>"12:04 to 12:24 · 20 s" on the demo's clock.</summary>
    public string RangeText
    {
        get
        {
            string range = $"{ReviewQueueTabViewModel.Clock(Entry.FromTick, Entry.TickRate)} to "
                           + ReviewQueueTabViewModel.Clock(Entry.ToTick, Entry.TickRate);
            return Entry.Seconds is double seconds
                ? string.Create(CultureInfo.InvariantCulture, $"{range} · {Math.Round(seconds)} s")
                : range;
        }
    }

    /// <summary>Where the clip came from, as a word.</summary>
    public string SourceText => Entry.Source;

    internal void Apply(ReviewEntry entry)
    {
        Entry = entry;
        _applying = true;
        try
        {
            if (Normalize(Title) != entry.Title)
            {
                Title = entry.Title;
            }

            if (Normalize(Note) != entry.Note)
            {
                Note = entry.Note;
            }

            if (Normalize(Question) != entry.Question)
            {
                Question = entry.Question;
            }
        }
        finally
        {
            _applying = false;
        }

        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(DemoLabel));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(SourceText));
    }

    internal void SetPosition(int position)
    {
        if (Position != position)
        {
            Position = position;
            OnPropertyChanged(nameof(Position));
        }
    }

    internal void SetClipCount(int count)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"{count} clip{(count == 1 ? "" : "s")}");
        if (ClipCountText != text)
        {
            ClipCountText = text;
            OnPropertyChanged(nameof(ClipCountText));
        }
    }

    partial void OnTitleChanged(string value) => Write(ReviewTextField.Title, value);

    partial void OnNoteChanged(string value) => Write(ReviewTextField.Note, value);

    partial void OnQuestionChanged(string value) => Write(ReviewTextField.Question, value);

    private void Write(ReviewTextField field, string value)
    {
        if (!_applying)
        {
            _owner.EditText(this, field, value);
        }
    }

    private static string Normalize(string? text) => (text ?? "").ReplaceLineEndings(" ").Trim();

    [RelayCommand]
    private Task Open() => _owner.OpenAsync(this);

    [RelayCommand]
    private void MoveUp() => _owner.Move(this, -1);

    [RelayCommand]
    private void MoveDown() => _owner.Move(this, 1);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);
}
