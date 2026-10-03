#region

using System.Collections.ObjectModel;
using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     The pack lifecycle (item 3): what <see cref="App.StartPacks" /> runs with the extension on and
///     off, in the real container, and what <see cref="StratBookLifecycle.OnShutdown" /> touches.
///     <see cref="NotInParallelAttribute" /> because the container cases pin the process-global config
///     dir, as <see cref="DemoViewer.NET.AppTests.Extensions.StratBook.StratBookPackTests" /> does.
/// </summary>
[NotInParallel]
public class StratBookLifecycleTests
{
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
        await WithContainer(null, async (provider, recorder) =>
        {
            int before = recorder.Titles.Count;
            App.StartPacks(provider, FeaturePacks.Default);

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
                await Assert.That(provider.GetRequiredService<PackLifecycleRegistry>().Ran).HasCount().EqualTo(1);
            }
        });
    }

    [Test]
    public async Task PackOff_StartPacks_RunsNoLifecycle_AndBuildsNoneOfItsStartupStores()
    {
        const string seed = """
                             {
                               "Features": { "Overrides": { "pack.stratbook": false } }
                             }
                             """;

        await WithContainer(seed, async (provider, recorder) =>
        {
            int before = recorder.Titles.Count;
            App.StartPacks(provider, FeaturePacks.Default);

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
                await Assert.That(instances.Watched).IsNull()
                    .Because("the Situations badge is lazy now, and the hub was never activated");
                await Assert.That(provider.GetRequiredService<PackLifecycleRegistry>().Ran).IsEmpty();
            }
        });
    }

    [Test]
    public async Task EitherWay_TheModuleRegistryAndItsReviewQueue_AreNeverForcedByComposition()
    {
        // ModuleRegistry.Modules runs CreateTabs for every module regardless of the gate (section 3.1 of
        // the plan doc); this is the regression item 3's ReviewQueueModule and SituationsModule changes
        // guard: resolving the registry must not, by itself, force the queue "Load: review queue" used to
        // submit from Contribute's old eager resolve.
        await WithContainer(null, async (provider, recorder) =>
        {
            int before = recorder.Titles.Count;
            _ = provider.GetRequiredService<ModuleRegistry>();
            List<string> titles = [.. recorder.Titles.Skip(before)];
            await Assert.That(titles).DoesNotContain("Load: review queue")
                .Because("the Review badge subscribes on first activation now, not when the registry is built");
        });
    }

    [Test]
    public void OnShutdown_WithNothingBuilt_TouchesNoStore()
    {
        StratBookPackInstances instances = new();
        ServiceCollection services = new();
        services.AddSingleton(new ModuleRegistry());
        using ServiceProvider sp = services.BuildServiceProvider();
        StratBookLifecycle lifecycle = new(sp, instances);

        // Nothing to flush and nothing resolved beyond the registry itself: no exception is the proof
        // that the tracker's null fields, not a fresh GetService, decided what to touch.
        lifecycle.OnShutdown(TimeSpan.FromSeconds(1));
    }

    [Test]
    public void OnShutdown_FlushesTheGrenadeIndex_WhenItWasBuilt()
    {
        DemoCacheStore cache = new(null);
        GrenadeIndex grenades = new(cache);
        StratBookPackInstances instances = new() { Grenades = grenades };
        ServiceCollection services = new();
        services.AddSingleton(new ModuleRegistry());
        using ServiceProvider sp = services.BuildServiceProvider();
        StratBookLifecycle lifecycle = new(sp, instances);

        lifecycle.OnShutdown(TimeSpan.FromSeconds(1)); // must not throw against a session-only (null-root) index
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

    // Records every queue job's title, in submission order, without running its body: the pack's
    // startup loads enqueue before their first await, so the recorded order IS the real order.
    private sealed class RecordingQueue : IDemoProcessingQueue
    {
        public List<string> Titles { get; } = [];

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
