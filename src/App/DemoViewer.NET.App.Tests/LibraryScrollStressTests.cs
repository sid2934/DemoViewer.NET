#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.Views.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Regression stress for the virtualized library browsers: rapid scrolling over a large library
///     must not throw (reported crash: "if I scroll on the library too quickly it crashes").
///     Drives the card grid's and list's ScrollViewer through large offset jumps: the recycling
///     path VirtualizingStackPanel exercises on a fast wheel/trackpad fling.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class LibraryScrollStressTests
{
    private static readonly Action<Action> _inline = a => a();

    [Test]
    public async Task FastScroll_CardsAndList_DoesNotThrow()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "dvlibscroll_" + Guid.NewGuid().ToString("N") + ".json");
        Exception? failure = null;
        int cardSweeps = 0, listSweeps = 0;

        await HeadlessSession.RunOnUi(() =>
        {
            using DemoLibraryService svc = new(_inline, tempData);
            LibraryTabViewModel vm = new(
                svc,
                _ => Task.CompletedTask,
                () => Task.FromResult<IReadOnlyList<string>>([]));

            List<DemoEntry> bulk = new(500);
            for (int i = 0; i < 500; i++)
            {
                bulk.Add(new DemoEntry
                {
                    FilePath = $"/demos/d{i}.dem",
                    FileName = $"d{i}.dem",
                    Directory = "/demos",
                    FileSizeBytes = 100_000_000,
                    Modified = new DateTime(2026, 7, 1).AddMinutes(-i),
                    MapName = i % 3 == 0 ? "de_nuke" : i % 3 == 1 ? "de_dust2" : "de_mirage",
                    ServerName = "srv",
                    Players = ["p1", "p2", "p3", "p4", "p5"],
                    DurationSeconds = 3000,
                    // A live-update mix: one demo mid-index (animated bar), some failed.
                    State = i == 7 ? DemoIndexState.Indexing
                        : i % 41 == 0 ? DemoIndexState.Failed
                        : DemoIndexState.Indexed
                });
            }

            svc.Entries.AddRange(bulk);

            LibraryTabView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 900,
                Height = 640,
                Content = view
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            try
            {
                cardSweeps = StressScroll(view);

                vm.IsListView = true;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                listSweeps = StressScroll(view);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            return Task.CompletedTask;
        });

        Console.WriteLine($"[libscroll] cardSweeps={cardSweeps} listSweeps={listSweeps} failure={failure?.GetType().Name}");
        await Assert.That(failure).IsNull().Because(failure?.ToString() ?? "no failure");
        await Assert.That(cardSweeps).IsGreaterThan(0);
        await Assert.That(listSweeps).IsGreaterThan(0);
    }

    // Jump the visible ListBox's scroll offset across its whole extent in large, alternating
    // strides (down, up, random-ish jumps), pumping layout+render between steps: the same
    // realize/recycle churn a fast trackpad fling produces.
    private static int StressScroll(LibraryTabView view)
    {
        ScrollViewer? scroll = view.GetVisualDescendants()
            .OfType<ListBox>()
            .Where(lb => lb.IsEffectivelyVisible)
            .Select(lb => lb.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault())
            .FirstOrDefault(s => s is not null);
        if (scroll is null)
        {
            return 0;
        }

        int sweeps = 0;
        double extent = Math.Max(1, scroll.Extent.Height - scroll.Viewport.Height);
        double[] pattern = [0.25, 0.9, 0.1, 1.0, 0.5, 0.0, 0.75, 0.33, 1.0, 0.0];
        foreach (double frac in pattern)
        {
            scroll.Offset = new Vector(0, extent * frac);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            sweeps++;
        }

        // Fine-grained fling: many small consecutive steps downward then upward.
        for (double y = 0; y <= extent; y += extent / 40)
        {
            scroll.Offset = new Vector(0, y);
            Dispatcher.UIThread.RunJobs();
            sweeps++;
        }

        for (double y = extent; y >= 0; y -= extent / 40)
        {
            scroll.Offset = new Vector(0, y);
            Dispatcher.UIThread.RunJobs();
            sweeps++;
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return sweeps;
    }

    /// <summary>
    ///     The real crash scenario: scrolling WHILE the background indexer mutates the
    ///     library: per-entry state/field updates on realized cards, new entries arriving
    ///     (extent changes under a deep scroll offset), and the filter projection rebuilding
    ///     (FilteredEntries/CardRows Clear+Add while scrolled far down).
    /// </summary>
    [Test]
    public async Task FastScroll_WhileIndexerMutates_DoesNotThrow()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "dvlibscrollm_" + Guid.NewGuid().ToString("N") + ".json");
        Exception? failure = null;
        int steps = 0;

        await HeadlessSession.RunOnUi(() =>
        {
            using DemoLibraryService svc = new(_inline, tempData);
            LibraryTabViewModel vm = new(
                svc,
                _ => Task.CompletedTask,
                () => Task.FromResult<IReadOnlyList<string>>([]));

            List<DemoEntry> bulk = new(300);
            for (int i = 0; i < 300; i++)
            {
                bulk.Add(MakeEntry(i));
            }

            svc.Entries.AddRange(bulk);

            LibraryTabView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 900,
                Height = 640,
                Content = view
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            try
            {
                ScrollViewer? scroll = view.GetVisualDescendants()
                    .OfType<ListBox>()
                    .Where(lb => lb.IsEffectivelyVisible)
                    .Select(lb => lb.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault())
                    .FirstOrDefault(s => s is not null);

                int next = 300;
                for (int step = 0; step < 120 && scroll is not null; step++)
                {
                    // REAL input pipeline: wheel events (gesture → smooth scroll → anchoring), the
                    // path a fast trackpad fling takes, plus periodic hard offset jumps.
                    window.MouseWheel(new Point(450, 400),
                        new Vector(0, step % 2 == 0 ? -12 : -3));
                    if (step % 9 == 0)
                    {
                        window.MouseWheel(new Point(450, 400), new Vector(0, 40));
                    }

                    double extent = Math.Max(1, scroll.Extent.Height - scroll.Viewport.Height);
                    if (step % 11 == 0)
                    {
                        scroll.Offset = new Vector(0, extent * (step * 37 % 100 / 100.0));
                    }

                    // Indexer-style mutations interleaved with the scroll churn:
                    DemoEntry entry = svc.Entries[step % svc.Entries.Count];
                    switch (step % 5)
                    {
                        case 0: // tier-2 completes on some entry (field posts on a possibly-realized card)
                            entry.State = DemoIndexState.Indexing;
                            entry.Players = ["a", "b", "c", "d", "e"];
                            entry.DurationSeconds = 2400 + step;
                            entry.State = DemoIndexState.Indexed;
                            break;
                        case 2: // new demos discovered mid-scroll → extent changes + filter rebuild
                            svc.Entries.AddRange([MakeEntry(next++), MakeEntry(next++)]);
                            break;
                        case 4: // a single add (per-item CollectionChanged path)
                            svc.Entries.Add(MakeEntry(next++));
                            break;
                    }

                    Dispatcher.UIThread.RunJobs();
                    if (step % 7 == 0)
                    {
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        Dispatcher.UIThread.RunJobs();
                    }

                    steps++;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            return Task.CompletedTask;
        });

        Console.WriteLine($"[libscrollm] steps={steps} failure={failure?.GetType().Name}");
        await Assert.That(failure).IsNull().Because(failure?.ToString() ?? "no failure");
        await Assert.That(steps).IsGreaterThan(0);
    }

    private static DemoEntry MakeEntry(int i) => new()
    {
        FilePath = $"/demos/d{i}.dem",
        FileName = $"d{i}.dem",
        Directory = "/demos",
        FileSizeBytes = 100_000_000,
        Modified = new DateTime(2026, 7, 1).AddMinutes(-i),
        MapName = i % 3 == 0 ? "de_nuke" : i % 3 == 1 ? "de_dust2" : "de_mirage",
        ServerName = "srv",
        Players = ["p1", "p2", "p3", "p4", "p5"],
        DurationSeconds = 3000,
        State = i == 7 ? DemoIndexState.Indexing
            : i % 41 == 0 ? DemoIndexState.Failed
            : DemoIndexState.Indexed
    };
}
