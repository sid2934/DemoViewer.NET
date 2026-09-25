#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     The coordinated opener (suggested-tags.md §3.3): <c>K</c> detonations by one side inside
///     <c>W</c> seconds, the first such run per side per round. Smokes, flashes, HEs and inferno starts
///     count; an inferno start carries no thrower on the wire, so it has no side and counts only where
///     an attribution filled one in. The one-region factor is what separates a set execute from three
///     players throwing utility in three directions.
/// </summary>
public sealed class OpenerDetector : IProposalDetector
{
    /// <summary>The detector and code id.</summary>
    public const string DetectorId = "opener";

    private const double Ceiling = 0.95;

    // The window starting before this second reads as a planned opener rather than a mid-round one.
    private const int EarlySecond = 20;

    private static readonly HashSet<string> _kinds = new(StringComparer.Ordinal)
    {
        DetonationEvents.Smoke, DetonationEvents.Flash, DetonationEvents.He, DetonationEvents.Inferno
    };

    /// <inheritdoc />
    public string Id => DetectorId;

    /// <inheritdoc />
    public string Code => DetectorId;

    /// <inheritdoc />
    public IReadOnlyList<DetectorParameter> Parameters { get; } =
    [
        new("K", 3, "detonations by one side"),
        new("W", 2, "seconds the K detonations must fall inside"),
        new("lead", 5, "seconds claimed before the first detonation, for the throws"),
        new("lag", 6, "seconds claimed after the last detonation")
    ];

    /// <inheritdoc />
    public IEnumerable<TagProposal> Detect(DetectorContext ctx, RoundOccupancy round, DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(profile);

        int k = Math.Max(1, (int)profile.Get(Id, "K"));
        int windowTicks = (int)Math.Round(profile.Get(Id, "W") * round.TickRate);

        foreach (int side in (int[])[2, 3])
        {
            List<PlacedEvent> detonations = [.. ctx.Events.Where(e => e.Side == side && _kinds.Contains(e.Kind))];
            List<PlacedEvent>? group = null;
            for (int i = 0; i < detonations.Count && group is null; i++)
            {
                int j = i;
                while (j + 1 < detonations.Count && detonations[j + 1].Tick - detonations[i].Tick <= windowTicks)
                {
                    j++;
                }

                if (j - i + 1 >= k)
                {
                    group = detonations.GetRange(i, j - i + 1);
                }
            }

            if (group is null)
            {
                continue;
            }

            List<string> resolved = [.. group.Select(e => e.Place).OfType<string>()];
            string? region = resolved.Count > 0
                ? SiteRegions.Sites.FirstOrDefault(site => resolved.All(p => ctx.Regions.RegionOf(site).Contains(p)))
                : null;
            int firstSecond = round.SecondAt(group[0].Tick);

            double fCount = 0.1 * (group.Count - k);
            double fRegion = region is not null ? 0.2 : 0;
            double fEarly = firstSecond < EarlySecond ? 0.1 : 0;
            double confidence = Math.Min(Ceiling, 0.5 + fCount + fRegion + fEarly);

            Dictionary<string, string> labels = new(StringComparer.Ordinal)
            {
                ["side"] = ProposalIds.SideName(side),
                ["count"] = DetectorMath.Text(group.Count),
                ["kinds"] = string.Join(',', group.Select(e => e.Kind).Distinct().Order(StringComparer.Ordinal))
            };
            if (region is not null)
            {
                labels["region"] = region;
            }

            List<ProposalEvidence> evidence = [.. group.Select(DetectorMath.Evidence)];
            if (region is not null)
            {
                evidence.Add(new ProposalEvidence("event", group[^1].Tick, string.Create(CultureInfo.InvariantCulture,
                    $"all {resolved.Count} placed detonations in {DetectorMath.RegionText(region, ctx.Regions.RegionOf(region))}")));
            }

            yield return new TagProposal(
                ProposalIds.For("opener", round.Round, side, region, firstSecond),
                Id,
                Code,
                round.Round,
                side,
                DetectorMath.Lead(round, group[0].Tick, profile.Get(Id, "lead")),
                DetectorMath.Lag(round, group[^1].Tick, profile.Get(Id, "lag")),
                group[0].Tick,
                DetectorMath.Confidence(confidence),
                new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["base"] = 0.5,
                    ["count"] = DetectorMath.Round(fCount),
                    ["region"] = fRegion,
                    ["early"] = fEarly
                },
                labels,
                evidence);
        }
    }
}
