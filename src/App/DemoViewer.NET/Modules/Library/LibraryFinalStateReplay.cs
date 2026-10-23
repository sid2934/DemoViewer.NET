#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>A controller's six <c>m_iCompetitive*</c> fields at the end of a demo. Every value is the pre-match one.</summary>
/// <param name="SteamId64">The controller's <c>m_steamID</c>.</param>
/// <param name="Team">The controller's team at the read: 2 T, 3 CT.</param>
/// <param name="Ranking">The rank; 0 for an unranked account.</param>
/// <param name="Wins">Wins before the match.</param>
/// <param name="RankType">The rank system: 11 premier, 12 competitive, 0 none.</param>
/// <param name="PredictedWin">The rank a win would give; 0 when the server predicts none.</param>
/// <param name="PredictedLoss">The rank a loss would give.</param>
/// <param name="PredictedTie">The rank a tie would give.</param>
public sealed record ControllerRank(
    ulong SteamId64,
    int Team,
    int Ranking,
    int Wins,
    int RankType,
    int PredictedWin,
    int PredictedLoss,
    int PredictedTie);

/// <summary>
///     The library's read when its visit runs no rules: CCSTeam, the controllers and the game rules replayed to
///     the last frame through a tracker that stores only those three classes. A visit that runs rules reads
///     <see cref="LibraryTeamTables" /> instead.
/// </summary>
public static class LibraryFinalStateReplay
{
    /// <summary>The only classes the read looks at; the tracker stores nothing else.</summary>
    public static IReadOnlySet<string> Classes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "CCSTeam", "CCSPlayerController", "CCSGameRulesProxy" };

    public static EntityTracker NewTracker() => new() { StoreClassFilter = Classes };

    /// <summary>Replays a held parse to its last frame and reads it. Decode site 1.</summary>
    public static LibraryTeamState Replay(ParsedDemo parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        IReadOnlyList<DemoFrame> frames = parsed.Frames;
        if (frames.Count == 0)
        {
            return LibraryTeamState.Empty();
        }

        DecodeSites.Enter(1);
        EntityTracker tracker = NewTracker();
        tracker.ReplayToIndex(frames.Count - 1, frames);
        bool matchEnded = parsed.AllGameEvents.Aggregate(false, (ended, e) => LibraryFinalScore.FoldMatchEnded(ended, e.Name));
        return Read(tracker, parsed.Players.Keys, matchEnded, LibraryOvertime.Of(parsed.Frames));
    }

    /// <summary>
    ///     Scores and clans from CCSTeam, coaches from the controller at entity index slot + 1, through
    ///     <see cref="LibraryTeamState.Of" />.
    /// </summary>
    /// <param name="tracker">A tracker at the demo's last frame.</param>
    /// <param name="playerSlots">The roster's slots.</param>
    /// <param name="matchEnded">Whether the current match ended (<see cref="LibraryFinalScore.FoldMatchEnded" />).</param>
    /// <param name="overtime">The demo's overtime convars.</param>
    public static LibraryTeamState Read(EntityTracker tracker, IEnumerable<int> playerSlots, bool matchEnded, LibraryOvertime overtime)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(playerSlots);
        int? ct = null, t = null;
        string? ctClan = null, tClan = null;
        foreach ((int _, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (ent.ClassName != "CCSTeam")
            {
                continue;
            }

            int teamNum = CoerceInt(ent["m_iTeamNum"]);
            int score = CoerceInt(ent["m_iScore"]);
            string clan = ent["m_szClanTeamname"] as string ?? "";
            if (teamNum == 2)
            {
                t = score;
                if (clan.Length > 0)
                {
                    tClan = clan;
                }
            }
            else if (teamNum == 3)
            {
                ct = score;
                if (clan.Length > 0)
                {
                    ctClan = clan;
                }
            }
        }

        // The match's last round keeps its win reason past the panel; a later round would have reset it to 0.
        int? winReason = tracker.CurrentEntities.OfClass("CCSGameRulesProxy").FirstOrDefault() is { } rules
            ? CoerceInt(rules["m_pGameRules.m_eRoundWinReason"])
            : null;

        HashSet<int> coachSlots = [];
        foreach (int slot in playerSlots)
        {
            EntityState? controller = tracker.CurrentEntities[slot + 1];
            if (controller is not null
                && controller.ClassName.Contains("PlayerController", StringComparison.OrdinalIgnoreCase)
                && CoerceInt(controller["m_iCoachingTeam"]) != 0)
            {
                coachSlots.Add(slot);
            }
        }

        return LibraryTeamState.Of(ct, t, ctClan, tClan, coachSlots, matchEnded, overtime, LibraryFinalScore.IsSurrender(winReason));
    }

    /// <summary>
    ///     A tracker at <paramref name="lastFullPacket" /> without replaying the demo to it: the signon frames, the
    ///     instance baselines, then the full packet's entity snapshot. For a read whose tap replayed nothing.
    /// </summary>
    /// <param name="signonPrefix">The reader's signon frames.</param>
    /// <param name="baseline">The full packet carrying the last instance baselines, or null.</param>
    /// <param name="lastFullPacket">The demo's last <c>DEM_FullPacket</c>.</param>
    public static EntityTracker Checkpoint(IReadOnlyList<DemoFrame> signonPrefix, DemoFrame? baseline, DemoFrame lastFullPacket)
    {
        ArgumentNullException.ThrowIfNull(signonPrefix);
        ArgumentNullException.ThrowIfNull(lastFullPacket);
        EntityTracker tracker = NewTracker();
        bool marked = false;
        foreach (DemoFrame frame in signonPrefix)
        {
            // Site 11 on the first fold only, so it stays one-to-one with the engine's counter: that one
            // fires on a tracker's first frame, and an empty prefix folds none.
            if (!marked)
            {
                marked = true;
                DecodeSites.Enter(11);
            }

            tracker.AdvanceOneFrame(frame);
        }

        if (baseline is not null)
        {
            tracker.LoadInstanceBaselineSnapshot(baseline);
        }

        tracker.ProcessFullPacketCheckpoint(lastFullPacket);
        return tracker;
    }

    /// <summary>Every human controller's rank fields: one row per non-zero <c>m_steamID</c>.</summary>
    /// <param name="tracker">A tracker that stored <c>CCSPlayerController</c>.</param>
    public static IReadOnlyList<ControllerRank> ReadRanks(EntityTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        List<ControllerRank> rows = [];
        foreach (EntityState controller in tracker.CurrentEntities.OfClass("CCSPlayerController"))
        {
            ulong steamId = controller["m_steamID"] switch
            {
                ulong u => u,
                long l => (ulong)l,
                _ => 0
            };
            if (steamId == 0)
            {
                continue;
            }

            rows.Add(new ControllerRank(steamId, CoerceInt(controller["m_iTeamNum"]),
                CoerceInt(controller["m_iCompetitiveRanking"]), CoerceInt(controller["m_iCompetitiveWins"]),
                CoerceInt(controller["m_iCompetitiveRankType"]), CoerceInt(controller["m_iCompetitiveRankingPredicted_Win"]),
                CoerceInt(controller["m_iCompetitiveRankingPredicted_Loss"]), CoerceInt(controller["m_iCompetitiveRankingPredicted_Tie"])));
        }

        return rows;
    }

    private static int CoerceInt(object? v) => v switch
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
