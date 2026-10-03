#region

using System.Collections.ObjectModel;
using System.Numerics;
using Avalonia.Threading;
using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     The live toggle (item 8) in the real container: off to on runs the startup loads once and queues the
///     re-poll, on to off cancels the pack's items by owner and releases what on built, on-off-on reloads, a
///     flip mid-load cancels through the enable's token, the first-run wizard's answer drives the switch,
///     and shutdown after a release touches nothing. The queue is a double that runs each job inline (or holds
///     it, for the mid-load case) and records titles, owner cancels and parse submissions.
///     <see cref="NotInParallelAttribute" /> because the container cases pin the process-global config dir.
/// </summary>
[NotInParallel]
public class StratBookLiveToggleTests
{
    private const string ReIndexTitle = "Strat Book extension: find demos to re-index";

    private static readonly string[] _startupLabels =
    [
        "Load: situations index",
        "Load: grenade index",
        "Load: teams",
        "Teams: update"
    ];

    private static IFeaturePack Pack => FeaturePacks.Default.Single(p => p.FeatureId == StratBookPack.PackFeatureId);

    [Test]
    public async Task OffToOn_RunsTheStartupLoadsOnce_QueuesTheReIndexPoll_AndTheCoordinatorReconsiders()
    {
        await WithContainer(Seed(packOn: false), async (provider, queue, settings) =>
        {
            // A parsed demo with Round Facts and no round index: exactly what the round index evaluator
            // wants once the pack is on, and nothing it may touch while the pack is off.
            SeedLibrary(provider, demos: 1, roundsPerDemo: 2, grenadesPerDemo: 0, indexRounds: false);
            App.StartPacks(provider);
            PackSwitch packs = provider.GetRequiredService<PackSwitch>();
            await Assert.That(queue.Titles).IsEmpty().Because("off at startup: nothing queued");
            await Assert.That(packs.IsOn(Pack)).IsFalse();

            settings.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();

            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            using (Assert.Multiple())
            {
                await Assert.That(packs.IsOn(Pack)).IsTrue();
                await Assert.That(string.Join(", ", queue.Titles.Take(_startupLabels.Length + 1)))
                    .IsEqualTo(string.Join(", ", _startupLabels.Append(ReIndexTitle)))
                    .Because("the same loads startup runs, once, then the re-poll behind them; core's own reactions follow");
                await Assert.That(queue.Titles.Count(t => t == "Load: situations index")).IsEqualTo(1);
                await Assert.That(queue.Parses.Select(p => p.Owner)).Contains(RoundIndexEvaluator.EvaluatorId)
                    .Because("the re-poll made the coordinator submit the demo the round index now wants");
                await Assert.That(instances.Situations).IsNotNull();
                await Assert.That(instances.Grenades).IsNotNull();
                await Assert.That(instances.Teams).IsNotNull();
                await Assert.That(instances.Lineups).IsNotNull();
                await Assert.That(instances.TagFacts).IsNotNull();
                await Assert.That(instances.Situations!.IsReady).IsTrue();
                await Assert.That(instances.Grenades!.IsReady).IsTrue();
            }
        });
    }

    [Test]
    public async Task OnToOff_CancelsEveryPackOwner_ReleasesTheResidents_AndNullsTheLiveView()
    {
        await WithContainer(Seed(packOn: true), async (provider, queue, settings) =>
        {
            SeedLibrary(provider, demos: 3, roundsPerDemo: 4, grenadesPerDemo: 5, indexRounds: true);
            App.StartPacks(provider);
            PackSwitch packs = provider.GetRequiredService<PackSwitch>();
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();

            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            SituationIndex situations = provider.GetRequiredService<SituationIndex>();
            GrenadeIndex grenades = provider.GetRequiredService<GrenadeIndex>();
            TeamIdentityService teams = provider.GetRequiredService<TeamIdentityService>();
            await Assert.That(situations.IndexedDemoCount).IsEqualTo(3).Because("the fixture loaded");
            await Assert.That(grenades.DemoCount).IsEqualTo(3);
            await Assert.That(teams.IsLoaded).IsTrue();
            int before = queue.Titles.Count;

            settings.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(packs.IsOn(Pack)).IsFalse();
                await Assert.That(queue.CancelledOwners).IsEquivalentTo(StratBookLifecycle.OwnerTags)
                    .Because("every owner tag a pack job or parse attachment carries is cancelled, once each");
                await Assert.That(queue.Titles.Skip(before)).IsEquivalentTo([StratBookLifecycle.ReleaseTitle])
                    .Because("the release is the one item the switch-off queues");
                await Assert.That(instances.Situations).IsNull();
                await Assert.That(instances.Grenades).IsNull();
                await Assert.That(instances.Teams).IsNull();
                await Assert.That(instances.Lineups).IsNull();
                await Assert.That(instances.TagFacts).IsNull();
                await Assert.That(instances.Watched).IsNull();
                await Assert.That(instances.Residents.Count).IsGreaterThanOrEqualTo(5)
                    .Because("the built residents survive the release; they are the container's singletons");
                await Assert.That(situations.IsReady).IsFalse();
                await Assert.That(situations.IndexedDemoCount).IsEqualTo(0);
                await Assert.That(grenades.IsReady).IsFalse();
                await Assert.That(grenades.DemoCount).IsEqualTo(0);
                await Assert.That(teams.IsLoaded).IsFalse();
            }

            // Subscriptions gone: a cache change the attached index would act on changes nothing.
            DemoCacheStore cache = provider.GetRequiredService<DemoCacheStore>();
            cache.Remove("/d/0.dem");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(situations.IndexedDemoCount).IsEqualTo(0);
            await Assert.That(queue.Titles.Count).IsEqualTo(before + 1).Because("nothing of the pack reacted to the cache");
        });
    }

    [Test]
    public async Task OnOffOn_ReloadsTheSameWay_AndTheIndexAnswersAgain()
    {
        await WithContainer(Seed(packOn: true), async (provider, queue, settings) =>
        {
            SeedLibrary(provider, demos: 2, roundsPerDemo: 3, grenadesPerDemo: 2, indexRounds: true);
            App.StartPacks(provider);
            PackSwitch packs = provider.GetRequiredService<PackSwitch>();
            await packs.Pending;

            settings.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();
            int before = queue.Titles.Count;

            settings.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();

            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            SituationIndex situations = provider.GetRequiredService<SituationIndex>();
            using (Assert.Multiple())
            {
                await Assert.That(packs.IsOn(Pack)).IsTrue();
                await Assert.That(string.Join(", ", queue.Titles.Skip(before).Take(_startupLabels.Length + 1)))
                    .IsEqualTo(string.Join(", ", _startupLabels.Append(ReIndexTitle)))
                    .Because("the second enable runs the startup loads again, in the same order");
                await Assert.That(situations.IsReady).IsTrue();
                await Assert.That(situations.IndexedDemoCount).IsEqualTo(2);
                await Assert.That(provider.GetRequiredService<GrenadeIndex>().DemoCount).IsEqualTo(2);
                await Assert.That(provider.GetRequiredService<TeamIdentityService>().IsLoaded).IsTrue();
                await Assert.That(instances.Situations).IsNotNull();
                await Assert.That(instances.Grenades).IsNotNull();
                await Assert.That(instances.Teams).IsNotNull();
                await Assert.That(instances.Lineups).IsNotNull();
                await Assert.That(instances.TagFacts).IsNotNull();
            }
        });
    }

    [Test]
    public async Task AFlipMidLoad_CancelsTheEnableThroughItsToken_ThenReleases()
    {
        await WithContainer(Seed(packOn: false), async (provider, queue, settings) =>
        {
            SeedLibrary(provider, demos: 2, roundsPerDemo: 2, grenadesPerDemo: 1, indexRounds: true);
            App.StartPacks(provider);
            PackSwitch packs = provider.GetRequiredService<PackSwitch>();

            // Hold every job: the loads are queued and not yet run when the user flips back.
            queue.Defer = true;
            settings.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));
            await Assert.That(queue.Titles).Contains("Load: situations index");
            settings.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);

            await Assert.That(queue.CancelledOwners).Contains("situations");
            await Assert.That(queue.Titles).Contains(StratBookLifecycle.ReleaseTitle);

            // The real queue dropped those by owner; this double runs them anyway to prove the token alone
            // keeps a load from landing after the flip.
            queue.RunDeferred();
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();

            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            SituationIndex situations = provider.GetRequiredService<SituationIndex>();
            using (Assert.Multiple())
            {
                await Assert.That(packs.IsOn(Pack)).IsFalse();
                await Assert.That(situations.IsReady).IsFalse().Because("the load saw the cancelled token and did nothing");
                await Assert.That(situations.IndexedDemoCount).IsEqualTo(0);
                await Assert.That(provider.GetRequiredService<GrenadeIndex>().IsReady).IsFalse();
                await Assert.That(instances.Situations).IsNull();
                await Assert.That(instances.Grenades).IsNull();
                await Assert.That(instances.Teams).IsNull();
            }
        });
    }

    [Test]
    public async Task FirstRun_StartPacksWaitsForTheWizard_AcceptStartsThePack_DeclineLeavesItUnbuilt()
    {
        // Accept: a fresh dir (no settings.json), so NeedsFirstRun is true and the pack resolves on by default.
        await WithContainer(null, async (provider, queue, settings) =>
        {
            await Assert.That(settings.NeedsFirstRun).IsTrue();
            App.StartPacks(provider);
            PackSwitch packs = provider.GetRequiredService<PackSwitch>();
            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            using (Assert.Multiple())
            {
                await Assert.That(queue.Titles).IsEmpty().Because("the wizard has still to ask, so nothing starts");
                await Assert.That(packs.IsOn(Pack)).IsFalse();
                await Assert.That(instances.Situations).IsNull();
                await Assert.That(instances.Grenades).IsNull();
            }

            FirstRunWizardViewModel wizard = new(settings);
            await Assert.That(wizard.PackOptions.Single(p => p.FeatureId == StratBookPack.PackFeatureId).Enabled).IsTrue();
            wizard.FinishCommand.Execute(null);
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();

            using (Assert.Multiple())
            {
                await Assert.That(packs.IsOn(Pack)).IsTrue();
                await Assert.That(string.Join(", ", queue.Titles.Take(_startupLabels.Length + 1)))
                    .IsEqualTo(string.Join(", ", _startupLabels.Append(ReIndexTitle)))
                    .Because("the wizard's accept is the enable, with the in-session re-poll behind the loads");
                await Assert.That(instances.Situations).IsNotNull();
                await Assert.That(instances.Grenades).IsNotNull();
            }
        });

        // Decline: the explicit off override lands, the gate resolves off, nothing is built.
        await WithContainer(null, async (provider, queue, settings) =>
        {
            App.StartPacks(provider);
            FirstRunWizardViewModel wizard = new(settings);
            wizard.PackOptions.Single(p => p.FeatureId == StratBookPack.PackFeatureId).Enabled = false;
            wizard.FinishCommand.Execute(null);
            PackSwitch packs = provider.GetRequiredService<PackSwitch>();
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();

            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            using (Assert.Multiple())
            {
                await Assert.That(settings.NeedsFirstRun).IsFalse();
                await Assert.That(provider.GetRequiredService<IFeatureGate>().IsEnabled(StratBookPack.PackFeatureId)).IsFalse();
                await Assert.That(packs.IsOn(Pack)).IsFalse();
                await Assert.That(queue.Titles).IsEmpty();
                await Assert.That(instances.Situations).IsNull();
                await Assert.That(instances.Grenades).IsNull();
                await Assert.That(instances.Lineups).IsNull();
            }
        });
    }

    [Test]
    public async Task ShutdownAfterARelease_FlushesNothing_AndBuildsNothingBack()
    {
        await WithContainer(Seed(packOn: true), async (provider, queue, settings) =>
        {
            SeedLibrary(provider, demos: 1, roundsPerDemo: 1, grenadesPerDemo: 2, indexRounds: true);
            App.StartPacks(provider);
            PackSwitch packs = provider.GetRequiredService<PackSwitch>();
            await packs.Pending;
            GrenadeIndex grenades = provider.GetRequiredService<GrenadeIndex>();
            _ = grenades.Query(new GrenadeQuery("de_nuke")); // mints an anchor: the lineup store is dirty

            settings.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();
            string lineups = Path.Combine(AppPaths.DemoCacheDir!, GrenadeLineupStore.FileName);
            await Assert.That(File.Exists(lineups)).IsTrue().Because("the release wrote the pending lineup before dropping it");
            DateTime written = File.GetLastWriteTimeUtc(lineups);
            int before = queue.Titles.Count;

            IPackLifecycle lifecycle = provider.GetRequiredKeyedService<IPackLifecycle>(Pack.Id);
            lifecycle.OnShutdown(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            SituationIndex situations = provider.GetRequiredService<SituationIndex>();
            using (Assert.Multiple())
            {
                await Assert.That(File.GetLastWriteTimeUtc(lineups)).IsEqualTo(written).Because("nothing live, nothing flushed");
                await Assert.That(queue.Titles.Count).IsEqualTo(before).Because("shutdown queued nothing");
                await Assert.That(situations.IsReady).IsFalse().Because("shutdown attached and loaded nothing back");
                await Assert.That(grenades.IsReady).IsFalse();
                await Assert.That(provider.GetRequiredService<StratBookPackInstances>().Grenades).IsNull();
            }
        });
    }

    /// <summary>
    ///     Decision 3 measured: the heap after the release is back where it was before the enable, within a
    ///     small margin, on a fixture large enough that the enable itself is unmistakable. Two cycles: the
    ///     first pays whatever the process pays once between the marks whether or not the pack exists (the
    ///     Reels fingerprint reading the rule docs, loggers, JIT); the second is the claim, and the residual
    ///     must not have grown between the two, or something of the pack's is leaking per toggle. A copy of
    ///     a real library can stand in for the synthetic fixture through <c>DV_STRATBOOK_TOGGLE_CONFIG</c>
    ///     (never the live config dir: the pack writes into it).
    /// </summary>
    [Test]
    [Category("Budget")]
    public async Task OnThenOff_ReturnsTheHeap_ToWhereItWasBeforeTheEnable()
    {
        string? copy = Environment.GetEnvironmentVariable("DV_STRATBOOK_TOGGLE_CONFIG");
        await WithContainer(Seed(packOn: true), async (provider, queue, settings) =>
        {
            if (copy is null)
            {
                SeedLibrary(provider, demos: 160, roundsPerDemo: 24, grenadesPerDemo: 60, indexRounds: true);
            }

            Dispatcher.UIThread.RunJobs();
            PackSwitch packs = provider.GetRequiredService<PackSwitch>();
            App.StartPacks(provider);
            await packs.Pending;
            Dispatcher.UIThread.RunJobs();

            (long Built, long Left) first = await Cycle(packs, settings, "first");
            (long Built, long Left) second = await Cycle(packs, settings, "second");
            Console.WriteLine($"@TOGGLE_HEAP demos={provider.GetRequiredService<DemoCacheStore>().Index.Count} " +
                              $"first: enable=+{Mb(first.Built)}MB left=+{Mb(first.Left)}MB; second: enable=+{Mb(second.Built)}MB left=+{Mb(second.Left)}MB");
            using (Assert.Multiple())
            {
                await Assert.That(second.Built).IsGreaterThan(2L * 1024 * 1024).Because("the fixture must cost something or the test proves nothing");
                await Assert.That(second.Left).IsLessThan(Math.Max(1L * 1024 * 1024, second.Built / 10))
                    .Because("after the release the heap is back within a margin the pack's shells and the queue history explain");
                await Assert.That(second.Left - first.Left).IsLessThan(512L * 1024)
                    .Because("a residual that grows per toggle is a leak; one that does not is the process's one-time cost");
            }
        }, configDir: copy);
    }

    // Off, settle, on, settle, off, settle: what the enable built and what the release left behind.
    private static async Task<(long Built, long Left)> Cycle(PackSwitch packs, SettingsService settings, string label)
    {
        settings.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
        await packs.Pending;
        Dispatcher.UIThread.RunJobs();
        long before = Settle();

        settings.Write(s => s.Features.Overrides.Remove(StratBookPack.PackFeatureId));
        await packs.Pending;
        Dispatcher.UIThread.RunJobs();
        long afterEnable = Settle();

        settings.Write(s => s.Features.Overrides[StratBookPack.PackFeatureId] = false);
        await packs.Pending;
        Dispatcher.UIThread.RunJobs();
        long afterDisable = Settle();
        Console.WriteLine($"@TOGGLE_HEAP_{label} before={Mb(before)}MB enable=+{Mb(afterEnable - before)}MB after-disable=+{Mb(afterDisable - before)}MB");
        return (afterEnable - before, afterDisable - before);
    }

    private static string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private static long Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(true);
    }

    private static string Seed(bool packOn) => packOn
        ? """
          {
            "FirstRunCompleted": true
          }
          """
        : """
          {
            "FirstRunCompleted": true,
            "Features": { "Overrides": { "pack.stratbook": false } }
          }
          """;

    // Parsed records with Round Facts for every demo; a round index sidecar and a grenade sidecar each when
    // asked, stamped the way the evaluators leave them, so the loads have something to hold.
    private static void SeedLibrary(IServiceProvider provider, int demos, int roundsPerDemo, int grenadesPerDemo, bool indexRounds)
    {
        DemoCacheStore cache = provider.GetRequiredService<DemoCacheStore>();
        RoundIndexStore sidecars = provider.GetRequiredService<RoundIndexStore>();
        string fingerprint = provider.GetRequiredService<RoundIndexPlaceSources>().FingerprintFor("de_nuke");
        string[] ctPlaces = ["BombsiteA", "Heaven", "Ramp", "Secret", "Outside", "Lobby"];
        string[] tPlaces = ["Outside", "Ramp", "Lobby", "Squeaky", "Silo", "Vents"];
        for (int d = 0; d < demos; d++)
        {
            string path = $"/d/{d}.dem";
            string sha = Convert.ToHexString(BitConverter.GetBytes(d)) + new string('0', 56);
            var facts = Enumerable.Range(1, roundsPerDemo).Select(r => RoundIndexTestData.Round(r, r * 1000, r * 1000 + 600)).ToArray();
            if (!indexRounds)
            {
                cache.Upsert(RoundIndexTestData.ParsedRecord(path, "de_nuke", sha, RoundIndexTestData.Facts(facts), modifiedTicks: 20 + d));
                continue;
            }

            var rounds = Enumerable.Range(1, roundsPerDemo).Select(r =>
            {
                RoundIndexRun[] runs = Enumerable.Range(0, 6).Select(k => new RoundIndexRun(k * 10, k * 10 + 9,
                    $"{ctPlaces[(k + d) % ctPlaces.Length]}:{1 + (k + r) % 5}", $"{tPlaces[(k + r) % tPlaces.Length]}:{1 + (k + d) % 5}")).ToArray();
                return (r, r * 1000, r * 1000 + 600, runs);
            }).ToArray();
            RoundIndexDocument document = RoundIndexTestData.Document("de_nuke", fingerprint, rounds);
            RoundIndexTestData.Indexed(cache, sidecars, path, document, computedAt: 100 + d, modifiedTicks: 20 + d, sha: sha);
            if (grenadesPerDemo > 0)
            {
                IndexGrenades(cache, path, sha, grenadesPerDemo, d);
            }
        }
    }

    private static void IndexGrenades(DemoCacheStore cache, string path, string sha, int count, int seed)
    {
        GrenadeDocument document = new()
        {
            Demo = new GrenadeDemoHeader { Sha256 = sha, StableKey = DemoCacheStore.StableKey(path) },
            Grenades =
            [
                .. Enumerable.Range(0, count).Select(i => new GrenadeRow
                {
                    Id = $"g{i}",
                    Kind = (GrenadeKind)(i % 4),
                    ThrowerTeam = i % 2 == 0 ? 3 : 2,
                    ThrowerName = $"player {i % 10}",
                    ReleaseTick = 1000 + i * 37,
                    ReleasePosition = WorldPoint.From(new Vector3(512 + (i * 97 + seed * 13) % 2000, 288 + (i * 61) % 1500, -160)),
                    DetonationPosition = WorldPoint.From(new Vector3(-1400 + (i * 131) % 2500, -1400 + (i * 71 + seed) % 2500, -170)),
                    EndKind = GrenadeEndKind.Detonated
                })
            ]
        };
        cache.WriteSibling(path, GrenadeSidecar.Suffix, GrenadeSidecar.Serialize(document));
        DemoCacheRecord record = cache.TryLoadRecord(path) ?? RoundIndexTestData.ParsedRecord(path, "de_nuke", sha);
        DemoCacheStore.StampGrenades(record);
        record.GrenadeState = DemoAnalysisState.Indexed;
        record.GrenadeCount = document.Grenades.Count;
        record.GrenadeWalker = GrenadeWalker.Version;
        cache.Upsert(record);
    }

    // Runs every job inline on submit (so a load has run when OnEnabledAsync returns) unless Defer holds them;
    // records titles, owner-wide cancels and parse submissions. Parses never run: the coordinator's submit is
    // the fact under test.
    private sealed class InlineQueue : IDemoProcessingQueue
    {
        private readonly List<(Func<IQueueJobContext, Task> Run, TaskCompletionSource Done)> _deferred = [];

        public List<string> Titles { get; } = [];
        public List<string> CancelledOwners { get; } = [];
        public List<(string Owner, string Path)> Parses { get; } = [];
        public bool Defer { get; set; }

        public ReadOnlyObservableCollection<DemoQueueItem> Items { get; } = new([]);
        public int MaxConcurrency { get; set; } = 1;
        public int MaxQueueSize { get; set; } = 200;
        public bool BackgroundEnabled { get; set; } = true;
        public bool IsPaused => false;
        public int QueuedCount => 0;
        public int RunningCount => 0;

        public event Action? Changed;

        public event Action? CapacityAvailable
        {
            add { }
            remove { }
        }

        public int ActiveCount(QueueJobKind kind) => 0;

        public Task<ParsedDemo> RequestForegroundAsync(string? path, ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request)
        {
            lock (Titles)
            {
                Parses.Add((request.OwnerTag, request.Path));
            }

            return new DoneHandle(Task.CompletedTask);
        }

        public IDemoQueueHandle SubmitJob(QueueJobRequest request)
        {
            lock (Titles)
            {
                Titles.Add(request.Title);
            }

            Changed?.Invoke();
            if (Defer)
            {
                TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_deferred)
                {
                    _deferred.Add((request.RunAsync, done));
                }

                return new DoneHandle(done.Task);
            }

            request.RunAsync(new Context()).GetAwaiter().GetResult();
            return new DoneHandle(Task.CompletedTask);
        }

        /// <summary>Runs every held job, in submission order.</summary>
        public void RunDeferred()
        {
            List<(Func<IQueueJobContext, Task> Run, TaskCompletionSource Done)> held;
            lock (_deferred)
            {
                held = [.. _deferred];
                _deferred.Clear();
            }

            Defer = false;
            foreach ((Func<IQueueJobContext, Task> run, TaskCompletionSource done) in held)
            {
                run(new Context()).GetAwaiter().GetResult();
                done.TrySetResult();
            }
        }

        public IReadOnlyList<DemoQueueItemSnapshot> Snapshot() => [];

        public void RemoveByUser(Guid itemId)
        {
        }

        public void CancelOwned(string ownerTag, string path)
        {
        }

        public void CancelOwned(string ownerTag)
        {
            lock (Titles)
            {
                CancelledOwners.Add(ownerTag);
            }
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        private sealed class Context : IQueueJobContext
        {
            public CancellationToken CancellationToken => CancellationToken.None;

            public void Report(int done, int total, string? detail = null)
            {
            }

            public Task StepAsideAsync() => Task.CompletedTask;

            public void ReleaseSlot()
            {
            }
        }

        private sealed class DoneHandle(Task completion) : IDemoQueueHandle
        {
            public Guid Id { get; } = Guid.NewGuid();
            public DemoQueueItemState State => completion.IsCompleted ? DemoQueueItemState.Completed : DemoQueueItemState.Queued;
            public Task Completion => completion;

            public void Cancel()
            {
            }
        }
    }

    // The real composition root (App.ComposeServices) over a throwaway config dir (or the caller's copy), with
    // the processing queue replaced by the inline double. StartPacks is the test's to call.
    private static async Task WithContainer(string? seedSettingsJson, Func<ServiceProvider, InlineQueue, SettingsService, Task> body,
        string? configDir = null)
    {
        string dir = configDir ?? Path.Combine(Path.GetTempPath(), "dvstrattoggle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (seedSettingsJson is not null && configDir is null)
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), seedSettingsJson);
        }

        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                FeatureCatalog.Compose(FeaturePacks.Default);
                InlineQueue queue = new();
                ServiceCollection services = App.ComposeServices(new DesktopWindowService(() => null), FeaturePacks.Default);
                services.Replace(ServiceDescriptor.Singleton<IDemoProcessingQueue>(_ => queue));
                ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
                try
                {
                    QueueWork.Ambient = provider.GetRequiredService<IDemoProcessingQueue>();
                    provider.GetRequiredService<DemoEvaluationCoordinator>();
                    await body(provider, queue, provider.GetRequiredService<SettingsService>());
                }
                finally
                {
                    QueueWork.Ambient = null;
                    provider.Dispose();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
            if (configDir is null)
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }
}
