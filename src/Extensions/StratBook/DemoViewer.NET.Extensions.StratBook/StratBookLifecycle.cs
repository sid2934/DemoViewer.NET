#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's startup loads, its in-session release and its shutdown flushes. <see cref="OnEnabledAsync" />
///     is the explicit startup block <c>App.axaml.cs</c> used to run by hand and runs the same way for a
///     switch-on mid-session: the loads and the <see cref="IPackResident.Attach" /> calls are queue items.
///     <see cref="OnDisabledAsync" /> cancels the pack's queue items by owner and releases every resident as
///     one queue item, so a large index never leaves memory on the UI thread.
///     <para>
///         Every item of the pack's own shares one serial, so loads, attaches and releases are totally
///         ordered, and each carries the epoch it was queued under: a release still queued when the user
///         switches back on, or a load still queued when the user switches off, runs after the newer
///         transition's bump and does nothing. Nothing of the pack's state changes outside those items.
///     </para>
/// </summary>
internal sealed class StratBookLifecycle : IPackLifecycle
{
    /// <summary>The owner tag of the pack's own queue items; also the serial its loads and release share.</summary>
    internal const string Owner = StratBookPack.PackFeatureId;

    /// <summary>The release item's title, pinned by a test.</summary>
    internal const string ReleaseTitle = "Strat Book: release memory";

    /// <summary>The attach item's title (the subscriptions that are not a load), pinned by a test.</summary>
    internal const string AttachTitle = "Strat Book: attach services";

    /// <summary>
    ///     Every owner tag a pack job or parse attachment carries, cancelled together on disable. No
    ///     user-truth store saves travel under these: the Tag Store and the Strat Store write inline, the
    ///     Review Queue ("review") is core. "demo-cache" is the sidecar migrations, submitted only here.
    /// </summary>
    internal static readonly string[] OwnerTags =
    [
        "situations", "utility", "lineup-clips", "strat-mining", "strats", "suggested", "suggested-inbox", "tags", "dossier",
        "teams", "demo-cache",
        RoundFactsEvaluator.EvaluatorId, RoundIndexEvaluator.EvaluatorId, SuggestedTagsService.EvaluatorId,
        GrenadeIndexEvaluator.EvaluatorId,
        Owner
    ];

    private readonly IServiceProvider _sp;
    private readonly StratBookPackInstances _instances;
    private readonly TimeSpan _migrationDelay;

    // Bumped by every enable and disable; an item queued under an older value does nothing when it runs.
    private int _epoch;

    /// <param name="sp">The composition root, resolved from the same container <see cref="StratBookPack.Register" /> fed.</param>
    /// <param name="instances">The pack's live-state tracker, so shutdown never constructs a store that nothing opened.</param>
    /// <param name="migrationDelay">
    ///     How long after enabling the one-off sidecar migrations wait; 30 seconds in production, overridable
    ///     so a test can pin their labels without waiting.
    /// </param>
    public StratBookLifecycle(IServiceProvider sp, StratBookPackInstances instances, TimeSpan? migrationDelay = null)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(instances);
        _sp = sp;
        _instances = instances;
        _migrationDelay = migrationDelay ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    /// <remarks>Completes when the loads and the attach have run (or were dropped). The migrations are not awaited.</remarks>
    public Task OnEnabledAsync(PackStartReason reason, CancellationToken ct)
    {
        int epoch = Interlocked.Increment(ref _epoch);
        IDemoProcessingQueue? queue = _sp.GetService<IDemoProcessingQueue>();

        // A release still queued from the switch-off this enable follows goes now; one already running
        // finishes first (the serial) and the items below rebuild after it; one this cancel missed sees the
        // newer epoch and does nothing.
        queue?.CancelOwned(Owner);

        // Resolved here, attached in the item below: a first build subscribes in its constructor, every
        // later enable through Attach, and the item keeps either ordered after a release still queued. It
        // goes first on the serial so Team Identity's file read (the Library team filter) does not wait
        // behind both index loads.
        LineupClipService lineups = _sp.GetRequiredService<LineupClipService>();
        TeamIdentityService teams = _sp.GetRequiredService<TeamIdentityService>();
        TagFactsRefresher tagFacts = _sp.GetRequiredService<TagFactsRefresher>();
        // The zone graphs are read per map by the loads below and by nothing outside the pack.
        IPackResident? zones = _sp.GetService<IZonePlaceResolverSource>() as IPackResident;

        Task attach = PackItem(queue, QueueJobKind.SectionCompute, AttachTitle, Owner, epoch, () =>
        {
            lineups.Attach();

            // Team Identity's startup: Attach schedules its file read as a queue item, once; StartAsync then
            // rebuilds from the sidecars when team-index.json is missing or behind, else runs the
            // index-versus-cache diff. Off the UI thread; the tab reads whatever is there meanwhile.
            teams.Attach();
            _ = teams.StartAsync();

            tagFacts.Attach();
            if (zones is not null)
            {
                _instances.Record(zones);
            }

            // After a release the typed view is empty; the residents are the same objects. Everything built
            // lazily before the release (Watched Situations, Strat Mining) re-attaches here too.
            _instances.Restore();
            foreach (IPackResident resident in _instances.Residents)
            {
                resident.Attach();
            }
        }, ct);

        // The situation index's startup load: every current sidecar, as a queue item at the front (2 ms per
        // demo measured). Queries before it finishes answer empty with IsReady false and the strip says so.
        SituationIndex situations = _sp.GetRequiredService<SituationIndex>();
        Task situationsLoad = PackItem(queue, QueueJobKind.StoreLoad, "Load: situations index", "situations", epoch, situations.Load, ct);

        // The grenade index's startup load: every current rows sibling; the Utility Book says it is reading.
        GrenadeIndex grenadeIndex = _sp.GetRequiredService<GrenadeIndex>();
        Task grenadesLoad = PackItem(queue, QueueJobKind.StoreLoad, "Load: grenade index", "utility", epoch, grenadeIndex.Load, ct);

        // One-off re-encode of pre-gzip record and grenade sidecars: no parse, a processing queue item that
        // steps aside between batches, marker-gated once a pass converts everything it found. Queued after
        // the delay so startup loads are not competing for the disk.
        if (!OperatingSystem.IsBrowser() && queue is not null)
        {
            DemoCacheStore demoCache = _sp.GetRequiredService<DemoCacheStore>();
            _ = Task.Delay(_migrationDelay, ct).ContinueWith(_ =>
                {
                    SidecarFormatMigration.Submit(queue, demoCache,
                        [demoCache.ConvertLegacyRecord, path => GrenadeSidecar.ConvertLegacy(demoCache, path)]);

                    // Grenade rows to throw logs and one flight per lineup position; see GrenadeStoreMigration.
                    GrenadeStoreMigration.Submit(queue, demoCache, grenadeIndex);
                },
                ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        return Task.WhenAll(attach, situationsLoad, grenadesLoad);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The evaluators already stop by predicate. Every queued pack item goes by owner tag (a running one
    ///     finishes its unit); the release itself is a user-priority queue item sharing the loads' serial, so
    ///     it runs after a load still in flight and never beside one, and does nothing when an enable has
    ///     bumped the epoch since.
    /// </remarks>
    public Task OnDisabledAsync()
    {
        int epoch = Interlocked.Increment(ref _epoch);
        IDemoProcessingQueue? queue = _sp.GetService<IDemoProcessingQueue>();
        if (queue is not null)
        {
            foreach (string owner in OwnerTags)
            {
                queue.CancelOwned(owner);
            }
        }

        return QueueWork.Run(queue, QueueJobKind.SectionCompute, ReleaseTitle, Owner, _ =>
        {
            if (Volatile.Read(ref _epoch) == epoch)
            {
                Release();
            }
        }, DemoJobPriority.UserRequested, serial: Owner);
    }

    // Dependents first (reverse build order): a watcher leaves the index before the index empties and raises.
    private void Release()
    {
        ILogger log = DiagnosticsLog.CreateLogger(AppLog.ShellCategory);
        foreach (IPackResident resident in _instances.Residents.Reverse())
        {
            try
            {
                resident.Release();
            }
            catch (Exception ex)
            {
                AppLog.OperationFailed(log, "strat book release", ex);
            }
        }

        _instances.Clear();
    }

    /// <inheritdoc />
    public void OnShutdown(TimeSpan budget)
    {
        // Each guarded by the tracker, not a fresh resolve: GetService on a singleton factory constructs
        // it, which is exactly what a never-opened pack must not do at shutdown. The strat commit runs
        // first (it is the user's own work, written nowhere else) and each flush is isolated so the
        // lineup flush still runs even if committing the open strat throws.
        ILogger log = DiagnosticsLog.CreateLogger(AppLog.ShellCategory);
        try
        {
            // StratBookModule.Shutdown is itself a no-op when its tab was never activated.
            if (_sp.GetService<ModuleRegistry>()?.Modules.OfType<StratBookModule>().FirstOrDefault() is { } stratBook)
            {
                stratBook.Shutdown();
            }
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(log, "strat commit on shutdown", ex);
        }

        try
        {
            _instances.Grenades?.FlushLineups(budget);
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(log, "grenade lineup flush on shutdown", ex);
        }

        // The Tag Store defers its index to shutdown; idempotent, so a re-fired request
        // writes nothing new.
        try
        {
            _instances.Tags?.SaveIndex();
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(log, "tag index flush on shutdown", ex);
        }
    }

    // One of the pack's own items: a light queue item ahead of background work, so it shows in the queue
    // list, on the serial that orders it against every other pack item. It does nothing when the enable it
    // belongs to was cancelled or a later transition bumped the epoch, whether or not the queue dropped it.
    private Task PackItem(IDemoProcessingQueue? queue, QueueJobKind kind, string title, string owner, int epoch,
        Action work, CancellationToken ct) =>
        QueueWork.Run(queue, kind, title, owner, _ =>
        {
            if (!ct.IsCancellationRequested && Volatile.Read(ref _epoch) == epoch)
            {
                work();
            }
        }, DemoJobPriority.UserRequested, serial: Owner);
}
