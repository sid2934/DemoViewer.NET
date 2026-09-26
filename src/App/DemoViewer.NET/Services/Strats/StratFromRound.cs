#region

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Services.RoundFacts;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>What the review settled before anything is written: the side, the slot map and the arrows.</summary>
/// <param name="OurSide">2 for T, 3 for CT.</param>
/// <param name="Tokens">Controller slot to token slot, <c>A..E</c> for ours and <c>O1..O5</c> for theirs.</param>
/// <param name="RoundSeconds">The round length the clock maps with (<see cref="StratClock.RoundSecondsFor" />).</param>
/// <param name="DrawThrowArrows">One arrow per throw step from the thrower to the landing (on by default).</param>
/// <param name="LevelMinZFor">A world Z's level key; <see cref="StratFromRound.QuantizedLevel" /> without map levels.</param>
public sealed record StratCaptureOptions(
    int OurSide,
    IReadOnlyDictionary<int, string> Tokens,
    double RoundSeconds,
    bool DrawThrowArrows,
    Func<double, double> LevelMinZFor);

/// <summary>
///     Create Strat From Round's pre-population (step-authoring.md §3.9): a captured round turned into steps on the
///     strat clock. Pure: the walk is <see cref="RoundCaptureWalker" />'s and the review is the dialog's, so the same
///     capture rebuilds instantly when the side or the slot map changes.
///     <para>
///         <b>Cadence (O-25).</b> The freeze-end holds every live pawn on both sides. Each of our side's utility
///         events is a <c>throw</c> step carrying the thrower and every token that moved more than
///         <see cref="MoveThreshold" /> units since its last keyframe; <c>bomb_planted</c> is a <c>plant</c> step for
///         the planter; the 10 s sweep is a <c>move</c> step for the tokens that moved that far. A token that did not
///         move gets no entry, which is the stationary rule (§3.3).
///     </para>
/// </summary>
public static class StratFromRound
{
    /// <summary>World units a token has to move since its last keyframe to get a new one (O-25).</summary>
    public const double MoveThreshold = 200;

    /// <summary>The generated arrows' width: the ink default, so they read like hand-drawn ones.</summary>
    private static readonly AnnotationStyle ArrowStyle = AnnotationStyle.Default;

    /// <summary>
    ///     The level key of a world Z with no map levels to ask: the Z itself, quantized. A token is drawn at the
    ///     key plus half a quantum, so the pane's band lookup still lands on the pawn's floor.
    /// </summary>
    /// <param name="z">World Z.</param>
    public static double QuantizedLevel(double z) => MapSpace.QuantizeZ(z);

    /// <summary>
    ///     The level key of a world Z against the host's levels (<c>MapSpace.LevelFor(z).ZMin</c>, quantized), the
    ///     band a Z sits in, else the nearest band; with no levels, <see cref="QuantizedLevel" />. The list is a copy
    ///     taken on the UI thread, so the capture thread never reads the live map space.
    /// </summary>
    /// <param name="levels">The map's levels, bottom first or in any order.</param>
    public static Func<double, double> LevelKeys(IReadOnlyList<MapLevel>? levels)
    {
        if (levels is not { Count: > 0 })
        {
            return QuantizedLevel;
        }

        MapLevel[] bands = [.. levels];
        return z =>
        {
            MapLevel? best = null;
            double distance = double.MaxValue;
            foreach (MapLevel band in bands)
            {
                if (band.Contains(z))
                {
                    return MapSpace.QuantizeZ(band.ZMin);
                }

                double d = Math.Abs(z - (band.ZMin + band.ZMax) / 2);
                if (d < distance)
                {
                    distance = d;
                    best = band;
                }
            }

            return MapSpace.QuantizeZ(best!.ZMin);
        };
    }

    /// <summary>The steps of a captured round, in tick order, which is <c>atSeconds</c> non-increasing.</summary>
    /// <param name="capture">The walk's result.</param>
    /// <param name="options">The reviewed side, slot map, round length and arrows.</param>
    public static List<StratStep> Steps(RoundCapture capture, StratCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(options);

        List<StratStep> steps = [];
        Dictionary<string, Keyed> last = new(StringComparer.Ordinal);
        foreach (CaptureMoment moment in capture.Moments)
        {
            StratStep? step = moment.Trigger switch
            {
                CaptureTrigger.FreezeEnd => FreezeEnd(moment, options, last),
                CaptureTrigger.Utility => Throw(moment, options, last),
                CaptureTrigger.Plant => Plant(moment, options, last),
                _ => Sweep(moment, options, last)
            };
            if (step is null)
            {
                continue;
            }

            step.Id = Guid.NewGuid();
            step.AtSeconds = StratClock.AtSecondsFor(moment.Tick, capture.FreezeEndTick, capture.TickRate, options.RoundSeconds)
                             ?? options.RoundSeconds;
            steps.Add(step);
        }

        return steps;
    }

    /// <summary>
    ///     The demo-to-slot map (strat-model.md §3.5): our side's pawns at the freeze-end matched to <c>A..E</c> by
    ///     strat pin first, then the book default for the demo's epoch, then the remaining pawns to the remaining
    ///     slots in controller-slot order. Returned as the model names it, slot letter to SteamID64.
    /// </summary>
    /// <param name="ours">Our side's live pawns at the freeze-end.</param>
    /// <param name="pins">The strat's pinned slots, or null for a new strat.</param>
    /// <param name="bookDefaults">The book's slot defaults for the demo's epoch, slot letter to SteamID64 text.</param>
    public static IReadOnlyDictionary<char, ulong> SlotMap(IEnumerable<CapturedPawn> ours, IReadOnlyList<StratSlot>? pins,
        IReadOnlyDictionary<string, string>? bookDefaults)
    {
        ArgumentNullException.ThrowIfNull(ours);
        List<CapturedPawn> remaining = [.. ours.OrderBy(p => p.PlayerSlot).Take(StratVocabulary.Slots.Count)];
        Dictionary<char, ulong> map = [];

        void Claim(string slot, string? steamText)
        {
            if (map.ContainsKey(slot[0]) || !ulong.TryParse(steamText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
                                         || id == 0)
            {
                return;
            }

            int index = remaining.FindIndex(p => p.SteamId == id);
            if (index >= 0)
            {
                map[slot[0]] = id;
                remaining.RemoveAt(index);
            }
        }

        foreach (StratSlot pin in pins ?? [])
        {
            Claim(pin.Slot, pin.SteamId);
        }

        foreach (string slot in StratVocabulary.Slots)
        {
            Claim(slot, bookDefaults?.GetValueOrDefault(slot));
        }

        foreach (string slot in StratVocabulary.Slots)
        {
            if (!map.ContainsKey(slot[0]) && remaining.Count > 0)
            {
                map[slot[0]] = remaining[0].SteamId;
                remaining.RemoveAt(0);
            }
        }

        return map;
    }

    /// <summary>
    ///     Controller slot to token for the whole round: ours through <paramref name="slotMap" /> (a SteamID64 of 0, a
    ///     bot, takes the next unmapped pawn in slot order), theirs as <c>O1..O5</c> in controller-slot order.
    /// </summary>
    /// <param name="freezeEnd">The freeze-end's live pawns.</param>
    /// <param name="ourSide">2 or 3.</param>
    /// <param name="slotMap">The reviewed map.</param>
    public static Dictionary<int, string> Tokens(IEnumerable<CapturedPawn> freezeEnd, int ourSide, IReadOnlyDictionary<char, ulong> slotMap)
    {
        ArgumentNullException.ThrowIfNull(freezeEnd);
        ArgumentNullException.ThrowIfNull(slotMap);
        List<CapturedPawn> pawns = [.. freezeEnd.OrderBy(p => p.PlayerSlot)];
        List<CapturedPawn> ours = [.. pawns.Where(p => p.Team == ourSide)];
        Dictionary<int, string> tokens = [];
        foreach (string slot in StratVocabulary.Slots)
        {
            if (!slotMap.TryGetValue(slot[0], out ulong id))
            {
                continue;
            }

            CapturedPawn? pawn = ours.Where(p => !tokens.ContainsKey(p.PlayerSlot)).Select(p => (CapturedPawn?)p)
                .FirstOrDefault(p => id != 0 && p!.Value.SteamId == id)
                ?? ours.Where(p => !tokens.ContainsKey(p.PlayerSlot) && p.SteamId == 0).Select(p => (CapturedPawn?)p).FirstOrDefault();
            if (pawn is { } found)
            {
                tokens[found.PlayerSlot] = slot;
            }
        }

        int opponent = 0;
        foreach (CapturedPawn pawn in pawns.Where(p => p.Team != ourSide && p.Team is 2 or 3))
        {
            if (opponent < StratVocabulary.OpponentSlots.Count)
            {
                tokens[pawn.PlayerSlot] = StratVocabulary.OpponentSlots[opponent++];
            }
        }

        return tokens;
    }

    /// <summary>
    ///     A new strat from a reviewed capture: the steps, <c>origin</c>, the round length, and what Round Facts
    ///     knows when the demo has rows (our side's buy type as the economy, the plant site as the target). Not
    ///     saved; the caller commits it as revision 1.
    /// </summary>
    /// <param name="capture">The walk's result.</param>
    /// <param name="options">The review's answers.</param>
    /// <param name="owner">The book.</param>
    /// <param name="map">The map, the parser's spelling.</param>
    /// <param name="name">The strat's name.</param>
    /// <param name="origin">The demo and round it came from.</param>
    /// <param name="facts">The round's Round Facts row, when the demo has one.</param>
    /// <param name="nowUtc">The creation time.</param>
    public static StratDocument Document(RoundCapture capture, StratCaptureOptions options, StratOwner owner, string map, string name,
        StratOrigin origin, RoundFacts.RoundFacts? facts, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(origin);

        bool t = options.OurSide == 2;
        List<StratStep> steps = Steps(capture, options);
        string? site = SiteOf(facts, steps);
        string side = t ? StratVocabulary.SideT : StratVocabulary.SideCt;
        string type = !t ? "setup" : site is null ? "default" : "execute";

        StratDocument document = StratDocument.Create(Guid.NewGuid(), owner, map, side, type, name, nowUtc);
        // Only an execute is aimed at the site; a CT setup is not defined by where the other side planted.
        document.TargetSite = type == "execute" ? site : null;
        document.Origin = origin;
        document.Clock.RoundSeconds = options.RoundSeconds;
        document.Steps = steps;
        if (facts is not null)
        {
            SideFacts ours = t ? facts.T : facts.Ct;
            document.Economy = ours.BuyType.ToString().ToLowerInvariant();
        }

        return document;
    }

    // Round Facts' plant site when there are rows; else where the planter stood, since a pawn's place name on a
    // site is BombsiteA or BombsiteB on every competitive map.
    private static string? SiteOf(RoundFacts.RoundFacts? facts, List<StratStep> steps)
    {
        if (facts is { PlantTick: not null })
        {
            return facts.PlantSite switch
            {
                BombSite.A => "A",
                BombSite.B => "B",
                _ => null
            };
        }

        string? place = steps.FirstOrDefault(s => s.Verb == "plant")?.To?.Place;
        return place switch
        {
            not null when place.EndsWith("BombsiteA", StringComparison.OrdinalIgnoreCase) => "A",
            not null when place.EndsWith("BombsiteB", StringComparison.OrdinalIgnoreCase) => "B",
            _ => null
        };
    }

    private static StratStep FreezeEnd(CaptureMoment moment, StratCaptureOptions options, Dictionary<string, Keyed> last)
    {
        StratStep step = new() { Actor = StratVocabulary.ActorAll, Verb = "hold" };
        foreach (CapturedPawn pawn in moment.Pawns)
        {
            if (options.Tokens.TryGetValue(pawn.PlayerSlot, out string? token))
            {
                Add(step, token, pawn, options, last);
            }
        }

        step.Positions.Sort((a, b) => TokenOrder(a.Slot).CompareTo(TokenOrder(b.Slot)));
        return step;
    }

    private static StratStep? Throw(CaptureMoment moment, StratCaptureOptions options, Dictionary<string, Keyed> last)
    {
        // Ours only. A fire with no resolvable thrower has no side either; it is kept, with the actor left for
        // the user, rather than dropping what may be our own molotov.
        if (moment.ActorTeam != 0 && moment.ActorTeam != options.OurSide)
        {
            return null;
        }

        string? actor = options.Tokens.GetValueOrDefault(moment.ActorSlot);
        if (moment.ActorTeam == 0 && actor is not null && !StratVocabulary.Slots.Contains(actor))
        {
            return null;
        }

        CapturedPawn? thrower = moment.ActorSlot >= 0 ? moment.PawnIn(moment.ActorSlot) : null;
        double landingLevel = options.LevelMinZFor(moment.Position.Z);
        StratStep step = new()
        {
            Actor = actor is not null && StratVocabulary.Slots.Contains(actor) ? actor : StratVocabulary.ActorAll,
            Verb = "throw",
            From = thrower?.Place is { } from ? new PlaceRef { Place = from } : null,
            Utility = new UtilityRef
            {
                Kind = moment.UtilityKind ?? "smoke",
                Landing = new UtilityLanding
                {
                    X = Round(moment.Position.X),
                    Y = Round(moment.Position.Y),
                    LevelMinZ = landingLevel
                }
            }
        };

        if (thrower is { } pawn && actor is not null)
        {
            Add(step, actor, pawn, options, last);
        }

        AddMovers(step, moment, options, last);

        if (options.DrawThrowArrows && thrower is { } origin)
        {
            step.Strokes.Add(Arrow(origin, moment, options));
        }

        return step;
    }

    private static StratStep? Plant(CaptureMoment moment, StratCaptureOptions options, Dictionary<string, Keyed> last)
    {
        string? actor = options.Tokens.GetValueOrDefault(moment.ActorSlot);
        CapturedPawn? planter = moment.PawnIn(moment.ActorSlot);
        StratStep step = new()
        {
            Actor = actor is not null && StratVocabulary.Slots.Contains(actor) ? actor : StratVocabulary.ActorAll,
            Verb = "plant",
            To = planter?.Place is { } place ? new PlaceRef { Place = place } : null
        };

        if (planter is { } pawn && actor is not null)
        {
            Add(step, actor, pawn, options, last);
        }

        return step;
    }

    private static StratStep? Sweep(CaptureMoment moment, StratCaptureOptions options, Dictionary<string, Keyed> last)
    {
        StratStep step = new() { Verb = "move" };
        AddMovers(step, moment, options, last);
        if (step.Positions.Count == 0)
        {
            // Nobody moved far enough: a step with no entries would only be a pause marker.
            return null;
        }

        // One of ours moving is that slot's move; several are "all", headed where most of them went.
        List<StepPosition> own = [.. step.Positions.Where(p => StratVocabulary.Slots.Contains(p.Slot))];
        step.Actor = own.Count == 1 ? own[0].Slot : StratVocabulary.ActorAll;
        string? to = own
            .Select(p => moment.Pawns.FirstOrDefault(c => options.Tokens.GetValueOrDefault(c.PlayerSlot) == p.Slot).Place)
            .Where(p => !string.IsNullOrEmpty(p))
            .GroupBy(p => p, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        step.To = to is null ? null : new PlaceRef { Place = to };
        return step;
    }

    private static void AddMovers(StratStep step, CaptureMoment moment, StratCaptureOptions options, Dictionary<string, Keyed> last)
    {
        foreach (CapturedPawn pawn in moment.Pawns)
        {
            if (!options.Tokens.TryGetValue(pawn.PlayerSlot, out string? token)
                || step.Positions.Any(p => p.Slot == token)
                || !Moved(last.GetValueOrDefault(token), pawn, options))
            {
                continue;
            }

            Add(step, token, pawn, options, last);
        }

        step.Positions.Sort((a, b) => TokenOrder(a.Slot).CompareTo(TokenOrder(b.Slot)));
    }

    // Further than the threshold on the map, or onto another floor at the same spot: either is a move a
    // reader of the strat has to see.
    private static bool Moved(Keyed? previous, CapturedPawn pawn, StratCaptureOptions options)
    {
        if (previous is not { } from)
        {
            return true;
        }

        double dx = pawn.X - from.X, dy = pawn.Y - from.Y;
        return dx * dx + dy * dy > MoveThreshold * MoveThreshold || options.LevelMinZFor(pawn.Z) != from.LevelMinZ;
    }

    private static void Add(StratStep step, string token, CapturedPawn pawn, StratCaptureOptions options, Dictionary<string, Keyed> last)
    {
        double level = options.LevelMinZFor(pawn.Z);
        step.Positions.Add(new StepPosition
        {
            Slot = token,
            X = Round(pawn.X),
            Y = Round(pawn.Y),
            LevelMinZ = level,
            YawDegrees = Round(NormalizeYaw(pawn.Yaw))
        });
        last[token] = new Keyed(pawn.X, pawn.Y, level);
    }

    // The "draw throw arrows" ink: a world-space arrow from the grenade's real release point (#56, #59) when the
    // sampler named one, else from where the thrower stands at the step, to the landing, on the thrower's floor,
    // in the stored stroke shape the canvas reads back.
    private static JsonObject Arrow(CapturedPawn thrower, CaptureMoment moment, StratCaptureOptions options)
    {
        Vector3 origin = moment.ThrowOrigin ?? new Vector3(thrower.X, thrower.Y, thrower.Z);
        AnnotationElement arrow = new(Guid.NewGuid(), AnnotationKind.Arrow, ArrowStyle,
            new SpaceRef.World(options.LevelMinZFor(thrower.Z)), TimeEnvelope.Static,
            [new InkPoint(origin.X, origin.Y, 0.5f), new InkPoint(moment.Position.X, moment.Position.Y, 0.5f)], null);
        return StratStrokes.ToJson(arrow);
    }

    private static int TokenOrder(string slot)
    {
        int own = StratVocabulary.Slots.ToList().IndexOf(slot);
        return own >= 0 ? own : StratVocabulary.Slots.Count + StratVocabulary.OpponentSlots.ToList().IndexOf(slot);
    }

    private static double NormalizeYaw(double yaw)
    {
        double wrapped = yaw % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }

    // Two decimals, the canvas's own rounding for what it writes.
    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private readonly record struct Keyed(float X, float Y, double LevelMinZ);
}
