#region

using Avalonia.Threading;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     One read per demo per session, on the real composition root with every Strat Book pass on (round facts,
///     the round index sweep, suggested tags and the grenade sweep) and the highlights backlog on: an import
///     reads each demo once, a demo open while the library finds it is read once, and a demo the library finds
///     still queued when the user opens it is read once, by the open. A later re-check of the whole library
///     reads nothing, so no pass is left wanting a demo it just ran on. Demos are read in place.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ParseOncePerDemoTests
{
    [Test]
    [Arguments("trimmed")]
    [Arguments("benchmarks")]
    public async Task AnImport_ReadsEachDemoOnce(string corpus)
    {
        string folder = Corpus(corpus);
        await WithApp(folder, async provider =>
        {
            DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
            await provider.GetRequiredService<DemoLibraryService>().RescanAsync();
            await SettleAsync(queue);
            provider.GetRequiredService<DemoScheduler>().RecheckAll();
            await SettleAsync(queue);

            await AssertEachReadOnce(provider, queue, folder);
        });
    }

    [Test]
    public async Task ADemoOpenWhileTheLibraryFindsIt_IsReadOnce_ByTheOpen()
    {
        string folder = Corpus("trimmed");
        string opened = Demos(folder)[0];
        await WithApp(folder, async provider =>
        {
            DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
            MainViewModel shell = provider.GetRequiredService<MainViewModel>();
            await shell.LoadDemoFromPathAsync(opened);
            await SettleAsync(queue);
            await provider.GetRequiredService<DemoLibraryService>().RescanAsync();
            await SettleAsync(queue);

            await Assert.That(shell.LoadedDemoPath).IsEqualTo(opened);
            await AssertEachReadOnce(provider, queue, folder);
        });
    }

    [Test]
    public async Task AnImportStillQueuedWhenTheDemoOpens_RunsOnTheOpen_AndReadsItOnce()
    {
        string folder = Corpus("trimmed");
        string opened = Demos(folder)[0];
        await WithApp(folder, async provider =>
        {
            DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
            MainViewModel shell = provider.GetRequiredService<MainViewModel>();

            // Background work off: the library still finds the demos and the scheduler plans them, but their
            // visits wait in the queue.
            queue.BackgroundEnabled = false;
            await provider.GetRequiredService<DemoLibraryService>().RescanAsync();
            await WaitAsync(() => queue.Snapshot().Any(s => s.Kind == QueueJobKind.DemoProcessing && s.Path == opened
                                                            && s.State == DemoQueueItemState.Queued),
                () => "the import's visit: " + string.Join("; ", queue.Snapshot().Select(s => $"{s.Kind} {s.State} {s.Path} {s.DisplayName}")));

            await shell.LoadDemoFromPathAsync(opened);
            queue.BackgroundEnabled = true;
            await SettleAsync(queue);

            await AssertEachReadOnce(provider, queue, folder);
        });
    }

    private static async Task AssertEachReadOnce(IServiceProvider provider, DemoProcessingQueue queue, string folder)
    {
        DemoCacheStore cache = provider.GetRequiredService<DemoCacheStore>();
        using (Assert.Multiple())
        {
            foreach (string demo in Demos(folder))
            {
                DemoCacheIndexEntry? entry = cache.TryGetIndex(demo);
                Console.WriteLine($"{Path.GetFileName(demo)}: reads {queue.ParseCount(demo)}, parsed {entry?.ParseSchema}, "
                                  + $"round facts {entry?.HasRoundFacts()}, round index {entry?.RoundIndexStamp()?.State}, "
                                  + $"grenades {entry?.GrenadesStamp()?.State}, suggestions {entry?.SuggestionsStamp()?.State}");
                await Assert.That(entry?.ParseSchema ?? 0).IsGreaterThan(0).Because($"the library indexed {demo}");
                await Assert.That(entry?.GrenadesStamp()).IsNotNull().Because($"the grenade sweep walked {demo}");
                await Assert.That(queue.ParseCount(demo)).IsEqualTo(1).Because($"{demo} is read once this session");
            }
        }
    }

    // Idle for a whole second: nothing queued or running, the scheduler's planning included.
    private static async Task SettleAsync(DemoProcessingQueue queue)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(15);
        int quiet = 0;
        while (quiet < 10)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the queue never went idle");
            }

            Dispatcher.UIThread.RunJobs();
            bool busy = queue.Snapshot().Any(s => s.State is DemoQueueItemState.Queued or DemoQueueItemState.Running);
            quiet = busy ? 0 : quiet + 1;
            await Task.Delay(100);
        }
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string> what)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what()}");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
    }

    private static string Corpus(string name)
    {
        string? root = DemoTestHelper.FindRepoRoot();
        string folder = root is null ? "" : Path.Combine(root, "demos", name);
        if (!Directory.Exists(folder) || Demos(folder).Count == 0)
        {
            throw new TUnit.Core.Exceptions.SkipTestException($"no demos under demos/{name}");
        }

        return folder;
    }

    private static List<string> Demos(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*.dem", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)];

    // Every Strat Book pass and the highlights backlog on; lineup clips off, since they render after the index
    // changes, which is a feature of its own and not indexing.
    private static string Settings(string folder) =>
        $$"""
          {
            "FirstRunCompleted": true,
            "Library": { "Folders": [ {{System.Text.Json.JsonSerializer.Serialize(folder)}} ] },
            "Highlights": { "BackgroundScan": true },
            "Situations": { "BackgroundIndex": true },
            "Grenades": { "BackgroundIndex": true, "RenderLineupClips": false },
            "Playback2D": { "SuggestedTagsBackground": true }
          }
          """;

    private static async Task WithApp(string folder, Func<ServiceProvider, Task> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvparseonce_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), Settings(folder));
        string? prev = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                ServiceProvider provider = App.BuildServices(new DesktopWindowService(() => null));
                try
                {
                    await body(provider);
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
