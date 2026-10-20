#region

using System.Globalization;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>An account the subject played against, and how often.</summary>
/// <param name="SteamId64">The opponent.</param>
/// <param name="Name">The RAW name of the newest demo they met in: sanitise at the render boundary.</param>
/// <param name="Matches">The demos the two ended on opposite sides.</param>
public sealed record PlayerOpponent(ulong SteamId64, string Name, int Matches);

/// <summary>One demo the subject and the opponent ended on opposite sides, and what happened between them.</summary>
/// <param name="Match">The subject's entry for the demo.</param>
/// <param name="Outcome">How the demo ended for the subject; null without the subject's player stats.</param>
/// <param name="HasKills">Whether the demo's <c>player_event</c> facet is read; false leaves the three counts 0.</param>
/// <param name="Kills">Times the subject killed the opponent.</param>
/// <param name="Deaths">Times the opponent killed the subject.</param>
/// <param name="HeadshotKills">Of <paramref name="Kills" />, the headshots.</param>
public sealed record HeadToHeadMatch(PlayerMatch Match, MatchOutcome? Outcome, bool HasKills, int Kills, int Deaths, int HeadshotKills);

/// <summary>
///     The subject against one opponent over the demos they ended on opposite sides: the match record from the
///     subject's <c>player_match</c> rows and the kills between them from each demo's <c>player_event</c> facet.
///     A demo meets on the side lists, so a demo either left early is not in it. Pure: it reads only what it is
///     handed.
/// </summary>
/// <param name="Matches">Every demo the two met in, newest first.</param>
public sealed record HeadToHead(IReadOnlyList<HeadToHeadMatch> Matches)
{
    /// <summary>Demos the subject won.</summary>
    public int Wins => Matches.Count(m => m.Outcome == MatchOutcome.Win);

    /// <summary>Demos the subject lost.</summary>
    public int Losses => Matches.Count(m => m.Outcome == MatchOutcome.Loss);

    /// <summary>Demos tied.</summary>
    public int Ties => Matches.Count(m => m.Outcome == MatchOutcome.Tie);

    /// <summary>Demos with an outcome: the record's denominator.</summary>
    public int WithOutcome => Matches.Count(m => m.Outcome is not null);

    /// <summary>Demos whose kills are read: the denominator of the three counts.</summary>
    public int WithKills => Matches.Count(m => m.HasKills);

    /// <summary>Times the subject killed the opponent, over <see cref="WithKills" />.</summary>
    public int Kills => Matches.Sum(m => m.Kills);

    /// <summary>Times the opponent killed the subject, over <see cref="WithKills" />.</summary>
    public int Deaths => Matches.Sum(m => m.Deaths);

    /// <summary>Of <see cref="Kills" />, the headshots.</summary>
    public int HeadshotKills => Matches.Sum(m => m.HeadshotKills);

    /// <summary>
    ///     Everyone who ended a demo of the subject's on the other side, the most often met first, then by name.
    ///     The subject's own accounts on the other side (a smurf in a scrim) count like anyone else.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="matches">The subject's demos, newest first.</param>
    public static IReadOnlyList<PlayerOpponent> Opponents(ulong subject, IReadOnlyList<PlayerMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        Dictionary<ulong, (string Name, int Matches)> met = [];
        HashSet<string> demos = new(StringComparer.Ordinal);
        foreach (PlayerMatch match in matches)
        {
            if (match.Side is not { } side || !demos.Add(PlayerEventCache.ContentIdOf(match.Row)))
            {
                continue;
            }

            foreach (IndexSidePlayer player in (side == 3 ? match.Row.TPlayers : match.Row.CtPlayers) ?? [])
            {
                if (!ulong.TryParse(player.SteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0 || id == subject)
                {
                    continue;
                }

                met[id] = met.TryGetValue(id, out (string Name, int Matches) seen) ? (seen.Name, seen.Matches + 1) : (player.Name, 1);
            }
        }

        return
        [
            .. met.Select(m => new PlayerOpponent(m.Key, m.Value.Name, m.Value.Matches))
                .OrderByDescending(o => o.Matches)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.SteamId64)
        ];
    }

    /// <summary>The subject's demos the opponent ended on the other side of, newest first, each once.</summary>
    /// <param name="subject">The subject's demos.</param>
    /// <param name="opponent">The opponent's demos.</param>
    public static IReadOnlyList<PlayerMatch> Against(IReadOnlyList<PlayerMatch> subject, IReadOnlyList<PlayerMatch> opponent)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(opponent);
        Dictionary<string, int> sides = new(opponent.Count, StringComparer.Ordinal);
        foreach (PlayerMatch match in opponent)
        {
            if (match.Side is { } side)
            {
                sides.TryAdd(PlayerEventCache.ContentIdOf(match.Row), side);
            }
        }

        HashSet<string> taken = new(StringComparer.Ordinal);
        return
        [
            .. subject.Where(m => m.Side is { } side
                                  && sides.TryGetValue(PlayerEventCache.ContentIdOf(m.Row), out int theirs) && theirs != side
                                  && taken.Add(PlayerEventCache.ContentIdOf(m.Row)))
        ];
    }

    /// <summary>The record and the kills over <paramref name="against" />.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="opponent">The opponent.</param>
    /// <param name="against">The demos they met in (<see cref="Against" />).</param>
    /// <param name="outcome">How a demo ended for the subject; null when its stats are not read.</param>
    /// <param name="kills">Each demo's kills by content id (<see cref="PlayerEventCache.LoadAsync" />); a demo absent is unread.</param>
    public static HeadToHead Of(ulong subject, ulong opponent, IReadOnlyList<PlayerMatch> against, Func<PlayerMatch, MatchOutcome?> outcome,
        IReadOnlyDictionary<string, IReadOnlyList<PlayerKill>> kills)
    {
        ArgumentNullException.ThrowIfNull(against);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(kills);
        string me = subject.ToString(CultureInfo.InvariantCulture);
        string them = opponent.ToString(CultureInfo.InvariantCulture);
        List<HeadToHeadMatch> matches = new(against.Count);
        foreach (PlayerMatch match in against)
        {
            if (!kills.TryGetValue(PlayerEventCache.ContentIdOf(match.Row), out IReadOnlyList<PlayerKill>? demo))
            {
                matches.Add(new HeadToHeadMatch(match, outcome(match), false, 0, 0, 0));
                continue;
            }

            int k = 0, d = 0, hs = 0;
            foreach (PlayerKill kill in demo)
            {
                if (kill.Killer == me && kill.Victim == them)
                {
                    k++;
                    hs += kill.Headshot ? 1 : 0;
                }
                else if (kill.Killer == them && kill.Victim == me)
                {
                    d++;
                }
            }

            matches.Add(new HeadToHeadMatch(match, outcome(match), true, k, d, hs));
        }

        return new HeadToHead(matches);
    }
}
