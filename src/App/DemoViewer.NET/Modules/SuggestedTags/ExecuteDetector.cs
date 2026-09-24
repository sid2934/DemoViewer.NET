#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     The execute, with the fast-or-rush label on it (suggested-tags.md §3.3). At some second
///     <c>s</c> no earlier than <c>minSecond</c>, at least <c>N</c> alive T players are inside a site's
///     region, and within <c>T + touch</c> seconds a T player is inside the site itself. The earliest
///     such second in the round, over both sites with A first on a tie, is the round's one execute.
///     <para>
///         Shipped strict (<c>N = 4</c>, <c>T = 4 s</c>, owner decision 2): a proposal the tagger rejects
///         costs a keypress, but a queue that is mostly noise gets ignored. The touch is read from the
///         per-side count, so the index and the walk agree on it; with per-slot data a profile can switch
///         to the window mode (<c>window = 1</c>), which counts distinct players with any second in the
///         region inside a <c>T</c>-second window and so catches a group that arrives staggered.
///     </para>
/// </summary>
public sealed class ExecuteDetector : IProposalDetector
{
    /// <summary>The detector and code id.</summary>
    public const string DetectorId = "execute";

    /// <inheritdoc />
    public string Id => DetectorId;

    /// <inheritdoc />
    public string Code => DetectorId;

    /// <inheritdoc />
    public IReadOnlyList<DetectorParameter> Parameters { get; } =
    [
        new("N", 4, "alive T players in the site region at once"),
        new("T", 4, "seconds after the trigger the count and the site touch are read over"),
        new("touch", 10, "further seconds a late site touch is still accepted, at 0.7"),
        new("minSecond", 3, "the earliest second an execute can trigger"),
        new("lead", 6, "seconds claimed before the trigger, or before the first arrival with per-slot data"),
        new("lag", 8, "seconds claimed after trigger plus T"),
        new("rushSecond", 25, "an execute at or before this second is tempo rush"),
        new("slowSecond", 60, "an execute at or after this second is tempo late"),
        new("window", 0, "1 counts distinct players across a T-second window (per-slot data only); 0 counts at one second")
    ];

    /// <inheritdoc />
    public IEnumerable<TagProposal> Detect(DetectorContext ctx, RoundOccupancy round, DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(profile);

        int n = (int)profile.Get(Id, "N");
        int t = (int)profile.Get(Id, "T");
        int touch = (int)profile.Get(Id, "touch");
        int minSecond = (int)profile.Get(Id, "minSecond");
        bool windowMode = profile.Get(Id, "window") >= 1 && round.HasSlots;

        (int Second, string Site, int TouchSecond)? found = null;
        foreach (string site in SiteRegions.Sites)
        {
            IReadOnlySet<string> region = ctx.Regions.RegionOf(site);
            HashSet<string> siteOnly = new(StringComparer.Ordinal) { site };
            for (int s = Math.Max(0, minSecond); s < round.Seconds; s++)
            {
                if (found is { } earlier && s >= earlier.Second)
                {
                    break; // a later site cannot beat an earlier one; a tie goes to A
                }

                int count = windowMode ? DistinctIn(round, region, s, s + t - 1).Count : round.CountIn(2, s, region);
                if (count < n)
                {
                    continue;
                }

                int? touched = null;
                for (int u = s; u <= s + t + touch && u < round.Seconds; u++)
                {
                    if (round.CountIn(2, u, siteOnly) > 0)
                    {
                        touched = u;
                        break;
                    }
                }

                if (touched is { } touchSecond)
                {
                    found = (s, site, touchSecond);
                    break;
                }
            }
        }

        if (found is not { } execute)
        {
            yield break;
        }

        (int trigger, string at, int touchAt) = execute;
        IReadOnlySet<string> fired = ctx.Regions.RegionOf(at);

        int peak = 0;
        if (windowMode)
        {
            peak = DistinctIn(round, fired, trigger, trigger + t - 1).Count;
        }
        else
        {
            for (int u = trigger; u <= trigger + t && u < round.Seconds; u++)
            {
                peak = Math.Max(peak, round.CountIn(2, u, fired));
            }
        }

        List<PlacedEvent> utility = DetectorMath.DetonationsInto(ctx.Events, 2, fired,
            round.TickAt(trigger - 8), round.TickAt(trigger + t + 1));
        double fCount = Math.Min(1, peak / 5.0);
        double fTouch = touchAt <= trigger + t ? 1 : 0.7;
        double fUtility = 0.6 + 0.1 * Math.Min(4, utility.Count);

        int triggerTick = round.TickAt(trigger);
        int arrival = round.HasSlots ? FirstArrival(round, fired, trigger, windowMode ? trigger + t - 1 : trigger) : trigger;
        int from = DetectorMath.Lead(round, round.TickAt(arrival), profile.Get(Id, "lead"));
        int to = DetectorMath.Lag(round, round.TickAt(trigger + t), profile.Get(Id, "lag"));

        Dictionary<string, string> labels = new(StringComparer.Ordinal)
        {
            ["site"] = at,
            ["count"] = DetectorMath.Text(peak),
            ["tempo"] = Tempo(trigger, profile)
        };
        if (round.Bomb is { } bomb && bomb.Site == at && bomb.PlantTick >= triggerTick)
        {
            labels["plant"] = "true";
        }

        List<ProposalEvidence> evidence =
        [
            new("occupancy", triggerTick,
                string.Create(CultureInfo.InvariantCulture, $"{peak} T in {DetectorMath.RegionText(at, fired)}")),
            new("occupancy", round.TickAt(touchAt), string.Create(CultureInfo.InvariantCulture, $"T in {at}"))
        ];
        evidence.AddRange(utility.Select(DetectorMath.Evidence));
        evidence.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        yield return new TagProposal(
            ProposalIds.For("exec", round.Round, 2, at, trigger),
            Id,
            Code,
            round.Round,
            2,
            from,
            to,
            triggerTick,
            DetectorMath.Confidence(fCount * fTouch * fUtility),
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["count"] = DetectorMath.Round(fCount),
                ["touch"] = DetectorMath.Round(fTouch),
                ["utility"] = DetectorMath.Round(fUtility)
            },
            labels,
            evidence);
    }

    /// <summary><c>rush</c>, <c>mid</c> or <c>late</c> for an execute triggered at <paramref name="second" />.</summary>
    /// <param name="second">The trigger second.</param>
    /// <param name="profile">The profile whose <c>rushSecond</c> and <c>slowSecond</c> apply.</param>
    public static string Tempo(int second, DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (second <= profile.Get(DetectorId, "rushSecond"))
        {
            return "rush";
        }

        return second >= profile.Get(DetectorId, "slowSecond") ? "late" : "mid";
    }

    // The T slots with any second in the region inside [from, to].
    private static HashSet<int> DistinctIn(RoundOccupancy round, IReadOnlySet<string> region, int from, int to)
    {
        HashSet<int> slots = [];
        foreach (int slot in round.Slots(2))
        {
            for (int u = Math.Max(0, from); u <= to && u < round.Seconds; u++)
            {
                if (round.PlaceOf(slot, u) is { } place && region.Contains(place))
                {
                    slots.Add(slot);
                    break;
                }
            }
        }

        return slots;
    }

    // The earliest second any player counted in [from, to] entered the region on the stay that
    // counted: walking back from a player's first counted second to where the stay began.
    private static int FirstArrival(RoundOccupancy round, IReadOnlySet<string> region, int from, int to)
    {
        int earliest = from;
        foreach (int slot in round.Slots(2))
        {
            int? counted = null;
            for (int u = from; u <= to && u < round.Seconds; u++)
            {
                if (round.PlaceOf(slot, u) is { } place && region.Contains(place))
                {
                    counted = u;
                    break;
                }
            }

            if (counted is not { } s)
            {
                continue;
            }

            while (s > 0 && round.PlaceOf(slot, s - 1) is { } before && region.Contains(before))
            {
                s--;
            }

            earliest = Math.Min(earliest, s);
        }

        return earliest;
    }
}
