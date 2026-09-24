#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Modules.SuggestedTags;

/// <summary>
///     Runs the detectors over a demo's rounds in the profile's order (suggested-tags.md §3.3, §3.5).
///     Pure: occupancy, events, regions and a profile in, proposals out.
/// </summary>
public static class ProposalDetection
{
    /// <summary>
    ///     A round with fewer seated players on either side is skipped: that drops warmup, and nobody
    ///     tags a round short of four a side. Overtime is a real round and is kept.
    /// </summary>
    public const int MinPlayersPerSide = 4;

    /// <summary>The five detectors in their shipped order. Execute comes first because default and fake read it.</summary>
    public static IReadOnlyList<IProposalDetector> All { get; } =
    [
        new ExecuteDetector(),
        new DefaultDetector(),
        new FakeDetector(),
        new OpenerDetector(),
        new RetakeDetector()
    ];

    /// <summary>The detector with an id, or null.</summary>
    /// <param name="id">A detector id.</param>
    public static IProposalDetector? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>
    ///     The proposals for every round, round by round. Events are the whole demo's; each round takes
    ///     the ones inside its window.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <param name="regions">The composed site regions for the map.</param>
    /// <param name="events">Every placed detonation of the demo, tick order.</param>
    /// <param name="rounds">The rounds' occupancy.</param>
    /// <param name="profile">The parameter values in force.</param>
    public static IReadOnlyList<TagProposal> Detect(
        string map,
        int tickRate,
        SiteRegions regions,
        IReadOnlyList<PlacedEvent> events,
        IEnumerable<RoundOccupancy> rounds,
        DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        return [.. rounds.SelectMany(r => DetectRound(map, tickRate, regions, events, r, profile))];
    }

    /// <summary>
    ///     One round: seats the round's events on the side their thrower played this round, then runs
    ///     each detector in <see cref="DetectorProfile.Order" /> with what the earlier ones proposed.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <param name="regions">The composed site regions for the map.</param>
    /// <param name="events">Placed detonations; those outside the round's window are ignored.</param>
    /// <param name="round">The round's occupancy.</param>
    /// <param name="profile">The parameter values in force.</param>
    public static IReadOnlyList<TagProposal> DetectRound(
        string map,
        int tickRate,
        SiteRegions regions,
        IReadOnlyList<PlacedEvent> events,
        RoundOccupancy round,
        DetectorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(profile);
        if (round.Slots(2).Count < MinPlayersPerSide || round.Slots(3).Count < MinPlayersPerSide)
        {
            return [];
        }

        // A thrower's side is the round's, not the demo's: sides swap at half.
        List<PlacedEvent> roundEvents =
        [
            .. events
                .Where(e => e.Tick >= round.StartTick && e.Tick < round.EndTick)
                .Select(e => e.ThrowerSlot >= 0 ? e with { Side = round.SideOf(e.ThrowerSlot) } : e)
                .OrderBy(e => e.Tick)
        ];

        List<TagProposal> proposals = [];
        foreach (string id in profile.Order)
        {
            if (Find(id) is not { } detector)
            {
                continue; // a detector a newer profile names that this build does not have
            }

            DetectorContext ctx = new(map, tickRate, regions, roundEvents, [.. proposals]);
            proposals.AddRange(detector.Detect(ctx, round, profile));
        }

        return proposals;
    }

    /// <summary>A detector's declared parameters, or none for an unknown id.</summary>
    /// <param name="detector">A detector id.</param>
    internal static IReadOnlyList<DetectorParameter> ParametersOf(string detector) =>
        Find(detector)?.Parameters ?? [];

    /// <summary>A parameter's shipped value, or null when no detector declares it.</summary>
    /// <param name="detector">A detector id.</param>
    /// <param name="parameter">The parameter name.</param>
    internal static double? DefaultOf(string detector, string parameter) =>
        ParametersOf(detector).FirstOrDefault(p => p.Name == parameter)?.Default;
}

/// <summary>The arithmetic and wording the five detectors share.</summary>
internal static class DetectorMath
{
    /// <summary>The confidence floor every proposal is clamped to.</summary>
    internal const double MinConfidence = 0.05;

    /// <summary>The confidence ceiling.</summary>
    internal const double MaxConfidence = 0.99;

    /// <summary>
    ///     Clamps to <c>[0.05, 0.99]</c> and rounds to four places, so a proposal written to disk and read
    ///     back compares equal and a golden does not move on the last bit of a product.
    /// </summary>
    internal static double Confidence(double value) => Round(Math.Clamp(value, MinConfidence, MaxConfidence));

    /// <summary>Four decimal places.</summary>
    internal static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    /// <summary>A region as evidence reads it: the site first, then the rest in ordinal order.</summary>
    internal static string RegionText(string site, IReadOnlySet<string> region) =>
        string.Join('+', region.Contains(site)
            ? region.Where(p => p != site).Order(StringComparer.Ordinal).Prepend(site)
            : region.Order(StringComparer.Ordinal));

    /// <summary>An integer label value.</summary>
    internal static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>One evidence line for a detonation.</summary>
    internal static ProposalEvidence Evidence(PlacedEvent e) => new(
        "event",
        e.Tick,
        e.ThrowerSlot >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{e.Kind} by slot {e.ThrowerSlot} -> {e.Place ?? "unplaced"}")
            : string.Create(CultureInfo.InvariantCulture, $"{e.Kind} -> {e.Place ?? "unplaced"}"));

    /// <summary>A tick <paramref name="seconds" /> before <paramref name="tick" />, floored at the round start.</summary>
    internal static int Lead(RoundOccupancy round, int tick, double seconds) =>
        Math.Max(round.StartTick, tick - (int)Math.Round(seconds * round.TickRate));

    /// <summary>A tick <paramref name="seconds" /> after <paramref name="tick" />.</summary>
    internal static int Lag(RoundOccupancy round, int tick, double seconds) =>
        tick + (int)Math.Round(seconds * round.TickRate);

    /// <summary>The detonations of one side placed inside a place set, in <c>[fromTick, toTick)</c>.</summary>
    internal static List<PlacedEvent> DetonationsInto(
        IReadOnlyList<PlacedEvent> events,
        int side,
        IReadOnlySet<string> places,
        int fromTick,
        int toTick) =>
    [
        .. events.Where(e => e.Side == side && e.Place is { } place && places.Contains(place)
                             && e.Tick >= fromTick && e.Tick < toTick)
    ];
}
