#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The tier-2 extension: the cheap pass now captures
///     the roster WITH teams, bot flags, steam ids and round boundaries, so a cached Match Overview can render
///     everything except the scoreboard without an analysis run.
///     <para>
///         Asserted against a REAL demo, because the invariant that matters is one synthetic data cannot
///         exercise: the cached player count must agree with the cached rosters. Counting every named entry
///         once reported 13 players above rosters of ten on tournament GOTV, which carries observers, coaches
///         and admins plus the GOTV proxy itself.
///     </para>
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class DemoCacheTier2Tests
{
    private static ParsedDemo Parse()
    {
        string path = DemoTestHelper.RequireDemo();
        return DemoParser.Parse(File.ReadAllBytes(path).AsMemory());
    }

    [Test]
    public async Task ProjectTier2_CapturesTheRosterWithTeamsAndBots()
    {
        ParsedDemo parsed = Parse();
        (List<CachedPlayerInfo> players, _) = DemoLibraryService.ProjectTier2(parsed);

        if (players.Count == 0)
        {
            throw new SkipTestException("demo carries no named players");
        }

        DemoCacheRecord record = new()
        {
            Players = players
        };

        using (Assert.Multiple())
        {
            await Assert.That(record.Roster.Any()).IsTrue()
                .Because("a real match has players on teams 2/3");
            await Assert.That(players.All(p => p.Name.Length > 0)).IsTrue();
            await Assert.That(players.All(p => p.Team is 2 or 3 or 0 or 1)).IsTrue();
            // The whole point of the extension: teams are known from cache alone.
            await Assert.That(record.Roster.Any(p => p.Team == 2)).IsTrue();
            await Assert.That(record.Roster.Any(p => p.Team == 3)).IsTrue();
        }
    }

    /// <summary>
    ///     The counting invariant, stated exactly as Match Overview states it: PLAYERS equals the two rosters,
    ///     and spectators are counted separately rather than folded into a number that claims to describe the
    ///     match. The cache must reproduce this, because the cached render has no ParsedDemo to recount from.
    /// </summary>
    [Test]
    public async Task CachedCounts_AgreeWithMatchOverviewsOwnCountingRule()
    {
        ParsedDemo parsed = Parse();
        (List<CachedPlayerInfo> players, _) = DemoLibraryService.ProjectTier2(parsed);
        DemoCacheRecord record = new()
        {
            Players = players
        };

        // Recompute the same numbers straight from the parse, the way SetSummary does.
        int liveT = 0, liveCt = 0, liveSpectators = 0;
        foreach (PlayerInfo p in parsed.Players.Values)
        {
            if (p.Name.Length == 0 || p.IsHltv)
            {
                continue;
            }

            if (p.Team == 2)
            {
                liveT++;
            }
            else if (p.Team == 3)
            {
                liveCt++;
            }
            else
            {
                liveSpectators++;
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(record.Roster.Count(p => p.Team == 2)).IsEqualTo(liveT);
            await Assert.That(record.Roster.Count(p => p.Team == 3)).IsEqualTo(liveCt);
            await Assert.That(record.Spectators.Count()).IsEqualTo(liveSpectators);
            await Assert.That(record.Roster.Count()).IsEqualTo(liveT + liveCt)
                .Because("PLAYERS must equal the two rosters — the invariant the card is built on");
            await Assert.That(players.Any(p => p.Name.Contains("GOTV", StringComparison.OrdinalIgnoreCase)
                                               && p.Team is not (2 or 3))).IsFalse()
                .Because("the GOTV proxy is infrastructure and is excluded at cache time, not at render time");
        }
    }

    /// <summary>
    ///     Round boundaries must come from <c>round_freeze_end</c>, NOT <c>round_start</c> — CS2 does not emit
    ///     the latter at all. Deriving them from the wrong event yields an empty list on every CS2 demo, which
    ///     silently disables the clip lead-in floor that <c>ClipWindows.RoundStartFor</c> exists to apply. That
    ///     is precisely the state the shipped highlights cache was in: every scanned row had zero rounds.
    ///     <para>
    ///         This test asserts BOTH halves — that rounds are found, and that the event the old code looked
    ///         for genuinely does not exist — so a future "simplification" back to the string match fails here
    ///         with the reason attached rather than quietly producing nothing.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RoundBoundaries_ComeFromFreezeEnd_BecauseCs2HasNoRoundStart()
    {
        ParsedDemo parsed = Parse();
        (_, List<CachedRound> rounds) = DemoLibraryService.ProjectTier2(parsed);

        await Assert.That(parsed.AllGameEvents.Any(e =>
                string.Equals(e.Name, "round_start", StringComparison.Ordinal))).IsFalse()
            .Because("CS2 emits no round_start — this is why the old string match produced nothing");

        await Assert.That(rounds).IsNotEmpty()
            .Because("a real match has rounds, derived from round_freeze_end");

        using (Assert.Multiple())
        {
            await Assert.That(rounds.Select(r => r.Number)).IsEquivalentTo(
                    Enumerable.Range(1, rounds.Count).ToList())
                .Because("rounds are numbered sequentially from 1");
            await Assert.That(rounds.Zip(rounds.Skip(1)).All(p => p.Second.StartTickFrameClock
                                                                  >= p.First.StartTickFrameClock)).IsTrue()
                .Because("round starts are monotonic in frame-clock ticks");
            // Frame clock, NOT server-tick space — never offset by ServerStartTick. A round start below the
            // server start tick would prove the wrong clock had been stored.
            await Assert.That(rounds[^1].StartTickFrameClock).IsLessThanOrEqualTo(parsed.TickCount);
        }
    }

    /// <summary>
    ///     A cached record must carry everything the facts strip and rosters need, so the page can render
    ///     without a parse. This is the actual acceptance criterion for "Match Overview is a cache render".
    /// </summary>
    [Test]
    public async Task ATier2Record_CarriesEverythingMatchOverviewNeedsExceptTheScoreboard()
    {
        ParsedDemo parsed = Parse();
        (List<CachedPlayerInfo> players, List<CachedRound> rounds) = DemoLibraryService.ProjectTier2(parsed);

        string root = Path.Combine(Path.GetTempPath(), $"dv-t2-{Guid.NewGuid():N}");
        try
        {
            DemoCacheStore store = new(root);
            store.Update("/demos/real.dem", 1234, 5678, r =>
            {
                r.Map = parsed.MapName;
                r.Server = parsed.ServerName;
                r.DurationSeconds = parsed.Duration.TotalSeconds;
                r.TickRate = parsed.TickRate;
                r.TickCount = parsed.TickCount;
                r.ServerStartTick = parsed.ServerStartTick;
                r.Players = players;
                r.Rounds = rounds;
                DemoCacheStore.StampParse(r);
            });
            store.SaveIndex();

            DemoCacheRecord? cached = new DemoCacheStore(root).TryLoadRecord("/demos/real.dem");

            using (Assert.Multiple())
            {
                await Assert.That(cached).IsNotNull();
                await Assert.That(cached!.Tier).IsEqualTo(DemoCacheTier.Parse);
                await Assert.That(cached.TickRate).IsGreaterThan(0).Because("TICK RATE tile");
                await Assert.That(cached.DurationSeconds).IsGreaterThan(0).Because("DURATION tile");
                await Assert.That(cached.Roster.Any()).IsTrue().Because("both roster cards");
                await Assert.That(cached.Map).IsNotNull().Because("the identity hero");
                // And the scoreboard is deliberately absent — that is tier 3, behind an explicit action.
                await Assert.That(cached.Scoreboard).IsEmpty();
                await Assert.That(cached.Analysis.IsPresent).IsFalse();
                await Assert.That(cached.AnalysisState).IsEqualTo(DemoAnalysisState.Pending);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
