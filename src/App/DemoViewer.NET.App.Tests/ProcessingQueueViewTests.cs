#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.DemoProcessing;
using DemoViewer.NET.Views.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The queue flyout over a real queue holding every kind of job: a running mine with progress, queued clips,
///     pack export, migration and demo parse, a finished parse and a failed clip batch. Rendered to
///     <c>queue-flyout-mixed.png</c> under the artifact directory.
/// </summary>
[NotInParallel]
public class ProcessingQueueViewTests
{
    private static ParsedDemo SyntheticDemo() => SyntheticParsedDemo.Create(
        [], [], new Dictionary<int, PlayerInfo>(), null,
        "de_test", 0, 1f / 64, "test",
        "test", "csgo", 0, 0, 0,
        "valve_demo_2", "", "", DemoProfile.Unknown);

    private static async Task PumpUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            Playback2DTimelineHarness.Pump(1);
            await Task.Delay(10);
        }
    }

    [Test]
    [Category("Integration")]
    public async Task TheFlyout_ShowsEveryKind_WithItsTitleStateAndProgress() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using HeavyJobGate gate = new();
            // Two declared extension kinds, labelled as the Strat Book labels its own, so the flyout shows the chips a
            // real library shows.
            DemoViewer.NET.Extensions.JobKindRegistry kinds = DemoViewer.NET.Extensions.JobKindRegistry.Build([new KindsExtension()]);
            using DemoProcessingQueue queue = new(gate, parseFile: _ => SyntheticDemo(), compactHeap: () => Task.CompletedTask,
                jobKinds: kinds);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);

            IDemoQueueHandle done = queue.SubmitBackground(new DemoProcessingRequest("/demos/navi-vs-vitality-m2.dem",
                "library", DemoJobPriority.Background, 1, _ => { }, null, "navi-vs-vitality-m2.dem"));
            IDemoQueueHandle failed = queue.SubmitJob(new QueueJobRequest(QueueJobKind.Extension,
                "Lineup clips: de_nuke, 3 clips from g2-vs-mouz-m1.dem", "lineup-clips", DemoJobPriority.Background,
                _ => Task.FromException(new InvalidOperationException("no map bundle for de_nuke")),
                ExtensionKind: KindsExtension.Clips));
            await PumpUntilAsync(() => done.Completion.IsCompleted && failed.Completion.IsCompleted
                                         && queue.QueuedCount + queue.RunningCount == 0, "the finished items and the drain compaction");

            IDemoQueueHandle mining = queue.SubmitJob(new QueueJobRequest(QueueJobKind.Extension, "Strat mining: library",
                "strat-mining", DemoJobPriority.UserRequested, async job =>
                {
                    job.Report(48, 366, "48 of 366 demos");
                    reported.TrySetResult();
                    await release.Task.WaitAsync(job.CancellationToken);
                }, ExtensionKind: KindsExtension.Mining));
            await PumpUntilAsync(() => reported.Task.IsCompleted, "the mine to report");

            queue.Pause();
            queue.SubmitJob(new QueueJobRequest(QueueJobKind.Extension,
                "Lineup clips: de_mirage, 6 clips from faze-vs-spirit-m1.dem", "lineup-clips", DemoJobPriority.Background,
                _ => Task.CompletedTask, "lineup-clips", "/demos/faze-vs-spirit-m1.dem", ExtensionKind: KindsExtension.Clips));
            queue.SubmitJob(new QueueJobRequest(QueueJobKind.PackExport, "Pack export: 12 segments to review.mp4",
                "review", DemoJobPriority.UserRequested, _ => Task.CompletedTask, Target: "/exports/review.mp4"));
            queue.SubmitJob(new QueueJobRequest(QueueJobKind.SidecarMigration, "Sidecar format: compress cached files",
                "demo-cache", DemoJobPriority.Background, _ => Task.CompletedTask));
            queue.SubmitBackground(new DemoProcessingRequest("/demos/spirit-vs-mongolz-m3.dem", "library, highlights",
                DemoJobPriority.Background, 2, _ => { }, null, "spirit-vs-mongolz-m3.dem"));
            await PumpUntilAsync(() => queue.Items.Count == 7, "the mirror");

            using ProcessingQueueStatusViewModel vm = new(queue, () => { }, jobKinds: kinds);
            Border host = new()
            {
                Padding = new Thickness(20),
                Child = new Border
                {
                    Classes = { "card-flyout" },
                    Child = new ProcessingQueueStatusView { DataContext = vm }
                }
            };
            host.Bind(Border.BackgroundProperty, host.GetResourceObservable("ShellBg"));
            Window window = new() { Width = 420, Height = 640, Content = host };
            window.Show();
            Playback2DTimelineHarness.Pump();

            ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single();
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "queue-flyout-mixed.png"),
                new PngBitmapEncoderOptions());

            using (Assert.Multiple())
            {
                await Assert.That(list.ItemCount).IsEqualTo(7);
                await Assert.That(vm.Rows.Select(r => r.KindLabel).Where(k => k.Length > 0).Distinct().Order())
                    .IsEquivalentTo(["clips", "migration", "mining", "pack export"]);
                DemoQueueRowViewModel mine = vm.Rows.Single(r => r.KindLabel == "mining");
                await Assert.That(mine.HasProgress).IsTrue();
                await Assert.That(mine.ProgressValue).IsEqualTo(48.0 / 366);
                await Assert.That(mine.Detail).IsEqualTo("48 of 366 demos");
                await Assert.That(vm.Rows.Single(r => r.StateLabel == "Failed").Error).IsEqualTo("no map bundle for de_nuke");
                await Assert.That(vm.StatusLine).IsEqualTo("1 running · 4 queued · paused: background work held");
            }

            // The row's remove cancels the running job through its token.
            vm.Rows.Single(r => r.KindLabel == "mining").RemoveCommand.Execute(null);
            await PumpUntilAsync(() => mining.Completion.IsCompleted, "the mine to stop");
            await Assert.That(mining.State).IsEqualTo(DemoQueueItemState.Cancelled);
            window.Close();
        });

    /// <summary>
    ///     An open waiting on a background parse it cannot stop: first row, and it names the file it waits for.
    ///     Rendered to <c>queue-flyout-open-waiting.png</c>.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task AnOpenWaitingOnAParse_IsTheFirstRow_AndNamesTheParse() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using HeavyJobGate gate = new();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim release = new();
            using DemoProcessingQueue queue = new(gate, parseFile: _ =>
            {
                started.TrySetResult();
                release.Wait();
                return SyntheticDemo();
            }, parseBytes: _ => SyntheticDemo(), compactHeap: () => Task.CompletedTask);
            try
            {
                queue.SubmitBackground(new DemoProcessingRequest("/demos/navi-vs-vitality-m2.dem", "library",
                    DemoJobPriority.Background, 2, _ => { }, null, "navi-vs-vitality-m2.dem"));
                queue.SubmitBackground(new DemoProcessingRequest("/demos/spirit-vs-mongolz-m3.dem", "library, highlights",
                    DemoJobPriority.Background, 1, _ => { }, null, "spirit-vs-mongolz-m3.dem"));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

                using IDemoOpenTicket open = queue.BeginOpen("/demos/faze-vs-g2-m1.dem", "faze-vs-g2-m1.dem");
                Task<ParsedDemo> parse = open.ParseAsync(new byte[] { 1 });
                using ProcessingQueueStatusViewModel vm = new(queue, () => { });
                await PumpUntilAsync(() => vm.Rows.Count == 3 && vm.Rows[0].HasDetail, "the waiting open");

                Border host = new()
                {
                    Padding = new Thickness(20),
                    Child = new Border
                    {
                        Classes = { "card-flyout" },
                        Child = new ProcessingQueueStatusView { DataContext = vm }
                    }
                };
                host.Bind(Border.BackgroundProperty, host.GetResourceObservable("ShellBg"));
                Window window = new() { Width = 420, Height = 360, Content = host };
                window.Show();
                Playback2DTimelineHarness.Pump();
                window.CaptureRenderedFrame()?.Save(
                    Path.Combine(HeadlessSession.ArtifactDir, "queue-flyout-open-waiting.png"), new PngBitmapEncoderOptions());

                DemoQueueRowViewModel first = vm.Rows[0];
                using (Assert.Multiple())
                {
                    await Assert.That(first.DisplayText).Contains("Open demo: faze-vs-g2-m1.dem");
                    await Assert.That(first.KindLabel).IsEqualTo("open");
                    await Assert.That(first.StateLabel).IsEqualTo("Queued");
                    await Assert.That(first.Detail).IsEqualTo("Waiting for navi-vs-vitality-m2.dem to finish parsing");
                    await Assert.That(first.HasElevatedPriority).IsFalse();
                }

                release.Set();
                await PumpUntilAsync(() => parse.IsCompleted, "the open to parse");
                open.Complete();
                window.Close();
            }
            finally
            {
                release.Set();
            }
        });

    private sealed class KindsExtension : IExtension
    {
        public const string Clips = "test.clips";
        public const string Mining = "test.mining";

        public string Id => "net.test.kinds";
        public string FeatureId => "pack.kinds";
        public IEnumerable<ExtensionFeature> Features => [];
        public IEnumerable<ExtensionJobKind> JobKinds => [new(Clips, "clips", false, 3), new(Mining, "mining", false, 2)];

        public void Register(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}
