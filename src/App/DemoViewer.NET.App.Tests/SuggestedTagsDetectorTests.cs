#region

using DemoViewer.NET.Modules.SuggestedTags;
using static DemoViewer.NET.AppTests.SuggestedTagsTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The five detectors on hand-written occupancy (suggested-tags.md §7.1): the four-at-once execute,
///     staggered arrivals in both counting modes, the tempo label, a default that becomes an execute,
///     a fake at B before an A execute, openers inside and outside the window, retakes with one alive CT
///     and with three, the per-side fallback, the round-size skip, and a table that moves every
///     parameter in the profile and sees the proposals change.
/// </summary>
public class SuggestedTagsDetectorTests
{
    [Test]
    public async Task FourAtOnce_FiresTheExecute_WithItsWindowLabelsAndFactors()
    {
        (RoundOccupancy round, List<PlacedEvent> events) = LateTouchExecute();

        List<TagProposal> executes = Detect(round, events).Of(ExecuteDetector.DetectorId);

        await Assert.That(executes.Count).IsEqualTo(1);
        TagProposal execute = executes[0];
        using (Assert.Multiple())
        {
            await Assert.That(execute.Id).IsEqualTo("exec|r1|T|BombsiteA|s=10").Because("second 12 quantised down to 10");
            await Assert.That(execute.Side).IsEqualTo(2);
            await Assert.That(execute.TriggerTick).IsEqualTo(At(12));
            await Assert.That(execute.FromTick).IsEqualTo(At(12 - 6)).Because("the four arrived at 12; lead is 6 s");
            await Assert.That(execute.ToTick).IsEqualTo(At(12 + 4 + 8)).Because("trigger plus T plus lag");
            await Assert.That(execute.Factors["count"]).IsEqualTo(0.8).Because("four of five");
            await Assert.That(execute.Factors["touch"]).IsEqualTo(0.7).Because("the site was touched six seconds in, after T");
            await Assert.That(execute.Factors["utility"]).IsEqualTo(0.7).Because("one T smoke into the region");
            await Assert.That(execute.Confidence).IsEqualTo(0.392);
            await Assert.That(execute.Labels["site"]).IsEqualTo(SiteRegions.SiteA);
            await Assert.That(execute.Labels["count"]).IsEqualTo("4");
            await Assert.That(execute.Labels["tempo"]).IsEqualTo("rush").Because("second 12 is a rush");
            await Assert.That(execute.Labels["plant"]).IsEqualTo("true").Because("the bomb went down at A after the trigger");
            await Assert.That(execute.Evidence.Select(e => e.Tick)).IsOrderedBy(t => t);
            await Assert.That(execute.Evidence.Any(e => e.Text == "4 T in BombsiteA+ALong+ASmall")).IsTrue();
        }
    }

    [Test]
    public async Task StaggeredArrivals_OnlyTheWindowModeFires()
    {
        RoundOccupancy round = StaggeredArrivals();

        List<TagProposal> instant = Detect(round).Of(ExecuteDetector.DetectorId);
        List<TagProposal> window = Detect(round, profile: DetectorProfile.Default.With("execute", "window", 1))
            .Of(ExecuteDetector.DetectorId);

        using (Assert.Multiple())
        {
            await Assert.That(instant).IsEmpty().Because("never more than two at one second");
            await Assert.That(window.Count).IsEqualTo(1).Because("four distinct players inside one four-second window");
            await Assert.That(window[0].TriggerTick).IsEqualTo(At(10));
            await Assert.That(window[0].Labels["count"]).IsEqualTo("4");
            await Assert.That(window[0].FromTick).IsEqualTo(At(10 - 6)).Because("the first of the four arrived at 10");
        }
    }

    [Test]
    public async Task WindowMode_WithoutPerSlotData_CountsAtOneSecond()
    {
        RoundOccupancy staggered = StaggeredArrivals();
        RoundOccupancy perSide = ToPerSide(staggered);

        List<TagProposal> window = Detect(perSide, profile: DetectorProfile.Default.With("execute", "window", 1))
            .Of(ExecuteDetector.DetectorId);

        await Assert.That(window).IsEmpty().Because("the per-side token cannot tell four players from one player seen four times");
    }

    [Test]
    [Arguments(12, "rush")]
    [Arguments(25, "rush")]
    [Arguments(26, "mid")]
    [Arguments(59, "mid")]
    [Arguments(60, "late")]
    public async Task Tempo_FollowsRushAndSlowSeconds(int second, string tempo)
    {
        await Assert.That(ExecuteDetector.Tempo(second, DetectorProfile.Default)).IsEqualTo(tempo);
    }

    [Test]
    public async Task ARushAtTwelve_IsLabelledRush_AndTheSameRoundLaterIsMid()
    {
        (RoundOccupancy round, List<PlacedEvent> events) = LateTouchExecute();
        TagProposal early = Detect(round, events).Of(ExecuteDetector.DetectorId).Single();
        TagProposal later = Detect(round, events, DetectorProfile.Default.With("execute", "rushSecond", 10))
            .Of(ExecuteDetector.DetectorId).Single();

        using (Assert.Multiple())
        {
            await Assert.That(early.Labels["tempo"]).IsEqualTo("rush");
            await Assert.That(later.Labels["tempo"]).IsEqualTo("mid");
        }
    }

    [Test]
    public async Task ADefault_ThatBecomesAnExecuteAt55_NamesItAndEndsAtIt()
    {
        (RoundOccupancy round, List<PlacedEvent> events) = SpreadDefault(executeAt: 55);

        IReadOnlyList<TagProposal> proposals = Detect(round, events);
        TagProposal execute = proposals.Of(ExecuteDetector.DetectorId).Single();
        TagProposal @default = proposals.Of(DefaultDetector.DetectorId).Single();

        using (Assert.Multiple())
        {
            await Assert.That(execute.TriggerTick).IsEqualTo(At(55));
            await Assert.That(execute.Labels["tempo"]).IsEqualTo("mid");
            await Assert.That(@default.Id).IsEqualTo("default|r1|T");
            await Assert.That(@default.Labels["then"]).IsEqualTo("execute@BombsiteA");
            await Assert.That(@default.Labels["places"]).IsEqualTo("ALong,BTunnel,Middle");
            await Assert.That(@default.FromTick).IsEqualTo(Start);
            await Assert.That(@default.ToTick).IsEqualTo(execute.TriggerTick);
            await Assert.That(@default.Confidence).IsEqualTo(0.6).Because("three places at the threshold plus smokes into both regions");
        }
    }

    [Test]
    public async Task ADefault_WithNoExecute_RunsToDefaultSecondPlusLag()
    {
        (RoundOccupancy round, List<PlacedEvent> events) = SpreadDefault();

        IReadOnlyList<TagProposal> proposals = Detect(round, events);
        TagProposal @default = proposals.Of(DefaultDetector.DetectorId).Single();

        using (Assert.Multiple())
        {
            await Assert.That(proposals.Of(ExecuteDetector.DetectorId)).IsEmpty();
            await Assert.That(@default.Labels["places"]).IsEqualTo("ALong,BTunnel,Connector,Middle");
            await Assert.That(@default.Labels.ContainsKey("then")).IsFalse();
            await Assert.That(@default.ToTick).IsEqualTo(At(40 + 10));
            await Assert.That(@default.Confidence).IsEqualTo(0.7).Because("0.5, one place over the threshold, smokes into both regions");
        }
    }

    [Test]
    public async Task AnExecuteByDefaultSecond_LeavesNoDefault()
    {
        (RoundOccupancy round, List<PlacedEvent> events) = SpreadDefault(executeAt: 35);

        IReadOnlyList<TagProposal> proposals = Detect(round, events);

        using (Assert.Multiple())
        {
            await Assert.That(proposals.Of(ExecuteDetector.DetectorId).Count).IsEqualTo(1);
            await Assert.That(proposals.Of(DefaultDetector.DetectorId)).IsEmpty().Because("a round gets one or the other");
        }
    }

    [Test]
    public async Task ATwoPlayerFakeAtB_BeforeAnAExecute_SpansBoth()
    {
        IReadOnlyList<TagProposal> proposals = Detect(FakeThenA());
        TagProposal execute = proposals.Of(ExecuteDetector.DetectorId).Single();
        TagProposal fake = proposals.Of(FakeDetector.DetectorId).Single();

        using (Assert.Multiple())
        {
            await Assert.That(execute.TriggerTick).IsEqualTo(At(40));
            await Assert.That(fake.Id).IsEqualTo("fake|r1|T|BombsiteB>BombsiteA|s=40");
            await Assert.That(fake.Labels["fake"]).IsEqualTo(SiteRegions.SiteB);
            await Assert.That(fake.Labels["real"]).IsEqualTo(SiteRegions.SiteA);
            await Assert.That(fake.Labels["presence"]).IsEqualTo("2");
            await Assert.That(fake.Labels["utility"]).IsEqualTo("0");
            await Assert.That(fake.TriggerTick).IsEqualTo(At(20));
            await Assert.That(fake.FromTick).IsEqualTo(At(20 - 4));
            await Assert.That(fake.ToTick).IsEqualTo(execute.ToTick).Because("the fake band spans the execute's");
            await Assert.That(fake.Confidence).IsEqualTo(Math.Round(execute.Confidence * 0.8, 4));
        }
    }

    [Test]
    public async Task OneFaker_AndOneSmoke_AreBelowBothThresholds()
    {
        IReadOnlyList<TagProposal> proposals = Detect(FakeThenA(1), [Det(25, DetonationEvents.Smoke, 7, "BTunnel")]);

        await Assert.That(proposals.Of(FakeDetector.DetectorId)).IsEmpty();
    }

    [Test]
    public async Task ThreeFlashesIn1_5s_AreAnOpener_InOneRegion()
    {
        IReadOnlyList<TagProposal> proposals = Detect(Round(), ThreeFlashes());
        TagProposal opener = proposals.Of(OpenerDetector.DetectorId).Single();

        using (Assert.Multiple())
        {
            await Assert.That(opener.Id).IsEqualTo("opener|r1|T|BombsiteA|s=10");
            await Assert.That(opener.Labels["count"]).IsEqualTo("3");
            await Assert.That(opener.Labels["region"]).IsEqualTo(SiteRegions.SiteA);
            await Assert.That(opener.Labels["kinds"]).IsEqualTo("flash");
            await Assert.That(opener.Confidence).IsEqualTo(0.8).Because("0.5, one region, and before second 20");
            await Assert.That(opener.FromTick).IsEqualTo(At(10 - 5));
            await Assert.That(opener.ToTick).IsEqualTo(At(11.5 + 6));
        }
    }

    [Test]
    public async Task ThreeFlashesAcross3s_AreNot()
    {
        IReadOnlyList<TagProposal> proposals = Detect(Round(), ThreeFlashes(3));

        await Assert.That(proposals.Of(OpenerDetector.DetectorId)).IsEmpty();
    }

    [Test]
    public async Task AnInfernoWithNoThrower_CountsForNoSide()
    {
        List<PlacedEvent> events =
        [
            Det(10, DetonationEvents.Flash, 6, "ALong"),
            Det(10.5, DetonationEvents.Flash, 7, "ALong"),
            Det(11, DetonationEvents.Inferno, -1, "ALong")
        ];

        await Assert.That(Detect(Round(), events).Of(OpenerDetector.DetectorId)).IsEmpty();
    }

    [Test]
    public async Task ARetakeWithThreeAliveCts_GroupsAllThree()
    {
        TagProposal retake = Detect(Retake()).Of(RetakeDetector.DetectorId).Single();

        using (Assert.Multiple())
        {
            await Assert.That(retake.Id).IsEqualTo("retake|r1|CT|BombsiteA|s=55");
            await Assert.That(retake.Side).IsEqualTo(3);
            await Assert.That(retake.Labels["group"]).IsEqualTo("3");
            await Assert.That(retake.Labels["aliveCt"]).IsEqualTo("3");
            await Assert.That(retake.Labels["outcome"]).IsEqualTo("defused");
            await Assert.That(retake.FromTick).IsEqualTo(At(55 - 5));
            await Assert.That(retake.ToTick).IsEqualTo(At(61 + 10));
            await Assert.That(retake.Confidence).IsEqualTo(0.75).Because("0.5, one over the pair, defused");
        }
    }

    [Test]
    public async Task ARetakeWithOneAliveCt_ProducesNothing()
    {
        await Assert.That(Detect(Retake(aliveCts: 1)).Of(RetakeDetector.DetectorId)).IsEmpty();
    }

    [Test]
    public async Task TheRetakeFallback_OnThePerSideToken_ReadsTheRise()
    {
        RoundOccupancy perSide = ToPerSide(Retake(defused: false));

        TagProposal retake = Detect(perSide).Of(RetakeDetector.DetectorId).Single();

        using (Assert.Multiple())
        {
            await Assert.That(perSide.HasSlots).IsFalse();
            await Assert.That(retake.Labels["group"]).IsEqualTo("2").Because("the count first reaches two at 58");
            await Assert.That(retake.Labels["outcome"]).IsEqualTo("exploded");
            await Assert.That(retake.TriggerTick).IsEqualTo(At(55)).Because("the first entrant stood there from 55");
            await Assert.That(retake.ToTick).IsEqualTo(At(58 + 10)).Because("the group formed at 58");
        }
    }

    [Test]
    public async Task ARoundShortOfFourASide_IsSkipped()
    {
        (RoundOccupancy full, List<PlacedEvent> events) = LateTouchExecute();
        RoundOccupancy threeT = Round(tSlots: [6, 7, 8], t: new Dictionary<int, string?[]>
        {
            [6] = Path(90, "TSpawn", (12, 30, "ALong")),
            [7] = Path(90, "TSpawn", (12, 30, "ALong")),
            [8] = Path(90, "TSpawn", (12, 30, "ALong"))
        });

        using (Assert.Multiple())
        {
            await Assert.That(Detect(full, events)).IsNotEmpty();
            await Assert.That(Detect(threeT, ThreeFlashes())).IsEmpty();
        }
    }

    [Test]
    public async Task TheOrder_IsData_AndAFakeWithoutTheExecuteBeforeItFindsNothing()
    {
        DetectorProfile fakeFirst = DetectorProfile.Default.WithOrder(["fake", "execute"]);

        IReadOnlyList<TagProposal> proposals = Detect(FakeThenA(), profile: fakeFirst);

        using (Assert.Multiple())
        {
            await Assert.That(proposals.Of(ExecuteDetector.DetectorId).Count).IsEqualTo(1);
            await Assert.That(proposals.Of(FakeDetector.DetectorId)).IsEmpty();
            await Assert.That(proposals.Of(OpenerDetector.DetectorId)).IsEmpty().Because("the order names only two");
        }
    }

    [Test]
    public async Task EveryConfidence_IsClampedAndRounded()
    {
        List<TagProposal> all =
        [
            .. Detect(LateTouchExecute().Round, LateTouchExecute().Events),
            .. Detect(FakeThenA()),
            .. Detect(Round(), ThreeFlashes()),
            .. Detect(Retake()),
            .. Detect(SpreadDefault().Round, SpreadDefault().Events)
        ];

        await Assert.That(all.Count).IsGreaterThanOrEqualTo(6);
        foreach (TagProposal p in all)
        {
            await Assert.That(p.Confidence).IsBetween(0.05, 0.99).Because(p.Id);
            await Assert.That(Math.Round(p.Confidence, 4)).IsEqualTo(p.Confidence).Because(p.Id);
        }
    }

    // ── Every parameter moves something ──────────────────────────────────────────────────────────

    // (detector, parameter, moved value, scenario). The scenario is built so that the moved value
    // changes what that detector proposes; the shipped value's proposals are the baseline.
    private static readonly (string Detector, string Parameter, double Moved, string Scenario)[] _moves =
    [
        ("execute", "N", 5, "late-touch"),
        ("execute", "T", 6, "late-touch"),
        ("execute", "touch", 1, "late-touch"),
        ("execute", "minSecond", 13, "late-touch"),
        ("execute", "lead", 2, "late-touch"),
        ("execute", "lag", 3, "late-touch"),
        ("execute", "rushSecond", 10, "late-touch"),
        ("execute", "slowSecond", 50, "default-then-execute"),
        ("execute", "window", 1, "staggered"),
        ("default", "defaultSecond", 95, "spread"),
        ("default", "spreadSecond", 5, "spread"),
        ("default", "spreadPlaces", 5, "spread"),
        ("default", "lag", 20, "spread"),
        ("fake", "fakeWindow", 5, "fake"),
        ("fake", "fakePresence", 1, "weak-fake"),
        ("fake", "fakeUtility", 1, "weak-fake"),
        ("fake", "lead", 8, "fake"),
        ("opener", "K", 4, "flashes"),
        ("opener", "W", 1, "flashes"),
        ("opener", "lead", 2, "flashes"),
        ("opener", "lag", 2, "flashes"),
        ("retake", "retakeGap", 2, "retake"),
        ("retake", "minGroup", 4, "retake"),
        ("retake", "lead", 1, "retake"),
        ("retake", "lag", 3, "retake")
    ];

    public static IEnumerable<Func<(string, string, double, string)>> Moves() =>
        _moves.Select(m => (Func<(string, string, double, string)>)(() => m));

    [Test]
    [MethodDataSource(nameof(Moves))]
    public async Task MovingAParameter_ChangesWhatItsDetectorProposes(string detector, string parameter, double moved, string scenario)
    {
        (RoundOccupancy round, List<PlacedEvent> events) = Scenario(scenario);
        DetectorProfile shipped = DetectorProfile.Default;
        DetectorProfile changed = shipped.With(detector, parameter, moved);

        List<string> before = [.. Detect(round, events, shipped).Of(detector).Select(Describe)];
        List<string> after = [.. Detect(round, events, changed).Of(detector).Select(Describe)];

        using (Assert.Multiple())
        {
            await Assert.That(shipped.Get(detector, parameter)).IsNotEqualTo(moved);
            await Assert.That(before.Count + after.Count).IsGreaterThan(0).Because("the scenario must exercise the detector");
            await Assert.That(string.Join('\n', after)).IsNotEqualTo(string.Join('\n', before))
                .Because($"{detector}.{parameter} = {moved} must change {detector}'s proposals in '{scenario}'");
        }
    }

    [Test]
    public async Task TheMoveTable_CoversEveryParameterInTheProfile()
    {
        HashSet<string> declared = [.. ProposalDetection.All.SelectMany(d => d.Parameters.Select(p => $"{d.Id}.{p.Name}"))];
        HashSet<string> moved = [.. _moves.Select(m => $"{m.Detector}.{m.Parameter}")];

        await Assert.That(moved).IsEquivalentTo(declared);
    }

    private static (RoundOccupancy Round, List<PlacedEvent> Events) Scenario(string name) => name switch
    {
        "late-touch" => LateTouchExecute(),
        "staggered" => (StaggeredArrivals(), []),
        "spread" => SpreadDefault(),
        "default-then-execute" => SpreadDefault(executeAt: 55),
        "fake" => (FakeThenA(), []),
        "weak-fake" => (FakeThenA(1), [Det(25, DetonationEvents.Smoke, 7, "BTunnel")]),
        "flashes" => (Round(), ThreeFlashes()),
        "retake" => (Retake(), []),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    // The same round as the Round Index would carry it: per-side counts only.
    private static RoundOccupancy ToPerSide(RoundOccupancy round)
    {
        Dictionary<int, (IReadOnlyList<(string Place, int Count)> Ct, IReadOnlyList<(string Place, int Count)> T)> bySecond = [];
        for (int s = 0; s < round.Seconds; s++)
        {
            bySecond[s] = (Pairs(3, s), Pairs(2, s));
        }

        Dictionary<int, int> sides = [];
        foreach (int side in (int[])[2, 3])
        {
            foreach (int slot in round.Slots(side))
            {
                sides[slot] = side;
            }
        }

        return RoundOccupancy.FromSideCounts(round.Round, round.StartTick, round.EndTick, round.TickRate, sides, bySecond,
            round.Bomb);

        List<(string Place, int Count)> Pairs(int side, int second)
        {
            List<(string Place, int Count)> pairs = [.. round.CountsAt(side, second).Select(p => (p.Key, p.Value))];
            int unplaced = round.AliveCount(side, second) - pairs.Sum(p => p.Count);
            if (unplaced > 0)
            {
                pairs.Add((RoundOccupancy.Unplaced, unplaced));
            }

            return pairs;
        }
    }
}
