#region

using System.Text.Json.Serialization;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Provenance;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The filter rail's values as one plain object: what the rail's fields hold, with no options, no
///     selection and nothing observable. The rail projects into it on every read and a watched
///     situation stores it, so the two resolve a filter through one pair of functions.
///     <para>
///         <b>Fields, not demo sets.</b> The opponent is a team id, the source a label and the dates a
///         range, never the stable keys they narrow to today: a watched situation re-derives the demo
///         set on every evaluation, which is how a demo against the opponent indexed tomorrow counts as
///         new. Correction 3's rule holds by construction: nothing here names a demo.
///     </para>
///     <para>
///         Every value is absolute per side, the Round Facts rule; <see cref="Side" /> is the one
///         relative field and joins through <see cref="TeamIdentityService.SideAtRound" /> on the us
///         team, so it applies only while one is set.
///     </para>
/// </summary>
public sealed record SearchFilterValues
{
    /// <summary>Our side in the round, 3 for CT or 2 for T; null for any.</summary>
    public int? Side { get; init; }

    public BuyType? BuyCt { get; init; }

    public BuyType? BuyT { get; init; }

    /// <summary>The phase at the matched tick.</summary>
    public RoundPhase? Phase { get; init; }

    /// <summary>Seconds since the freeze end at the matched tick, in the three bands.</summary>
    public ClockBand? Clock { get; init; }

    /// <summary>The relative alive count at the matched tick.</summary>
    public ManCountState? ManCount { get; init; }

    /// <summary>The score before the round.</summary>
    public ScoreSituation? Score { get; init; }

    /// <summary>A team the library knows, other than us: demos where our side resolved and they were the other side.</summary>
    public Guid? Opponent { get; init; }

    /// <summary>The provenance label in force, or <see cref="SearchFilterOptions.Unlabeled" />.</summary>
    public string? Source { get; init; }

    /// <summary>The earliest demo date kept, inclusive; null for no lower bound.</summary>
    public DateTime? From { get; init; }

    /// <summary>The latest demo date kept, inclusive; null for no upper bound.</summary>
    public DateTime? To { get; init; }

    /// <summary>Nothing set.</summary>
    public static SearchFilterValues None { get; } = new();

    /// <summary>Any field narrows something.</summary>
    [JsonIgnore]
    public bool IsActive => HasFactFilter || HasDemoFilter;

    /// <summary>A field that reads the Round Facts rows is set.</summary>
    [JsonIgnore]
    public bool HasFactFilter =>
        Side is not null || BuyCt is not null || BuyT is not null || Phase is not null || Clock is not null
        || ManCount is not null || Score is not null;

    /// <summary>A field that narrows the demo set is set.</summary>
    [JsonIgnore]
    public bool HasDemoFilter => Opponent is not null || Source is not null || From is not null || To is not null;

    /// <summary>
    ///     The Round Facts filter the fact fields amount to, or null when none is set. Our side becomes
    ///     the filter's <see cref="RoundFactsFilter.Where" />, joined per round through the us team; the
    ///     rest map one to one.
    /// </summary>
    /// <param name="teams">Team Identity, for the us team; null (or no us team) leaves the side field inert.</param>
    public RoundFactsFilter? ToFacts(TeamIdentityService? teams)
    {
        if (!HasFactFilter)
        {
            return null;
        }

        Func<string, RoundFacts, bool>? where = null;
        if (Side is int side && teams?.Us is { } us)
        {
            Guid usId = us.Id;
            where = (path, round) => teams.SideAtRound(path, usId, round.Number) == side;
        }

        return new RoundFactsFilter
        {
            BuyCt = BuyCt,
            BuyT = BuyT,
            Phase = Phase,
            ClockBand = Clock,
            ManCount = ManCount,
            Score = Score,
            Where = where
        };
    }

    /// <summary>
    ///     The demos the opponent, date and source fields keep, as stable keys, or null when none of the
    ///     three is set. The three intersect: a demo must pass every set field.
    /// </summary>
    /// <param name="demoCache">The index rows the fields read.</param>
    /// <param name="teams">Team Identity, for the opponent's demos; null keeps every demo on that field.</param>
    /// <param name="provenance">Demo Provenance Labels, for the source field; null keeps every demo on that field.</param>
    public IReadOnlySet<string>? ToDemos(DemoCacheStore demoCache, TeamIdentityService? teams, IDemoProvenanceSource? provenance)
    {
        ArgumentNullException.ThrowIfNull(demoCache);
        if (!HasDemoFilter)
        {
            return null;
        }

        IReadOnlyList<DemoCacheIndexEntry> rows = demoCache.Index;
        HashSet<string> keep = new(StringComparer.Ordinal);

        HashSet<string>? against = null;
        if (Opponent is Guid opponent && teams is not null)
        {
            against = new HashSet<string>(teams.DemosAgainst(opponent).Select(d => d.StableKey), StringComparer.Ordinal);
        }

        IReadOnlyDictionary<string, DemoProvenance>? labels = null;
        if (Source is not null && provenance is not null)
        {
            labels = provenance.ResolveAll(rows.Select(r => r.Path));
        }

        DateTime? from = From?.Date;
        DateTime? to = To?.Date.AddDays(1);
        foreach (DemoCacheIndexEntry row in rows)
        {
            string key = DemoCacheStore.StableKey(row.Path);
            if (against is not null && !against.Contains(key))
            {
                continue;
            }

            if (Source is { } source)
            {
                string? label = labels is not null && labels.TryGetValue(row.Path, out DemoProvenance? resolved) ? resolved.Label : null;
                bool matches = source == SearchFilterOptions.Unlabeled ? label is null : string.Equals(label, source, StringComparison.Ordinal);
                if (!matches)
                {
                    continue;
                }
            }

            // The cache row's modified time stands in for the match date, the Library's own rule until
            // a real one exists; the bounds are whole days in the same local clock the file time is in.
            if (from is not null || to is not null)
            {
                DateTime modified = new(row.ModifiedTicks);
                if ((from is { } lower && modified < lower) || (to is { } upper && modified >= upper))
                {
                    continue;
                }
            }

            keep.Add(key);
        }

        return keep;
    }
}

/// <summary>The rail's fixed vocabulary that both the rail and a stored filter spell.</summary>
public static class SearchFilterOptions
{
    /// <summary>The source option for demos that carry no label: the absence of one, which the vocabulary has no word for.</summary>
    public const string Unlabeled = "unlabeled";
}
