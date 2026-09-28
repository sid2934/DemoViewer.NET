#region

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services.Export.Pack;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.ViewModels.Review;
using DemoViewer.NET.Views.Review;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Times the Review section over a queue the size the owner's grew to: load, open, scroll to the
///     bottom and a lineup-clip batch arriving while it is open. Prints the numbers and renders the
///     section. <c>DV_REVIEW_QUEUE</c> names a copy of a real <c>review-queue.json</c>; without it a
///     synthetic queue of the same shape is used.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ReviewQueuePerfTests
{
    private const int SyntheticClips = 5000;

    [Test]
    public async Task OpenScrollAndABatch_OverAThousandsOfEntriesQueue()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-review-perf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, ReviewQueue.FileName);
        string label;
        if (Environment.GetEnvironmentVariable("DV_REVIEW_QUEUE") is { Length: > 0 } source && File.Exists(source))
        {
            File.Copy(source, file);
            label = "owner";
        }
        else
        {
            ReviewQueue seed = new(null);
            foreach ((string map, int count) in Maps())
            {
                seed.Merge(Enumerable.Range(0, count).Select(i => LineupClip(map, i)), "Lineup clips, " + map,
                    static (_, _) => false);
            }

            await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(
                new ReviewQueueFile { Entries = [.. seed.Entries] }, ReviewQueueFile.JsonOptions));
            label = "synthetic";
        }

        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                // The app has its shell up before the section opens; pay the controls' first-use cost here.
                using (ReviewQueueTabViewModel warm = new(new ReviewQueue(null)))
                {
                    Window first = new() { Width = 400, Height = 300, Content = new ReviewQueueTabView { DataContext = warm } };
                    first.Show();
                    warm.AddSectionCommand.Execute(null);
                    Settle();
                    first.Close();
                }

                Stopwatch sw = Stopwatch.StartNew();
                ReviewQueue queue = new(root);
                await queue.Loaded;
                double load = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                // An exporter makes CanPack true, so PackSummary costs what it does in the app.
                using ReviewQueueTabViewModel vm = new(queue,
                    exportPack: static (_, _, _) => Task.FromException<PackResult>(new NotSupportedException()));
                double build = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                ReviewQueueTabView view = new() { DataContext = vm };
                Window window = new() { Width = 1280, Height = 900, Content = view };
                window.Show();
                Settle();
                double open = sw.Elapsed.TotalMilliseconds;
                int realized = TextBoxes(view);
                Save(window, $"review-queue-{label}-open.png");

                // Every section open: the worst case the owner can ask for.
                sw.Restart();
                vm.ExpandAllCommand.Execute(null);
                Settle();
                double expand = sw.Elapsed.TotalMilliseconds;
                int realizedExpanded = TextBoxes(view);

                // The same section opened again in a second window: open without first-use costs.
                sw.Restart();
                Window again = new() { Width = 1280, Height = 900, Content = new ReviewQueueTabView { DataContext = vm } };
                again.Show();
                Settle();
                double reopen = sw.Elapsed.TotalMilliseconds;
                again.Close();
                sw.Restart();
                Settle();
                double idle = sw.Elapsed.TotalMilliseconds;

                ScrollViewer? scroller = view.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(s => !s.GetVisualAncestors().OfType<TextBox>().Any() && s.Extent.Height > 2000);
                sw.Restart();
                double worstStep = 0;
                if (scroller is not null)
                {
                    for (int step = 1; step <= 40; step++)
                    {
                        Stopwatch one = Stopwatch.StartNew();
                        scroller.Offset = new Vector(0, scroller.Extent.Height * step / 40.0);
                        Settle();
                        worstStep = Math.Max(worstStep, one.Elapsed.TotalMilliseconds);
                    }
                }

                double scroll = sw.Elapsed.TotalMilliseconds;

                // Wheel-like: 60 steps of 300 px from the top, the way a reviewer reads down the list.
                double worstWheel = 0;
                sw.Restart();
                if (scroller is not null)
                {
                    scroller.Offset = default;
                    Settle();
                    sw.Restart();
                    for (int step = 1; step <= 60; step++)
                    {
                        Stopwatch one = Stopwatch.StartNew();
                        scroller.Offset = new Vector(0, step * 300);
                        Settle();
                        worstWheel = Math.Max(worstWheel, one.Elapsed.TotalMilliseconds);
                    }
                }

                double wheel = sw.Elapsed.TotalMilliseconds / 60;
                int realizedScrolled = TextBoxes(view);
                Save(window, $"review-queue-{label}-scrolled.png");

                // What LineupClipService.Plan does after an index change: one Merge per map, 20 new clips
                // each. Once as ten bare merges, once inside Defer as Plan now does.
                sw.Restart();
                MergeBatch(queue, 100_000);
                Settle();
                double bare = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                using (queue.Defer())
                {
                    MergeBatch(queue, 200_000);
                }

                Settle();
                double deferred = sw.Elapsed.TotalMilliseconds;
                vm.CollapseAllCommand.Execute(null);
                Settle();
                Save(window, $"review-queue-{label}-collapsed.png");
                queue.Flush();

                Console.WriteLine($"[review-perf] {label}: entries={queue.Entries.Count} load={load:F0}ms "
                                  + $"vm={build:F0}ms show={open:F0}ms expandAll={expand:F0}ms reopenExpanded={reopen:F0}ms idleFrame={idle:F0}ms "
                                  + $"textboxes open/expanded/scrolled={realized}/{realizedExpanded}/{realizedScrolled} "
                                  + $"scroll40={scroll:F0}ms worstStep={worstStep:F0}ms wheelStep={wheel:F1}ms worstWheel={worstWheel:F0}ms batch bare={bare:F0}ms deferred={deferred:F0}ms "
                                  + $"file={new FileInfo(file).Length / 1024}KiB");
                window.Close();
                await Assert.That(realizedExpanded).IsLessThan(200)
                    .Because("only the rows on screen are realized; the StackPanel realized 19,245 text boxes");
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static int TextBoxes(Visual view) => view.GetVisualDescendants().OfType<TextBox>().Count();

    private static void MergeBatch(ReviewQueue queue, int first)
    {
        foreach ((string map, int _) in Maps())
        {
            queue.Merge(Enumerable.Range(first, 20).Select(i => LineupClip(map, i)), "Lineup clips, " + map,
                static (_, _) => false);
        }
    }

    private static IEnumerable<(string Map, int Count)> Maps() =>
    [
        ("de_ancient", 900), ("de_nuke", 880), ("de_dust2", 830), ("de_mirage", 820), ("de_inferno", 660),
        ("de_overpass", 330), ("de_train", 190), ("de_cache", 170), ("de_anubis", 160), ("de_vertigo", 60)
    ];

    private static ReviewEntry LineupClip(string map, int i) =>
        ReviewEntry.Clip($"/demos/{map}-{i % 37}.dem", i * 400, i * 400 + 270,
            $"Smoke into {map} spot {i}. setpos -613.43 615.90 -78.98; setang -51.19 -169.10 0.00",
            ReviewSources.Lineup, 64, "sha-" + i) with { LineupId = Guid.NewGuid() };

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        string path = Path.Combine(HeadlessSession.ArtifactDir, name);
        frame?.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine($"[review-perf] {path}");
    }
}
