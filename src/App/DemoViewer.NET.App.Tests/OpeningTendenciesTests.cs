#region

using System.Numerics;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Review;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Teams;
using DemoViewer.NET.ViewModels.Dossier;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Opening Tendencies over one synthetic demo: only the rounds Team Identity puts the team on a side,
///     one block per map and side; the first utility (the team's own earliest release, the other side's
///     never) as a clock histogram and by kind and landing place; first contact as a clock histogram;
///     on T the site split, the entry player by site and the lurk from the positions file; a demo with no
///     grenade rows counted rather than read as no utility; and every number in the Dossier opening its
///     rounds in the Review Queue.
/// </summary>
[NotInParallel]
public class OpeningTendenciesTests
{
    private const string Demo = "/d/openings.dem";
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

    private static RoundFacts Round(int number, int freezeEnd, int[] ctSlots, int[] tSlots, int? contact = null,
        int? plant = null, BombSite site = BombSite.Unknown, params KillStep[] kills) => new()
    {
        Number = number,
        IsLive = true,
        FreezeEndTick = freezeEnd,
        FirstContactTick = contact is { } c ? At(freezeEnd, c) : null,
        PlantTick = plant is { } p ? At(freezeEnd, p) : null,
        PlantSite = site,
        EndTick = At(freezeEnd, 90),
        Ct = new SideFacts { Side = 3, Slots = ctSlots, BuyType = BuyType.Full },
        T = new SideFacts { Side = 2, Slots = tSlots, BuyType = BuyType.Full },
        Kills = [.. kills]
    };

    private static KillStep Kill(int freezeEnd, int seconds, int attacker, int victim) => new()
    {
        Tick = At(freezeEnd, seconds),
        AttackerSlot = attacker,
        VictimSlot = victim
    };

    // Team A (slots 0..4) is T for rounds 1 to 3 and CT for 4 and 5.
    // Round 1: contact at 12 s, p0 wins the opening duel, plant A. Round 2: contact at 22 s after a
    // teamkill that is no duel, p0 loses it, plant B. Round 3: nothing. Round 4: contact at 31 s.
    // Round 5: contact at 61 s.
    private static RoundFactsRows Rows() => new()
    {
        Schema = DemoCacheRecord.RoundFactsSchema,
        Clock = new RoundFactsClock { TickRate = Rate },
        Rounds =
        [
            Round(1, 1000, TeamBSlots, TeamASlots, 12, 40, BombSite.A, Kill(1000, 13, 0, 5)),
            Round(2, 10000, TeamBSlots, TeamASlots, 22, 50, BombSite.B, Kill(10000, 20, 1, 2), Kill(10000, 22, 6, 0)),
            Round(3, 20000, TeamBSlots, TeamASlots),
            Round(4, 30000, TeamASlots, TeamBSlots, 31),
            Round(5, 40000, TeamASlots, TeamBSlots, 61)
        ]
    };

    // Landing east of x = 0 is Ramp, west of it Outside.
    private static GrenadeRow Grenade(string id, GrenadeKind kind, int round, int team, int release, float landingX) => new()
    {
        Id = id,
        Kind = kind,
        RoundNumber = round,
        ThrowerTeam = team,
        ReleaseTick = release,
        ReleasePosition = WorldPoint.From(new Vector3(0, 0, 0)),
        DetonationTick = release + 128,
        DetonationPosition = WorldPoint.From(new Vector3(landingX, 0, 0)),
        EndKind = GrenadeEndKind.Detonated
    };

    private static List<GrenadeRow> Grenades() =>
    [
        // Round 1: CT's smoke at 3 s is theirs; A's first is the smoke at 8 s, the flash at 20 s is not first.
        Grenade("ct1", GrenadeKind.Smoke, 1, 3, At(1000, 3), 500),
        Grenade("s1", GrenadeKind.Smoke, 1, 2, At(1000, 8), 500),
        Grenade("f1", GrenadeKind.Flash, 1, 2, At(1000, 20), 500),
        Grenade("m2", GrenadeKind.Molotov, 2, 2, At(10000, 9), -500),
        Grenade("s4", GrenadeKind.Smoke, 4, 3, At(30000, 2), 500)
    ];

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

    // Round 1: p0..p2 stay together and p3 walks off alone from 15 s; round 2: everyone together;
    // round 3: only two T alive, so nobody is alone. The CT at x = -5000 is never a T teammate.
    private static RoundPositionsDocument Positions(string fingerprint)
    {
        List<List<RoundPosition>> Steps(Func<int, IEnumerable<RoundPosition>> at) =>
            [.. Enumerable.Range(0, 60).Select(step => at(step).ToList())];

        IEnumerable<RoundPosition> Together(bool lurker, int step)
        {
            yield return new RoundPosition(0, 0, 0, 0, 0);
            yield return new RoundPosition(1, 100, 0, 0, 0);
            yield return new RoundPosition(2, 0, 100, 0, 0);
            yield return new RoundPosition(3, lurker && step >= 15 ? 3000 : 200, 0, 0, 0);
            yield return new RoundPosition(5, -5000, 0, 0, 0);
        }

        return new RoundPositionsDocument
        {
            Fingerprint = fingerprint,
            Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(Demo), Sha256 = "abc123" },
            CadenceTicks = Rate,
            Places = ["Ramp"],
            Rounds =
            [
                new RoundPositionsRound { Number = 1, FreezeEndTick = 1000, Ct = TeamBSlots, Pos = Steps(s => Together(true, s)) },
                new RoundPositionsRound { Number = 2, FreezeEndTick = 10000, Ct = TeamBSlots, Pos = Steps(s => Together(false, s)) },
                new RoundPositionsRound
                {
                    Number = 3, FreezeEndTick = 20000, Ct = TeamBSlots,
                    Pos = Steps(_ => [new RoundPosition(0, 0, 0, 0, 0), new RoundPosition(3, 3000, 0, 0, 0)])
                }
            ]
        };
    }

    [Test]
    public async Task Build_GivesOneBlockPerMapAndSide_WithEveryNumberFromItsRounds()
    {
        using Harness h = await Harness.Create();

        OpeningTendenciesSet set = h.Service.Build(h.TeamA);

        await Assert.That(set.Blocks.Count).IsEqualTo(2);
        OpeningTendencies t = set.Blocks[0];
        OpeningTendencies ct = set.Blocks[1];
        using (Assert.Multiple())
        {
            await Assert.That(set.DemosRead).IsEqualTo(1);
            await Assert.That(set.DemosWithoutGrenades).IsEqualTo(0);
            await Assert.That(set.DemosWithoutPositions).IsEqualTo(0);

            await Assert.That(t.Map).IsEqualTo("de_nuke");
            await Assert.That(t.Side).IsEqualTo(2);
            await Assert.That(t.Rounds.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([1, 2, 3]);
            await Assert.That(t.UtilityRounds).IsEqualTo(3);
            await Assert.That(Shape(t.FirstUtilityClock)).IsEqualTo(Joined("0-5 s:0", "5-10 s:2", "none:1"))
                .Because("the CT smoke at 3 s is the other side's, and the empty bar keeps the clock");
            await Assert.That(Shape(t.FirstUtilityPlaces)).IsEqualTo(Joined("molotov at Outside:1", "smoke at Ramp:1"));
            await Assert.That(Shape(t.FirstContactClock))
                .IsEqualTo(Joined("0-5 s:0", "5-10 s:0", "10-15 s:1", "15-20 s:0", "20-25 s:1", "no contact:1"));
            await Assert.That(Shape(t.SiteSplit)).IsEqualTo(Joined("A:1", "B:1", "no plant:1"));
            await Assert.That(Shape(t.EntryBySite)).IsEqualTo(Joined("p0 at A:1", "p0 at B:1"))
                .Because("the round 2 teamkill is no duel, and a lost duel is still the entry");
            await Assert.That(t.LurkRounds).IsEqualTo(3);
            await Assert.That(Shape(t.LurkClock)).IsEqualTo(Joined("0-5 s:0", "5-10 s:0", "10-15 s:0", "15-20 s:1", "no lurk:2"));
            await Assert.That(Shape(t.Lurkers)).IsEqualTo(Joined("p3:1"));

            await Assert.That(ct.Side).IsEqualTo(3);
            await Assert.That(ct.Rounds.Rounds.Select(r => r.RoundNumber)).IsEquivalentTo([4, 5]);
            await Assert.That(Shape(ct.FirstUtilityClock)).IsEqualTo(Joined("0-5 s:1", "none:1"));
            await Assert.That(Shape(ct.FirstUtilityPlaces)).IsEqualTo(Joined("smoke at Ramp:1"));
            await Assert.That(ct.FirstContactClock.Count).IsEqualTo(13).Because("thirteen bars to 60 s+, and both rounds had contact");
            await Assert.That(ct.FirstContactClock[^1].Label).IsEqualTo("60 s+");
            await Assert.That(ct.SiteSplit.Count + ct.EntryBySite.Count + ct.LurkClock.Count).IsEqualTo(0)
                .Because("on CT the opponent picks the site");
        }

        // The clips sit around what was counted, inside the live window.
        TendencyRound utility = t.FirstUtilityClock[1].Rounds[0];
        TendencyRound lurk = t.Lurkers[0].Rounds[0];
        using (Assert.Multiple())
        {
            await Assert.That(utility.RoundNumber).IsEqualTo(1);
            await Assert.That(utility.FromTick).IsEqualTo(At(1000, 8) - OpeningTendenciesService.ClipLeadSeconds * Rate);
            await Assert.That(utility.ToTick).IsEqualTo(At(1000, 8) + 128 + OpeningTendenciesService.ClipTailSeconds * Rate);
            await Assert.That(utility.Sha256).IsEqualTo("abc123");
            await Assert.That(lurk.FromTick).IsEqualTo(At(1000, 15) - 2 * Rate);
            await Assert.That(t.FirstContactClock[^1].Rounds[0].FromTick).IsEqualTo(20000);
        }
    }

    [Test]
    public async Task ADemoWithoutGrenadeRows_IsCounted_NotReadAsNoUtility()
    {
        using Harness h = await Harness.Create(loadGrenades: false);

        OpeningTendenciesSet set = h.Service.Build(h.TeamA);

        using (Assert.Multiple())
        {
            await Assert.That(set.DemosWithoutGrenades).IsEqualTo(1);
            await Assert.That(set.Blocks[0].UtilityRounds).IsEqualTo(0);
            await Assert.That(set.Blocks[0].FirstUtilityClock.Count).IsEqualTo(0);
            await Assert.That(set.Blocks[0].FirstContactClock.Count).IsGreaterThan(0);
            await Assert.That(OpeningTendenciesSectionViewModel.LineFor(set))
                .IsEqualTo("5 rounds over 1 map · 1 demo without grenades; index grenades");
        }
    }

    [Test]
    public async Task TheOpeningDuel_SkipsKillsWithinOneSide_AndTheLurkNeedsThreeSteps()
    {
        RoundFacts round = Rows().Rounds[1];
        (KillStep kill, int slot, bool won) = OpeningTendenciesService.OpeningDuel(round, 2)!.Value;
        using (Assert.Multiple())
        {
            await Assert.That(kill.Tick).IsEqualTo(At(10000, 22));
            await Assert.That(slot).IsEqualTo(0);
            await Assert.That(won).IsFalse();
            await Assert.That(OpeningTendenciesService.OpeningDuel(Rows().Rounds[2], 2)).IsNull();
        }

        RoundPositionsDocument positions = Positions("fp");
        // Plant the bomb two steps after the split: two steps alone are not a lurk.
        RoundFacts early = Rows().Rounds[0];
        early.PlantTick = At(1000, 17);
        await Assert.That(OpeningTendenciesService.Lurk(positions, early, 2, Rate)).IsNull();
        early.PlantTick = At(1000, 18);
        await Assert.That(OpeningTendenciesService.Lurk(positions, early, 2, Rate)).IsEqualTo((3, At(1000, 15)));
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
            openings: h.Service);

        await Assert.That(vm.Openings.HasSection).IsTrue();
        await Assert.That(vm.HasHeatmapSection).IsFalse();
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == h.TeamA);
        await vm.Openings.BuildTask;

        await Assert.That(vm.Openings.Blocks.Count).IsEqualTo(2);
        OpeningBlockViewModel t = vm.Openings.Blocks[0];
        TendencyLinkViewModel bar = t.UtilityClock[1];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Openings.IsBuilding).IsFalse();
            await Assert.That(vm.Openings.Line).IsEqualTo("5 rounds over 1 map");
            await Assert.That(t.SideLabel).IsEqualTo("T");
            await Assert.That(t.IsT).IsTrue();
            await Assert.That(t.UtilityNote).IsEqualTo("first utility over 3 rounds");
            await Assert.That(t.LurkNote).IsEqualTo("lurk over 3 rounds");
            await Assert.That(t.RoundsLink.ToolTip).IsEqualTo("Open 3 rounds");
            await Assert.That(bar.Title).IsEqualTo("de_nuke T · first utility 5-10 s");
            await Assert.That(bar.BarHeight).IsEqualTo(TendencyLinkViewModel.MaxBarHeight);
            await Assert.That(t.UtilityClock[0].CanOpen).IsFalse();
            await Assert.That(t.UtilityClock[2].BarHeight).IsEqualTo(TendencyLinkViewModel.MaxBarHeight / 2);
            await Assert.That(vm.Openings.Blocks[1].IsT).IsFalse();
        }

        // Every number of every block is a link to exactly the rounds it counts.
        IEnumerable<TendencyLinkViewModel> numbers = vm.Openings.Blocks.SelectMany(b =>
            b.UtilityClock.Concat(b.UtilityPlaces).Concat(b.ContactClock).Concat(b.SiteSplit)
                .Concat(b.Entries).Concat(b.LurkClock).Concat(b.Lurkers).Append(b.RoundsLink));
        await Assert.That(numbers.All(n => n.Bucket.Rounds.Count == n.Count && n.CanOpen == n.Count > 0)).IsTrue();

        vm.Openings.OpenCommand.Execute(t.UtilityClock[0]);
        await Assert.That(queue.ClipCount).IsEqualTo(0).Because("a zero opens nothing");

        vm.Openings.OpenCommand.Execute(bar);
        using (Assert.Multiple())
        {
            await Assert.That(queue.ClipCount).IsEqualTo(2);
            await Assert.That(queue.Clips.All(c => c.Source == ReviewSources.Dossier)).IsTrue();
            await Assert.That(queue.Clips[0].Note).IsEqualTo("de_nuke T · first utility 5-10 s · round 1");
            await Assert.That(queue.Clips[1].Note).IsEqualTo("de_nuke T · first utility 5-10 s · round 2");
            await Assert.That(queue.SectionOf(queue.Clips[0].Id)).IsNotNull();
            await Assert.That(vm.Openings.ReviewLine).IsEqualTo("2 rounds sent to Review");
            await Assert.That(shown).IsEquivalentTo(["review.queue"]);
        }

        vm.Openings.OpenCommand.Execute(bar);
        await Assert.That(vm.Openings.ReviewLine).IsEqualTo("already in Review");

        vm.Openings.OpenCommand.Execute(t.SiteSplit[0]);
        await Assert.That(queue.ClipCount).IsEqualTo(3);

        // Deselecting drops the section's blocks.
        vm.SelectedTeam = null;
        await Assert.That(vm.Openings.HasBlocks).IsFalse();
        await Assert.That(vm.Openings.Line).IsEqualTo("");
    }

    // Order matters: a histogram is read left to right.
    private static string Shape(IEnumerable<TendencyBucket> buckets) =>
        string.Join(", ", buckets.Select(b => $"{b.Label}:{b.Count}"));

    private static string Joined(params string[] parts) => string.Join(", ", parts);

    private sealed class Harness : IDisposable
    {
        private Harness(DemoCacheStore cache, TeamIdentityService teams, RoundIndexStore store, GrenadeIndex grenades,
            OpeningTendenciesService service, Guid teamA)
        {
            Cache = cache;
            Teams = teams;
            Store = store;
            Grenades = grenades;
            Service = service;
            TeamA = teamA;
        }

        public DemoCacheStore Cache { get; }
        public TeamIdentityService Teams { get; }
        public RoundIndexStore Store { get; }
        public GrenadeIndex Grenades { get; }
        public OpeningTendenciesService Service { get; }
        public Guid TeamA { get; }

        public void Dispose()
        {
            Grenades.Dispose();
            Store.Dispose();
            Teams.Dispose();
        }

        public static async Task<Harness> Create(bool loadGrenades = true)
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache, new RoundFactsSource(cache), run: _inline);
            await teams.StartAsync();

            DemoCacheRecord record = Record(Demo, "de_nuke", 0, Rows());
            if (loadGrenades)
            {
                GrenadeDocument document = new()
                {
                    Demo = new GrenadeDemoHeader { Sha256 = "abc123", StableKey = DemoCacheStore.StableKey(Demo) },
                    Grenades = Grenades()
                };
                cache.WriteSibling(Demo, GrenadeSidecar.Suffix, GrenadeSidecar.Serialize(document));
                DemoCacheStore.StampGrenades(record);
                record.GrenadeState = DemoAnalysisState.Indexed;
                record.GrenadeCount = document.Grenades.Count;
                record.GrenadeWalker = GrenadeWalker.Version;
            }

            using (cache.BeginBatch())
            {
                cache.Upsert(record);
                cache.Upsert(Record("/d/other.dem", "de_mirage", 1, null));
            }

            await teams.Idle;

            RoundIndexStore store = new(null, cache);
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            store.WritePositions(Demo, Positions(sources.FingerprintFor("de_nuke")));

            GrenadeIndex grenades = new(cache, new RoundIndexEvaluatorTests.MapZones(
                ("de_nuke", new RoundIndexBuilderTests.FakeZoneResolver("zv-nuke", v => v.X < 0 ? "Outside" : "Ramp"))));
            grenades.Load();

            OpeningTendenciesService service = new(teams, cache, grenades, store, sources.FingerprintFor);
            Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
            return new Harness(cache, teams, store, grenades, service, teamA);
        }
    }
}
