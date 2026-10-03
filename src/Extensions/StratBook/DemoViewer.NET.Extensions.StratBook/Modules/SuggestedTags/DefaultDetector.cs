#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     The default (suggested-tags.md §3.3): no execute by <c>defaultSecond</c>, and at
///     <c>spreadSecond</c> the T side stands in at least <c>spreadPlaces</c> distinct places that are not
///     spawn-adjacent. A weaker claim than an execute, so its confidence never passes 0.9. A round gets
///     an execute by <c>defaultSecond</c> or a default or neither, never both; an execute after it is
///     named in the default's <c>then</c> label and ends its window.
/// </summary>
public sealed class DefaultDetector : IProposalDetector
{
    /// <summary>The detector and code id.</summary>
    public const string DetectorId = "default";

    private const double Ceiling = 0.9;

    /// <inheritdoc />
    public string Id => DetectorId;

    /// <inheritdoc />
    public string Code => DetectorId;

    /// <inheritdoc />
    public IReadOnlyList<DetectorParameter> Parameters { get; } =
    [
        new("defaultSecond", 40, "no execute by this second; the round must also last past it"),
        new("spreadSecond", 30, "the second the T spread is read at"),
        new("spreadPlaces", 3, "distinct non-spawn places the T side must hold at spreadSecond"),
        new("lag", 10, "seconds claimed after defaultSecond when no execute follows")
    ];

    /// <inheritdoc />
    public IEnumerable<TagProposal> Detect(DetectorContext ctx, RoundOccupancy round, DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(profile);

        int defaultSecond = (int)profile.Get(Id, "defaultSecond");
        int spreadSecond = (int)profile.Get(Id, "spreadSecond");
        int spreadPlaces = (int)profile.Get(Id, "spreadPlaces");
        if (round.Seconds <= defaultSecond || spreadSecond >= round.Seconds)
        {
            yield break; // the round did not last long enough to be defaulting
        }

        TagProposal? execute = ctx.EarlierProposalsThisRound.FirstOrDefault(p => p.Detector == ExecuteDetector.DetectorId);
        if (execute is not null && round.SecondAt(execute.TriggerTick) <= defaultSecond)
        {
            yield break;
        }

        List<string> places =
        [
            .. round.CountsAt(2, spreadSecond).Keys
                .Where(p => !ctx.Regions.SpawnAdjacent.Contains(p))
                .Order(StringComparer.Ordinal)
        ];
        if (places.Count < spreadPlaces)
        {
            yield break;
        }

        int defaultTick = round.TickAt(defaultSecond);
        HashSet<string> sitesHit = new(StringComparer.Ordinal);
        List<PlacedEvent> utility = [];
        foreach (PlacedEvent e in ctx.Events)
        {
            if (e.Side != 2 || e.Place is not { } place || e.Tick >= defaultTick)
            {
                continue;
            }

            IReadOnlyList<string> sites = ctx.Regions.SitesContaining(place);
            if (sites.Count > 0)
            {
                sitesHit.UnionWith(sites);
                utility.Add(e);
            }
        }

        double fSpread = 0.1 * (places.Count - spreadPlaces);
        double fUtility = sitesHit.Count >= 2 ? 0.1 : 0;
        double confidence = Math.Min(Ceiling, 0.5 + fSpread + fUtility);

        // The default runs until the execute starts, which is its trigger: the execute's own band can
        // reach back to a lurker's first arrival, long before the round stopped defaulting.
        int to = execute is not null
            ? execute.TriggerTick
            : DetectorMath.Lag(round, defaultTick, profile.Get(Id, "lag"));
        Dictionary<string, string> labels = new(StringComparer.Ordinal)
        {
            ["places"] = string.Join(',', places)
        };
        if (execute is not null && execute.Labels.TryGetValue("site", out string? site))
        {
            labels["then"] = $"execute@{site}";
        }

        int spreadTick = round.TickAt(spreadSecond);
        List<ProposalEvidence> evidence =
        [
            new("occupancy", spreadTick,
                string.Create(CultureInfo.InvariantCulture, $"T in {places.Count} places: {string.Join(", ", places)}"))
        ];
        evidence.AddRange(utility.Select(DetectorMath.Evidence));
        evidence.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        yield return new TagProposal(
            ProposalIds.For("default", round.Round, 2, null, null),
            Id,
            Code,
            round.Round,
            2,
            round.StartTick,
            Math.Max(round.StartTick, to),
            spreadTick,
            DetectorMath.Confidence(confidence),
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["base"] = 0.5,
                ["spread"] = DetectorMath.Round(fSpread),
                ["utility"] = DetectorMath.Round(fUtility)
            },
            labels,
            evidence);
    }
}
