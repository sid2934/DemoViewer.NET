#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The library's score, clans and coaches come from the <c>library_teams</c> and <c>library_coaches</c>
///     tables of the merged rules run. On the benchmark demos they equal the two-class tracker replay to the
///     last frame they replaced, kept here as the oracle.
/// </summary>
[NotInParallel]
public class LibraryTeamTablesTests
{
    private static RuleConfigLoadResult ShippedRules() =>
        YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null);

    [Test]
    public async Task ARunWithoutTheTables_ReadsAsEmpty()
    {
        LibraryTeamState none = LibraryTeamTables.Read(null, []);

        using (Assert.Multiple())
        {
            await Assert.That(none.Ct).IsNull();
            await Assert.That(none.T).IsNull();
            await Assert.That(none.CtClan).IsNull();
            await Assert.That(none.TClan).IsNull();
            await Assert.That(none.CoachSlots).IsEmpty();
        }
    }

    [Test]
    public async Task EveryMergedRun_RecordsTheLibraryTables_WhateverTheCut()
    {
        MergedRulesBuild rules = new(ShippedRules) { OutputsFor = _ => new HashSet<string>(StringComparer.Ordinal) };
        AnalysisRun run = rules.BareRun(MergedRulesBuildTests.TwoFrameDemo(), "/demos/a.dem");

        await Assert.That((run.Build.Outputs ?? []).Select(o => o.Id).Where(LibraryTeamTables.Tables.Contains).Order(StringComparer.Ordinal))
            .IsEquivalentTo(LibraryTeamTables.Tables.Order(StringComparer.Ordinal));
        // Two frames, no team entity and no closed round: no score and no coach, as the replay read it.
        await Assert.That(Show(LibraryTeamTables.Read(run, []))).IsEqualTo(Show(LibraryTeamState.Empty()));
    }

    [Test]
    public async Task TheLibraryRulesets_StayOutOfTheHighlightSetAndTheFacets()
    {
        MergedRulesBuild rules = new(ShippedRules, () => [.. LibraryTeamTables.RulesetIds.Select(StampedRuleset.Core)]);

        using (Assert.Multiple())
        {
            await Assert.That(rules.CoreDocs.Select(d => d.Id)).DoesNotContain(LibraryTeamTables.TeamsRulesetId);
            await Assert.That(rules.CoreDocs.Select(d => d.Id)).DoesNotContain(LibraryTeamTables.CoachesRulesetId);
            await Assert.That(rules.EnabledDoc(LibraryTeamTables.TeamsRulesetId)).IsNotNull();
            await Assert.That(rules.EnabledDoc(LibraryTeamTables.CoachesRulesetId)).IsNotNull();
            await Assert.That(new StampedFacts(rules).Facets()).IsEmpty();
        }
    }

    [Test]
    public async Task TheLibrary_ReplaysWhenItsVisitRunsNoRules_AndReadsTheRunWhenOneDoes()
    {
        MergedRulesBuild rules = new(ShippedRules);
        int thread = Environment.CurrentManagedThreadId;
        int replays = 0;
        DecodeSites.Entered = site => replays += site == 1 && Environment.CurrentManagedThreadId == thread ? 1 : 0;
        string json = Path.Combine(Path.GetTempPath(), $"dv-teams-{Guid.NewGuid():N}.json");
        try
        {
            AnalysisRun? served = null;
            using DemoLibraryService library = new(a => a(), json) { TeamRun = (_, _) => served };
            library.IndexTier2Core(Entry(), MergedRulesBuildTests.TwoFrameDemo());
            int withoutRun = replays;
            ParsedDemo parsed = MergedRulesBuildTests.TwoFrameDemo();
            served = rules.BareRun(parsed, "/demos/a.dem");
            library.IndexTier2Core(Entry(), parsed);

            using (Assert.Multiple())
            {
                await Assert.That(withoutRun).IsEqualTo(1).Because("no run on the visit: the two-class replay");
                await Assert.That(replays).IsEqualTo(1).Because("a run on the visit carries the tables");
            }
        }
        finally
        {
            DecodeSites.Entered = null;
            File.Delete(json);
        }
    }

    [Test]
    public async Task ARulesRead_CarriesTheFinalState_ForALibraryJoiningIt()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DemoVisit.CanJoinRunning(PassNeeds.ForwardRead(ForwardNeeds.FinalState), true, ForwardNeeds.Rules, false)).IsTrue();
            await Assert.That(DemoVisit.CanJoinRunning(PassNeeds.ForwardRead(ForwardNeeds.Rules), true, ForwardNeeds.FinalState, false)).IsFalse();
        }
    }

    [Test]
    public async Task AVisitInFlight_AnswersItsForwardNeeds_UntilItEnds()
    {
        ActiveCollectors active = new();
        IDisposable visit = active.Begin("/demos/a.dem", CollectorSet.Empty, ForwardNeeds.FinalState | ForwardNeeds.Rules);
        ForwardNeeds during = active.NeedsFor("/DEMOS/A.DEM");
        visit.Dispose();

        using (Assert.Multiple())
        {
            await Assert.That(during).IsEqualTo(ForwardNeeds.FinalState | ForwardNeeds.Rules);
            await Assert.That(active.NeedsFor("/demos/a.dem")).IsEqualTo(ForwardNeeds.None);
        }
    }

    /// <summary>
    ///     A forward read with the final state and no rules replays it in the tap; with rules it reads none, and
    ///     the run's tables say the same.
    /// </summary>
    [Test]
    [Category("RealDemo")]
    public async Task AForwardReadWithoutRules_ReplaysTheFinalState_TheTablesAgree()
    {
        string path = BenchmarkDemo(0);
        MergedRulesBuild rules = new(ShippedRules);
        ForwardDemoResult alone;
        ForwardDemoResult withRules;
        using (DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None)))
        {
            alone = ForwardDemoPass.Run(reader, ForwardNeeds.FinalState, null);
        }

        using (DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None)))
        {
            withRules = ForwardDemoPass.Run(reader, ForwardNeeds.FinalState | ForwardNeeds.Rules, rules.Docs);
        }

        using (Assert.Multiple())
        {
            await Assert.That(alone.Run).IsNull();
            await Assert.That(alone.FinalState).IsNotNull();
            await Assert.That(withRules.FinalState).IsNull().Because("a rules run carries the final state as tables");
            await Assert.That(Show(LibraryTeamTables.Read(withRules.Run, withRules.Demo.Players.Values))).IsEqualTo(Show(alone.FinalState!));
            await Assert.That(alone.FinalState!.Ct).IsNotNull();
        }
    }

    private static DemoEntry Entry() => new()
    {
        FilePath = "/demos/a.dem",
        FileName = "a.dem",
        Directory = "/demos",
        FileSizeBytes = 1,
        Modified = new DateTime(2026, 1, 1)
    };

    [Test]
    [Category("RealDemo")]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task TheTables_EqualTheLastFrameReplay(int which)
    {
        string path = BenchmarkDemo(which);
        ParsedDemo parsed = BackgroundPlanRealDemoTests.ParseMapped(path, DemoProcessingQueue.WithoutUserCommands);
        LibraryTeamState replay = Replay(parsed);
        MergedRulesBuild rules = new(ShippedRules);
        LibraryTeamState merged = LibraryTeamTables.Read(rules.BareRun(parsed, path), parsed.Players.Values);
        LibraryTeamState alone = LibraryTeamTables.Evaluate(parsed, ShippedRules().Rulesets);

        Console.WriteLine($"{Path.GetFileName(path)}: replay {Show(replay)}, merged {Show(merged)}");
        using (Assert.Multiple())
        {
            await Assert.That(Show(merged)).IsEqualTo(Show(replay));
            await Assert.That(Show(alone)).IsEqualTo(Show(replay));
            await Assert.That(replay.Ct).IsNotNull().Because("a benchmark demo is a whole match");
        }
    }

    /// <summary>
    ///     A tournament demo, where the clan names are set. The clans agree with the replay. The score may not:
    ///     on vitality-vs-fut-m2-dust2 the run reads FUT's deciding point (11:13) at the end, while the
    ///     tracker replay stays on 12 to the last frame, 35k ticks after its last change. Anything else moving
    ///     fails.
    /// </summary>
    [Test]
    [Category("RealDemo")]
    public async Task TheTables_EqualTheLastFrameReplay_OnATournamentDemo_ButForTheDecidingPoint()
    {
        string path = DemoTestHelper.FindDemoPath("vitality-vs-fut-m2-dust2.dem")
                      ?? DemoTestHelper.FindDemoPath("vitality-vs-fut-m3-nuke.dem")
                      ?? throw new SkipTestException("no tournament demo present");
        ParsedDemo parsed = BackgroundPlanRealDemoTests.ParseMapped(path, DemoProcessingQueue.WithoutUserCommands);
        LibraryTeamState replay = Replay(parsed);
        LibraryTeamState merged = LibraryTeamTables.Read(new MergedRulesBuild(ShippedRules).BareRun(parsed, path), parsed.Players.Values);

        Console.WriteLine($"{Path.GetFileName(path)}: replay {Show(replay)}, merged {Show(merged)}");
        bool sameScore = merged.Ct == replay.Ct && merged.T == replay.T;
        bool decidingPoint = (merged.Ct == replay.Ct && merged.T == replay.T + 1 && merged.T == 13)
                             || (merged.T == replay.T && merged.Ct == replay.Ct + 1 && merged.Ct == 13);
        using (Assert.Multiple())
        {
            await Assert.That(sameScore || decidingPoint).IsTrue().Because($"replay {Show(replay)}, merged {Show(merged)}");
            await Assert.That((merged.CtClan, merged.TClan)).IsEqualTo((replay.CtClan, replay.TClan));
            await Assert.That(merged.CoachSlots.Order().ToList()).IsEquivalentTo(replay.CoachSlots.Order().ToList());
            await Assert.That(replay.CtClan).IsNotNull();
            await Assert.That(replay.TClan).IsNotNull();
        }
    }

    private static string Show(LibraryTeamState s) =>
        $"{s.Ct}:{s.T}:{s.CtClan}:{s.TClan}:[{string.Join(",", s.CoachSlots.Order())}]";

    private static string BenchmarkDemo(int which)
    {
        string? root = DemoTestHelper.FindRepoRoot();
        string folder = root is null ? "" : Path.Combine(root, "demos", "benchmarks");
        List<string> demos = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.dem", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
        return which < demos.Count ? demos[which] : throw new SkipTestException($"fewer than {which + 1} demos under demos/benchmarks");
    }

    // The read the tables replaced: CCSTeam per side and the controller at entity index slot + 1, replayed to
    // the last frame through a tracker that stores only those two classes.
    private static LibraryTeamState Replay(ParsedDemo parsed)
    {
        if (parsed.Frames.Count == 0)
        {
            return LibraryTeamState.Empty();
        }

        EntityTracker tracker = new() { StoreClassFilter = new HashSet<string>(StringComparer.Ordinal) { "CCSTeam", "CCSPlayerController" } };
        tracker.ReplayToIndex(parsed.Frames.Count - 1, parsed.Frames);
        int? ct = null, t = null;
        string? ctClan = null, tClan = null;
        foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (ent.ClassName != "CCSTeam")
            {
                continue;
            }

            int score = ToInt(ent["m_iScore"]);
            string clan = ent["m_szClanTeamname"] as string ?? "";
            switch (ToInt(ent["m_iTeamNum"]))
            {
                case 2:
                    t = score;
                    tClan = clan.Length > 0 ? clan : tClan;
                    break;
                case 3:
                    ct = score;
                    ctClan = clan.Length > 0 ? clan : ctClan;
                    break;
            }
        }

        HashSet<int> coaches = [];
        foreach (int slot in parsed.Players.Keys)
        {
            if (tracker.CurrentEntities[slot + 1] is { } controller
                && controller.ClassName.Contains("PlayerController", StringComparison.OrdinalIgnoreCase)
                && ToInt(controller["m_iCoachingTeam"]) != 0)
            {
                coaches.Add(slot);
            }
        }

        return ct is null || t is null || ct + t == 0
            ? new LibraryTeamState(null, null, null, null, coaches)
            : new LibraryTeamState(ct, t, ctClan, tClan, coaches);
    }

    private static int ToInt(object? v) => v switch
    {
        int i => i,
        uint u => (int)u,
        short s => s,
        ushort u => u,
        long l => (int)l,
        ulong u => (int)u,
        byte b => b,
        sbyte s => s,
        _ => 0
    };
}
