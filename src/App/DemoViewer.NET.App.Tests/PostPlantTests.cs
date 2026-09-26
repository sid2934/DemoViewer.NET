#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Dossier;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Post-Plant And Retake over one synthetic demo: only the rounds Team Identity puts the team on a
///     side, one block per map and side; the planted rounds, the man count at the plant and the outcomes
///     from Round Facts alone; on T the plant spots clustered per site and the hold read from the positions
///     file; on CT the retake grouped by when the CTs alive at the plant reached the bomb; a demo with no
///     positions counted rather than read as rounds with no hold; and every number in the Dossier opening
///     its rounds in the Review Queue.
/// </summary>
[NotInParallel]
public class PostPlantTests
{
    private const string Demo = "/d/postplant.dem";
    private const int Rate = 64;

    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly int[] TeamASlots = [0, 1, 2, 3, 4];
    private static readonly int[] TeamBSlots = [5, 6, 7, 8, 9];

    private static string Id(int n) => $"7656{n:D4}";

    private static int At(int freezeEnd, int seconds) => freezeEnd + seconds * Rate;

    private static int Freeze(int number) => number * 10000;

    private static RoundFacts Round(int number, int[] ctSlots, int[] tSlots, int? plant = null, BombSite site = BombSite.Unknown,
        int? planter = null, int winner = 0, RoundEndReason reason = RoundEndReason.Unknown, int end = 90, params KillStep[] kills)
    {
        int freeze = Freeze(number);
        return new RoundFacts
        {
            Number = number,
            IsLive = true,
            FreezeEndTick = freeze,
            PlantTick = plant is { } p ? At(freeze, p) : null,
            PlantSite = site,
            PlanterSlot = planter,
            DefuseTick = reason == RoundEndReason.BombDefused ? At(freeze, end) : null,
            ExplodeTick = reason == RoundEndReason.TargetBombed ? At(freeze, end) : null,
            EndTick = At(freeze, end),
            EndReason = reason,
            WinnerSide = winner,
            Ct = new SideFacts { Side = 3, Slots = ctSlots, PlayersAtFreezeEnd = 5, BuyType = BuyType.Full },
            T = new SideFacts { Side = 2, Slots = tSlots, PlayersAtFreezeEnd = 5, BuyType = BuyType.Full },
            Kills = [.. kills]
        };
    }

    // Team A (slots 0..4) is T for rounds 1 to 5 and CT for 6 to 9.
    // Round 1: plant A by p0 in a 5v4, exploded. Round 2: plant A by p1 a hundred units off, defused at 55 s.
    // Round 3: plant B by p2, the Ts wiped. Round 4: plant B with no planter, exploded. Round 5: no plant.
    // Rounds 6 to 8: B plants A at (0, 0); the retake is together, trickled, then nobody. Round 9: no plant.
    private static RoundFactsRows Rows() => new()
    {
        Schema = DemoCacheRecord.RoundFactsSchema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds =
        [
            Round(1, TeamBSlots, TeamASlots, 40, BombSite.A, 0, 2, RoundEndReason.TargetBombed,
                kills: new KillStep { Tick = At(Freeze(1), 20), AttackerSlot = 0, VictimSlot = 5, CtAlive = 4, TAlive = 5 }),
            Round(2, TeamBSlots, TeamASlots, 50, BombSite.A, 1, 3, RoundEndReason.BombDefused, 55),
            Round(3, TeamBSlots, TeamASlots, 45, BombSite.B, 2, 3, RoundEndReason.CTWin),
            Round(4, TeamBSlots, TeamASlots, 45, BombSite.B, null, 2, RoundEndReason.TargetBombed),
            Round(5, TeamBSlots, TeamASlots, winner: 3, reason: RoundEndReason.TargetSaved),
            Round(6, TeamASlots, TeamBSlots, 40, BombSite.A, 5, 3, RoundEndReason.BombDefused,
                kills:
                [
                    new KillStep { Tick = At(Freeze(6), 20), AttackerSlot = 5, VictimSlot = 3, CtAlive = 4, TAlive = 5 },
                    new KillStep { Tick = At(Freeze(6), 25), AttackerSlot = 5, VictimSlot = 4, CtAlive = 3, TAlive = 5 }
                ]),
            Round(7, TeamASlots, TeamBSlots, 40, BombSite.A, 5, 2, RoundEndReason.TargetBombed),
            Round(8, TeamASlots, TeamBSlots, 40, BombSite.A, 5, 2, RoundEndReason.TerroristsWin),
            Round(9, TeamASlots, TeamBSlots, winner: 3, reason: RoundEndReason.CTWin)
        ]
    };

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

    private static RoundPosition P(int slot, int x, int y, int place = 0) => new(slot, x, y, 0, place);

    private static RoundPositionsRound Stored(int number, int[] ct, Func<int, IEnumerable<RoundPosition>> at) => new()
    {
        Number = number,
        FreezeEndTick = Freeze(number),
        Ct = ct,
        Pos = [.. Enumerable.Range(0, 90).Select(step => at(step).ToList())]
    };

    // One step a second. Places: 0 BombsiteA, 1 Palace.
    // A on site A: p0 on the bomb, p1 a hundred units off, p2 707 units off (away), p3 and p4 in Palace.
    // A on site B: all five at (-2000, 0) in Palace. The CT at (-5000, 0) is never a T.
    // A retaking (0, 0): round 6 p0..p2 arrive at 45, 46 and 47 s; round 7 p0 at 42 s and p1 at 55 s;
    // round 8 nobody leaves (5000, 0).
    private static RoundPositionsDocument Positions(string fingerprint)
    {
        IEnumerable<RoundPosition> SiteA(int s) =>
        [
            P(0, 1000, 1000), P(1, 1100, 1000), P(2, 1500, 1500), P(3, 3000, 0, 1), P(4, 3000, 100, 1), P(5, -5000, 0)
        ];

        IEnumerable<RoundPosition> SiteB(int s) =>
            [.. TeamASlots.Select(slot => P(slot, -2000, 0, 1)), P(5, -5000, 0)];

        IEnumerable<RoundPosition> Retake(int s, params int[] arrivals)
        {
            for (int i = 0; i < arrivals.Length; i++)
            {
                yield return P(i, s >= arrivals[i] ? 500 : 5000, 0);
            }

            yield return P(5, 0, 0);
        }

        return new RoundPositionsDocument
        {
            Fingerprint = fingerprint,
            Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(Demo), Sha256 = "abc123" },
            CadenceTicks = Rate,
            Places = ["BombsiteA", "Palace"],
            Rounds =
            [
                Stored(1, TeamBSlots, SiteA),
                Stored(2, TeamBSlots, SiteA),
                Stored(3, TeamBSlots, SiteB),
                Stored(4, TeamBSlots, SiteB),
                Stored(5, TeamBSlots, SiteB),
                Stored(6, TeamASlots, s => Retake(s, 45, 46, 47)),
                Stored(7, TeamASlots, s => Retake(s, 42, 55)),
                Stored(8, TeamASlots, s => Retake(s, 999, 999, 999))
            ]
        };
    }

    [Test]
    public async Task Build_GivesOneBlockPerMapAndSide_WithEveryNumberFromItsRounds()
    {
        using Harness h = await Harness.Create();

        PostPlantSet set = h.Service.Build(h.TeamA);

        await Assert.That(set.Blocks.Count).IsEqualTo(2);
        PostPlantBlock t = set.Blocks[0];
        PostPlantBlock ct = set.Blocks[1];
        using (Assert.Multiple())
        {
            await Assert.That(set.DemosRead).IsEqualTo(1);
            await Assert.That(set.DemosWithoutPositions).IsEqualTo(0);

            await Assert.That(t.Map).IsEqualTo("de_nuke");
            await Assert.That(t.Side).IsEqualTo(2);
            await Assert.That(t.SideRounds.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2, 3, 4, 5]);
            await Assert.That(t.Planted.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2, 3, 4]);
            await Assert.That(Shape(t.ManCount)).IsEqualTo(Joined("5v5:3", "5v4:1"));
            await Assert.That(Shape(t.Outcomes)).IsEqualTo(Joined("won: exploded:2", "lost: defused:1", "lost: elimination:1"));
            await Assert.That(t.PositionRounds).IsEqualTo(4);
            await Assert.That(Shape(t.PlantClusters)).IsEqualTo(Joined("A spot 1 (1050, 1000):2", "B spot 1 (-2000, 0):1", "B spot unread:1"))
                .Because("p1's plant a hundred units from p0's joins its cluster, and a plant with no planter has no spot");
            await Assert.That(Shape(t.HoldShapes))
                .IsEqualTo(Joined("2 near the bomb, 3 away:1", "3 near the bomb, 2 away:1", "5 near the bomb, 0 away:1", "spot unread:1"));
            await Assert.That(Shape(t.HoldPlaces)).IsEqualTo(Joined("Palace:4", "BombsiteA:2"));
            await Assert.That(t.RetakeGroups.Count).IsEqualTo(0).Because("on T the team holds, it does not retake");

            await Assert.That(ct.Side).IsEqualTo(3);
            await Assert.That(ct.SideRounds.Count).IsEqualTo(4);
            await Assert.That(ct.Planted.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([6, 7, 8]);
            await Assert.That(Shape(ct.ManCount)).IsEqualTo(Joined("5v5:2", "3v5:1"));
            await Assert.That(Shape(ct.Outcomes)).IsEqualTo(Joined("lost: elimination:1", "lost: exploded:1", "won: defused:1"));
            await Assert.That(Shape(ct.RetakeGroups)).IsEqualTo(Joined("nobody reached the bomb:1", "together:1", "trickled:1"));
            await Assert.That(ct.PlantClusters.Count + ct.HoldShapes.Count + ct.HoldPlaces.Count).IsEqualTo(0)
                .Because("on CT the opponent plants and holds");
        }

        // The clips run from just before the plant to the end, inside the live window.
        TendencyRound first = t.Planted.Rounds[0];
        TendencyRound defused = t.Planted.Rounds[1];
        using (Assert.Multiple())
        {
            await Assert.That(first.FromTick).IsEqualTo(At(Freeze(1), 40) - PostPlantService.ClipLeadSeconds * Rate);
            await Assert.That(first.ToTick).IsEqualTo(At(Freeze(1), 40 + PostPlantService.PostPlantSeconds));
            await Assert.That(defused.ToTick).IsEqualTo(At(Freeze(2), 55));
            await Assert.That(first.Sha256).IsEqualTo("abc123");
        }
    }

    [Test]
    public async Task ADemoWithoutPositions_IsCounted_NotReadAsNoHold()
    {
        using Harness h = await Harness.Create(writePositions: false);

        PostPlantSet set = h.Service.Build(h.TeamA);

        using (Assert.Multiple())
        {
            await Assert.That(set.DemosWithoutPositions).IsEqualTo(1);
            await Assert.That(set.Blocks[0].PositionRounds).IsEqualTo(0);
            await Assert.That(set.Blocks[0].PlantClusters.Count + set.Blocks[0].HoldShapes.Count).IsEqualTo(0);
            await Assert.That(set.Blocks[1].RetakeGroups.Count).IsEqualTo(0);
            await Assert.That(set.Blocks[0].ManCount.Count).IsGreaterThan(0).Because("the man count is Round Facts alone");
            await Assert.That(PostPlantSectionViewModel.LineFor(set))
                .IsEqualTo("7 plants over 1 map · 1 demo without positions; rebuild the index");
        }
    }

    [Test]
    public async Task TheRetakeGap_SplitsGroups_AndTheOutcomeNamesHowItEnded()
    {
        RoundPositionsDocument positions = Positions("fp");
        RoundPosition bomb = P(5, 0, 0);
        RoundFacts round = Rows().Rounds[6];

        // Arrivals exactly the gap apart are one group; a second more is two.
        positions.Rounds[6] = Stored(7, TeamASlots, s =>
            [P(0, s >= 42 ? 500 : 5000, 0), P(1, s >= 42 + PostPlantService.RetakeGapSeconds ? 500 : 5000, 0)]);
        await Assert.That(PostPlantService.RetakeGroup(positions, round, bomb, Rate)).IsEqualTo(PostPlantService.Together);
        positions.Rounds[6] = Stored(7, TeamASlots, s =>
            [P(0, s >= 42 ? 500 : 5000, 0), P(1, s >= 43 + PostPlantService.RetakeGapSeconds ? 500 : 5000, 0)]);
        await Assert.That(PostPlantService.RetakeGroup(positions, round, bomb, Rate)).IsEqualTo(PostPlantService.Trickled);

        // One CT alive who reaches the bomb, and no CT alive at all.
        positions.Rounds[6] = Stored(7, TeamASlots, s => [P(0, 500, 0), P(5, 0, 0)]);
        await Assert.That(PostPlantService.RetakeGroup(positions, round, bomb, Rate)).IsEqualTo(PostPlantService.OnePlayer);
        positions.Rounds[6] = Stored(7, TeamASlots, s => [P(5, 0, 0)]);
        await Assert.That(PostPlantService.RetakeGroup(positions, round, bomb, Rate)).IsEqualTo(PostPlantService.NoCtAlive);

        using (Assert.Multiple())
        {
            await Assert.That(PostPlantService.PlantSpot(Positions("fp"), Rows().Rounds[3])).IsNull();
            await Assert.That(PostPlantService.PlantSpot(Positions("fp"), Rows().Rounds[0])).IsEqualTo(P(0, 1000, 1000));
            await Assert.That(PostPlantService.OutcomeLabel(Rows().Rounds[1], 2)).IsEqualTo("lost: defused");
            await Assert.That(PostPlantService.OutcomeLabel(Rows().Rounds[1], 3)).IsEqualTo("won: defused");
            await Assert.That(PostPlantService.OutcomeLabel(new RoundFacts(), 2)).IsEqualTo("no result");
            await Assert.That(PostPlantService.ManCountLabel(Rows().Rounds[5], 3)).IsEqualTo("3v5");
            await Assert.That(PostPlantService.ManCountLabel(Rows().Rounds[5], 2)).IsEqualTo("5v3");
        }
    }

    [Test]
    public async Task TheDossier_ShowsTheSection_AndEveryNumberOpensItsRounds()
    {
        using Harness h = await Harness.Create();
        ReviewQueue queue = new(null);
        List<string> shown = [];
        using DossierTabViewModel vm = new(h.Teams, h.Cache, new VetoHistoryStore(null), false,
            review: queue,
            selectTab: id =>
            {
                shown.Add(id);
                return true;
            },
            post: a => a(),
            postPlant: h.Service);

        await Assert.That(vm.PostPlant.HasSection).IsTrue();
        await Assert.That(vm.Openings.HasSection).IsFalse();
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == h.TeamA);
        await vm.PostPlant.BuildTask;

        await Assert.That(vm.PostPlant.Blocks.Count).IsEqualTo(2);
        PostPlantBlockViewModel t = vm.PostPlant.Blocks[0];
        PostPlantBlockViewModel ct = vm.PostPlant.Blocks[1];
        TendencyLinkViewModel cluster = t.PlantClusters[0];
        using (Assert.Multiple())
        {
            await Assert.That(vm.PostPlant.IsBuilding).IsFalse();
            await Assert.That(vm.PostPlant.Line).IsEqualTo("7 plants over 1 map");
            await Assert.That(t.SideLabel).IsEqualTo("T");
            await Assert.That(t.ShowsHolds).IsTrue();
            await Assert.That(t.ShowsRetakes).IsFalse();
            await Assert.That(ct.ShowsHolds).IsFalse();
            await Assert.That(ct.ShowsRetakes).IsTrue();
            await Assert.That(t.PlantedText).IsEqualTo("planted in");
            await Assert.That(ct.PlantedText).IsEqualTo("they planted in");
            await Assert.That(t.PlantedLink.CountText).IsEqualTo("4");
            await Assert.That(t.RoundsLink.ToolTip).IsEqualTo("Open 5 rounds");
            await Assert.That(t.PositionNote).IsEqualTo("positions over 4 rounds");
            await Assert.That(cluster.Title).IsEqualTo("de_nuke T · plant A spot 1 (1050, 1000)");
            await Assert.That(ct.RetakeGroups[1].Title).IsEqualTo("de_nuke CT · retake together");
        }

        // Every number of every block is a link to exactly the rounds it counts.
        IEnumerable<TendencyLinkViewModel> numbers = vm.PostPlant.Blocks.SelectMany(b =>
            b.ManCount.Concat(b.Outcomes).Concat(b.PlantClusters).Concat(b.HoldShapes).Concat(b.HoldPlaces)
                .Concat(b.RetakeGroups).Append(b.PlantedLink).Append(b.RoundsLink));
        await Assert.That(numbers.All(n => n.Bucket.Rounds.Count == n.Count && n.CanOpen == n.Count > 0)).IsTrue();

        vm.PostPlant.OpenCommand.Execute(cluster);
        using (Assert.Multiple())
        {
            await Assert.That(queue.ClipCount).IsEqualTo(2);
            await Assert.That(queue.Clips.All(c => c.Source == ReviewSources.Dossier)).IsTrue();
            await Assert.That(queue.Clips[0].Note).IsEqualTo("de_nuke T · plant A spot 1 (1050, 1000) · round 1");
            await Assert.That(queue.Clips[1].Note).IsEqualTo("de_nuke T · plant A spot 1 (1050, 1000) · round 2");
            await Assert.That(queue.SectionOf(queue.Clips[0].Id)).IsNotNull();
            await Assert.That(vm.PostPlant.ReviewLine).IsEqualTo("2 rounds sent to Review");
            await Assert.That(shown).IsEquivalentTo(["review.queue"]);
        }

        vm.PostPlant.OpenCommand.Execute(cluster);
        await Assert.That(vm.PostPlant.ReviewLine).IsEqualTo("already in Review");

        vm.PostPlant.OpenCommand.Execute(ct.RetakeGroups[0]);
        await Assert.That(queue.ClipCount).IsEqualTo(3);

        // Deselecting drops the section's blocks.
        vm.SelectedTeam = null;
        await Assert.That(vm.PostPlant.HasBlocks).IsFalse();
        await Assert.That(vm.PostPlant.Line).IsEqualTo("");
    }

    // Order matters: the lists are ranked.
    private static string Shape(IEnumerable<TendencyBucket> buckets) =>
        string.Join(", ", buckets.Select(b => $"{b.Label}:{b.Count}"));

    private static string Joined(params string[] parts) => string.Join(", ", parts);

    private sealed class Harness : IDisposable
    {
        private Harness(DemoCacheStore cache, TeamIdentityService teams, RoundIndexStore store, PostPlantService service, Guid teamA)
        {
            Cache = cache;
            Teams = teams;
            Store = store;
            Service = service;
            TeamA = teamA;
        }

        public DemoCacheStore Cache { get; }
        public TeamIdentityService Teams { get; }
        public RoundIndexStore Store { get; }
        public PostPlantService Service { get; }
        public Guid TeamA { get; }

        public void Dispose()
        {
            Store.Dispose();
            Teams.Dispose();
        }

        public static async Task<Harness> Create(bool writePositions = true)
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache, new RoundFactsSource(cache), run: _inline);
            await teams.StartAsync();

            using (cache.BeginBatch())
            {
                cache.Upsert(Record(Demo, "de_nuke", 0, Rows()));
                cache.Upsert(Record("/d/other.dem", "de_mirage", 1, null));
            }

            await teams.Idle;

            RoundIndexStore store = new(null, cache);
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            if (writePositions)
            {
                store.WritePositions(Demo, Positions(sources.FingerprintFor("de_nuke")));
            }

            PostPlantService service = new(teams, cache, store, sources.FingerprintFor);
            Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
            return new Harness(cache, teams, store, service, teamA);
        }
    }
}
