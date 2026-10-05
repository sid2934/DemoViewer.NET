#region

using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
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
///     switch-on mid-session: the loads and the <see cref="IExtensionResident.Attach" /> calls are queue jobs.
///     <see cref="OnDisabledAsync" /> cancels the pack's queued jobs and releases every resident as one queue
///     job, so a large index never leaves memory on the UI thread.
///     <para>
///         Every item of the pack's own shares one serial, so loads, attaches and releases are totally
///         ordered, and each carries the epoch it was queued under: a release still queued when the user
///         switches back on, or a load still queued when the user switches off, runs after the newer
///         transition's bump and does nothing. Nothing of the pack's state changes outside those items.
///     </para>
/// </summary>
internal sealed class StratBookLifecycle : IExtensionLifecycle
{
    /// <summary>The serial the pack's loads, attach and release share.</summary>
    internal const string Owner = StratBookPack.PackFeatureId;

    /// <summary>The release item's title, pinned by a test.</summary>
    internal const string ReleaseTitle = "Strat Book: release memory";

    /// <summary>The attach item's title (the subscriptions that are not a load), pinned by a test.</summary>
    internal const string AttachTitle = "Strat Book: attach services";

    private readonly IServiceProvider _sp;
    private readonly StratBookPackInstances _instances;
    private IJobHandle? _release;

    // Bumped by every enable and disable; an item queued under an older value does nothing when it runs.
    private int _epoch;

    /// <param name="sp">The composition root, resolved from the same container <see cref="StratBookPack.Register" /> fed.</param>
    /// <param name="instances">The pack's live-state tracker, so shutdown never constructs a store that nothing opened.</param>
    public StratBookLifecycle(IServiceProvider sp, StratBookPackInstances instances)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(instances);
        _sp = sp;
        _instances = instances;
    }

    /// <inheritdoc />
    /// <remarks>Completes when the loads and the attach have run (or were dropped).</remarks>
    public Task OnEnabledAsync(ExtensionStartReason reason, CancellationToken ct)
    {
        int epoch = Interlocked.Increment(ref _epoch);
        IExtensionJobs jobs = _sp.GetExtensionContext(StratBookPack.PackId).Jobs;

        // A release still queued from the switch-off this enable follows goes now; one already running
        // finishes first (the serial) and the jobs below rebuild after it; one this cancel missed sees the
        // newer epoch and does nothing.
        Interlocked.Exchange(ref _release, null)?.Cancel();

        // Resolved here, attached in the item below: a first build subscribes in its constructor, every
        // later enable through Attach, and the item keeps either ordered after a release still queued. It
        // goes first on the serial so Team Identity's file read (the Library team filter) does not wait
        // behind both index loads.
        LineupClipService lineups = _sp.GetRequiredService<LineupClipService>();
        TeamIdentityService teams = _sp.GetRequiredService<TeamIdentityService>();
        TagFactsRefresher tagFacts = _sp.GetRequiredService<TagFactsRefresher>();
        // The zone graphs are read per map by the loads below and by nothing outside the pack.
        IExtensionResident? zones = _sp.GetService<IZonePlaceResolverSource>() as IExtensionResident;

        Task attach = PackItem(jobs, BuiltInJobKinds.Compute, AttachTitle, epoch, () =>
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
            foreach (IExtensionResident resident in _instances.Residents)
            {
                resident.Attach();
            }
        }, ct);

        // The situation index's startup load: every current sidecar, as a queue item at the front (2 ms per
        // demo measured). Queries before it finishes answer empty with IsReady false and the strip says so.
        SituationIndex situations = _sp.GetRequiredService<SituationIndex>();
        Task situationsLoad = PackItem(jobs, BuiltInJobKinds.Load, "Load: situations index", epoch, situations.Load, ct);

        // The grenade index's startup load: every current rows sibling; the Utility Book says it is reading.
        GrenadeIndex grenadeIndex = _sp.GetRequiredService<GrenadeIndex>();
        Task grenadesLoad = PackItem(jobs, BuiltInJobKinds.Load, "Load: grenade index", epoch, grenadeIndex.Load, ct);

        return Task.WhenAll(attach, situationsLoad, grenadesLoad);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The passes already stop by predicate, and the host has cancelled every queued job of the pack (a
    ///     running one finishes its unit). The release is a user-priority job sharing the loads' serial, so it
    ///     runs after a load still in flight and never beside one, and does nothing when an enable has bumped the
    ///     epoch since.
    /// </remarks>
    public Task OnDisabledAsync()
    {
        int epoch = Interlocked.Increment(ref _epoch);
        IExtensionJobs jobs = _sp.GetExtensionContext(StratBookPack.PackId).Jobs;
        IJobHandle release = jobs.Enqueue(new JobRequest(ReleaseTitle, _ =>
        {
            if (Volatile.Read(ref _epoch) == epoch)
            {
                Release();
            }

            return Task.CompletedTask;
        }, new JobOptions(BuiltInJobKinds.Compute, JobPriority.UserRequested, Serial: Owner)));
        Volatile.Write(ref _release, release);
        return release.Completion;
    }

    // Dependents first (reverse build order): a watcher leaves the index before the index empties and raises.
    private void Release()
    {
        ILogger log = DiagnosticsLog.CreateLogger(AppLog.ShellCategory);
        foreach (IExtensionResident resident in _instances.Residents.Reverse())
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
            _instances.StratBook?.Shutdown();
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

    // One of the pack's own jobs: a light job ahead of background work, so it shows in the queue list, on the
    // serial that orders it against every other pack job. It does nothing when the enable it belongs to was
    // cancelled or a later transition bumped the epoch, whether or not the queue dropped it.
    private Task PackItem(IExtensionJobs jobs, string kind, string title, int epoch, Action work, CancellationToken ct) =>
        jobs.RunAsync(title, _ =>
        {
            if (!ct.IsCancellationRequested && Volatile.Read(ref _epoch) == epoch)
            {
                work();
            }

            return Task.CompletedTask;
        }, new JobOptions(kind, JobPriority.UserRequested, Serial: Owner));
}
