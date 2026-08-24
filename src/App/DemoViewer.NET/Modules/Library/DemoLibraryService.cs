#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Configuration;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>
///     Scans user-configured folders for <c>*.dem</c> files and indexes their metadata in two tiers:
///     <b>tier 1</b> is the cheap first-frame header read (map / server — near-instant, via
///     <see cref="DownstreamUtilities.TryReadQuickInfo(string,out DownstreamUtilities.DemoQuickInfo)" />);
///     <b>
///         tier
///         2
///     </b>
///     is a background <b>full parse</b> for players + duration (the only place those live — not in the
///     header, not in the .dem.info companion). Results are cached to disk keyed on (path, size, mtime) so
///     relaunches are instant and only new/changed files are re-indexed.
///     <para>
///         <b>Threading.</b> Enumeration + parsing run on background threads; every mutation of the bound
///         <see cref="Entries" />/<see cref="Folders" /> collections and of a <see cref="DemoEntry" />'s
///         observable fields is marshalled through the injected <c>post</c> delegate (the UI dispatcher in the
///         app; an inline invoker in tests). Full parses run <b>sequentially</b> — one demo at a time, under
///         the machine-wide gate when present — because a full parse holds the whole demo in RAM and this
///         project has a documented parser-parallelism OOM history.
///     </para>
///     <para>
///         <b>Persistence</b> mirrors <c>SessionStore</c>: best-effort JSON at
///         <c>%AppData%/DemoViewer.NET/library.json</c>, no-op on WASM (no filesystem).
///     </para>
/// </summary>
public sealed class DemoLibraryService : IDisposable, IDemoEvaluator
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    // The only class the final-score replay reads — passed as EntityTracker.StoreClassFilter so every
    // other class is decoded-and-discarded (bits consumed, fields not stored) for a cheaper replay.
    private static readonly IReadOnlySet<string> _scoreClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "CCSTeam"
    };

    // Fan-out skip set for this evaluator's own tier-2 hand-off: when the Library slot hands its held
    // parse to the OTHER evaluators (replacing the old Tier2DemoParsed piggyback), Library is the
    // producer and must not be re-fed its own parse.
    private static readonly IReadOnlySet<string> _fanOutSkipSelf =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "library"
        };

    private readonly Dictionary<string, DemoLibraryCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private readonly string? _dataPath; // library.json, or null on WASM

    // Diagnostics-pillar logger (v0.6.0 — replaced Console.WriteLine, which a windowed Release build
    // never shows). Lazy like MainViewModel.DiagLog: the ambient factory is wired after construction.
    private ILogger? _diagLog;
    private ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger(AppLog.LibraryCategory);

    // The unified demo cache, dual-written alongside library.json during the transition. Null → legacy only.
    private readonly DemoCacheStore? _demoCache;
    private readonly Dictionary<string, DemoEntry> _pendingFull = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<Action> _post; // marshal to the UI thread (Dispatcher in-app; inline in tests)

    // When injected, AppSettings.Library.Folders is the folder source-of-truth (read on
    // construction, written on Add/Remove). Null → the legacy path where library.json owns the folder list.
    // The metadata cache stays in library.json either way.

    // Paths with a tier-2 replay in flight RIGHT NOW — dedupes the two RunTier2 callers (the queue's
    // Evaluate and an interactive open's OnParsedOpportunistically) when they race the same demo: a second
    // concurrent replay would be wasted work and could flip Indexed↔Failed on a throw.
    private readonly HashSet<string> _tier2InProgress = new(StringComparer.OrdinalIgnoreCase);

    // Tier-2 backlog: _pendingFull is the working set of demos still needing a tier-2 parse (recomputed
    // each rescan; the coordinator's Wants(path) reads its membership). The coordinator's own outstanding
    // set prevents double-submit, so no separate _enqueued set is needed here.
    private readonly object _tier2Lock = new();
    private int _enrichedSinceSave;

    // A plain array mirror of Folders, refreshed on the (UI) thread that mutates the UI-bound
    // ObservableCollection. Save() runs on a queue worker thread and reads THIS — never enumerating the
    // live Folders across a concurrent Add/Remove (which would throw "collection was modified").
    private volatile string[] _folderSnapshot = [];

    // Registered roots that were actually reachable during the last enumeration. Read by the stale-row
    // prune, which must never treat "this folder was unavailable" as "these demos were deleted".
    private volatile string[] _lastScannedRoots = [];

    private CancellationTokenSource? _scanCts;

    /// <param name="post">
    ///     Marshals an action onto the UI thread. Defaults to <c>Dispatcher.UIThread.Post</c> in the app;
    ///     tests pass an inline invoker for deterministic, single-threaded assertions.
    /// </param>
    /// <param name="dataPathOverride">
    ///     Test seam: overrides the persisted-library JSON path (keeps tests out
    ///     of the real <c>%AppData%</c>). Null → the default AppData path (or no persistence on WASM).
    /// </param>
    /// <param name="settings">
    ///     When supplied, the configured folder list is read from
    ///     <c>AppSettings.Library.Folders</c> and Add/Remove write it back through
    ///     <see cref="SettingsService.Write" />. Null → the legacy path where library.json owns the folders.
    /// </param>
    /// <param name="demoCache">
    ///     The unified demo cache. When supplied, tier-2
    ///     results are written HERE AS WELL AS to <c>library.json</c> — a deliberate dual write for the
    ///     transition, so the cache the app runs on today is never at risk while the new one fills. Null in
    ///     tests and on the legacy path.
    /// </param>
    public DemoLibraryService(Action<Action>? post = null, string? dataPathOverride = null,
        SettingsService? settings = null, DemoCacheStore? demoCache = null)
    {
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        SettingsBacking = settings;
        _demoCache = demoCache;

        if (dataPathOverride is not null)
        {
            _dataPath = dataPathOverride;
        }
        else if (!OperatingSystem.IsBrowser())
        {
            _dataPath = AppPaths.LibraryCacheFile;
        }

        // LoadPersisted restores the metadata cache and returns the folder list stored in library.json.
        // SeedFolders then chooses the authoritative folder source (settings when injected, else library.json).
        List<string> legacyFolders = LoadPersisted();
        SeedFolders(legacyFolders);

        // Keep the worker-readable snapshot in step with Folders, updated on the mutating thread.
        _folderSnapshot = Folders.ToArray();
        Folders.CollectionChanged += (_, _) => _folderSnapshot = Folders.ToArray();
    }

    /// <summary>
    ///     The settings service this indexer is folder-backed by, or <c>null</c> on the legacy path.
    ///     Exposed for the composition-root test to assert the container injected the SINGLETON instance.
    /// </summary>
    internal SettingsService? SettingsBacking { get; }

    // The "one parse, many evaluators" coordinator. When set, this service
    // is registered as an IDemoEvaluator and the coordinator owns submission + the CapacityAvailable
    // re-feed — this service NEVER touches the queue directly. Null (tests) → the inline one-at-a-time
    // path, behaviourally identical to the pre-queue null-gate path.
    /// <summary>The evaluation coordinator that drives this service's tier-2 work; null → the inline path.</summary>
    public DemoEvaluationCoordinator? Coordinator { get; set; }

    /// <summary>The configured root folders (recursively scanned). Bound to the UI; mutate via the public methods.</summary>
    public ObservableCollection<string> Folders { get; } = [];

    /// <summary>All discovered demos with their (progressively enriched) metadata. Bound to the browser.</summary>
    public BulkObservableCollection<DemoEntry> Entries { get; } = [];

    // ── IDemoEvaluator ("one parse, many evaluators") ──

    /// <inheritdoc />
    public string Id => "library";

    /// <inheritdoc />
    /// <remarks>
    ///     Cheap membership test against the tier-2 backlog recorded at reconcile. The coordinator
    ///     re-polls this on every CapacityAvailable, so it MUST go false once processed — which
    ///     <see cref="ClearTier2Backlog" /> guarantees synchronously in both Evaluate and OnFailed.
    /// </remarks>
    public bool Wants(string path)
    {
        lock (_tier2Lock)
        {
            return _pendingFull.ContainsKey(path);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Runs on a queue worker thread with the parse still held (the memory-safety window). Library
    ///     is the SOLE producer of this parse, so it fans the held demo out to the OTHER evaluators (the
    ///     Highlights piggyback replacement). The backlog is cleared SYNCHRONOUSLY here (not via the posted
    ///     UI callback) so a CapacityAvailable re-poll that races this can't see the path as still-wanted and
    ///     double-submit it.
    /// </remarks>
    public void Evaluate(string path, ParsedDemo parsed) => RunTier2(path, parsed, true);

    /// <inheritdoc />
    /// <remarks>
    ///     An interactive open (or another evaluator's tier-2) already parsed this demo — index the
    ///     Library card from THAT held parse instead of a second background parse. Guarded on tier-2 backlog
    ///     membership: a demo that isn't a known-pending library entry (opened from outside every registered
    ///     folder, or already indexed) is a no-op — we never inject a foreign demo into the library. When it
    ///     IS pending this indexes it AND clears the backlog synchronously, so a subsequent coordinator
    ///     re-poll sees <see cref="Wants" /> == false and never submits the redundant background parse.
    ///     <para>
    ///         Unlike <see cref="Evaluate" /> this does NOT re-fan the parse to the other evaluators: the
    ///         coordinator's <see cref="DemoEvaluationCoordinator.FanOutParsed" /> that invoked this already
    ///         decides the full fan-out set (and its skip list), so re-fanning here would double-feed them (e.g.
    ///         a redundant Highlights re-analysis on open, on top of its own open-demo harvest).
    ///     </para>
    /// </remarks>
    public void OnParsedOpportunistically(string path, ParsedDemo parsed) =>
        RunTier2(path, parsed, false);

    /// <inheritdoc />
    public void OnFailed(string path)
    {
        DemoEntry? entry;
        lock (_tier2Lock)
        {
            _pendingFull.TryGetValue(path, out entry);
        }

        try
        {
            if (entry is not null)
            {
                _post(() => entry.State = DemoIndexState.Failed);
                UpsertCache(path, c => c.FullyIndexed = false);
            }
        }
        finally
        {
            // MUST clear even on failure, else Wants stays true → the coordinator re-submits a corrupt
            // demo on every CapacityAvailable, monopolizing the one worker (the Phase-2 infinite-loop trap).
            ClearTier2Backlog(path);
        }
    }

    /// <inheritdoc />
    public DemoJobPriority PriorityFor(string path) => DemoJobPriority.Background;

    /// <inheritdoc />
    public long OrderHint(string path)
    {
        lock (_tier2Lock)
        {
            return _pendingFull.TryGetValue(path, out DemoEntry? e) ? e.Modified.Ticks : 0;
        }
    }

    /// <summary>Cancels any in-flight scan and releases the cancellation source.</summary>
    public void Dispose()
    {
        // The coordinator owns the CapacityAvailable subscription now — nothing queue-side to detach here.
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
    }

    /// <summary>Raised (on the post thread) whenever a scan finishes a phase, so the VM can refresh filters.</summary>
    public event Action? Changed;

    // Populates Folders from the authoritative source. Legacy path (no settings): library.json's folders,
    // identical to the previous behavior. Settings path: AppSettings.Library.Folders — with a one-time
    // migration that lifts an existing library.json folder list into settings.json when settings has none
    // yet, so an upgrading install does not silently lose its configured folders.
    private void SeedFolders(List<string> legacyFolders)
    {
        if (SettingsBacking is null)
        {
            foreach (string f in legacyFolders)
            {
                Folders.Add(f);
            }

            return;
        }

        string[] fromSettings = SettingsBacking.Current.Library.Folders;
        if (fromSettings.Length == 0 && legacyFolders.Count > 0)
        {
            foreach (string f in legacyFolders)
            {
                if (!Folders.Contains(f))
                {
                    Folders.Add(f);
                }
            }

            SettingsBacking.Write(s => s.Library.Folders = Folders.ToArray());
        }
        else
        {
            foreach (string f in fromSettings)
            {
                if (!Folders.Contains(f))
                {
                    Folders.Add(f);
                }
            }
        }
    }

    // Persists the folder list after an Add/Remove. When settings-backed, AppSettings.Library.Folders is
    // authoritative (written via SettingsService, which reloads + fires OnChange); the cache is always
    // saved to library.json. Without settings this is exactly the legacy Save() (folders + cache to disk).
    private void PersistFolders()
    {
        SettingsBacking?.Write(s => s.Library.Folders = Folders.ToArray());
        Save();
    }

    /// <summary>Adds folders (ignoring duplicates + non-existent paths), persists, and kicks a rescan.</summary>
    public async Task AddFoldersAsync(IEnumerable<string> paths)
    {
        bool added = false;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || Folders.Contains(path))
            {
                continue;
            }

            Folders.Add(path);
            added = true;
        }

        if (added)
        {
            PersistFolders();
            await RescanAsync();
        }
    }

    /// <summary>Removes a folder, drops its demos from the view, persists, and rescans the rest.</summary>
    public async Task RemoveFolderAsync(string path)
    {
        if (Folders.Remove(path))
        {
            PersistFolders();
            await RescanAsync();
        }
    }

    /// <summary>
    ///     How many demos IN THE LIBRARY are waiting on a score re-derivation.
    ///     <para>
    ///         Counted over <see cref="Entries" />, not over the cache, and the difference is not pedantic: on
    ///         the reference library 552 rows were repairable but only 342 had a file still on disk — the rest
    ///         described demos under a folder the user had removed, which nothing can ever re-derive. A count
    ///         offering to repair 552 things and then repairing 342 would be lying to the user about the
    ///         work it is proposing.
    ///     </para>
    /// </summary>
    public int ScoreRepairPendingCount => Entries.Count(e => e.ScoreRepairPending);

    /// <summary>
    ///     Re-derives the score for every demo carrying <see cref="DemoEntry.ScoreRepairPending" /> — the
    ///     explicit form of the sweep that used to run automatically on first launch.
    ///     <para>
    ///         It clears <see cref="DemoLibraryCacheEntry.ScoreComputed" /> on the flagged rows — the same
    ///         field the old hydrate repair cleared, so they re-derive by exactly the same route — and
    ///         then enlists them in the tier-2 backlog DIRECTLY.
    ///     </para>
    ///     <para>
    ///         <b>Deliberately not via <see cref="RescanAsync" />.</b> <c>Reconcile</c> only evaluates
    ///         NEWLY-discovered files; an entry already in <see cref="Entries" /> hits its <c>continue</c> and
    ///         is never re-tested for the backlog. So on a populated library — i.e. always, in the real app —
    ///         a rescan would flip the flag on disk and enlist NOTHING: a button that mutates the cache,
    ///         reports success and parses nothing. <c>_pendingFull</c> is this evaluator's <see cref="Wants" />
    ///         gate, so populating it IS the enlistment.
    ///     </para>
    ///     <para>
    ///         <see cref="DemoEntry.ScoreRepairPending" /> is deliberately NOT cleared here. The row is not
    ///         repaired until a parse has actually re-derived it, and clearing on submit would drop the card's
    ///         badge the instant the button was pressed — for a queue that may be hours long.
    ///     </para>
    ///     <para>
    ///         <b>An interrupted repair RESUMES on the next launch</b>, because the cleared
    ///         <c>ScoreComputed</c> is persisted. That is intended — the user asked for this work and quitting
    ///         is not a retraction — but it is the one path by which flagged rows re-enter the automatic
    ///         backlog, so it is worth knowing when reading the "never automatic" rule. Rows never pressed
    ///         are untouched and stay out of it.
    ///     </para>
    /// </summary>
    /// <returns>How many rows were enlisted.</returns>
    public async Task<int> RepairPendingScoresAsync()
    {
        List<DemoEntry> targets = [.. Entries.Where(e => e.ScoreRepairPending)];
        if (targets.Count == 0)
        {
            return 0;
        }

        foreach (DemoEntry target in targets)
        {
            UpsertCache(target.FilePath, c => c.ScoreComputed = false);
        }

        Save();

        // No queue (WASM, and the inline test path): parse them here, one at a time, and await — the same
        // shape RescanAsync uses when it has no Coordinator.
        if (Coordinator is null)
        {
            foreach (DemoEntry target in targets)
            {
                await Task.Run(() => IndexTier2Inline(target)).ConfigureAwait(false);
            }

            Save();
            RaiseChanged();
            return targets.Count;
        }

        lock (_tier2Lock)
        {
            foreach (DemoEntry target in targets)
            {
                _pendingFull[target.FilePath] = target;
            }
        }

        // These rows are already Indexed and have players/duration/map to show, so they deliberately do NOT
        // get the Indexing signal — the same reasoning the backlog submission uses. Pulsing hundreds of
        // populated cards at once, for hours, reads as the library breaking rather than topping up.
        foreach (DemoEntry target in targets)
        {
            Coordinator.Consider(target.FilePath);
        }

        return targets.Count;
    }

    /// <summary>
    ///     Re-enumerates all folders, reconciles <see cref="Entries" /> (adds new, drops missing, applies
    ///     cache hits), then enriches uncached demos in the background (tier 1 map first, then tier 2 full parse).
    /// </summary>
    public async Task RescanAsync()
    {
        _scanCts?.Cancel();
        CancellationTokenSource cts = _scanCts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;

        List<(string Path, long Size, DateTime Modified)> primaries;
        Dictionary<string, IReadOnlyList<string>> shadowFolders;
        try
        {
            // Enumerate (path-canonicalized), then collapse byte-identical COPIES at different real
            // paths onto one primary (content dedup) — cheap via a size pre-filter (only same-size
            // files are hashed) with the hash cached on the metadata row.
            (List<(string Path, long Size, DateTime Modified)> files, List<string> scannedRoots) =
                await Task.Run(EnumerateFiles, ct);
            _lastScannedRoots = [.. scannedRoots];
            (primaries, shadowFolders) = await Task.Run(() => ResolveContentIdentities(files, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        List<DemoEntry> needMap = new();
        List<DemoEntry> needFull = new();
        await PostAsync(() => Reconcile(primaries, shadowFolders, needMap, needFull));
        RaiseChanged();

        if (ct.IsCancellationRequested)
        {
            return;
        }

        // Tier 1 (cheap header → map/server). Light parallelism: it only reads ~256 KB per file.
        try
        {
            await Parallel.ForEachAsync(needMap,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = ct
                },
                async (entry, c) => { await Task.Run(() => IndexTier1(entry), c); });
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RaiseChanged();

        // Tier 2 (full parse → players/duration). Each parse holds a whole demo in RAM, so it runs
        // one demo at a time under the machine-wide invariant.
        if (Coordinator is not null)
        {
            // Coordinator path: record the backlog (its membership is this evaluator's Wants gate) and
            // ask the coordinator to consider each — it submits ONE queue item per interested evaluator,
            // coalesced by path, so Library + Highlights ride a single parse. RETURN; the queue owns the
            // workers + drain + gate yielding, and each Evaluate persists the cache itself.
            List<DemoEntry> toConsider;
            lock (_tier2Lock)
            {
                _pendingFull.Clear();
                foreach (DemoEntry entry in needFull)
                {
                    _pendingFull[entry.FilePath] = entry;
                }

                toConsider = [.. _pendingFull.Values];
            }

            foreach (DemoEntry entry in toConsider)
            {
                DemoEntry captured = entry;

                // Only a row with nothing to show gets the "being analyzed" signal at SUBMIT time. The real
                // one is posted when the parse actually starts (IndexTier2Core), and DemoEntry.IsIndexing is
                // documented as unique — "the indexer runs one demo at a time, so at most one entry is ever
                // true", which is what the card's animated bar and the row's pulsing dot mean.
                //
                // Marking the whole backlog Indexing up front always broke that, but it was invisible while
                // the backlog was a handful of new demos. The half-score repair enlists ALREADY-INDEXED rows
                // — 552 of them on the reference library — and those have real players, duration and a map
                // to show. Pulsing every card in the library at once, for hours, to re-derive one field
                // each, reads as the app having lost the library rather than as it quietly topping up.
                if (captured.State != DemoIndexState.Indexed)
                {
                    _post(() => captured.State = DemoIndexState.Indexing);
                }

                Coordinator.Consider(captured.FilePath);
            }

            return;
        }

        // Legacy inline path (no queue — tests): parse one at a time on this thread and AWAIT
        // completion, so a caller that awaits RescanAsync sees a fully-indexed library.
        try
        {
            foreach (DemoEntry entry in needFull)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Run(() => IndexTier2Inline(entry), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Persist whatever completed before cancellation.
        }

        Save();
        RaiseChanged();
    }

    // Shared tier-2 body for both held-parse entry points. The in-progress guard dedupes the two callers
    // when they race the same path (queue Evaluate + interactive-open fan-out): the second observes the
    // path in flight and bails, leaving the first to index + clear the backlog.
    private void RunTier2(string path, ParsedDemo parsed, bool fanOutToOthers)
    {
        DemoEntry? entry;
        lock (_tier2Lock)
        {
            if (!_tier2InProgress.Add(path))
            {
                return; // a replay for this exact path is already running — skip the duplicate
            }

            _pendingFull.TryGetValue(path, out entry);
        }

        try
        {
            if (entry is not null)
            {
                IndexTier2Core(entry, parsed, fanOutToOthers);
            }
        }
        finally
        {
            lock (_tier2Lock)
            {
                _tier2InProgress.Remove(path);
            }

            ClearTier2Backlog(path);
        }
    }

    /// <summary>
    ///     The current tier-2 backlog paths — a worker-readable snapshot for the coordinator's
    ///     candidate universe (never enumerate the UI-bound <see cref="Entries" /> off-thread).
    /// </summary>
    public IReadOnlyList<string> Tier2Backlog()
    {
        lock (_tier2Lock)
        {
            return [.. _pendingFull.Keys];
        }
    }

    // Removes a path from the tier-2 backlog (worker thread, under the lock) and, when the backlog drains,
    // persists the tail (parity with the inline path's final Save — the every-12 Save in IndexTier2Core
    // can leave <12 demos only in the in-memory cache).
    private void ClearTier2Backlog(string path)
    {
        bool drained;
        lock (_tier2Lock)
        {
            _pendingFull.Remove(path);
            drained = _pendingFull.Count == 0;
        }

        if (drained)
        {
            Save();
            RaiseChanged();
        }
    }

    // ── Enumeration + reconciliation ──────────────────────────────────────────

    // Also reports which registered roots were ACTUALLY reachable this pass. That distinction is the whole
    // safety property of the stale-row prune below: a configured folder on a detached volume enumerates zero
    // files and is indistinguishable, from the file list alone, from a folder whose demos were all deleted.
    // Pruning on "the file wasn't found" would wipe an entire external library's cache the first time it was
    // unplugged.
    private (List<(string Path, long Size, DateTime Modified)> Files, List<string> ScannedRoots)
        EnumerateFiles()
    {
        List<(string, long, DateTime)> result = new();
        List<string> scannedRoots = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };

        foreach (string rawFolder in Folders.ToArray())
        {
            // Resolve the registered folder to its real, normalized path FIRST, so two registrations of
            // the same directory (a symlink to it, a trailing-slash / relative form, or a folder nested
            // under another registered folder) enumerate the same file identities and collapse below.
            string folder = CanonicalizeDirectory(rawFolder);
            if (!Directory.Exists(folder))
            {
                continue; // unavailable (unmounted volume, deleted folder) — NOT evidence its demos are gone
            }

            scannedRoots.Add(folder);

            try
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*.dem", options))
                {
                    // Skip macOS AppleDouble sidecars ("._name.dem"): resource-fork / xattr metadata
                    // companions macOS writes next to a file when it's copied across a filesystem without
                    // native resource-fork support (SMB / exFAT / NFS). They match "*.dem" but are ~368 B of
                    // metadata, not a demo — parsing one fails into a bogus "Unknown" library card. AppleDouble
                    // names are ALWAYS "._<name>", so a filename-prefix skip is exact: no real demo is named that.
                    if (Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Canonicalize the file path (normalize + follow a leaf FILE symlink) so the same
                    // physical file reached via overlapping/nested registrations, a symlink, a trailing
                    // slash, or a differently-cased path resolves to ONE identity — appearing (and being
                    // processed) exactly once. Genuine content copies at different real paths are caught
                    // later by the content hash; here we only collapse paths that point at the same file.
                    string canonical = CanonicalizePath(path);
                    if (!seen.Add(canonical))
                    {
                        continue; // overlapping folders / symlink / already-seen identity
                    }

                    try
                    {
                        FileInfo fi = new(canonical);
                        result.Add((canonical, fi.Length, fi.LastWriteTime));
                    }
                    catch (IOException)
                    {
                        // file vanished mid-scan — skip
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable folder — skip
            }
        }

        return (result, scannedRoots);
    }

    /// <summary>
    ///     Drops metadata rows for demos that are provably gone. <see cref="Reconcile" /> has always dropped
    ///     the UI <see cref="Entries" /> for a vanished file but never the persisted <c>_cache</c> row behind
    ///     it, so the cache only ever grew: on the reference library 354 of 719 rows described files that no
    ///     longer existed — 332 of them under a folder the user had since removed from the library entirely.
    ///     Half the cache (and, once it is dual-written, half the sidecars) was describing demos the app can
    ///     never show.
    ///     <para>
    ///         <b>"The file wasn't found" is NOT sufficient evidence.</b> A configured folder on a detached
    ///         external volume enumerates zero files and looks exactly like a folder whose demos were all
    ///         deleted; pruning on absence alone would wipe that library's cache the first time it was
    ///         unplugged, turning a re-plug into a full re-index. A row is therefore dropped only when one of
    ///         two things is true:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             it sits under a root this scan actually REACHED, and the scan did not find it — the folder
    ///             was read and the file genuinely is not in it; or
    ///         </item>
    ///         <item>
    ///             it sits under no registered folder at all — out of scope, so nothing can ever index it
    ///             again without the user re-adding the folder, which re-indexes anyway.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         A row under a registered-but-unreachable root is KEPT, which is the detached-volume case.
    ///     </para>
    /// </summary>
    /// <param name="wanted">Paths this scan actually found, keyed case-insensitively.</param>
    private void PruneStaleCacheRows(Dictionary<string, (long Size, DateTime Modified)> wanted)
    {
        string[] scannedRoots = _lastScannedRoots;
        string[] registered = [.. _folderSnapshot.Select(CanonicalizeDirectory)];

        // No reachable root at all means the whole library is offline (every volume detached, or the very
        // first construction before any scan). Pruning then would delete everything.
        if (scannedRoots.Length == 0)
        {
            return;
        }

        static bool Under(string path, string root) =>
            path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);

        List<string> doomed = [];
        lock (_cacheLock)
        {
            foreach (string path in _cache.Keys)
            {
                if (wanted.ContainsKey(path))
                {
                    continue; // found by this scan — alive
                }

                bool underScanned = scannedRoots.Any(r => Under(path, r));
                bool underRegistered = registered.Any(r => Under(path, r));

                if (underScanned || !underRegistered)
                {
                    doomed.Add(path);
                }
            }

            foreach (string path in doomed)
            {
                _cache.Remove(path);
            }
        }

        if (doomed.Count == 0)
        {
            return;
        }

        // The unified cache holds a whole sidecar FILE per demo, so a stale row there costs real disk rather
        // than a line of JSON. Drop those too, in one batch — consumers re-project wholesale per change.
        if (_demoCache is not null)
        {
            using (_demoCache.BeginBatch())
            {
                foreach (string path in doomed)
                {
                    _demoCache.Remove(path);
                }
            }
        }

        // Diagnostics pillar, not Console (v0.6.0) — Console is invisible in a windowed Release build.
        AppLog.LibraryCachePruned(DiagLog, doomed.Count);
    }

    // Resolves a registered folder to its real, normalized absolute path — following a DIRECTORY
    // symlink to its final target — so a symlink-to-a-folder (or a nested/relative/trailing-slash
    // registration) enumerates the same file identities as the real folder. Best-effort: on any error
    // the input is returned unchanged (the scan still works, just without that canonicalization).
    private static string CanonicalizeDirectory(string folder)
    {
        try
        {
            string full = Path.GetFullPath(folder);
            FileSystemInfo? target = new DirectoryInfo(full).ResolveLinkTarget(true);
            return target is not null ? Path.GetFullPath(target.FullName) : full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return folder;
        }
    }

    // Normalizes a file path and follows a leaf FILE symlink to its final target, mapping a symlinked
    // or non-normalized path to the same identity as the real file. A symlinked PARENT directory is
    // handled by CanonicalizeDirectory; anything left (e.g. a mid-tree directory symlink, or a genuine
    // content copy) is caught by the Phase-4 content hash. Best-effort — returns the input on error.
    private static string CanonicalizePath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            FileSystemInfo? target = new FileInfo(full).ResolveLinkTarget(true);
            return target is not null ? Path.GetFullPath(target.FullName) : full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    // ── Content dedup: collapse byte-identical COPIES at different real paths ─────────────────────
    // Canonical-path dedup already folds the SAME physical file reached different ways; this
    // catches genuine COPIES (same bytes, distinct real paths). Cheap by construction: only files that
    // share an EXACT byte size are hashed (byte-identical ⟹ equal size, so a unique-size file can have no
    // twin), and the SHA is cached on the metadata row (path,size,mtime) so rescans don't re-read files.
    // Returns the primaries (one per content group — the lexicographically-smallest path, a stable choice)
    // plus, per primary, the OTHER folders holding a copy (for the "＋N copies" card hint). Runs off-thread.
    private (List<(string Path, long Size, DateTime Modified)> Primaries,
        Dictionary<string, IReadOnlyList<string>> ShadowFolders) ResolveContentIdentities(
            List<(string Path, long Size, DateTime Modified)> files, CancellationToken ct)
    {
        // Size pre-filter: hash a file's bytes only when another discovered file shares its exact size.
        Dictionary<long, int> countBySize = new();
        foreach ((string _, long size, DateTime _) in files)
        {
            countBySize[size] = countBySize.GetValueOrDefault(size) + 1;
        }

        Dictionary<string, string?> shaByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, long size, DateTime modified) in files)
        {
            ct.ThrowIfCancellationRequested();
            shaByPath[path] = countBySize[size] >= 2 ? GetOrComputeSha(path, size, modified) : null;
        }

        // Group by content identity: a non-null SHA groups copies together; a null SHA (unique size, or a
        // hash that failed) is its own singleton keyed by path, so it always stands alone as a primary.
        Dictionary<string, List<(string Path, long Size, DateTime Modified)>> groups = new(StringComparer.Ordinal);
        foreach ((string Path, long Size, DateTime Modified) file in files)
        {
            string? sha = shaByPath.GetValueOrDefault(file.Path);
            string key = sha is not null ? "sha:" + sha : "path:" + file.Path;
            if (!groups.TryGetValue(key, out List<(string, long, DateTime)>? list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(file);
        }

        List<(string, long, DateTime)> primaries = new(groups.Count);
        Dictionary<string, IReadOnlyList<string>> shadowFolders = new(StringComparer.OrdinalIgnoreCase);
        foreach (List<(string Path, long Size, DateTime Modified)> list in groups.Values)
        {
            // Primary = lexicographically-smallest path (Ordinal): deterministic + stable across runs, so the
            // same copy stays the canonical card regardless of enumeration order.
            list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));
            (string Path, long Size, DateTime Modified) primary = list[0];
            primaries.Add(primary);

            if (list.Count > 1)
            {
                List<string> folders = list.Skip(1)
                    .Select(s => Path.GetDirectoryName(s.Path) ?? "")
                    .Where(d => d.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (folders.Count > 0)
                {
                    shadowFolders[primary.Path] = folders;
                }
            }
        }

        return (primaries, shadowFolders);
    }

    // Returns the cached SHA-256 (lowercase hex) for a file when the (path,size,mtime) key still matches,
    // else streams the bytes to hash it and writes the result back onto the metadata row. Null on an I/O
    // failure — the caller then treats the file as its own singleton (never wrongly deduped).
    private string? GetOrComputeSha(string path, long size, DateTime modified)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(path, out DemoLibraryCacheEntry? c)
                && c.Size == size && c.ModifiedTicks == modified.Ticks && c.Sha256 is not null)
            {
                return c.Sha256;
            }
        }

        string? sha = HashFileStreaming(path);
        if (sha is not null)
        {
            UpsertCache(path, c => c.Sha256 = sha);
        }

        return sha;
    }

    // Streaming SHA-256 (constant memory — never loads the whole demo). Best-effort: null on any I/O error.
    private static string? HashFileStreaming(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Runs on the post (UI) thread. `primaries` are the deduped demos (one per content group);
    // `shadowFolders[primaryPath]` lists the other folders that hold a byte-identical copy of that primary.
    private void Reconcile(
        List<(string Path, long Size, DateTime Modified)> primaries,
        Dictionary<string, IReadOnlyList<string>> shadowFolders,
        List<DemoEntry> needMap, List<DemoEntry> needFull)
    {
        Dictionary<string, (long Size, DateTime Modified)> wanted = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, long size, DateTime modified) in primaries)
        {
            wanted[path] = (size, modified);
        }

        // Drop entries whose file no longer exists, moved out of scope, OR became a SHADOW (a smaller-path
        // copy appeared and took over as primary — this card collapses into that one).
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            if (!wanted.ContainsKey(Entries[i].FilePath))
            {
                Entries.RemoveAt(i);
            }
        }

        PruneStaleCacheRows(wanted);

        Dictionary<string, DemoEntry> byPath = Entries.ToDictionary(e => e.FilePath, StringComparer.OrdinalIgnoreCase);

        List<DemoEntry> added = new();
        foreach ((string path, (long size, DateTime modified)) in wanted)
        {
            IReadOnlyList<string> dupFolders = shadowFolders.TryGetValue(path, out IReadOnlyList<string>? f) ? f : [];

            if (byPath.TryGetValue(path, out DemoEntry? kept))
            {
                // Already present (kept across rescans) — just refresh its copy set (a twin may have
                // appeared or vanished since the last scan).
                if (!kept.DuplicateFolders.SequenceEqual(dupFolders, StringComparer.OrdinalIgnoreCase))
                {
                    kept.DuplicateFolders = dupFolders;
                }

                continue;
            }

            DemoEntry entry = new()
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Directory = Path.GetDirectoryName(path) ?? "",
                FileSizeBytes = size,
                Modified = modified,
                DuplicateFolders = dupFolders
            };

            DemoLibraryCacheEntry? cached = LookupCache(path, size, modified);
            if (cached is not null)
            {
                ApplyCache(entry, cached);
            }

            added.Add(entry);

            if (entry.MapName is null)
            {
                needMap.Add(entry);
            }

            // Full parse needed when the demo isn't indexed yet, OR it's indexed from an OLD cache row that
            // predates the score field (opportunistic backfill — see [[project_demo_library_browser]]): the
            // players/duration stay visible from cache while the score fills in, no full-cache wipe.
            //
            // What this deliberately does NOT sweep: a row REPAIRED at load. It keeps
            // ScoreComputed = true and carries ScoreRepairPending instead, so it does not land here. On the
            // reference library that was 342 demos / ~100 GB of background re-parsing, automatically, on the
            // first launch after upgrade. RepairPendingScoresAsync clears the flag-holders into this backlog
            // when the user asks for it; the card says so in the meantime (DemoEntry.NeedsScoreRepair).
            //
            // What this deliberately does NOT re-index: a row whose ROSTER is names-only because it predates
            // the tier-2 extension. That state is absent, not wrong, and it is already labelled as absent —
            // HasTeamSplit reads false and Match Overview offers a per-demo re-index (LegacyCacheMigration
            // documents that choice). Sweeping it automatically would turn a bounded repair of rows that
            // render incorrectly into a fresh full-library re-parse of ~575 demos on the next launch.
            if (entry.State != DemoIndexState.Indexed || cached is not { ScoreComputed: true })
            {
                needFull.Add(entry);
            }
        }

        // Single Reset event: a large folder adds hundreds of entries, and per-add notifications make
        // every bound consumer (VM filter pass + ItemsControl containers) re-run once PER ENTRY.
        Entries.AddRange(added);

        // Index newest-first — the browser's default sort — so the top of the visible list gets its
        // players/score first while the long tail fills in behind it.
        needMap.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        needFull.Sort((a, b) => b.Modified.CompareTo(a.Modified));

        int backfill = needFull.Count(e => e.State == DemoIndexState.Indexed);
        if (backfill > 0)
        {
            AppLog.LibraryScoreBackfill(DiagLog, backfill);
        }
    }

    private static void ApplyCache(DemoEntry entry, DemoLibraryCacheEntry cached)
    {
        entry.MapName = cached.Map;
        entry.ServerName = cached.Server;
        entry.DemoVersion = cached.DemoVersion;

        // A stale half-resolved score is refused HERE, at the read boundary, rather than being repaired into
        // the cache row.
        //
        // The obvious alternative — clear the row at hydrate and mark it — is a trap, and it is worth
        // knowing why. Cleared-to-all-nulls reads as COHERENT to IsScoreResultCoherent, so the marker would
        // have to be persisted to survive; and because UpsertCache mutates the very row that was cleared, ANY
        // later Save (a tier-1 map write will do) would persist the cleared row. Lose the marker in that
        // window and the demo is silently scoreless forever, with nothing left on disk to detect. Refusing at
        // read keeps the original half data on the row as the permanent evidence, so the state is re-derived
        // correctly on every launch and cannot be lost. It also means NO new persisted field.
        bool scoreIsStale = cached.FullyIndexed
                            && !IsScoreResultCoherent(cached.CtScore, cached.Score, cached.CtClan, cached.Clan);
        entry.ScoreRepairPending = scoreIsStale;

        if (cached.FullyIndexed)
        {
            entry.Players = cached.Players ?? [];
            entry.DurationSeconds = cached.DurationSeconds;
            entry.RoundCount = cached.RoundCount;
            if (!scoreIsStale)
            {
                entry.CtScore = cached.CtScore;
                entry.TScore = cached.Score;
                entry.CtClan = cached.CtClan;
                entry.TClan = cached.Clan;
            }

            entry.State = DemoIndexState.Indexed;
        }
    }

    // ── Indexing tiers (background threads; field writes marshalled via _post) ─

    private void IndexTier1(DemoEntry entry)
    {
        if (!DownstreamUtilities.TryReadQuickInfo(entry.FilePath, out DownstreamUtilities.DemoQuickInfo info))
        {
            return; // leave to tier 2 (or mark failed there)
        }

        _post(() =>
        {
            entry.MapName = info.MapName;
            entry.ServerName = info.ServerName;
            entry.DemoVersion = info.DemoVersion;
        });

        UpsertCache(entry.FilePath, c =>
        {
            c.Map = info.MapName;
            c.Server = info.ServerName;
            c.DemoVersion = info.DemoVersion;
        });
    }

    // Legacy inline tier-2 (no queue): read + parse on this thread, then run the shared core. Its own
    // failure handling marks the row Failed on a parse error.
    private void IndexTier2Inline(DemoEntry entry)
    {
        _post(() => entry.State = DemoIndexState.Indexing);

        ParsedDemo parsed;
        try
        {
            byte[] bytes = File.ReadAllBytes(entry.FilePath);
            parsed = DemoParser.Parse(bytes.AsMemory());
        }
        catch (Exception)
        {
            _post(() => entry.State = DemoIndexState.Failed);
            UpsertCache(entry.FilePath, c => c.FullyIndexed = false);
            return;
        }

        IndexTier2Core(entry, parsed, true);
    }

    // Post-parse tier-2 extraction (players / duration / map / final score) + optional fan-out to the OTHER
    // evaluators + cache write. Runs with the ParsedDemo held — inside the queue's gate slot on the queue
    // path, or inline on the legacy path. Self-contained failure handling so a throw marks ONLY this row
    // Failed. fanOutToOthers is true when Library is the producer (background tier-2); false when the parse
    // arrived opportunistically (the coordinator already handled the wider fan-out).
    private void IndexTier2Core(DemoEntry entry, ParsedDemo parsed, bool fanOutToOthers)
    {
        List<string> players;
        double duration;
        string? map;
        int? ctScore = null, tScore = null;
        string? ctClan = null, tClan = null;
        try
        {
            players = parsed.Players.Values
                // IsHltv as well as IsBot: the GOTV proxy holds a userinfo slot with a name, and
                // before the CS2-path fakeplayer/ishltv read it landed on library cards and in the
                // player filter as if it were someone who played.
                .Where(p => !p.IsBot && !p.IsHltv && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => p.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            duration = parsed.Duration.TotalSeconds;
            map = parsed.MapName;

            // Post the primary metadata FIRST (cheap) so the card fills in players/duration immediately;
            // the score comes from a slower entity replay (below) and fills in a few seconds later.
            _post(() =>
            {
                if (!string.IsNullOrEmpty(map))
                {
                    entry.MapName = map;
                }

                entry.Players = players;
                entry.DurationSeconds = duration;
                entry.State = DemoIndexState.Indexed;
            });

            // Final score: CCSTeam.m_iScore at match end (no cheap event source exists in CS2 — team_score /
            // round_end are absent). Best-effort — a replay failure just leaves the score unset.
            try
            {
                (ctScore, tScore, ctClan, tClan) = ExtractFinalScore(parsed);
            }
            catch (Exception)
            {
                // score stays null; players/duration are already posted
            }

            // Fan-out: when Library is the PRODUCER of this parse (background
            // tier-2), hand the still-held parse to the OTHER evaluators (the highlight scanner) so a
            // stale/missing highlights row refreshes on THIS parse instead of a second one — the generalized
            // replacement for the old Tier2DemoParsed piggyback. Each evaluator is isolated inside
            // FanOutParsed, so a scan failure never marks the LIBRARY row failed. Skipped when the parse
            // arrived opportunistically (the coordinator's FanOutParsed already fanned to the others; re-
            // fanning would double-feed them). Null coordinator (inline/legacy path, tests) → nothing to fan.
            if (fanOutToOthers)
            {
                Coordinator?.FanOutParsed(entry.FilePath, parsed, _fanOutSkipSelf);
            }

            // parsed drops out of scope here → GC can reclaim before the next parse.
        }
        catch (Exception)
        {
            _post(() => entry.State = DemoIndexState.Failed);
            UpsertCache(entry.FilePath, c => c.FullyIndexed = false);
            return;
        }

        // Mirror ExtractFinalScore's both-or-nothing contract at the WRITE boundary as well as the read
        // one. The extractor upholds it today, so this is not tidiness: it is what stops a future edit
        // there from minting the same half-resolved rows LoadPersisted now has to repair. A half score is
        // silent — HasScore needs BOTH sides, so the card just quietly loses its badge — and it persists
        // with ScoreComputed = true, which is exactly the flag that stops it ever being recomputed.
        if (!IsScoreResultCoherent(ctScore, tScore, ctClan, tClan))
        {
            (ctScore, tScore, ctClan, tClan) = (null, null, null, null);
        }

        if (ctScore is int ct && tScore is int t)
        {
            _post(() =>
            {
                entry.CtScore = ct;
                entry.TScore = t;
                entry.CtClan = ctClan;
                entry.TClan = tClan;
            });
        }

        // Unconditional, unlike the block above: the badge means "a re-derivation is owed", and one just ran.
        // Leaving it set when the extractor honestly returned nothing would make the card ask forever for
        // work that has already been done.
        if (entry.ScoreRepairPending)
        {
            _post(() => entry.ScoreRepairPending = false);
        }

        // Rounds + the richer roster are a PARSE product, already in hand — projected ONCE here for both
        // consumers rather than twice. The library row wants the round COUNT, which nothing ever wrote:
        // every cached row carried RoundCount = 0, and the legacy migration faithfully carried that zero
        // into the unified cache. The unified cache wants the boundaries themselves.
        //
        // Isolated in its own try because a projection failure must leave the row INDEXED — the demo
        // parsed fine, and players/duration are already posted to the card.
        List<CachedPlayerInfo>? cachedPlayers = null;
        List<CachedRound>? rounds = null;
        try
        {
            (cachedPlayers, rounds) = ProjectTier2(parsed);
        }
        catch (Exception)
        {
            // Both stay null → the row keeps the round count it already had rather than gaining a zero.
        }

        UpsertCache(entry.FilePath, c =>
        {
            if (!string.IsNullOrEmpty(map))
            {
                c.Map = map;
            }

            c.Players = players;
            c.DurationSeconds = duration;

            if (rounds is not null)
            {
                c.RoundCount = rounds.Count;
            }

            c.CtScore = ctScore;
            c.Score = tScore;
            c.CtClan = ctClan;
            c.Clan = tClan;
            c.ScoreComputed = true;
            c.FullyIndexed = true;
        });

        WriteTier2ToDemoCache(entry, parsed, map, duration, ctScore, tScore, ctClan, tClan, cachedPlayers, rounds);

        // Persist periodically so a long scan's progress survives an app close, and nudge the VM so
        // the player/map filters grow during a long sequential scan (its end may be an hour away).
        if (Interlocked.Increment(ref _enrichedSinceSave) % 12 == 0)
        {
            Save();
            RaiseChanged();
        }
    }

    // The TIER-2 EXTENSION. Everything written here is
    // already in hand at this point in the pass — PlayerInfo is (Slot, Name, SteamId64, UserId, Team, IsBot)
    // plus IsHltv, and ParsedDemo exposes TickCount/TickRate/Duration — so the library cache storing NAMES
    // ONLY was a choice, not a cost. Capturing the rest (~+0.5 KB/demo) is what lets Match Overview render
    // rosters split by team, bot tags, honest player/spectator counts and tick rate from cache alone, for
    // the ~80% of a real library this pass has already covered.
    //
    // Deliberately excludes anything needing the rules engine (scoreboard, per-side split, highlights) —
    // that is tier 3, and it stays behind an explicit per-demo action.
    //
    // Fully defensive: a cache-write failure must never mark the LIBRARY row failed, exactly as a highlight
    // scan failure does not.
    //
    // players/rounds arrive PRE-PROJECTED (ProjectTier2 runs in the caller) because the library row needs the
    // round count too, and this method no-ops entirely when the unified cache is absent — projecting here
    // would have made the count unobtainable on exactly the path that was writing zeros. Null means the
    // projection threw; every other field is still worth writing.
    private void WriteTier2ToDemoCache(DemoEntry entry, ParsedDemo parsed, string? map, double duration,
        int? ctScore, int? tScore, string? ctClan, string? tClan,
        List<CachedPlayerInfo>? players, List<CachedRound>? rounds)
    {
        if (_demoCache is null)
        {
            return;
        }

        try
        {
            _demoCache.Update(entry.FilePath, entry.FileSizeBytes, entry.Modified.Ticks, record =>
            {
                if (!string.IsNullOrEmpty(map))
                {
                    record.Map = map;
                }

                record.Server = parsed.ServerName;
                record.DurationSeconds = duration;
                record.TickRate = parsed.TickRate;
                record.TickCount = parsed.TickCount;
                record.ServerStartTick = parsed.ServerStartTick;

                // Null only when the projection threw — keep what the record already holds rather than
                // replacing a real roster with an empty one.
                if (players is not null)
                {
                    record.Players = players;
                }

                // RoundCount tracks the boundaries whenever we HAVE boundaries. It exists for migrated rows
                // that carry a count with nothing behind it, and a re-index that wrote Rounds but left the
                // migrated count alone would leave the record contradicting itself — masked today only
                // because ToIndexEntry happens to prefer Rounds.Count.
                if (rounds is not null)
                {
                    record.Rounds = rounds;
                    record.RoundCount = rounds.Count;
                }

                record.CtScore = ctScore;
                record.TScore = tScore;
                record.CtClan = ctClan;
                record.TClan = tClan;
                DemoCacheStore.StampParse(record);
            });
        }
        catch (Exception)
        {
            // Rebuildable cache — the library row stands on its own.
        }
    }

    /// <summary>
    ///     The tier-2 projection: roster and round boundaries out of a parsed demo, in the exact shape the
    ///     unified cache stores. Internal so it can be asserted directly against a real demo — the invariant
    ///     that matters (cached player count agrees with the cached rosters) is one this codebase has broken
    ///     before, when counting every named entry reported 13 players above rosters of ten.
    /// </summary>
    internal static (List<CachedPlayerInfo> Players, List<CachedRound> Rounds) ProjectTier2(ParsedDemo parsed)
    {
        List<CachedPlayerInfo> players =
        [
            .. parsed.Players.Values
                // The GOTV proxy holds a userinfo slot with a name but never played; excluding it here is
                // what makes the cached player count agree with the cached rosters. Bots and spectators ARE
                // kept, with their team — the projection decides how to present them, and it cannot recover
                // a distinction the cache threw away.
                .Where(p => !p.IsHltv && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new CachedPlayerInfo
                {
                    Slot = p.Slot,
                    Name = p.Name, // RAW — sanitize at the render boundary only
                    SteamId64 = p.SteamId64.ToString(CultureInfo.InvariantCulture),
                    Team = p.Team,
                    IsBot = p.IsBot
                })
        ];

        // Round boundaries are a PARSE product, not an analysis one — and there is ONE deriver for them
        // now: CS2DemoKit.Analysis.Clips.ClipRounds, in the FRAME clock.
        //
        // Careful: CS2 DOES NOT EMIT round_start. It opens a round with round_freeze_end and closes it with
        // round_officially_ended. Matching the string "round_start" (as the highlights scanner did)
        // therefore yields an EMPTY list on every CS2 demo, which is exactly what the measured cache
        // showed: zero rounds on every row, including the ones that had actually been scanned.
        //
        // GameTick, not ServerTick: this field is FRAME CLOCK, and the clip math that consumes it
        // (ClipWindows.RoundStartFor) is frame clock throughout. Never offset it by ServerStartTick —
        // DemoAnalyzer's own round list is the ABSOLUTE-clock variant and is not interchangeable here.
        List<CachedRound> rounds = ClipRounds.Derive(parsed).ToCachedRounds();

        return (players, rounds);
    }

    // Reads the authoritative final scoreboard: CCSTeam.m_iScore per side (CT = team 3, T = team 2) plus clan
    // names, entity-replayed to the last frame — exactly what the app's own UpdateGameInfo treats as the score.
    // Correct for complete demos; on demos truncated at the buzzer the winner's final-round increment can be
    // absent from the recorded frames (unrecoverable — no event carries the final score), matching what the app
    // itself would display. Returns nulls for warmup-only / team-less demos so the card omits the score.
    internal static (int? Ct, int? T, string? CtClan, string? TClan) ExtractFinalScore(ParsedDemo parsed)
    {
        IReadOnlyList<DemoFrame> frames = parsed.Frames;
        if (frames.Count == 0)
        {
            return (null, null, null, null);
        }

        // The score reads only CCSTeam. The entity bitstream is sequential (every entity must be
        // DECODED to reach the next), but we STORE only CCSTeam's fields — skipping the per-field
        // storage + allocation for every other class. Byte-identical score (proven by
        // EntityStoreFilterEquivalenceTests over real demos, including buzzer-truncated ones); ~1.2x
        // faster and ~30-60% less allocation on this replay — the latter eases the Library-backlog RAM
        // pressure. Unlike the reverted checkpoint approach this replays ALL deltas from frame 0, so it
        // is truly identical, not merely approximate.
        EntityTracker tracker = new()
        {
            StoreClassFilter = _scoreClasses
        };
        tracker.ReplayToIndex(frames.Count - 1, frames);

        int? ct = null, t = null;
        string? ctClan = null, tClan = null;
        foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (ent.ClassName != "CCSTeam")
            {
                continue;
            }

            int teamNum = CoerceInt(ent["m_iTeamNum"]);
            int score = CoerceInt(ent["m_iScore"]);
            string clan = ent["m_szClanTeamname"] as string ?? "";
            if (teamNum == 2)
            {
                t = score;
                if (clan.Length > 0)
                {
                    tClan = clan;
                }
            }
            else if (teamNum == 3)
            {
                ct = score;
                if (clan.Length > 0)
                {
                    ctClan = clan;
                }
            }
        }

        if (ct is null || t is null || ct + t == 0)
        {
            return (null, null, null, null); // warmup-only / no teams → omit
        }

        return (ct, t, ctClan, tClan);
    }

    /// <summary>
    ///     Is this score/clan tuple one <see cref="ExtractFinalScore" /> could actually have produced?
    ///     <para>
    ///         The extractor is BOTH-OR-NOTHING: it returns all four nulls unless it resolved a score for
    ///         team 2 AND team 3 with a non-zero sum, and it only ever reaches the clan reads on that same
    ///         path. So "CT 16, T null" is a state the current code cannot emit — yet real caches are full of
    ///         it (on the reference library 555 rows carry a CT score and 3 carry the T score), left behind by
    ///         an older model whose <c>TScore</c>/<c>TClan</c> properties were renamed to <c>Score</c>/
    ///         <c>Clan</c> in commit eb79e1e. Every already-written row silently stopped deserializing its T
    ///         side, and <c>ScoreComputed = true</c> meant nothing ever recomputed it.
    ///     </para>
    ///     <para>
    ///         <b>This predicate is the whole loop guard</b>, so its exact shape matters. It flags only states
    ///         the extractor CANNOT produce, which makes the repair self-terminating by construction: whatever
    ///         a re-derivation writes is coherent, so a repaired row is never suspect a second time — no
    ///         "already tried" bookkeeping needed. In particular a single-clan result (both scores, one clan
    ///         name) is legitimate — HLTV demos where only one side set a clan tag — and is NOT flagged.
    ///         Flagging it would re-index those demos on every single launch, forever.
    ///     </para>
    /// </summary>
    /// <param name="ctScore">CT-side (team 3) final score, or null when the replay resolved none.</param>
    /// <param name="tScore">T-side (team 2) final score, or null when the replay resolved none.</param>
    /// <param name="ctClan">CT-side clan name, or null/blank when the demo carries none.</param>
    /// <param name="tClan">T-side clan name, or null/blank when the demo carries none.</param>
    /// <returns>True when the tuple satisfies the extractor's contract; false when it is a stale half-result.</returns>
    internal static bool IsScoreResultCoherent(int? ctScore, int? tScore, string? ctClan, string? tClan)
    {
        if (ctScore is int ct && tScore is int t)
        {
            return ct + t > 0; // both sides resolved — clans may legitimately be one-sided or absent
        }

        // Neither side resolved. The extractor bails BEFORE it can have kept a clan, so a clan without a
        // score is the same stale half-result as a score without its other half.
        return ctScore is null && tScore is null
                                && string.IsNullOrWhiteSpace(ctClan) && string.IsNullOrWhiteSpace(tClan);
    }

    // Does this hydrated row hold a score the extractor could not have produced? PURE — it deliberately
    // mutates nothing (see the note in ApplyCache for why repairing the row in place is unsafe). The row is
    // left exactly as written and the half score is refused at the read boundary instead.
    private static bool HasIncoherentScore(DemoLibraryCacheEntry row) =>
        row.FullyIndexed && !IsScoreResultCoherent(row.CtScore, row.Score, row.CtClan, row.Clan);

    // CCSTeam scalars arrive boxed (Int32 on the wire per project_cs2_wire_encoding); coerce defensively.
    private static int CoerceInt(object? v) => v switch
    {
        int i => i,
        uint u => (int)u,
        short s => s,
        ushort u => u,
        long l => (int)l,
        ulong u => (int)u,
        byte b => b,
        sbyte s => s,
        _ => 0
    };

    // ── Cache (thread-safe) ───────────────────────────────────────────────────

    private DemoLibraryCacheEntry? LookupCache(string path, long size, DateTime modified)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(path, out DemoLibraryCacheEntry? c) &&
                c.Size == size && c.ModifiedTicks == modified.Ticks)
            {
                return c;
            }
        }

        return null;
    }

    private void UpsertCache(string path, Action<DemoLibraryCacheEntry> mutate)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(path, out DemoLibraryCacheEntry? c))
            {
                long size = 0;
                long ticks = 0;
                try
                {
                    FileInfo fi = new(path);
                    size = fi.Length;
                    ticks = fi.LastWriteTime.Ticks;
                }
                catch (IOException)
                {
                    // best-effort keying
                }

                c = new DemoLibraryCacheEntry
                {
                    Path = path,
                    Size = size,
                    ModifiedTicks = ticks
                };
                _cache[path] = c;
            }

            mutate(c);
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    // Restores the metadata cache from library.json and RETURNS the folder list it stored (empty when
    // there is no file). The caller (SeedFolders) decides whether library.json or settings is the
    // authoritative folder source — so this never mutates Folders itself.
    private List<string> LoadPersisted()
    {
        List<string> folders = [];
        if (_dataPath is null || !File.Exists(_dataPath))
        {
            return folders;
        }

        try
        {
            DemoLibraryData? data = JsonSerializer.Deserialize<DemoLibraryData>(File.ReadAllText(_dataPath));
            if (data is null || data.SchemaVersion != DemoLibraryCacheEntry.CurrentSchema)
            {
                // Keep folders even on a schema bump; drop the stale cache so it re-indexes.
                if (data is not null)
                {
                    folders.AddRange(data.Folders);
                }

                return folders;
            }

            folders.AddRange(data.Folders);

            // Count only — the rows are hydrated exactly as written. A score that violates
            // ExtractFinalScore's both-or-nothing contract is refused in ApplyCache (which is also where the
            // reason it is not repaired in place is written down), and the user is offered the re-derivation
            // explicitly rather than having ~100 GB of parsing started on their behalf at launch.
            int repaired = 0;
            lock (_cacheLock)
            {
                foreach (DemoLibraryCacheEntry c in data.Cache)
                {
                    if (HasIncoherentScore(c))
                    {
                        repaired++;
                    }

                    _cache[c.Path] = c;
                }
            }

            if (repaired > 0)
            {
                // This count is ROWS MARKED, which is not the same as demos that can be re-parsed, and on a
                // real cache the gap is large: 552 rows were repairable on the reference library but only 342
                // of them had a file still on disk. The rest described demos under a folder the user had
                // removed, and nothing can ever re-derive those. PruneStaleCacheRows drops them on the first
                // scan, so from the second launch onwards the two numbers converge — which is why the figure
                // the UI offers to repair is ScoreRepairPendingCount (counted over Entries), not this one.
                AppLog.LibraryHalfResolvedScores(DiagLog, repaired);
            }
        }
        catch
        {
            // best-effort; ignore a corrupt file
        }

        return folders;
    }

    /// <summary>Persists folders + cache (best-effort; no-op on WASM).</summary>
    public void Save()
    {
        // The unified cache's sidecars are already on disk (Upsert writes them eagerly); only its index is
        // deferred, so it rides the same checkpoints library.json uses — including the every-12-demos save
        // inside a long scan, which is what makes an interrupted backfill's progress survive.
        _demoCache?.SaveIndex();

        if (_dataPath is null)
        {
            return;
        }

        DemoLibraryData data;
        lock (_cacheLock)
        {
            data = new DemoLibraryData
            {
                SchemaVersion = DemoLibraryCacheEntry.CurrentSchema,
                Folders = [.. _folderSnapshot],
                Cache = _cache.Values.ToList()
            };
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dataPath)!);
            File.WriteAllText(_dataPath, JsonSerializer.Serialize(data, _jsonOptions));
        }
        catch
        {
            // best-effort
        }
    }

    private Task PostAsync(Action action)
    {
        TaskCompletionSource tcs = new();
        _post(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private void RaiseChanged() => _post(() => Changed?.Invoke());
}
