#region

using System.Diagnostics;
using System.Text.Json;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.Services.Zones;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Strat Book extension plan, item M0 (and item 9, which reruns the same probes against a later head):
///     docs/architecture/strat-book-plugin.md §12. Three env-var-gated probes, skipped (not failed) when
///     their env var is unset, so the standard tier never runs them. Driven by
///     <c>tools/strat-book-baseline/run.sh</c> against a COPY of a real config dir, never the live one.
///     <para>
///         Each probe is one measurement per process: a second boot in the same process would carry the
///         first boot's JIT and GC committed high-water, so repeats come from running the test again, not
///         from looping inside it.
///     </para>
/// </summary>
[NotInParallel]
public class StratBookPackBaselineTests
{
    private const string ConfigEnvVar = "DV_M0_CONFIG";
    private const string DemosEnvVar = "DV_M0_DEMOS";

    private static string RequireEnv(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            throw new SkipTestException(name + " not set");
        }

        return value;
    }

    // ── Probe 1: the real composition root's resident set after its startup loads settle ──────────────

    /// <summary>
    ///     Boots the REAL composition root (<see cref="App.BuildServices" />) against
    ///     <see cref="ConfigEnvVar" />, waits for the startup loads <c>App.axaml.cs</c> runs unconditionally
    ///     (situations index, grenade index, Team Identity) to settle, then reports the process's resident
    ///     footprint. This IS the "Pack on" row in §12: everything the app does at launch today, nothing
    ///     skipped, nothing stubbed. The 30s-delayed sidecar migrations never run in this configuration
    ///     (see <see cref="SettleStartupLoads" />), which is fine: the copy already carries their "done"
    ///     markers, so production would find them a no-op too.
    /// </summary>
    [Test]
    [Category("Environmental")]
    public async Task ResidentSetAfterStartup()
    {
        string dir = RequireEnv(ConfigEnvVar);
        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                ServiceProvider provider = App.BuildServices(new DesktopWindowService(() => null));
                // Subscribed from here, before SettleStartupLoads, so a job that starts and finishes
                // between two polls (invisible to Items, which drops terminal entries) still gets logged.
                // Covers the race SettleStartupLoads has: TeamIdentityService.IsLoaded can go true before
                // StartAsync's own TeamsCommand is even submitted.
                IDemoProcessingQueue diagQueue = provider.GetRequiredService<IDemoProcessingQueue>();
                long t0 = Stopwatch.GetTimestamp();
                void LogChanged() => Console.WriteLine(
                    $"@M0_QUEUE_CHANGED t={Stopwatch.GetElapsedTime(t0).TotalSeconds:F1}s "
                    + $"items=[{string.Join(", ", diagQueue.Items.Select(i => $"{i.Kind}/{i.DisplayName}/{i.Owners}/{i.Priority}/{i.State}"))}]");
                diagQueue.Changed += LogChanged;
                try
                {
                    LogChanged();
                    await SettleStartupLoads(provider);
                    LogChanged();
                    Snapshot(provider, "early");

                    // Checks that "settled" (the readiness flags above) really is settled: the Changed log
                    // above catches any queue-tracked job, start to finish, for the whole window; this also
                    // polls Items every 15s as a belt-and-suspenders cross-check, then snapshots again. If
                    // the late figure disagrees with the early one and nothing queue-tracked explains it
                    // (no Changed line in between), the release is happening outside the processing queue
                    // entirely, and the early snapshot's "settled" does not mean what it sounds like.
                    for (int elapsed = 0; elapsed < 90; elapsed += 15)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15));
                        Console.WriteLine($"@M0_QUEUE_T+{elapsed + 15}s "
                            + $"queued={diagQueue.QueuedCount} running={diagQueue.RunningCount} "
                            + $"items=[{string.Join(", ", diagQueue.Items.Select(i => $"{i.Kind}/{i.DisplayName}/{i.State}"))}]");
                    }

                    Snapshot(provider, "late");
                }
                finally
                {
                    diagQueue.Changed -= LogChanged;
                    provider.Dispose();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
        }
    }

    private static void Snapshot(ServiceProvider provider, string label)
    {
        Process proc = Process.GetCurrentProcess();
        proc.Refresh();
        long gcBytes = GC.GetTotalMemory(true);
        GCMemoryInfo gi = GC.GetGCMemoryInfo();
        Console.WriteLine($"@M0_RESIDENT_{label} " + JsonSerializer.Serialize(new
        {
            workingSetMb = proc.WorkingSet64 / 1024.0 / 1024.0,
            privateMb = proc.PrivateMemorySize64 / 1024.0 / 1024.0,
            gcMb = gcBytes / 1024.0 / 1024.0,
            committedMb = gi.TotalCommittedBytes / 1024.0 / 1024.0,
            demos = provider.GetRequiredService<DemoCacheStore>().Index.Count,
            situationsLoaded = provider.GetRequiredService<SituationIndex>().IsReady,
            grenadesLoaded = provider.GetRequiredService<GrenadeIndex>().DemoCount
        }));
        if (label == "early")
        {
            Console.WriteLine("@M0_RESIDENT " + JsonSerializer.Serialize(new
            {
                workingSetMb = proc.WorkingSet64 / 1024.0 / 1024.0,
                privateMb = proc.PrivateMemorySize64 / 1024.0 / 1024.0,
                gcMb = gcBytes / 1024.0 / 1024.0,
                committedMb = gi.TotalCommittedBytes / 1024.0 / 1024.0,
                demos = provider.GetRequiredService<DemoCacheStore>().Index.Count
            }));
        }
    }

    // Waits for the situation index, grenade index and Team Identity to report ready and Team Identity's
    // own rebuild-or-sync job (QueueJobKind.TeamsCommand, submitted by StartAsync) to finish, then forces a
    // full blocking, compacting collection so the snapshot is not mid-GC.
    //
    // Deliberately does NOT wait for the whole queue to drain. With Highlights.BackgroundScan and
    // ProcessingQueue.BackgroundProcessingEnabled off in the copy's settings (so this probe's own numbers
    // are not polluted by the library opportunistically reprocessing demos while it measures), any demo in
    // the copy that still needs an ordinary library tier-2 pass sits QUEUED forever: BackgroundProcessingEnabled
    // stops Background-priority DemoProcessing jobs from ever starting, and that has nothing to do with
    // whether the pack's own startup work settled. An earlier version of this method waited on
    // QueuedCount/RunningCount reaching zero and fell through its 90s deadline silently every run, on
    // exactly that backlog, while the actual pack readiness flags had long since gone true.
    //
    // Throws instead of falling through a deadline: a silent fall-through would measure a snapshot that
    // never actually settled and report it as if it had, which is worse than a loud failure.
    private static async Task SettleStartupLoads(ServiceProvider provider)
    {
        SituationIndex situations = provider.GetRequiredService<SituationIndex>();
        GrenadeIndex grenades = provider.GetRequiredService<GrenadeIndex>();
        TeamIdentityService teams = provider.GetRequiredService<TeamIdentityService>();
        IDemoProcessingQueue queue = provider.GetRequiredService<IDemoProcessingQueue>();

        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (!(situations.IsReady && grenades.IsReady && teams.IsLoaded
                 && queue.ActiveCount(QueueJobKind.TeamsCommand) == 0
                 && queue.ActiveCount(QueueJobKind.StoreLoad) == 0))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"startup did not settle within 90s: situationsReady={situations.IsReady} "
                    + $"grenadesReady={grenades.IsReady} teamsLoaded={teams.IsLoaded} "
                    + $"teamsCommandActive={queue.ActiveCount(QueueJobKind.TeamsCommand)} "
                    + $"storeLoadActive={queue.ActiveCount(QueueJobKind.StoreLoad)} "
                    + $"items=[{string.Join(", ", queue.Items.Select(i => $"{i.Kind}/{i.DisplayName}/{i.State}"))}]");
            }

            await Task.Delay(200);
        }

        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }
    }

    // ── Probe 2: the pack's two resident indexes plus Team Identity, built directly ─────────────────────

    /// <summary>
    ///     <see cref="App.BuildServices" /> runs the pack's startup loads unconditionally (no gate exists
    ///     yet; that is item 1/3/8's job), so there is no seam to boot "with the loads skipped" without
    ///     editing <c>App.axaml.cs</c>, a file item 0 owns this wave. This probe is the narrower,
    ///     exact substitute: it constructs <see cref="SituationIndex" />, <see cref="GrenadeIndex" /> and
    ///     <see cref="TeamIdentityService" /> directly over a fresh <see cref="DemoCacheStore" /> on the
    ///     copy (the same construction <c>StratMiningCalibration</c> uses), outside any DI container, and
    ///     takes a <see cref="GC.GetTotalMemory" /> delta around each one's load. It excludes Team
    ///     Identity's three sibling stores (Strats, Dossier, Veto History, wired by the DI factory, not the
    ///     service itself) and Tag Facts and the Lineup Clip service (event-driven / render work, not a
    ///     bulk load; see §3.2), so it is a lower bound on the pack's resident cost, not the whole of it.
    /// </summary>
    [Test]
    [Category("Environmental")]
    public async Task PackResidentCostDirect()
    {
        string dir = RequireEnv(ConfigEnvVar);
        string cache = Path.Combine(dir, "cache");

        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        }

        long before = GC.GetTotalMemory(true);

        DemoCacheStore demoCache = new(cache);
        RoundIndexStore positions = new(cache, demoCache);
        AssetZonePlaceResolverSource zones = new();
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn, zones);
        using SituationIndex situations = new(demoCache, positions, sources);
        situations.Load();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        long afterSituations = GC.GetTotalMemory(true);

        using GrenadeIndex grenades = new(demoCache, zones);
        grenades.Load();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        long afterGrenades = GC.GetTotalMemory(true);

        using TeamIdentityService teams = new(dir, demoCache, new CachedFacts(demoCache));
        await teams.StartAsync();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        long afterTeams = GC.GetTotalMemory(true);

        Console.WriteLine("@M0_PACKCOST " + JsonSerializer.Serialize(new
        {
            demos = demoCache.Index.Count,
            grenadeDemos = grenades.DemoCount,
            situationsMb = (afterSituations - before) / 1024.0 / 1024.0,
            grenadesMb = (afterGrenades - afterSituations) / 1024.0 / 1024.0,
            teamsMb = (afterTeams - afterGrenades) / 1024.0 / 1024.0,
            totalMb = (afterTeams - before) / 1024.0 / 1024.0
        }));

        // Checks whether the ~90s resident-memory decline ResidentSetAfterStartup shows is a whole-process
        // effect (buffer pools, delayed finalizers) or specific to the full app: if this isolated, no-DI,
        // no-Avalonia construction ALSO drops after idling with nothing else running, the cause is below
        // the app layer and the two probes' numbers are comparable late as well as early.
        await Task.Delay(TimeSpan.FromSeconds(90));
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }

        long late = GC.GetTotalMemory(true);
        Console.WriteLine("@M0_PACKCOST_LATE " + JsonSerializer.Serialize(new
        {
            totalMb = (late - before) / 1024.0 / 1024.0
        }));
    }

    // A read-only facts source over the already-written cache records; mirrors StratMiningCalibration's
    // helper so Team Identity's SideAtRound join has something real to read without a DI container.
    private sealed class CachedFacts(DemoCacheStore cache) : IRoundFactsSource
    {
        public int Schema => 0;

        public RoundFactsRows? TryGet(string demoPath) => cache.TryLoadRecord(demoPath)?.RoundFacts;

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) => null;

        public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter) => [];

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];

        public event Action<string>? Updated
        {
            add { }
            remove { }
        }
    }

    // ── Probe 3: per-demo indexing time, full evaluator list versus the pack evaluators removed ────────

    /// <summary>
    ///     Times one retained parse evaluated by all six registered evaluators, the roster
    ///     <c>AppCompositionRootTests</c> pins (library, highlights, round facts, round index, suggested
    ///     tags, grenades), against one forward parse evaluated by library and highlights alone. The mode
    ///     switch is not an artifact of this harness: round index, suggested tags and grenades have no
    ///     <see cref="IDemoEvaluator.ForwardFor" />, so the real coordinator already runs a retained parse
    ///     whenever any of them wants a demo, and a forward one when only library and highlights do, which
    ///     is the shape gating those three off actually produces. <see cref="DemosEnvVar" /> names a file of
    ///     demo paths, one per line; the first line is a discarded warm-up run under both modes. Each of the
    ///     rest runs under exactly one mode, never both: a mode run second on a demo the other mode already
    ///     read pulls a warm OS page cache and times several times faster for reasons that have nothing to
    ///     do with the evaluator list, so the measured demos split into two disjoint groups by position
    ///     (odd index full, even index reduced) instead. Demos are read in place, never copied or moved.
    ///     <para>
    ///         Round Facts, Round Index, Suggested Tags and Highlights each check a fingerprint or state
    ///         field against the cache record and silently do nothing when it already matches, which it
    ///         does for every demo in an indexed library: an early build of this probe measured all four
    ///         doing effectively zero work, for the same reason <see cref="IDemoEvaluator.Wants" /> would
    ///         already say no to them. Before EVERY timed demo, in both modes, this clears those four
    ///         fields on its cache record (<see cref="DemoCacheStore.UpdateExisting" />, see
    ///         <see cref="InvalidateForReindex" />) so all four actually recompute, the scenario this number
    ///         needs to answer: what re-indexing a demo costs, not what re-checking an already-current one
    ///         costs. Grenades has no such check (it always walks). Library is gated by membership in a
    ///         private, un-forceable "pending" dictionary this probe does not populate, so it no-ops in
    ///         both modes; since both modes call it the same way, that does not bias the delta, but it does
    ///         mean the numbers below do not include library's own (expected small) tier-2 cost.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Environmental")]
    public async Task IndexingTimePerDemo()
    {
        string dir = RequireEnv(ConfigEnvVar);
        string demosFile = RequireEnv(DemosEnvVar);
        string[] lines = File.ReadAllLines(demosFile).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 2)
        {
            throw new SkipTestException(DemosEnvVar + " must list a warm-up demo plus at least one measured demo");
        }

        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            // BuildServices + settling must run on the UI thread (ValidateOnBuild constructs MainViewModel,
            // which starts a DispatcherTimer), but HeadlessSession enforces a 4-minute budget per RunOnUi
            // body and treats a slow but legitimately-busy body as hung. Nine mmap reads of ~300 MB demos
            // easily exceed that on top of the ~2-minute settle, so only the boot + settle runs on the UI
            // thread; the timed loop below runs on this (plain, unwatched) thread, then a second short
            // RunOnUi disposes the provider (MainViewModel's timer wants to stop on the thread it started on).
            ServiceProvider? provider = null;
            DemoLibraryService library = null!;
            HighlightScanService highlights = null!;
            ForwardPassRunner forward = null!;
            IDemoEvaluator[] full = null!;
            DemoCacheStore demoCache = null!;
            await HeadlessSession.RunOnUi(async () =>
            {
                provider = App.BuildServices(new DesktopWindowService(() => null));
                await SettleStartupLoads(provider);

                library = provider.GetRequiredService<DemoLibraryService>();
                highlights = provider.GetRequiredService<HighlightScanService>();
                RoundFactsEvaluator roundFacts = provider.GetRequiredService<RoundFactsEvaluator>();
                RoundIndexEvaluator roundIndex = provider.GetRequiredService<RoundIndexEvaluator>();
                SuggestedTagsService suggestedTags = provider.GetRequiredService<SuggestedTagsService>();
                GrenadeIndexEvaluator grenades = provider.GetRequiredService<GrenadeIndexEvaluator>();
                forward = new ForwardPassRunner(provider.GetRequiredService<MergedRulesBuild>());
                demoCache = provider.GetRequiredService<DemoCacheStore>();
                full = [library, highlights, roundFacts, roundIndex, suggestedTags, grenades];
            });

            try
            {
                // Warm-up: both modes, on the same demo, discarded. A mode run second on a demo it shares
                // with the other mode reads a warm OS page cache and times artificially fast (measured: a
                // same-demo full-then-reduced pair showed the SECOND mode 5x to 7x faster purely from that),
                // so each measured demo below runs under exactly ONE mode, never both.
                InvalidateForReindex(demoCache, lines[0]);
                TimeFull(lines[0], full);
                InvalidateForReindex(demoCache, lines[0]);
                TimeReduced(lines[0], forward, library, highlights);

                List<(string Demo, double Ms)> fullResults = [];
                List<(string Demo, double Ms)> reducedResults = [];
                for (int i = 1; i < lines.Length; i++)
                {
                    string path = lines[i];
                    string name = Path.GetFileName(path);
                    double sizeMb = new FileInfo(path).Length / 1024.0 / 1024.0;
                    // Odd measured index -> full; even -> reduced, so neither mode's file reads are all
                    // taken first or last (guards against a steady drift over the course of the run).
                    InvalidateForReindex(demoCache, path);
                    if (i % 2 == 1)
                    {
                        double ms = TimeFull(path, full);
                        fullResults.Add((name, ms));
                        Console.WriteLine("@M0_INDEXMS " + JsonSerializer.Serialize(new { demo = name, sizeMb, mode = "full", ms }));
                    }
                    else
                    {
                        double ms = TimeReduced(path, forward, library, highlights);
                        reducedResults.Add((name, ms));
                        Console.WriteLine("@M0_INDEXMS " + JsonSerializer.Serialize(new { demo = name, sizeMb, mode = "reduced", ms }));
                    }
                }

                Console.WriteLine("@M0_INDEXMS_SUMMARY " + JsonSerializer.Serialize(new
                {
                    fullMedianMs = Median(fullResults.Select(r => r.Ms)),
                    reducedMedianMs = Median(reducedResults.Select(r => r.Ms)),
                    fullCount = fullResults.Count,
                    reducedCount = reducedResults.Count
                }));
            }
            finally
            {
                if (provider is not null)
                {
                    ServiceProvider toDispose = provider;
                    await HeadlessSession.RunOnUi(() =>
                    {
                        toDispose.Dispose();
                        return Task.CompletedTask;
                    });
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.OrderBy(v => v)];
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    // Round Facts, Round Index, Suggested Tags and Highlights each gate their real work on a fingerprint
    // or state already matching the cache record; clearing them is what RoundIndexEvaluator.Request and
    // SuggestedTagsService.Request do via their own forced-path sets, but those ALSO call
    // Coordinator.Consider, which would submit an async queue job racing this probe's own direct Evaluate
    // call. Mutating the record directly forces the same recompute with no concurrent submission.
    //
    // NOT fixed here: DemoLibraryService's own gate is membership in a private "pending" dictionary with
    // no fingerprint and no public per-path force, populated only by its own RescanAsync reconcile. Both
    // TimeFull and TimeReduced call library.Evaluate/EvaluateForward and both still no-op on it, the same
    // way in both arms, so it does not bias the full-versus-reduced delta, but it does mean library's own
    // (expected small: a players list and a few fields off data the parse already produced) tier-2 cost is
    // not captured by either number here.
    private static void InvalidateForReindex(DemoCacheStore demoCache, string path) =>
        demoCache.UpdateExisting(path, r =>
        {
            r.RoundFactsFingerprint = null;
            r.RoundIndexFingerprint = null;
            r.SuggestionsFingerprint = null;
            r.AnalysisState = DemoAnalysisState.Pending;
        });

    // Verbatim copy of DemoProcessingQueue.RunEntry's retained path: a single mmap-or-bytes parse with
    // DecodePlan.Everything (user commands kept, since GrenadeIndexEvaluator defaults ReadsUserCommands to
    // true and is in this list), then every evaluator's Evaluate on the one held parse.
    private static double TimeFull(string path, IReadOnlyList<IDemoEvaluator> evaluators)
    {
        long t0 = Stopwatch.GetTimestamp();
        ParsedDemo parsed = MappedParsePolicy.IsSettled(path, TimeProvider.System, MappedParsePolicy.StatFile)
            ? MemoryMappedDemoSource.ParseFile(path, new ParseOptions { Plan = DecodePlan.Everything })
            : DemoParser.Parse(File.ReadAllBytes(path).AsMemory(), new ParseOptions { Plan = DecodePlan.Everything });
        foreach (IDemoEvaluator evaluator in evaluators)
        {
            evaluator.Evaluate(path, parsed);
        }

        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    // Verbatim copy of DemoProcessingQueue.RunForward's path: ForwardPassRunner.Run (ForwardDemoPass under
    // the hood) for FinalState + Rules, then each of the two forward-capable evaluators' EvaluateForward.
    private static double TimeReduced(string path, ForwardPassRunner forward, DemoLibraryService library,
        HighlightScanService highlights)
    {
        long t0 = Stopwatch.GetTimestamp();
        ForwardDemoResult pass = forward.Run(path, ForwardNeeds.FinalState | ForwardNeeds.Rules, _ => { },
            CancellationToken.None);
        library.EvaluateForward(path, pass);
        highlights.EvaluateForward(path, pass);
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
}
