#region

using System.Globalization;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>An account that ended demos on the subject's side, and how those demos went.</summary>
/// <param name="SteamId64">The teammate.</param>
/// <param name="Name">The RAW name of the newest demo they shared: sanitise at the render boundary.</param>
/// <param name="Matches">The demos the two ended on the same side.</param>
/// <param name="Wins">Of those, the demos won.</param>
/// <param name="Losses">Of those, the demos lost.</param>
/// <param name="Ties">Of those, the demos tied.</param>
/// <param name="Hltv">The teammate's HLTV pooled over the shared demos with stats; null when none has.</param>
/// <param name="Rated">The shared demos <paramref name="Hltv" /> pools.</param>
public sealed record PlayerTeammate(ulong SteamId64, string Name, int Matches, int Wins, int Losses, int Ties, double? Hltv, int Rated)
{
    /// <summary>Shared demos with an outcome: the win rate's denominator.</summary>
    public int WithOutcome => Wins + Losses + Ties;

    /// <summary>Wins of <see cref="WithOutcome" />, in percent; null without an outcome.</summary>
    public double? WinRate => WithOutcome == 0 ? null : 100.0 * Wins / WithOutcome;
}

/// <summary>
///     Everyone who ended a demo on the subject's side. Who shared a side comes from the index's side lists; the
///     outcome is the subject's (the two won or lost together) and each teammate's rating comes from the other
///     accounts the subject's <c>player_match</c> rows carry (<see cref="PlayerDemo.Others" />), pooled by the
///     HLTV column's rule. A demo either left early is not in it. Pure: it reads only what it is handed.
/// </summary>
public static class Teammates
{
    /// <summary>The subject's teammates over <paramref name="demos" />, the most shared first, then by name.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="demos">The subject's demos, newest first.</param>
    public static IReadOnlyList<PlayerTeammate> Of(ulong subject, IReadOnlyList<PlayerDemo> demos)
    {
        ArgumentNullException.ThrowIfNull(demos);
        ColumnRule rating = ColumnRules.For("HLTV")!;
        Dictionary<ulong, Shared> mates = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (PlayerDemo demo in demos)
        {
            PlayerMatch match = demo.Match;
            if (match.Side is not { } side || !seen.Add(match.Row.ContentId()))
            {
                continue;
            }

            MatchOutcome? outcome = demo.Outcome;
            HashSet<ulong> counted = [];
            foreach (IndexSidePlayer player in (side == 3 ? match.Row.CtPlayers : match.Row.TPlayers) ?? [])
            {
                if (!ulong.TryParse(player.SteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0 || id == subject
                    || !counted.Add(id))
                {
                    continue;
                }

                if (!mates.TryGetValue(id, out Shared? shared))
                {
                    mates[id] = shared = new Shared(player.Name);
                }

                shared.Matches++;
                shared.Wins += outcome == MatchOutcome.Win ? 1 : 0;
                shared.Losses += outcome == MatchOutcome.Loss ? 1 : 0;
                shared.Ties += outcome == MatchOutcome.Tie ? 1 : 0;
                if (demo.Others?.GetValueOrDefault(id) is { } row)
                {
                    shared.Rows.Add(row);
                }
            }
        }

        return
        [
            .. mates.Select(m => new PlayerTeammate(m.Key, m.Value.Name, m.Value.Matches, m.Value.Wins, m.Value.Losses, m.Value.Ties,
                    m.Value.Rows.Count == 0 ? null : rating.Pool(m.Value.Rows), m.Value.Rows.Count))
                .OrderByDescending(t => t.Matches)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.SteamId64)
        ];
    }

    private sealed class Shared(string name)
    {
        public string Name { get; } = name;
        public List<IReadOnlyDictionary<string, double>> Rows { get; } = [];
        public int Matches { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Ties { get; set; }
    }
}
