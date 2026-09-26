#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Playback2D.Pipeline.Frames;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>What made Create Strat From Round stop at a tick (step-authoring.md §3.9).</summary>
public enum CaptureTrigger
{
    /// <summary>The round's freeze-end: every live pawn on both sides.</summary>
    FreezeEnd,

    /// <summary>A smoke, flash or HE detonation, a fire starting or a decoy starting.</summary>
    Utility,

    /// <summary><c>bomb_planted</c>.</summary>
    Plant,

    /// <summary>The 10 s move sweep, where no other trigger is within 3 s.</summary>
    Sweep
}

/// <summary>
///     One live pawn at a captured tick: the values a keyframe needs, copied out of the tracker so nothing
///     pooled is retained. Dead pawns are never captured, and neither are orphans: the walk reads through the
///     controller's live pawn, never by enumerating the pawn class (§3.9's finding).
/// </summary>
/// <param name="PlayerSlot">The controller slot.</param>
/// <param name="Team">2 for T, 3 for CT.</param>
/// <param name="SteamId">The controller's SteamID64, or 0.</param>
/// <param name="Name">The controller's name, raw; sanitised at the render boundary.</param>
/// <param name="X">Cell-reconstructed world X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z, which picks the level.</param>
/// <param name="Yaw"><c>m_angEyeAngles.Y</c>.</param>
/// <param name="Place"><c>m_szLastPlaceName</c>, with the wire's empty string as null.</param>
public readonly record struct CapturedPawn(
    int PlayerSlot, int Team, ulong SteamId, string? Name, float X, float Y, float Z, float Yaw, string? Place);

/// <summary>One tick the capture stopped at and the live pawns there.</summary>
/// <param name="Tick">Frame clock: the trigger's own tick, not the frame's.</param>
/// <param name="Trigger">Why it stopped.</param>
/// <param name="Pawns">Every live pawn at the first frame at or after <paramref name="Tick" />, in slot order.</param>
/// <param name="UtilityKind">For a utility trigger, the strat's spelling: smoke, flash, he, molotov or decoy.</param>
/// <param name="ActorSlot">The thrower or planter's controller slot, or -1 when the wire does not name one.</param>
/// <param name="ActorTeam">The actor's team, or 0 when it is unknown.</param>
/// <param name="Position">Where the utility went off; zero for other triggers.</param>
/// <param name="ThrowOrigin">
///     The grenade's release point (<c>m_vInitialPosition</c>), when <see cref="ProjectileThrowerMatch" /> found the
///     projectile behind this detonation; null when none matched, and the thrower's position at the stop stands in.
/// </param>
public sealed record CaptureMoment(
    int Tick,
    CaptureTrigger Trigger,
    IReadOnlyList<CapturedPawn> Pawns,
    string? UtilityKind = null,
    int ActorSlot = -1,
    int ActorTeam = 0,
    Vector3 Position = default,
    Vector3? ThrowOrigin = null)
{
    /// <summary>The live pawn in a slot at this tick, or null.</summary>
    /// <param name="playerSlot">A controller slot.</param>
    public CapturedPawn? PawnIn(int playerSlot)
    {
        foreach (CapturedPawn pawn in Pawns)
        {
            if (pawn.PlayerSlot == playerSlot)
            {
                return pawn;
            }
        }

        return null;
    }
}

/// <summary>
///     One round as Create Strat From Round reads it: the ticks it stopped at, in tick order, the first of them
///     the freeze-end. Side-neutral: which side is "ours" and which slot is which is decided afterwards, in the
///     review, so changing the side picker never walks the demo again.
/// </summary>
/// <param name="Round"><c>ClipRound.Number</c>.</param>
/// <param name="FreezeEndTick">The round's zero, frame clock.</param>
/// <param name="LiveEndTick">The tick the round was decided at (exclusive); nothing at or after it is captured.</param>
/// <param name="TickRate">The demo's tick rate.</param>
/// <param name="Moments">Every stop, tick order, freeze-end first.</param>
public sealed record RoundCapture(int Round, int FreezeEndTick, int LiveEndTick, int TickRate, IReadOnlyList<CaptureMoment> Moments)
{
    /// <summary>The freeze-end stop.</summary>
    public CaptureMoment FreezeEnd => Moments[0];
}

/// <summary>
///     The tracker walk behind Create Strat From Round (step-authoring.md §3.9): a private
///     <see cref="EntityTracker" /> seeded at the round's freeze-end and stepped once to the round's last stop,
///     off the UI thread. It never touches the shared playback clock, the rule every export keeps (design §5.7).
///     <para>
///         The stops are known before the walk except the round's end: a Valve matchmaking demo carries no
///         <c>round_end</c>, so the end is the game rules' <c>m_iRoundWinStatus</c> turning non-zero, else
///         <c>round_officially_ended</c>, else the next freeze-end. Pawns respawn in the next round's buy time,
///         which is still inside this round's window, and a sweep there would pull every token back to spawn.
///     </para>
///     <para>
///         A round with a utility stop also runs a bounded <see cref="ProjectileSampler" /> pass over the same
///         window (#56, #59), so a fire or a decoy names its thrower from the projectile that caused it rather
///         than the live entity join, and every throw names its release point for the strat's arrow.
///     </para>
/// </summary>
public static class RoundCaptureWalker
{
    /// <summary>The move sweep's period (O-25).</summary>
    public const int SweepSeconds = 10;

    /// <summary>A sweep is skipped when another stop is this close.</summary>
    public const int SweepQuietSeconds = 3;

    // Controllers sit at entity index slot + 1; no GOTV demo seats more.
    private const int MaxSlots = 64;

    private const string RoundWinStatusField = "m_pGameRules.m_iRoundWinStatus";

    /// <summary>Walks one round. Blocking and CPU-bound: call it off the UI thread.</summary>
    /// <param name="demo">The parsed demo, read only.</param>
    /// <param name="round"><c>ClipRound.Number</c>.</param>
    /// <param name="freezeEndTick">The round's freeze-end, frame clock.</param>
    /// <param name="windowEndTick">The next round's freeze-end, or null for the last round.</param>
    /// <param name="knownEndTick">Round Facts' <c>EndTick</c> when the demo has rows; the walk's own end otherwise.</param>
    /// <param name="progress">Fraction of the walk done, for the review's progress line.</param>
    /// <param name="ct">Cancels between frames.</param>
    public static RoundCapture Walk(ParsedDemo demo, int round, int freezeEndTick, int? windowEndTick, int? knownEndTick = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demo);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        int tickRate = demo.TickRate > 0 ? demo.TickRate : 64;
        int lastTick = frames.Count > 0 ? frames[^1].ServerTick : freezeEndTick;
        int windowEnd = windowEndTick ?? lastTick + 1;

        // Round Facts' end is the decision instant, the same signal the walk would look for; with it the walk
        // does not need the rules entity at all.
        int end = knownEndTick is { } known && known > freezeEndTick ? Math.Min(known, windowEnd) : windowEnd;
        foreach (GameEvent fire in demo.AllGameEvents)
        {
            if (fire.Payload is RoundOfficiallyEndedEvent && fire.GameTick > freezeEndTick && fire.GameTick < end)
            {
                end = fire.GameTick;
            }
        }

        List<Stop> stops = [new(freezeEndTick, CaptureTrigger.FreezeEnd, null)];
        stops.AddRange(EventStops(demo, freezeEndTick, end));
        stops.AddRange(SweepStops(stops, freezeEndTick, end, tickRate));
        stops.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Trigger.CompareTo(b.Trigger));

        List<CaptureMoment> moments = [];
        int firstFrame = FrameAtOrAfter(frames, freezeEndTick);
        if (firstFrame < 0)
        {
            return new RoundCapture(round, freezeEndTick, end, tickRate, [new CaptureMoment(freezeEndTick, CaptureTrigger.FreezeEnd, [])]);
        }

        EntityTracker tracker = new EntitySeekService(static () => new EntityTracker()).SeekToFrameNoSnapshot(firstFrame, frames).Tracker;
        TrackerSceneSnapshot snapshot = new();
        int cursor = firstFrame;
        int lastStopFrame = FrameAtOrAfter(frames, stops[^1].Tick);
        int lastFrame = Math.Max(firstFrame, lastStopFrame >= 0 ? lastStopFrame : frames.Count - 1);

        // A second tracker pass over the same window (#56, #59): only when a utility stop needs a thrower or
        // a release point, and bounded to this round rather than the whole demo (ProjectileSampler has no late
        // start, so frames before firstFrame are unavoidable, but the tail past the round is skipped).
        List<ProjectileSample> projectiles = stops.Any(s => s.Trigger == CaptureTrigger.Utility)
            ? [.. ProjectileSampler.Walk(demo, frameStride: 8, maxFrames: lastFrame + 1).Where(s => s.Removed)]
            : [];

        foreach (Stop stop in stops)
        {
            if (stop.Tick >= end)
            {
                break;
            }

            int frame = Math.Max(firstFrame, FrameAtOrAfter(frames, stop.Tick));
            if (frame < 0)
            {
                break;
            }

            bool decided = false;
            while (cursor < frame)
            {
                ct.ThrowIfCancellationRequested();
                tracker.AdvanceOneFrame(frames[++cursor]);

                // Only knownEndTick's absence makes the rules worth reading; one field per frame.
                if (knownEndTick is null && cursor > firstFrame && RoundDecided(tracker))
                {
                    end = Math.Min(end, frames[cursor].ServerTick);
                    decided = stop.Tick >= end;
                    if (decided)
                    {
                        break;
                    }
                }
            }

            if (decided)
            {
                break;
            }

            progress?.Report(lastFrame > firstFrame ? (double)(cursor - firstFrame) / (lastFrame - firstFrame) : 1);
            snapshot.Refresh(tracker);
            List<CapturedPawn> pawns = ReadPawns(tracker, snapshot);
            moments.Add(stop.Trigger switch
            {
                CaptureTrigger.Utility or CaptureTrigger.Plant => Resolve(stop, tracker, pawns, projectiles),
                _ => new CaptureMoment(stop.Tick, stop.Trigger, pawns)
            });
        }

        progress?.Report(1);
        return new RoundCapture(round, freezeEndTick, end, tickRate, moments);
    }

    /// <summary>The first frame whose tick is at or after <paramref name="tick" />, or -1 past the last.</summary>
    /// <param name="frames">The frame list, tick-ordered.</param>
    /// <param name="tick">Frame clock.</param>
    public static int FrameAtOrAfter(IReadOnlyList<DemoFrame> frames, int tick)
    {
        ArgumentNullException.ThrowIfNull(frames);
        int lo = 0, hi = frames.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].ServerTick >= tick)
            {
                found = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return found;
    }

    /// <summary>
    ///     The sweep ticks: every <see cref="SweepSeconds" /> from the freeze-end, before the end, except where
    ///     another stop is within <see cref="SweepQuietSeconds" />. Pure, so the rule is tested without a demo.
    /// </summary>
    /// <param name="otherTicks">Every non-sweep stop's tick.</param>
    /// <param name="freezeEndTick">The round's zero.</param>
    /// <param name="endTick">Exclusive.</param>
    /// <param name="tickRate">Ticks per second.</param>
    public static IReadOnlyList<int> SweepTicks(IEnumerable<int> otherTicks, int freezeEndTick, int endTick, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(otherTicks);
        int[] others = [.. otherTicks];
        int quiet = SweepQuietSeconds * tickRate;
        List<int> sweeps = [];
        for (int tick = freezeEndTick + SweepSeconds * tickRate; tick < endTick; tick += SweepSeconds * tickRate)
        {
            if (!others.Any(o => Math.Abs(o - tick) <= quiet))
            {
                sweeps.Add(tick);
            }
        }

        return sweeps;
    }

    private static IEnumerable<Stop> SweepStops(List<Stop> stops, int freezeEndTick, int end, int tickRate) =>
        SweepTicks(stops.Select(s => s.Tick), freezeEndTick, end, tickRate).Select(t => new Stop(t, CaptureTrigger.Sweep, null));

    // Every smoke, flash, HE, fire and decoy, and the plant, inside the live window. Both sides: the review
    // picks ours afterwards.
    private static List<Stop> EventStops(ParsedDemo demo, int freezeEndTick, int end)
    {
        List<Stop> stops = [];
        foreach (GameEvent fire in demo.AllGameEvents)
        {
            int tick = fire.GameTick;
            if (tick < freezeEndTick || tick >= end)
            {
                continue;
            }

            Stop? stop = fire.Payload switch
            {
                SmokeGrenadeDetonateEvent e => new Stop(tick, CaptureTrigger.Utility, new Source("smoke", e.UserId, -1, -1, new Vector3(e.X, e.Y, e.Z))),
                FlashbangDetonateEvent e => new Stop(tick, CaptureTrigger.Utility, new Source("flash", e.UserId, -1, -1, new Vector3(e.X, e.Y, e.Z))),
                HegrenadeDetonateEvent e => new Stop(tick, CaptureTrigger.Utility, new Source("he", e.UserId, -1, -1, new Vector3(e.X, e.Y, e.Z))),

                // No thrower on the wire; the inferno entity's owner may still name one (resolved at the stop).
                InfernoStartburnEvent e => new Stop(tick, CaptureTrigger.Utility, new Source("molotov", -1, e.EntityId, -1, new Vector3(e.X, e.Y, e.Z))),

                // UserId here is a pawn handle rather than a slot (DetonationEvents); resolved at the stop. A
                // value small enough to be a slot is read as one, should a build ever send it that way.
                DecoyStartedEvent e => new Stop(tick, CaptureTrigger.Utility, e.UserId < MaxSlots
                    ? new Source("decoy", (int)e.UserId, -1, -1, new Vector3(e.X, e.Y, e.Z))
                    : new Source("decoy", -1, -1, e.UserId, new Vector3(e.X, e.Y, e.Z))),
                BombPlantedEvent e => new Stop(tick, CaptureTrigger.Plant, new Source(null, e.UserId, -1, -1, default)),
                _ => null
            };
            if (stop is not null)
            {
                stops.Add(stop);
            }
        }

        return stops;
    }

    // The actor's slot and team at the stop: the projectile sampler's match first (#56, #59, survives the
    // thrower's death), else the event's own slot, else the inferno's owner or the decoy's pawn handle joined
    // to a slot through the live pawns. The same match also names the release point for the throw arrow.
    private static CaptureMoment Resolve(Stop stop, EntityTracker tracker, List<CapturedPawn> pawns, IReadOnlyList<ProjectileSample> projectiles)
    {
        Source source = stop.Source!;
        int slot = source.Slot;

        ProjectileSample? matched = ProjectileClassFor(source.Kind) is { } className
            ? ProjectileThrowerMatch.Nearest(projectiles, className, stop.Tick, source.Position)
            : null;

        if (slot < 0 && matched is { ThrowerSlot: >= 0 } m)
        {
            slot = m.ThrowerSlot;
        }

        if (slot < 0 && source.PawnHandle != -1)
        {
            slot = SlotOfPawnIndex(tracker, PawnLookup.IndexOf((uint)source.PawnHandle));
        }
        else if (slot < 0 && source.EntityIndex > 0
                 && tracker.CurrentEntities[source.EntityIndex] is { } inferno
                 && PawnLookup.TryReadHandle(inferno, "m_hOwnerEntity", out uint owner))
        {
            slot = SlotOfPawnIndex(tracker, PawnLookup.IndexOf(owner));
        }

        int team = 0;
        foreach (CapturedPawn pawn in pawns)
        {
            if (pawn.PlayerSlot == slot)
            {
                team = pawn.Team;
            }
        }

        if (slot >= 0 && team == 0 && tracker.CurrentEntities[slot + 1] is { } controller)
        {
            // A thrower who died before the grenade went off has no live pawn; the controller keeps the team.
            team = CoerceInt(controller["m_iTeamNum"]);
        }

        return new CaptureMoment(stop.Tick, stop.Trigger, pawns, source.Kind, slot, team, source.Position, matched?.InitialPosition);
    }

    // The strat's utility spelling (Source.Kind) to the projectile class it flies as, or null for a plant.
    private static string? ProjectileClassFor(string? kind) => kind switch
    {
        "smoke" => GrenadeProjectileClasses.Smoke,
        "flash" => GrenadeProjectileClasses.Flashbang,
        "he" => GrenadeProjectileClasses.HEGrenade,
        "molotov" => GrenadeProjectileClasses.Molotov,
        "decoy" => GrenadeProjectileClasses.Decoy,
        _ => null
    };

    private static int SlotOfPawnIndex(EntityTracker tracker, int pawnIndex)
    {
        if (pawnIndex <= 0 || tracker.CurrentEntities[pawnIndex] is not { } target)
        {
            return -1;
        }

        int found = -1;
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
        {
            if (ReferenceEquals(pawn, target))
            {
                found = slot;
            }
        });
        return found;
    }

    private static List<CapturedPawn> ReadPawns(EntityTracker tracker, TrackerSceneSnapshot snapshot)
    {
        List<CapturedPawn> pawns = [];
        foreach (IPlayerState player in snapshot.Players)
        {
            // Through the controller's live pawn only: SceneFrameBuilder.BuildMarkers' join, which is what keeps
            // the orphaned pawns the design measured out of every keyframe. Alive is the engine's own rule,
            // re-read off the tracker rather than kept as a local copy (#58).
            if (!player.HasLivePawn || player.Pawn is not { } pawn || player.WorldPosition is not { } world
                || player.Team is not (2 or 3)
                || PawnLookup.ResolvePawn(tracker, player.Slot) is not { } resolved || !PawnLookup.IsAlive(resolved))
            {
                continue;
            }

            float yaw = pawn.TryGet("m_angEyeAngles", out Vector3 eye) ? eye.Y : 0;
            string? place = pawn.TryGet("m_szLastPlaceName", out string? name) && !string.IsNullOrEmpty(name) ? name : null;
            string? playerName = player.Controller?["m_iszPlayerName"] as string;
            pawns.Add(new CapturedPawn(player.Slot, player.Team, snapshot.SteamIdForSlot(player.Slot), playerName,
                world.X, world.Y, world.Z, yaw, place));
        }

        return pawns;
    }

    private static bool RoundDecided(EntityTracker tracker)
    {
        foreach (EntityState rules in tracker.CurrentEntities.OfClass("CCSGameRulesProxy"))
        {
            return CoerceInt(rules[RoundWinStatusField]) != 0;
        }

        return false;
    }

    private static int CoerceInt(object? value) => value switch
    {
        int i => i,
        uint u => (int)u,
        short s => s,
        ushort u => u,
        long l => (int)l,
        ulong u => (int)u,
        byte b => b,
        sbyte s => s,
        _ => 0
    };

    private sealed record Stop(int Tick, CaptureTrigger Trigger, Source? Source);

    private sealed record Source(string? Kind, int Slot, int EntityIndex, long PawnHandle, Vector3 Position);
}
