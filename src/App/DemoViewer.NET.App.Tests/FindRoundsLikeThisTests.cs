#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.ViewModels.Situations;
using DemoViewer.NET.Views.Playback2D;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Find Rounds Like This: the key (plan D7), the snapshot's round-trip with the index builder (one
///     function applied twice, in both token modes), the canvas load, the 2D tab's funnel onto the seam
///     and the shipped seam's tab switch. The real-demo half is <c>FindRoundsLikeThisRealDemoTests</c>.
/// </summary>
public class FindRoundsLikeThisTests
{
    private const string Demo = "/d/a.dem";

    // The QueryCanvasTests situation: two on A and three outside, three in lobby and two on ramp, with
    // every player at a distinct spot on one Z band.
    private static readonly (int Slot, int Team, float X, float Y, string Place)[] _scene =
    [
        (1, 3, 600, -400, "BombsiteA"),
        (2, 3, 640, -420, "BombsiteA"),
        (3, 3, -1300, -900, "Outside"),
        (4, 3, -1340, -880, "Outside"),
        (5, 3, -1280, -940, "Outside"),
        (6, 2, -900, 200, "Lobby"),
        (7, 2, -940, 220, "Lobby"),
        (8, 2, -880, 260, "Lobby"),
        (9, 2, 1300, -1000, "Ramp"),
        (10, 2, 1340, -1020, "Ramp")
    ];

    [Test]
    public async Task CtrlF_IsFindRoundsLikeThis_AndBareFStaysFollow()
    {
        using (Assert.Multiple())
        {
            await Assert.That(Playback2DKeymap.TryResolve(Key.F, KeyModifiers.Control, false, out Playback2DAction find))
                .IsTrue();
            await Assert.That(find).IsEqualTo(Playback2DAction.FindRoundsLikeThis);
            await Assert.That(Playback2DKeymap.TryResolve(Key.F, KeyModifiers.None, false, out Playback2DAction follow))
                .IsTrue();
            await Assert.That(follow).IsEqualTo(Playback2DAction.CycleFollowNext);

            // A drawing tool does not take the chord away: the row is Always-scoped and nothing
            // tool-scoped claims it.
            await Assert.That(Playback2DKeymap.TryResolve(Key.F, KeyModifiers.Control, true, out Playback2DAction tool))
                .IsTrue();
            await Assert.That(tool).IsEqualTo(Playback2DAction.FindRoundsLikeThis);

            // Not a shell accelerator and not one the browser eats, so a user may keep it on either head.
            await Assert.That(Playback2DKeymap.ReservedGestures(true)).DoesNotContain((Key.F, KeyModifiers.Control));
            await Assert.That(Playback2DKeymap.GestureText(Playback2DAction.FindRoundsLikeThis)).IsEqualTo("Ctrl+F");
        }
    }

    [Test]
    public async Task TheSnapshot_MintsTheTokenTheBuilderMints_ForTheSamePositions()
    {
        SceneTime time = new(1000, 10, 15.6, 0.015625, false);
        SituationSnapshot snapshot = SituationSnapshot.Capture("de_nuke", time, Markers(), PawnPlaceSource.Instance);

        // The builder over the same ten positions at the same tick, seen through PositionSampler's eyes.
        RoundIndexDocument document = RoundIndexBuilder.Build(Demo(), Facts(Round(1, 1000, 1200)),
            RoundIndexOptions.Default, PawnPlaceSource.Instance, Samples(1000));
        RoundIndexRun stored = document.Rounds[0].Runs[0];

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.Ct.Count).IsEqualTo(5);
            await Assert.That(snapshot.T.Count).IsEqualTo(5);
            await Assert.That(snapshot.TokenFor(QuerySide.Ct)).IsEqualTo(stored.Ct);
            await Assert.That(snapshot.TokenFor(QuerySide.T)).IsEqualTo(stored.T);
            await Assert.That(stored.Ct).IsEqualTo("BombsiteA:2|Outside:3");
            await Assert.That(stored.T).IsEqualTo("Lobby:3|Ramp:2");
        }

        // Then through the canvas: the loaded rail encodes to the same token, and the index counts it.
        using Harness h = new();
        Indexed(h.Cache, h.Sidecars, Demo, document);
        h.Index.Load();

        h.Vm.LoadSnapshot(snapshot);

        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.Map).IsEqualTo("de_nuke");
            await Assert.That(h.Vm.Document.PlacedCount).IsEqualTo(10);
            await Assert.That(h.Vm.CtToken).IsEqualTo(stored.Ct);
            await Assert.That(h.Vm.TToken).IsEqualTo(stored.T);
            await Assert.That(h.Index.Count(h.Vm.Draft.ToQuery())).IsEqualTo(1);
            await Assert.That(h.Vm.HintLine).Contains("tick 1000");
            await Assert.That(h.Vm.Maps).Contains("de_nuke");
        }

        // The token sits where the player stood, so the map shows the situation the key captured.
        QueryToken first = h.Vm.Document.Get(QuerySide.Ct, 0)!.Value;
        await Assert.That((first.WorldX, first.WorldY, first.Place)).IsEqualTo((600f, -400f, "BombsiteA"));
    }

    [Test]
    public async Task TheZonesMode_ResolvesTheMarkerPositions_AsTheBuilderDoes()
    {
        // The zone set names by X alone; the pawn field says something else, and must lose.
        RoundIndexBuilderTests.FakeZoneResolver zones = new("zv-3", v => v.X < 0 ? "West" : "East");
        RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Zones,
            new RoundIndexEvaluatorTests.MapZones(("de_nuke", zones)));
        IPlaceSource source = sources.SourceFor("de_nuke");

        SceneTime time = new(1000, 10, 15.6, 0.015625, false);
        SituationSnapshot snapshot = SituationSnapshot.Capture("de_nuke", time, Markers(), source);
        RoundIndexDocument document = RoundIndexBuilder.Build(Demo(), Facts(Round(1, 1000, 1200)),
            RoundIndexOptions.Default, source, Samples(1000));
        RoundIndexRun stored = document.Rounds[0].Runs[0];

        using (Assert.Multiple())
        {
            await Assert.That(source).IsTypeOf<ZonePlaceSource>();
            await Assert.That(snapshot.TokenFor(QuerySide.Ct)).IsEqualTo(stored.Ct);
            await Assert.That(snapshot.TokenFor(QuerySide.T)).IsEqualTo(stored.T);
            await Assert.That(stored.Ct).IsEqualTo("East:2|West:3");
            await Assert.That(stored.T).IsEqualTo("East:2|West:3");
        }
    }

    [Test]
    public async Task DeadAndSpectatingMarkers_AreLeftOut_AndAnUnknownPlace_IsOnTheMapButOutOfTheQuery()
    {
        SceneTime time = new(1000, 10, 15.6, 0.015625, false);
        PlayerMarker[] markers =
        [
            Marker(1, 3, 600, -400, "BombsiteA"),
            Marker(2, 3, 640, -420, "BombsiteA", alive: false),
            Marker(3, 1, 0, 0, "BombsiteA"),
            Marker(6, 2, -900, 200, null),
            Marker(7, 2, -940, 220, "Lobby")
        ];

        SituationSnapshot snapshot = SituationSnapshot.Capture("de_nuke", time, markers, PawnPlaceSource.Instance);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.Ct.Count).IsEqualTo(1).Because("the dead CT and the spectator are not alive players");
            await Assert.That(snapshot.T.Count).IsEqualTo(2);
            await Assert.That(snapshot.TokenFor(QuerySide.Ct)).IsEqualTo("BombsiteA:1");
            await Assert.That(snapshot.TokenFor(QuerySide.T)).IsEqualTo(PlaceCountToken.EncodePlaces([null, "Lobby"]))
                .Because("the snapshot token is the builder's, unknown place included");
        }

        using Harness h = new();
        h.Vm.LoadSnapshot(snapshot);

        QueryToken unknown = h.Vm.Document.Get(QuerySide.T, 0)!.Value;
        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.Document.PlacedCount).IsEqualTo(3);
            await Assert.That(unknown.IsResolved).IsFalse();
            await Assert.That(h.Vm.CtToken).IsEqualTo("BombsiteA:1");
            await Assert.That(h.Vm.TToken).IsEqualTo("Lobby:1").Because("a query never targets ?");
            await Assert.That(h.Vm.Draft.ToQuery().T).IsEquivalentTo([new PlaceQuery("Lobby", 1)]);
        }
    }

    [Test]
    public async Task ASideBeyondTheRail_KeepsItsFirstFive_AndTheHintSaysSo()
    {
        SceneTime time = new(2000, 20, 31.25, 0.015625, false);
        List<PlayerMarker> markers = [];
        for (int slot = 0; slot < 7; slot++)
        {
            markers.Add(Marker(slot, 2, slot * 10, 0, "Lobby"));
        }

        SituationSnapshot snapshot = SituationSnapshot.Capture("de_nuke", time, markers, PawnPlaceSource.Instance);
        using Harness h = new();
        h.Vm.LoadSnapshot(snapshot);

        using (Assert.Multiple())
        {
            await Assert.That(snapshot.TokenFor(QuerySide.T)).IsEqualTo("Lobby:7");
            await Assert.That(h.Vm.Document.PlacedCount).IsEqualTo(5);
            await Assert.That(h.Vm.TToken).IsEqualTo("Lobby:5");
            await Assert.That(h.Vm.HintLine).Contains("2 more than the rail holds");
        }
    }

    [Test]
    public async Task ASecondSnapshot_ReplacesTheRail_OnTheSameMap()
    {
        using Harness h = new();
        SceneTime time = new(1000, 10, 15.6, 0.015625, false);
        h.Vm.LoadSnapshot(SituationSnapshot.Capture("de_nuke", time, Markers(), PawnPlaceSource.Instance));
        await Assert.That(h.Vm.Document.PlacedCount).IsEqualTo(10);

        h.Vm.LoadSnapshot(SituationSnapshot.Capture("de_nuke", time,
            [Marker(1, 3, 600, -400, "BombsiteA")], PawnPlaceSource.Instance));

        using (Assert.Multiple())
        {
            await Assert.That(h.Vm.Document.PlacedCount).IsEqualTo(1);
            await Assert.That(h.Vm.CtToken).IsEqualTo("BombsiteA:1");
            await Assert.That(h.Vm.TToken).IsEqualTo("");
            await Assert.That(h.Vm.ResultCount).IsNull();
        }
    }

    [Test]
    public async Task ThePlaybackTab_HandsTheCurrentFrameToTheSeam_AndRefusesWithoutOne()
    {
        // A bare context with no roster: the activation resync then builds an empty scene, which is
        // the state before the first push.
        Playback2DTabViewModel vm = new();
        Playback2DFakeContext ctx = new()
        {
            MapName = "de_nuke"
        };
        vm.OnActivated(ctx);
        RecordingSeam seam = new();
        vm.FindRounds = seam;

        // Nothing pushed yet: the scene is empty, so the key stays unhandled without asking the seam.
        await Assert.That(vm.ExecuteAction(Playback2DAction.FindRoundsLikeThis)).IsFalse();
        await Assert.That(seam.Calls.Count).IsEqualTo(0);

        ctx.PushPlacedMarkers((1, 3, 600, -400, 64, "BombsiteA"), (6, 2, -900, 200, 64, "Lobby"));

        await Assert.That(vm.ExecuteAction(Playback2DAction.FindRoundsLikeThis)).IsTrue();
        (string map, SceneTime time, IReadOnlyList<PlayerMarker> markers) = seam.Calls.Single();
        using (Assert.Multiple())
        {
            await Assert.That(map).IsEqualTo("de_nuke");
            await Assert.That(time.Tick).IsEqualTo(ctx.CurrentTick).Because("the frame the markers belong to");
            await Assert.That(markers.Select(m => m.Place ?? "?")).IsEquivalentTo(["BombsiteA", "Lobby"]);
        }

        // The command the menu entry and the toolbar button run is the same funnel.
        vm.FindRoundsLikeThisCommand.Execute(null);
        await Assert.That(seam.Calls.Count).IsEqualTo(2);

        // No seam (a host without the Situations module) and no map both leave the key unhandled
        // without asking anything.
        vm.FindRounds = null;
        await Assert.That(vm.ExecuteAction(Playback2DAction.FindRoundsLikeThis)).IsFalse();
        vm.FindRounds = seam;
        ctx.MapName = null;
        await Assert.That(vm.ExecuteAction(Playback2DAction.FindRoundsLikeThis)).IsFalse();
        await Assert.That(seam.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task TheLabelAndTheToolTip_ReadTheResolvedProfile()
    {
        (Playback2DTabViewModel vm, _) = Playback2DTimelineHarness.Tab();
        await Assert.That(vm.FindRoundsLikeThisLabel).IsEqualTo("Find rounds like this (Ctrl+F)");
        await Assert.That(vm.FindRoundsLikeThisToolTip).StartsWith("Find rounds like this (Ctrl+F): ");

        vm.ApplyKeymapOverrides(["FindRoundsLikeThis=Ctrl+Shift+S"]);

        using (Assert.Multiple())
        {
            await Assert.That(vm.KeymapRejections).IsEmpty();
            await Assert.That(vm.FindRoundsLikeThisLabel).IsEqualTo("Find rounds like this (Ctrl+Shift+S)");
            await Assert.That(vm.Keymap.TryResolve(Key.S, KeyModifiers.Control | KeyModifiers.Shift, false,
                out Playback2DAction action)).IsTrue();
            await Assert.That(action).IsEqualTo(Playback2DAction.FindRoundsLikeThis);
            await Assert.That(vm.Keymap.TryResolve(Key.F, KeyModifiers.Control, false, out _)).IsFalse()
                .Because("the shipped chord was vacated by the override");
        }
    }

    [Test]
    public async Task TheShippedSeam_LoadsTheCanvas_AndShowsTheTab_OrRefusesWhenTheTabIsGone()
    {
        using Harness h = new();
        using SituationsTabViewModel tab = new(h.Index, null, h.Cache, h.Sources,
            () => RoundIndexTokenSource.Pawn, false, h.Vm);
        List<string> shown = [];
        bool tabOnStrip = true;
        FindRoundsLikeThis seam = new(() => tab, h.Sources, id =>
        {
            shown.Add(id);
            return tabOnStrip;
        });
        SceneTime time = new(1000, 10, 15.6, 0.015625, false);

        await Assert.That(seam.Show("de_nuke", time, Markers())).IsTrue();
        using (Assert.Multiple())
        {
            // The id the seam asks for is the one SituationsModule contributes.
            await Assert.That(shown).IsEquivalentTo(["situations.search"]);
            await Assert.That(h.Vm.CtToken).IsEqualTo("BombsiteA:2|Outside:3");
            await Assert.That(h.Vm.TToken).IsEqualTo("Lobby:3|Ramp:2");
        }

        // Nobody alive: nothing to search for, and the tab is not even asked for.
        await Assert.That(seam.Show("de_nuke", time, [Marker(1, 3, 0, 0, "BombsiteA", alive: false)])).IsFalse();
        await Assert.That(shown.Count).IsEqualTo(1);

        // The tab is gated off: the key stays unhandled and the canvas keeps what it had.
        tabOnStrip = false;
        await Assert.That(seam.Show("de_nuke", time, [Marker(1, 3, 0, 0, "Ramp")])).IsFalse();
        await Assert.That(h.Vm.CtToken).IsEqualTo("BombsiteA:2|Outside:3");
    }

    /// <summary>
    ///     The whole route, under a real headless key event: the view's tunnelling handler resolves the
    ///     chord through the VM's profile and lands on the seam.
    /// </summary>
    [Test]
    [NotInParallel]
    [Category("Render")]
    public async Task CtrlF_RoutesThroughTheView_ToTheSeam()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.MapName = "de_nuke";
            RecordingSeam seam = new();
            vm.FindRounds = seam;
            ctx.PushPlacedMarkers((1, 3, 600, -400, 64, "BombsiteA"));

            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
            Playback2DTimelineHarness.Pump();

            await Assert.That(seam.Calls.Count).IsEqualTo(1);
            await Assert.That(seam.Calls[0].Markers.Single().Place).IsEqualTo("BombsiteA");

            // Bare F is still follow cycling, not a snapshot.
            window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(seam.Calls.Count).IsEqualTo(1);
        });
    }

    private static PlayerMarker Marker(int slot, int team, float x, float y, string? place, bool alive = true) =>
        new(slot, team, x, y, 64f, 0, alive ? RingState.Team : RingState.Dead, 1.0, "P", alive, 0, 0, 0, place);

    private static PlayerMarker[] Markers() =>
        [.. _scene.Select(p => Marker(p.Slot, p.Team, p.X, p.Y, p.Place))];

    private static IEnumerable<PositionSample> Samples(int tick) =>
        _scene.Select(p => Sample(tick, p.Slot, p.Place, p.X, p.Y, 64f, frame: 10));

    /// <summary>Records every hand-over and answers the shipped seam's way: nobody alive is nothing to show.</summary>
    private sealed class RecordingSeam : IFindRoundsLikeThis
    {
        public List<(string Map, SceneTime Time, IReadOnlyList<PlayerMarker> Markers)> Calls { get; } = [];

        public bool Show(string map, SceneTime time, IReadOnlyList<PlayerMarker> markers)
        {
            Calls.Add((map, time, [.. markers]));
            return markers.Any(m => m.IsAlive);
        }
    }

    /// <summary>A canvas over an empty index on a bundle-less host, the QueryCanvasTests shape without the panes.</summary>
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Cache = new DemoCacheStore(null);
            Sidecars = new RoundIndexStore(null, Cache);
            Sources = new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn);
            Index = new SituationIndex(Cache, Sidecars, Sources);
            Index.Load();
            Vm = new QueryCanvasViewModel(Index, new QueryPlaceResolver(Index, Sources.Zones), Cache, _ => null,
                dispose => dispose());
        }

        public DemoCacheStore Cache { get; }
        public RoundIndexStore Sidecars { get; }
        public RoundIndexPlaceSources Sources { get; }
        public SituationIndex Index { get; }
        public QueryCanvasViewModel Vm { get; }

        public void Dispose()
        {
            Vm.Dispose();
            Index.Dispose();
            Sidecars.Dispose();
        }
    }
}
