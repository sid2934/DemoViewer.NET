#region

using Avalonia.Threading;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     One read per demo per session, on the real composition root with every Strat Book pass on (round facts,
///     the round index sweep, suggested tags and the grenade sweep) and the highlights backlog on: an import
///     reads each demo once, a demo open while the library finds it is read once, and a demo the library finds
///     still queued when the user opens it is read once, by the open. A demo opened after its import finished is
///     read again by the open and by nothing else. A later re-check of the whole library reads nothing, so no
///     pass is left wanting a demo it just ran on. Demos are read in place.
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

    // Round Facts and the round index wait on the Library's parse stamp; none of them may sit the import's
    // read out and need a second one.
    [Test]
    [Arguments("trimmed")]
    [Arguments("benchmarks")]
    public async Task AFreshImport_RunsTheLibraryRoundFactsAndTheRoundIndex_OnItsOneRead(string corpus)
    {
        string folder = Corpus(corpus);
        await WithApp(folder, async provider =>
        {
            DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
            PassOutcomes outcomes = new(provider.GetRequiredService<DemoScheduler>());
            await provider.GetRequiredService<DemoLibraryService>().RescanAsync();
            await SettleAsync(queue);
            provider.GetRequiredService<DemoScheduler>().RecheckAll();
            await SettleAsync(queue);

            await AssertEachReadOnce(provider, queue, folder);
            await AssertRanOnTheRead(provider, outcomes, folder);
        });
    }

    // A demo the Library indexed whose unified-cache row is gone has no parse stamp, and every pass after the
    // Library waits on that stamp. The Library reads it again, once, with those passes on the same read.
    [Test]
    public async Task ADemoWhoseParseStampIsGone_IsReadOnce_WithEveryWaitingPassOnThatRead()
    {
        string folder = Corpus("trimmed");
        string config = Path.Combine(Path.GetTempPath(), "dvparseonce_" + Guid.NewGuid().ToString("N"));
        try
        {
            await WithApp(folder, async provider =>
            {
                DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
                DemoLibraryService library = provider.GetRequiredService<DemoLibraryService>();
                await library.RescanAsync();
                await SettleAsync(queue);
                library.Save();
            }, configDir: config);

            await WithApp(folder, async provider =>
            {
                DemoCacheStore cache = provider.GetRequiredService<DemoCacheStore>();
                foreach (string demo in Demos(folder))
                {
                    cache.Remove(demo);
                }

                DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
                PassOutcomes outcomes = new(provider.GetRequiredService<DemoScheduler>());
                await provider.GetRequiredService<DemoLibraryService>().RescanAsync();
                await SettleAsync(queue);
                provider.GetRequiredService<DemoScheduler>().RecheckAll();
                await SettleAsync(queue);

                await AssertEachReadOnce(provider, queue, folder);
                await AssertRanOnTheRead(provider, outcomes, folder, roundIndex: false);
            }, configDir: config);
        }
        finally
        {
            try
            {
                Directory.Delete(config, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Test]
    public async Task AnImportWithLineupClipsOn_RendersTheClipsOnTheImportsReads()
    {
        string folder = Corpus("benchmarks");
        string? clips = null;
        await WithApp(folder, async provider =>
        {
            DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
            clips = Path.Combine(AppPaths.ConfigRoot!, LineupClipService.DirectoryName);
            await provider.GetRequiredService<DemoLibraryService>().RescanAsync();
            await SettleAsync(queue);
            provider.GetRequiredService<DemoScheduler>().RecheckAll();
            await SettleAsync(queue);

            string[] gifs = Directory.Exists(clips) ? Directory.GetFiles(clips, "*.gif") : [];
            Console.WriteLine($"lineup clips written: {gifs.Length}");
            await Assert.That(gifs.Length).IsGreaterThan(0).Because("repeated lineups in the corpus get their clips");
            await AssertEachReadOnce(provider, queue, folder);
        }, renderClips: true);
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

    // The import's parse is gone once its visit ends, so the open reads the demo again. That read is the only
    // one: no pass runs again on the open, and a re-check after it reads nothing.
    [Test]
    public async Task AnOpenAfterTheImportFinished_IsTheDemosOnlyOtherRead()
    {
        string folder = Corpus("trimmed");
        string opened = Demos(folder)[0];
        await WithApp(folder, async provider =>
        {
            DemoProcessingQueue queue = provider.GetRequiredService<DemoProcessingQueue>();
            MainViewModel shell = provider.GetRequiredService<MainViewModel>();
            await provider.GetRequiredService<DemoLibraryService>().RescanAsync();
            await SettleAsync(queue);
            await Assert.That(queue.ParseCount(opened)).IsEqualTo(1).Because("the import read it once");

            await shell.LoadDemoFromPathAsync(opened);
            await SettleAsync(queue);
            provider.GetRequiredService<DemoScheduler>().RecheckAll();
            await SettleAsync(queue);

            await Assert.That(shell.LoadedDemoPath).IsEqualTo(opened);
            await Assert.That(queue.ParseCount(opened)).IsEqualTo(2).Because("the open's own parse is the only other read");
            await AssertEachReadOnce(provider, queue, folder, except: opened);
        });
    }

    private static async Task AssertEachReadOnce(IServiceProvider provider, DemoProcessingQueue queue, string folder,
        string? except = null)
    {
        DemoCacheStore cache = provider.GetRequiredService<DemoCacheStore>();
        using (Assert.Multiple())
        {
            foreach (string demo in Demos(folder))
            {
                DemoCacheIndexEntry? entry = cache.TryGetIndex(demo);
                Console.WriteLine($"{Path.GetFileName(demo)}: reads {queue.ParseCount(demo)}, parsed {entry?.ParseSchema}, "
                                  + $"round facts {entry?.HasRoundFacts()}, round index {provider.GetRequiredService<RoundIndexStore>().Stamp(demo)?.State}, "
                                  + $"grenades {provider.GetRequiredService<GrenadeStore>().Stamp(demo)?.State}, "
                                  + $"suggestions {provider.GetRequiredService<ProposalStore>().Stamp(demo)?.State}");
                await Assert.That(entry?.ParseSchema ?? 0).IsGreaterThan(0).Because($"the library indexed {demo}");
                await Assert.That(provider.GetRequiredService<GrenadeStore>().Stamp(demo)).IsNotNull().Because($"the grenade sweep walked {demo}");
                if (!string.Equals(demo, except, StringComparison.Ordinal))
                {
                    await Assert.That(queue.ParseCount(demo)).IsEqualTo(1).Because($"{demo} is read once this session");
                }
            }
        }
    }

    // The round index keeps its own store; a session that kept it has nothing for the index to redo.
    private static async Task AssertRanOnTheRead(IServiceProvider provider, PassOutcomes outcomes, string folder,
        bool roundIndex = true)
    {
        DemoCacheStore cache = provider.GetRequiredService<DemoCacheStore>();
        using (Assert.Multiple())
        {
            foreach (string demo in Demos(folder))
            {
                await Assert.That(outcomes.Of(demo, "library")).IsEquivalentTo([PassOutcome.Ran]).Because($"{demo}: library");
                await Assert.That(outcomes.Of(demo, RoundFactsEvaluator.EvaluatorId)).IsEquivalentTo([PassOutcome.Ran])
                    .Because($"{demo}: round facts ran on the Library's read instead of sitting it out");
                if (roundIndex && cache.TryGetIndex(demo)?.HasRoundFacts() == true)
                {
                    await Assert.That(outcomes.Of(demo, RoundIndexEvaluator.EvaluatorId)).IsEquivalentTo([PassOutcome.Ran])
                        .Because($"{demo}: the round index ran on the same read as the rows it indexes");
                }
            }
        }
    }

    // Every pass outcome per demo, in the order the visits ended them.
    private sealed class PassOutcomes
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Demo, string Pass, PassOutcome Outcome)> _seen = new();

        public PassOutcomes(DemoScheduler scheduler) => scheduler.PassFinished += (demo, pass, outcome) => _seen.Enqueue((demo, pass, outcome));

        public PassOutcome[] Of(string demo, string pass) =>
            [.. _seen.Where(s => s.Demo == demo && s.Pass == pass).Select(s => s.Outcome)];
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

    // Every Strat Book pass and the highlights backlog on; lineup clips only where a test asks for them.
    private static string Settings(string folder, bool renderClips) =>
        $$"""
          {
            "FirstRunCompleted": true,
            "Library": { "Folders": [ {{System.Text.Json.JsonSerializer.Serialize(folder)}} ] },
            "Highlights": { "BackgroundScan": true },
            "Situations": { "BackgroundIndex": true },
            "Grenades": { "BackgroundIndex": true, "RenderLineupClips": {{(renderClips ? "true" : "false")}} },
            "Playback2D": { "SuggestedTagsBackground": true }
          }
          """;

    // A given config dir is kept for the caller's next session; otherwise a throwaway one is made and deleted.
    private static async Task WithApp(string folder, Func<ServiceProvider, Task> body, bool renderClips = false,
        string? configDir = null)
    {
        string dir = configDir ?? Path.Combine(Path.GetTempPath(), "dvparseonce_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), Settings(folder, renderClips));
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
            }, TimeSpan.FromMinutes(20));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prev);
            try
            {
                if (configDir is null)
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
