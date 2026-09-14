#region

using System.Numerics;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     An independent replay of the counter-strafe ADMISSION gate, written so the lookback window
///     can be swept instead of assumed.
///     <para>
///         <b>Why this exists.</b> The gate admits a shot into the counter-strafing denominator when
///         the shooter exceeded <c>0.34 * m_flMaxspeed</c> at some sampled tick inside a lookback
///         window before firing. That window is the whole metric: it sets the denominator, and the
///         engine ships one value for it, so a test that only reads the engine's output can examine
///         exactly one candidate. This fold produces the whole curve from a single pass, which is
///         what showed the curve has no knee to fit to and sent the window to a physical derivation
///         instead.
///     </para>
///     <para>
///         <b>How one pass gives every window.</b> Peak speed over a window is monotone in the
///         window, so a shot is admitted at window W exactly when the most recent tick at which the
///         shooter was above their threshold is no more than W ticks back. Recording that one number
///         per shot (<see cref="AdmissionShot.MinWindowTicks" />) turns the sweep into a comparison
///         rather than a re-run, and it is why fitting the window costs one parse and not one parse
///         per candidate.
///     </para>
///     <para>
///         <b>Where this is an approximation, and why that is stated rather than hidden.</b> The
///         engine samples speed off the digest's per-pawn position columns, gated on the pawn being
///         a live, team-assigned viewer that tick; this samples every pawn the tracker reports with
///         a controller. Both reconstruct position through
///         <see cref="PositionUtil.CellToWorld" /> and difference it the same way (same maximum
///         sample gap, same teleport cap, same repeat-tick rule), so the two agree wherever the
///         liveness gates agree. <c>CounterStrafeAdmissionFoldTests</c> pins that agreement at the
///         shipped window before it trusts the curve at any other one: an oracle nobody checked
///         against the thing it describes is just a second opinion.
///     </para>
/// </summary>
public static class CounterStrafeAdmissionFold
{
    /// <summary>
    ///     Largest gap, in ticks, a position delta may be differenced across. A wider gap means the
    ///     pawn was absent from the sample set in between (dead, dormant, or the demo paused), and
    ///     the straight line across that hole is not a speed. Mirrors the engine's own default;
    ///     <c>CounterStrafeAdmissionFoldTests</c> pins the pair together.
    /// </summary>
    public const int MaxSpeedSampleGapTicks = 8;

    /// <summary>
    ///     Implied horizontal speed above which a delta is read as a teleport and reported as zero.
    ///     A round restart relocates a player between two sampled ticks, and an uncapped derivative
    ///     turns that into a five-figure spike that satisfies every "was moving" gate.
    /// </summary>
    public const float TeleportSpeedThreshold = 500f;

    private static readonly string _maxSpeedPath =
        SchemaNames.CBasePlayerPawn.MovementServices + "." + SchemaNames.CPlayerMovementServices.MaxSpeed;

    /// <summary>
    ///     Replays the demo and records, for every bullet shot taken during a live round, how far
    ///     back the shooter last exceeded their movement threshold.
    /// </summary>
    /// <param name="demo">The parsed demo.</param>
    /// <param name="maxLookbackTicks">
    ///     Widest window the sweep will ask about. Sets the depth of the speed history kept per
    ///     slot, so a shot whose last movement is further back than this reports
    ///     <see cref="AdmissionShot.NotAdmittedWithinRange" /> rather than a wrong number.
    /// </param>
    /// <returns>The fold.</returns>
    public static AdmissionFoldResult Fold(ParsedDemo demo, int maxLookbackTicks)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLookbackTicks, 1);

        double tickRate = demo.TickRate > 0 ? demo.TickRate : 64.0;
        int historyWidth = maxLookbackTicks + 1;

        Dictionary<int, SlotSpeedHistory> histories = [];
        Dictionary<int, float> maxSpeedPreFrame = [];
        List<AdmissionShot> shots = [];

        // Events carry the index of the frame they arrived on, so one pass over the frames can
        // interleave entity state and events without a second ordering pass.
        Dictionary<int, List<GameEvent>> eventsByFrame = [];
        foreach (GameEvent gameEvent in demo.AllGameEvents)
        {
            if (gameEvent.FrameNumber < 0 || gameEvent.FrameNumber >= demo.Frames.Count)
            {
                continue;
            }

            if (gameEvent.Payload is not (WeaponFireEvent or RoundFreezeEndEvent or RoundEndEvent
                or BeginNewMatchEvent))
            {
                continue;
            }

            if (!eventsByFrame.TryGetValue(gameEvent.FrameNumber, out List<GameEvent>? bucket))
            {
                bucket = [];
                eventsByFrame[gameEvent.FrameNumber] = bucket;
            }

            bucket.Add(gameEvent);
        }

        EntityTracker tracker = new();
        bool matchStarted = false;
        bool roundActive = false;
        int unmeasurableShots = 0;
        int warmupShots = 0;

        for (int frameIndex = 0; frameIndex < demo.Frames.Count; frameIndex++)
        {
            DemoFrame frame = demo.Frames[frameIndex];

            // Pre-frame, matching the engine: this frame's entity updates have already landed by the
            // time an event on it dispatches, so the live value already carries this shot's own
            // weapon swap. Frame-start state is the cap the player was actually moving under.
            if (eventsByFrame.ContainsKey(frameIndex))
            {
                SnapshotMaxSpeed(tracker, maxSpeedPreFrame);
            }

            tracker.AdvanceOneFrame(frame);
            SampleSpeeds(tracker, histories, frame.ServerTick, tickRate, historyWidth);

            if (!eventsByFrame.TryGetValue(frameIndex, out List<GameEvent>? frameEvents))
            {
                continue;
            }

            foreach (GameEvent gameEvent in frameEvents)
            {
                switch (gameEvent.Payload)
                {
                    case BeginNewMatchEvent:
                        matchStarted = true;
                        continue;
                    case RoundFreezeEndEvent:
                        roundActive = true;
                        continue;
                    case RoundEndEvent:
                        roundActive = false;
                        continue;
                    case WeaponFireEvent fire:
                        // The same gate the ruleset applies: bullets only, live rounds only. A
                        // grenade or a knife carries no movement penalty at all, so admitting one
                        // would widen the denominator with shots the metric says nothing about and
                        // quietly flatten the curve.
                        if (!WeaponClassification.IsBulletWeapon(fire.Weapon) || fire.UserId < 0)
                        {
                            continue;
                        }

                        if (!matchStarted || !roundActive)
                        {
                            warmupShots++;
                            continue;
                        }

                        shots.Add(Resolve(fire.UserId, frame.ServerTick, maxSpeedPreFrame, histories,
                            maxLookbackTicks, ref unmeasurableShots));
                        continue;
                    default:
                        continue;
                }
            }
        }

        Dictionary<int, string> names = new();
        foreach ((int slot, PlayerInfo info) in demo.Players)
        {
            names[slot] = info.Name;
        }

        return new AdmissionFoldResult(
            tickRate, maxLookbackTicks, unmeasurableShots, warmupShots, names, shots);
    }

    private static AdmissionShot Resolve(
        int slot,
        int tick,
        Dictionary<int, float> maxSpeedPreFrame,
        Dictionary<int, SlotSpeedHistory> histories,
        int maxLookbackTicks,
        ref int unmeasurable)
    {
        // No readable movement cap means no threshold, and the engine drops such a shot from the
        // denominator rather than admitting it on zeros that read as a perfectly still player. The
        // fold has to make the same call or its denominator is not the engine's.
        if (!maxSpeedPreFrame.TryGetValue(slot, out float maxSpeed) || maxSpeed <= 0f
            || !histories.TryGetValue(slot, out SlotSpeedHistory? history))
        {
            unmeasurable++;
            return new AdmissionShot(slot, tick, AdmissionShot.NotAdmittedWithinRange, false, 0f, 0f);
        }

        float threshold = maxSpeed * AimShotContextEdge.CounterStrafeSpeedFraction;
        bool sampledAtShot = history.TrySpeedAt(tick, out float speedAtShot);
        return new AdmissionShot(
            slot,
            tick,
            history.TicksSinceAbove(tick, threshold, maxLookbackTicks),
            sampledAtShot,
            speedAtShot,
            threshold);
    }

    private static void SnapshotMaxSpeed(EntityTracker tracker, Dictionary<int, float> into)
    {
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
        {
            if (pawn[_maxSpeedPath] is { } cell && TryFloat(cell, out float value))
            {
                into[slot] = value;
            }
        });
    }

    private static void SampleSpeeds(
        EntityTracker tracker,
        Dictionary<int, SlotSpeedHistory> histories,
        int tick,
        double tickRate,
        int historyWidth)
    {
        PawnLookup.ForEachLivePawn(tracker, (slot, pawn) =>
        {
            if (PositionUtil.CellToWorld(pawn) is not { } position)
            {
                return; // No reconstructable origin yet (pre-spawn or dormant).
            }

            if (!histories.TryGetValue(slot, out SlotSpeedHistory? history))
            {
                histories[slot] = history = new SlotSpeedHistory(historyWidth);
            }

            history.Advance(tick, position, tickRate);
        });
    }

    private static bool TryFloat(object cell, out float value)
    {
        switch (cell)
        {
            case float f:
                value = f;
                return true;
            case double d:
                value = (float)d;
                return true;
            case int i:
                value = i;
                return true;
            default:
                value = 0f;
                return false;
        }
    }

    /// <summary>
    ///     One slot's tick-stamped speed history. Stamped rather than indexed by a moving head
    ///     because ticks repeat (several frames can carry one server tick), skip, and occasionally
    ///     go backwards, none of which a monotonic cursor survives; a stale stamp simply falls
    ///     outside every query window.
    /// </summary>
    private sealed class SlotSpeedHistory
    {
        private readonly float[] _speeds;
        private readonly int[] _ticks;
        private bool _hasAnchor;
        private float _lastSpeed;
        private int _lastTick;
        private Vector3 _lastPosition;

        internal SlotSpeedHistory(int width)
        {
            _speeds = new float[width];
            _ticks = new int[width];

            // int.MinValue, not 0: tick 0 is a real tick, and a zero-filled stamp array would make
            // every slot claim a sample there.
            Array.Fill(_ticks, int.MinValue);
        }

        internal void Advance(int tick, Vector3 position, double tickRate)
        {
            if (_hasAnchor && tick == _lastTick)
            {
                // Several frames can carry the same server tick. Re-differencing across a zero gap
                // would report the player as stationary on the second frame of every repeated tick.
                Stamp(tick, _lastSpeed);
                return;
            }

            float speed = 0f;
            int gap = tick - _lastTick;
            if (_hasAnchor && gap > 0 && gap <= MaxSpeedSampleGapTicks)
            {
                float dx = position.X - _lastPosition.X;
                float dy = position.Y - _lastPosition.Y;
                float candidate = MathF.Sqrt((dx * dx) + (dy * dy)) / (float)(gap / tickRate);
                speed = candidate <= TeleportSpeedThreshold ? candidate : 0f;
            }

            _lastPosition = position;
            _lastTick = tick;
            _lastSpeed = speed;
            _hasAnchor = true;
            Stamp(tick, speed);
        }

        /// <summary>
        ///     Ticks back to the most recent sample above <paramref name="threshold" />, or
        ///     <see cref="AdmissionShot.NotAdmittedWithinRange" /> when there is none inside
        ///     <paramref name="maxLookbackTicks" />. Strictly above, matching the engine: the
        ///     threshold is the line at which the server STARTS charging inaccuracy.
        /// </summary>
        internal int TicksSinceAbove(int tick, float threshold, int maxLookbackTicks)
        {
            int best = AdmissionShot.NotAdmittedWithinRange;
            int oldest = tick - maxLookbackTicks;
            for (int i = 0; i < _ticks.Length; i++)
            {
                int stamped = _ticks[i];
                if (stamped < oldest || stamped > tick || _speeds[i] <= threshold)
                {
                    continue;
                }

                int distance = tick - stamped;
                if (best == AdmissionShot.NotAdmittedWithinRange || distance < best)
                {
                    best = distance;
                }
            }

            return best;
        }

        /// <summary>
        ///     The speed sampled at exactly this tick, which is the engine's <c>Speed2D</c>: the
        ///     "was the player still WHEN they fired" half, as opposed to the lookback's "had they
        ///     been moving recently".
        /// </summary>
        internal bool TrySpeedAt(int tick, out float speed)
        {
            int index = (int)((uint)tick % (uint)_speeds.Length);
            speed = _ticks[index] == tick ? _speeds[index] : 0f;
            return _ticks[index] == tick;
        }

        private void Stamp(int tick, float speed)
        {
            int index = (int)((uint)tick % (uint)_speeds.Length);
            _speeds[index] = speed;
            _ticks[index] = tick;
        }
    }
}

/// <summary>One bullet shot, and the narrowest lookback window that would admit it.</summary>
/// <param name="Slot">The shooter.</param>
/// <param name="Tick">The frame tick the shot dispatched on, which is the clock speeds are sampled on.</param>
/// <param name="MinWindowTicks">
///     Ticks back to the shooter's most recent sample above their threshold, so the shot is admitted
///     at every window at least this wide. <see cref="NotAdmittedWithinRange" /> when there is no
///     such sample inside the swept range, or when the movement cap could not be read at all.
/// </param>
/// <param name="HasMovementSample">
///     Whether the shot had a readable movement cap. A shot without one is dropped from the
///     denominator entirely, exactly as the engine drops it, rather than counted as standing still.
/// </param>
/// <param name="SpeedAtShot">Derived 2D speed on the shot's own tick, in units per second.</param>
/// <param name="Threshold"><c>0.34 * m_flMaxspeed</c> at the shot, the line the server charges movement inaccuracy above.</param>
public sealed record AdmissionShot(
    int Slot,
    int Tick,
    int MinWindowTicks,
    bool HasMovementSample,
    float SpeedAtShot,
    float Threshold)
{
    /// <summary>Sentinel for "no qualifying sample inside the swept range".</summary>
    public const int NotAdmittedWithinRange = -1;

    /// <summary>
    ///     Whether the shot was taken from a standstill by the engine's own line: the speed AT the
    ///     shot was below the threshold. Independent of admission, which asks whether the player was
    ///     moving in the window BEFORE it. A clean counter-strafe is both.
    ///     <para>
    ///         <b>This is the speed comparison only.</b> The engine's <c>CounterStrafeGood</c> prefers
    ///         the server's own movement penalty where the event carries one and falls back to this
    ///         comparison; the fired arm never carries one, which is why reproducing the fired-arm
    ///         counters needs only the fallback. It also means the fold's count of NOT-clean shots is
    ///         window-invariant by this record's own construction, so agreement with the engine on
    ///         that count confirms the two agree and is not independent evidence about the window:
    ///         the engine's own run is what carries that claim.
    ///     </para>
    /// </summary>
    public bool CleanAtShot => HasMovementSample && SpeedAtShot < Threshold;

    /// <summary>Whether a window of <paramref name="windowTicks" /> admits this shot.</summary>
    /// <param name="windowTicks">The candidate window, in ticks.</param>
    /// <returns><c>true</c> when the shot enters the counter-strafing denominator.</returns>
    public bool AdmittedAt(int windowTicks) =>
        MinWindowTicks != NotAdmittedWithinRange && MinWindowTicks <= windowTicks;
}

/// <summary>Everything one fold pass learned, in a shape a window sweep can query repeatedly.</summary>
/// <param name="TickRate">Ticks per second, for converting a window in ticks to seconds and back.</param>
/// <param name="MaxLookbackTicks">The widest window this fold can answer for.</param>
/// <param name="UnmeasurableShots">
///     Shots whose movement cap could not be read. These are outside the denominator at every
///     window, so a large count here is a decode problem masquerading as a calibration one.
/// </param>
/// <param name="ShotsOutsideLiveRounds">
///     Bullets fired in warmup or between rounds. Counted rather than dropped silently, because the
///     live-round gate is itself a denominator choice.
/// </param>
/// <param name="NamesBySlot">Slot to display name, for joining against a reference keyed by name.</param>
/// <param name="Shots">Every admitted-or-not bullet shot from a live round.</param>
public sealed record AdmissionFoldResult(
    double TickRate,
    int MaxLookbackTicks,
    int UnmeasurableShots,
    int ShotsOutsideLiveRounds,
    IReadOnlyDictionary<int, string> NamesBySlot,
    IReadOnlyList<AdmissionShot> Shots)
{
    /// <summary>Admitted shot counts per player name, at one candidate window.</summary>
    /// <param name="windowTicks">The candidate window, in ticks.</param>
    /// <returns>Player name to admitted count.</returns>
    public IReadOnlyDictionary<string, int> AdmittedByName(int windowTicks)
    {
        Dictionary<string, int> byName = new(StringComparer.Ordinal);
        foreach (AdmissionShot shot in Shots)
        {
            if (!shot.AdmittedAt(windowTicks) || !NamesBySlot.TryGetValue(shot.Slot, out string? name))
            {
                continue;
            }

            byName[name] = byName.GetValueOrDefault(name) + 1;
        }

        return byName;
    }

    /// <summary>
    ///     Clean counter-strafe counts per player name at one candidate window: admitted, and taken
    ///     from a standstill. This is the <c>cs_clean</c> numerator.
    /// </summary>
    /// <param name="windowTicks">The candidate window, in ticks.</param>
    /// <returns>Player name to clean count.</returns>
    public IReadOnlyDictionary<string, int> CleanByName(int windowTicks)
    {
        Dictionary<string, int> byName = new(StringComparer.Ordinal);
        foreach (AdmissionShot shot in Shots)
        {
            if (!shot.AdmittedAt(windowTicks) || !shot.CleanAtShot
                || !NamesBySlot.TryGetValue(shot.Slot, out string? name))
            {
                continue;
            }

            byName[name] = byName.GetValueOrDefault(name) + 1;
        }

        return byName;
    }

    /// <summary>Total bullet shots per player name, the denominator the admitted count sits inside.</summary>
    /// <returns>Player name to shot count.</returns>
    public IReadOnlyDictionary<string, int> ShotsByName()
    {
        Dictionary<string, int> byName = new(StringComparer.Ordinal);
        foreach (AdmissionShot shot in Shots)
        {
            if (NamesBySlot.TryGetValue(shot.Slot, out string? name))
            {
                byName[name] = byName.GetValueOrDefault(name) + 1;
            }
        }

        return byName;
    }

    /// <summary>Converts a window in seconds to the tick count the engine would round it to.</summary>
    /// <param name="seconds">The window in seconds.</param>
    /// <returns>The window in ticks.</returns>
    public int TicksFor(double seconds) => (int)Math.Round(seconds * TickRate);
}
