#region

using System.Text.Json;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Free Labels From Round Facts (tag-store.md §3.3, §9 step 6) on synthetic rows: the facts namespace
///     rewritten wholesale from the round holding <c>fromTick</c> with the human labels untouched byte for
///     byte, a start in no round kept and marked stale, the refresh reaching a sidecar on disk and an open
///     session alike without an undo entry, and a fresh instance carrying its facts from the moment it is
///     made. Synthetic rows keep this suite fast and isolated; the real-demo variant is in
///     <see cref="TagFactsRealDemoTests" />.
/// </summary>
[NotInParallel]
public class TagFactsRefresherTests
{
    private const string DemoPath = "/d/match.dem";

    private static readonly DateTime Refreshed = new(2026, 9, 24, 9, 30, 0, DateTimeKind.Utc);

    private static readonly List<CachedRound> _rounds =
    [
        new() { Number = 1, StartTickFrameClock = 1_000 },
        new() { Number = 2, StartTickFrameClock = 10_000 }
    ];

    // Round 1: CT pistol win on elimination, no plant, one T death at 1 500. Round 2: T full buy against a
    // CT eco, a B plant at 12 000, the bomb explodes.
    private static RoundFactsRows Rows(int roundTwoCtScore = 1)
    {
        RoundFacts one = RoundIndexTestData.Round(1, 1_000, 8_000, kills: [new KillStep { Tick = 1_500, VictimSide = 2, CtAlive = 5, TAlive = 4 }]);
        one.MatchRoundNumber = 1;
        one.Half = RoundHalf.First;
        one.Ct.BuyType = BuyType.Pistol;
        one.T.BuyType = BuyType.Pistol;

        RoundFacts two = RoundIndexTestData.Round(2, 10_000, 18_000);
        two.MatchRoundNumber = 2;
        two.Half = RoundHalf.First;
        two.WinnerSide = 2;
        two.EndReason = RoundEndReason.TargetBombed;
        two.PlantTick = 12_000;
        two.PlantSite = BombSite.B;
        two.RoundTimeSeconds = 88;
        two.Ct.BuyType = BuyType.Eco;
        two.Ct.ScoreBefore = roundTwoCtScore;
        two.T.BuyType = BuyType.Full;
        return RoundIndexTestData.Facts(one, two);
    }

    private static string Value(TagInstance instance, string group) =>
        instance.Facts.Single(f => string.Equals(f.Group, group, StringComparison.Ordinal)).Value;

    private static string LabelsJson(TagInstance instance) =>
        JsonSerializer.Serialize(instance.Labels, TagJsonContext.Default.Options);

    [Test]
    public async Task Refresh_WritesTheRoundsFacts_UnderPlainGroupNames_AtTheInstancesStart()
    {
        TagInstance execute = Instance("B execute", 11_000, 13_000, ("outcome", "won"));
        TagDocument document = Document(Sha, execute);

        TagFactsRefresher.Refresh(document, Rows(), DemoCacheRecord.RoundFactsSchema, Refreshed);

        using (Assert.Multiple())
        {
            await Assert.That(execute.Round).IsEqualTo(2);
            await Assert.That(Value(execute, "round")).IsEqualTo("2");
            await Assert.That(Value(execute, "matchRound")).IsEqualTo("2");
            await Assert.That(Value(execute, "half")).IsEqualTo("first");
            await Assert.That(Value(execute, "buy.ct")).IsEqualTo("eco");
            await Assert.That(Value(execute, "buy.t")).IsEqualTo("full");
            await Assert.That(Value(execute, "score.ct")).IsEqualTo("1");
            await Assert.That(Value(execute, "score.t")).IsEqualTo("0");
            await Assert.That(Value(execute, "winner")).IsEqualTo("T");
            await Assert.That(Value(execute, "endReason")).IsEqualTo(nameof(RoundEndReason.TargetBombed));
            await Assert.That(Value(execute, "plantSite")).IsEqualTo("b");
            await Assert.That(Value(execute, "plantTick")).IsEqualTo("12000");
            await Assert.That(Value(execute, "roundTime")).IsEqualTo("88");
            await Assert.That(Value(execute, "phase")).IsEqualTo(
                RoundFactsValues.LowerCamel(RoundPhases.At(Rows().Rounds[1], 11_000)))
                .Because("the tick-anchored facts are read at fromTick");
            await Assert.That(Value(execute, "manCount.ct")).IsEqualTo("5");
            await Assert.That(Value(execute, "manCount.t")).IsEqualTo("5");
            await Assert.That(execute.Facts.Any(f => f.Group is "side" or "buy.us" or "buy.them")).IsFalse()
                .Because("relative facts are Team Identity's to derive at query time (overview correction 10)");
            await Assert.That(execute.Facts.Any(f => f.Group.StartsWith("parser.", StringComparison.Ordinal))).IsFalse();
            await Assert.That(execute.FactsStamp!.Schema).IsEqualTo(DemoCacheRecord.RoundFactsSchema);
            await Assert.That(execute.FactsStamp.ComputedUtc).IsEqualTo(Refreshed);
            await Assert.That(execute.FactsStamp.Stale).IsFalse();
        }
    }

    [Test]
    public async Task Refresh_ReplacesTheFactsWholesale_AndLeavesTheLabelsByteForByte()
    {
        TagInstance execute = Instance("B execute", 11_000, 13_000, ("outcome", "won"), ("", "sloppy"), ("site", "B"));
        execute.Labels[0].Extra = new Dictionary<string, JsonElement>
        {
            ["future"] = JsonDocument.Parse("""{ "kept": 1 }""").RootElement.Clone()
        };
        execute.Facts = [new TagLabel("buy.ct", "full"), new TagLabel("renamedByAnOlderSchema", "x")];
        execute.Note = "late smokes";
        string before = LabelsJson(execute);
        TagDocument document = Document(Sha, execute);

        TagFactsRefresher.Refresh(document, Rows(), DemoCacheRecord.RoundFactsSchema, Refreshed);

        using (Assert.Multiple())
        {
            await Assert.That(LabelsJson(execute)).IsEqualTo(before);
            await Assert.That(execute.Note).IsEqualTo("late smokes");
            await Assert.That(execute.Facts.Select(f => f.Group)).DoesNotContain("renamedByAnOlderSchema")
                .Because("the whole array is rewritten, not merged");
            await Assert.That(Value(execute, "buy.ct")).IsEqualTo("eco");
            await Assert.That(execute.Facts.Count(f => f.Group == "buy.ct")).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AStartInNoRound_KeepsItsOldFacts_MarkedStale()
    {
        TagInstance warmup = Instance("warmup", 200, 400);
        warmup.Round = 7;
        warmup.Facts = [new TagLabel("buy.ct", "full")];
        warmup.FactsStamp = new TagFactsStamp { Schema = 1, ComputedUtc = Created };

        TagFactsRefresher.Refresh(Document(Sha, warmup), Rows(), DemoCacheRecord.RoundFactsSchema, Refreshed);

        using (Assert.Multiple())
        {
            await Assert.That(warmup.FactsStamp!.Stale).IsTrue();
            await Assert.That(warmup.FactsStamp.ComputedUtc).IsEqualTo(Created)
                .Because("the stamp still says when the kept facts were computed");
            await Assert.That(warmup.Facts.Single().Value).IsEqualTo("full");
            await Assert.That(warmup.Round).IsEqualTo(7);
        }
    }

    [Test]
    public async Task RefreshDemo_RewritesTheSidecar_WhenNoSessionHoldsIt()
    {
        string root = TempRoot();
        try
        {
            TagStore store = new(root);
            TagInstance execute = Instance("B execute", 11_000, 13_000, ("outcome", "won"));
            store.Save(Document(Sha, execute));
            FakeFacts facts = new(Rows());
            using TagFactsRefresher refresher = new(store, facts, _ => Sha, action => action(), () => Refreshed);

            string labelsBefore = LabelsJson(store.TryLoad(Sha)!.Instances.Single());
            facts.Rows = Rows(roundTwoCtScore: 4);
            facts.Raise(DemoPath);

            TagInstance stored = new TagStore(root).TryLoad(Sha)!.Instances.Single();
            using (Assert.Multiple())
            {
                await Assert.That(Value(stored, "score.ct")).IsEqualTo("4").Because("Updated drove a refresh from the new rows");
                await Assert.That(stored.FactsStamp!.ComputedUtc).IsEqualTo(Refreshed);
                await Assert.That(LabelsJson(stored)).IsEqualTo(labelsBefore);
            }
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [Test]
    public async Task RefreshDemo_WithoutADocument_WritesNothing()
    {
        TagStore store = new(null);
        FakeFacts facts = new(Rows());
        using TagFactsRefresher refresher = new(store, facts, _ => Sha, action => action(), () => Refreshed);

        using (Assert.Multiple())
        {
            await Assert.That(refresher.RefreshDemo(DemoPath)).IsTrue().Because("rows and a hash: the store decides");
            await Assert.That(store.Contains(Sha)).IsFalse().Because("opening a demo must not leave a tag file behind");
        }
    }

    [Test]
    public async Task RefreshDemo_WithoutRows_OrWithoutAHash_DoesNothing()
    {
        TagStore store = new(null);
        TagInstance execute = Instance("B execute", 11_000, 13_000);
        store.Save(Document(Sha, execute));
        FakeFacts facts = new(null);
        using TagFactsRefresher noRows = new(store, facts, _ => Sha, action => action(), () => Refreshed);
        await Assert.That(noRows.RefreshDemo(DemoPath)).IsFalse();

        facts.Rows = Rows();
        using TagFactsRefresher noHash = new(store, facts, _ => null, action => action(), () => Refreshed);
        await Assert.That(noHash.RefreshDemo(DemoPath)).IsFalse();

        // The rows' own hash is the fallback join when the cache index has none.
        facts.Rows!.DemoSha256 = Sha;
        await Assert.That(noHash.RefreshDemo(DemoPath)).IsTrue();
        await Assert.That(store.TryLoad(Sha)!.Instances.Single().Facts).IsNotEmpty();
    }

    [Test]
    public async Task ARefreshOfAnOpenDemo_GoesThroughTheSession_OutsideItsUndoHistory()
    {
        TagStore store = new(null);
        FakeFacts facts = new(Rows());
        using TagSession session = new(store, _ => _rounds, () => false, () => Created, facts)
        {
            AutoSaveDelay = TimeSpan.FromHours(1)
        };
        await session.AttachAsync(Demo, Clock, DemoPath);
        TagInstance execute = Instance("B execute", 11_000, 13_000, ("outcome", "won"));
        session.Apply(new TagDelta.Add(execute));
        int depth = session.UndoDepth;
        int version = session.Version;

        using TagFactsRefresher refresher = new(store, facts, _ => Sha, action => action(), () => Refreshed);
        facts.Rows = Rows(roundTwoCtScore: 9);
        facts.Raise(DemoPath);

        TagInstance live = session.Document!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(Value(live, "score.ct")).IsEqualTo("9");
            await Assert.That(live.FactsStamp!.ComputedUtc).IsEqualTo(Refreshed);
            await Assert.That(session.UndoDepth).IsEqualTo(depth).Because("a refresh is not the user's edit");
            await Assert.That(session.Version).IsGreaterThan(version);
            await Assert.That(live.Labels.Single().Value).IsEqualTo("won");
        }

        // Undoing the add still removes it; redo brings back what the person made, facts included.
        await Assert.That(session.Undo()).IsTrue();
        await Assert.That(session.Document!.Instances).IsEmpty();
    }

    [Test]
    public async Task AFreshInstance_AlreadyCarriesItsRoundsFacts()
    {
        FakeFacts facts = new(Rows());
        using TagSession session = new(new TagStore(null), _ => _rounds, () => false, () => Created, facts)
        {
            AutoSaveDelay = TimeSpan.FromHours(1)
        };
        await session.AttachAsync(Demo, Clock, DemoPath);

        session.Apply(new TagDelta.Add(Instance("B execute", 11_000, 13_000)));

        TagInstance made = session.Document!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(made.Round).IsEqualTo(2);
            await Assert.That(Value(made, "buy.ct")).IsEqualTo("eco");
            await Assert.That(Value(made, "buy.t")).IsEqualTo("full");
            await Assert.That(Value(made, "score.ct")).IsEqualTo("1");
            await Assert.That(Value(made, "endReason")).IsEqualTo(nameof(RoundEndReason.TargetBombed));
            await Assert.That(Value(made, "plantSite")).IsEqualTo("b");
            await Assert.That(made.FactsStamp!.ComputedUtc).IsEqualTo(Created);
            await Assert.That(made.Labels).IsEmpty().Because("facts never land in the human namespace");
        }

        // Redo re-adds what was made, facts and all, without re-reading anything.
        session.Undo();
        session.Redo();
        await Assert.That(Value(session.Document!.Instances.Single(), "buy.t")).IsEqualTo("full");
    }

    [Test]
    public async Task MovingAStart_IntoAnotherRound_RederivesItsFacts_AndUndoMovesThemBack()
    {
        FakeFacts facts = new(Rows());
        using TagSession session = new(new TagStore(null), _ => _rounds, () => false, () => Created, facts)
        {
            AutoSaveDelay = TimeSpan.FromHours(1)
        };
        await session.AttachAsync(Demo, Clock, DemoPath);
        TagInstance execute = Instance("B execute", 11_000, 13_000);
        session.Apply(new TagDelta.Add(execute));

        TagInstance moved = session.Document!.Instances.Single().Clone();
        moved.FromTick = 1_200;
        moved.ToTick = 2_000;
        session.Apply(new TagDelta.Replace(execute.Id, moved));

        TagInstance live = session.Document.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(live.Round).IsEqualTo(1);
            await Assert.That(Value(live, "buy.ct")).IsEqualTo("pistol");
            await Assert.That(Value(live, "round")).IsEqualTo("1");
        }

        session.Undo();
        await Assert.That(Value(session.Document.Instances.Single(), "buy.ct")).IsEqualTo("eco");
    }

    [Test]
    public async Task Attaching_FillsFacts_OnInstancesMadeBeforeTheDemoHadRows()
    {
        TagStore store = new(null);
        TagInstance early = Instance("B execute", 11_000, 13_000, ("outcome", "won"));
        store.Save(Document(Sha, early));
        FakeFacts facts = new(Rows());

        using TagSession session = new(store, _ => _rounds, () => false, () => Created, facts)
        {
            AutoSaveDelay = TimeSpan.FromHours(1)
        };
        await session.AttachAsync(Demo, Clock, DemoPath);

        TagInstance loaded = session.Document!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(Value(loaded, "buy.t")).IsEqualTo("full");
            await Assert.That(session.UndoDepth).IsEqualTo(0);
            await Assert.That(loaded.Labels.Single().Value).IsEqualTo("won");
        }
    }

    [Test]
    public async Task ASessionWithoutRows_LeavesFactsToTheRefresher()
    {
        FakeFacts facts = new(null);
        using TagSession session = new(new TagStore(null), _ => _rounds, () => false, () => Created, facts)
        {
            AutoSaveDelay = TimeSpan.FromHours(1)
        };
        await session.AttachAsync(Demo, Clock, DemoPath);
        session.Apply(new TagDelta.Add(Instance("B execute", 11_000, 13_000)));

        TagInstance made = session.Document!.Instances.Single();
        await Assert.That(made.Facts).IsEmpty();
        await Assert.That(made.FactsStamp).IsNull();

        // The rows arrive; the session drops what it read, so the next tag gets facts too.
        facts.Rows = Rows();
        facts.Raise(DemoPath);
        session.Apply(new TagDelta.Add(Instance("A split", 11_500, 12_500)));
        await Assert.That(Value(session.Document.Instances[1], "buy.t")).IsEqualTo("full");
    }

    [Test]
    public async Task Disposing_Unsubscribes()
    {
        TagStore store = new(null);
        store.Save(Document(Sha, Instance("B execute", 11_000, 13_000)));
        FakeFacts facts = new(Rows());
        TagFactsRefresher refresher = new(store, facts, _ => Sha, action => action(), () => Refreshed);
        refresher.Dispose();

        facts.Raise(DemoPath);

        await Assert.That(store.TryLoad(Sha)!.Instances.Single().Facts).IsEmpty();
        await Assert.That(facts.Subscribers).IsEqualTo(0);
    }

    private sealed class FakeFacts(RoundFactsRows? rows) : IRoundFactsSource
    {
        private Action<string>? _updated;

        public RoundFactsRows? Rows { get; set; } = rows;

        public int Subscribers => _updated?.GetInvocationList().Length ?? 0;

        public int Schema => DemoCacheRecord.RoundFactsSchema;

        public event Action<string>? Updated
        {
            add => _updated += value;
            remove => _updated -= value;
        }

        public RoundFactsRows? TryGet(string demoPath) => Rows;

        public RoundFacts? RoundAt(string demoPath, int frameClockTick) =>
            Rows is null ? null : RoundFactsSource.FindRound(Rows.Rounds, frameClockTick);

        public IReadOnlyList<(DemoCacheIndexEntry Demo, RoundFacts Round)> Query(RoundFactsFilter filter) => [];

        public IReadOnlyList<FactLabel> FactsFor(string demoPath, int round, int? atTick = null) =>
            Rows?.Rounds.FirstOrDefault(r => r.Number == round) is { } facts ? RoundFactsSource.Labels(facts, atTick) : [];

        public void Raise(string demoPath) => _updated?.Invoke(demoPath);
    }
}
