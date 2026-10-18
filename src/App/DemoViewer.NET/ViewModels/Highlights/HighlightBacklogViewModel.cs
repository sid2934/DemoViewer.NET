#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.ViewModels.Highlights;

/// <summary>
///     The Highlights tab's notice for demos whose highlights are not current: missing, from older rules, or
///     failed. Progress is not shown here; the highlights pass runs on the shared queue and shows there.
///     A pure projection of <see cref="HighlightScanService" /> and the <see cref="DemoCacheStore" /> rows.
/// </summary>
public sealed partial class HighlightBacklogViewModel : ViewModelBase, IDisposable
{
    private readonly HighlightScanService _scanner;
    private readonly DemoCacheStore _store;
    private bool _disposed;

    /// <summary>Rows whose last scan failed: the <c>Retry failed</c> population. Never refreshed on their own.</summary>
    [ObservableProperty]
    private int _failedCount;

    /// <summary>The notice text, empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _notice = "";

    /// <summary>Demos without current highlights: never scanned, or scanned under other rules.</summary>
    [ObservableProperty]
    private int _staleCount;

    /// <param name="scanner">The highlights pass; owns the staleness rule and the opt-in.</param>
    /// <param name="store">The cache the pass writes into.</param>
    public HighlightBacklogViewModel(HighlightScanService scanner, DemoCacheStore store)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(store);
        _scanner = scanner;
        _store = store;

        // ScanProgressChanged covers queue moves and opt-in changes, Changed covers row rewrites.
        _scanner.ScanProgressChanged += Refresh;
        _store.Changed += OnCacheChanged;
        Refresh();
    }

    /// <summary>True when at least one row failed: gates <c>Retry failed</c>.</summary>
    public bool HasFailed => FailedCount > 0;

    /// <summary>Whether the notice is shown at all.</summary>
    public bool IsRelevant => StaleCount > 0 || FailedCount > 0;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scanner.ScanProgressChanged -= Refresh;
        _store.Changed -= OnCacheChanged;
    }

    /// <summary>Re-queues every failed row at user priority. Per row, so the demos that worked are not re-read.</summary>
    [RelayCommand(CanExecute = nameof(HasFailed))]
    private void RetryAllFailed()
    {
        // Materialised first: RequestScan updates rows, and the store raises Changed while we would be enumerating.
        List<string> failed =
        [
            .. _store.Contents.Where(r => r.AnalysisState == DemoAnalysisState.Failed).Select(r => r.Path)
        ];
        foreach (string path in failed)
        {
            _scanner.RequestScan(path);
        }
    }

    private void OnCacheChanged(string? changedPath) => Refresh();

    private void Refresh()
    {
        StaleCount = _scanner.PendingPaths().Count;
        FailedCount = _store.Contents.Count(r => r.AnalysisState == DemoAnalysisState.Failed);
        Notice = BuildNotice(StaleCount, FailedCount, _scanner.BackgroundScanEnabled);

        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(IsRelevant));
        RetryAllFailedCommand.NotifyCanExecuteChanged();
    }

    internal static string BuildNotice(int stale, int failed, bool backgroundScan)
    {
        List<string> parts = [];
        if (stale > 0)
        {
            string demos = stale == 1 ? "1 demo has" : $"{stale} demos have";
            parts.Add(backgroundScan
                ? $"{demos} highlights from older rules or none yet. They are refreshing in the background."
                : $"{demos} highlights from older rules or none yet. They refresh as demos are processed, "
                  + "or turn on the background scan in Settings to refresh them all.");
        }

        if (failed > 0)
        {
            parts.Add(failed == 1 ? "1 demo failed its highlight scan." : $"{failed} demos failed their highlight scan.");
        }

        return string.Join(" ", parts);
    }
}
