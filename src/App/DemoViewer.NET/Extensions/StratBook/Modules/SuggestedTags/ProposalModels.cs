namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>One tunable number of a detector, with the value the shipped profile gives it.</summary>
/// <param name="Name">The key in the profile's detector section, as §3.7 spells it.</param>
/// <param name="Default">The shipped value.</param>
/// <param name="Meaning">What moving it does, for the tuning view.</param>
public sealed record DetectorParameter(string Name, double Default, string Meaning);

/// <summary>One line of why a proposal fired.</summary>
/// <param name="Kind"><c>occupancy</c>, <c>event</c> or <c>bomb</c>.</param>
/// <param name="Tick">Frame clock.</param>
/// <param name="Text">What a coach reads.</param>
public sealed record ProposalEvidence(string Kind, int Tick, string Text);

/// <summary>
///     A candidate tag instance (suggested-tags.md §3.1, §3.5). Never a tag until accepted. Ticks are
///     frame clock; <see cref="Confidence" /> is the product of <see cref="Factors" /> (or their sum for
///     the additive detectors) clamped to <c>[0.05, 0.99]</c>, so the queue can show why.
/// </summary>
/// <param name="Id">The identity key a verdict is remembered under; see <see cref="ProposalIds" />.</param>
/// <param name="Detector">The detector id.</param>
/// <param name="Code">The tag code proposed.</param>
/// <param name="Round">The round number.</param>
/// <param name="Side">2 = T, 3 = CT.</param>
/// <param name="FromTick">The claim window's start.</param>
/// <param name="ToTick">The claim window's end.</param>
/// <param name="TriggerTick">The tick the rule fired on.</param>
/// <param name="Confidence">In <c>[0.05, 0.99]</c>.</param>
/// <param name="Factors">The named factors the confidence was computed from.</param>
/// <param name="Labels">The labels an accepted instance carries.</param>
/// <param name="Evidence">Why it fired, in tick order.</param>
public sealed record TagProposal(
    string Id,
    string Detector,
    string Code,
    int Round,
    int Side,
    int FromTick,
    int ToTick,
    int TriggerTick,
    double Confidence,
    IReadOnlyDictionary<string, double> Factors,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<ProposalEvidence> Evidence);

/// <summary>
///     What a detector sees besides the round: the map, the clock, the site regions in force, the
///     round's placed detonations with their sides, and what the detectors before it proposed for this
///     round (default and fake read the execute's result).
/// </summary>
/// <param name="Map">The map as the demo header spells it.</param>
/// <param name="TickRate">Ticks per second.</param>
/// <param name="Regions">The composed site regions for the map.</param>
/// <param name="Events">The round's detonations, tick order, side filled.</param>
/// <param name="EarlierProposalsThisRound">Every proposal an earlier detector in the order made for this round.</param>
public sealed record DetectorContext(
    string Map,
    int TickRate,
    SiteRegions Regions,
    IReadOnlyList<PlacedEvent> Events,
    IReadOnlyList<TagProposal> EarlierProposalsThisRound);

/// <summary>
///     A pure function from one round's occupancy and events to zero or more proposals. The five are
///     ordinary classes in <see cref="ProposalDetection.All" />; there is deliberately no plug-in seam
///     (a team that wants its own detector wants YAML, which is route (b)).
/// </summary>
public interface IProposalDetector
{
    /// <summary><c>execute</c>, <c>default</c>, <c>fake</c>, <c>opener</c> or <c>retake</c>: the profile section.</summary>
    string Id { get; }

    /// <summary>The tag code proposed.</summary>
    string Code { get; }

    /// <summary>Every tunable number, with its shipped value.</summary>
    IReadOnlyList<DetectorParameter> Parameters { get; }

    /// <summary>The proposals for one round.</summary>
    /// <param name="ctx">The map, the regions, the events and the earlier detectors' proposals.</param>
    /// <param name="round">The round's occupancy.</param>
    /// <param name="profile">The parameter values in force.</param>
    IEnumerable<TagProposal> Detect(DetectorContext ctx, RoundOccupancy round, DetectorProfile profile);
}

/// <summary>
///     The identity key: <c>detector | round | side | site-or-region | trigger second quantised to 5 s</c>
///     (suggested-tags.md §3.4). Stable across a re-detection with changed parameters as long as the
///     same event is found, which is what lets a rejection survive a tuning pass.
/// </summary>
public static class ProposalIds
{
    /// <summary>Seconds the trigger second is floored to.</summary>
    public const int Quantum = 5;

    /// <summary>Builds a key. A null <paramref name="where" /> or <paramref name="triggerSecond" /> drops that segment.</summary>
    /// <param name="prefix">The detector's short name: <c>exec</c>, <c>default</c>, <c>fake</c>, <c>opener</c>, <c>retake</c>.</param>
    /// <param name="round">The round number.</param>
    /// <param name="side">2 = T, 3 = CT.</param>
    /// <param name="where">The site, the region or the fake-to-real pair.</param>
    /// <param name="triggerSecond">Seconds since freeze end.</param>
    public static string For(string prefix, int round, int side, string? where, int? triggerSecond)
    {
        List<string> parts =
        [
            prefix,
            FormattableString.Invariant($"r{round}"),
            SideName(side)
        ];
        if (where is not null)
        {
            parts.Add(where);
        }

        if (triggerSecond is { } s)
        {
            int quantised = (int)Math.Floor(s / (double)Quantum) * Quantum;
            parts.Add(FormattableString.Invariant($"s={quantised}"));
        }

        return string.Join('|', parts);
    }

    /// <summary><c>T</c>, <c>CT</c> or <c>?</c>.</summary>
    /// <param name="side">2 = T, 3 = CT.</param>
    public static string SideName(int side) => side switch
    {
        2 => "T",
        3 => "CT",
        _ => "?"
    };
}
