#region

using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Services.Export;

#endregion

namespace DemoViewer.NET.ViewModels.Playback2D;

/// <summary>
///     Maps the background <see cref="IExportJobService" /> status onto a status-strip chip and its flyout
///     — the export instance of the shared <c>StatusChip</c> pattern, built to
///     <see cref="Highlights.ReelJobStatusViewModel" />'s shape because the two jobs are the same shape:
///     one at a time, started fire-and-forget, running for minutes while the user carries on.
///     <para>
///         <b>This class is the missing half.</b> <c>ExportJobService</c> marshalled phase, frame counts,
///         throughput, elapsed and the error to the UI thread and raised <c>StatusChanged</c> on every
///         one — and nothing anywhere subscribed. <c>CancelAsync</c> had no production call site at all,
///         so an export that had started could not be stopped by any means short of killing the app, and
///         a failure set <c>Error</c> into a status no surface read. Three doc comments described the chip
///         you are looking at as though it already existed.
///     </para>
///     <para>
///         Unlike the reel chip this one has a determinate bar: the export contract carries
///         <c>FramesDone</c> / <c>FramesTotal</c>, so a fraction here is a measurement rather than a
///         decoration.
///     </para>
/// </summary>
public sealed partial class Playback2DExportStatusViewModel : ViewModelBase, IDisposable
{
    /// <summary>How many log lines the flyout keeps. Enough for the encoder line plus ffmpeg's tail.</summary>
    private const int MaxLogLines = 120;

    private readonly IExportJobService _job;

    // Bounded: ffmpeg writes a progress line roughly once a second for the whole render, so an unbounded
    // list would be a slow leak that only long exports (the ones hardest to diagnose) could produce.
    private readonly Queue<string> _log = new();
    private readonly Action<string>? _openFolder;

    /// <summary>"312 / 4 800 frames · 118 fps · 2:41 elapsed · ~3:20 left", or empty when not running.</summary>
    [ObservableProperty]
    private string _detail = "";

    private bool _disposed;

    [ObservableProperty]
    private string? _errorText;

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

    /// <summary>The tail of the export's own log — the chosen encoder, then ffmpeg's stderr.</summary>
    [ObservableProperty]
    private string _logText = "";

    [ObservableProperty]
    private string? _outputPath;

    [ObservableProperty]
    private double _progressFraction;

    private ExportJobStatus _status = ExportJobStatus.Idle;

    /// <summary>Creates the mapper over a running job service and seeds the chip from its CURRENT status.</summary>
    /// <param name="job">The background export job.</param>
    /// <param name="openFolder">Opens the finished file's folder; null = no launcher (browser, tests).</param>
    public Playback2DExportStatusViewModel(IExportJobService job, Action<string>? openFolder = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        _job = job;
        _openFolder = openFolder;

        Chip = new StatusChipViewModel
        {
            FlyoutContent = this
        };

        _job.StatusChanged += OnStatusChanged;
        Apply(_job.Status);
    }

    /// <summary>The status-strip chip this VM drives.</summary>
    public StatusChipViewModel Chip { get; }

    /// <summary>No export has run, or the chip was dismissed. The shell reconciles presence on this.</summary>
    public bool IsIdle => !IsRunning && !IsCompleted && !IsFailed && !IsCancelled;

    /// <summary>True when the finished job carries a message worth copying.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    /// <summary>True once anything has been logged — gates the flyout's log section.</summary>
    public bool HasLog => LogText.Length > 0;

    /// <summary>Whether the Open-folder affordance applies.</summary>
    public bool CanOpenFolder => IsCompleted && !string.IsNullOrEmpty(OutputPath) && _openFolder is not null;

    /// <summary>
    ///     A paste-ready block for a failed export: the phase, how far it got, the file, and the log tail
    ///     ffmpeg actually printed. The tail matters because a bare "ffmpeg exited with code 1" gives
    ///     nothing to act on.
    /// </summary>
    public string CopyDiagnosticsText
    {
        get
        {
            StringBuilder sb = new();
            sb.Append("DemoViewer 2D export — ").AppendLine(_status.Phase.ToString().ToUpperInvariant());
            sb.Append("Frames: ").Append(_status.FramesDone).Append(" of ").Append(_status.FramesTotal)
                .AppendLine();
            sb.Append("Elapsed: ").AppendLine(Duration(_status.Elapsed));
            if (_status.OutputPath is { Length: > 0 } outPath)
            {
                sb.Append("Output: ").AppendLine(outPath);
            }

            sb.Append("Error: ").AppendLine(string.IsNullOrWhiteSpace(ErrorText) ? "(none)" : ErrorText);
            if (LogText.Length > 0)
            {
                sb.AppendLine("--- log ---").AppendLine(LogText);
            }

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
        _job.StatusChanged -= OnStatusChanged;
    }

    /// <summary>Raised when the flyout's Dismiss fires — the shell removes the chip.</summary>
    public event EventHandler? DismissRequested;

    /// <summary>
    ///     One line from the export's own diagnostics: the encoder the ladder chose, then whatever ffmpeg
    ///     writes to stderr.
    ///     <para>
    ///         <b>Callable from any thread</b>, which is the whole point — the runner reports from the
    ///         export's pool thread. The ring is behind a lock, and the two bound properties are published
    ///         on the UI thread: an <c>ObservableObject</c> raising <c>PropertyChanged</c> off-thread
    ///         reaches <c>AvaloniaObject.SetValue</c> through the binding and throws "call from invalid
    ///         thread" — from inside ffmpeg's stderr pump, where nothing would catch it. The rate is one
    ///         line per second or so, not per frame, so a post per line costs nothing.
    ///     </para>
    /// </summary>
    /// <param name="line">The line, as the runner produced it.</param>
    public void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        string joined;
        lock (_log)
        {
            _log.Enqueue(line.TrimEnd());
            while (_log.Count > MaxLogLines)
            {
                _log.Dequeue();
            }

            joined = string.Join(Environment.NewLine, _log);
        }

        Publish(() =>
        {
            LogText = joined;
            OnPropertyChanged(nameof(HasLog));
            OnPropertyChanged(nameof(CopyDiagnosticsText));
        });
    }

    // CheckAccess is true in a harness with no platform at all, which is what lets the pure-VM cases run
    // this inline — the same reason ExportJobService.SetStatus is written this way.
    private static void Publish(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    /// <summary>Cancels a running export: kills ffmpeg, removes the partial file, releases the gate.</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private Task Cancel() => _job.CancelAsync();

    /// <summary>Opens the finished file's folder.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (!string.IsNullOrEmpty(OutputPath))
        {
            _openFolder?.Invoke(OutputPath);
        }
    }

    /// <summary>Dismisses a finished chip.</summary>
    [RelayCommand]
    private void Dismiss() => DismissRequested?.Invoke(this, EventArgs.Empty);

    private void OnStatusChanged(object? sender, ExportJobStatus status)
    {
        if (!_disposed)
        {
            Apply(status);
        }
    }

    /// <summary>The single status→chip + status→flyout map. Called at construction and per change.</summary>
    /// <param name="status">The job's new status.</param>
    public void Apply(ExportJobStatus status)
    {
        _status = status;
        ProgressFraction = status.Fraction;

        if (status.IsIdle)
        {
            SetChip(StatusChipDotState.Off, false, "Export · idle");
            ShowSection();
            Headline = "";
            Detail = "";
        }
        else
        {
            switch (status.Phase)
            {
                case ExportPhase.Preparing:
                case ExportPhase.Seeking:
                    SetChip(StatusChipDotState.Working, true, "Export · preparing…");
                    ShowSection(true);
                    // Seeking is the one from-zero tracker replay that reaches the first frame, and on a
                    // long demo it is minutes with no frame counter moving. Saying so is the difference
                    // between "working" and "hung".
                    Headline = status.Phase == ExportPhase.Seeking
                        ? "Replaying the demo up to the first exported frame…"
                        : "Preparing the export…";
                    break;

                case ExportPhase.Rendering:
                    SetChip(StatusChipDotState.Working, true,
                        $"Export · {(int)Math.Round(status.Fraction * 100)}%");
                    ShowSection(true);
                    Headline = "Rendering the video.";
                    break;

                case ExportPhase.Finalizing:
                    SetChip(StatusChipDotState.Working, true, "Export · finishing…");
                    ShowSection(true);
                    Headline = "Flushing the encoder.";
                    break;

                case ExportPhase.Completed:
                    SetChip(StatusChipDotState.Good, false, "Export · done");
                    ShowSection(completed: true);
                    Headline = $"Export complete — {status.FramesDone} frames.";
                    break;

                case ExportPhase.Cancelled:
                    SetChip(StatusChipDotState.Off, false, "Export · cancelled");
                    ShowSection(cancelled: true);
                    Headline = "Export cancelled — the partial file was removed.";
                    break;

                default: // Failed
                    SetChip(StatusChipDotState.Error, false, "Export · failed");
                    ShowSection(failed: true);
                    Headline = "The video export failed.";
                    break;
            }

            Detail = BuildDetail(status);
        }

        OutputPath = status.OutputPath;
        ErrorText = status.Error;

        CancelCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();

        // Derived from the four section flags and from OutputPath, so each needs its own raise: the chip's
        // presence and the Open-folder button are bound to them.
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(CopyDiagnosticsText));
    }

    // Frames first, because that is the unit the whole contract is in; throughput and ETA are only
    // meaningful once frames have actually been written, so they are appended rather than reserved.
    private static string BuildDetail(ExportJobStatus status)
    {
        StringBuilder sb = new();
        sb.Append(status.FramesDone.ToString("N0", CultureInfo.CurrentCulture))
            .Append(" / ")
            .Append(status.FramesTotal.ToString("N0", CultureInfo.CurrentCulture))
            .Append(" frames");

        if (status.FramesPerSecond > 0)
        {
            sb.Append(" · ").Append(status.FramesPerSecond.ToString("F0", CultureInfo.CurrentCulture))
                .Append(" fps");
        }

        sb.Append(" · ").Append(Duration(status.Elapsed)).Append(" elapsed");

        if (status.Eta is { } eta && status.IsRunning)
        {
            sb.Append(" · ~").Append(Duration(eta)).Append(" left");
        }

        return sb.ToString();
    }

    private static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);

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
