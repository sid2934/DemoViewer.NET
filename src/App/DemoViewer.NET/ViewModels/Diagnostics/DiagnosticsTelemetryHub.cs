#region

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.Diagnostics;

/// <summary>
///     App-side, bounded, WASM-safe sink for ALL diagnostics log rows, the first-party internal
///     pillar (analysis lifecycle, app orchestration; fed by a custom <c>ILoggerProvider</c>) AND the
///     out-of-process CSVG host logs (fed by the desktop LiveSync engine across the <c>AppHostHooks</c>
///     seam). One hub, one filterable list in the Diagnostics tab, plus the bottom Output drawer's
///     "Live Sync" mirror. Lives in the App project so the Browser head compiles (it simply stays
///     empty there, no in-process host emits on WASM).
///     <para>
///         <b>Two ingest paths, one bounded store.</b> Background producers (the internal logger
///         provider, on arbitrary threads) call <see cref="Enqueue" />, which coalesces bursts into a
///         single UI-thread drain, never one <c>Dispatcher.Post</c> per row. The existing CSVG bridge,
///         already marshalled to the UI thread per line, calls <see cref="AppendOnUiThread" /> directly.
///         Both funnel into the same ring, capped live at <see cref="_maxRows" /> (default from
///         <c>DiagnosticsSettings.MaxLogRows</c>) so memory stays bounded: an unbounded log channel was
///         a real prior leak.
///     </para>
/// </summary>
public sealed class DiagnosticsTelemetryHub
{
    private readonly Func<int> _maxRows;
    private readonly ConcurrentQueue<TelemetryLogRow> _pending = new();
    private readonly Action<Action> _uiPost;
    private int _drainScheduled;

    /// <summary>
    ///     Constructs the hub. <paramref name="maxRows" /> supplies the ring cap live (re-read on every
    ///     append, so a settings change takes effect immediately); default 5000. <paramref name="uiPost" />
    ///     marshals a drain onto the UI thread: defaults to <see cref="Dispatcher" />, but tests inject a
    ///     synchronous <c>a =&gt; a()</c> so <see cref="Enqueue" /> drains inline.
    /// </summary>
    public DiagnosticsTelemetryHub(Func<int>? maxRows = null, Action<Action>? uiPost = null)
    {
        _maxRows = maxRows ?? (static () => 5000);
        _uiPost = uiPost ?? (static a => Dispatcher.UIThread.Post(a));
    }

    /// <summary>All captured rows (newest last), bounded to the live <c>MaxLogRows</c> cap.</summary>
    public ObservableCollection<TelemetryLogRow> Logs { get; } = [];

    /// <summary>Raised (UI thread) after a batch of rows is appended, so filtered mirrors update incrementally.</summary>
    public event Action<IReadOnlyList<TelemetryLogRow>>? RowsAppended;

    /// <summary>
    ///     Thread-safe ingest for background producers. Queues the row and, if no drain is already
    ///     pending, schedules exactly one UI-thread drain, so a burst of N rows costs one marshal.
    /// </summary>
    public void Enqueue(TelemetryLogRow row)
    {
        _pending.Enqueue(row);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            _uiPost(Drain);
        }
    }

    /// <summary>Direct UI-thread append for producers that already marshalled (the CSVG per-line bridge).</summary>
    public void AppendOnUiThread(TelemetryLogRow row)
    {
        AppendCore(row);
        RowsAppended?.Invoke(new[]
        {
            row
        });
    }

    /// <summary>Drains the pending queue into <see cref="Logs" /> on the UI thread (one batch event).</summary>
    public void Drain()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);
        List<TelemetryLogRow>? batch = null;
        while (_pending.TryDequeue(out TelemetryLogRow? row))
        {
            AppendCore(row);
            (batch ??= []).Add(row);
        }

        if (batch is { Count: > 0 })
        {
            RowsAppended?.Invoke(batch);
        }
    }

    /// <summary>Clears every captured row (Disable, or the Diagnostics tab Clear button). UI-thread only.</summary>
    public void Clear()
    {
        _pending.Clear();
        Logs.Clear();
    }

    // Appends one row, dropping the oldest to honor the live cap. UI-thread only (all callers are).
    private void AppendCore(TelemetryLogRow row)
    {
        int cap = Math.Max(1, _maxRows());
        while (Logs.Count >= cap)
        {
            Logs.RemoveAt(0);
        }

        Logs.Add(row);
    }
}

/// <summary>
///     One diagnostics log row, from any source. <see cref="Source" /> ("Analysis", "App", "CSVG", …)
///     tags provenance for the tab's source column/filter; <see cref="Level" /> (framework
///     <see cref="LogLevel" />) drives the severity filter and the view's lvl-* class styles the
///     chip tint (v0.6.0, was a code-held brush trio, the third copy of the severity ramp).
/// </summary>
public sealed record TelemetryLogRow(
    string Source,
    LogLevel Level,
    string LevelLabel,
    string Category,
    string Message,
    string Time)
{
    /// <summary>Error/Critical row (AccentError chip).</summary>
    public bool IsSevError => Level is LogLevel.Critical or LogLevel.Error;

    /// <summary>Warning row (AccentAmber chip).</summary>
    public bool IsSevWarn => Level == LogLevel.Warning;

    /// <summary>Trace/Debug row (TextMid chip, the muted developer tiers).</summary>
    public bool IsSevDebug => Level is LogLevel.Trace or LogLevel.Debug;
}
