#region

using DemoViewer.NET.Modules.Review;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.RoundTagger;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Matrix (plan §3, tag-store.md §3.7): the dense table over a pivot, the round text, and the tab
///     over a tagged fixture corpus that reproduces the design's screen mock ("Matrix: Falcons, last 6
///     demos, de_nuke, T side") exactly, with every kind of noise the scope must drop. Round Facts rows
///     are synthetic: the engine row source is a stub until CS2DemoKit #54, so the side join reads rows
///     built here.
/// </summary>
public class TagMatrixTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    // The mock, cell for cell: code, then the five routes in the mock's order.
    private static readonly string[] MockRoutes = ["ramp", "lower", "outside", "yard", "hut"];

    private static readonly (string Code, int[] Counts)[] Mock =
    [
        ("execute", [7, 11, 4, 2, 1]),
        ("fake → rotate", [3, 1, 6, 0, 0]),
        ("default", [12, 5, 9, 8, 3]),
        ("fast / rush", [1, 4, 0, 0, 2])
    ];

    private static readonly string[] OrderedRoutes = ["hut", "lower", "outside", "ramp", "yard"];
    private static readonly string[] OrderedCodes = ["default", "execute", "fake → rotate", "fast / rush"];
    private static readonly int[] OneToTwelve = [.. Enumerable.Range(1, 12)];

    // ── The table ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheTable_FillsAbsentCellsWithZero_AndTotalsCountAMultiValuedInstanceOnce()
    {
        TagInstance both = Instance("execute", 100, 200, ("route", "ramp"), ("route", "hut"));
        both.Round = 1;
        TagInstance one = Instance("default", 300, 400, ("route", "ramp"));
        one.Round = 2;
        TagPivot pivot = TagQuery.Pivot([Document(ShaOf(1), both, one)], TagSlice.Everything, new PivotAxis.Code(),
            new PivotAxis.Label(LabelNamespace.Human, "route"));

        TagMatrixTable table = new(pivot);

        using (Assert.Multiple())
        {
            await Assert.That(table.Count("default", "hut")).IsEqualTo(0).Because("the sparse pivot has no such cell");
            await Assert.That(table.Refs("default", "hut")).IsEmpty();
            await Assert.That(table.Count("execute", "hut")).IsEqualTo(1);
            await Assert.That(table.Count("execute", "ramp")).IsEqualTo(1);
            await Assert.That(table.RowTotals[table.RowKeys.ToList().IndexOf("execute")]).IsEqualTo(1)
                .Because("one instance with two routes is one instance in its row");
            await Assert.That(table.ColumnTotals[table.ColumnKeys.ToList().IndexOf("ramp")]).IsEqualTo(2);
            await Assert.That(table.Total).IsEqualTo(2);
        }
    }

    [Test]
    public async Task TheAxes_AreCodeRoundDemo_ThenHumanGroups_ThenFactGroups()
    {
        TagInstance instance = Instance("execute", 100, 200, ("route", "ramp"), ("outcome", "won"));
        instance.Facts = [new TagLabel("buy.t", "full")];

        IReadOnlyList<TagMatrixAxis> axes = TagMatrix.AxesOf([Document(ShaOf(1), instance)]);

        await Assert.That(axes.Select(a => a.Display).SequenceEqual(["Code", "Round", "Demo", "Label · outcome", "Label · route", "Fact · buy.t"]))
            .IsTrue();
    }

    [Test]
    [Arguments("", true, 0)]
    [Arguments("1-12", true, 12)]
    [Arguments("1\u201312", true, 12)]
    [Arguments(" 1, 5, 7-9 ", true, 5)]
    [Arguments("13-", false, 0)]
    [Arguments("0-3", false, 0)]
    [Arguments("9-3", false, 0)]
    [Arguments("1-5000", false, 0)]
    [Arguments("a", false, 0)]
    public async Task TheRoundText_ParsesRangesAndLists_AndRefusesNonsense(string text, bool ok, int count)
    {
        bool parsed = TagMatrix.TryParseRounds(text, out IReadOnlySet<int>? rounds);

        using (Assert.Multiple())
        {
            await Assert.That(parsed).IsEqualTo(ok);
            await Assert.That(rounds?.Count ?? 0).IsEqualTo(count);
        }
    }

    // ── The tab over the mock corpus ───────────────────────────────────────────────────────────────

    [Test]
    public async Task TheMockScreen_IsReproduced_FalconsLastSixNukeDemos_TSide()
    {
        using Corpus corpus = await Corpus.Create();
        TagMatrixTabViewModel matrix = corpus.Matrix;

        await corpus.Pick(() =>
        {
            matrix.Team.Selected = matrix.Team.Options.Single(o => o.Display == "Falcons");
            matrix.Last.Selected = matrix.Last.Options.Single(o => o.Value == 6);
        });
        await corpus.Pick(() =>
        {
            matrix.Map.Selected = matrix.Map.Options.Single(o => o.Value == "de_nuke");
            matrix.Side.Selected = matrix.Side.Options.Single(o => o.Value == 2);
        });

        using (Assert.Multiple())
        {
            await Assert.That(matrix.Title).IsEqualTo("Falcons, last 6 demos, de_nuke, T side");
            await Assert.That(matrix.RowAxis).IsEqualTo(TagMatrixAxis.Code);
            await Assert.That(matrix.ColumnAxis).IsEqualTo(TagMatrixAxis.Label(LabelNamespace.Human, "route"))
                .Because("the first build puts the human label group across the top, the mock's shape");
            await Assert.That(matrix.Table!.RowKeys.SequenceEqual(OrderedCodes)).IsTrue();
            await Assert.That(matrix.Table.ColumnKeys.SequenceEqual(OrderedRoutes)).IsTrue();
            foreach ((string code, int[] counts) in Mock)
            {
                for (int c = 0; c < MockRoutes.Length; c++)
                {
                    await Assert.That(matrix.Table.Count(code, MockRoutes[c])).IsEqualTo(counts[c])
                        .Because($"{code} × {MockRoutes[c]}");
                }
            }

            await Assert.That(matrix.Table.Total).IsEqualTo(Mock.Sum(m => m.Counts.Sum()));
            await Assert.That(matrix.SummaryLine).IsEqualTo("79 instances in 6 demos");
            await Assert.That(matrix.Rows.Count).IsEqualTo(4);
            await Assert.That(matrix.Rows.Single(r => r.Display == "fake → rotate").Cells.Count(c => c.IsEmpty)).IsEqualTo(2)
                .Because("the mock's zeros are drawn as zeros, not left out");
        }
    }

    [Test]
    public async Task ACount_SendsExactlyItsClips_ToTheReviewQueue_UnderOneTitleCard_AndShowsReview()
    {
        using Corpus corpus = await Corpus.Create();
        await corpus.PickMockScope();

        TagMatrixCellViewModel cell = corpus.Matrix.Rows.Single(r => r.Display == "execute")
            .Cells.Single(c => c.ColumnDisplay == "ramp");
        cell.OpenCommand.Execute(null);

        IReadOnlyList<ReviewEntry> entries = corpus.Queue.Entries;
        using (Assert.Multiple())
        {
            await Assert.That(cell.Count).IsEqualTo(7);
            await Assert.That(entries.Count).IsEqualTo(8).Because("one title card, then the seven clips");
            await Assert.That(entries[0].Kind).IsEqualTo(ReviewEntryKind.Section);
            await Assert.That(entries[0].Title).IsEqualTo("Matrix · execute × ramp");
            await Assert.That(entries[0].Note).IsEqualTo("Falcons, last 6 demos, de_nuke, T side");
            await Assert.That(entries.Skip(1).All(e => e.Source == ReviewSources.Tag)).IsTrue();
            await Assert.That(entries.Skip(1).Select(e => (e.DemoPath, e.FromTick, e.ToTick)))
                .IsEquivalentTo(cell.Refs.Select(r => (corpus.PathOf(r.Sha256), r.FromTick, r.ToTick)));
            await Assert.That(entries.Skip(1).All(e => e.TickRate == 64)).IsTrue().Because("the document's clock names the rate");
            await Assert.That(corpus.SelectedTabs).IsEquivalentTo([ReviewQueueModule.TabId]);
            await Assert.That(corpus.Matrix.StatusLine).IsEqualTo("7 clips sent to Review");
        }

        cell.OpenCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(corpus.Queue.Entries.Count).IsEqualTo(8).Because("a second click adds nothing");
            await Assert.That(corpus.Matrix.StatusLine).IsEqualTo("already in Review");
        }
    }

    [Test]
    public async Task SwappingTheAxes_Transposes_TheSameCounts()
    {
        using Corpus corpus = await Corpus.Create();
        await corpus.PickMockScope();

        corpus.Matrix.SwapAxesCommand.Execute(null);
        await corpus.Matrix.Pending;

        TagMatrixTable table = corpus.Matrix.Table!;
        using (Assert.Multiple())
        {
            await Assert.That(corpus.Matrix.RowAxis).IsEqualTo(TagMatrixAxis.Label(LabelNamespace.Human, "route"));
            await Assert.That(corpus.Matrix.ColumnAxis).IsEqualTo(TagMatrixAxis.Code);
            await Assert.That(table.RowKeys.SequenceEqual(OrderedRoutes)).IsTrue();
            foreach ((string code, int[] counts) in Mock)
            {
                for (int c = 0; c < MockRoutes.Length; c++)
                {
                    await Assert.That(table.Count(MockRoutes[c], code)).IsEqualTo(counts[c]);
                }
            }
        }
    }

    [Test]
    public async Task TheDynamicMode_RestrictsToASlice_RoundsAndAFactHeldToOneValue()
    {
        using Corpus corpus = await Corpus.Create();
        await corpus.PickMockScope();
        TagMatrixTabViewModel matrix = corpus.Matrix;

        await corpus.Pick(() => matrix.RoundsText = "1-6");
        int firstHalf = corpus.Mocked.Count(m => m.Round <= 6);
        int executeRampFirstHalf = corpus.Mocked.Count(m => m is { Round: <= 6, Code: "execute", Route: "ramp" });
        using (Assert.Multiple())
        {
            await Assert.That(matrix.Table!.Total).IsEqualTo(firstHalf);
            await Assert.That(matrix.Table.Count("execute", "ramp")).IsEqualTo(executeRampFirstHalf);
            await Assert.That(firstHalf).IsGreaterThan(0).And.IsLessThan(79).Because("the fixture must make the slice bite");
        }

        await corpus.Pick(() => matrix.RoundsText = "1-");
        await Assert.That(matrix.RoundsProblem).IsNotEmpty().Because("a half-typed range waits and says why");
        await Assert.That(matrix.Table!.Total).IsEqualTo(firstHalf).Because("the table is left as it was");

        await corpus.Pick(() =>
        {
            matrix.RoundsText = "";
            matrix.Filter.Selected = matrix.Filter.Options.Single(o => o.Display == "Fact · buy.t");
        });
        await corpus.Pick(() => matrix.FilterValue.Selected = matrix.FilterValue.Options.Single(o => o.Value == "eco"));
        int eco = corpus.Mocked.Count(m => m.Buy == "eco");
        using (Assert.Multiple())
        {
            await Assert.That(matrix.FilterValue.Options.Select(o => o.Value)).IsEquivalentTo([null, "eco", "full"]);
            await Assert.That(matrix.Table!.Total).IsEqualTo(eco);
            await Assert.That(eco).IsGreaterThan(0).And.IsLessThan(79);
        }

        matrix.ClearSliceCommand.Execute(null);
        await matrix.Pending;
        using (Assert.Multiple())
        {
            await Assert.That(matrix.Title).IsEqualTo("Falcons, 8 demos").Because("clear keeps the team and drops map, last and side");
            await Assert.That(matrix.Filter.IsAny).IsTrue();
        }
    }

    [Test]
    public async Task WithNoTeam_TheScopeIsEveryTaggedDemo_AndTheSideFieldIsIgnored()
    {
        using Corpus corpus = await Corpus.Create();
        TagMatrixTabViewModel matrix = corpus.Matrix;
        await corpus.Pick(() => matrix.Side.Selected = matrix.Side.Options.Single(o => o.Value == 2));

        using (Assert.Multiple())
        {
            await Assert.That(matrix.Title).IsEqualTo("All tagged demos");
            await Assert.That(matrix.SummaryLine).IsEqualTo($"{corpus.AllInstances} instances in 10 demos");
            await Assert.That(matrix.Map.Options.Select(o => o.Value)).IsEquivalentTo([null, "de_mirage", "de_nuke"]);
            await Assert.That(matrix.HasTeam).IsFalse();
        }
    }

    [Test]
    public async Task AnInstanceWhoseDemoIsNotInTheLibrary_IsNotQueued_AndTheLineSaysSo()
    {
        using Corpus corpus = await Corpus.Create();
        TagMatrixTabViewModel matrix = corpus.Matrix;
        await corpus.Pick(() => matrix.RowAxis = TagMatrixAxis.Demo);

        TagMatrixRowViewModel orphan = matrix.Rows.Single(r => r.Display == Corpus.OrphanFileName);
        orphan.Cells.First(c => !c.IsEmpty).OpenCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(corpus.Queue.Entries).IsEmpty();
            await Assert.That(matrix.StatusLine).IsEqualTo("nothing queued: the demo is not in the library");
            await Assert.That(corpus.SelectedTabs).IsEmpty();
        }
    }

    [Test]
    public async Task AStoreChange_WhileTheTabIsHidden_WaitsForTheNextActivation()
    {
        using Corpus corpus = await Corpus.Create();
        await corpus.PickMockScope();
        int before = corpus.Matrix.Table!.Total;

        corpus.Matrix.OnDeactivated();
        TagDocument doc = corpus.Store.TryLoad(ShaOf(7))!;
        TagInstance added = Instance("execute", 99_000, 99_500, ("route", "ramp"));
        added.Round = 1;
        doc.Instances.Add(added);
        corpus.Store.Save(doc);
        await corpus.Matrix.Pending;
        await Assert.That(corpus.Matrix.Table!.Total).IsEqualTo(before).Because("a hidden tab does not rebuild");

        corpus.Matrix.OnActivated(null!);
        await corpus.Matrix.Pending;
        await Assert.That(corpus.Matrix.Table!.Total).IsEqualTo(before + 1);
    }

    private static string ShaOf(int n) => n.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    ///     Nine tagged demos. f1 to f7 are Falcons on de_nuke, days 1 to 7; f8 is Falcons on de_mirage, day 8,
    ///     the newest, so a map filter must come before the last-N cut; o9 is two teams that are not Falcons;
    ///     x10 is tagged but not in the library. In each Falcons demo the five are T in rounds 1 to 12 and
    ///     CT in 13 to 24. The mock's 79 instances sit in the T rounds of f2 to f7; every other instance is
    ///     noise the scope must drop: CT rounds, the oldest demo, the mirage demo, the other teams.
    /// </summary>
    private sealed class Corpus : IDisposable
    {
        public const string OrphanFileName = "x10.dem";

        private static readonly string[] Falcons = Ids(11, 12, 13, 14, 15);

        private readonly FakeFacts _facts = new();

        private Corpus()
        {
            Cache = new DemoCacheStore(null);
            Teams = new TeamIdentityService(null, Cache, _facts, run: _inline);
            Store = new TagStore(null);
            Queue = new ReviewQueue(null);
            Matrix = new TagMatrixTabViewModel(Store, Queue, Cache.TryGetIndexBySha256, Teams,
                tab =>
                {
                    SelectedTabs.Add(tab);
                    return true;
                },
                debounce: TimeSpan.Zero, isBrowser: false);
        }

        public DemoCacheStore Cache { get; }
        public TeamIdentityService Teams { get; }
        public TagStore Store { get; }
        public ReviewQueue Queue { get; }
        public TagMatrixTabViewModel Matrix { get; }
        public List<string> SelectedTabs { get; } = [];
        public int AllInstances { get; private set; }

        /// <summary>The mock's instances as placed: the expected values for the slice tests.</summary>
        public List<(string Code, string Route, int Round, string Buy)> Mocked { get; } = [];

        public static async Task<Corpus> Create()
        {
            Corpus c = new();
            await c.Teams.StartAsync();
            using (c.Cache.BeginBatch())
            {
                for (int n = 1; n <= 8; n++)
                {
                    c.Cache.Upsert(Record(n, n == 8 ? "de_mirage" : "de_nuke", Falcons, Ids(100 + n * 5, 101 + n * 5, 102 + n * 5, 103 + n * 5, 104 + n * 5)));
                }

                c.Cache.Upsert(Record(9, "de_nuke", Ids(60, 61, 62, 63, 64), Ids(70, 71, 72, 73, 74)));
            }

            await c.Teams.Idle;
            c.Teams.Rename(c.Teams.TeamOnSide(PathOf(2), 2)!.Id, "Falcons");

            RoundFactsRows rows = RoundIndexTestData.Facts(
            [
                .. Enumerable.Range(1, 24).Select(r => RoundIndexTestData.Round(r, r * 1000, r * 1000 + 900,
                    ctSlots: r <= 12 ? [5, 6, 7, 8, 9] : [0, 1, 2, 3, 4],
                    tSlots: r <= 12 ? [0, 1, 2, 3, 4] : [5, 6, 7, 8, 9]))
            ]);
            for (int n = 1; n <= 9; n++)
            {
                c._facts.Rows[PathOf(n)] = rows;
            }

            c.Seed();
            c.Matrix.Refresh();
            await c.Matrix.Pending;
            return c;
        }

        public static string PathOf(int n) => $"/d/f{n}.dem";

        public string PathOf(string sha) => Cache.TryGetIndexBySha256(sha)!.Path;

        public async Task Pick(Action pick)
        {
            pick();
            await Matrix.Pending;
        }

        public async Task PickMockScope()
        {
            await Pick(() =>
            {
                Matrix.Team.Selected = Matrix.Team.Options.Single(o => o.Display == "Falcons");
                Matrix.Last.Selected = Matrix.Last.Options.Single(o => o.Value == 6);
                Matrix.Map.Selected = Matrix.Map.Options.Single(o => o.Value == "de_nuke");
                Matrix.Side.Selected = Matrix.Side.Options.Single(o => o.Value == 2);
            });
        }

        public void Dispose()
        {
            Matrix.Dispose();
            Teams.Dispose();
        }

        private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

        // The five in slots 0 to 4 at the end of the demo, T; the other five CT.
        private static DemoCacheRecord Record(int n, string map, string[] t, string[] ct)
        {
            DemoCacheRecord record = new()
            {
                Path = PathOf(n),
                Size = 1000,
                ModifiedTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(n).Ticks,
                Sha256 = ShaOf(n),
                Map = map
            };
            int slot = 0;
            foreach (string id in t)
            {
                record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 2 });
            }

            foreach (string id in ct)
            {
                record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 3 });
            }

            DemoCacheStore.StampParse(record);
            return record;
        }

        private void Seed()
        {
            Dictionary<int, List<TagInstance>> byDemo = [];
            int placed = 0;

            void Add(int demo, string code, string route, int round, string buy)
            {
                TagInstance instance = Instance(code, round * 1000 + 100 + placed, round * 1000 + 400 + placed, ("route", route));
                instance.Round = round;
                instance.Facts = [new TagLabel("buy.t", buy)];
                (byDemo.TryGetValue(demo, out List<TagInstance>? list) ? list : byDemo[demo] = []).Add(instance);
                placed++;
            }

            // The mock, dealt round-robin over f2 to f7 and the T rounds, a quarter of them on an eco.
            int deal = 0;
            foreach ((string code, int[] counts) in Mock)
            {
                for (int c = 0; c < MockRoutes.Length; c++)
                {
                    for (int k = 0; k < counts[c]; k++, deal++)
                    {
                        int round = OneToTwelve[deal % 12];
                        string buy = deal % 4 == 0 ? "eco" : "full";
                        Add(2 + deal % 6, code, MockRoutes[c], round, buy);
                        Mocked.Add((code, MockRoutes[c], round, buy));
                    }
                }
            }

            // The noise: the same codes and routes where the scope must not look.
            foreach (int demo in new[] { 2, 5, 7 })
            {
                Add(demo, "execute", "ramp", 15, "full"); // CT rounds
                Add(demo, "default", "hut", 20, "full");
            }

            Add(1, "execute", "ramp", 3, "full"); // the seventh-newest nuke demo
            Add(8, "execute", "ramp", 3, "full"); // mirage
            Add(9, "execute", "ramp", 3, "full"); // not Falcons

            foreach ((int demo, List<TagInstance> instances) in byDemo)
            {
                Store.Save(Document(ShaOf(demo), [.. instances]));
            }

            TagInstance orphan = Instance("execute", 5100, 5400, ("route", "yard"));
            orphan.Round = 5;
            TagDocument unknown = Document(ShaOf(10), orphan);
            unknown.Demo.FileName = OrphanFileName;
            Store.Save(unknown);

            AllInstances = placed + 1;
        }
    }

    private sealed class FakeFacts : IRoundFactsSource
    {
        public Dictionary<string, RoundFactsRows> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Schema => DemoCacheRecord.RoundFactsSchema;

        public event Action<string>? Updated
        {
            add { }
            remove { }
        }

        public RoundFactsRows? TryGet(string demoPath) => Rows.GetValueOrDefault(demoPath);

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) =>
            TryGet(demoPath) is { } rows ? RoundFactsSource.FindRound(rows.Rounds, frameClockTick) : null;

        public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter) => [];

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) => [];
    }
}
