#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.Views.Library;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Headless render smoke for the library tab: builds the real <see cref="LibraryTabView" /> over a VM
///     with a couple of injected demos, renders both the card and list views to Skia frames, and asserts
///     they produce visible ink. Catches XAML load / binding / converter errors the compile can't (the
///     folder-remove command binding, the <c>MapAccentConverter</c> x:Static use, the card template).
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ZLibraryRenderTests
{
    private static readonly Action<Action> _inline = a => a();

    private static DemoEntry SampleEntry(string map, string players) => new()
    {
        FilePath = $"/demos/{map}.dem",
        FileName = $"{map}.dem",
        Directory = "/demos",
        FileSizeBytes = 480_000_000,
        Modified = new DateTime(2026, 7, 1, 12, 0, 0),
        MapName = map,
        ServerName = "BLAST.tv Premier CS2 Server",
        Players = players.Split(',').ToList(),
        DurationSeconds = 3375,
        CtScore = 13,
        TScore = 11,
        State = DemoIndexState.Indexed
    };

    [Test]
    public async Task LibraryView_RendersCards_AndList()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "dvlibrender_" + Guid.NewGuid().ToString("N") + ".json");
        int cardInk = 0, listInk = 0;
        (bool Indexing, bool Failed, int ActiveCount) indicatorStates = default;

        await HeadlessSession.RunOnUi(() =>
        {
            using DemoLibraryService svc = new(_inline, tempData);
            LibraryTabViewModel vm = new(
                svc,
                _ => Task.CompletedTask,
                () => Task.FromResult<IReadOnlyList<string>>([]));

            // Inject demos directly (bypassing a real scan) — the VM picks them up via CollectionChanged.
            // One of each indicator state: Indexed (normal), Indexing (animated amber top bar / pulsing
            // dot — property-asserted below, pixels are animation-phase-dependent), Failed (static red).
            svc.Entries.Add(SampleEntry("de_nuke", "ZywOo,apEX,flameZ,Spinx,mezii"));
            svc.Entries.Add(SampleEntry("de_dust2", "s1mple,b1t,Aleksib,iM,jL"));
            DemoEntry indexing = SampleEntry("de_mirage", "x");
            indexing.Players = [];
            indexing.CtScore = null;
            indexing.TScore = null;
            indexing.State = DemoIndexState.Indexing;
            DemoEntry failed = SampleEntry("de_anubis", "x");
            failed.Players = [];
            failed.CtScore = null;
            failed.TScore = null;
            failed.State = DemoIndexState.Failed;
            svc.Entries.Add(indexing);
            svc.Entries.Add(failed);

            // Indicator-state flags are asserted OUTSIDE RunOnUi (it swallows async exceptions).
            indicatorStates = (indexing.IsIndexing, failed.IsFailed, svc.Entries.Count(e => e.IsIndexing));

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

            // Card view (default).
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (window.CaptureRenderedFrame() is { } cards)
            {
                cards.Save(Path.Combine(HeadlessSession.ArtifactDir, "library-cards.png"));
                cardInk = NonBackground(cards);
            }

            // List view.
            vm.IsListView = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (window.CaptureRenderedFrame() is { } list)
            {
                list.Save(Path.Combine(HeadlessSession.ArtifactDir, "library-list.png"));
                listInk = NonBackground(list);
            }

            return Task.CompletedTask;
        });

        Console.WriteLine($"[librender] cardInk={cardInk} listInk={listInk} indicators={indicatorStates}");
        await Assert.That(cardInk).IsGreaterThan(500); // toolbar + cards drew
        await Assert.That(listInk).IsGreaterThan(500); // header + rows drew
        await Assert.That(indicatorStates.Indexing).IsTrue(); // the Indexing card exposes the animated-bar flag
        await Assert.That(indicatorStates.Failed).IsTrue(); // the Failed card exposes the static red flag
        await Assert.That(indicatorStates.ActiveCount).IsEqualTo(1); // one-at-a-time invariant the design leans on
    }

    /// <summary>
    ///     Headless render smoke for the P3.2b landing hero: with NO folders configured, the empty state is
    ///     a proper hero — app title + primary "Open Demo…" + a recents list (one dimmed missing row) + the
    ///     drop hint. Asserts the hero produces visible ink and that the recents projected (incl. the
    ///     grey-out Exists flag), catching XAML/binding faults the compile can't (the recents template, the
    ///     RowOpacity/Meta bindings, the DragDrop.AllowDrop attach).
    /// </summary>
    [Test]
    public async Task LibraryView_RendersLandingHero_WithRecents()
    {
        string tempLib = Path.Combine(Path.GetTempPath(), "dvlibhero_" + Guid.NewGuid().ToString("N") + ".json");
        // recents persist to the single config file, so the seam is a throwaway config dir.
        string tempRecents = Path.Combine(Path.GetTempPath(), "dvhrec_" + Guid.NewGuid().ToString("N"));
        string realDemo = Path.Combine(Path.GetTempPath(), "dvhreal_" + Guid.NewGuid().ToString("N") + ".dem");
        File.WriteAllText(realDemo, "x");

        RecentFilesStore recents = new(new SettingsService(tempRecents));
        recents.RecordOpen(realDemo, "de_mirage"); // exists → shown normally
        recents.RecordOpen("/demos/gone.dem", "de_nuke"); // never existed → dimmed, sorts to front

        int heroInk = 0;
        (bool HasNoFolders, int RecentCount, bool FirstMissing) state = default;
        bool ctaHittable = false;

        try
        {
            await HeadlessSession.RunOnUi(() =>
            {
                using DemoLibraryService svc = new(_inline, tempLib); // no folders → hero
                LibraryTabViewModel vm = new(
                    svc,
                    _ => Task.CompletedTask,
                    () => Task.FromResult<IReadOnlyList<string>>([]),
                    () => Task.CompletedTask,
                    recents);

                // Asserted OUTSIDE RunOnUi (it swallows async exceptions).
                state = (vm.HasNoFolders, vm.RecentFiles.Count, !vm.RecentFiles[0].Exists);

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

                if (window.CaptureRenderedFrame() is { } hero)
                {
                    hero.Save(Path.Combine(HeadlessSession.ArtifactDir, "library-hero.png"));
                    heroInk = NonBackground(hero);
                }

                // Clickability: the empty card ListBox (IsCardView defaults true, transparent = hit-testable)
                // is an earlier sibling filling the same space; the hero must be declared LAST so it hit-tests
                // on TOP, or the primary CTA is dead. Hit-test the primary Button's centre → the topmost visual
                // there must be the Button (or a descendant), NOT the underlying ListBox.
                // The hero's primary CTA — the effectively-visible one (there's a second Classes="primary"
                // Open-Demo in the actions strip, collapsed in the empty state → skip it).
                Button? primary = FindPrimary(view);
                if (primary is not null)
                {
                    Point centre = new(primary.Bounds.Width / 2, primary.Bounds.Height / 2);
                    if (primary.TranslatePoint(centre, view) is { } p && view.GetVisualAt(p) is { } hit)
                    {
                        ctaHittable = hit == primary || hit.GetVisualAncestors().Contains(primary);
                    }
                }

                return Task.CompletedTask;
            });
        }
        finally
        {
            TryDelete(tempLib);
            TryDeleteDir(tempRecents);
            TryDelete(realDemo);
        }

        Console.WriteLine($"[libhero] ink={heroInk} state={state} ctaHittable={ctaHittable}");
        await Assert.That(state.HasNoFolders).IsTrue(); // no folders → the hero is the body
        await Assert.That(state.RecentCount).IsEqualTo(2); // both recents projected
        await Assert.That(state.FirstMissing).IsTrue(); // most-recent (gone.dem) is the dimmed/missing row
        await Assert.That(heroInk).IsGreaterThan(500); // title + CTA + recents drew visible ink
        await Assert.That(ctaHittable).IsTrue(); // and the primary CTA is actually clickable (on top)
    }

    // The effectively-visible primary "Open Demo…" button (Classes="primary"), found by walking the visual
    // tree. Skips collapsed primaries (the actions-strip Open-Demo is hidden in the empty/hero state).
    private static Button? FindPrimary(Visual root)
    {
        if (root is Button { Classes: var c } b && c.Contains("primary") && b.IsEffectivelyVisible)
        {
            return b;
        }

        foreach (Visual child in root.GetVisualChildren())
        {
            if (FindPrimary(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void TryDeleteDir(string dir)
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

    /// <summary>
    ///     The virtualization proof: 400 demos must NOT realize 400 card controls. The card grid is a
    ///     virtualized list of row-chunks, so the realized ListBoxItem count stays bounded by the
    ///     viewport (rows visible + recycle buffer) regardless of library size. Same for the list view.
    /// </summary>
    [Test]
    public async Task LargeLibrary_RealizesOnlyViewportContainers()
    {
        string tempData = Path.Combine(Path.GetTempPath(), "dvlibvirt_" + Guid.NewGuid().ToString("N") + ".json");
        int cardRows = 0, realizedCardRows = 0, realizedListRows = 0, totalEntries = 0;

        await HeadlessSession.RunOnUi(() =>
        {
            using DemoLibraryService svc = new(_inline, tempData);
            LibraryTabViewModel vm = new(
                svc,
                _ => Task.CompletedTask,
                () => Task.FromResult<IReadOnlyList<string>>([]));

            List<DemoEntry> bulk = new(400);
            for (int i = 0; i < 400; i++)
            {
                DemoEntry sample = SampleEntry(i % 2 == 0 ? "de_nuke" : "de_dust2", "p1,p2,p3,p4,p5");
                bulk.Add(new DemoEntry
                {
                    FilePath = $"/demos/d{i}.dem",
                    FileName = $"d{i}.dem",
                    Directory = sample.Directory,
                    FileSizeBytes = sample.FileSizeBytes,
                    Modified = sample.Modified,
                    MapName = sample.MapName,
                    ServerName = sample.ServerName,
                    Players = sample.Players,
                    DurationSeconds = sample.DurationSeconds,
                    CtScore = sample.CtScore,
                    TScore = sample.TScore,
                    State = sample.State
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

            totalEntries = vm.FilteredEntries.Count;
            cardRows = vm.CardRows.Count;
            realizedCardRows = CountRealized(view);

            vm.IsListView = true;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            realizedListRows = CountRealized(view);

            return Task.CompletedTask;
        });

        Console.WriteLine($"[libvirt] entries={totalEntries} cardRows={cardRows} "
                          + $"realizedCardRows={realizedCardRows} realizedListRows={realizedListRows}");
        await Assert.That(totalEntries).IsEqualTo(400);
        await Assert.That(cardRows).IsGreaterThanOrEqualTo(100); // 400 entries / 4 columns
        // Viewport is ~640px: ~4 card rows (160px) / ~22 list rows (29px) + recycle buffer. The bound
        // that matters is "far below the total", not the exact count.
        await Assert.That(realizedCardRows).IsGreaterThan(0);
        await Assert.That(realizedCardRows).IsLessThan(25);
        await Assert.That(realizedListRows).IsGreaterThan(0);
        await Assert.That(realizedListRows).IsLessThan(80);
    }

    private static int CountRealized(Visual root)
    {
        int n = 0;
        foreach (Visual child in root.GetVisualChildren())
        {
            n += CountRealized(child);
        }

        return n + (root is ListBoxItem ? 1 : 0);
    }

    private static int NonBackground(WriteableBitmap bmp)
    {
        const byte BgR = 0x08, BgG = 0x08, BgB = 0x16; // ShellBg #080816
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int n = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            if (Math.Abs(buffer[i] - BgB) > 8 || Math.Abs(buffer[i + 1] - BgG) > 8 || Math.Abs(buffer[i + 2] - BgR) > 8)
            {
                n++;
            }
        }

        return n;
    }
}
