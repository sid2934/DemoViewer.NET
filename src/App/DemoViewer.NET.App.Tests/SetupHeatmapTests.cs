#region

using DemoViewer.NET.Modules.Dossier;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Dossier;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Setup Heatmaps By Buy over one synthetic demo: only the rounds Team Identity puts the team on CT,
///     grouped by map and CT buy; only CT tuples inside the setup window, which first contact cuts
///     short; fixed against rotating places by share of sampled rounds; a round with no window still
///     belonging to its heatmap; a stale positions file counted rather than read; the Dossier drawing
///     each heatmap through the Overlay View's document and layer; and every heatmap opening its rounds
///     in the Review Queue.
/// </summary>
[NotInParallel]
public class SetupHeatmapTests
{
    private const string Demo = "/d/setup.dem";
    private const int Rate = 64;

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly int[] TeamASlots = [0, 1, 2, 3, 4];
    private static readonly int[] TeamBSlots = [5, 6, 7, 8, 9];

    private static string Id(int n) => $"7656{n:D4}";

    private static RoundFacts Round(int number, int freezeEnd, BuyType ctBuy, int[] ctSlots, int[] tSlots,
        int? contactAfterSeconds = null) => new()
    {
        Number = number,
        IsLive = true,
        FreezeEndTick = freezeEnd,
        FirstContactTick = contactAfterSeconds is { } s ? freezeEnd + s * Rate : null,
        EndTick = freezeEnd + 90 * Rate,
        Ct = new SideFacts { Side = 3, Slots = ctSlots, BuyType = ctBuy },
        T = new SideFacts { Side = 2, Slots = tSlots, BuyType = BuyType.Full }
    };

    // Team B (slots 5..9) holds CT for rounds 1, 2, 3 and 5; the halves swap at round 4. Round 2's
    // first contact at 20 s cuts its window to 15..19 s; round 3's at 5 s leaves it no window at all.
    private static DemoCacheRecord Record() => Record(Demo, "de_nuke", 0, new RoundFactsRows
    {
        Schema = DemoCacheRecord.RoundFactsSchema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds =
        [
            Round(1, 1000, BuyType.Pistol, TeamBSlots, TeamASlots),
            Round(2, 10000, BuyType.Full, TeamBSlots, TeamASlots, 20),
            Round(3, 20000, BuyType.Full, TeamBSlots, TeamASlots, 5),
            Round(4, 30000, BuyType.Full, TeamASlots, TeamBSlots),
            Round(5, 40000, BuyType.Full, TeamBSlots, TeamASlots)
        ]
    });

    // A second meeting of the same two teams, so Team Identity clusters each five into a team; it has
    // no Round Facts rows, so the heatmaps skip it.
    private static DemoCacheRecord Record(string path, string map, int day, RoundFactsRows? rows)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map,
            Sha256 = rows is null ? null : "abc123",
            RoundFacts = rows
        };
        for (int slot = 0; slot < 10; slot++)
        {
            record.Players.Add(new CachedPlayerInfo
            {
                Slot = slot,
                Name = $"p{slot}",
                SteamId64 = Id(slot < 5 ? slot + 1 : slot + 6),
                Team = slot < 5 ? 2 : 3
            });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    private static List<List<RoundPosition>> Steps(Func<int, IEnumerable<RoundPosition>> at) =>
        [.. Enumerable.Range(0, 40).Select(step => at(step).ToList())];

    // Places: 0 BombsiteA, 1 Ramp, 2 Outside, 3 Heaven.
    private static RoundPositionsDocument Positions(string fingerprint) => new()
    {
        Fingerprint = fingerprint,
        Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(Demo), Sha256 = "abc123" },
        CadenceTicks = Rate,
        Places = ["BombsiteA", "Ramp", "Outside", "Heaven"],
        Rounds =
        [
            new RoundPositionsRound
            {
                Number = 1, FreezeEndTick = 1000, Ct = TeamBSlots,
                Pos = Steps(_ => [new RoundPosition(5, 600, -400, -416, 0), new RoundPosition(6, 1400, -700, -700, 1), new RoundPosition(0, -200, -900, -300, 2)])
            },
            new RoundPositionsRound
            {
                Number = 2, FreezeEndTick = 10000, Ct = TeamBSlots,
                Pos = Steps(_ => [new RoundPosition(5, 610, -410, -416, 0), new RoundPosition(7, 500, -100, -200, 3)])
            },
            new RoundPositionsRound
            {
                Number = 3, FreezeEndTick = 20000, Ct = TeamBSlots,
                Pos = Steps(_ => [new RoundPosition(5, 620, -420, -416, 0)])
            },
            new RoundPositionsRound
            {
                Number = 4, FreezeEndTick = 30000, Ct = TeamASlots,
                Pos = Steps(_ => [new RoundPosition(0, 600, -400, -416, 0)])
            },
            new RoundPositionsRound
            {
                Number = 5, FreezeEndTick = 40000, Ct = TeamBSlots,
                Pos = Steps(_ => [new RoundPosition(5, 630, -430, -416, 0)])
            }
        ]
    };

    [Test]
    public async Task Build_GroupsTheTeamsCtRoundsByMapAndBuy_OverTheSetupWindow()
    {
        using Harness h = await Harness.Create();

        SetupHeatmapSet set = h.Service.Build(h.TeamB);

        await Assert.That(set.Heatmaps.Count).IsEqualTo(2);
        SetupHeatmap pistol = set.Heatmaps[0];
        SetupHeatmap full = set.Heatmaps[1];
        using (Assert.Multiple())
        {
            await Assert.That(set.DemosRead).IsEqualTo(1);
            await Assert.That(set.DemosWithoutPositions).IsEqualTo(0);

            await Assert.That(pistol.Map).IsEqualTo("de_nuke");
            await Assert.That(pistol.Buy).IsEqualTo(BuyType.Pistol);
            await Assert.That(pistol.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1]);
            // Steps 15..35 inclusive, two CT tuples each; the T tuple at Outside never counts.
            await Assert.That(pistol.StateCount).IsEqualTo(21);
            await Assert.That(pistol.Points.Count).IsEqualTo(42);
            await Assert.That(pistol.Points.All(p => p.Side == QuerySide.Ct)).IsTrue();
            await Assert.That(pistol.Points[0]).IsEqualTo(new OverlayPoint(600, -400, -416, QuerySide.Ct));
            await Assert.That(pistol.Positions.Select(p => p.Place)).IsEquivalentTo(["BombsiteA", "Ramp"]);

            await Assert.That(full.Buy).IsEqualTo(BuyType.Full);
            await Assert.That(full.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([2, 3, 5])
                .Because("round 4 is the team's T side, and round 3 belongs even with no window");
            await Assert.That(full.Rounds.Single(r => r.RoundNumber == 3).Sampled).IsFalse();
            await Assert.That(full.SampledRounds).IsEqualTo(2);
            // Round 2: steps 15..19 (contact at 20 s); round 5: steps 15..35.
            await Assert.That(full.StateCount).IsEqualTo(5 + 21);
            await Assert.That(full.Points.Count).IsEqualTo(5 * 2 + 21);
            await Assert.That(full.Positions[0]).IsEqualTo(new SetupPosition("BombsiteA", 2, 2, true));
            await Assert.That(full.Positions[1]).IsEqualTo(new SetupPosition("Heaven", 1, 2, false))
                .Because("held in half the sampled rounds, under the fixed share");
        }

        SetupHeatmapRound first = pistol.Rounds[0];
        using (Assert.Multiple())
        {
            await Assert.That(first.FreezeEndTick).IsEqualTo(1000);
            await Assert.That(first.ClipEndTick).IsEqualTo(1000 + (SetupHeatmapService.SetupToSeconds + SetupHeatmapService.ClipTailSeconds) * Rate);
            await Assert.That(first.TickRate).IsEqualTo(Rate);
            await Assert.That(first.Sha256).IsEqualTo("abc123");
        }

        // Team A defended only round 4, a full buy.
        SetupHeatmapSet teamA = h.Service.Build(h.TeamA);
        await Assert.That(teamA.Heatmaps.Single().Rounds.Single().RoundNumber).IsEqualTo(4);
    }

    [Test]
    public async Task AStalePositionsFile_IsCounted_AndItsRoundsStillOpen()
    {
        using Harness h = await Harness.Create(positionsFingerprint: "ri1;stale");

        SetupHeatmapSet set = h.Service.Build(h.TeamB);

        using (Assert.Multiple())
        {
            await Assert.That(set.DemosWithoutPositions).IsEqualTo(1);
            await Assert.That(set.Heatmaps.Count).IsEqualTo(2);
            await Assert.That(set.Heatmaps.All(m => m.Points.Count == 0)).IsTrue();
            await Assert.That(set.Heatmaps.Sum(m => m.Rounds.Count)).IsEqualTo(4);
            await Assert.That(DossierTabViewModel.HeatmapLineFor(set))
                .IsEqualTo("2 heatmaps over 4 CT rounds · 1 demo without positions; rebuild the index");
        }
    }

    [Test]
    public async Task TheSetupWindow_IsCutShortByContactThePlantAndTheEnd()
    {
        RoundFacts round = Round(1, 1000, BuyType.Full, TeamBSlots, TeamASlots);
        await Assert.That(SetupHeatmapService.SetupWindow(round, Rate)).IsEqualTo((1000 + 15 * Rate, 1000 + 35 * Rate));

        round.PlantTick = 1000 + 25 * Rate;
        await Assert.That(SetupHeatmapService.SetupWindow(round, Rate).To).IsEqualTo(1000 + 25 * Rate - 1);

        round.FirstContactTick = 1000 + 10 * Rate;
        (int from, int to) = SetupHeatmapService.SetupWindow(round, Rate);
        await Assert.That(to < from).IsTrue().Because("contact before the window opens leaves no setup to show");
    }

    [Test]
    public async Task TheDossier_DrawsEachHeatmap_AndEveryHeatmapOpensItsRounds()
    {
        using Harness h = await Harness.Create();
        ReviewQueue queue = new(null);
        List<string> shown = [];
        using DossierTabViewModel vm = new(h.Teams, h.Cache, new VetoHistoryStore(null), false,
            h.Service, queue, id =>
            {
                shown.Add(id);
                return true;
            },
            () => new SetupHeatmapRenderer(_ => null), a => a(), _ => null);

        await Assert.That(vm.HasHeatmapSection).IsTrue();
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == h.TeamB);
        await vm.HeatmapTask;

        await Assert.That(vm.Heatmaps.Count).IsEqualTo(2);
        SetupHeatmapViewModel full = vm.Heatmaps[1];
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsHeatmapBuilding).IsFalse();
            await Assert.That(vm.HeatmapLine).IsEqualTo("2 heatmaps over 4 CT rounds");
            await Assert.That(full.BuyLabel).IsEqualTo("full buy");
            await Assert.That(full.RoundsLabel).IsEqualTo("3 rounds · 2 with a setup sampled");
            await Assert.That(full.FixedLabel).IsEqualTo("fixed: BombsiteA 2/2");
            await Assert.That(full.RotatingLabel).IsEqualTo("rotating: Heaven 1/2");
            await Assert.That(full.OpenLabel).IsEqualTo("Open 3 rounds");
            await Assert.That(full.Overlay.MapName).IsEqualTo("de_nuke");
            await Assert.That(full.Overlay.Count).IsEqualTo(31).Because("the heatmap's points are an Overlay View document");
            await Assert.That(full.ImagePng).IsNotNull();
            await Assert.That(full.HasImageNote).IsFalse();
        }

        using (SKBitmap? decoded = SKBitmap.Decode(full.ImagePng))
        {
            await Assert.That(decoded).IsNotNull();
            await Assert.That(decoded!.Width).IsEqualTo(SetupHeatmapRenderer.Size.Width);
            await Assert.That(decoded.Height).IsEqualTo(SetupHeatmapRenderer.Size.Height);
        }

        vm.OpenRoundsCommand.Execute(full);
        using (Assert.Multiple())
        {
            await Assert.That(queue.ClipCount).IsEqualTo(3);
            await Assert.That(queue.Clips.All(c => c.Source == ReviewSources.Dossier)).IsTrue();
            await Assert.That(queue.Clips.Select(c => c.FromTick)).IsEquivalentTo([10000, 20000, 40000]);
            await Assert.That(queue.Clips[0].Note).IsEqualTo("de_nuke · full buy · round 2");
            await Assert.That(queue.SectionOf(queue.Clips[0].Id)).IsNotNull();
            await Assert.That(vm.ReviewLine).IsEqualTo("3 rounds sent to Review");
            await Assert.That(shown).IsEquivalentTo(["review.queue"]);
        }

        vm.OpenRoundsCommand.Execute(full);
        await Assert.That(vm.ReviewLine).IsEqualTo("already in Review");

        // Deselecting drops the section's rows.
        vm.SelectedTeam = null;
        await Assert.That(vm.HasHeatmaps).IsFalse();
    }

    private sealed class Harness : IDisposable
    {
        private Harness(DemoCacheStore cache, TeamIdentityService teams, RoundIndexStore store, SetupHeatmapService service,
            Guid teamA, Guid teamB)
        {
            Cache = cache;
            Teams = teams;
            Store = store;
            Service = service;
            TeamA = teamA;
            TeamB = teamB;
        }

        public DemoCacheStore Cache { get; }
        public TeamIdentityService Teams { get; }
        public RoundIndexStore Store { get; }
        public SetupHeatmapService Service { get; }
        public Guid TeamA { get; }
        public Guid TeamB { get; }

        public void Dispose()
        {
            Store.Dispose();
            Teams.Dispose();
        }

        public static async Task<Harness> Create(string? positionsFingerprint = null)
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache, new RoundFactsSource(cache), run: _inline);
            await teams.StartAsync();
            using (cache.BeginBatch())
            {
                cache.Upsert(Record());
                cache.Upsert(Record("/d/other.dem", "de_mirage", 1, null));
            }

            await teams.Idle;

            RoundIndexStore store = new(null, cache);
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            store.WritePositions(Demo, Positions(positionsFingerprint ?? sources.FingerprintFor("de_nuke")));
            SetupHeatmapService service = new(teams, cache, store, sources.FingerprintFor);

            Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
            Guid teamB = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(11)) == true)).Id;
            return new Harness(cache, teams, store, service, teamA, teamB);
        }
    }
}
