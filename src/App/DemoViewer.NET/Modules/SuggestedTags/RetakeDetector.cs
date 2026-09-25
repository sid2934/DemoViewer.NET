#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     Retake grouping (suggested-tags.md §3.3): after the plant, CT players alive at the plant each
///     first enter the site's region within <c>retakeGap</c> seconds of the previous entrant; the group
///     is every entrant of the first run of at least <c>minGroup</c>. Rounds with one alive CT or none
///     produce nothing, correctly.
///     <para>
///         Entry times need the per-slot form. With only the per-side token it degrades to the fallback
///         the first release ships: the CT count in the region rose from below <c>minGroup</c> to at least
///         <c>minGroup</c> within <c>retakeGap</c> seconds.
///     </para>
/// </summary>
public sealed class RetakeDetector : IProposalDetector
{
    /// <summary>The detector and code id.</summary>
    public const string DetectorId = "retake";

    private const double Ceiling = 0.95;

    /// <inheritdoc />
    public string Id => DetectorId;

    /// <inheritdoc />
    public string Code => DetectorId;

    /// <inheritdoc />
    public IReadOnlyList<DetectorParameter> Parameters { get; } =
    [
        new("retakeGap", 6, "seconds between one CT's entry into the region and the next"),
        new("minGroup", 2, "entrants a run needs to be a group"),
        new("lead", 5, "seconds claimed before the first entrant"),
        new("lag", 10, "seconds claimed after the last entrant, capped at the round end")
    ];

    /// <inheritdoc />
    public IEnumerable<TagProposal> Detect(DetectorContext ctx, RoundOccupancy round, DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(profile);

        if (round.Bomb is not { Site: { } site } bomb)
        {
            yield break;
        }

        int gap = (int)profile.Get(Id, "retakeGap");
        int minGroup = Math.Max(1, (int)profile.Get(Id, "minGroup"));
        IReadOnlySet<string> region = ctx.Regions.RegionOf(site);
        int plantSecond = Math.Max(0, round.SecondAt(bomb.PlantTick));

        int aliveCt;
        int first;
        int last;
        int group;
        List<ProposalEvidence> evidence = [];
        if (round.HasSlots)
        {
            List<int> alive = [.. round.Slots(3).Where(slot => round.IsAlive(slot, plantSecond))];
            aliveCt = alive.Count;
            List<(int Slot, int Second)> entries = [];
            foreach (int slot in alive)
            {
                for (int s = plantSecond; s < round.Seconds; s++)
                {
                    if (round.PlaceOf(slot, s) is { } place && region.Contains(place))
                    {
                        entries.Add((slot, s));
                        break;
                    }
                }
            }

            entries.Sort((a, b) => a.Second != b.Second ? a.Second.CompareTo(b.Second) : a.Slot.CompareTo(b.Slot));
            List<(int Slot, int Second)>? run = null;
            List<(int Slot, int Second)> current = [];
            foreach ((int Slot, int Second) entry in entries)
            {
                if (current.Count > 0 && entry.Second - current[^1].Second > gap)
                {
                    if (current.Count >= minGroup)
                    {
                        break;
                    }

                    current = [];
                }

                current.Add(entry);
            }

            if (current.Count >= minGroup)
            {
                run = current;
            }

            if (run is null)
            {
                yield break;
            }

            first = run[0].Second;
            last = run[^1].Second;
            group = run.Count;
            evidence.AddRange(run.Select(e => new ProposalEvidence("occupancy", round.TickAt(e.Second),
                string.Create(CultureInfo.InvariantCulture, $"CT slot {e.Slot} enters {round.PlaceOf(e.Slot, e.Second)}"))));
        }
        else
        {
            aliveCt = round.AliveCount(3, plantSecond);
            (int From, int To, int Count)? rise = null;
            for (int b = plantSecond; b < round.Seconds && rise is null; b++)
            {
                int at = round.CountIn(3, b, region);
                int lower = Math.Max(plantSecond, b - gap);
                if (at < minGroup || !Enumerable.Range(lower, b - lower).Any(a => round.CountIn(3, a, region) < minGroup))
                {
                    continue;
                }

                // The first entrant is where the unbroken presence that reached the group began, no
                // further back than the gap allows.
                int start = b;
                while (start - 1 >= lower && round.CountIn(3, start - 1, region) > 0)
                {
                    start--;
                }

                rise = (start, b, at);
            }

            if (rise is not { } found)
            {
                yield break;
            }

            (first, last, group) = found;
            evidence.Add(new ProposalEvidence("occupancy", round.TickAt(last),
                string.Create(CultureInfo.InvariantCulture, $"{group} CT in {DetectorMath.RegionText(site, region)}")));
        }

        if (aliveCt < minGroup)
        {
            yield break;
        }

        int from = DetectorMath.Lead(round, round.TickAt(first), profile.Get(Id, "lead"));
        int to = Math.Min(round.EndTick, DetectorMath.Lag(round, round.TickAt(last), profile.Get(Id, "lag")));
        List<PlacedEvent> utility = DetectorMath.DetonationsInto(ctx.Events, 3, region, from, to + 1);
        bool defused = bomb.DefuseTick is not null;
        string outcome = defused ? "defused" : bomb.ExplodeTick is not null ? "exploded" : "wiped";

        double fGroup = 0.15 * (group - 2);
        double fUtility = utility.Count > 0 ? 0.1 : 0;
        double fDefused = defused ? 0.1 : 0;
        double confidence = Math.Min(Ceiling, 0.5 + fGroup + fUtility + fDefused);

        evidence.Add(new ProposalEvidence("bomb", bomb.PlantTick,
            string.Create(CultureInfo.InvariantCulture, $"planted at {site} with {aliveCt} CT alive")));
        evidence.AddRange(utility.Select(DetectorMath.Evidence));
        evidence.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        yield return new TagProposal(
            ProposalIds.For("retake", round.Round, 3, site, first),
            Id,
            Code,
            round.Round,
            3,
            from,
            to,
            round.TickAt(first),
            DetectorMath.Confidence(confidence),
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["base"] = 0.5,
                ["group"] = DetectorMath.Round(fGroup),
                ["utility"] = fUtility,
                ["defused"] = fDefused
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["site"] = site,
                ["group"] = DetectorMath.Text(group),
                ["aliveCt"] = DetectorMath.Text(aliveCt),
                ["outcome"] = outcome
            },
            evidence);
    }
}
