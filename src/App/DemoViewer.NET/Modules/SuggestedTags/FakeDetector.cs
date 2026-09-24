#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     Fake then rotate (suggested-tags.md §3.3): an execute at <c>Y</c> preceded, between
///     <c>fakeWindow</c> and 3 seconds before it, by at least <c>fakePresence</c> alive T players in the
///     other site's region or at least <c>fakeUtility</c> T detonations placed there. The execute keeps
///     its own band; the fake's band spans both.
///     <para>
///         The other region is counted without the places it shares with <c>Y</c>'s: a learned region can
///         reach into mid on both sites (de_inferno and de_ancient, §3.2), and a player in a shared place
///         is on the way to the real site as much as the fake one.
///     </para>
/// </summary>
public sealed class FakeDetector : IProposalDetector
{
    /// <summary>The detector and code id.</summary>
    public const string DetectorId = "fake";

    private const double Ceiling = 0.95;

    // The fake evidence must end this many seconds before the execute triggers.
    private const int Gap = 3;

    /// <inheritdoc />
    public string Id => DetectorId;

    /// <inheritdoc />
    public string Code => DetectorId;

    /// <inheritdoc />
    public IReadOnlyList<DetectorParameter> Parameters { get; } =
    [
        new("fakeWindow", 25, "seconds before the execute the fake is looked for"),
        new("fakePresence", 2, "alive T players at once in the other region"),
        new("fakeUtility", 2, "T detonations placed in the other region"),
        new("lead", 4, "seconds claimed before the first fake evidence")
    ];

    /// <inheritdoc />
    public IEnumerable<TagProposal> Detect(DetectorContext ctx, RoundOccupancy round, DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(profile);

        TagProposal? execute = ctx.EarlierProposalsThisRound.FirstOrDefault(p => p.Detector == ExecuteDetector.DetectorId);
        if (execute is null || !execute.Labels.TryGetValue("site", out string? real))
        {
            yield break;
        }

        string? fake = SiteRegions.Sites.FirstOrDefault(s => s != real);
        if (fake is null)
        {
            yield break;
        }

        int fakeWindow = (int)profile.Get(Id, "fakeWindow");
        int fakePresence = (int)profile.Get(Id, "fakePresence");
        int fakeUtility = (int)profile.Get(Id, "fakeUtility");

        HashSet<string> places = new(ctx.Regions.RegionOf(fake), StringComparer.Ordinal);
        places.ExceptWith(ctx.Regions.RegionOf(real));
        int trigger = round.SecondAt(execute.TriggerTick);
        int from = Math.Max(0, trigger - fakeWindow);
        int to = trigger - Gap;
        if (to < from)
        {
            yield break;
        }

        int presence = 0;
        int? presenceSecond = null;
        for (int s = from; s <= to && s < round.Seconds; s++)
        {
            int count = round.CountIn(2, s, places);
            presence = Math.Max(presence, count);
            if (presenceSecond is null && count >= fakePresence)
            {
                presenceSecond = s;
            }
        }

        List<PlacedEvent> utility = DetectorMath.DetonationsInto(ctx.Events, 2, places, round.TickAt(from), round.TickAt(to + 1));
        bool byPresence = presence >= fakePresence;
        bool byUtility = utility.Count >= fakeUtility;
        if (!byPresence && !byUtility)
        {
            yield break;
        }

        int firstEvidence = int.MaxValue;
        if (byPresence && presenceSecond is { } p)
        {
            firstEvidence = round.TickAt(p);
        }

        if (utility.Count > 0)
        {
            firstEvidence = Math.Min(firstEvidence, utility[0].Tick);
        }

        if (firstEvidence == int.MaxValue)
        {
            firstEvidence = round.TickAt(from); // a threshold of zero fires on nothing seen
        }

        double factor = 0.6 + 0.1 * presence + 0.1 * utility.Count;
        double confidence = Math.Min(Ceiling, execute.Confidence * factor);

        List<ProposalEvidence> evidence = [];
        if (presenceSecond is { } at)
        {
            evidence.Add(new ProposalEvidence("occupancy", round.TickAt(at),
                string.Create(CultureInfo.InvariantCulture, $"{presence} T in {DetectorMath.RegionText(fake, places)}")));
        }

        evidence.AddRange(utility.Select(DetectorMath.Evidence));
        evidence.Add(new ProposalEvidence("occupancy", execute.TriggerTick,
            string.Create(CultureInfo.InvariantCulture, $"execute at {real}")));
        evidence.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        yield return new TagProposal(
            ProposalIds.For("fake", round.Round, 2, $"{fake}>{real}", trigger),
            Id,
            Code,
            round.Round,
            2,
            DetectorMath.Lead(round, firstEvidence, profile.Get(Id, "lead")),
            execute.ToTick,
            firstEvidence,
            DetectorMath.Confidence(confidence),
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["execute"] = execute.Confidence,
                ["fake"] = DetectorMath.Round(factor)
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fake"] = fake,
                ["real"] = real,
                ["presence"] = DetectorMath.Text(presence),
                ["utility"] = DetectorMath.Text(utility.Count)
            },
            evidence);
    }
}
