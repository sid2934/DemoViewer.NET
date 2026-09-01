#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.Highlights;

/// <summary>
///     Maps the background <see cref="IReelJobService" /> status onto the second status-strip chip and its
///     flyout. It owns one reusable <see cref="StatusChipViewModel" />
///     whose <c>FlyoutContent</c> is this VM (the app <c>ViewLocator</c> resolves
///     <c>Views/Highlights/ReelJobStatusView</c> for the body): the Reel instance of the shared
///     <c>StatusChip</c> pattern, alongside the Live Sync chip (the ≥2× that justifies the shared control).
///     <para>
///         <b>Contract-faithful reductions.</b> <see cref="ReelJobStatus" /> carries only clip-level counts,
///         <c>CurrentClipLabel</c>, and <c>FailedClipIndices</c>: no per-clip labels and no intra-clip
///         percent, so the per-clip list labels non-active rows generically ("Clip k") and the progress
///         bar is <em>indeterminate</em> (an intra-clip fill has no seam in the App contract). The
///         status carries no <c>DryRun</c> flag either, so the chip renders identically for a real vs a mock
///         run; the dry-run framing lives in the dialog.
///     </para>
/// </summary>
public sealed partial class ReelJobStatusViewModel : ViewModelBase, IDisposable
{
    private readonly Action<string>? _openFolder;
    private readonly IReelJobService _reelJob;

    [ObservableProperty]
    private bool _canOpenFolder;

    [ObservableProperty]
    private bool _canRetry;

    private bool _disposed;

    [ObservableProperty]
    private string? _errorText;

    // ── Flyout content (per-state prose + rows) ──
    [ObservableProperty]
    private string _headline = "";

    [ObservableProperty]
    private bool _isCancelled;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private bool _isRunning;

    // The error last written to the diagnostics pillar: dedupes the Apply call (fired on every status
    // change, and again at construction) so one failed job logs exactly one Error line, not N.
    private string? _loggedError;

    [ObservableProperty]
    private string? _outputPath;

    // The most recent status, retained so CopyDiagnosticsText can assemble a full, paste-ready report
    // (phase + clip counts + failed indices + current clip) rather than just the bare message.
    private ReelJobStatus _status = ReelJobStatus.Idle;

    /// <summary>
    ///     Constructs the mapper over the running job service. Seeds the chip from the CURRENT status (never
    ///     blank) and tracks <see cref="IReelJobService.StatusChanged" /> (raised on the UI thread).
    /// </summary>
    /// <param name="reelJob">The background reel-generation service.</param>
    /// <param name="openFolder">Opens the finished reel's output folder ("Open folder"); null = no launcher.</param>
    public ReelJobStatusViewModel(IReelJobService reelJob, Action<string>? openFolder = null)
    {
        ArgumentNullException.ThrowIfNull(reelJob);
        _reelJob = reelJob;
        _openFolder = openFolder;

        Chip = new StatusChipViewModel
        {
            FlyoutContent = this
        };
        _reelJob.StatusChanged += OnStatusChanged;
        Apply(_reelJob.Status);
    }

    /// <summary>The status-strip chip this VM drives (added to <c>MainViewModel.Chips</c> while relevant).</summary>
    public StatusChipViewModel Chip { get; }

    /// <summary>
    ///     No job has run (or the VM was reset). The Reels dashboard's INLINE job strip is gated on this: the
    ///     strip is a second view of THIS VM, and a permanently-present idle strip would reserve
    ///     vertical space for a job that does not exist.
    /// </summary>
    public bool IsIdle => !IsRunning && !IsCompleted && !IsFailed && !IsCancelled;

    /// <summary>The per-clip status list, rebuilt on every status change.</summary>
    public ObservableCollection<ReelClipStatusRow> Clips { get; } = [];

    /// <summary>True when the finished job carries a diagnosable error message: gates the Copy affordance.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>
    ///     A paste-ready diagnostic block for the failed job (the Copy button + the diagnostics-log line share
    ///     it). Full context: phase, clip tally, failed indices, the active clip, and the verbatim engine
    ///     error, so a user handing this back is a self-contained bug report, not a truncated sentence.
    /// </summary>
    public string CopyDiagnosticsText
    {
        get
        {
            StringBuilder sb = new();
            sb.Append("DemoViewer reel generation — ").AppendLine(_status.Phase.ToString().ToUpperInvariant());
            sb.Append("Clips: ").Append(_status.ClipsCompleted).Append(" of ")
                .Append(Math.Max(_status.ClipsTotal, _status.ClipsCompleted)).AppendLine(" completed");
            if (_status.FailedClipIndices.Count > 0)
            {
                sb.Append("Failed clip #: ")
                    .AppendLine(string.Join(", ", _status.FailedClipIndices.Select(i => i + 1)));
            }

            if (_status.CurrentClipLabel is { Length: > 0 } label)
            {
                sb.Append("Current clip: ").AppendLine(DisplayText.Sanitize(label));
            }

            if (_status.OutputPath is { Length: > 0 } outPath)
            {
                sb.Append("Output: ").AppendLine(outPath);
            }

            sb.Append("Error: ").AppendLine(string.IsNullOrWhiteSpace(ErrorText) ? "(none)" : ErrorText);
            return sb.ToString().TrimEnd();
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
        _reelJob.StatusChanged -= OnStatusChanged;
    }

    /// <summary>Raised when the flyout's Dismiss fires: the shell removes the chip.</summary>
    public event EventHandler? DismissRequested;

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Cancels the running job (stops the capture session, restores the install). Enabled while running.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task Cancel() => _reelJob.CancelAsync();

    /// <summary>Re-submits the failed + never-started clips as a NEW job (separate output files).</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => _reelJob.RetryRemaining();

    /// <summary>Opens the finished reel's output folder.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (!string.IsNullOrEmpty(OutputPath))
        {
            _openFolder?.Invoke(OutputPath);
        }
    }

    /// <summary>Dismisses a finished chip: raises <see cref="DismissRequested" /> for the shell.</summary>
    [RelayCommand]
    private void Dismiss() => DismissRequested?.Invoke(this, EventArgs.Empty);

    // ── Engine → UI mapping ───────────────────────────────────────────────────

    private void OnStatusChanged(object? sender, ReelJobStatus status)
    {
        if (!_disposed)
        {
            Apply(status);
        }
    }

    /// <summary>The single status→chip + status→flyout map. Called at construction and per change.</summary>
    public void Apply(ReelJobStatus status)
    {
        _status = status;
        int total = Math.Max(status.ClipsTotal, status.ClipsCompleted);
        // CurrentClipLabel is the engine echo of ReelClip.Label, which is built from RAW
        // in-demo player-name-bearing highlight titles: sanitize at the render boundary like
        // every other rendered player name (hostile bidi/combining-mark names crash the wrap
        // splitter; see DisplayText).
        string? clipLabel = status.CurrentClipLabel is { Length: > 0 } rawLabel
            ? DisplayText.Sanitize(rawLabel)
            : null;

        switch (status.Phase)
        {
            case ReelJobPhase.StartingSession:
                SetChip(StatusChipDotState.Working, true, "Reel · starting…");
                ShowSection(true);
                // "in-engine capture", not OBS: the pinned provider is the CS2 present hook + ffmpeg.
                Headline = "Starting the capture session… (real: CS2 + in-engine capture, up to ~2 min)";
                break;

            case ReelJobPhase.Capturing:
                int current = Math.Min(status.ClipsCompleted + 1, Math.Max(1, total));
                SetChip(StatusChipDotState.Working, true, $"Reel · {current} of {total}");
                ShowSection(true);
                Headline = $"Rendering clip {current} of {total}"
                           + (clipLabel is not null ? $" — {clipLabel}" : "");
                break;

            case ReelJobPhase.Completed:
                SetChip(StatusChipDotState.Good, false, "Reel · done");
                ShowSection(completed: true);
                Headline = $"Reel complete — {status.ClipsCompleted} clip"
                           + (status.ClipsCompleted == 1 ? "" : "s") + " rendered.";
                break;

            case ReelJobPhase.Failed:
                SetChip(StatusChipDotState.Error, false,
                    $"Reel · failed ({status.FailedClipIndices.Count})");
                ShowSection(failed: true);
                // Generic headline; the specific reason renders once, in the failed section body (icon +
                // neutral text): no duplication.
                Headline = "Reel generation failed.";
                break;

            case ReelJobPhase.Cancelled:
                SetChip(StatusChipDotState.Off, false, "Reel · cancelled");
                ShowSection(cancelled: true);
                Headline = "Reel cancelled.";
                break;

            default: // Idle: the chip is not shown in this phase, but keep the map total.
                SetChip(StatusChipDotState.Off, false, "Reel · idle");
                ShowSection();
                Headline = "";
                break;
        }

        OutputPath = status.OutputPath;
        ErrorText = status.Error;
        CanOpenFolder = status.Phase == ReelJobPhase.Completed
                        && !string.IsNullOrEmpty(status.OutputPath) && _openFolder is not null;
        CanRetry = status.HasRetryableClips;

        RebuildClipRows(status, total, clipLabel);

        // A failed job is a user-visible fault worth persisting: mirror it to the diagnostics pillar
        // (Diagnostics tab + rolling log file) so the report survives dismissing the chip. Deduped on the
        // message, because Apply also runs at construction and on unrelated re-applies of the same status.
        if (status.Phase == ReelJobPhase.Failed && status.Error is { Length: > 0 } err
                                                && !string.Equals(err, _loggedError, StringComparison.Ordinal))
        {
            _loggedError = err;
            ILogger reelsLog = DiagnosticsLog.CreateLogger(AppLog.ReelsCategory);
            if (reelsLog.IsEnabled(LogLevel.Error))
            {
                AppLog.ReelGenerationFailed(reelsLog, CopyDiagnosticsText);
            }
        }
        else if (status.Phase is ReelJobPhase.Idle or ReelJobPhase.StartingSession)
        {
            _loggedError = null; // a new run re-arms the one-shot so its own failure logs afresh
        }

        CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();

        // IsIdle is derived from four [ObservableProperty] flags, so it needs its own raise: the dashboard's
        // inline strip binds to it and would otherwise never appear.
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CopyDiagnosticsText));
    }

    private void RebuildClipRows(ReelJobStatus status, int total, string? clipLabel)
    {
        Clips.Clear();
        HashSet<int> failed = new(status.FailedClipIndices);
        for (int i = 0; i < total; i++)
        {
            ReelClipRowState state;
            if (failed.Contains(i))
            {
                state = ReelClipRowState.Failed;
            }
            else if (i < status.ClipsCompleted)
            {
                state = ReelClipRowState.Done;
            }
            else if (i == status.ClipsCompleted && status.IsRunning)
            {
                state = ReelClipRowState.Current;
            }
            else
            {
                state = ReelClipRowState.Queued;
            }

            string label = state == ReelClipRowState.Current && clipLabel is not null
                ? clipLabel
                : "Clip " + (i + 1).ToString(CultureInfo.InvariantCulture);
            Clips.Add(new ReelClipStatusRow(i + 1, label, state));
        }
    }

    private void SetChip(StatusChipDotState dot, bool pulsing, string label)
    {
        Chip.DotState = dot;
        Chip.IsPulsing = pulsing;
        Chip.Label = label;
    }

    private void ShowSection(
        bool running = false, bool completed = false, bool failed = false, bool cancelled = false)
    {
        IsRunning = running;
        IsCompleted = completed;
        IsFailed = failed;
        IsCancelled = cancelled;
    }
}

/// <summary>One row of the reel chip flyout's per-clip status list.</summary>
public sealed class ReelClipStatusRow(int number, string label, ReelClipRowState state)
{
    public int Number { get; } = number;

    /// <summary>The clip label: the live <c>CurrentClipLabel</c> for the active clip, else "Clip k".</summary>
    public string Label { get; } = label;

    public ReelClipRowState State { get; } = state;

    /// <summary>Status glyph: ✓ done · ◐ current · · queued · ✕ failed.</summary>
    public string Glyph => State switch
    {
        ReelClipRowState.Done => "✓",
        ReelClipRowState.Current => "◐",
        ReelClipRowState.Failed => "✕",
        _ => "·"
    };

    public bool IsDone => State == ReelClipRowState.Done;
    public bool IsCurrent => State == ReelClipRowState.Current;
    public bool IsFailed => State == ReelClipRowState.Failed;
    public bool IsQueued => State == ReelClipRowState.Queued;
}

/// <summary>Per-clip lifecycle for the reel chip flyout.</summary>
public enum ReelClipRowState
{
    Queued,
    Current,
    Done,
    Failed
}
