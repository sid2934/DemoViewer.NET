#region

using System.Diagnostics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Suggested Tags against the four Valve matchmaking replays the design measured (suggested-tags.md
///     §7.2, §10), read in place from the <c>DEMO_PATH</c> folder; the tour sample is never used. The
///     pinned-round recapture, the walk-versus-index agreement and the every-round survey run against
///     <see cref="SuggestedTagsRealDemoRows" />'s synthesised rows; two more tests run the same fold
///     and index paths over the engine's own Round Facts rows.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class SuggestedTagsRealDemoTests
{
    /// <summary>
    ///     The measured demos and the round pinned from each (one per map, §10), chosen so the four
    ///     together fire all five detectors: de_inferno 11 is an execute at B after a two-player fake at A
    ///     with a default before it and a retake after, de_ancient 24 adds a T opener, de_nuke 15 and
    ///     de_dust2 20 are rush and mid executes.
    /// </summary>
    internal static readonly (string Map, string Demo, int Round)[] Pinned =
    [
        ("de_nuke", "match730_003731893271710924851_1024675027_129.dem", 15),
        ("de_inferno", "match730_003752202995232669993_0995639494_129.dem", 11),
        ("de_dust2", "match730_003744626821098897655_0217256255_410.dem", 20),
        ("de_ancient", "match730_003765251335658668110_1533655444_405.dem", 24)
    ];

    private sealed record Walked(ParsedDemo Parsed, RoundFactsRows Rows, List<PositionSample> Samples, OccupancyBuild Build,
        SiteRegionTable Table, List<PlacedEvent> Events);

    // The replays folder DEMO_PATH names, or the folder of the file it names.
    private static string ReplaysFolder()
    {
        string? env = Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (Directory.Exists(env))
            {
                return env;
            }

            if (File.Exists(env))
            {
                return Path.GetDirectoryName(env)!;
            }
        }

        throw new SkipTestException($"{DemoTestHelper.DemoPathEnvVar} does not name the replays folder the design measured");
    }

    private static Walked Walk(string demo)
    {
        string path = Path.Combine(ReplaysFolder(), demo);
        if (!File.Exists(path))
        {
            throw new SkipTestException($"{demo} is not in the replays folder");
        }

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        List<PositionSample> samples = [.. PositionSampler.Walk(parsed, RoundOccupancyBuilder.FrameStride)];
        RoundFactsRows rows = SuggestedTagsRealDemoRows.Synthesize(parsed, samples);
        OccupancyBuild build = RoundOccupancyBuilder.FromWalk(parsed, rows, samples: samples);
        SiteRegionTable table = SiteRegionLearner.Learn(parsed.MapName, [build.Rounds]);
        List<PlacedEvent> events = new DetonationPlaceResolver(null, null, build.Cloud).Place(DetonationEvents.From(parsed));
        return new Walked(parsed, rows, samples, build, table, events);
    }

    // Same walk, but the rows come from the shipped engine ruleset rather than the demo's own kill
    // timeline. Used by the two tests that check the engine rows against the walk and the index.
    private static Walked WalkFromEngineRows(string demo)
    {
        string path = Path.Combine(ReplaysFolder(), demo);
        if (!File.Exists(path))
        {
            throw new SkipTestException($"{demo} is not in the replays folder");
        }

        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        DemoCacheStore store = new(null);
        RoundFactsEvaluator facts = new(store, new EngineRoundFactsRowSource(), new RulesRoundFactsRulesetIdentity());
        facts.OnParsedOpportunistically(path, parsed);
        RoundFactsRows rows = store.TryLoadRecord(path)?.RoundFacts ?? throw new InvalidOperationException("the evaluator wrote no rows");

        List<PositionSample> samples = [.. PositionSampler.Walk(parsed, RoundOccupancyBuilder.FrameStride)];
        OccupancyBuild build = RoundOccupancyBuilder.FromWalk(parsed, rows, samples: samples);
        SiteRegionTable table = SiteRegionLearner.Learn(parsed.MapName, [build.Rounds]);
        List<PlacedEvent> events = new DetonationPlaceResolver(null, null, build.Cloud).Place(DetonationEvents.From(parsed));
        return new Walked(parsed, rows, samples, build, table, events);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ThePinnedRound_RecapturesToTheCommittedFixture(int which)
    {
        (string map, string demo, int number) = Pinned[which];
        string repo = DemoTestHelper.FindRepoRoot() ?? throw new SkipTestException("repo root not found");
        string file = Path.Combine(SuggestedTagsGolden.FixtureDirectory(repo), $"{map}.json");

        Walked walked = Walk(demo);
        RoundOccupancy round = walked.Build.Rounds.Single(r => r.Round == number);
        SuggestedTagsGolden golden = SuggestedTagsGolden.Capture(demo, map, walked.Parsed.TickRate, walked.Table, round,
            walked.Events);
        golden.Proposals = golden.Detect();

        if (Environment.GetEnvironmentVariable("ST_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, golden.Serialize());
        }

        if (!File.Exists(file))
        {
            throw new SkipTestException($"missing {file}; capture it with ST_GOLDEN_UPDATE=1");
        }

        using (Assert.Multiple())
        {
            await Assert.That(walked.Parsed.MapName).IsEqualTo(map);
            await Assert.That(golden.Serialize()).IsEqualTo(File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal))
                .Because("the fold, the placed events and the learned table of the pinned round are pinned");
        }
    }

    [Test]
    public async Task OnARealDemo_TheWalkAndTheIndex_AgreePerSidePerSecond()
    {
        Walked walked = Walk(Pinned[1].Demo);
        RoundIndexDocument index = RoundIndexBuilder.Build(walked.Parsed, walked.Rows, RoundIndexOptions.Default,
            PawnPlaceSource.Instance);
        IReadOnlyList<RoundOccupancy> fromIndex = RoundOccupancyBuilder.FromIndex(index, walked.Rows, walked.Parsed.TickRate);

        int cells = 0;
        int agree = 0;
        foreach (RoundOccupancy walkedRound in walked.Build.Rounds)
        {
            RoundOccupancy indexed = fromIndex.Single(r => r.Round == walkedRound.Round);
            for (int s = 0; s < walkedRound.Seconds; s++)
            {
                foreach (int side in (int[])[2, 3])
                {
                    if (walkedRound.AliveCount(side, s) == 0 && indexed.AliveCount(side, s) == 0)
                    {
                        continue;
                    }

                    cells++;
                    if (walkedRound.CountsAt(side, s).OrderBy(p => p.Key, StringComparer.Ordinal)
                        .SequenceEqual(indexed.CountsAt(side, s).OrderBy(p => p.Key, StringComparer.Ordinal)))
                    {
                        agree++;
                    }
                }
            }
        }

        Console.WriteLine($"walk versus index: {agree} of {cells} (side, second) cells agree");
        // The two read different frames inside a second (the walk the first of every eighth, the index
        // every sample of its due tick), so a player crossing a boundary inside that second can differ.
        await Assert.That(agree / (double)cells).IsGreaterThanOrEqualTo(0.95);
    }

    /// <summary>
    ///     The shipped profile over every round of the four demos, printed the way §3.3 tabulates it,
    ///     and held to the property the spawn filter exists for: executes do not fire in most rounds
    ///     without a plant (12 of 14 on de_nuke without it; 0 to 3 of 2 to 14 measured with it).
    /// </summary>
    [Test]
    public async Task TheShippedProfile_OverEveryRound_FiresRarelyWithoutAPlant()
    {
        int demos = 0;
        foreach ((string map, string demo, _) in Pinned)
        {
            string path = Path.Combine(ReplaysFolder(), demo);
            if (!File.Exists(path))
            {
                continue;
            }

            demos++;
            Stopwatch watch = Stopwatch.StartNew();
            Walked walked = Walk(demo);
            SiteRegions regions = SiteRegions.Compose(map, null, walked.Table, DetectorProfile.Default);
            IReadOnlyList<TagProposal> proposals = ProposalDetection.Detect(map, walked.Parsed.TickRate, regions,
                walked.Events, walked.Build.Rounds, DetectorProfile.Default);
            watch.Stop();

            HashSet<int> planted = [.. walked.Build.Rounds.Where(r => r.Bomb is not null).Select(r => r.Round)];
            int scored = walked.Build.Rounds.Count(r => r.Slots(2).Count >= ProposalDetection.MinPlayersPerSide);
            List<TagProposal> executes = [.. proposals.Where(p => p.Detector == ExecuteDetector.DetectorId)];
            int noPlantRounds = scored - planted.Count;
            int noPlantFires = executes.Count(p => !planted.Contains(p.Round));
            Console.WriteLine($"{map}: {scored} rounds, {planted.Count} planted, regions {regions.Source}: " +
                              string.Join("; ", SiteRegions.Sites.Select(s => DetectorMath.RegionText(s, regions.RegionOf(s)))));
            Console.WriteLine($"  executes {executes.Count} (no-plant {noPlantFires} of {noPlantRounds}), " +
                              string.Join(", ", ProposalDetection.All.Skip(1).Select(d => $"{d.Id} {proposals.Count(p => p.Detector == d.Id)}")) +
                              $"; {watch.ElapsedMilliseconds} ms parse, walk and detect");
            foreach (TagProposal p in proposals)
            {
                Console.WriteLine($"    r{p.Round} {p.Id} c={p.Confidence} {string.Join(',', p.Labels.Select(l => $"{l.Key}={l.Value}"))}");
            }

            await Assert.That(noPlantFires).IsLessThanOrEqualTo(Math.Max(1, noPlantRounds / 2)).Because(map);
        }

        if (demos == 0)
        {
            throw new SkipTestException("none of the measured demos is in the replays folder");
        }
    }

    /// <summary>
    ///     The engine's rows replace the demo's own synthesised ones for the round pinned from de_nuke
    ///     (§10). The fold cannot equal the synthesised fixture byte for byte: the engine ends a round
    ///     at <c>round_decided</c>, 448 ticks (seven seconds) before the <c>round_officially_ended</c>
    ///     the synthesised rows close on, and it keeps the 23rd round they drop, so the learned cloud
    ///     places two detonations differently. The engine fold is pinned in its own fixture, and what
    ///     the two row sources must share (the round's start, sides and plant, and which proposals fire)
    ///     is checked against the synthesised one.
    /// </summary>
    [Test]
    public async Task OverTheEngineRows_ThePinnedRound_MatchesItsCommittedFold()
    {
        (string map, string demo, int number) = Pinned[0];
        string repo = DemoTestHelper.FindRepoRoot() ?? throw new SkipTestException("repo root not found");
        string synthesised = Path.Combine(SuggestedTagsGolden.FixtureDirectory(repo), $"{map}.json");
        string file = Path.Combine(SuggestedTagsGolden.EngineRowsFixtureDirectory(repo), $"{map}.json");

        Walked walked = WalkFromEngineRows(demo);
        RoundOccupancy round = walked.Build.Rounds.Single(r => r.Round == number);
        SuggestedTagsGolden golden = SuggestedTagsGolden.Capture(demo, map, walked.Parsed.TickRate, walked.Table, round,
            walked.Events);
        golden.Note = SuggestedTagsGolden.EngineRowsNote;
        golden.Proposals = golden.Detect();

        if (Environment.GetEnvironmentVariable("ST_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, golden.Serialize());
        }

        if (!File.Exists(file) || !File.Exists(synthesised))
        {
            throw new SkipTestException($"missing {file} or {synthesised}; capture them with ST_GOLDEN_UPDATE=1");
        }

        SuggestedTagsGolden fromSynthesised = SuggestedTagsGolden.Load(synthesised);
        using (Assert.Multiple())
        {
            await Assert.That(golden.Serialize()).IsEqualTo(File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal))
                .Because("the fold, the placed events and the learned table over the engine's rows are pinned");
            await Assert.That(golden.Round.StartTick).IsEqualTo(fromSynthesised.Round.StartTick);
            await Assert.That(golden.Round.EndTick).IsLessThan(fromSynthesised.Round.EndTick)
                .Because("the engine closes the round when it is decided, not when it is officially over");
            await Assert.That(golden.Round.Sides).IsEquivalentTo(fromSynthesised.Round.Sides)
                .Because("the sides are the same facts under either row source");
            await Assert.That(golden.Round.Bomb).IsEqualTo(fromSynthesised.Round.Bomb)
                .Because("the plant is the same fact under either row source");
            await Assert.That(golden.Proposals.Select(p => p.Id)).IsEquivalentTo(fromSynthesised.Proposals.Select(p => p.Id))
                .Because("the same proposals fire under either row source");
        }
    }

    /// <summary>
    ///     With the engine's rows behind the index evaluator too, the index source must feed the per-side
    ///     detectors the same executes, defaults and fakes as the walk does, on the de_inferno replay §10
    ///     pins. Retakes are left out: they turn on which CT is alive at the plant and when each first
    ///     enters the site, which the walk (first frame of every eighth) and the index (its due tick)
    ///     read at different frames inside a second, and they differ between the two sources over the
    ///     synthesised rows just the same (rounds 2, 3, 5, 10, 11 and 14 of this replay).
    /// </summary>
    [Test]
    public async Task OverTheEngineRows_TheIndexSource_FeedsTheSameProposalsAsTheWalk()
    {
        (string map, string demo, _) = Pinned[1];
        Walked walked = WalkFromEngineRows(demo);
        RoundIndexDocument index = RoundIndexBuilder.Build(walked.Parsed, walked.Rows, RoundIndexOptions.Default,
            PawnPlaceSource.Instance);
        IReadOnlyList<RoundOccupancy> fromIndex = RoundOccupancyBuilder.FromIndex(index, walked.Rows, walked.Parsed.TickRate);

        SiteRegions regions = SiteRegions.Compose(map, null, walked.Table, DetectorProfile.Default);
        IReadOnlyList<TagProposal> fromWalk = ProposalDetection.Detect(map, walked.Parsed.TickRate, regions, walked.Events,
            walked.Build.Rounds, DetectorProfile.Default);
        IReadOnlyList<TagProposal> fromIndexed = ProposalDetection.Detect(map, walked.Parsed.TickRate, regions, walked.Events,
            fromIndex, DetectorProfile.Default);

        HashSet<string> occupancyDetectors = [ExecuteDetector.DetectorId, DefaultDetector.DetectorId, FakeDetector.DetectorId];
        IEnumerable<string> Keys(IEnumerable<TagProposal> proposals) => proposals
            .Where(p => occupancyDetectors.Contains(p.Detector))
            .Select(p => $"{p.Round}:{p.Detector}:{string.Join(',', p.Labels.Select(l => $"{l.Key}={l.Value}"))}")
            .Order(StringComparer.Ordinal);

        await Assert.That(Keys(fromWalk)).IsNotEmpty();
        await Assert.That(Keys(fromIndexed)).IsEquivalentTo(Keys(fromWalk));
    }
}
