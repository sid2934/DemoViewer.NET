#region

using System.Collections.Concurrent;
using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;
using DemoViewer.NET.Views.PlayerOverview;
using DemoViewer.NET.Views.PlayerOverview.Sections;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview's highlights over an in-memory store: only the subject's slots, a reconnect's second
///     slot included, best first; each demo read once until its scan moves; the unscanned note and the empty
///     state; and the link that opens the Add-clips picker with the subject ticked.
/// </summary>
public class PlayerOverviewHighlightsTests
{
    private const ulong Grim = PlayerOverviewHistoryTests.Grim;
    private const ulong Mate = 76561190000000012;

    private static string Id(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture);

    private static CachedHighlightEvent Fired(int slot, int score, string id, int round = 3, HighlightKind kind = HighlightKind.Highlight) => new()
    {
        RulesetId = "player_stats",
        HighlightId = id,
        PlayerSlot = slot,
        RoundNumber = round,
        Tick = 1000 * round,
        RenderedTitle = $"slot {slot} {id}",
        Score = score,
        Kind = kind
    };

    // Grim holds slot 2 and Mate slot 3 (the history's demos); in demo 1 Grim reconnected onto slot 5 as well.
    private static DemoCacheRecord Demo(int i, string? scan = "cfg")
    {
        DemoCacheRecord record = PlayerOverviewHistoryTests.Demo(i);
        if (i == 1)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = 5, Name = "grim", SteamId64 = Id(Grim), Team = 2 });
        }

        record.ConfigFingerprint = scan;
        if (scan is not null)
        {
            record.Highlights =
            [
                Fired(2, 40 + i, "ace"),
                Fired(3, 90, "clutch"),
                Fired(7, 99, "noone"),
                .. i == 1 ? (CachedHighlightEvent[]) [Fired(5, 70, "after_reconnect", 9)] : [],
                .. i == 0 ? (CachedHighlightEvent[]) [Fired(2, 5, "flash_self", 1, HighlightKind.Funny)] : []
            ];
        }

        return record;
    }

    private static DemoCacheStore Library(int demos, Func<int, string?>? scan = null)
    {
        DemoCacheStore store = new(null);
        using (store.BeginBatch())
        {
            for (int i = 0; i < demos; i++)
            {
                store.Upsert(Demo(i, scan is null ? "cfg" : scan(i)));
            }
        }

        return store;
    }

    private sealed class Shell : IDisposable
    {
        public Shell(DemoCacheStore store, bool withPicker = true)
        {
            Store = store;
            Facts = new HighlightsOnly(store);
            Directory = new PlayerDirectory(store, _ => true);
            Identity = new IdentityService(new SettingsService((string?)null));
            Tab = new PlayerOverviewTabViewModel(new PlayerOverviewServices(Directory, Identity, null,
                Opened.Add, _ => { }, (_, _) => { }, _ => { }, Facts, withPicker ? AddedFor.Add : null));
            Tab.OnActivated(null!);
        }

        public DemoCacheStore Store { get; }
        public HighlightsOnly Facts { get; }
        public PlayerDirectory Directory { get; }
        public IdentityService Identity { get; }
        public PlayerOverviewTabViewModel Tab { get; }
        public List<string> Opened { get; } = [];
        public List<ulong> AddedFor { get; } = [];

        public HighlightsSectionViewModel Highlights => Tab.Reveal<HighlightsSectionViewModel>();

        public void Dispose()
        {
            Tab.Dispose();
            Identity.Dispose();
        }
    }

    // Only Highlights is read by the section; the rest of the facts surface is not its business.
    private sealed class HighlightsOnly(DemoCacheStore store) : IAnalysisFacts
    {
        public ConcurrentQueue<string> Reads { get; } = new();

        public IReadOnlyList<FactKey> Declared => [];

        public IRoundFacts RoundFacts => throw new NotSupportedException();

        public FactStatus Status(string demoPath, FactKey key) => FactStatus.Absent;

        public bool IsCurrent(string demoPath, FactKey key) => false;

        public FactTable? TryGet(string demoPath, FactKey key) => null;

        public IReadOnlyList<LibraryHighlight> Highlights(string demoPath)
        {
            Reads.Enqueue(demoPath);
            return store.TryLoadRecord(demoPath, false) is { } record
                ? [.. record.Highlights.Select(h => new LibraryHighlight(h.RulesetId, h.HighlightId, h.Tick, h.PlayerSlot,
                    h.RoundNumber, h.RenderedTitle, h.Score, h.Kind.ToString()))]
                : [];
        }
    }

    [Test]
    public async Task TheSubjectsSlotsOnly_AReconnectIncluded_BestFirst()
    {
        using Shell shell = new(Library(3));
        shell.Tab.OpenPlayer(Grim);
        await shell.Highlights.Loading;
        HighlightsSectionViewModel section = shell.Highlights;

        using (Assert.Multiple())
        {
            await Assert.That(section.IsVisible).IsTrue();
            await Assert.That(section.Rows.Select(r => (r.Match.Row.Path, r.Highlight.HighlightId))).IsEquivalentTo(
            [
                ("/lib/1.dem", "after_reconnect"), ("/lib/2.dem", "ace"), ("/lib/1.dem", "ace"), ("/lib/0.dem", "ace"), ("/lib/0.dem", "flash_self")
            ], CollectionOrdering.Matching).Because("Mate's slot 3 and the unheld slot 7 are someone else's");
            await Assert.That(section.CountText).IsEqualTo("5 in 3 matches");
            await Assert.That(section.KindText).IsEqualTo("4 highlights, 1 funny");
            await Assert.That(section.HasRows).IsTrue();
            await Assert.That(section.ShowEmpty).IsFalse();
            await Assert.That(section.HasUnscanned).IsFalse();
            PlayerHighlightItem top = section.Rows[0];
            await Assert.That((top.Title, top.RoundText, top.MapText, top.KindText, top.ScoreText))
                .IsEqualTo(("slot 5 after_reconnect", "R9", "Nuke", "Highlight", "70"));
            await Assert.That(section.Rows[^1].IsFunny).IsTrue();
        }

        shell.Tab.OpenPlayer(Mate);
        await shell.Highlights.Loading;
        await Assert.That(shell.Highlights.Rows.Select(r => r.Highlight.HighlightId).Distinct()).IsEquivalentTo(["clutch"]);
    }

    [Test]
    public async Task EachDemoIsReadOnce_UntilItsScanMoves()
    {
        DemoCacheStore store = Library(3);
        using Shell shell = new(store);
        shell.Tab.OpenPlayer(Grim);
        await shell.Highlights.Loading;
        await Assert.That(shell.Facts.Reads.Count).IsEqualTo(3);

        shell.Tab.OpenPlayer(Mate);
        await shell.Highlights.Loading;
        shell.Tab.OpenPlayer(Grim);
        await shell.Highlights.Loading;
        await Assert.That(shell.Facts.Reads.Count).IsEqualTo(3).Because("every demo's list holds every player's highlights");

        DemoCacheRecord rescanned = Demo(2, "cfg2");
        rescanned.Highlights.Add(Fired(2, 100, "five_k", 12));
        store.Upsert(rescanned);
        await shell.Highlights.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(shell.Facts.Reads.ToArray()[3..]).IsEquivalentTo(["/lib/2.dem"]);
            await Assert.That(shell.Highlights.Rows[0].Highlight.HighlightId).IsEqualTo("five_k");
        }
    }

    [Test]
    public async Task UnscannedDemos_AreNoted_AndNoHighlightIsTheEmptyState()
    {
        using Shell shell = new(Library(3, i => i == 0 ? "cfg" : null));
        shell.Tab.OpenPlayer(Grim);
        await shell.Highlights.Loading;
        await Assert.That(shell.Highlights.UnscannedText).IsEqualTo("2 matches not scanned for highlights yet");
        await Assert.That(shell.Highlights.Rows.Count).IsEqualTo(2);

        using Shell none = new(Library(2, _ => null));
        none.Tab.OpenPlayer(Grim);
        await none.Highlights.Loading;
        using (Assert.Multiple())
        {
            await Assert.That(none.Highlights.IsVisible).IsTrue();
            await Assert.That(none.Highlights.ShowEmpty).IsTrue();
            await Assert.That(none.Highlights.HasRows).IsFalse();
            await Assert.That(none.Facts.Reads).IsEmpty().Because("a demo the index says holds no highlights is never opened");
        }
    }

    [Test]
    public async Task AddClips_OpensThePickerForTheSubject_AndOpenOpensTheDemo()
    {
        using Shell shell = new(Library(2));
        shell.Tab.OpenPlayer(Grim);
        await shell.Highlights.Loading;
        await Assert.That(shell.Highlights.CanAddClips).IsTrue();
        shell.Highlights.AddClipsCommand.Execute(null);
        shell.Highlights.OpenDemoCommand.Execute(shell.Highlights.Rows[0]);
        await Assert.That(shell.AddedFor).IsEquivalentTo([Grim]);
        await Assert.That(shell.Opened).IsEquivalentTo([shell.Highlights.Rows[0].Match.Row.Path]);

        using Shell noReels = new(Library(1), withPicker: false);
        await Assert.That(noReels.Highlights.CanAddClips).IsFalse();
    }

    [Test]
    public async Task ThePicker_OpensWithTheSubjectTicked()
    {
        List<DemoCacheRecord> records = [Demo(0), Demo(1)];
        AddClipsPickerViewModel picker = new(records, 2, _ => false, _ => { }, _ => { }, () => { }, players: [Id(Grim)]);

        using (Assert.Multiple())
        {
            await Assert.That(picker.PlayerFilters.Where(p => p.IsSelected).Select(p => p.SteamId64)).IsEquivalentTo([Id(Grim)]);
            await Assert.That(picker.Rows.Count).IsEqualTo(4);
            await Assert.That(picker.Rows.All(r => r.PlayerKey == Id(Grim))).IsTrue();
        }
    }
}

/// <summary>The highlights section renders the subject's rows and the Add-clips link.</summary>
[NotInParallel]
[Category("Render")]
public class PlayerOverviewHighlightsRenderTests
{
    [Test]
    public async Task TheSection_RendersItsRows_AndTheLink()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore store = new(null);
            for (int i = 0; i < 3; i++)
            {
                DemoCacheRecord record = PlayerOverviewHistoryTests.Demo(i);
                record.ConfigFingerprint = "cfg";
                record.Highlights = [new CachedHighlightEvent { RulesetId = "r", HighlightId = "ace", PlayerSlot = 2, RoundNumber = 4, Score = 60, RenderedTitle = "grim ace" }];
                store.Upsert(record);
            }

            PlayerDirectory directory = new(store, _ => true);
            using IdentityService identity = new(new SettingsService((string?)null));
            using PlayerOverviewTabViewModel vm = new(new PlayerOverviewServices(directory, identity, null,
                _ => { }, _ => { }, (_, _) => { }, _ => { }, new AnalysisFacts(store, null, new NoRoundFacts()), _ => { }));
            vm.OnActivated(null!);
            vm.OpenPlayer(PlayerOverviewHistoryTests.Grim);
            HighlightsSectionViewModel section = vm.Reveal<HighlightsSectionViewModel>();
            await section.Loading;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(section.Rows.Count).IsEqualTo(3);

            vm.ShowPage(PlayerOverviewPageKind.Achievements);

            Window window = new() { Width = 1300, Height = 1400, Content = new PlayerOverviewTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            HighlightsSectionView view = window.GetVisualDescendants().OfType<HighlightsSectionView>().Single();
            ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "HighlightList");
            await Assert.That(list.GetVisualDescendants().OfType<ListBoxItem>().Count()).IsEqualTo(3);
            await Assert.That(view.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "Add clips") && b.IsEffectivelyVisible)).IsTrue();
            window.Close();
        });
    }

    private sealed class NoRoundFacts : IRoundFacts
    {
        public int Schema => 0;

        public event Action<string>? Updated { add { } remove { } }

        public RoundFactsRows? TryGet(string demoPath) => null;

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) => null;

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];
    }
}
