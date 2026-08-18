#region

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Diagnostics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Diagnostics;
using DemoViewer.NET.ViewModels.Analysis;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.Diagnostics;

/// <summary>
///     The Diagnostics tab VM. Surfaces always-on session/system troubleshooting
///     info plus per-layer profiling panels that self-gate on their own signal:
///     the parse + entity panels populate only when a run was profiled at RUNTIME
///     (<see cref="CS2DemoKit.Parser.Profiling.Enabled" />). The <b>Re-run with capture</b> button
///     turns the switch on and re-runs the analysis, populating the entity panel; the parse panel
///     (B1) needs a re-parse, so after a Re-run the user reloads the demo (or starts with
///     <c>DEMOVIEWER_PROFILE=1</c>). The evaluator-runtime panel ships in every build and is idle until
///     the user attaches the live capture toggle.
///     <para>
///         The VM only <i>reads</i> the demo / analysis state it is handed; it owns no demo, parser,
///         or scanner. It refreshes lazily on tab activation and after an evaluation completes — no
///         timer, no polling. The one resource it owns is the optional runtime listener, disposed
///         when the view detaches.
///     </para>
/// </summary>
public sealed partial class DiagnosticsTabViewModel : ObservableObject, IDisposable
{
    private const string EvaluatorMeterName = "CS2DemoKit.Analysis.Evaluator";

    // Opt-in capture buffers are bounded too: frame-duration samples (per-frame) and phase spans
    // are capped so an armed capture over a long run can't grow without limit.
    private const int RuntimeDurationSampleCap = 50000;
    private const int RuntimeSpanCap = 500;

    // ── CSVG traces + metrics capture (telemetry P3) ──────────────────────────
    // The gRPC + ASP.NET stack (and CSVG itself) emit via System.Diagnostics
    // Activity/Meter — BCL types keyed by STRING source/meter names, so these listeners live
    // App-side with NO ASP.NET/gRPC type crossing the seam (and stay dormant on WASM, where no
    // in-process host emits). The names below were verified with a one-shot mock-session probe.
    // Callbacks fire on arbitrary threads → lock-protected buffers, snapshotted on refresh (the
    // capture pattern above). Default OFF as well; a running CSVG session populates them once armed.

    private const string CsvgActivitySource = "Cs2VideoGenerator.Core";
    private const string AspNetActivitySource = "Microsoft.AspNetCore";

    private const int CsvgSpanBufferCap = 500;

    // Number of recent log lines attached to a copied report (bounded so the clipboard stays reasonable).
    private const int LogTailLines = 200;

    private static readonly string[] _csvgMeterNames =
    [
        "Cs2VideoGenerator.Core", // CSVG's own cs2.* instruments (grpc/demo/process/host)
        "Microsoft.AspNetCore.Hosting", // http.server.request.duration / active_requests
        "Microsoft.AspNetCore.Server.Kestrel"
    ];

    private readonly AnalysisTabViewModel _analysisTab;
    private readonly Dictionary<string, double> _csvgCounterTotals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _csvgGauges = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (long Count, double Sum, double Last)> _csvgHistograms =
        new(StringComparer.Ordinal);

    private readonly List<(string Op, double Ms)> _csvgSpans = [];

    private readonly object _csvgTelemetryGate = new();
    private readonly Func<IReadOnlyList<DemoFrame>?> _frames;
    private readonly Func<string?> _loadedDemoPath;
    private readonly Dictionary<string, long> _runtimeCounters = new(StringComparer.Ordinal);
    private readonly List<double> _runtimeFrameDurations = [];

    // Runtime listeners — null until the user toggles capture on; disposed on view unload.
    private readonly object _runtimeGate = new();
    private readonly List<(string Name, double Ms, int Depth)> _runtimeSpans = [];
    private readonly DiagnosticsTelemetryHub _telemetry;
    private ActivityListener? _activityListener;

    // ── A4. Copy fallback ─────────────────────────────────────────────────────

    /// <summary>The plain-text diagnostics block surfaced in the read-only fallback TextBox.</summary>
    [ObservableProperty]
    private string _copyFallbackText = string.Empty;

    private ActivityListener? _csvgActivityListener;
    private MeterListener? _csvgMeterListener;

    /// <summary>Aggregated CSVG + framework metric rows (counters/gauges/histograms).</summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _csvgMetricRows = [];

    /// <summary>Default-OFF capture toggle for the CSVG/ASP.NET Activity + Meter listeners.</summary>
    [ObservableProperty]
    private bool _csvgTelemetryCapturing;

    /// <summary>Status line under the P3 panel (capture state / reproduce hint).</summary>
    [ObservableProperty]
    private string _csvgTelemetryStatus = "Capture off. Toggle on, then reproduce your CSVG scenario.";

    /// <summary>Most-recent CSVG/ASP.NET spans (newest first) from a captured session.</summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _csvgTraceRows = [];

    /// <summary>Nested scanner + tracker timing rows (indented keys); carries a re-run hint when unprofiled.</summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _entityProfilingRows = [];

    // ── Entity profiling (runtime-gated: Profiling.Enabled at decode time) ────

    /// <summary>True when a profiled run captured entity-decode data.</summary>
    [ObservableProperty]
    private bool _entityProfilingVisible;

    /// <summary>Curated env-var allowlist rows.</summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _environmentRows = [];

    /// <summary>Evaluator counter rows; show "—" until a captured run has produced measurements.</summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _evaluatorCounterRows = [];

    /// <summary>Status line under the evaluator-runtime panel (capture state / re-run hint).</summary>
    [ObservableProperty]
    private string _evaluatorStatus = "Live capture off. Toggle on, then re-run to capture an evaluation.";

    /// <summary>Phase-timeline rows (indented spans) from a captured run.</summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _evaluatorTimelineRows = [];

    /// <summary>True once the hub has captured at least one row (drives the empty-state hint).</summary>
    [ObservableProperty]
    private bool _hasLogs;

    /// <summary>True while a captured re-run is in flight — disables the re-run button.</summary>
    [ObservableProperty]
    private bool _isReRunning;

    // ── Evaluator runtime (always rendered; idle until toggled) ───────────────

    /// <summary>Default-OFF live-capture toggle for the evaluator Meter/ActivitySource.</summary>
    [ObservableProperty]
    private bool _liveCaptureAttached;

    // ── Unified diagnostics log surface (internal ILogger pillar + CSVG host logs) ─

    /// <summary>Minimum severity shown in the log list — a floor: rows at/above it are kept.</summary>
    [ObservableProperty]
    private LogLevel _logFilter = LogLevel.Trace;

    /// <summary>Row-count label for the panel header (e.g. "128 shown / 1,204 captured").</summary>
    [ObservableProperty]
    private string _logSummary = "no logs captured yet";

    private MeterListener? _meterListener;

    /// <summary>Parse-pipeline timing rows; carries a reload hint when the last parse was unprofiled.</summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _parseProfilingRows = [];

    // ── B1. Parse profiling (runtime-gated: Profiling.Enabled at parse time) ──

    /// <summary>True when the last parse was profiled and captured data.</summary>
    [ObservableProperty]
    private bool _parseProfilingVisible;

    /// <summary>Session rows — demo path/size, rules dir, counts, map, source.</summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _sessionRows = [];

    /// <summary>True after a clipboard write failed (e.g. WASM permission) — reveals the manual-copy TextBox.</summary>
    [ObservableProperty]
    private bool _showCopyFallback;

    /// <summary>Provenance filter — "All" or one source tag ("Analysis" / "App" / "CSVG").</summary>
    [ObservableProperty]
    private string _sourceFilter = "All";

    // ── A. Always-on info ─────────────────────────────────────────────────────

    /// <summary>System / session rows — app + parser version, runtime, OS, GC.</summary>
    [ObservableProperty]
    private IReadOnlyList<KvpRow> _systemRows = [];

    /// <summary>Initializes a new <see cref="DiagnosticsTabViewModel" /> instance.</summary>
    /// <param name="analysisTab">
    ///     The analysis tab VM — read-only source of the evaluated demo, the
    ///     retained entity scanner, and the rules directory.
    /// </param>
    /// <param name="loadedDemoPath">Returns the loaded demo's full path, or <c>null</c>.</param>
    /// <param name="frames">Returns the parsed frame list for cheap counts, or <c>null</c>.</param>
    /// <param name="telemetry">
    ///     The app-lifetime unified diagnostics log hub the log panel binds
    ///     (internal ILogger pillar + CSVG host logs).
    /// </param>
    public DiagnosticsTabViewModel(
        AnalysisTabViewModel analysisTab,
        Func<string?> loadedDemoPath,
        Func<IReadOnlyList<DemoFrame>?> frames,
        DiagnosticsTelemetryHub telemetry)
    {
        _analysisTab = analysisTab;
        _loadedDemoPath = loadedDemoPath;
        _frames = frames;
        _telemetry = telemetry;
        _telemetry.RowsAppended += OnTelemetryRowsAppended;
        RebuildLogView();

        // Refresh the always-on + compile-gated rows whenever an evaluation finishes, so post-run
        // snapshots (the entity panel) are populated. AnalysisViewModel raises IsRunning=false at
        // the end of RunAsync.
        _analysisTab.Analysis.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AnalysisViewModel.IsRunning) && !_analysisTab.Analysis.IsRunning)
            {
                Refresh();
            }
        };

        Refresh();
    }

    /// <summary>True on desktop hosts — gates the <c>dotnet-trace</c>/<c>dotnet-counters</c> hint.</summary>
    public static bool IsDesktopHost => !OperatingSystem.IsBrowser();

    /// <summary>Severity floors offered by the filter (excludes <c>None</c> — that would hide all).</summary>
    public IReadOnlyList<LogLevel> LogFilterOptions { get; } =
    [
        LogLevel.Trace, LogLevel.Debug,
        LogLevel.Information, LogLevel.Warning,
        LogLevel.Error, LogLevel.Critical
    ];

    /// <summary>Source options offered by the provenance filter.</summary>
    public IReadOnlyList<string> SourceOptions { get; } = ["All", "Analysis", "App", "CSVG"];

    /// <summary>Filtered log rows the panel binds (mirrors the hub, severity + source filtered, bounded).</summary>
    public ObservableCollection<TelemetryLogRow> LogRows { get; } = [];

    /// <summary>Tears down the optional runtime listeners (CA1001) + the CSVG-hub subscription.</summary>
    public void Dispose()
    {
        DetachRuntimeListeners();
        DetachCsvgTelemetryListeners();
        _telemetry.RowsAppended -= OnTelemetryRowsAppended;
    }

    // ── Refresh ────────────────────────────────────────────────────────────────

    /// <summary>Rebuilds all always-on + compile-gated rows. Called on tab activation and after each run.</summary>
    public void Refresh()
    {
        SystemRows = RuntimeEnvInfo.SystemRows();
        EnvironmentRows = RuntimeEnvInfo.EnvRows();
        SessionRows = BuildSessionRows();
        RefreshParseProfiling();
        RefreshEntityProfiling();
        RebuildEvaluatorRows();
        RebuildCsvgTelemetryRows();
        // Avalonia caches CanExecute and only re-queries on CanExecuteChanged — so the re-run button
        // would stay disabled forever after a demo loads unless we poke it here. Refresh() fires on
        // tab activation and on each "evaluation completed" signal, both of which can flip LastEvaluatedDemo.
        ReRunCapturedCommand.NotifyCanExecuteChanged();
    }

    private List<KvpRow> BuildSessionRows()
    {
        List<KvpRow> rows = [];
        string? path = _loadedDemoPath();
        rows.Add(Row("demo path", path ?? "(no demo loaded)"));

        // Demo file size — desktop only (no filesystem path on the browser host).
        if (path is not null && !OperatingSystem.IsBrowser() && File.Exists(path))
        {
            long bytes = new FileInfo(path).Length;
            rows.Add(Row("demo size", $"{bytes / (1024.0 * 1024.0):F1} MiB"));
        }
        else if (path is not null && OperatingSystem.IsBrowser())
        {
            rows.Add(Row("demo size", "(n/a in browser)"));
        }

        rows.Add(Row("rules dir", AnalysisViewModel.RulesDirectory));

        IReadOnlyList<DemoFrame>? frames = _frames();
        rows.Add(Row("frames", frames is null ? "(no demo loaded)" : frames.Count.ToString("N0", CultureInfo.InvariantCulture)));

        ParsedDemo? demo = _analysisTab.Analysis.LastEvaluatedDemo;
        if (demo is not null)
        {
            rows.Add(Row("events", demo.AllGameEvents.Count.ToString("N0", CultureInfo.InvariantCulture)));
            // Q-4: map + source kind are cheap header fields on ParsedDemo — include them.
            if (!string.IsNullOrEmpty(demo.MapName))
            {
                rows.Add(Row("map", demo.MapName));
            }

            rows.Add(Row("source kind", demo.Profile.SourceKind.ToString()));
        }

        return rows;
    }

    private void RefreshParseProfiling()
    {
        ParseProfilingSnapshot snap = ParseProfilingSnapshot.Read();
        // The panel is always visible now (it ships in every build); when the last parse was unprofiled it
        // shows a hint. Parse data can ONLY come from a load done with profiling already on — the tab's
        // Re-run reuses the retained ParsedDemo and does NOT re-parse — so the hint says "reload", not
        // "re-run" (re-run only repopulates the entity panel, which is driven during evaluation).
        ParseProfilingVisible = true;
        if (!snap.Enabled)
        {
            ParseProfilingRows =
            [
                Row("parse profiling", "no data captured — Re-run with capture turns profiling on; then RELOAD the demo to profile the parse pass")
            ];
            return;
        }

        ParseProfilingRows =
        [
            // ParseProfiler now resets per-parse (the snapshot's Enabled reflects the parse that produced
            // the data), so these are this load's figures, not a process-since-start accumulation.
            Row("pass 1 (header scan)", $"{Ms(snap.Pass1HeaderTicks)} · {Mib(snap.Pass1Alloc)}"),
            Row("pass 2 (parallel decode)", $"{Ms(snap.Pass2WallTicks)} · alloc: n/a (parallel)"),
            Row("pass 3 (enrich)", $"{Ms(snap.Pass3EnrichTicks)} · {Mib(snap.Pass3Alloc)}"),
            Row("frames", snap.FrameCount.ToString("N0", CultureInfo.InvariantCulture)),
            Row("compressed frames", snap.CompressedFrames.ToString("N0", CultureInfo.InvariantCulture))
        ];
    }

    private void RefreshEntityProfiling()
    {
        EntityChangeScanner? scanner = _analysisTab.Analysis.EntityScanner;
        if (scanner is null)
        {
            EntityProfilingVisible = false;
            EntityProfilingRows = [];
            return;
        }

        ScannerProfilingSnapshot s = scanner.GetProfilingSnapshot();
        EntityProfilingSnapshot t = scanner.Layer.Tracker.GetProfilingSnapshot();

        // Both snapshots' Enabled now reflect whether a profiled run captured their data (runtime, via
        // Profiling.Enabled). The panel is always visible; when nothing was captured it shows a re-run hint.
        // Note: under the Track-4 parallel precompute path the scanner's OWN tracker is never driven (the
        // throwaway worker trackers do the decode), so t.Enabled may be false even when s.Enabled is true —
        // the tracker sub-tree then simply doesn't render; the scanner panel (precompute/seek) still does.
        EntityProfilingVisible = true;
        if (!s.Enabled && !t.Enabled)
        {
            EntityProfilingRows =
            [
                Row("entity profiling", "no data captured — hit \"Re-run with capture\" below to profile this demo")
            ];
            return;
        }

        List<KvpRow> rows = [];

        if (s.Enabled)
        {
            rows.Add(Row("scanner", string.Empty));
            rows.Add(Indent("precompute (parallel)", $"{Ms(s.PrecomputeTicks)} · {Mib(s.PrecomputeAlloc)}", 1));
            rows.Add(Indent("seek", $"{Ms(s.SeekTicks)} · {Mib(s.SeekAlloc)}", 1));
            rows.Add(Indent("snapshot", $"{Ms(s.SnapshotTicks)} · {Mib(s.SnapshotAlloc)}", 1));
            rows.Add(Indent("frames polled", s.FramesPolled.ToString("N0", CultureInfo.InvariantCulture), 1));
        }

        if (t.Enabled)
        {
            // Tracker decode nests under the scanner's seek; report the unattributed remainder explicitly.
            long children = t.FieldPathTicks + t.FieldValueTicks + t.DescriptorBuildTicks;
            long remainder = t.PacketEntitiesTicks - children;
            rows.Add(Indent("tracker.PacketEntities", $"{Ms(t.PacketEntitiesTicks)} · {Mib(t.PacketEntitiesAlloc)}", 1));
            rows.Add(Indent("fieldPath", $"{Ms(t.FieldPathTicks)} · {Mib(t.FieldPathAlloc)}", 2));
            rows.Add(Indent("fieldValue", $"{Ms(t.FieldValueTicks)} · {Mib(t.FieldValueAlloc)}", 2));
            rows.Add(Indent("descriptorBuild", $"{Ms(t.DescriptorBuildTicks)} · {Mib(t.DescriptorBuildAlloc)}", 2));
            rows.Add(Indent("(unattributed)", Ms(remainder), 2));
            rows.Add(Indent("packetEntities count", t.PacketEntitiesCount.ToString("N0", CultureInfo.InvariantCulture), 2));
            rows.Add(Indent("entity field reads", t.EntityFieldReads.ToString("N0", CultureInfo.InvariantCulture), 2));
            rows.Add(Indent("descriptor builds", t.DescriptorBuilds.ToString("N0", CultureInfo.InvariantCulture), 2));
        }

        EntityProfilingRows = rows;
    }

    // ── Runtime listener (pattern from ProfilingSession) ──────────────────────

    partial void OnLiveCaptureAttachedChanged(bool value)
    {
        if (value)
        {
            AttachRuntimeListeners();
            EvaluatorStatus = "Live capture armed — re-run the analysis to populate counters.";
        }
        else
        {
            DetachRuntimeListeners();
            EvaluatorStatus = "Live capture off. Toggle on, then re-run to capture an evaluation.";
        }
    }

    private void AttachRuntimeListeners()
    {
        if (_meterListener is not null)
        {
            return;
        }

        lock (_runtimeGate)
        {
            _runtimeCounters.Clear();
            _runtimeFrameDurations.Clear();
            _runtimeSpans.Clear();
        }

        MeterListener meter = new()
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == EvaluatorMeterName)
                {
                    l.EnableMeasurementEvents(inst);
                }
            }
        };
        meter.SetMeasurementEventCallback<long>((inst, measurement, _, _) =>
        {
            lock (_runtimeGate)
            {
                _runtimeCounters[inst.Name] = _runtimeCounters.GetValueOrDefault(inst.Name) + measurement;
            }
        });
        meter.SetMeasurementEventCallback<double>((inst, measurement, _, _) =>
        {
            lock (_runtimeGate)
            {
                // Bounded: keep an early representative window rather than growing per-frame forever.
                if (_runtimeFrameDurations.Count < RuntimeDurationSampleCap)
                {
                    _runtimeFrameDurations.Add(measurement);
                }
            }
        });
        meter.Start();
        _meterListener = meter;

        _activityListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == AnalysisDiagnostics.SourceName,
            Sample = static (ref options) => ActivitySamplingResult.AllData,
            ActivityStopped = OnSpanStopped
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    private void OnSpanStopped(Activity a)
    {
        int depth = 0;
        for (Activity? p = a.Parent; p is not null; p = p.Parent)
        {
            depth++;
        }

        lock (_runtimeGate)
        {
            if (_runtimeSpans.Count >= RuntimeSpanCap)
            {
                _runtimeSpans.RemoveAt(0);
            }

            _runtimeSpans.Add((a.OperationName, a.Duration.TotalMilliseconds, depth));
        }
    }

    /// <summary>
    ///     Disposes the runtime listeners. Called by the view on detach (the TabControl
    ///     unloads inactive tab content) and on toggle-off. Also clears <see cref="LiveCaptureAttached" />
    ///     so the toggle bool can never outlive the listener: a tab-switch tears the listener down, and
    ///     the toggle must report OFF afterward or a later Re-run would think capture was armed when it
    ///     was not (the silent-no-op trap).
    /// </summary>
    public void DetachRuntimeListeners()
    {
        _meterListener?.Dispose();
        _meterListener = null;
        _activityListener?.Dispose();
        _activityListener = null;
        // Setting this false re-enters OnLiveCaptureAttachedChanged(false), which calls back into here;
        // by then both listeners are already null so the second pass is a cheap no-op.
        LiveCaptureAttached = false;
    }

    private bool Passes(TelemetryLogRow row) =>
        row.Level >= LogFilter && (SourceFilter == "All" || row.Source == SourceFilter);

    private void OnTelemetryRowsAppended(IReadOnlyList<TelemetryLogRow> batch)
    {
        // Fired on the UI thread (hub drains / appends there), so touching the bound collection is safe.
        // Batched: one pass per drain, not one marshal per row — keeps a high-rate producer cheap.
        foreach (TelemetryLogRow row in batch)
        {
            if (Passes(row))
            {
                LogRows.Add(row);
            }
        }

        TrimView();
        UpdateLogSummary();
    }

    partial void OnLogFilterChanged(LogLevel value) => RebuildLogView();

    partial void OnSourceFilterChanged(string value) => RebuildLogView();

    private void RebuildLogView()
    {
        LogRows.Clear();
        foreach (TelemetryLogRow row in _telemetry.Logs)
        {
            if (Passes(row))
            {
                LogRows.Add(row);
            }
        }

        TrimView();
        UpdateLogSummary();
    }

    // The filtered view is a subset of the bounded hub, so it can never legitimately exceed the hub's
    // current size — trim any stragglers left from rows the hub has since dropped from its ring. This is
    // what keeps the view bounded (the prior CSVG view grew unbounded because it never dropped).
    private void TrimView()
    {
        int cap = _telemetry.Logs.Count;
        while (LogRows.Count > cap)
        {
            LogRows.RemoveAt(0);
        }
    }

    private void UpdateLogSummary()
    {
        int total = _telemetry.Logs.Count;
        HasLogs = total > 0;
        LogSummary = total == 0
            ? "no logs captured yet — internal logs appear on load/analysis; CSVG logs when Live Sync runs"
            : $"{LogRows.Count.ToString("N0", CultureInfo.InvariantCulture)} shown / " +
              $"{total.ToString("N0", CultureInfo.InvariantCulture)} captured";
    }

    /// <summary>Clears the captured diagnostics logs (hub + this view).</summary>
    [RelayCommand]
    private void ClearLogs()
    {
        _telemetry.Clear();
        LogRows.Clear();
        UpdateLogSummary();
    }

    partial void OnCsvgTelemetryCapturingChanged(bool value)
    {
        if (value)
        {
            AttachCsvgTelemetryListeners();
            CsvgTelemetryStatus = "Capturing CSVG traces + metrics — reproduce, then Refresh.";
        }
        else
        {
            DetachCsvgTelemetryListeners();
            CsvgTelemetryStatus = "Capture off. Toggle on, then reproduce your CSVG scenario.";
        }
    }

    private void AttachCsvgTelemetryListeners()
    {
        if (_csvgMeterListener is not null)
        {
            return;
        }

        lock (_csvgTelemetryGate)
        {
            _csvgCounterTotals.Clear();
            _csvgGauges.Clear();
            _csvgHistograms.Clear();
            _csvgSpans.Clear();
        }

        MeterListener meter = new()
        {
            InstrumentPublished = (inst, l) =>
            {
                if (Array.IndexOf(_csvgMeterNames, inst.Meter.Name) >= 0)
                {
                    l.EnableMeasurementEvents(inst);
                }
            }
        };
        meter.SetMeasurementEventCallback<double>((inst, m, _, _) => RecordCsvgMeasurement(inst, m));
        meter.SetMeasurementEventCallback<long>((inst, m, _, _) => RecordCsvgMeasurement(inst, m));
        meter.SetMeasurementEventCallback<int>((inst, m, _, _) => RecordCsvgMeasurement(inst, m));
        meter.Start();
        _csvgMeterListener = meter;

        _csvgActivityListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name is CsvgActivitySource or AspNetActivitySource,
            Sample = static (ref _) => ActivitySamplingResult.AllData,
            ActivityStopped = OnCsvgSpanStopped
        };
        ActivitySource.AddActivityListener(_csvgActivityListener);
    }

    private void DetachCsvgTelemetryListeners()
    {
        _csvgMeterListener?.Dispose();
        _csvgMeterListener = null;
        _csvgActivityListener?.Dispose();
        _csvgActivityListener = null;
        CsvgTelemetryCapturing = false;
    }

    // Classify by instrument type: histograms track n/sum/last; observable instruments report their
    // current cumulative value (store latest, never sum — that would double-count each poll);
    // plain Counter/UpDownCounter measurements are deltas we accumulate.
    private void RecordCsvgMeasurement(Instrument inst, double value)
    {
        string typeName = inst.GetType().Name;
        lock (_csvgTelemetryGate)
        {
            if (typeName.StartsWith("Histogram", StringComparison.Ordinal))
            {
                (long count, double sum, double _) = _csvgHistograms.GetValueOrDefault(inst.Name);
                _csvgHistograms[inst.Name] = (count + 1, sum + value, value);
            }
            else if (typeName.StartsWith("Observable", StringComparison.Ordinal))
            {
                _csvgGauges[inst.Name] = value;
            }
            else
            {
                _csvgCounterTotals[inst.Name] = _csvgCounterTotals.GetValueOrDefault(inst.Name) + value;
            }
        }
    }

    private void OnCsvgSpanStopped(Activity a)
    {
        lock (_csvgTelemetryGate)
        {
            _csvgSpans.Add((a.OperationName, a.Duration.TotalMilliseconds));
            if (_csvgSpans.Count > CsvgSpanBufferCap)
            {
                _csvgSpans.RemoveRange(0, _csvgSpans.Count - CsvgSpanBufferCap);
            }
        }
    }

    /// <summary>Snapshots the CSVG capture buffers into the bound rows (Refresh button + tab activation).</summary>
    [RelayCommand]
    private void RefreshCsvgTelemetry() => RebuildCsvgTelemetryRows();

    private void RebuildCsvgTelemetryRows()
    {
        // Pull the latest values from observable (polled) instruments before snapshotting.
        _csvgMeterListener?.RecordObservableInstruments();

        Dictionary<string, double> counters;
        Dictionary<string, double> gauges;
        Dictionary<string, (long Count, double Sum, double Last)> histos;
        List<(string Op, double Ms)> spans;
        lock (_csvgTelemetryGate)
        {
            counters = new Dictionary<string, double>(_csvgCounterTotals, StringComparer.Ordinal);
            gauges = new Dictionary<string, double>(_csvgGauges, StringComparer.Ordinal);
            histos = new Dictionary<string, (long, double, double)>(_csvgHistograms, StringComparer.Ordinal);
            spans = [.. _csvgSpans];
        }

        List<KvpRow> metrics = [];
        foreach (string name in counters.Keys.Concat(gauges.Keys).Concat(histos.Keys)
                     .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (histos.TryGetValue(name, out (long Count, double Sum, double Last) h))
            {
                double mean = h.Count == 0 ? 0 : h.Sum / h.Count;
                metrics.Add(Row(name,
                    $"n={h.Count.ToString("N0", CultureInfo.InvariantCulture)} · mean {mean.ToString("F2", CultureInfo.InvariantCulture)} · last {h.Last.ToString("F2", CultureInfo.InvariantCulture)}"));
            }
            else if (gauges.TryGetValue(name, out double g))
            {
                metrics.Add(Row(name, g.ToString("0.###", CultureInfo.InvariantCulture)));
            }
            else
            {
                metrics.Add(Row(name, counters[name].ToString("0.###", CultureInfo.InvariantCulture)));
            }
        }

        CsvgMetricRows = metrics.Count == 0
            ? [Row("metrics", CsvgTelemetryCapturing ? "(no measurements captured yet)" : "—")]
            : metrics;

        // Newest spans first, capped for display.
        List<KvpRow> traces = [];
        for (int i = spans.Count - 1; i >= 0 && traces.Count < 60; i--)
        {
            traces.Add(Row(spans[i].Op, $"{spans[i].Ms.ToString("F1", CultureInfo.InvariantCulture)} ms"));
        }

        CsvgTraceRows = traces.Count == 0
            ? [Row("spans", CsvgTelemetryCapturing ? "(no spans captured yet)" : "—")]
            : traces;
    }

    private void RebuildEvaluatorRows()
    {
        Dictionary<string, long> counters;
        List<double> durations;
        List<(string Name, double Ms, int Depth)> spans;
        lock (_runtimeGate)
        {
            counters = new Dictionary<string, long>(_runtimeCounters, StringComparer.Ordinal);
            durations = [.. _runtimeFrameDurations];
            spans = [.. _runtimeSpans];
        }

        EvaluatorCounterRows =
        [
            CounterRow("messages.processed", counters, "analysis.messages.processed"),
            CounterRow("edges.evaluated", counters, "analysis.edges.evaluated"),
            CounterRow("edges.fired", counters, "analysis.edges.fired"),
            CounterRow("logic_nodes.recomputed", counters, "analysis.logic_nodes.recomputed"),
            CounterRow("players.materialized", counters, "analysis.players.materialized"),
            BuildDurationRow(durations)
        ];

        EvaluatorTimelineRows = spans.Count == 0
            ? [Row("timeline", LiveCaptureAttached ? "(no captured run yet)" : "—")]
            : [.. spans.Select(sp => Indent(sp.Name, $"{sp.Ms:F1} ms", sp.Depth))];
    }

    private static KvpRow CounterRow(string label, Dictionary<string, long> counters, string key) =>
        counters.TryGetValue(key, out long v)
            ? Row(label, v.ToString("N0", CultureInfo.InvariantCulture))
            : Row(label, "—");

    private static KvpRow BuildDurationRow(List<double> durations)
    {
        if (durations.Count == 0)
        {
            return Row("frame.duration_ms", "—");
        }

        double[] sorted = durations.OrderBy(d => d).ToArray();
        double mean = sorted.Average();
        double p95 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))];
        return Row("frame.duration_ms", $"n={sorted.Length:N0} · mean {mean:F3} · p95 {p95:F3} ms");
    }

    // ── Re-run with capture ───────────────────────────────────────────────────

    /// <summary>
    ///     The only coherent in-app capture path (listener-lifetime constraint): attaches
    ///     the listener (if needed) while the Diagnostics tab is alive, then re-invokes the public
    ///     <c>AnalysisViewModel.RunAsync</c> on the retained <see cref="ParsedDemo" /> so the run fires
    ///     with live capture in place.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReRun))]
    private async Task ReRunCapturedAsync()
    {
        ParsedDemo? demo = _analysisTab.Analysis.LastEvaluatedDemo;
        if (demo is null)
        {
            EvaluatorStatus = "No evaluated demo retained — load a demo first.";
            return;
        }

        // Guarantee a LIVE listener for this run — gate on the actual resource, not the toggle bool,
        // which could read ON from a stale state. AttachRuntimeListeners also clears prior capture.
        if (_meterListener is null)
        {
            AttachRuntimeListeners();
            LiveCaptureAttached = true;
        }
        else
        {
            // Already armed: clear prior capture so the panel reflects only this run.
            lock (_runtimeGate)
            {
                _runtimeCounters.Clear();
                _runtimeFrameDurations.Clear();
                _runtimeSpans.Clear();
            }
        }

        // Turn on the single runtime profiling switch BEFORE the run so the entity-decode accumulators
        // latch and populate this pass — the evaluator counters come from the listener, but the
        // parse/entity profile trees come from Profiling.Enabled. Set before RunAsync (which drives the
        // scanner / parallel precompute) so the set-before-run contract holds.
        Profiling.Enabled = true;

        IsReRunning = true;
        EvaluatorStatus = "Re-running analysis with profiling + live capture…";
        try
        {
            await _analysisTab.Analysis.RunAsync(demo);
            RebuildEvaluatorRows();
            RefreshEntityProfiling();
            EvaluatorStatus = "Captured. Counters + entity profile reflect the re-run.";
        }
        finally
        {
            IsReRunning = false;
            ReRunCapturedCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanReRun() => !IsReRunning && _analysisTab.Analysis.LastEvaluatedDemo is not null;

    partial void OnIsReRunningChanged(bool value) => ReRunCapturedCommand.NotifyCanExecuteChanged();

    // ── Copy-to-clipboard text builder ────────────────────────────────────────

    /// <summary>Builds the plain-text diagnostics block (A1–A3) for the clipboard / fallback TextBox.</summary>
    public string BuildCopyText()
    {
        StringBuilder sb = new();
        string app = SystemRows.FirstOrDefault(r => r.Key == "app version")?.Value ?? "?";
        string parser = SystemRows.FirstOrDefault(r => r.Key == "parser version")?.Value ?? "?";
        sb.AppendLine(CultureInfo.InvariantCulture, $"DemoViewer.NET diagnostics — app {app} · parser {parser}");
        sb.AppendLine();
        AppendSection(sb, "System", SystemRows);
        AppendSection(sb, "Environment", EnvironmentRows);
        AppendSection(sb, "Session", SessionRows);
        AppendRecentLogs(sb);
        return sb.ToString();
    }

    // Attaches recent diagnostics logs to the copied report so a user-reported issue carries its
    // lead-up. Prefers the rolling file (more history + full dates); falls back to the in-memory hub
    // rows when there's no file (WASM, or file logging disabled).
    private void AppendRecentLogs(StringBuilder sb)
    {
        sb.AppendLine("[Recent logs]");

        IReadOnlyList<string> files = AppPaths.LatestLogFiles();
        // ReadTail opens the file share-read-WRITE, so it works even while the sink holds the active file
        // open (the common case). It can still come back empty (unreadable, or all history is in memory);
        // fall through to the hub tail in that case so a copied report is never left with just paths.
        List<string> tail = files.Count > 0
            ? DiagnosticsFileLog.ReadTail(files[0], LogTailLines)
            : [];

        if (files.Count > 0 && tail.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  file: {files[0]}");
            for (int i = 1; i < files.Count; i++)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  (rolled: {files[i]})");
            }

            sb.AppendLine();
            foreach (string line in tail)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {line}");
            }
        }
        else
        {
            // No readable file (WASM, file logging off, or a read that came back empty) — dump the tail
            // of the in-memory hub instead. Note the file path if there was one, for context.
            if (files.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  file: {files[0]} (in-memory tail)");
            }

            int total = _telemetry.Logs.Count;
            int take = Math.Min(total, LogTailLines);
            for (int i = total - take; i < total; i++)
            {
                TelemetryLogRow r = _telemetry.Logs[i];
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  {r.Time} {r.LevelLabel,-5} {r.Source}/{r.Category}: {r.Message}");
            }

            if (total == 0)
            {
                sb.AppendLine("  (no logs captured)");
            }
        }

        sb.AppendLine();
    }

    /// <summary>Records that the clipboard write failed and exposes the manual-copy fallback TextBox.</summary>
    public void ShowClipboardFallback(string text)
    {
        CopyFallbackText = text;
        ShowCopyFallback = true;
    }

    private static void AppendSection(StringBuilder sb, string title, IReadOnlyList<KvpRow> rows)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"[{title}]");
        foreach (KvpRow r in rows)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {r.Key}: {r.Value}");
        }

        sb.AppendLine();
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static KvpRow Row(string key, string value) => new(key, value, false, null);

    private static KvpRow Indent(string key, string value, int depth) =>
        new(new string(' ', depth * 4) + key, value, false, null);

    private static string Ms(long ticks) =>
        $"{Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds:F1} ms";

    private static string Mib(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MiB";
}
