#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     The pack's startup loads and shutdown flushes, run only while <c>pack.stratbook</c> resolves on.
///     <see cref="OnEnabledAsync" /> is the explicit startup block <c>App.axaml.cs</c> used to run by
///     hand; resolving each service here is itself the subscription (the index loads once, the facts
///     refresher starts watching writes), so there is nothing further to await.
/// </summary>
internal sealed class StratBookLifecycle : IPackLifecycle
{
    private readonly IServiceProvider _sp;
    private readonly StratBookPackInstances _instances;
    private readonly TimeSpan _migrationDelay;

    /// <param name="sp">The composition root, resolved from the same container <see cref="StratBookPack.Register" /> fed.</param>
    /// <param name="instances">The pack's was-built tracker, so shutdown never constructs a store that nothing opened.</param>
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
    public Task OnEnabledAsync(PackStartReason reason, CancellationToken ct)
    {
        // The situation index's startup load: every current sidecar, as a queue item at the front (2 ms per
        // demo measured). Queries before it finishes answer empty with IsReady false and the strip says so.
        SituationIndex situations = _sp.GetRequiredService<SituationIndex>();
        _ = StartupLoad(_sp, "Load: situations index", "situations", situations.Load);

        // The grenade index's startup load: every current rows sibling; the Utility Book says it is reading.
        GrenadeIndex grenadeIndex = _sp.GetRequiredService<GrenadeIndex>();
        _ = StartupLoad(_sp, "Load: grenade index", "utility", grenadeIndex.Load);

        // Nothing resolves the lineup clip service; constructing it is what subscribes it to the index.
        _sp.GetRequiredService<LineupClipService>();

        // Team Identity's startup: a rebuild from the sidecars when team-index.json is missing or behind,
        // else the index-versus-cache diff. Off the UI thread; the tab reads whatever is there meanwhile.
        _ = _sp.GetRequiredService<TeamIdentityService>().StartAsync();

        // Nothing resolves the facts refresher; constructing it is what subscribes it to the rows writes.
        _sp.GetRequiredService<TagFactsRefresher>();

        // One-off re-encode of pre-gzip record and grenade sidecars: no parse, a processing queue item that
        // steps aside between batches, marker-gated once a pass converts everything it found. Queued after
        // the delay so startup loads are not competing for the disk.
        if (!OperatingSystem.IsBrowser())
        {
            DemoCacheStore demoCache = _sp.GetRequiredService<DemoCacheStore>();
            IDemoProcessingQueue queue = _sp.GetRequiredService<IDemoProcessingQueue>();
            GrenadeIndex grenades = _sp.GetRequiredService<GrenadeIndex>();
            _ = Task.Delay(_migrationDelay, ct).ContinueWith(_ =>
                {
                    SidecarFormatMigration.Submit(queue, demoCache,
                        [demoCache.ConvertLegacyRecord, path => GrenadeSidecar.ConvertLegacy(demoCache, path)]);

                    // Grenade rows to throw logs and one flight per lineup position; see GrenadeStoreMigration.
                    GrenadeStoreMigration.Submit(queue, demoCache, grenades);
                },
                ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void OnDisabled()
    {
        // Item 8 fills this in: unsubscribe, cancel owned jobs, release resident indexes.
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
    private static Task StartupLoad(IServiceProvider sp, string title, string owner, Action load) =>
        QueueWork.Run(sp.GetRequiredService<IDemoProcessingQueue>(), QueueJobKind.StoreLoad, title, owner,
            _ => load(), DemoJobPriority.UserRequested);
}
