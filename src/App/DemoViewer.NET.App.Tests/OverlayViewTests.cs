#region

using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.ViewModels.Situations;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Overlay all N over synthetic hits and positions files: every sampled step from a hit's first
///     matched tick to its last, alive tuples only, the side from the round's CT slots, into the
///     canvas's document; the line that says what was stacked and how many rounds had nothing to give;
///     a set replaced mid-build posting nothing; and the tab wiring that puts the results' overlay on
///     the canvas and clears it with the map.
/// </summary>
public class OverlayViewTests
{
    private const string DemoA = "/d/a.dem";
    private const string DemoB = "/d/b.dem";

    private const string WaitingOnEngine =
        "waiting on CS2DemoKit #54: a real demo's hits need Round Facts rows for the index that yields them, and the engine row source writes none yet";

    private static SituationHit Hit(string path, int round, int freezeEnd, int first, int last) =>
        new(path, DemoCacheStore.StableKey(path), null, "de_nuke", round, freezeEnd, first, last, (last - first) / 64 + 1);

    /// <summary>Round 3 at 64-tick cadence: steps 2 to 4 sampled with two CT and one T, step 5 empty, step 6 with one CT.</summary>
    private static RoundPositionsDocument Positions(string fingerprint) => new()
    {
        Fingerprint = fingerprint,
        Demo = new RoundPositionsDemo
        {
            StableKey = DemoCacheStore.StableKey(DemoA)
        },
        CadenceTicks = 64,
        Places = ["BombsiteA", "Ramp"],
        Rounds =
        [
            new RoundPositionsRound
            {
                Number = 3,
                FreezeEndTick = 1000,
                Ct = [0, 2],
                Pos =
                [
                    [], [],
                    [new RoundPosition(0, 620, -420, -416, 0), new RoundPosition(2, 960, -640, -416, 0), new RoundPosition(1, 1420, -760, -700, 1)],
                    [new RoundPosition(0, 630, -410, -416, 0), new RoundPosition(2, 950, -650, -416, 0), new RoundPosition(1, 1400, -740, -700, 1)],
                    [new RoundPosition(0, 640, -400, -416, 0), new RoundPosition(2, 940, -660, -416, 0), new RoundPosition(1, 1380, -720, -700, 1)],
                    [],
                    [new RoundPosition(0, 650, -390, -416, 0)]
                ]
            }
        ]
    };

    [Test]
    public async Task OverlayAll_StacksEveryStepBetweenTheFirstAndLastMatch_WithSidesFromTheCtSlots()
    {
        using Harness h = new();
        h.Cache.Upsert(ParsedRecord(DemoA));
        h.Sidecars.WritePositions(DemoA, Positions(h.Sources.FingerprintFor("de_nuke")));

        // Matched from step 2 (tick 1128) to step 4 (tick 1256): three sampled states, nine tuples.
        h.Vm.Load([Hit(DemoA, 3, 1000, 1128, 1256)]);
        await h.Vm.BatchTask;
        await Assert.That(h.Vm.OverlayLabel).IsEqualTo("Overlay all 1");
        await Assert.That(h.Vm.CanOverlay).IsTrue();

        h.Vm.OverlayAllCommand.Execute(null);
        await h.Vm.OverlayTask;

        OverlayDocument overlay = h.Vm.Overlay;
        using (Assert.Multiple())
        {
            await Assert.That(overlay.MapName).IsEqualTo("de_nuke");
            await Assert.That(overlay.StateCount).IsEqualTo(3);
            await Assert.That(overlay.Count).IsEqualTo(9);
            await Assert.That(overlay.Points.Count(p => p.Side == QuerySide.Ct)).IsEqualTo(6);
            await Assert.That(overlay.Points.Count(p => p.Side == QuerySide.T)).IsEqualTo(3);
            await Assert.That(overlay.Points[0]).IsEqualTo(new OverlayPoint(620, -420, -416, QuerySide.Ct));
            await Assert.That(overlay.Points[2]).IsEqualTo(new OverlayPoint(1420, -760, -700, QuerySide.T));
            await Assert.That(h.Vm.IsOverlayShown).IsTrue();
            await Assert.That(h.Vm.IsOverlayBuilding).IsFalse();
            await Assert.That(h.Vm.OverlayLine).IsEqualTo("1 round · 3 states · 9 positions");
            await Assert.That(h.Vm.Overlay).IsSameReferenceAs(h.Canvas).Because("the results fill the canvas's own document");
        }

        // A hit whose window spans an unsampled step skips it, and the last step is inclusive.
        h.Vm.Load([Hit(DemoA, 3, 1000, 1256, 1384)]);
        await Assert.That(h.Vm.IsOverlayShown).IsFalse().Because("a new set drops the old overlay");
        h.Vm.OverlayAllCommand.Execute(null);
        await h.Vm.OverlayTask;
        await Assert.That(h.Vm.Overlay.StateCount).IsEqualTo(2);
        await Assert.That(h.Vm.Overlay.Count).IsEqualTo(4);
    }

    [Test]
    public async Task ARoundWithNoPositions_ContributesNothing_AndTheLineSaysHowMany()
    {
        using Harness h = new();
        h.Cache.Upsert(ParsedRecord(DemoA));
        h.Cache.Upsert(ParsedRecord(DemoB));
        h.Sidecars.WritePositions(DemoA, Positions(h.Sources.FingerprintFor("de_nuke")));
        h.Sidecars.WritePositions(DemoB, Positions("ri1;stale"));

        h.Vm.Load(
        [
            Hit(DemoA, 3, 1000, 1128, 1256),
            Hit(DemoA, 4, 9000, 9128, 9256), // no such round in the file
            Hit(DemoA, 3, 1000, 1320, 1320), // step 5 was not sampled
            Hit(DemoB, 3, 1000, 1128, 1256) // a stale fingerprint is no file at all
        ]);
        h.Vm.OverlayAllCommand.Execute(null);
        await h.Vm.OverlayTask;

        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.Overlay.Count).IsEqualTo(9);
            await Assert.That(h.Vm.OverlayLine).IsEqualTo("4 rounds · 3 states · 9 positions · 3 without positions");
            await Assert.That(ResultCardsViewModel.OverlayLineFor(40, 0, 312, 2104)).IsEqualTo("40 rounds · 312 states · 2,104 positions");
        }

        // Nothing at all to stack: the note, and no overlay on the canvas.
        h.Vm.Load([Hit(DemoB, 3, 1000, 1128, 1256)]);
        h.Vm.OverlayAllCommand.Execute(null);
        await h.Vm.OverlayTask;
        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.IsOverlayShown).IsFalse();
            await Assert.That(h.Vm.OverlayLine).IsEqualTo(ResultCardsViewModel.NoOverlayNote);
        }
    }

    [Test]
    public async Task ClearingTheOverlay_KeepsTheCards_AndASetReplacedMidBuild_PostsNothing()
    {
        using Harness h = new();
        h.Cache.Upsert(ParsedRecord(DemoA));
        h.Sidecars.WritePositions(DemoA, Positions(h.Sources.FingerprintFor("de_nuke")));

        h.Vm.Load([Hit(DemoA, 3, 1000, 1128, 1256)]);
        h.Vm.OverlayAllCommand.Execute(null);
        await h.Vm.OverlayTask;
        await Assert.That(h.Vm.IsOverlayShown).IsTrue();

        h.Vm.ClearOverlayCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.IsOverlayShown).IsFalse();
            await Assert.That(h.Vm.OverlayLine).IsEqualTo("");
            await Assert.That(h.Vm.Count).IsEqualTo(1).Because("the cards stay");
            await Assert.That(h.Vm.CanOverlay).IsTrue();
        }

        // The worker's answer lands after the set it described was replaced: it is discarded, and the
        // new set's overlay stays empty until asked for.
        h.Post.Hold = true;
        h.Vm.OverlayAllCommand.Execute(null);
        await h.Vm.OverlayTask;
        await Assert.That(h.Vm.IsOverlayBuilding).IsTrue();
        h.Vm.Load([Hit(DemoA, 3, 1000, 1192, 1192)]);
        h.Post.Release();
        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.IsOverlayShown).IsFalse();
            await Assert.That(h.Vm.IsOverlayBuilding).IsFalse();
            await Assert.That(h.Vm.OverlayLine).IsEqualTo("");
        }

        h.Vm.Clear();
        await Assert.That(h.Vm.CanOverlay).IsFalse().Because("no result set to stack");
    }

    [Test]
    public async Task TheTab_PutsTheResultsOverlayOnTheCanvas_AndTheMapClearsIt()
    {
        DemoCacheStore cache = new(null);
        using RoundIndexStore sidecars = new(null, cache);
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
        using SituationIndex index = new(cache, sidecars, sources);
        index.Load();
        using QueryCanvasViewModel canvas = new(index, new QueryPlaceResolver(index, sources.Zones), cache,
            _ => null, dispose => dispose(), post: action => action(), countDelay: TimeSpan.Zero);
        using SituationsTabViewModel tab = new(index, null, cache, sources, () => RoundIndexTokenSource.Pawn, false,
            canvas, sidecars: sidecars);

        await Assert.That(tab.Results.Overlay).IsSameReferenceAs(canvas.Overlay);

        canvas.Overlay.Replace("de_nuke", [new OverlayPoint(1, 2, 3, QuerySide.Ct)], 1);
        await Assert.That(tab.Results.IsOverlayShown).IsTrue();

        // A map change empties the document directly, whether or not a result set is under it: a
        // position on one map means nothing on another.
        canvas.Map = "de_mirage";
        await Assert.That(canvas.Overlay.IsEmpty).IsTrue();
        await Assert.That(tab.Results.IsOverlayShown).IsFalse();
    }

    /// <summary>Forty synthetic hits over one file: the stack is milliseconds, and no demo is opened.</summary>
    [Test]
    [Category("Budget")]
    public async Task FortyRounds_StackInWellUnderASecond()
    {
        using Harness h = new();
        h.Cache.Upsert(ParsedRecord(DemoA));
        h.Sidecars.WritePositions(DemoA, Positions(h.Sources.FingerprintFor("de_nuke")));
        h.Vm.Load([.. Enumerable.Range(0, 40).Select(_ => Hit(DemoA, 3, 1000, 1128, 1384))]);

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        h.Vm.OverlayAllCommand.Execute(null);
        await h.Vm.OverlayTask;
        watch.Stop();

        Console.WriteLine($"forty rounds stacked: {watch.ElapsedMilliseconds} ms, {h.Vm.Overlay.Count} points");
        await Assert.That(h.Vm.Overlay.Count).IsEqualTo(40 * 10);
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }

    [Test]
    [Category("RealDemo")]
    [Skip(WaitingOnEngine)]
    public Task OverlayAll_OverTheRealReplays_StacksEveryHitsSteps() => Task.CompletedTask;

    /// <summary>A UI-thread marshal a test can hold back, so a worker's answer can land after the set moved on.</summary>
    private sealed class HeldPost
    {
        private readonly List<Action> _held = [];

        public bool Hold { get; set; }

        public void Run(Action action)
        {
            if (Hold)
            {
                _held.Add(action);
                return;
            }

            action();
        }

        public void Release()
        {
            Hold = false;
            foreach (Action action in _held)
            {
                action();
            }

            _held.Clear();
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Cache = new DemoCacheStore(null);
            Sidecars = new RoundIndexStore(null, Cache);
            Sources = new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn);
            Vm = new ResultCardsViewModel(Cache, Sidecars, Sources, () => null, new SituationThumbnailCache(),
                () => new SituationThumbnailRenderer(_ => null), Post.Run, _ => null, Canvas);
        }

        public DemoCacheStore Cache { get; }
        public RoundIndexStore Sidecars { get; }
        public RoundIndexPlaceSources Sources { get; }
        public ResultCardsViewModel Vm { get; }
        public OverlayDocument Canvas { get; } = new();
        public HeldPost Post { get; } = new();

        public void Dispose() => Sidecars.Dispose();
    }
}
