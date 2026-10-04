#region

using DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Services.Strats;

#endregion

namespace DemoViewer.NET.Modules.StratBook.Canvas;

/// <summary>
///     How a throw's lineup flies and where it lands, or null when it does not resolve. Called on the UI thread,
///     so it answers from memory.
/// </summary>
/// <param name="map">The strat's map.</param>
/// <param name="utility">The step's utility, with a lineup id.</param>
public delegate LineupFlight? ThrowFlightResolver(string map, UtilityRef utility);

/// <summary>
///     The utility pass of a projection: every step with utility becomes a <see cref="UtilityCue" /> that leaves its
///     thrower at the step's tick, flies to the landing and goes off there.
///     <para>
///         The thrower stands at the lineup's technique origin when the lineup resolves, else wherever its token is
///         at the step's tick. The landing is the lineup's landing group, else <c>utility.landing</c>'s point, else
///         its place's arrival centre. A throw with no landing draws nothing.
///     </para>
/// </summary>
public static class StratThrows
{
    /// <summary>
    ///     How fast a throw without a recorded air time crosses the map. A full throw covers about 2000 units in
    ///     2.5 s, so this gives a long smoke and a short pop-flash plausible times.
    /// </summary>
    public const double UnitsPerSecond = 800;

    /// <summary>The shortest flight a throw without a recorded air time gets.</summary>
    public const double MinFlightSeconds = 0.5;

    /// <summary>The longest flight a throw without a recorded air time gets.</summary>
    public const double MaxFlightSeconds = 3.5;

    /// <summary>Every path step's throws, in path order.</summary>
    /// <param name="map">The strat's map.</param>
    /// <param name="path">The steps.</param>
    /// <param name="ticks">Each step's tick.</param>
    /// <param name="tracks">The projected token tracks, for a thrower without a lineup.</param>
    /// <param name="labels">Each slot's side.</param>
    /// <param name="flights">Resolves a lineup; null resolves none.</param>
    /// <param name="arrivals">Where a landing place is; null resolves no place.</param>
    /// <param name="defaultLevelMinZ">The level a landing without one sits on.</param>
    public static List<UtilityCue> Cues(string map, IReadOnlyList<StratPathStep> path, IReadOnlyList<int> ticks,
        IReadOnlyList<TokenTrack> tracks, IReadOnlyList<TokenLabel> labels, ThrowFlightResolver? flights,
        PlaceArrivalResolver? arrivals, double defaultLevelMinZ)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(ticks);
        List<UtilityCue> cues = [];
        for (int i = 0; i < path.Count; i++)
        {
            StratStep step = path[i].Step;
            if (step.Utility is not { } utility || GrenadeOf(utility.Kind) is not { } kind)
            {
                continue;
            }

            LineupFlight? lineup = utility.LineupId is not null && flights is not null ? flights(map, utility) : null;
            List<string> throwers = ThrowersOf(step);
            int team = TeamOf(throwers.Count > 0 ? throwers[0] : null, labels);
            if (lineup is not null)
            {
                // One lineup is one grenade, however many lines the step has.
                cues.Add(LineupCue(ticks[i], kind, team, lineup));
                continue;
            }

            if (LandingOf(utility.Landing, arrivals, defaultLevelMinZ) is not { } landing)
            {
                continue;
            }

            if (throwers.Count == 0)
            {
                cues.Add(new UtilityCue(ticks[i], kind, landing.X, landing.Y, MarkerZ(landing.LevelMinZ)));
                continue;
            }

            foreach (string slot in throwers)
            {
                TokenTrack? track = tracks.FirstOrDefault(t => string.Equals(t.Slot, slot, StringComparison.Ordinal));
                cues.Add(track is not null && track.TrySample(ticks[i], out TokenKeyframe at)
                    ? StraightCue(ticks[i], kind, TeamOf(slot, labels), (at.X, at.Y, at.LevelMinZ), landing)
                    : new UtilityCue(ticks[i], kind, landing.X, landing.Y, MarkerZ(landing.LevelMinZ)));
            }
        }

        return cues;
    }

    /// <summary>The grenade a strat kind names, or null for one the preview does not know.</summary>
    /// <param name="kind">The step's <c>utility.kind</c>.</param>
    public static GrenadeKind? GrenadeOf(string? kind) => kind switch
    {
        "smoke" => GrenadeKind.Smoke,
        "molotov" => GrenadeKind.Molotov,
        "he" => GrenadeKind.He,
        "flash" => GrenadeKind.Flash,
        "decoy" => GrenadeKind.Decoy,
        _ => null
    };

    /// <summary>A flight without a recorded air time: its distance at <see cref="UnitsPerSecond" />, clamped.</summary>
    /// <param name="distance">World units from release to landing.</param>
    public static double FlightSecondsFor(double distance) =>
        Math.Clamp(distance / UnitsPerSecond, MinFlightSeconds, MaxFlightSeconds);

    private static UtilityCue LineupCue(int tick, GrenadeKind kind, int team, LineupFlight lineup)
    {
        double airSeconds = lineup.AirSeconds > 0
            ? lineup.AirSeconds
            : FlightSecondsFor(Distance(lineup.Origin.X, lineup.Origin.Y, (float)lineup.LandingX, (float)lineup.LandingY));
        int air = Math.Max(1, (int)Math.Round(airSeconds * StepSchedule.TicksPerSecond));
        int pop = Math.Max(air, (int)Math.Round(lineup.PopSeconds * StepSchedule.TicksPerSecond));
        float originZ = MarkerZ(lineup.Origin.LevelMinZ);
        float landingZ = MarkerZ(lineup.LandingLevelMinZ);
        List<FlightPoint> flight = [];
        if (lineup.Path is { Count: >= 2 } recorded && recorded[^1].Seconds > 0)
        {
            // The recorded shape, bent so it leaves the technique's origin and ends on the group's landing.
            (double _, float firstX, float firstY, double _) = recorded[0];
            (double last, float lastX, float lastY, double _) = recorded[^1];
            int previous = -1;
            foreach ((double seconds, float x, float y, double level) in recorded)
            {
                float u = (float)Math.Clamp(seconds / last, 0, 1);
                int at = tick + (int)Math.Round(u * air);
                if (at <= previous)
                {
                    continue;
                }

                previous = at;
                flight.Add(new FlightPoint(at,
                    x + (lineup.Origin.X - firstX) * (1 - u) + ((float)lineup.LandingX - lastX) * u,
                    y + (lineup.Origin.Y - firstY) * (1 - u) + ((float)lineup.LandingY - lastY) * u,
                    MarkerZ(level)));
            }

            flight[0] = flight[0] with { X = lineup.Origin.X, Y = lineup.Origin.Y, Z = originZ };
            flight[^1] = flight[^1] with { Tick = tick + air, X = (float)lineup.LandingX, Y = (float)lineup.LandingY, Z = landingZ };
        }

        if (flight.Count < 2)
        {
            flight =
            [
                new FlightPoint(tick, lineup.Origin.X, lineup.Origin.Y, originZ),
                new FlightPoint(tick + air, (float)lineup.LandingX, (float)lineup.LandingY, landingZ)
            ];
        }

        return new UtilityCue(tick + pop, kind, (float)lineup.LandingX, (float)lineup.LandingY, landingZ)
        {
            Team = team,
            Flight = flight
        };
    }

    private static UtilityCue StraightCue(int tick, GrenadeKind kind, int team, (float X, float Y, double LevelMinZ) from,
        (float X, float Y, double LevelMinZ) to)
    {
        int air = Math.Max(1, (int)Math.Round(FlightSecondsFor(Distance(from.X, from.Y, to.X, to.Y)) * StepSchedule.TicksPerSecond));
        float landingZ = MarkerZ(to.LevelMinZ);
        return new UtilityCue(tick + air, kind, to.X, to.Y, landingZ)
        {
            Team = team,
            Flight =
            [
                new FlightPoint(tick, from.X, from.Y, MarkerZ(from.LevelMinZ)),
                new FlightPoint(tick + air, to.X, to.Y, landingZ)
            ]
        };
    }

    private static (float X, float Y, double LevelMinZ)? LandingOf(UtilityLanding? landing, PlaceArrivalResolver? arrivals,
        double defaultLevelMinZ)
    {
        if (landing is null)
        {
            return null;
        }

        double level = landing.LevelMinZ ?? defaultLevelMinZ;
        if (landing is { X: { } x, Y: { } y } && double.IsFinite(x) && double.IsFinite(y))
        {
            return ((float)x, (float)y, level);
        }

        return landing.Place is { Length: > 0 } place && arrivals?.Invoke(place, level) is { } centre
            ? ((float)centre.X, (float)centre.Y, centre.LevelMinZ)
            : null;
    }

    private static double Distance(float x1, float y1, float x2, float y2) =>
        Math.Sqrt(((double)x2 - x1) * (x2 - x1) + ((double)y2 - y1) * (y2 - y1));

    // Each line's slot, or the single actor; none for "all" without lines.
    private static List<string> ThrowersOf(StratStep step) =>
        [.. StratStepLines.Of(step).Select(l => l.Slot).Where(StratVocabulary.Slots.Contains).Distinct(StringComparer.Ordinal)];

    private static int TeamOf(string? slot, IReadOnlyList<TokenLabel> labels) =>
        labels.FirstOrDefault(l => string.Equals(l.Slot, slot ?? TokenSlots.Own[0], StringComparison.Ordinal)).Team;

    // The middle of the level's first quantum, as a token's marker Z is, so the draw lands on that level's pane.
    private static float MarkerZ(double levelMinZ) => (float)(levelMinZ + MapSpace.LevelQuantum / 2);
}
