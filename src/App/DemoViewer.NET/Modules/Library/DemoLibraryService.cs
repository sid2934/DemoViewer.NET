#region

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>
///     Scans user-configured folders for <c>*.dem</c> files and indexes their metadata in two tiers:
///     <b>tier 1</b> is the cheap first-frame header read (map / server, near-instant, via
///     <see cref="DownstreamUtilities.TryReadQuickInfo(string,out DownstreamUtilities.DemoQuickInfo)" />);
///     <b>
///         tier
///         2
///     </b>
///     is a background <b>full parse</b> for players + duration (the only place those live: not in the
///     header, not in the .dem.info companion). Results are cached to disk keyed on (path, size, mtime) so
///     relaunches are instant and only new/changed files are re-indexed.
///     <para>
///         <b>Threading.</b> Enumeration + parsing run on background threads; every mutation of the bound
///         <see cref="Entries" />/<see cref="Folders" /> collections and of a <see cref="DemoEntry" />'s
///         observable fields is marshalled through the injected <c>post</c> delegate (the UI dispatcher in the
///         app; an inline invoker in tests). Full parses run <b>sequentially</b>: one demo at a time, under
///         the machine-wide gate when present, because a full parse holds the whole demo in RAM and this
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

    private readonly Dictionary<string, DemoLibraryCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private readonly string? _dataPath; // library.json, or null on WASM

    // The unified demo cache, dual-written alongside library.json during the transition. Null → legacy only.
    private readonly DemoCacheStore? _demoCache;
    private readonly Dictionary<string, DemoEntry> _pendingFull = new(StringComparer.OrdinalIgnoreCase);

    // Demos a reconcile added for indexing that no rescan has handed to tier 2 yet. A newer rescan that
    // replaces one mid-flight finds them already in Entries, and indexes them from here rather than never.
    private readonly HashSet<string> _awaitingIndex = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<Action> _post; // marshal to the UI thread (Dispatcher in-app; inline in tests)

    // When injected, AppSettings.Library.Folders is the folder source-of-truth (read on
    // construction, written on Add/Remove). Null → the legacy path where library.json owns the folder list.
    // The metadata cache stays in library.json either way.

    // Paths with a tier-2 replay in flight RIGHT NOW: a second concurrent replay of the same demo would be
    // wasted work and could flip Indexed↔Failed on a throw.
    private readonly HashSet<string> _tier2InProgress = new(StringComparer.OrdinalIgnoreCase);

    // Tier-2 backlog: _pendingFull is the working set of demos still needing a tier-2 parse (recomputed
    // each rescan; Wants(path) reads its membership). The scheduler's own outstanding set prevents
    // double-submit, so no separate _enqueued set is needed here.
    private readonly object _tier2Lock = new();

    // Diagnostics-pillar logger (v0.6.0, replaced Console.WriteLine, which a windowed Release build
    // never shows). Lazy like MainViewModel.DiagLog: the ambient factory is wired after construction.
    private ILogger? _diagLog;
    private int _enrichedSinceSave;

    // A plain array mirror of Folders, refreshed on the (UI) thread that mutates the UI-bound
    // ObservableCollection. Save() runs on a queue worker thread and reads THIS, never enumerating the
    // live Folders across a concurrent Add/Remove (which would throw "collection was modified").
    private volatile string[] _folderSnapshot = [];

    // Each registered folder's real path from the last walk that resolved it, so a folder that does not
    // answer this scan still covers the rows under its real path.
    private readonly Dictionary<string, string> _resolvedRoots = new(StringComparer.Ordinal);

    // Each registered folder's latest listing read and latest header read, so neither kind starts a read beside
    // one of its own that has not answered. Apart, so one stuck file does not stop the folder being listed.
    private readonly Dictionary<string, (Task Answer, DateTime Started)> _outstanding = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Task Answer, DateTime Started)> _outstandingHeaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Task Answer, DateTime Started)> _outstandingFingerprints = new(StringComparer.Ordinal);

    // Fingerprints of files no hashed row lists, read this session; one counts only while the file's size and
    // write time are the ones it was read at. Under its own lock.
    private readonly Dictionary<string, (long Size, long Ticks, DemoContentFingerprint Fingerprint)> _fingerprints =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _scanCts;
    private volatile bool _disposed;

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
    ///     results are written HERE AS WELL AS to <c>library.json</c>, a deliberate dual write for the
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

    private ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger(AppLog.LibraryCategory);

    /// <summary>The file-system calls the folder walk makes. Test seam.</summary>
    internal ILibraryFolderReader FolderReader { get; set; } = FileSystemLibraryFolderReader.Instance;

    /// <summary>The queue the scan's work runs on; null uses <see cref="QueueWork.Ambient" />. Test seam.</summary>
    internal IDemoProcessingQueue? QueueOverride { get; set; }

    /// <summary>The header read tier 1 makes per demo. Test seam.</summary>
    internal ILibraryHeaderReader HeaderReader { get; set; } = FileSystemLibraryHeaderReader.Instance;

    /// <summary>The content hash and fingerprint reads. Test seam.</summary>
    internal ILibraryContentReader ContentReader { get; set; } = FileSystemLibraryContentReader.Instance;

    /// <summary>
    ///     True when the parse tier 2 runs on hashes the file it reads and hands the hash to the cache
    ///     (<see cref="DemoCacheStore.NoteContentRead" />): tier 2 then takes the hash from there and never reads
    ///     the file again. False reads the file once more after the parse.
    /// </summary>
    internal bool ParseHashesContent { get; init; }

    /// <summary>How long the folder walk and the header reads wait on the file system. Test seam.</summary>
    internal LibraryScanTiming Timing { get; set; } = LibraryScanTiming.Default;

    /// <summary>The clock a file's settle window is measured against. Test seam.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    private IDemoProcessingQueue? Queue => QueueOverride ?? QueueWork.Ambient;

    /// <summary>
    ///     The settings service this indexer is folder-backed by, or <c>null</c> on the legacy path.
    ///     Exposed for the composition-root test to assert the container injected the SINGLETON instance.
    /// </summary>
    internal SettingsService? SettingsBacking { get; }

    // The scheduler owns submission; this service never touches the queue directly.
    /// <summary>The scheduler that drives this service's tier-2 work; null leaves tier 2 pending.</summary>
    public DemoScheduler? Scheduler { get; set; }

    /// <summary>The configured root folders (recursively scanned). Bound to the UI; mutate via the public methods.</summary>
    public ObservableCollection<string> Folders { get; } = [];

    /// <summary>All discovered demos with their (progressively enriched) metadata. Bound to the browser.</summary>
    public BulkObservableCollection<DemoEntry> Entries { get; } = [];

    /// <summary>
    ///     How many demos IN THE LIBRARY are waiting on a score re-derivation.
    ///     <para>
    ///         Counted over <see cref="Entries" />, not over the cache, and the difference is not pedantic: on
    ///         the reference library 552 rows were repairable but only 342 had a file still on disk. The rest
    ///         described demos under a folder the user had removed, which nothing can ever re-derive. A count
    ///         offering to repair 552 things and then repairing 342 would be lying to the user about the
    ///         work it is proposing.
    ///     </para>
    /// </summary>
    public int ScoreRepairPendingCount => Entries.Count(e => e.ScoreRepairPending);

    // ── IDemoEvaluator ("one parse, many evaluators") ──

    /// <inheritdoc />
    public string Id => "library";

    /// <inheritdoc />
    public bool ReadsUserCommands => false;

    /// <inheritdoc />
    /// <remarks>
    ///     Cheap membership test against the tier-2 backlog recorded at reconcile. Asked again right before
    ///     this evaluator's turn, so it MUST go false once processed, which <see cref="ClearTier2Backlog" />
    ///     guarantees synchronously in both Evaluate and OnFailed.
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
    ///     Runs on a queue worker thread with the parse still held (the memory-safety window): the queue's
    ///     own read, or the shell's parse when the demo is being opened or is loaded. The backlog is cleared
    ///     SYNCHRONOUSLY here (not via the posted UI callback) so a plan that races this can't see the path as
    ///     still-wanted and double-submit it. A demo that is not a known-pending library entry (opened from
    ///     outside every registered folder, or already indexed) is a no-op: a foreign demo is never injected
    ///     into the library.
    /// </remarks>
    public void Evaluate(string path, ParsedDemo parsed) => RunTier2(path, entry => IndexTier2Core(entry, parsed));

    /// <inheritdoc />
    /// <remarks>
    ///     Rules as well as the final state: a new demo's highlights and round facts ride the same forward
    ///     read on the same visit.
    /// </remarks>
    public ForwardNeeds? ForwardFor(string path) => ForwardNeeds.FinalState | ForwardNeeds.Rules;

    /// <inheritdoc />
    public void EvaluateForward(string path, ForwardDemoResult pass) =>
        RunTier2(path, entry => IndexTier2Core(entry, pass));

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
        // The coordinator owns the CapacityAvailable subscription now: nothing queue-side to detach here.
        _disposed = true;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
    }

    /// <summary>Raised (on the post thread) whenever a scan finishes a phase, so the VM can refresh filters.</summary>
    public event Action? Changed;

    // Populates Folders from the authoritative source. Legacy path (no settings): library.json's folders,
    // identical to the previous behavior. Settings path: AppSettings.Library.Folders, with a one-time
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
    ///     Re-derives the score for every demo carrying <see cref="DemoEntry.ScoreRepairPending" />, the
    ///     explicit form of the sweep that used to run automatically on first launch.
    ///     <para>
    ///         It clears <see cref="DemoLibraryCacheEntry.ScoreComputed" /> on the flagged rows, the same
    ///         field the old hydrate repair cleared, so they re-derive by exactly the same route, and
    ///         then enlists them in the tier-2 backlog DIRECTLY.
    ///     </para>
    ///     <para>
    ///         <b>Deliberately not via <see cref="RescanAsync" />.</b> <c>Reconcile</c> only evaluates
    ///         NEWLY-discovered files; an entry already in <see cref="Entries" /> hits its <c>continue</c> and
    ///         is never re-tested for the backlog. So on a populated library, i.e. always in the real app,
    ///         a rescan would flip the flag on disk and enlist NOTHING: a button that mutates the cache,
    ///         reports success and parses nothing. <c>_pendingFull</c> is this evaluator's <see cref="Wants" />
    ///         gate, so populating it IS the enlistment.
    ///     </para>
    ///     <para>
    ///         <see cref="DemoEntry.ScoreRepairPending" /> is deliberately NOT cleared here. The row is not
    ///         repaired until a parse has actually re-derived it, and clearing on submit would drop the card's
    ///         badge the instant the button was pressed, for a queue that may be hours long.
    ///     </para>
    ///     <para>
    ///         <b>An interrupted repair RESUMES on the next launch</b>, because the cleared
    ///         <c>ScoreComputed</c> is persisted. That is intended: the user asked for this work, and quitting
    ///         is not a retraction. But it is the one path by which flagged rows re-enter the automatic
    ///         backlog, so it is worth knowing when reading the "never automatic" rule. Rows never pressed
    ///         are untouched and stay out of it.
    ///     </para>
    /// </summary>
    /// <returns>How many rows were enlisted.</returns>
    public Task<int> RepairPendingScoresAsync()
    {
        List<DemoEntry> targets = [.. Entries.Where(e => e.ScoreRepairPending)];
        // Without a scheduler nothing could read them; the rows stay as they are.
        if (targets.Count == 0 || Scheduler is null)
        {
            return Task.FromResult(0);
        }

        foreach (DemoEntry target in targets)
        {
            UpsertCache(target.FilePath, c => c.ScoreComputed = false);
        }

        Save();

        lock (_tier2Lock)
        {
            foreach (DemoEntry target in targets)
            {
                _pendingFull[target.FilePath] = target;
            }
        }

        // These rows are already Indexed and have players/duration/map to show, so they deliberately do NOT
        // get the Indexing signal, the same reasoning the backlog submission uses. Pulsing hundreds of
        // populated cards at once, for hours, reads as the library breaking rather than topping up.
        foreach (DemoEntry target in targets)
        {
            Scheduler.DemoChanged(target.FilePath);
        }

        return Task.FromResult(targets.Count);
    }

    /// <summary>
    ///     Re-lists all folders, reconciles <see cref="Entries" /> (adds new, drops missing, applies cache
    ///     hits), then enriches uncached demos in the background (tier 1 map first, then tier 2 full parse).
    ///     <para>
    ///         Each folder is walked on its own, in short light queue slices, so a slow or hung folder holds
    ///         up neither the other folders nor the queue: a folder that finishes early shows its demos at
    ///         once, and a read that gives no answer within <see cref="LibraryScanTiming.NoAnswer" /> ends that
    ///         folder's walk as not reached. A newer rescan stops this one's walks. A file no row lists that
    ///         shares its size with another file or a known demo is fingerprinted after every folder is listed,
    ///         in reads the returned task does not wait for, and only those files wait on them for their full
    ///         parse. A fingerprint matching a known demo attaches the file to it without reading it in full.
    ///     </para>
    ///     <para>
    ///         Header reads run the same way, a folder at a time. A folder's full parses are enlisted once its
    ///         headers are read; when a header read gives no answer in time, the folder's unread demos and its
    ///         full parses are left for the next scan rather than failed.
    ///     </para>
    /// </summary>
    public async Task RescanAsync()
    {
        if (_disposed)
        {
            return;
        }

        _scanCts?.Cancel();
        CancellationTokenSource cts = _scanCts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;

        string[] folders = _folderSnapshot;
        List<DemoEntry> needMap = [];
        List<DemoEntry> needFull = [];
        ScanScope scope;
        ContentGroups groups;
        List<string> deferred = [];
        try
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Dictionary<Task<LibraryRootWalk>, string> listing = [];
            foreach (string folder in folders)
            {
                listing[WalkAsync(new LibraryRootWalk(folder, FolderReader), ct)] = folder;
            }

            List<(LibraryRootWalk Walk, bool Reached)> settled = [];
            while (listing.Count > 0)
            {
                Task<LibraryRootWalk> finished = await Task.WhenAny(listing.Keys).ConfigureAwait(false);
                listing.Remove(finished);
                LibraryRootWalk walk = await finished.ConfigureAwait(false);
                bool reached = Settle(walk);
                settled.Add((walk, reached));

                // Show what is listed so far without waiting for slower folders; their entries stay as they are.
                if (listing.Count > 0 && walk.Demos.Count > 0)
                {
                    ScanScope partial = Fold(settled);
                    ContentGroups shown = ResolveContentIdentities(partial.Files);
                    string[] stillListing = [.. listing.Values.Select(KnownRoot)];
                    await PostAsync(() => Reconcile(shown.Primaries, shown.ShadowFolders, needMap, [], null, stillListing,
                            null, null))
                        .ConfigureAwait(false);
                    RaiseChanged();
                }
            }

            scope = Fold(settled);
            AppLog.LibraryScanListed(DiagLog, scope.Files.Count, scope.ReachedRoots.Length, folders.Length,
                clock.ElapsedMilliseconds);

            groups = ResolveContentIdentities(scope.Files);
            ct.ThrowIfCancellationRequested();
            await PostAsync(() => Reconcile(groups.Primaries, groups.ShadowFolders, needMap, needFull, scope, [],
                    groups.Unresolved, deferred))
                .ConfigureAwait(false);
            RaiseChanged();

            await IndexAsync(needMap, needFull, true, ct).ConfigureAwait(false);

            int pending;
            lock (_tier2Lock)
            {
                pending = _pendingFull.Count;
            }

            AppLog.LibraryScanFinished(DiagLog, scope.Files.Count, groups.Unresolved.Count, pending, clock.ElapsedMilliseconds);

            // Not awaited: on a network folder the reads can take a while, and nothing but the files read waits on them.
            if (groups.Unresolved.Count > 0)
            {
                CopiesResolved = ResolveCopiesAsync(scope.Files, groups, deferred, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (Scheduler is null)
        {
            // No scheduler (tests that only list folders): tier 2 is left pending; nothing reads a demo outside the queue.
            Save();
            RaiseChanged();
        }
        else if (groups.Unresolved.Count == 0)
        {
            try
            {
                await SaveScanAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the newer rescan saves
            }
        }
    }

    // A scan's attaches and prunes change only the unified cache's index, and a scan that enlists no parse
    // reaches no other save before the app closes. Without a scheduler the caller saves.
    private async Task SaveScanAsync(CancellationToken ct)
    {
        if (Scheduler is null)
        {
            Save();
            return;
        }

        await QueueWork.Run(Queue, QueueJobKind.LibraryListing, "Library: save the scan", "library", _ => Save())
            .WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The latest scan's copy detection; completes when its copies are resolved or it is cancelled. Test seam.</summary>
    internal Task CopiesResolved { get; private set; } = Task.CompletedTask;

    // Fingerprints the files the scan could not place, and the known demos they may be copies of, then places
    // them again. Cache rows the prune held back for them go once they had their chance to match.
    private async Task ResolveCopiesAsync(List<(string Path, long Size, DateTime Modified)> files, ContentGroups groups,
        List<string> deferred, CancellationToken ct)
    {
        try
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            List<(string Path, long Size, DateTime Modified)> reads =
                [.. files.Where(f => groups.Unresolved.Contains(f.Path)), .. groups.KnownToFingerprint];
            HashSet<string> unread;
            using (_demoCache?.BeginBatch())
            {
                unread = await FingerprintAsync(reads, ct).ConfigureAwait(false);
            }

            ContentGroups placed = ResolveContentIdentities(files);
            List<DemoEntry> needMap = [], needFull = [];
            await PostAsync(() => Reconcile(placed.Primaries, placed.ShadowFolders, needMap, needFull, null, [], unread, null))
                .ConfigureAwait(false);
            RaiseChanged();
            await IndexAsync(needMap, needFull, false, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_demoCache is not null && deferred.Count > 0)
            {
                using (_demoCache.BeginBatch())
                {
                    foreach (string path in deferred)
                    {
                        _demoCache.Detach(path);
                    }
                }
            }

            AppLog.LibraryCopiesResolved(DiagLog, reads.Count - unread.Count, files.Count - placed.Primaries.Count,
                clock.ElapsedMilliseconds);
            await SaveScanAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // a newer rescan resolves them
        }
    }

    // Tier 1 then tier 2, a folder at a time: a folder's full parses are enlisted once its headers are read,
    // so a folder whose reads stopped answering puts no parse of its demos on the heavy lane this scan.
    private async Task IndexAsync(List<DemoEntry> needMap, List<DemoEntry> needFull, bool replace, CancellationToken ct)
    {
        Func<string, string> folderOfPath = FolderLookup();
        string FolderOf(DemoEntry entry) => folderOfPath(entry.FilePath);

        Dictionary<string, Queue<DemoEntry>> headers = new(StringComparer.Ordinal);
        foreach (DemoEntry entry in needMap.Distinct())
        {
            string folder = FolderOf(entry);
            if (!headers.TryGetValue(folder, out Queue<DemoEntry>? pending))
            {
                headers[folder] = pending = new Queue<DemoEntry>();
            }

            pending.Enqueue(entry);
        }

        needMap.Clear();
        ILookup<string, DemoEntry> full = needFull.ToLookup(FolderOf, StringComparer.Ordinal);
        if (replace)
        {
            KeepOnlyInTier2Backlog(needFull);
        }

        EnlistTier2([.. full.Where(g => !headers.ContainsKey(g.Key)).SelectMany(g => g)]);

        await Task.WhenAll(headers.Select(async pair =>
        {
            if (await ReadHeadersAsync(pair.Key, pair.Value, ct).ConfigureAwait(false))
            {
                EnlistTier2([.. full[pair.Key]]);
            }
            else
            {
                HoldBackTier2(full[pair.Key]);
            }
        })).ConfigureAwait(false);
        RaiseChanged();
    }

    // Maps a demo path to the registered folder it was listed under, from the roots as last resolved; a path
    // under none maps to its own directory.
    private Func<string, string> FolderLookup()
    {
        (string Folder, string Root)[] roots = [.. _folderSnapshot.Select(f => (f, KnownRoot(f)))];
        return path =>
        {
            foreach ((string folder, string root) in roots)
            {
                if (IsUnder(path, root))
                {
                    return folder;
                }
            }

            return Path.GetDirectoryName(path) ?? "";
        };
    }

    private async Task<bool> ReadHeadersAsync(string folder, Queue<DemoEntry> pending, CancellationToken ct)
    {
        bool answered = await ReadEachAsync(folder, "Library: read demo headers in ", pending,
                new ReadSlice<DemoEntry, LibraryDemoHeader?>(), _outstandingHeaders, e => e.FilePath,
                e => ReadHeader(e.FilePath),
                (entry, header) =>
                {
                    if (header is not null)
                    {
                        ApplyHeader(entry, header);
                    }
                },
                (left, reason) => AppLog.LibraryHeadersLeftUnread(DiagLog, folder, left, reason), ct)
            .ConfigureAwait(false);
        if (answered)
        {
            RaiseChanged();
        }

        return answered;
    }

    // One folder's reads of one kind, a light queue slice at a time with one read in flight, the same shape as
    // the walk. False when a read gave no answer in time. Whatever was not read stays in pending and the slice.
    private async Task<bool> ReadEachAsync<TItem, TResult>(string folder, string label, Queue<TItem> pending,
        ReadSlice<TItem, TResult> slice, Dictionary<string, (Task Answer, DateTime Started)> outstanding,
        Func<TItem, string> pathOf, Func<TItem, TResult> read, Action<TItem, TResult> apply,
        Action<int, string> leftUnread, CancellationToken ct)
    {
        if (Outstanding(outstanding, folder) is { } earlier
            && !await AnswersInTimeAsync(earlier.Answer, ct, earlier.Started).ConfigureAwait(false))
        {
            leftUnread(pending.Count, $"a read from an earlier scan has not answered in {Timing.NoAnswer.TotalSeconds:0.#} s");
            return false;
        }

        while (pending.Count > 0 || slice.InFlight is not null)
        {
            ct.ThrowIfCancellationRequested();
            SliceEnd end = SliceEnd.Stopped;
            await QueueWork.Run(Queue, QueueJobKind.LibraryListing, label + folder, "library",
                    jobCt =>
                    {
                        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, jobCt);
                        end = RunReadSlice(folder, pending, slice, outstanding, read, apply, linked.Token);
                    })
                .WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            int left = pending.Count + (slice.InFlight is null ? 0 : 1);
            if (end == SliceEnd.Stopped)
            {
                leftUnread(left, "they were stopped in the queue");
                break;
            }

            if (end == SliceEnd.Waiting && slice.InFlight is { } waiting
                && !await AnswersInTimeAsync(waiting.Answer, ct, waiting.Started).ConfigureAwait(false))
            {
                leftUnread(left, $"{pathOf(waiting.Item)} gave no answer in {Timing.NoAnswer.TotalSeconds:0.#} s");
                return false;
            }
        }

        return true;
    }

    // Runs inside a light queue item; ends after the slice budget, or when a read has not answered within the
    // in-job wait and the caller waits for it outside the queue.
    private SliceEnd RunReadSlice<TItem, TResult>(string folder, Queue<TItem> pending, ReadSlice<TItem, TResult> slice,
        Dictionary<string, (Task Answer, DateTime Started)> outstanding, Func<TItem, TResult> read,
        Action<TItem, TResult> apply, CancellationToken ct)
    {
        System.Diagnostics.Stopwatch budget = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (slice.InFlight is null)
                {
                    if (!pending.TryDequeue(out TItem? next))
                    {
                        return SliceEnd.Done;
                    }

                    Task<TResult> started = Task.Run(() => read(next), CancellationToken.None);
                    DateTime at = DateTime.UtcNow;
                    slice.InFlight = (next, started, at);
                    lock (outstanding)
                    {
                        outstanding[folder] = (started, at);
                    }
                }

                (TItem item, Task<TResult> answer, _) = slice.InFlight.Value;
                if (!answer.Wait(Timing.InJobWait, ct))
                {
                    return SliceEnd.Waiting;
                }

                slice.InFlight = null;
                apply(item, answer.Result);

                if (budget.Elapsed >= Timing.SliceBudget)
                {
                    return pending.Count == 0 ? SliceEnd.Done : SliceEnd.Yielded;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return SliceEnd.Stopped;
        }
    }

    // Fingerprints, a folder at a time like the header reads. Returns the files left unread because a read gave
    // no answer or was stopped; every other file was read, whether or not it gave a fingerprint.
    private async Task<HashSet<string>> FingerprintAsync(List<(string Path, long Size, DateTime Modified)> files,
        CancellationToken ct)
    {
        Func<string, string> folderOf = FolderLookup();
        HashSet<string> unread = new(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(files.GroupBy(f => folderOf(f.Path), StringComparer.Ordinal).Select(async folder =>
        {
            Queue<(string Path, long Size, DateTime Modified)> pending = new(folder);
            ReadSlice<(string Path, long Size, DateTime Modified), DemoContentFingerprint?> slice = new();
            await ReadEachAsync(folder.Key, "Library: fingerprint demos in ", pending, slice, _outstandingFingerprints,
                    f => f.Path, f => ReadFingerprint(f.Path), ApplyFingerprint,
                    (left, reason) => AppLog.LibraryFingerprintsLeftUnread(DiagLog, folder.Key, left, reason), ct)
                .ConfigureAwait(false);
            lock (unread)
            {
                unread.UnionWith(pending.Select(f => f.Path));
                if (slice.InFlight is { } stuck)
                {
                    unread.Add(stuck.Item.Path);
                }
            }
        })).ConfigureAwait(false);
        return unread;
    }

    private DemoContentFingerprint? ReadFingerprint(string path)
    {
        try
        {
            return ContentReader.Fingerprint(path, Time);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // A known demo's confirmed path gives the row its missing fingerprint; any other file's is kept for matching.
    private void ApplyFingerprint((string Path, long Size, DateTime Modified) file, DemoContentFingerprint? fingerprint)
    {
        if (fingerprint is null || fingerprint.Size != file.Size)
        {
            return;
        }

        if (_demoCache?.LocationOf(file.Path) is { Location.Confirmed: true } at
            && at.Location.Size == file.Size && at.Location.ModifiedTicks == file.Modified.Ticks)
        {
            _demoCache.SetFingerprint(file.Path, fingerprint);
            return;
        }

        lock (_fingerprints)
        {
            _fingerprints[file.Path] = (file.Size, file.Modified.Ticks, fingerprint);
        }
    }

    // A read that throws has no header to show; tier 2 decides whether the file is a readable demo.
    private LibraryDemoHeader? ReadHeader(string path)
    {
        try
        {
            return HeaderReader.Read(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // Tier 2 (full parse -> players/duration). Each parse holds a whole demo in RAM, so it runs one demo at a
    // time under the machine-wide invariant. The backlog's membership is this evaluator's Wants gate; the
    // scheduler plans ONE visit per demo carrying every interested pass, and the queue owns the rest.
    private void EnlistTier2(List<DemoEntry> needFull)
    {
        if (Scheduler is not { } scheduler || needFull.Count == 0)
        {
            return;
        }

        lock (_tier2Lock)
        {
            foreach (DemoEntry entry in needFull)
            {
                _pendingFull[entry.FilePath] = entry;
                _awaitingIndex.Remove(entry.FilePath);
            }
        }

        foreach (DemoEntry entry in needFull)
        {
            DemoEntry captured = entry;

            // Only a row with nothing to show gets the "being analyzed" signal at SUBMIT time. The real one is
            // posted when the parse actually starts (IndexTier2Core); DemoEntry.IsIndexing is unique, and the
            // half-score repair enlists already-indexed rows that must not all pulse at once.
            if (captured.State != DemoIndexState.Indexed)
            {
                _post(() => captured.State = DemoIndexState.Indexing);
            }

            scheduler.DemoChanged(captured.FilePath);
        }
    }

    // A full scan's backlog is what it found owed; rows an earlier scan enlisted and this one did not drop out.
    private void KeepOnlyInTier2Backlog(List<DemoEntry> wanted)
    {
        if (Scheduler is null)
        {
            return;
        }

        HashSet<string> keep = new(wanted.Select(e => e.FilePath), StringComparer.OrdinalIgnoreCase);
        lock (_tier2Lock)
        {
            foreach (string path in _pendingFull.Keys.Where(p => !keep.Contains(p)).ToList())
            {
                _pendingFull.Remove(path);
            }
        }
    }

    // Owed to the next scan instead of enlisted: the parse would read a folder that just stopped answering.
    private void HoldBackTier2(IEnumerable<DemoEntry> entries)
    {
        List<DemoEntry> idle = [];
        lock (_tier2Lock)
        {
            foreach (DemoEntry entry in entries)
            {
                _pendingFull.Remove(entry.FilePath);
                _awaitingIndex.Add(entry.FilePath);
                if (!_tier2InProgress.Contains(entry.FilePath))
                {
                    idle.Add(entry);
                }
            }
        }

        foreach (DemoEntry entry in idle)
        {
            _post(() =>
            {
                if (entry.State == DemoIndexState.Indexing)
                {
                    entry.State = DemoIndexState.Pending;
                }
            });
        }
    }

    // One folder's walk, a light queue slice at a time. The blocking call runs on the pool so a slice can stop
    // waiting for it; while it is outstanding no queue item is running for this folder.
    private async Task<LibraryRootWalk> WalkAsync(LibraryRootWalk walk, CancellationToken ct)
    {
        // A read an earlier walk left behind answers before this folder is touched again, so a hung mount
        // costs one blocked thread rather than one per rescan.
        if (Outstanding(_outstanding, walk.Folder) is { } earlier
            && !await AnswersInTimeAsync(earlier.Answer, ct, earlier.Started).ConfigureAwait(false))
        {
            walk.GiveUp($"a read from an earlier scan has not answered in {Timing.NoAnswer.TotalSeconds:0.#} s");
            return walk;
        }

        WalkSlice slice = new();
        while (!walk.Done)
        {
            ct.ThrowIfCancellationRequested();
            SliceEnd end = SliceEnd.Stopped;
            await QueueWork.Run(Queue, QueueJobKind.LibraryListing, "Library: list " + walk.Folder, "library", jobCt =>
                {
                    using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, jobCt);
                    end = RunSlice(walk, slice, linked.Token);
                })
                .WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (end == SliceEnd.Stopped)
            {
                walk.GiveUp("its listing was stopped in the queue");
            }
            else if (end == SliceEnd.Waiting && slice.InFlight is { } waiting
                     && !await AnswersInTimeAsync(waiting.Answer, ct, waiting.Started).ConfigureAwait(false))
            {
                walk.GiveUp($"{waiting.Read.Directory} gave no answer in {Timing.NoAnswer.TotalSeconds:0.#} s");
                RescanOnLateAnswer(walk.Folder, waiting.Answer);
            }
        }

        return walk;
    }

    // Runs inside a light queue item. Ends after the slice budget, or when a read has not answered within the
    // in-job wait; the caller then waits for it outside the queue.
    private SliceEnd RunSlice(LibraryRootWalk walk, WalkSlice slice, CancellationToken ct)
    {
        System.Diagnostics.Stopwatch budget = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (slice.InFlight is null)
                {
                    if (walk.Next() is not { } read)
                    {
                        return SliceEnd.Done;
                    }

                    Task<LibraryRootWalk.Answer> started = Task.Run(() => walk.Execute(read), CancellationToken.None);
                    DateTime at = DateTime.UtcNow;
                    slice.InFlight = (read, started, at);
                    lock (_outstanding)
                    {
                        _outstanding[walk.Folder] = (started, at);
                    }
                }

                (LibraryRootWalk.Read pending, Task<LibraryRootWalk.Answer> answer, _) = slice.InFlight.Value;
                if (!answer.Wait(Timing.InJobWait, ct))
                {
                    return SliceEnd.Waiting;
                }

                slice.InFlight = null;
                walk.Complete(pending, answer.Result);
                if (budget.Elapsed >= Timing.SliceBudget)
                {
                    return walk.Done ? SliceEnd.Done : SliceEnd.Yielded;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return SliceEnd.Stopped;
        }
    }

    // True when the read answers within the no-answer bound counted from when it started.
    private async Task<bool> AnswersInTimeAsync(Task answer, CancellationToken ct, DateTime? started = null)
    {
        TimeSpan left = Timing.NoAnswer - (DateTime.UtcNow - (started ?? DateTime.UtcNow));
        if (left > TimeSpan.Zero && !answer.IsCompleted)
        {
            await Task.WhenAny(answer, Task.Delay(left, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        return answer.IsCompleted;
    }

    private static (Task Answer, DateTime Started)? Outstanding(Dictionary<string, (Task Answer, DateTime Started)> reads,
        string folder)
    {
        lock (reads)
        {
            if (!reads.TryGetValue(folder, out (Task Answer, DateTime Started) read))
            {
                return null;
            }

            if (read.Answer.IsCompleted)
            {
                reads.Remove(folder);
                return null;
            }

            return read;
        }
    }

    // A late answer to a read that timed out means the folder is up now; it is listed again.
    private void RescanOnLateAnswer(string folder, Task<LibraryRootWalk.Answer> answer) =>
        _ = answer.ContinueWith(t =>
            {
                if (!_disposed && t.Result.Error is null && _folderSnapshot.Contains(folder))
                {
                    _post(() => _ = RescanAsync());
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private enum SliceEnd
    {
        Done,
        Yielded,
        Waiting,
        Stopped
    }

    // The read a walk has handed to the pool and not yet applied.
    private sealed class WalkSlice
    {
        public (LibraryRootWalk.Read Read, Task<LibraryRootWalk.Answer> Answer, DateTime Started)? InFlight { get; set; }
    }

    // The read a folder's pass has handed to the pool and not yet applied.
    private sealed class ReadSlice<TItem, TResult>
    {
        public (TItem Item, Task<TResult> Answer, DateTime Started)? InFlight { get; set; }
    }

    // Shared tier-2 body for the retained and forward entry points. The in-progress guard dedupes two
    // callers racing the same path: the second observes the path in flight and bails, leaving the first to
    // index + clear the backlog.
    private void RunTier2(string path, Action<DemoEntry> index)
    {
        DemoEntry? entry;
        lock (_tier2Lock)
        {
            if (!_tier2InProgress.Add(path))
            {
                return; // a replay for this exact path is already running: skip the duplicate
            }

            _pendingFull.TryGetValue(path, out entry);
        }

        try
        {
            if (entry is not null)
            {
                index(entry);
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
    ///     The current tier-2 backlog paths, a worker-readable snapshot (never enumerate the UI-bound
    ///     <see cref="Entries" /> off-thread).
    /// </summary>
    public IReadOnlyList<string> Tier2Backlog()
    {
        lock (_tier2Lock)
        {
            return [.. _pendingFull.Keys];
        }
    }

    // Removes a path from the tier-2 backlog (worker thread, under the lock) and, when the backlog drains,
    // persists the tail (the every-12 Save in IndexTier2Core can leave <12 demos only in the in-memory cache).
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

    // A folder counts as REACHED only when its whole walk completed. That distinction is the whole safety
    // property of the stale-row prune below: a folder on a detached volume, an automount that is not up yet,
    // or a listing that failed partway looks exactly like a folder whose demos were deleted, if all one has
    // is the file list.
    //
    // Records a finished walk's real path, logs its outcome, and says whether it was reached.
    private bool Settle(LibraryRootWalk walk)
    {
        if (walk.Root is { } root)
        {
            lock (_resolvedRoots)
            {
                _resolvedRoots[walk.Folder] = root;
            }
        }

        if (!walk.Reached)
        {
            AppLog.LibraryFolderUnreached(DiagLog, walk.Folder, walk.UnreachedReason ?? "its listing did not finish");
            return false;
        }

        foreach ((string directory, string reason) in walk.UnreachedDirectories)
        {
            AppLog.LibraryDirectoryUnreached(DiagLog, walk.Folder, directory, reason);
        }

        if (walk.Demos.Count == 0 && CachedRowsUnder(walk.Root!) is var cached and > 0)
        {
            AppLog.LibraryFolderListedEmpty(DiagLog, walk.Folder, cached);
            return false;
        }

        AppLog.LibraryFolderListed(DiagLog, walk.Folder, walk.Demos.Count, (long)walk.Elapsed.TotalMilliseconds);
        return true;
    }

    // Folds settled walks into one listing and the prune's scope.
    private ScanScope Fold(List<(LibraryRootWalk Walk, bool Reached)> walks)
    {
        List<(string Path, long Size, DateTime Modified)> files = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> reached = [], unreachedDirectories = [], known = [];
        bool everyRootKnown = true;

        foreach ((LibraryRootWalk walk, bool isReached) in walks)
        {
            known.Add(walk.Root ?? KnownRoot(walk.Folder));
            // Rows sit under the folder's real path, which nothing has told this session yet.
            everyRootKnown &= walk.Root is not null || walk.Missing || HasResolved(walk.Folder);

            // Overlapping registrations list the same file twice; the first listing keeps it.
            foreach ((string Path, long Size, DateTime Modified) demo in walk.Demos)
            {
                if (seen.Add(demo.Path))
                {
                    files.Add(demo);
                }
            }

            if (isReached)
            {
                reached.Add(walk.Root!);
                unreachedDirectories.AddRange(walk.UnreachedDirectories.Select(d => d.Directory));
            }
        }

        return new ScanScope(files, [.. reached], [.. unreachedDirectories], [.. known], everyRootKnown);
    }

    // A registered folder's real path as last resolved, without touching the file system.
    private string KnownRoot(string folder)
    {
        lock (_resolvedRoots)
        {
            return _resolvedRoots.TryGetValue(folder, out string? root) ? root : FullPathOrSelf(folder);
        }
    }

    private bool HasResolved(string folder)
    {
        lock (_resolvedRoots)
        {
            return _resolvedRoots.ContainsKey(folder);
        }
    }

    // Rows in either cache under a folder's real path.
    private int CachedRowsUnder(string root)
    {
        HashSet<string> rows = new(StringComparer.OrdinalIgnoreCase);
        lock (_cacheLock)
        {
            rows.UnionWith(_cache.Keys.Where(p => IsUnder(p, root)));
        }

        if (_demoCache is not null)
        {
            rows.UnionWith(_demoCache.Index.Select(e => e.Path).Where(p => IsUnder(p, root)));
        }

        return rows.Count;
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string FullPathOrSelf(string folder)
    {
        try
        {
            return Path.GetFullPath(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return folder;
        }
    }

    /// <summary>What one scan listed, and what it may conclude from what it did not list.</summary>
    /// <param name="Files">Every demo listed, copies included.</param>
    /// <param name="ReachedRoots">Real paths of the folders whose whole walk completed.</param>
    /// <param name="UnreachedDirectories">Subdirectories of reached folders that could not be listed.</param>
    /// <param name="KnownRoots">Every registered folder's real path, or its full path when never resolved.</param>
    /// <param name="EveryRootKnown">False when a folder that is there has never resolved to its real path.</param>
    private sealed record ScanScope(
        List<(string Path, long Size, DateTime Modified)> Files,
        string[] ReachedRoots,
        string[] UnreachedDirectories,
        string[] KnownRoots,
        bool EveryRootKnown);

    /// <summary>
    ///     Drops metadata rows for demos that are provably gone. <see cref="Reconcile" /> has always dropped
    ///     the UI <see cref="Entries" /> for a vanished file but never the persisted <c>_cache</c> row behind
    ///     it, so the cache only ever grew: on the reference library 354 of 719 rows described files that no
    ///     longer existed, 332 of them under a folder the user had since removed from the library entirely.
    ///     <para>
    ///         <b>"The file wasn't found" is NOT sufficient evidence.</b> A folder on a detached volume, an
    ///         automount that is not up yet, or a listing that failed partway all look exactly like a folder
    ///         whose demos were deleted. A row is therefore dropped only when one of two things is true:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             it sits under a folder whose whole walk completed this scan, outside every subdirectory
    ///             that walk could not list, and the walk did not find it; or
    ///         </item>
    ///         <item>
    ///             it sits under no registered folder at all, out of scope, so nothing can ever index it
    ///             again without the user re-adding the folder, which re-indexes anyway.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         A folder that listed no demos while rows sit under it counts as not reached, which covers an
    ///         empty mount point. A row copied elsewhere in the library is alive: its cached hash is what keeps
    ///         the copy detection from re-reading both files on the next scan. A path a fingerprint attached is
    ///         pruned by the same rules, though it has no metadata row.
    ///     </para>
    ///     <para>
    ///         A unified cache row the size of a file still waiting for its fingerprint may be that file's
    ///         content before a move: it is put in <paramref name="deferred" /> rather than dropped, so the
    ///         fingerprint can still match it.
    ///     </para>
    ///     <para>
    ///         In the unified cache a prune only takes the path out of its demo's row
    ///         (<see cref="DemoCacheStore.Detach" />). A demo left with no path is orphaned and keeps its analysis
    ///         for the grace period; the metadata row here goes either way.
    ///     </para>
    /// </summary>
    private void PruneStaleCacheRows(ScanScope scope, HashSet<long> unplacedSizes, List<string>? deferred)
    {
        // No reachable root at all means the whole library is offline (every volume detached, or the very
        // first construction before any scan). Pruning then would delete everything.
        if (scope.ReachedRoots.Length == 0)
        {
            return;
        }

        HashSet<string> alive = new(scope.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        List<string> doomed = [];
        List<string> unconfirmed = [.. (_demoCache?.KnownContents() ?? [])
            .SelectMany(c => c.Locations.Where(l => !l.Confirmed).Select(l => l.Path))];
        lock (_cacheLock)
        {
            foreach (string path in _cache.Keys.Concat(unconfirmed).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (alive.Contains(path) || scope.UnreachedDirectories.Any(d => IsUnder(path, d)))
                {
                    continue;
                }

                bool underReached = scope.ReachedRoots.Any(r => IsUnder(path, r));
                bool outOfScope = scope.EveryRootKnown && !scope.KnownRoots.Any(r => IsUnder(path, r));
                if (underReached || outOfScope)
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

        // The unified cache only lets go of the paths: a demo whose last path went is kept as an orphan, so a
        // folder that comes back within the grace period takes its analysis back unparsed. One batch, since
        // consumers re-project wholesale per change.
        if (_demoCache is not null)
        {
            using (_demoCache.BeginBatch())
            {
                foreach (string path in doomed)
                {
                    if (deferred is not null && _demoCache.LocationOf(path) is { } at && unplacedSizes.Contains(at.Location.Size))
                    {
                        deferred.Add(path);
                    }
                    else
                    {
                        _demoCache.Detach(path);
                    }
                }
            }
        }

        // Diagnostics pillar, not Console (v0.6.0). Console is invisible in a windowed Release build.
        AppLog.LibraryCachePruned(DiagLog, doomed.Count);
    }

    // Resolves a registered folder to its real, normalized absolute path, following a DIRECTORY
    // symlink to its final target, so a symlink-to-a-folder (or a nested/relative/trailing-slash
    // registration) enumerates the same file identities as the real folder. Best-effort: on any error
    // the input is returned unchanged (the scan still works, just without that canonicalization).
    internal static string CanonicalizeDirectory(string folder)
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
    // content copy) is caught by the Phase-4 content hash. Best-effort: returns the input on error.
    internal static string CanonicalizePath(string path)
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

    // ── Content dedup: collapse COPIES at different real paths ─────────────────────
    // Canonical-path dedup already folds the SAME physical file reached different ways; this catches copies
    // (same bytes, distinct real paths). A file's content id is its full hash when something has read it,
    // else the content its fingerprint matched; two files with no content id group only while their
    // fingerprints match. Only a file no row lists, sharing its size with another listed file or a known
    // demo, needs a fingerprint at all (equal bytes mean equal size).
    // Returns the primaries (one per group: a confirmed path before an unconfirmed one, then the ordinally
    // smallest, the store's own pick) plus, per primary, the OTHER folders holding a copy.
    //
    // Reads no file. A file that needs a fingerprint it does not have yet is Unresolved and stands alone until
    // FingerprintAsync has read it; KnownToFingerprint names a listed path of each known demo with no
    // fingerprint stored that such a file could be a copy of.
    private ContentGroups ResolveContentIdentities(List<(string Path, long Size, DateTime Modified)> files)
    {
        Dictionary<DemoContentFingerprint, string> contentByFingerprint = new();
        HashSet<long> knownSizes = [];
        List<IReadOnlyList<DemoLocation>> withoutFingerprint = [];
        foreach ((string id, DemoContentFingerprint? fingerprint, IReadOnlyList<DemoLocation> locations) in
                 _demoCache?.KnownContents() ?? [])
        {
            if (fingerprint is not null)
            {
                contentByFingerprint.TryAdd(fingerprint, id);
                knownSizes.Add(fingerprint.Size);
            }
            else
            {
                knownSizes.UnionWith(locations.Where(l => l.Confirmed).Select(l => l.Size));
                withoutFingerprint.Add(locations);
            }
        }

        Dictionary<long, int> countBySize = new();
        foreach ((string _, long size, DateTime _) in files)
        {
            countBySize[size] = countBySize.GetValueOrDefault(size) + 1;
        }

        Dictionary<string, (string Key, bool Confirmed)> keyByPath = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> unresolved = new(StringComparer.OrdinalIgnoreCase);
        // One change event for the scan's attaches, not one per file.
        using IDisposable? batch = _demoCache?.BeginBatch();
        foreach ((string path, long size, DateTime modified) in files)
        {
            if (KnownIdentity(path, size, modified) is { } known)
            {
                keyByPath[path] = ("sha:" + known.ContentId, known.Confirmed);
            }
            else if (SessionFingerprint(path, size, modified) is { } fingerprint)
            {
                keyByPath[path] = contentByFingerprint.TryGetValue(fingerprint, out string? id)
                                  && _demoCache!.AttachUnconfirmed(id, path, size, modified.Ticks)
                    ? ("sha:" + id, false)
                    : ($"fp:{fingerprint.Size}:{fingerprint.WindowBytes}:{fingerprint.Head}:{fingerprint.Tail}", false);
            }
            else
            {
                keyByPath[path] = ("path:" + path, false);
                if ((countBySize[size] >= 2 || knownSizes.Contains(size)) && _demoCache?.TryGetIndex(path) is null)
                {
                    unresolved.Add(path);
                }
            }
        }

        Dictionary<string, List<(string Path, long Size, DateTime Modified)>> groups = new(StringComparer.Ordinal);
        foreach ((string Path, long Size, DateTime Modified) file in files)
        {
            string key = keyByPath[file.Path].Key;
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
            // Deterministic and stable across runs, so the same copy stays the card whatever the listing order.
            list.Sort((a, b) => keyByPath[a.Path].Confirmed != keyByPath[b.Path].Confirmed
                ? keyByPath[a.Path].Confirmed ? -1 : 1
                : string.Compare(a.Path, b.Path, StringComparison.Ordinal));
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

        HashSet<long> unresolvedSizes = [.. files.Where(f => unresolved.Contains(f.Path)).Select(f => f.Size)];
        Dictionary<string, (string Path, long Size, DateTime Modified)> listed = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string Path, long Size, DateTime Modified) file in files)
        {
            listed.TryAdd(file.Path, file);
        }

        List<(string, long, DateTime)> knownToFingerprint = [];
        foreach (IReadOnlyList<DemoLocation> locations in withoutFingerprint)
        {
            foreach (DemoLocation location in locations.Where(l => l.Confirmed && unresolvedSizes.Contains(l.Size)))
            {
                if (listed.TryGetValue(location.Path, out (string Path, long Size, DateTime Modified) file)
                    && file.Size == location.Size && file.Modified.Ticks == location.ModifiedTicks)
                {
                    knownToFingerprint.Add(file);
                    break;
                }
            }
        }

        return new ContentGroups(primaries, shadowFolders, unresolved, knownToFingerprint);
    }

    /// <summary>How one scan's listing groups by content.</summary>
    /// <param name="Primaries">One file per group: the card.</param>
    /// <param name="ShadowFolders">Per primary, the other folders that hold a copy.</param>
    /// <param name="Unresolved">Files that stand alone until they have a fingerprint.</param>
    /// <param name="KnownToFingerprint">A listed confirmed path of each known demo an unresolved file may copy.</param>
    private sealed record ContentGroups(
        List<(string Path, long Size, DateTime Modified)> Primaries,
        Dictionary<string, IReadOnlyList<string>> ShadowFolders,
        HashSet<string> Unresolved,
        List<(string Path, long Size, DateTime Modified)> KnownToFingerprint);

    // The content id a listed file already has: its full hash from the metadata row, or the store's row for
    // the path while the size and write time are the ones it was listed at. A hash on the metadata row the
    // store does not list the path under is a full read, so it confirms the path there. A path a fingerprint
    // attached whose file has since changed leaves its row, and is matched again like any new file.
    private (string ContentId, bool Confirmed)? KnownIdentity(string path, long size, DateTime modified)
    {
        (string ContentId, DemoLocation Location)? at = _demoCache?.LocationOf(path);
        if (at is null && _demoCache?.Reattach(path, size, modified.Ticks) == true)
        {
            at = _demoCache.LocationOf(path);
        }

        if (CachedSha(path, size, modified) is { } sha)
        {
            if (_demoCache is not null && at?.Location.Confirmed != true)
            {
                if (at is not null)
                {
                    _demoCache.ConfirmLocation(path, sha, size, modified.Ticks);
                }

                if (_demoCache.LocationOf(path) is null && _demoCache.AttachUnconfirmed(sha, path, size, modified.Ticks))
                {
                    _demoCache.ConfirmLocation(path, sha, size, modified.Ticks);
                }
            }

            return (sha, true);
        }

        if (at is not { } row)
        {
            return null;
        }

        if (row.Location.Size == size && row.Location.ModifiedTicks == modified.Ticks)
        {
            return (row.ContentId, row.Location.Confirmed);
        }

        if (!row.Location.Confirmed)
        {
            _demoCache!.Detach(path);
        }

        return null;
    }

    private DemoContentFingerprint? SessionFingerprint(string path, long size, DateTime modified)
    {
        lock (_fingerprints)
        {
            return _fingerprints.TryGetValue(path, out (long Size, long Ticks, DemoContentFingerprint Fingerprint) read)
                   && read.Size == size && read.Ticks == modified.Ticks
                ? read.Fingerprint
                : null;
        }
    }

    private string? CachedSha(string path, long size, DateTime modified)
    {
        lock (_cacheLock)
        {
            return _cache.TryGetValue(path, out DemoLibraryCacheEntry? c)
                   && c.Size == size && c.ModifiedTicks == modified.Ticks
                ? c.Sha256
                : null;
        }
    }

    // The SHA-256 (lowercase hex) of a file at the size and write time it was listed at: from the metadata row,
    // from a confirmed path of the cache, or from the read a parse or an open just made. Without one, and only
    // when no parse hashes what it reads, the bytes are streamed once more for the hash and the fingerprint.
    // Null when none of that gives one. A reconcile caller then treats the file as its own singleton (never
    // wrongly deduped); the tier-2 caller leaves the cache record's hash as it was. Cached is true when no
    // fingerprint came with the hash.
    private (string? Sha256, DemoContentFingerprint? Fingerprint, bool Cached) GetOrComputeSha(string path,
        long size, DateTime modified)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(path, out DemoLibraryCacheEntry? c)
                && c.Size == size && c.ModifiedTicks == modified.Ticks && c.Sha256 is not null)
            {
                return (c.Sha256, null, true);
            }
        }

        string? sha = null;
        DemoContentFingerprint? fingerprint = null;
        bool cached = false;
        if (_demoCache?.RecentContentRead(path, size, modified.Ticks) is { } read)
        {
            (sha, fingerprint) = read;
        }
        else if (_demoCache?.LocationOf(path) is { Location: { Confirmed: true } at } known
                 && at.Size == size && at.ModifiedTicks == modified.Ticks)
        {
            (sha, cached) = (known.ContentId, true);
        }
        else if (!ParseHashesContent)
        {
            (sha, fingerprint) = ContentReader.HashAndFingerprint(path, Time);
        }

        if (sha is not null)
        {
            UpsertCache(path, c => c.Sha256 = sha);
        }

        return (sha, fingerprint?.Size == size ? fingerprint : null, cached);
    }

    // The fingerprint to store beside a hash tier 2 took from the metadata row. Null when the record already
    // holds one for that hash, so a re-index reads nothing extra.
    private DemoContentFingerprint? FingerprintForCachedSha(DemoEntry entry, string sha)
    {
        if (_demoCache is null
            || _demoCache.TryGetIndex(entry.FilePath) is { ContentFingerprint: not null } row
            && row.MatchesFile(entry.FileSizeBytes, entry.Modified.Ticks)
            && string.Equals(row.Sha256, sha, StringComparison.Ordinal))
        {
            return null;
        }

        // The parse has just streamed the file, so both windows are read from the page cache.
        DemoContentFingerprint? fingerprint = ContentReader.Fingerprint(entry.FilePath, Time);
        return fingerprint?.Size == entry.FileSizeBytes ? fingerprint : null;
    }

    // Runs on the post (UI) thread. `primaries` are the deduped demos (one per content group);
    // `shadowFolders[primaryPath]` lists the other folders that hold a byte-identical copy of that primary.
    private void Reconcile(
        List<(string Path, long Size, DateTime Modified)> primaries,
        Dictionary<string, IReadOnlyList<string>> shadowFolders,
        List<DemoEntry> needMap, List<DemoEntry> needFull, ScanScope? scope, string[] keepUnder,
        HashSet<string>? holdBack, List<string>? deferred)
    {
        Dictionary<string, (long Size, DateTime Modified)> wanted = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, long size, DateTime modified) in primaries)
        {
            wanted[path] = (size, modified);
        }

        // Drop entries whose file no longer exists, moved out of scope, became a SHADOW (a smaller-path
        // copy appeared and took over as primary: this card collapses into that one), or changed size or
        // mtime since it was added. A changed file comes back below as a new entry and is indexed again.
        // One Reset for the lot: every collection change re-runs the Library tab's filters, sort and
        // provenance over the whole library.
        // Entries under a folder still being listed are left exactly as they are.
        // A card filled from a content row stands only while the store still lists its path under that content,
        // and a card still waiting on its parse is filled instead once the store knows the content.
        bool Unchanged(DemoEntry e) =>
            wanted.TryGetValue(e.FilePath, out (long Size, DateTime Modified) now)
                ? now.Size == e.FileSizeBytes && now.Modified.Ticks == e.Modified.Ticks && SameContent(e)
                : keepUnder.Any(r => IsUnder(e.FilePath, r));

        bool SameContent(DemoEntry e) => e.FilledFrom is { } id
            ? _demoCache?.LocationOf(e.FilePath)?.ContentId == id
            : e.State == DemoIndexState.Indexed || StoredParse(e.FilePath, e.FileSizeBytes, e.Modified) is null;

        List<DemoEntry> staying = [.. Entries.Where(Unchanged)];
        lock (_tier2Lock)
        {
            foreach (DemoEntry gone in Entries.Where(e => !Unchanged(e)))
            {
                _pendingFull.Remove(gone.FilePath);
                _awaitingIndex.Remove(gone.FilePath);
            }
        }

        foreach (DemoEntry changed in Entries.Where(e => wanted.ContainsKey(e.FilePath) && !Unchanged(e)))
        {
            Scheduler?.ForgetFaults(changed.FilePath);
        }

        if (staying.Count == Entries.Count - 1)
        {
            Entries.Remove(Entries.First(e => !Unchanged(e)));
        }
        else if (staying.Count < Entries.Count)
        {
            Entries.ReplaceAll(staying);
        }

        if (scope is not null)
        {
            HashSet<long> unplacedSizes = [.. primaries.Where(p => holdBack?.Contains(p.Path) == true).Select(p => p.Size)];
            PruneStaleCacheRows(scope, unplacedSizes, deferred);
        }

        DropRekeyedCacheRows(wanted);

        Dictionary<string, DemoEntry> byPath = Entries.ToDictionary(e => e.FilePath, StringComparer.OrdinalIgnoreCase);

        List<DemoEntry> added = new();
        foreach ((string path, (long size, DateTime modified)) in wanted)
        {
            IReadOnlyList<string> dupFolders = shadowFolders.TryGetValue(path, out IReadOnlyList<string>? f) ? f : [];

            if (byPath.TryGetValue(path, out DemoEntry? kept))
            {
                // Already present (kept across rescans): just refresh its copy set (a twin may have
                // appeared or vanished since the last scan).
                if (!kept.DuplicateFolders.SequenceEqual(dupFolders, StringComparer.OrdinalIgnoreCase))
                {
                    kept.DuplicateFolders = dupFolders;
                }

                // Still owed its indexing: a rescan this one replaced added it, or its tier 2 has not run.
                bool owed;
                lock (_tier2Lock)
                {
                    owed = _awaitingIndex.Contains(path) || _pendingFull.ContainsKey(path);
                }

                if (owed)
                {
                    if (kept.MapName is null)
                    {
                        needMap.Add(kept);
                    }

                    // A possible copy waits for its hash, so only one of a pair is ever parsed.
                    if (holdBack?.Contains(path) != true)
                    {
                        needFull.Add(kept);
                    }
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
            if (cached is not { FullyIndexed: true } && StoredParse(path, size, modified) is { } stored)
            {
                // A copy the store already knows, by hash or by fingerprint: its card needs no read of this file.
                ApplyIndexRow(entry, stored.Row);
                entry.FilledFrom = stored.ContentId;
            }
            else if (cached is not null)
            {
                ApplyCache(entry, cached);
            }

            added.Add(entry);

            if (entry.MapName is null)
            {
                needMap.Add(entry);
            }

            // Full parse needed when the demo isn't indexed yet, OR it's indexed from an OLD cache row that
            // predates the score field (opportunistic backfill, see [[project_demo_library_browser]]): the
            // players/duration stay visible from cache while the score fills in, no full-cache wipe.
            //
            // What this deliberately does NOT sweep: a row REPAIRED at load. It keeps
            // ScoreComputed = true and carries ScoreRepairPending instead, so it does not land here. On the
            // reference library that was 342 demos / ~100 GB of background re-parsing, automatically, on the
            // first launch after upgrade. RepairPendingScoresAsync clears the flag-holders into this backlog
            // when the user asks for it; the card says so in the meantime (DemoEntry.NeedsScoreRepair).
            //
            // What this deliberately does NOT re-index: a row whose ROSTER is names-only because it predates
            // the tier-2 extension. That state is absent, not wrong, and it is already labelled as absent:
            // HasTeamSplit reads false and Match Overview offers a per-demo re-index (LegacyCacheMigration
            // documents that choice). Sweeping it automatically would turn a bounded repair of rows that
            // render incorrectly into a fresh full-library re-parse of ~575 demos on the next launch.
            //
            // A row the unified cache holds no parse stamp for is re-indexed too: the Library is the only pass
            // that writes the stamp, and every pass that runs after it waits on it.
            if (entry.FilledFrom is null
                && (entry.State != DemoIndexState.Indexed || cached is not { ScoreComputed: true } || !HasParseStamp(path)))
            {
                if (holdBack?.Contains(path) != true)
                {
                    needFull.Add(entry);
                }

                lock (_tier2Lock)
                {
                    _awaitingIndex.Add(path);
                }
            }
        }

        // Single Reset event: a large folder adds hundreds of entries, and per-add notifications make
        // every bound consumer (VM filter pass + ItemsControl containers) re-run once PER ENTRY.
        Entries.AddRange(added);

        // Index newest-first, the browser's default sort, so the top of the visible list gets its
        // players/score first while the long tail fills in behind it.
        needMap.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        needFull.Sort((a, b) => b.Modified.CompareTo(a.Modified));

        int backfill = needFull.Count(e => e.State == DemoIndexState.Indexed);
        if (backfill > 0)
        {
            AppLog.LibraryScoreBackfill(DiagLog, backfill);
        }
    }

    private bool HasParseStamp(string path) => _demoCache is null || _demoCache.TryGetIndex(path) is { ParseSchema: > 0 };

    // The parsed content row the store lists this path under, seen from the path, while the file is the one
    // the store last saw there.
    private (string ContentId, DemoCacheIndexEntry Row)? StoredParse(string path, long size, DateTime modified) =>
        _demoCache?.LocationOf(path) is { } at && at.Location.Size == size && at.Location.ModifiedTicks == modified.Ticks
                                     && _demoCache.TryGetIndex(path) is { ParseSchema: > 0 } row
            ? (at.ContentId, row)
            : null;

    // ApplyCache's twin for a card filled from the unified cache's row rather than library.json's.
    private static void ApplyIndexRow(DemoEntry entry, DemoCacheIndexEntry row)
    {
        entry.MapName = row.Map;
        entry.ServerName = row.Server;
        entry.DemoVersion = row.DemoVersion;
        entry.Players = row.PlayerNames;
        entry.DurationSeconds = row.DurationSeconds;
        entry.RoundCount = row.RoundCount;
        if (IsScoreResultCoherent(row.CtScore, row.TScore, row.CtClan, row.TClan))
        {
            entry.CtScore = row.CtScore;
            entry.TScore = row.TScore;
            entry.CtClan = row.CtClan;
            entry.TClan = row.TClan;
        }

        entry.State = DemoIndexState.Indexed;
    }

    private static void ApplyCache(DemoEntry entry, DemoLibraryCacheEntry cached)
    {
        entry.MapName = cached.Map;
        entry.ServerName = cached.Server;
        entry.DemoVersion = cached.DemoVersion;

        // A stale half-resolved score is refused HERE, at the read boundary, rather than being repaired into
        // the cache row.
        //
        // The obvious alternative, clear the row at hydrate and mark it, is a trap, and it is worth
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

    private void ApplyHeader(DemoEntry entry, LibraryDemoHeader header)
    {
        _post(() =>
        {
            entry.MapName = header.MapName;
            entry.ServerName = header.ServerName;
            entry.DemoVersion = header.DemoVersion;
        });

        UpsertCache(entry.FilePath, c =>
        {
            c.Map = header.MapName;
            c.Server = header.ServerName;
            c.DemoVersion = header.DemoVersion;
        });
    }

    // Post-parse tier-2 extraction (players / duration / map / final score) + cache write. Runs with the
    // ParsedDemo held, inside the queue's gate slot.
    // Self-contained failure handling so a throw marks ONLY this row Failed.
    //
    // Internal so the real-demo test can drive one entry through it without a folder scan, which would
    // mean linking or copying a demo into a temp library.
    internal void IndexTier2Core(DemoEntry entry, ParsedDemo parsed) =>
        IndexTier2Core(entry, new Tier2Input(
            parsed.Players.Values, parsed.Duration.TotalSeconds, parsed.MapName, parsed.ServerName,
            parsed.Profile.SourceKind.ToString(), parsed.TickRate, parsed.TickCount, parsed.ServerStartTick,
            () => ClipRounds.Derive(parsed),
            () => ExtractFinalState(parsed)));

    // The same extraction off a forward pass: the pass already replayed the final state and derived the rounds.
    internal void IndexTier2Core(DemoEntry entry, ForwardDemoResult pass) =>
        IndexTier2Core(entry, new Tier2Input(
            pass.Demo.Players.Values, pass.Demo.Duration.TotalSeconds, pass.Demo.MapName, pass.Demo.ServerName,
            pass.Demo.Profile.SourceKind.ToString(), pass.Demo.TickRate, pass.Demo.TickCount, pass.Demo.ServerStartTick,
            () => pass.Rounds,
            () => pass.FinalState is { } s
                ? (s.Ct, s.T, s.CtClan, s.TClan, s.CoachSlots)
                : throw new InvalidOperationException("the forward pass read no final state")));

    private sealed record Tier2Input(
        IEnumerable<PlayerInfo> Players,
        double DurationSeconds,
        string? MapName,
        string ServerName,
        string SourceKind,
        int TickRate,
        int TickCount,
        int ServerStartTick,
        Func<IReadOnlyList<ClipRound>> Rounds,
        Func<(int? Ct, int? T, string? CtClan, string? TClan, HashSet<int> CoachSlots)> FinalState);

    private void IndexTier2Core(DemoEntry entry, Tier2Input parsed)
    {
        List<string> players;
        double duration;
        string? map;
        int? ctScore = null, tScore = null;
        string? ctClan = null, tClan = null;
        HashSet<int>? coachSlots = null;
        try
        {
            players = parsed.Players
                // IsHltv as well as IsBot: the GOTV proxy holds a userinfo slot with a name, and
                // before the CS2-path fakeplayer/ishltv read it landed on library cards and in the
                // player filter as if it were someone who played.
                .Where(p => !p.IsBot && !p.IsHltv && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => p.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            duration = parsed.DurationSeconds;
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

            // Final score: CCSTeam.m_iScore at match end (no cheap event source exists in CS2, team_score /
            // round_end are absent). Best-effort: a replay failure just leaves the score unset.
            try
            {
                (ctScore, tScore, ctClan, tClan, coachSlots) = parsed.FinalState();
            }
            catch (Exception)
            {
                // score stays null; players/duration are already posted
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
        // silent: HasScore needs BOTH sides, so the card just quietly loses its badge, and it persists
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
        // Leaving it set when the extractor returned nothing would make the card ask forever for
        // work that has already been done.
        if (entry.ScoreRepairPending)
        {
            _post(() => entry.ScoreRepairPending = false);
        }

        // Rounds + the richer roster are a PARSE product, already in hand, projected ONCE here for both
        // consumers rather than twice. The library row wants the round COUNT, which nothing ever wrote:
        // every cached row carried RoundCount = 0, and the legacy migration faithfully carried that zero
        // into the unified cache. The unified cache wants the boundaries themselves.
        //
        // Isolated in its own try because a projection failure must leave the row INDEXED. The demo
        // parsed fine, and players/duration are already posted to the card.
        List<CachedPlayerInfo>? cachedPlayers = null;
        List<CachedRound>? rounds = null;
        try
        {
            (cachedPlayers, rounds) = ProjectTier2(parsed.Players, parsed.Rounds(), coachSlots);
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

        // The content hash, for EVERY demo rather than only the size collisions reconcile hashes. The
        // user-truth stores (annotations, breakpoints, tags) key on it, and a demo they can find again
        // after a move or a re-download is only one the index already knows by content. Tier 2 is the
        // pass that has just streamed the whole file through the parser, so the second read is served
        // warm from the page cache; the (path,size,mtime) row makes a rescan free.
        (string? sha, DemoContentFingerprint? fingerprint, bool cached) =
            GetOrComputeSha(entry.FilePath, entry.FileSizeBytes, entry.Modified);
        if (sha is not null)
        {
            // Before the write: a path a fingerprint attached to other content must leave it first, or the
            // write would start from that content's record.
            _demoCache?.ConfirmLocation(entry.FilePath, sha, entry.FileSizeBytes, entry.Modified.Ticks);
        }

        if (sha is not null && cached)
        {
            fingerprint = FingerprintForCachedSha(entry, sha);
        }

        WriteTier2ToDemoCache(entry, parsed, map, duration, ctScore, tScore, ctClan, tClan, cachedPlayers, rounds,
            sha, fingerprint);

        // Persist periodically so a long scan's progress survives an app close, and nudge the VM so
        // the player/map filters grow during a long sequential scan (its end may be an hour away).
        if (Interlocked.Increment(ref _enrichedSinceSave) % 12 == 0)
        {
            Save();
            RaiseChanged();
        }
    }

    // The TIER-2 EXTENSION. Everything written here is
    // already in hand at this point in the pass: PlayerInfo is (Slot, Name, SteamId64, UserId, Team, IsBot)
    // plus IsHltv, and ParsedDemo exposes TickCount/TickRate/Duration, so the library cache storing NAMES
    // ONLY was a choice, not a cost. Capturing the rest (~+0.5 KB/demo) is what lets Match Overview render
    // rosters split by team, bot tags, accurate player/spectator counts and tick rate from cache alone, for
    // the ~80% of a real library this pass has already covered.
    //
    // Deliberately excludes anything needing the rules engine (scoreboard, per-side split, highlights):
    // that is tier 3, and it stays behind an explicit per-demo action.
    //
    // Fully defensive: a cache-write failure must never mark the LIBRARY row failed, exactly as a highlight
    // scan failure does not.
    //
    // players/rounds arrive PRE-PROJECTED (ProjectTier2 runs in the caller) because the library row needs the
    // round count too, and this method no-ops entirely when the unified cache is absent. Projecting here
    // would have made the count unobtainable on exactly the path that was writing zeros. Null means the
    // projection threw; every other field is still worth writing.
    private void WriteTier2ToDemoCache(DemoEntry entry, Tier2Input parsed, string? map, double duration,
        int? ctScore, int? tScore, string? ctClan, string? tClan,
        List<CachedPlayerInfo>? players, List<CachedRound>? rounds, string? sha256,
        DemoContentFingerprint? fingerprint)
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

                // Null only when the file could not be read back: keep whatever the record holds rather
                // than erasing a key some sidecar may already be joined on.
                if (sha256 is not null)
                {
                    record.SetContentHash(sha256, fingerprint);
                }

                record.Server = parsed.ServerName;
                // The header classifier's verdict, stored by name: what Demo Provenance Labels reads
                // as "matchmaking" without opening the file again.
                record.SourceKind = parsed.SourceKind;
                record.DurationSeconds = duration;
                record.TickRate = parsed.TickRate;
                record.TickCount = parsed.TickCount;
                record.ServerStartTick = parsed.ServerStartTick;

                // Null only when the projection threw: keep what the record already holds rather than
                // replacing a real roster with an empty one.
                if (players is not null)
                {
                    record.Players = players;
                }

                // RoundCount tracks the boundaries whenever we HAVE boundaries. It exists for migrated rows
                // that carry a count with nothing behind it, and a re-index that wrote Rounds but left the
                // migrated count alone would leave the record contradicting itself, masked today only
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
            // Rebuildable cache: the library row stands on its own.
        }
    }

    /// <summary>
    ///     The tier-2 projection: roster and round boundaries out of a parsed demo, in the exact shape the
    ///     unified cache stores. Internal so it can be asserted directly against a real demo: the invariant
    ///     that matters (cached player count agrees with the cached rosters) is one this codebase has broken
    ///     before, when counting every named entry reported 13 players above rosters of ten.
    /// </summary>
    internal static (List<CachedPlayerInfo> Players, List<CachedRound> Rounds) ProjectTier2(ParsedDemo parsed) =>
        ProjectTier2(parsed, null);

    /// <param name="parsed">The parse.</param>
    /// <param name="coachSlots">
    ///     Slots whose controller coached a team at the last frame (<see cref="ExtractFinalState" />); null
    ///     when the replay did not run, which reads as "no coach", the matchmaking truth.
    /// </param>
    internal static (List<CachedPlayerInfo> Players, List<CachedRound> Rounds) ProjectTier2(ParsedDemo parsed,
        IReadOnlySet<int>? coachSlots) =>
        ProjectTier2(parsed.Players.Values, ClipRounds.Derive(parsed), coachSlots);

    private static (List<CachedPlayerInfo> Players, List<CachedRound> Rounds) ProjectTier2(
        IEnumerable<PlayerInfo> roster, IReadOnlyList<ClipRound> clipRounds, IReadOnlySet<int>? coachSlots)
    {
        List<CachedPlayerInfo> players =
        [
            .. roster
                // The GOTV proxy holds a userinfo slot with a name but never played; excluding it here is
                // what makes the cached player count agree with the cached rosters. Bots and spectators ARE
                // kept, with their team. The projection decides how to present them, and it cannot recover
                // a distinction the cache threw away.
                .Where(p => !p.IsHltv && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new CachedPlayerInfo
                {
                    Slot = p.Slot,
                    Name = p.Name, // RAW: sanitize at the render boundary only
                    SteamId64 = p.SteamId64.ToString(CultureInfo.InvariantCulture),
                    Team = p.Team,
                    IsBot = p.IsBot,
                    IsCoach = coachSlots?.Contains(p.Slot) ?? false
                })
        ];

        // Round boundaries are a PARSE product, not an analysis one, and there is ONE deriver for them
        // now: CS2DemoKit.Analysis.Clips.ClipRounds, in the FRAME clock.
        //
        // Careful: CS2 DOES NOT EMIT round_start. It opens a round with round_freeze_end and closes it with
        // round_officially_ended. Matching the string "round_start" (as the highlights scanner did)
        // therefore yields an EMPTY list on every CS2 demo, which is exactly what the measured cache
        // showed: zero rounds on every row, including the ones that had actually been scanned.
        //
        // GameTick, not ServerTick: this field is FRAME CLOCK, and the clip math that consumes it
        // (ClipWindows.RoundStartFor) is frame clock throughout. Never offset it by ServerStartTick:
        // DemoAnalyzer's own round list is the ABSOLUTE-clock variant and is not interchangeable here.
        List<CachedRound> rounds = clipRounds.ToCachedRounds();

        return (players, rounds);
    }

    // Reads the authoritative final scoreboard: CCSTeam.m_iScore per side (CT = team 3, T = team 2) plus clan
    // names, entity-replayed to the last frame, exactly what the app's own UpdateGameInfo treats as the score.
    // Correct for complete demos; on demos truncated at the buzzer the winner's final-round increment can be
    // absent from the recorded frames (unrecoverable, no event carries the final score), matching what the app
    // itself would display. Returns nulls for warmup-only / team-less demos so the card omits the score.
    internal static (int? Ct, int? T, string? CtClan, string? TClan) ExtractFinalScore(ParsedDemo parsed)
    {
        (int? ct, int? t, string? ctClan, string? tClan, HashSet<int> _) = ExtractFinalState(parsed);
        return (ct, t, ctClan, tClan);
    }

    // The score plus the coach slots, from one replay: the coach flag is CCSPlayerController.m_iCoachingTeam
    // at the last frame, read off the controller seated at entity index slot + 1, so a registered coach
    // (who sits on a side without being one of its five) stays out of Team Identity's side keys. Coach
    // slots are returned even when the score is not, so a warmup-only demo still marks its coach.
    internal static (int? Ct, int? T, string? CtClan, string? TClan, HashSet<int> CoachSlots) ExtractFinalState(
        ParsedDemo parsed)
    {
        IReadOnlyList<DemoFrame> frames = parsed.Frames;
        if (frames.Count == 0)
        {
            return (null, null, null, null, []);
        }

        // Replays every delta from frame 0 but stores only the two classes the read looks at: the same
        // score as a full replay (EntityStoreFilterEquivalenceTests), ~1.2x faster and 30-60% less allocation.
        EntityTracker tracker = FinalTeamState.NewTracker();
        tracker.ReplayToIndex(frames.Count - 1, frames);
        FinalTeamState state = FinalTeamState.Read(tracker, parsed.Players.Keys);
        return (state.Ct, state.T, state.CtClan, state.TClan, state.CoachSlots);
    }

    /// <summary>
    ///     Is this score/clan tuple one <see cref="ExtractFinalScore" /> could actually have produced?
    ///     <para>
    ///         The extractor is BOTH-OR-NOTHING: it returns all four nulls unless it resolved a score for
    ///         team 2 AND team 3 with a non-zero sum, and it only ever reaches the clan reads on that same
    ///         path. So "CT 16, T null" is a state the current code cannot emit, yet real caches are full of
    ///         it (on the reference library 555 rows carry a CT score and 3 carry the T score), left behind by
    ///         an older model whose <c>TScore</c>/<c>TClan</c> properties were renamed to <c>Score</c>/
    ///         <c>Clan</c> in commit eb79e1e. Every already-written row silently stopped deserializing its T
    ///         side, and <c>ScoreComputed = true</c> meant nothing ever recomputed it.
    ///     </para>
    ///     <para>
    ///         <b>This predicate is the whole loop guard</b>, so its exact shape matters. It flags only states
    ///         the extractor CANNOT produce, which makes the repair self-terminating by construction: whatever
    ///         a re-derivation writes is coherent, so a repaired row is never suspect a second time, no
    ///         "already tried" bookkeeping needed. In particular a single-clan result (both scores, one clan
    ///         name) is legitimate, HLTV demos where only one side set a clan tag, and is NOT flagged.
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
            return ct + t > 0; // both sides resolved: clans may legitimately be one-sided or absent
        }

        // Neither side resolved. The extractor bails BEFORE it can have kept a clan, so a clan without a
        // score is the same stale half-result as a score without its other half.
        return ctScore is null && tScore is null
                               && string.IsNullOrWhiteSpace(ctClan) && string.IsNullOrWhiteSpace(tClan);
    }

    // Does this hydrated row hold a score the extractor could not have produced? PURE: it deliberately
    // mutates nothing (see the note in ApplyCache for why repairing the row in place is unsafe). The row is
    // left exactly as written and the half score is refused at the read boundary instead.
    private static bool HasIncoherentScore(DemoLibraryCacheEntry row) =>
        row.FullyIndexed && !IsScoreResultCoherent(row.CtScore, row.Score, row.CtClan, row.Clan);

    // CCSTeam scalars arrive boxed (Int32 on the wire per project_cs2_wire_encoding); coerce defensively.
    // ── Cache (thread-safe) ───────────────────────────────────────────────────

    // A row keyed to an older size or mtime describes bytes that are gone. UpsertCache never re-keys an
    // existing row, so it must go before the file is indexed again, or the new results land on the old key.
    private void DropRekeyedCacheRows(Dictionary<string, (long Size, DateTime Modified)> wanted)
    {
        lock (_cacheLock)
        {
            foreach ((string path, (long size, DateTime modified)) in wanted)
            {
                if (_cache.TryGetValue(path, out DemoLibraryCacheEntry? c)
                    && (c.Size != size || c.ModifiedTicks != modified.Ticks))
                {
                    _cache.Remove(path);
                }
            }
        }
    }

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
    // authoritative folder source, so this never mutates Folders itself.
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

            // Count only: the rows are hydrated exactly as written. A score that violates
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
                // scan, so from the second launch onwards the two numbers converge, which is why the figure
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
        // deferred, so it rides the same checkpoints library.json uses, including the every-12-demos save
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
            AtomicFile.WriteAllText(_dataPath, JsonSerializer.Serialize(data, _jsonOptions));
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
