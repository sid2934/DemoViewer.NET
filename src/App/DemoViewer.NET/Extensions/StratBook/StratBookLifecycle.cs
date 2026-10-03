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
///     switch-on mid-session: resolving a service is itself the subscription on first build, and
///     <see cref="IPackResident.Attach" /> is the subscription on every later enable. <see cref="OnDisabledAsync" />
///     cancels the pack's queue items by owner and releases every resident as one queue item, so a large
///     index never leaves memory on the UI thread.
/// </summary>
internal sealed class StratBookLifecycle : IPackLifecycle
{
    /// <summary>The owner tag of the pack's own queue items; also the serial its loads and release share.</summary>
    internal const string Owner = StratBookPack.PackFeatureId;

    /// <summary>The release item's title, pinned by a test.</summary>
    internal const string ReleaseTitle = "Strat Book: release memory";

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
    /// <remarks>Completes when the loads have run (or were cancelled). The migrations are not awaited.</remarks>
    public Task OnEnabledAsync(PackStartReason reason, CancellationToken ct)
    {
        IDemoProcessingQueue? queue = _sp.GetService<IDemoProcessingQueue>();

        // The situation index's startup load: every current sidecar, as a queue item at the front (2 ms per
        // demo measured). Queries before it finishes answer empty with IsReady false and the strip says so.
        SituationIndex situations = _sp.GetRequiredService<SituationIndex>();
        Task situationsLoad = StartupLoad(queue, "Load: situations index", "situations", situations.Load, ct);

        // The grenade index's startup load: every current rows sibling; the Utility Book says it is reading.
        GrenadeIndex grenadeIndex = _sp.GetRequiredService<GrenadeIndex>();
        Task grenadesLoad = StartupLoad(queue, "Load: grenade index", "utility", grenadeIndex.Load, ct);

        // Nothing resolves the lineup clip service; constructing it is what subscribes it to the index.
        _sp.GetRequiredService<LineupClipService>().Attach();

        // Team Identity's startup: its file read as a queue item (Attach schedules it once; the first build
        // already did when the pack was on at startup), then a rebuild from the sidecars when team-index.json
        // is missing or behind, else the index-versus-cache diff. Off the UI thread; the tab reads whatever
        // is there meanwhile.
        TeamIdentityService teams = _sp.GetRequiredService<TeamIdentityService>();
        teams.Attach();
        Task teamsStart = teams.StartAsync();

        // Nothing resolves the facts refresher; constructing it is what subscribes it to the rows writes.
        _sp.GetRequiredService<TagFactsRefresher>().Attach();

        // The zone graphs are read per map by the loads above and by nothing outside the pack, so they
        // are the pack's to release even though the source is registered by the composition root.
        if (_sp.GetService<IZonePlaceResolverSource>() is IPackResident zones)
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

        return Task.WhenAll(situationsLoad, grenadesLoad, teamsStart);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The evaluators already stop by predicate. Every queued pack item goes by owner tag (a running one
    ///     finishes its unit); the release itself is a user-priority queue item sharing the loads' serial, so
    ///     it runs after a load still in flight and never beside one.
    /// </remarks>
    public Task OnDisabledAsync()
    {
        IDemoProcessingQueue? queue = _sp.GetService<IDemoProcessingQueue>();
        if (queue is not null)
        {
            foreach (string owner in OwnerTags)
            {
                queue.CancelOwned(owner);
            }
        }

        return QueueWork.Run(queue, QueueJobKind.SectionCompute, ReleaseTitle, Owner, _ => Release(),
            DemoJobPriority.UserRequested, serial: Owner);
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
    }

    // A store's startup read: a light queue item ahead of background work, so it shows in the queue list.
    // Mirrors App.StartupLoad; kept local so the pack does not reach back into App for a private helper.
    // The serial keeps a release from running beside it. The token is the enable's: cancelled, the item
    // does nothing when the queue did not drop it first.
    private static Task StartupLoad(IDemoProcessingQueue? queue, string title, string owner, Action load, CancellationToken ct) =>
        QueueWork.Run(queue, QueueJobKind.StoreLoad, title, owner, _ =>
        {
            if (!ct.IsCancellationRequested)
            {
                load();
            }
        }, DemoJobPriority.UserRequested, serial: Owner);
}
