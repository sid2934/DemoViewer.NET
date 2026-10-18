#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Modules.Library;

/// <summary>
///     The library's read when its visit runs no rules: CCSTeam and the controllers replayed to the last frame
///     through a tracker that stores only those two classes. A visit that runs rules reads
///     <see cref="LibraryTeamTables" /> instead.
/// </summary>
public static class LibraryFinalStateReplay
{
    /// <summary>The only classes the read looks at; the tracker stores nothing else.</summary>
    public static IReadOnlySet<string> Classes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "CCSTeam", "CCSPlayerController" };

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

        return LibraryTeamState.Of(ct, t, ctClan, tClan, coachSlots, matchEnded, overtime);
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
