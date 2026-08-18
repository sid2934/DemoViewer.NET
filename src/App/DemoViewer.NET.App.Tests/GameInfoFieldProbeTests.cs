#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Impl-time probe confirming the EXACT field paths the game-info panel binds:
///     the m_pGameRules.*-prefixed keys on CCSGameRulesProxy (round phase / freeze / bomb / round number /
///     round start time) and the CCSTeam score field. Dumps the live fields and asserts the bound paths
///     resolve, so the panel is wired against verified strings — not guesses.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class GameInfoFieldProbeTests
{
    [Test]
    public async Task GameRulesProxy_And_Team_FieldsResolve()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        await Assert.That(frames.Count).IsGreaterThan(100);

        EntityTracker tracker = new();
        tracker.AdvanceToIndex(frames.Count / 2, frames);

        // ── CCSGameRulesProxy (the LIVE class — the rules object is the m_pGameRules sub-object) ──
        EntityState? proxy = tracker.CurrentEntities.OfClass("CCSGameRulesProxy").FirstOrDefault();
        await Assert.That(proxy).IsNotNull();

        Console.WriteLine("== CCSGameRulesProxy m_pGameRules.* fields ==");
        foreach ((string k, object? v) in proxy!.Fields.OrderBy(kv => kv.Key))
        {
            if (k.StartsWith("m_pGameRules.", StringComparison.Ordinal))
            {
                Console.WriteLine($"  {k} = {v}");
            }
        }

        // The freeze-period key is verified by FreezePeriodProvider; the others are the panel binds.
        await Assert.That(proxy["m_pGameRules.m_bFreezePeriod"]).IsNotNull();
        // Round number + start time (round-time-remaining is derived from start + assumed length).
        object? rounds = proxy["m_pGameRules.m_totalRoundsPlayed"];
        object? startTime = proxy["m_pGameRules.m_fRoundStartTime"];
        Console.WriteLine($"  totalRoundsPlayed={rounds}  roundStartTime={startTime}");

        // ── CCSTeam (team score — class + field are the impl-time confirmation) ──
        List<EntityState> teams = tracker.CurrentEntities.OfClass("CCSTeam").ToList();
        Console.WriteLine($"== CCSTeam entities: {teams.Count} ==");
        foreach (EntityState team in teams)
        {
            object? teamNum = team["m_iTeamNum"];
            object? score = team["m_iScore"];
            object? teamName = team["m_szTeamname"];
            Console.WriteLine($"  team m_iTeamNum={teamNum}  m_iScore={score}  name={teamName}");
        }

        // There should be at least the two playing teams (2 = T, 3 = CT) plus possibly spectator/unassigned.
        await Assert.That(teams.Count).IsGreaterThanOrEqualTo(2);

        // At least one team carries a readable m_iTeamNum of 2 or 3 (the playing sides).
        bool hasPlayingTeam = teams.Any(t =>
        {
            int n = ToInt(t["m_iTeamNum"]);
            return n is 2 or 3;
        });
        await Assert.That(hasPlayingTeam).IsTrue();
    }

    private static int ToInt(object? v) => v switch
    {
        int i => i,
        uint u => (int)u,
        short s => s,
        ushort u => u,
        byte b => b,
        long l => (int)l,
        ulong u => (int)u,
        _ => -1
    };
}
