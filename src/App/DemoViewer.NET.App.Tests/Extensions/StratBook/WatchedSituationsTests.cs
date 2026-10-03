#region

using System.Text.Json;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.ViewModels.Situations;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Watched Situations over a hand-built index and the real evaluator fed synthetic samples and
///     synthetic Round Facts rows: the file round trip and its refusal, the badge arithmetic on the
///     index's Indexed hook (map, match, re-index counting once, the filter's demo set re-derived per
///     evaluation), the restart path at the watermark agreeing with the hook, mark-as-seen, the tab
///     header badge moving with no restart, and re-run putting the query back on the canvas.
/// </summary>
public class WatchedSituationsTests
{
    private const string Seeded = "/d/seeded.dem";

    // Pinned before every real index stamp, so the watermark a new watch takes sits below them all.
    private const long Before = 1000;

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dv-watched-{Guid.NewGuid():N}");

    private static long LocalDay(int month, int day) => new DateTime(2026, month, day, 12, 0, 0).Ticks;

    /// <summary>Five CT tokens resolved to BombsiteA: the query "BombsiteA:5" on the CT side, T unconstrained.</summary>
    private static QueryToken[] FiveOnA() =>
        [.. Enumerable.Range(0, 5).Select(i => new QueryToken(QuerySide.Ct, i, 600 + i, -400, -416, "BombsiteA"))];

    [Test]
    public async Task AWatch_RoundTripsThroughTheFile_TokensToleranceFiltersAndWatermark()
    {
        string root = TempRoot();
        try
        {
            using Harness h = new();
            Guid opponent = Guid.NewGuid();
            SearchFilterValues filters = new()
            {
                Side = 3,
                BuyCt = BuyType.Full,
                Phase = RoundPhase.PostPlant,
                Opponent = opponent,
                Source = SearchFilterOptions.Unlabeled,
                From = new DateTime(2026, 9, 1),
                To = new DateTime(2026, 9, 20)
            };
            QueryToken[] tokens =
            [
                .. FiveOnA().Take(2),
                new QueryToken(QuerySide.T, 0, -900.5f, 200.25f, -512, "Lobby"),
                new QueryToken(QuerySide.T, 1, 10, 20, 0, null)
            ];

            WatchedSituation saved;
            using (WatchedSituationsService first = new(root, h.Index, h.Cache, now: () => 5000))
            {
                saved = first.Watch("  their A hold  ", "de_nuke", tokens, SituationTolerance.Adjacent, filters);
                first.Watch("", "de_dust2", FiveOnA(), SituationTolerance.Exact, SearchFilterValues.None);
            }

            using WatchedSituationsService second = new(root, h.Index, h.Cache, now: () => 9000);
            WatchedSituation loaded = second.Watches[0];
            JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, WatchedSituationsService.FileName)));

            using (Assert.Multiple())
            {
                await Assert.That(second.FileProblem).IsNull();
                await Assert.That(second.IsSessionOnly).IsFalse();
                await Assert.That(second.Watches.Count).IsEqualTo(2);
                await Assert.That(loaded.Id).IsEqualTo(saved.Id);
                await Assert.That(loaded.Name).IsEqualTo("their A hold").Because("the name is trimmed once, at save");
                await Assert.That(loaded.Map).IsEqualTo("de_nuke");
                await Assert.That(loaded.Tolerance).IsEqualTo(SituationTolerance.Adjacent);
                await Assert.That(loaded.CreatedTicks).IsEqualTo(5000);
                await Assert.That(loaded.WatermarkTicks).IsEqualTo(5000).Because("a new watch has seen what it matches today");
                await Assert.That(loaded.Tokens).IsEquivalentTo(tokens.Select(WatchedToken.From));
                await Assert.That(loaded.Filters).IsEqualTo(filters);
                await Assert.That(loaded.TokenFor(QuerySide.Ct)).IsEqualTo("BombsiteA:2");
                await Assert.That(loaded.TokenFor(QuerySide.T)).IsEqualTo("Lobby:1").Because("the unresolved token is kept on the canvas and left out of the query");
                await Assert.That(second.Watches[1].Name).IsEqualTo("CT BombsiteA:5 vs T any").Because("an empty name takes the query's own line");
                await Assert.That(json.RootElement.GetProperty("schemaVersion").GetInt32()).IsEqualTo(WatchedSituationsFile.CurrentSchema);
                await Assert.That(json.RootElement.GetProperty("watches")[0].GetProperty("filters").GetProperty("buyCt").GetString())
                    .IsEqualTo("Full").Because("enums are written by name, so a reordered enum cannot move a saved filter");
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(root, WatchedSituationsService.FileName)))
                    .DoesNotContain(Seeded).Because("a watch names no demo; the set is derived per evaluation");
            }

            second.Remove(saved.Id);
            using WatchedSituationsService third = new(root, h.Index, h.Cache);
            await Assert.That(third.Watches.Select(w => w.Map)).IsEquivalentTo(["de_dust2"]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task AFileThatCannotBeRead_IsRefused_AndNeverOverwritten()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, WatchedSituationsService.FileName);
            await File.WriteAllTextAsync(path, "{ not json");
            using Harness h = new();

            using WatchedSituationsService service = new(root, h.Index, h.Cache);
            service.Watch("kept for the session", "de_nuke", FiveOnA(), SituationTolerance.Exact, SearchFilterValues.None);

            using (Assert.Multiple())
            {
                await Assert.That(service.FileProblem).IsNotNull();
                await Assert.That(service.Watches.Count).IsEqualTo(1).Because("the session still works");
                await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("{ not json").Because("the user's file is not replaced by what one session holds");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task TheIndexedHook_MovesTheBadge_WithoutARestart_AndTheRestartPathAgrees()
    {
        string root = TempRoot();
        try
        {
            using Harness h = new();
            using WatchedSituationsService service = new(root, h.Index, h.Cache, now: () => Before);
            int raised = 0;
            service.Changed += () => raised++;

            // The tab header's badge, before the tab's VM exists: the module drives it from the service.
            WorkspaceTabDescriptor tab = new SituationsModule(() => throw new InvalidOperationException("never built here"), service)
                .CreateTabs(null!).Single();

            WatchedSituation watch = service.Watch("A hold", "de_nuke", FiveOnA(), SituationTolerance.Exact, SearchFilterValues.None);
            int afterWatch = raised;

            using (Assert.Multiple())
            {
                await Assert.That(h.Index.Count(service.ToQuery(watch))).IsEqualTo(1).Because("the seeded demo matches");
                await Assert.That(service.NewCount).IsEqualTo(0).Because("it was indexed before the watch was saved");
                await Assert.That(tab.Badge).IsNull();
                await Assert.That(tab.HasBadge).IsFalse();
            }

            // A matching demo indexes: one round, one demo.
            h.Evaluate("/d/b.dem", "de_nuke", 1000);
            using (Assert.Multiple())
            {
                await Assert.That(service.NewCount).IsEqualTo(1);
                await Assert.That(service.NewCountOf(watch.Id)).IsEqualTo(1);
                await Assert.That(service.NewDemoCountOf(watch.Id)).IsEqualTo(1);
                await Assert.That(service.NewHitsOf(watch.Id).Single().DemoPath).IsEqualTo("/d/b.dem");
                await Assert.That(tab.Badge).IsEqualTo("1 new");
                await Assert.That(raised).IsGreaterThan(afterWatch);
            }

            // Another map, and the right map with nobody on A: neither counts.
            h.Evaluate("/d/c.dem", "de_dust2", 1000);
            h.Evaluate("/d/d.dem", "de_nuke", null);
            await Assert.That(service.NewCount).IsEqualTo(1);

            // The same demo indexed again replaces what it contributed rather than adding to it.
            h.Evaluate("/d/b.dem", "de_nuke", 1000);
            await Assert.That(service.NewCount).IsEqualTo(1).Because("a rebuild counts once");

            // Two matching rounds in one more demo: three hits over two demos.
            h.Evaluate("/d/e.dem", "de_nuke", 1000, 3000);
            using (Assert.Multiple())
            {
                await Assert.That(service.NewCount).IsEqualTo(3);
                await Assert.That(service.NewDemoCountOf(watch.Id)).IsEqualTo(2);
                await Assert.That(tab.Badge).IsEqualTo("3 new");
            }

            // The restart path: a fresh service reads the same file and counts at the watermark through
            // the index stamps, with no Indexed event, and lands on the same number.
            using (WatchedSituationsService restarted = new(root, h.Index, h.Cache, now: () => Before))
            {
                using (Assert.Multiple())
                {
                    await Assert.That(restarted.NewCount).IsEqualTo(3);
                    await Assert.That(restarted.NewDemoCountOf(watch.Id)).IsEqualTo(2);
                }
            }

            // Mark as seen: the watermark moves past every counted stamp (the pinned clock is behind
            // them), the badge clears, and the file holds the new watermark.
            service.MarkSeen(watch.Id);
            long newest = h.Cache.TryGetIndex("/d/e.dem")!.RoundIndexComputedAtTicks;
            using (Assert.Multiple())
            {
                await Assert.That(service.NewCount).IsEqualTo(0);
                await Assert.That(tab.Badge).IsNull();
                await Assert.That(service.Watches.Single().WatermarkTicks).IsGreaterThanOrEqualTo(newest);
            }

            using (WatchedSituationsService restarted = new(root, h.Index, h.Cache, now: () => Before))
            {
                await Assert.That(restarted.NewCount).IsEqualTo(0).Because("the seen watermark survives a restart");
            }

            // A demo indexed after the mark is new again.
            await WaitPast(newest);
            h.Evaluate("/d/f.dem", "de_nuke", 1000);
            using (Assert.Multiple())
            {
                await Assert.That(service.NewCount).IsEqualTo(1);
                await Assert.That(tab.Badge).IsEqualTo("1 new");
            }

            // A drop has no Indexed event; the full pass on the index's Changed takes it back out.
            h.Cache.Remove("/d/f.dem");
            await Assert.That(service.NewCount).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task TheFiltersDemoSet_IsDerivedPerEvaluation_SoALaterDemoInRangeCounts()
    {
        using Harness h = new();
        using WatchedSituationsService service = new(null, h.Index, h.Cache, now: () => Before);
        SearchFilterValues september = new()
        {
            From = new DateTime(2026, 9, 1),
            To = new DateTime(2026, 9, 30)
        };
        WatchedSituation watch = service.Watch("", "de_nuke", FiveOnA(), SituationTolerance.Exact, september);

        // Neither demo existed when the watch was saved; only the one whose date falls in the range counts.
        h.EvaluateDated("/d/sep.dem", "de_nuke", LocalDay(9, 15), 1000);
        h.EvaluateDated("/d/jan.dem", "de_nuke", LocalDay(1, 15), 1000);

        using (Assert.Multiple())
        {
            await Assert.That(service.IsSessionOnly).IsTrue();
            await Assert.That(service.NewCountOf(watch.Id)).IsEqualTo(1);
            await Assert.That(service.NewHitsOf(watch.Id).Single().DemoPath).IsEqualTo("/d/sep.dem");
            await Assert.That(watch.Name).IsEqualTo("CT BombsiteA:5 vs T any · from 2026-09-01 · to 2026-09-30");
        }
    }

    [Test]
    public async Task TheList_ShowsEachWatchsNew_MarksSeen_AndReRunsOntoTheCanvas()
    {
        using Harness h = new();
        // The real clock here: a demo indexed before the watch is seen, one indexed after it is new.
        using WatchedSituationsService service = new(null, h.Index, h.Cache);
        using QueryCanvasViewModel canvas = new(h.Index, new QueryPlaceResolver(h.Index, h.Sources.Zones), h.Cache, _ => null,
            dispose => dispose());
        using WatchedSituationsViewModel list = new(service, canvas);
        List<SituationHit> searched = [];
        canvas.Searched += searched.AddRange;

        await Assert.That(list.CanWatch).IsFalse().Because("no map, no query");
        h.EvaluateDated("/d/old.dem", "de_nuke", LocalDay(3, 1), 1000);
        WatchedSituation watch = service.Watch("A hold", "de_nuke", FiveOnA(), SituationTolerance.AnyPlace,
            new SearchFilterValues { From = new DateTime(2020, 1, 1) });
        await WaitPast(watch.WatermarkTicks);
        h.EvaluateDated("/d/b.dem", "de_nuke", LocalDay(9, 15), 1000, 3000);

        WatchedSituationRowViewModel row = list.Rows.Single();
        using (Assert.Multiple())
        {
            await Assert.That(list.HeaderLine).IsEqualTo("Watched situations · 2 new");
            await Assert.That(row.BadgeText).IsEqualTo("2 new");
            await Assert.That(row.NewLine).IsEqualTo("2 new rounds in 1 demo");
            await Assert.That(row.MarkSeenCommand.CanExecute(null)).IsTrue();
        }

        row.RunCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(canvas.Map).IsEqualTo("de_nuke");
            await Assert.That(canvas.Document.Placed.Select(WatchedToken.From)).IsEquivalentTo(watch.Tokens);
            await Assert.That(canvas.Draft.Tolerance).IsEqualTo(SituationTolerance.AnyPlace);
            await Assert.That(canvas.Filters.Values).IsEqualTo(watch.Filters);
            await Assert.That(searched.Count).IsEqualTo(3)
                .Because("the re-run searches every demo in the date range, the seen one included; the seeded demo's date is before it");
            await Assert.That(list.CanWatch).IsTrue().Because("the re-run query is on the canvas");
        }

        list.Rows.Single().MarkSeenCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(list.NewCount).IsEqualTo(0);
            await Assert.That(list.HeaderLine).IsEqualTo("Watched situations");
            await Assert.That(list.Rows.Single().NewLine).IsEqualTo("nothing new");
        }

        list.Rows.Single().RemoveCommand.Execute(null);
        await Assert.That(list.HasRows).IsFalse();
    }

    private static async Task WaitPast(long stamp)
    {
        while (DateTime.UtcNow.Ticks <= stamp)
        {
            await Task.Delay(5);
        }
    }

    /// <summary>
    ///     An index with one seeded matching demo, merged incrementally through the real evaluator as
    ///     each test indexes more: the path a demo takes on desktop, synthetic samples and rows in.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private PositionSample[] _samples = [];

        public Harness()
        {
            Cache = new DemoCacheStore(null);
            Sidecars = new RoundIndexStore(null, Cache);
            Sources = new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn);
            Evaluator = new RoundIndexEvaluator(Cache, Sidecars, Sources, () => true, walk: _ => _samples);
            Index = new SituationIndex(Cache, Sidecars, Sources, evaluator: Evaluator);

            RoundIndexDocument seeded = Document("de_nuke", Sources.FingerprintFor("de_nuke"),
                (1, 1000, 1200, [new RoundIndexRun(0, 1, "BombsiteA:5", "Ramp:5")]));
            Indexed(Cache, Sidecars, Seeded, seeded, computedAt: 100);
            Index.Load();
        }

        public DemoCacheStore Cache { get; }
        public RoundIndexStore Sidecars { get; }
        public RoundIndexPlaceSources Sources { get; }
        public RoundIndexEvaluator Evaluator { get; }
        public SituationIndex Index { get; }

        public void Dispose()
        {
            Index.Dispose();
            Sidecars.Dispose();
        }

        /// <summary>Indexes a demo with a round per freeze end; null puts every CT in Outside instead of on A.</summary>
        public void Evaluate(string path, string map, params int[]? freezeEnds) => EvaluateDated(path, map, 20, freezeEnds);

        /// <summary>The same, with the cache row's modified time, which the date filter reads.</summary>
        public void EvaluateDated(string path, string map, long modifiedTicks, params int[]? freezeEnds)
        {
            int[] rounds = freezeEnds ?? [1000];
            string ctPlace = freezeEnds is null ? "Outside" : "BombsiteA";
            RoundFacts[] facts = [.. rounds.Select((freezeEnd, i) => Round(i + 1, freezeEnd, freezeEnd + 200))];
            Cache.Upsert(ParsedRecord(path, map, facts: Facts(facts), modifiedTicks: modifiedTicks));

            // The walk is the one seam the evaluator takes; it reads what this call set up.
            _samples = [.. rounds.SelectMany(r => Everyone(r, ctPlace, "Ramp").Concat(Everyone(r + 64, ctPlace, "Ramp")))];
            Evaluator.Evaluate(path, Demo(lastTick: 5000, map: map));
        }
    }
}
