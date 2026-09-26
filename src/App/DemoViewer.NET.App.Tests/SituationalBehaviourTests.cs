#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Dossier;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Situational Behaviour over one synthetic demo: only the rounds Team Identity puts the team on a
///     side, one block per map and side, read from Round Facts alone (no positions, no Grenade Index).
///     Round 1 is a pistol the team wins, round 2 (the bonus) it loses; round 3 is played against an eco
///     opponent and won with first contact at 8 s; round 4 the team reaches a 2-up edge and closes it out;
///     round 5 it loses, and the following round crosses the half boundary onto CT, where the buy shows
///     up as a save; round 6 it wins on CT; round 7 it loses on CT with no following round to pair.
/// </summary>
[NotInParallel]
public class SituationalBehaviourTests
{
    private const string Demo = "/d/situational.dem";
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

    private static RoundFacts Round(int number, int[] ctSlots, int[] tSlots, int winner, BuyType ctBuy, BuyType tBuy,
        int? firstContactSeconds = null, int end = 90, params KillStep[] kills)
    {
        int freeze = Freeze(number);
        return new RoundFacts
        {
            Number = number,
            IsLive = true,
            FreezeEndTick = freeze,
            FirstContactTick = firstContactSeconds is { } s ? At(freeze, s) : null,
            EndTick = At(freeze, end),
            WinnerSide = winner,
            Ct = new SideFacts { Side = 3, Slots = ctSlots, PlayersAtFreezeEnd = 5, BuyType = ctBuy },
            T = new SideFacts { Side = 2, Slots = tSlots, PlayersAtFreezeEnd = 5, BuyType = tBuy },
            Kills = [.. kills]
        };
    }

    // Team A (slots 0..4) is T for rounds 1 to 5 and CT for 6 to 7.
    private static RoundFactsRows Rows() => new()
    {
        Schema = DemoCacheRecord.RoundFactsSchema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds =
        [
            Round(1, TeamBSlots, TeamASlots, 2, BuyType.Pistol, BuyType.Pistol),
            Round(2, TeamBSlots, TeamASlots, 3, BuyType.Full, BuyType.Force),
            Round(3, TeamBSlots, TeamASlots, 2, BuyType.Eco, BuyType.Full, firstContactSeconds: 8),
            Round(4, TeamBSlots, TeamASlots, 2, BuyType.Full, BuyType.Full,
                kills:
                [
                    new KillStep { Tick = At(Freeze(4), 10), AttackerSlot = 0, VictimSlot = 5, CtAlive = 4, TAlive = 5 },
                    new KillStep { Tick = At(Freeze(4), 15), AttackerSlot = 1, VictimSlot = 6, CtAlive = 3, TAlive = 5 }
                ]),
            Round(5, TeamBSlots, TeamASlots, 3, BuyType.Full, BuyType.Full),
            Round(6, TeamASlots, TeamBSlots, 3, BuyType.Eco, BuyType.Full),
            Round(7, TeamASlots, TeamBSlots, 2, BuyType.Full, BuyType.Full)
        ]
    };

    private static DemoCacheRecord Record(string path, string map, RoundFactsRows? rows)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
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

    [Test]
    public async Task Build_GivesOneBlockPerMapAndSide_WithEveryNumberFromItsRounds()
    {
        using Harness h = await Harness.Create();

        SituationalBehaviourSet set = h.Service.Build(h.TeamA);

        await Assert.That(set.Blocks.Count).IsEqualTo(2);
        SituationalBehaviourBlock t = set.Blocks[0];
        SituationalBehaviourBlock ct = set.Blocks[1];
        using (Assert.Multiple())
        {
            await Assert.That(set.DemosRead).IsEqualTo(1);

            await Assert.That(t.Map).IsEqualTo("de_nuke");
            await Assert.That(t.Side).IsEqualTo(2);
            await Assert.That(t.SideRounds.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2, 3, 4, 5]);

            await Assert.That(t.PistolRounds.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1]);
            await Assert.That(Shape(t.PistolOutcomes)).IsEqualTo(Joined("won: elimination:1"));
            await Assert.That(Shape(t.PistolFollowUp)).IsEqualTo(Joined("won pistol, lost the bonus:1"));

            await Assert.That(t.AntiEcoRounds.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([3]);
            await Assert.That(Shape(t.AntiEcoBuyTypes)).IsEqualTo(Joined("eco:1"));
            await Assert.That(Shape(t.AntiEcoOutcomes)).IsEqualTo(Joined("won: elimination:1"));
            await Assert.That(Shape(t.AntiEcoContactClock)).IsEqualTo(Joined("0-5 s:0", "5-10 s:1"));

            await Assert.That(t.ManAdvantageRounds.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([4]);
            await Assert.That(Shape(t.ManAdvantage)).IsEqualTo(Joined("2-up:1"));
            await Assert.That(Shape(t.ManAdvantageOutcomes)).IsEqualTo(Joined("closed it out:1"));

            await Assert.That(t.RoundsLost.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([2, 5]);
            await Assert.That(Shape(t.SaveDiscipline)).IsEqualTo(Joined("eco next:1", "full buy next:1"))
                .Because("round 2's loss carries into round 3's own buy, round 5's into round 6's across the half swap");

            await Assert.That(ct.Side).IsEqualTo(3);
            await Assert.That(ct.SideRounds.Count).IsEqualTo(2);
            await Assert.That(ct.PistolRounds.Count).IsEqualTo(0);
            await Assert.That(ct.AntiEcoRounds.Count).IsEqualTo(0);
            await Assert.That(ct.ManAdvantageRounds.Count).IsEqualTo(0);
            await Assert.That(ct.RoundsLost.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([7]);
            await Assert.That(ct.SaveDiscipline.Count).IsEqualTo(0).Because("round 7 has no following round to pair");
        }
    }

    [Test]
    public async Task PistolFollowUpLabel_AndBuyLabel_NameTheVocabulary()
    {
        using (Assert.Multiple())
        {
            await Assert.That(SituationalBehaviourService.ResultWord(Rows().Rounds[0], 2)).IsEqualTo("won");
            await Assert.That(SituationalBehaviourService.ResultWord(Rows().Rounds[1], 2)).IsEqualTo("lost");
            await Assert.That(SituationalBehaviourService.ResultWord(new RoundFacts(), 2)).IsEqualTo("no result");
            await Assert.That(SituationalBehaviourService.PistolFollowUpLabel(Rows().Rounds[0], Rows().Rounds[1], 2))
                .IsEqualTo("won pistol, lost the bonus");
            await Assert.That(SituationalBehaviourService.BuyLabel(BuyType.Eco)).IsEqualTo("eco");
            await Assert.That(SituationalBehaviourService.BuyLabel(BuyType.Semi)).IsEqualTo("semi buy");
            await Assert.That(SituationalBehaviourService.BuyLabel(BuyType.Unknown)).IsEqualTo("unknown buy");
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
            situational: h.Service);

        await Assert.That(vm.Situational.HasSection).IsTrue();
        await Assert.That(vm.PostPlant.HasSection).IsFalse();
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == h.TeamA);
        await vm.Situational.BuildTask;

        await Assert.That(vm.Situational.Blocks.Count).IsEqualTo(2);
        SituationalBlockViewModel t = vm.Situational.Blocks[0];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Situational.IsBuilding).IsFalse();
            await Assert.That(vm.Situational.Line).IsEqualTo("1 pistol over 1 map");
            await Assert.That(t.SideLabel).IsEqualTo("T");
            await Assert.That(t.HasPistols).IsTrue();
            await Assert.That(t.HasAntiEco).IsTrue();
            await Assert.That(t.HasManAdvantage).IsTrue();
            await Assert.That(t.HasLosses).IsTrue();
            await Assert.That(t.RoundsLink.ToolTip).IsEqualTo("Open 5 rounds");
        }

        // Every number of every block is a link to exactly the rounds it counts.
        IEnumerable<TendencyLinkViewModel> numbers = vm.Situational.Blocks.SelectMany(b =>
            b.PistolOutcomes.Concat(b.PistolFollowUp).Concat(b.AntiEcoBuyTypes).Concat(b.AntiEcoOutcomes)
                .Concat(b.AntiEcoContactClock).Concat(b.ManAdvantage).Concat(b.ManAdvantageOutcomes).Concat(b.SaveDiscipline)
                .Append(b.RoundsLink).Append(b.PistolLink).Append(b.AntiEcoLink).Append(b.ManAdvantageLink).Append(b.RoundsLostLink));
        await Assert.That(numbers.All(n => n.Bucket.Rounds.Count == n.Count && n.CanOpen == n.Count > 0)).IsTrue();

        TendencyLinkViewModel manAdvantage = t.ManAdvantage.Single();
        vm.Situational.OpenCommand.Execute(manAdvantage);
        using (Assert.Multiple())
        {
            await Assert.That(queue.ClipCount).IsEqualTo(1);
            await Assert.That(queue.Clips[0].Source).IsEqualTo(ReviewSources.Dossier);
            await Assert.That(queue.Clips[0].Note).IsEqualTo("de_nuke T · led 2-up · round 4");
            await Assert.That(vm.Situational.ReviewLine).IsEqualTo("1 round sent to Review");
            await Assert.That(shown).IsEquivalentTo(["review.queue"]);
        }

        vm.Situational.OpenCommand.Execute(manAdvantage);
        await Assert.That(vm.Situational.ReviewLine).IsEqualTo("already in Review");

        // Deselecting drops the section's blocks.
        vm.SelectedTeam = null;
        await Assert.That(vm.Situational.HasBlocks).IsFalse();
        await Assert.That(vm.Situational.Line).IsEqualTo("");
    }

    // Order matters: the lists are ranked.
    private static string Shape(IEnumerable<TendencyBucket> buckets) =>
        string.Join(", ", buckets.Select(b => $"{b.Label}:{b.Count}"));

    private static string Joined(params string[] parts) => string.Join(", ", parts);

    private sealed class Harness : IDisposable
    {
        private Harness(DemoCacheStore cache, TeamIdentityService teams, SituationalBehaviourService service, Guid teamA)
        {
            Cache = cache;
            Teams = teams;
            Service = service;
            TeamA = teamA;
        }

        public DemoCacheStore Cache { get; }
        public TeamIdentityService Teams { get; }
        public SituationalBehaviourService Service { get; }
        public Guid TeamA { get; }

        public void Dispose() => Teams.Dispose();

        public static async Task<Harness> Create()
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache, new RoundFactsSource(cache), run: _inline);
            await teams.StartAsync();

            using (cache.BeginBatch())
            {
                cache.Upsert(Record(Demo, "de_nuke", Rows()));
                cache.Upsert(Record("/d/other.dem", "de_mirage", null));
            }

            await teams.Idle;

            SituationalBehaviourService service = new(teams, cache);
            Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
            return new Harness(cache, teams, service, teamA);
        }
    }
}
