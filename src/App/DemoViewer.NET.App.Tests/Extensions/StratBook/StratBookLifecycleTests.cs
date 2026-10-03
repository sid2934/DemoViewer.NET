#region

using System.Collections.ObjectModel;
using System.Numerics;
using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.StratBook;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     The pack lifecycle (item 3): what <see cref="App.StartPacks" /> runs with the extension on and
///     off, in the real container; what the gated <c>Contribute</c> resolve does to the Situations and
///     Review badges and their startup reads; and what <see cref="StratBookLifecycle.OnShutdown" />
///     touches, called unconditionally (review fix: no "ran at startup" gate).
///     <see cref="NotInParallelAttribute" /> because the container cases pin the process-global config
///     dir, as <see cref="DemoViewer.NET.AppTests.Extensions.StratBook.StratBookPackTests" /> does.
/// </summary>
[NotInParallel]
public class StratBookLifecycleTests
{
    // FirstRunCompleted: the wizard has asked, so StartPacks does not wait for it.
    private const string PackOnSeed = """
                                       {
                                         "FirstRunCompleted": true
                                       }
                                       """;

    private const string PackOffSeed = """
                                        {
                                          "FirstRunCompleted": true,
                                          "Features": { "Overrides": { "pack.stratbook": false } }
                                        }
                                        """;

    // The exact startup items App.axaml.cs used to submit by hand, in order: SituationIndex's own load,
    // GrenadeIndex's own load, then Team Identity's ctor-side load and its StartAsync update. Neither
    // LineupClipService nor TagFactsRefresher submits anything of its own; being resolved is the point.
    private static readonly string[] _expectedStartupLabels =
    [
        "Load: situations index",
        "Load: grenade index",
        "Load: teams",
        "Teams: update"
    ];

    [Test]
    public async Task PackOn_StartPacks_EnqueuesTheSameStartupItems_InTheSameOrder()
    {
        await WithContainer(PackOnSeed, async (provider, recorder) =>
        {
            int before = recorder.Titles.Count;
            App.StartPacks(provider);

            string titles = string.Join(", ", recorder.Titles.Skip(before));
            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            using (Assert.Multiple())
            {
                await Assert.That(titles).IsEqualTo(string.Join(", ", _expectedStartupLabels))
                    .Because("the pack's startup loads, in the order App.axaml.cs ran them by hand");
                await Assert.That(instances.Situations).IsNotNull();
                await Assert.That(instances.Grenades).IsNotNull();
                await Assert.That(instances.Teams).IsNotNull();
                await Assert.That(instances.Lineups).IsNotNull();
                await Assert.That(instances.TagFacts).IsNotNull();
            }
        });
    }

    [Test]
    public async Task PackOff_StartPacks_RunsNoLifecycle_AndBuildsNoneOfItsStartupStores()
    {
        await WithContainer(PackOffSeed, async (provider, recorder) =>
        {
            int before = recorder.Titles.Count;
            App.StartPacks(provider);

            List<string> titles = [.. recorder.Titles.Skip(before)];
            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();
            using (Assert.Multiple())
            {
                await Assert.That(titles).IsEmpty().Because("the lifecycle never ran, so it queued nothing");
                await Assert.That(instances.Situations).IsNull();
                await Assert.That(instances.Grenades).IsNull();
                await Assert.That(instances.Teams).IsNull();
                await Assert.That(instances.Lineups).IsNull();
                await Assert.That(instances.TagFacts).IsNull();
            }
        });
    }

    [Test]
    public async Task ModuleRegistry_WithThePackOn_BuildsWatchedSituations_AndQueuesTheReviewLoad()
    {
        // Contribute runs regardless of the gate (every module is always registered), so this is the
        // regression guard for the review fix: resolving the registry WITH the pack on must build the
        // badge services again (restoring the pre-item-3, item-1-shaped behaviour), not leave them lazy.
        await WithContainer(PackOnSeed, async (provider, recorder) =>
        {
            int before = recorder.Titles.Count;
            ModuleRegistry registry = provider.GetRequiredService<ModuleRegistry>();
            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();

            SituationsModule situations = registry.Modules.OfType<SituationsModule>().Single();
            ReviewQueueModule review = registry.Modules.OfType<ReviewQueueModule>().Single();

            using (Assert.Multiple())
            {
                await Assert.That(instances.Watched).IsNotNull()
                    .Because("the section's own id is on, so Contribute resolved it eagerly, same as item 1's Suggested section");
                await Assert.That(recorder.Titles.Skip(before)).Contains("Load: review queue")
                    .Because("ReviewQueue is core and resolving it eagerly queues its own startup read");
                await Assert.That(situations.CreateTabs(null!).Single().Badge).IsNull()
                    .Because("no watches were saved; the badge computes, it is just empty");
                await Assert.That(review.CreateTabs(null!).Single().Badge).IsNull()
                    .Because("no clips were queued; the badge computes, it is just empty");
            }
        });
    }

    [Test]
    public async Task ModuleRegistry_WithThePackOff_NeverBuildsWatchedSituations_AndShowsNoBadge()
    {
        await WithContainer(PackOffSeed, async (provider, recorder) =>
        {
            int before = recorder.Titles.Count;
            ModuleRegistry registry = provider.GetRequiredService<ModuleRegistry>();
            StratBookPackInstances instances = provider.GetRequiredService<StratBookPackInstances>();

            SituationsModule situations = registry.Modules.OfType<SituationsModule>().Single();
            ReviewQueueModule review = registry.Modules.OfType<ReviewQueueModule>().Single();

            using (Assert.Multiple())
            {
                await Assert.That(instances.Watched).IsNull()
                    .Because("the section's own id cascades off with the pack, so Contribute never resolved the service");
                await Assert.That(recorder.Titles.Skip(before)).DoesNotContain("Load: review queue")
                    .Because("ReviewQueue was never resolved through the gated Contribute path");
                await Assert.That(situations.CreateTabs(null!).Single().Badge).IsNull();
                await Assert.That(review.CreateTabs(null!).Single().Badge).IsNull();
            }
        });
    }

    [Test]
    public async Task MainViewModel_WithThePackOff_QueuesNeitherTeamsNorReviewQueueLoads()
    {
        // BuildShell resolves TeamIdentityService unconditionally (the Library team filter); this proves
        // the core factory's own gate, not Contribute's, keeps "Load: teams" out when the pack is off.
        await WithContainer(PackOffSeed, async (provider, recorder) =>
        {
            int before = recorder.Titles.Count;
            MainViewModel vm = provider.GetRequiredService<MainViewModel>();

            List<string> titles = [.. recorder.Titles.Skip(before)];
            using (Assert.Multiple())
            {
                await Assert.That(titles).DoesNotContain("Load: teams")
                    .Because("the pack is off, so the core factory reads teams.json inline instead of queuing it");
                await Assert.That(titles).DoesNotContain("Load: review queue");
                await Assert.That(vm).IsNotNull().Because("the Library filter still needs a working Team Identity service");
            }
        });
    }

    [Test]
    public async Task OnShutdown_WithNothingBuilt_NeverConstructsTheGrenadeIndex()
    {
        int constructed = 0;
        StratBookPackInstances instances = new();
        ServiceCollection services = new();
        services.AddSingleton(new ModuleRegistry());
        services.AddSingleton(_ =>
        {
            constructed++;
            return new GrenadeIndex(new DemoCacheStore(null));
        });
        using ServiceProvider sp = services.BuildServiceProvider();
        StratBookLifecycle lifecycle = new(sp, instances);

        lifecycle.OnShutdown(TimeSpan.FromSeconds(1));

        await Assert.That(constructed).IsEqualTo(0)
            .Because("OnShutdown must read the tracker, never GetService<GrenadeIndex> fresh");
    }

    [Test]
    public async Task OnShutdown_FlushesTheGrenadeIndex_WhenItWasBuilt()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvstratgrenadeflush_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string lineupsPath = Path.Combine(root, GrenadeLineupStore.FileName);
            DemoCacheStore cache = new(null);
            IndexOneGrenade(cache, "/d/a.dem", "de_dust2");
            // No background drain: the default schedule (Task.Run) writes the moment Query marks the
            // store dirty, racing this test's "before" check. Only an explicit Flush should write here.
            using GrenadeIndex grenades = new(cache, lineups: new GrenadeLineupStore(root), scheduleSave: _ => Task.CompletedTask);
            grenades.Load();
            // Minting the first anchor for this map marks the lineup store dirty (GrenadeIndex.EnsureAssignedLocked).
            _ = grenades.Query(new GrenadeQuery("de_dust2"));

            StratBookPackInstances instances = new() { Grenades = grenades };
            ServiceCollection services = new();
            services.AddSingleton(new ModuleRegistry());
            using ServiceProvider sp = services.BuildServiceProvider();
            StratBookLifecycle lifecycle = new(sp, instances);

            await Assert.That(File.Exists(lineupsPath)).IsFalse().Because("nothing has flushed yet");
            lifecycle.OnShutdown(TimeSpan.FromSeconds(5));
            await Assert.That(File.Exists(lineupsPath)).IsTrue().Because("OnShutdown's FlushLineups wrote the pending lineup");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task OnShutdown_ReachesTheStratBookModule_OnlyAfterItsTabWasActivated()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvstratshutdown_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string indexPath = Path.Combine(root, "index.json");
            StratBookPackInstances instances = new();
            StratStore store = new(root);
            StratBookTabViewModel vm = new(store);
            StratBookModule module = new(() => vm);
            ServiceCollection services = new();
            ModuleRegistry registry = new();
            registry.Register(module);
            services.AddSingleton(registry);
            using ServiceProvider sp = services.BuildServiceProvider();
            StratBookLifecycle lifecycle = new(sp, instances);

            // Never activated: Shutdown is StratBookModule's own no-op (StratBookModuleTests' guard, not
            // this one's). OnShutdown must still reach the registry and the module without throwing.
            lifecycle.OnShutdown(TimeSpan.FromSeconds(1));

            // Activated once (the tab was opened this session): the lifecycle's OnShutdown now reaches
            // the real VM, whose own Shutdown writes the strat index, proving the registry lookup found it.
            module.CreateTabs(null!).Single().ViewModelFactory!.Invoke();
            lifecycle.OnShutdown(TimeSpan.FromSeconds(1));
            await Assert.That(File.Exists(indexPath)).IsTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task OnShutdown_CommitsAStratWrittenAfterTheLifecycleNeverRanAtStartup()
    {
        // The review's blocker 1 scenario: the pack was off at startup (OnEnabledAsync never ran), the
        // user turns it on in Settings, opens Strats and writes a strat, then quits. Nothing here uses
        // "did OnEnabledAsync run" as a gate; the lifecycle is resolved and its OnShutdown called exactly
        // the way the fixed FlushStores does, unconditionally.
        await WithContainer(PackOffSeed, async (provider, _) =>
        {
            // The pack stays off for StartPacks (never called here, matching "never ran at startup").
            StratStore store = provider.GetRequiredService<StratStore>();
            store.Create(StratOwner.Me(), "de_dust2", "T", "default", "Written while the pack was off at startup");

            ModuleRegistry registry = provider.GetRequiredService<ModuleRegistry>();
            StratBookModule module = registry.Modules.OfType<StratBookModule>().Single();
            module.CreateTabs(null!).Single().ViewModelFactory!.Invoke(); // "opened Strats": activates the real tab

            // The fixed shutdown: every pack's lifecycle, unconditionally, by its own id (no Ran check).
            foreach (IFeaturePack pack in FeaturePacks.Default)
            {
                if (provider.GetKeyedService<IPackLifecycle>(pack.Id) is { } lifecycle)
                {
                    lifecycle.OnShutdown(TimeSpan.FromSeconds(5));
                }
            }

            string? stratsRoot = AppPaths.StratsDir;
            await Assert.That(stratsRoot).IsNotNull();
            string indexPath = Path.Combine(stratsRoot!, "index.json");
            await Assert.That(File.Exists(indexPath)).IsTrue()
                .Because("the strat committed through the real store must survive shutdown even though the lifecycle never started");
            await Assert.That(await File.ReadAllTextAsync(indexPath))
                .Contains("Written while the pack was off at startup");
        });
    }

    // One grenade row, no zones, so EnsureAssignedLocked mints a fresh anchor on the first query and
    // marks the lineup store dirty (SaveLineupsLocked). Mirrors GrenadeIndexTests' fixture, trimmed to
    // the one row this test needs.
    private static void IndexOneGrenade(DemoCacheStore cache, string path, string map)
    {
        GrenadeRow row = new()
        {
            Id = "a",
            Kind = GrenadeKind.Smoke,
            ThrowerTeam = 3,
            ReleaseTick = 1000,
            ReleasePosition = WorldPoint.From(new Vector3(512, 288, -160)),
            DetonationPosition = WorldPoint.From(new Vector3(-1400, -1400, -170)),
            EndKind = GrenadeEndKind.Detonated
        };
        DemoCacheRecord record = RoundIndexTestData.ParsedRecord(path, map);
        GrenadeDocument document = new()
        {
            Demo = new GrenadeDemoHeader { Sha256 = null, StableKey = DemoCacheStore.StableKey(path) },
            Grenades = [row]
        };
        cache.WriteSibling(path, GrenadeSidecar.Suffix, GrenadeSidecar.Serialize(document));
        DemoCacheStore.StampGrenades(record);
        record.GrenadeState = DemoAnalysisState.Indexed;
        record.GrenadeCount = document.Grenades.Count;
        record.GrenadeWalker = GrenadeWalker.Version;
        cache.Upsert(record);
    }

    // Records every queue job's title, in submission order, without running its body: the pack's
    // startup loads enqueue before their first await, so the recorded order IS the real order.
    private sealed class RecordingQueue : IDemoProcessingQueue
    {
        public List<string> Titles { get; } = [];

        public List<string> CancelledOwners { get; } = [];

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

        public IDemoQueueHandle SubmitBackground(DemoProcessingRequest request) => throw new NotSupportedException();

        public IDemoQueueHandle SubmitJob(QueueJobRequest request)
        {
            lock (Titles)
            {
                Titles.Add(request.Title);
            }

            Changed?.Invoke();
            return new DoneHandle();
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

        private sealed class DoneHandle : IDemoQueueHandle
        {
            public Guid Id { get; } = Guid.NewGuid();
            public DemoQueueItemState State => DemoQueueItemState.Completed;
            public Task Completion => Task.CompletedTask;

            public void Cancel()
            {
            }
        }
    }

    // The real composition root (App.ComposeServices), with the processing queue replaced by a
    // recorder so a startup item's title is observable without racing its (never run) body, and the
    // config dir pinned to a throwaway folder as every other composition-root test does.
    private static async Task WithContainer(string? seedSettingsJson, Func<ServiceProvider, RecordingQueue, Task> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvstratlifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (seedSettingsJson is not null)
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), seedSettingsJson);
        }

        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                FeatureCatalog.Compose(FeaturePacks.Default); // idempotent (TheCatalog_IsComposedOnce...)
                RecordingQueue recorder = new();
                ServiceCollection services = App.ComposeServices(new DesktopWindowService(() => null), FeaturePacks.Default);
                services.Replace(ServiceDescriptor.Singleton<IDemoProcessingQueue>(_ => recorder));
                ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
                try
                {
                    QueueWork.Ambient = provider.GetRequiredService<IDemoProcessingQueue>();
                    provider.GetRequiredService<DemoEvaluationCoordinator>();
                    await body(provider, recorder);
                }
                finally
                {
                    provider.Dispose();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
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
