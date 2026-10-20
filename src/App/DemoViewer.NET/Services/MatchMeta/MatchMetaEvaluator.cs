#region

using System.Globalization;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.Services.MatchMeta;

/// <summary>
///     The half of <see cref="DemoMatchMeta" /> only a read of the demo can give: the game mode, where the demo
///     came from, and every player's rank before and after the match. A core pass on the demo's visit that asks
///     for no more than the library's final state, so it rides the visit's one forward read. Stamped
///     <see cref="StampId" />, apart from the <c>matchmeta</c> record pass, which keeps what this pass wrote.
/// </summary>
public sealed class MatchMetaEvaluator : IDemoEvaluator
{
    /// <summary>The pass id and the stamp it writes.</summary>
    public const string StampId = "matchrank";

    /// <summary>The stamp's fingerprint: how the capture reads. A change re-reads every demo.</summary>
    public const string Version = "mr1";

    private const int PremierRankType = 11;

    private readonly DemoCacheStore _store;

    /// <param name="store">The demo cache the pass writes into.</param>
    public MatchMetaEvaluator(DemoCacheStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public string Id => StampId;

    /// <inheritdoc />
    public bool ReadsUserCommands => false;

    /// <summary>A demo the library has parsed whose rank section is missing or was read by another version.</summary>
    public bool Wants(string path) =>
        _store.TryGetIndex(path) is { ParseSchema: > 0 } entry && entry.NeedsPack(StampId, DemoMatchMeta.CurrentSchema, Version);

    /// <summary>A demo the library has not parsed yet: the pass writes into the record that parse creates.</summary>
    public bool WantsAfterUpstream(string path) => _store.TryGetIndex(path) is not { ParseSchema: > 0 };

    /// <summary>The rank fields come off the final state's read; the mode and the update off the tap.</summary>
    public ForwardNeeds? ForwardFor(string path) => ForwardNeeds.FinalState;

    /// <inheritdoc />
    public long OrderHint(string path) => _store.TryGetIndex(path)?.ModifiedTicks ?? 0;

    /// <inheritdoc />
    public void EvaluateForward(string path, ForwardDemoResult pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        Write(path, () => Capture(pass.GameMode, pass.Demo.Profile.SourceKind, pass.Demo.ServerName, pass.ControllerRanks,
            pass.RankUpdates, () => WinningTeam(pass.Run is { } run && LibraryTeamTables.Carries(run)
                ? LibraryTeamTables.Read(run, pass.Demo.Players.Values, pass.Overtime)
                : pass.FinalState)));
    }

    /// <summary>
    ///     The same off a held parse, which reported no unknown message: the ranks after the match are the
    ///     controllers' predictions.
    /// </summary>
    public void Evaluate(string path, ParsedDemo parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        Write(path, () =>
        {
            string? mode = parsed.Frames.SelectMany(f => f.DecodedMessages)
                .Select(m => (m.Payload as CSVCMsg_ServerInfo)?.GameSessionConfig?.Gamemode)
                .FirstOrDefault(g => !string.IsNullOrEmpty(g));
            if (parsed.Frames.Count == 0)
            {
                return Capture(mode, parsed.Profile.SourceKind, parsed.ServerName, [], [], () => null);
            }

            EntityTracker tracker = LibraryFinalStateReplay.NewTracker();
            tracker.ReplayToIndex(parsed.Frames.Count - 1, parsed.Frames);
            bool ended = parsed.AllGameEvents.Aggregate(false, (e, evt) => LibraryFinalScore.FoldMatchEnded(e, evt.Name));
            return Capture(mode, parsed.Profile.SourceKind, parsed.ServerName, LibraryFinalStateReplay.ReadRanks(tracker), [],
                () => WinningTeam(LibraryFinalStateReplay.Read(tracker, parsed.Players.Keys, ended, LibraryOvertime.Of(parsed.Frames))));
        });
    }

    /// <summary>
    ///     What the pass writes for a demo. The game mode falls back to the players' rank system; FACEIT's
    ///     server names are read as FACEIT, which the engine's classifier never answers.
    /// </summary>
    /// <param name="gameMode">The server info's game mode, or null.</param>
    /// <param name="kind">The engine's source kind.</param>
    /// <param name="serverName">The server name.</param>
    /// <param name="controllers">The controllers' rank fields at the end of the demo.</param>
    /// <param name="updates">The server's rank update rows.</param>
    /// <param name="winningTeam">See <see cref="RankRows" />.</param>
    public static (string? GameMode, string MatchSource, List<MatchRankRow> Players) Capture(string? gameMode, DemoSourceKind kind,
        string? serverName, IReadOnlyList<ControllerRank> controllers, IReadOnlyList<ServerRankUpdate> updates, Func<int?> winningTeam)
    {
        List<MatchRankRow> rows = RankRows(controllers, updates, winningTeam);
        string? mode = gameMode is { Length: > 0 }
            ? gameMode
            : rows.GroupBy(r => r.RankType).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => ModeOf(g.Key)).FirstOrDefault();
        if (kind is DemoSourceKind.Unknown or DemoSourceKind.Custom
            && serverName is not null && serverName.Contains("faceit", StringComparison.OrdinalIgnoreCase))
        {
            kind = DemoSourceKind.Faceit;
        }

        return (mode, kind.ToString(), rows);
    }

    /// <summary>
    ///     One row per SteamID64, newest information first: the server's update where it sent one, else the
    ///     controller. Off the controller, a premier rank after the match is its prediction for the result the
    ///     player got; a competitive one is unknown, since its predictions are all 0. Rank 0 is unranked and
    ///     reads as null. A controller with no rank system is no row.
    /// </summary>
    /// <param name="controllers">The controllers' rank fields at the end of the demo.</param>
    /// <param name="updates">The server's rank update rows.</param>
    /// <param name="winningTeam">The team that won (2 T, 3 CT), 0 for a tie, null when unknown. Asked only when a premier controller has no update.</param>
    public static List<MatchRankRow> RankRows(IReadOnlyList<ControllerRank> controllers, IReadOnlyList<ServerRankUpdate> updates,
        Func<int?> winningTeam)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(winningTeam);
        Dictionary<ulong, MatchRankRow> rows = [];
        foreach (ServerRankUpdate update in updates)
        {
            int? old = Ranked(update.RankOld), now = Ranked(update.RankNew);
            rows[update.SteamId64] = new MatchRankRow
            {
                SteamId64 = update.SteamId64.ToString(CultureInfo.InvariantCulture),
                RankType = update.RankType,
                RankOld = old,
                RankNew = now,
                RankChange = now - old,
                Wins = update.Wins,
                RankSource = RankSource.ServerRankUpdate
            };
        }

        Lazy<int?> winner = new(winningTeam);
        foreach (ControllerRank controller in controllers)
        {
            if (rows.ContainsKey(controller.SteamId64) || controller is { RankType: 0, Ranking: 0 })
            {
                continue;
            }

            MatchRankRow row = new()
            {
                SteamId64 = controller.SteamId64.ToString(CultureInfo.InvariantCulture),
                RankType = controller.RankType,
                RankOld = Ranked(controller.Ranking),
                RankSource = RankSource.Controller
            };
            if (controller.RankType == PremierRankType && controller.Team is 2 or 3 && winner.Value is { } won)
            {
                bool tie = won == 0, win = won == controller.Team;
                row.RankNew = Ranked(tie ? controller.PredictedTie : win ? controller.PredictedWin : controller.PredictedLoss);
                row.RankChange = row.RankNew - row.RankOld;
                row.Wins = controller.Wins + (win ? 1 : 0);
                row.RankSource = RankSource.Predicted;
            }

            rows[controller.SteamId64] = row;
        }

        return [.. rows.OrderBy(r => r.Key).Select(r => r.Value)];
    }

    /// <summary>
    ///     The team a final score says won: a finished result (<see cref="LibraryFinalScore.IsResult" />) or a
    ///     tie at 12 or more. A surrender or a demo cut short is unknown.
    /// </summary>
    /// <param name="state">The demo's final state, or null.</param>
    public static int? WinningTeam(LibraryTeamState? state)
    {
        if (state is not { Ct: { } ct, T: { } t })
        {
            return null;
        }

        if (ct == t)
        {
            return ct >= 12 ? 0 : null;
        }

        return LibraryFinalScore.IsResult(Math.Max(ct, t), Math.Min(ct, t)) ? ct > t ? 3 : 2 : null;
    }

    private static int? Ranked(int rank) => rank > 0 ? rank : null;

    private static string? ModeOf(int rankType) => rankType switch
    {
        PremierRankType => "premier",
        12 => "competitive",
        7 => "wingman",
        _ => null
    };

    private void Write(string path, Func<(string? GameMode, string MatchSource, List<MatchRankRow> Players)> capture)
    {
        if (_store.TryGetIndex(path) is not { ParseSchema: > 0 })
        {
            return;
        }

        (string? GameMode, string MatchSource, List<MatchRankRow> Players) read;
        try
        {
            read = capture();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failed stays out of the backlog: a retry is the user's to ask for, never a read on every pass.
            _store.UpdateExisting(path, r => r.MarkFailed(StampId));
            _store.SaveIndex();
            throw;
        }

        long now = DateTime.UtcNow.Ticks;
        _store.UpdateExisting(path, r =>
        {
            DemoMatchMeta meta = r.MatchMeta ?? new DemoMatchMeta();
            meta.GameMode = read.GameMode;
            meta.MatchSource = read.MatchSource;
            meta.Players = read.Players.Count > 0 ? read.Players : null;
            r.MatchMeta = meta;
            r.SetStamp(new PackStamp(StampId, DemoMatchMeta.CurrentSchema, Version) { ComputedAtTicks = now });
        });
        _store.SaveIndex();
    }
}
