#region

using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.Services.Players;

/// <summary>How far a read has got: demos read of those it has to read.</summary>
public readonly record struct PlayerEventsProgress(int Done, int Total);

/// <summary>
///     The kills of the demos the head-to-head has looked at, from their <c>player_event</c> facets, held in memory
///     so another opponent over the same demos reads no file. A demo's kills are kept under its content id and its
///     <c>facts:player_event</c> stamp, so a demo is read again only when its facet is written again. Not bounded:
///     a demo holds about a hundred and fifty kills, and only demos the subject played are ever read.
/// </summary>
public sealed class PlayerEventCache
{
    private static readonly string FacetStamp = StampedFacts.StampId(PlayerEventFacet.RulesetId);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<string, FactKey, FactTable?> _read;

    /// <param name="read">Reads one of a demo's tables by path and key, current or stale; null when none is written.</param>
    public PlayerEventCache(Func<string, FactKey, FactTable?> read)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
    }

    /// <summary>True when the demo's facet is written: its stamp is indexed.</summary>
    /// <param name="row">The demo's index row.</param>
    public static bool IsWritten(DemoCacheIndexEntry row) =>
        row?.Stamp(FacetStamp) is { State: DemoAnalysisState.Indexed };

    /// <summary>
    ///     The kills of each demo whose facet is written, by content id, reading off the calling thread every one
    ///     not held under its current stamp. A demo whose facet is not written, or will not read, is absent.
    /// </summary>
    /// <param name="rows">The demos' index rows.</param>
    /// <param name="progress">Told the count of demos to read, then each one read.</param>
    /// <param name="cancellationToken">Stops between reads; what was read is kept.</param>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<PlayerKill>>> LoadAsync(IReadOnlyList<DemoCacheIndexEntry> rows,
        IProgress<PlayerEventsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Task.Run<IReadOnlyDictionary<string, IReadOnlyList<PlayerKill>>>(() => Fill(rows, progress, cancellationToken), cancellationToken);
    }

    private Dictionary<string, IReadOnlyList<PlayerKill>> Fill(IReadOnlyList<DemoCacheIndexEntry> rows,
        IProgress<PlayerEventsProgress>? progress, CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<PlayerKill>> kills = new(rows.Count, StringComparer.Ordinal);
        List<(DemoCacheIndexEntry Row, string Id, PackStamp Stamp)> toRead = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (DemoCacheIndexEntry row in rows)
            {
                string id = row.ContentId();
                if (row.Stamp(FacetStamp) is not { State: DemoAnalysisState.Indexed } stamp || !seen.Add(id))
                {
                    continue;
                }

                if (_entries.TryGetValue(id, out Entry? held) && held.Stamp == stamp)
                {
                    kills[id] = held.Kills;
                }
                else
                {
                    toRead.Add((row, id, stamp));
                }
            }
        }

        progress?.Report(new PlayerEventsProgress(0, toRead.Count));
        for (int i = 0; i < toRead.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (DemoCacheIndexEntry row, string id, PackStamp stamp) = toRead[i];
            FactTable? killRows = _read(row.Path, PlayerEventFacet.KillsKey);
            FactTable? deathRows = _read(row.Path, PlayerEventFacet.DeathsKey);
            if (killRows is not null && deathRows is not null)
            {
                IReadOnlyList<PlayerKill> read = PlayerEventFacet.Kills(killRows.Rows, deathRows.Rows);
                kills[id] = read;
                lock (_gate)
                {
                    _entries[id] = new Entry(stamp, read);
                }
            }

            progress?.Report(new PlayerEventsProgress(i + 1, toRead.Count));
        }

        return kills;
    }

    private sealed record Entry(PackStamp Stamp, IReadOnlyList<PlayerKill> Kills);
}
