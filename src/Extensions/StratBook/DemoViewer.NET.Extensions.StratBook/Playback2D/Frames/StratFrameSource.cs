#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Keyframes;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Playback2D.Frames;

/// <summary>
///     A strat as a frame source: tokens sampled off their tracks, each throw's flight and effect from the utility
///     landings, and a round clock, all on the strat frame clock at <see cref="StepSchedule.TicksPerSecond" />
///     (step-authoring.md §3.6). The export session, the goldens and a later <c>dv2d strat</c> drive it
///     exactly as they drive a demo.
///     <para>
///         <b>Pure in the tick.</b> <see cref="FrameAt" /> samples afresh on every call and keeps no
///         history, so a rewind costs nothing, two runs hash identically, and a 50 fps export shows the same
///         world as a 20 fps one wherever their ticks meet. Nothing to warm up either, so this is not an
///         <see cref="IPreparableFrameSource" />.
///     </para>
///     <para>
///         <b>Lifetime.</b> Two frames with pooled lists, alternated, as <c>SceneFrameBuilder</c> does: a
///         returned frame is valid until the call after next, which covers the compositor holding the
///         previous frame while the next is built.
///     </para>
/// </summary>
public sealed class StratFrameSource : ISceneFrameSource
{
    /// <summary>How long a smoke stays up: the nominal CS2 duration.</summary>
    public const double SmokeSeconds = 18;

    /// <summary>How long a molotov burns: the nominal CS2 duration.</summary>
    public const double FireSeconds = 7;

    /// <summary>How long a decoy's marker stays: the nominal CS2 duration.</summary>
    public const double DecoySeconds = 15;

    /// <summary>How long a flashbang's pop shows.</summary>
    public const double FlashSeconds = 0.5;

    /// <summary>How long an HE's burst shows.</summary>
    public const double HeSeconds = 0.6;

    /// <summary>How long a smoke takes to bloom to its full radius.</summary>
    public const double SmokeBloomSeconds = 1;

    /// <summary>A flight line fades over this long after the projectile stops: <c>SceneFrameBuilder</c>'s.</summary>
    public const double TrailFadeSeconds = 2;

    // SceneFrameBuilder's own sizes, so a strat smoke reads the same as a demo one.
    private const float SmokeRadiusWorld = 144;
    private const float FireCellRadiusWorld = 28;

    private const float SmokeBloomFrom = 0.35f;
    private const float FlashRadiusFrom = 48;
    private const float FlashRadiusTo = 220;
    private const float HeRadiusFrom = 40;
    private const float HeRadiusTo = 180;
    private const float DecoyRadiusWorld = 20;
    private const int TrailFadeTicks = (int)(TrailFadeSeconds * StepSchedule.TicksPerSecond);

    // A molotov has no networked cells here, so it spreads as a fixed ring round the landing: the centre
    // plus six at this distance, close enough that the discs overlap into one patch.
    private const float FireSpreadWorld = 40;
    private const int FireRingCells = 6;

    private readonly string[] _labels;
    private readonly SceneMapInfo _map;
    private readonly List<TokenSample> _samples = new(TokenSlots.All.Count);
    private readonly FrameSlot[] _slots = [new(), new()];
    private readonly StratSceneSpec _spec;
    private readonly int[] _teams;
    private readonly double _ticksPerOutputFrame;

    private int _clockCacheSeconds = int.MinValue;
    private string _clockCacheText = "0:00";
    private int _next;

    /// <summary>Creates a source over a spec.</summary>
    /// <param name="spec">The strat to play. Its tracks must not change while the source is in use.</param>
    public StratFrameSource(StratSceneSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spec.Fps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spec.Speed);

        _spec = spec;
        _ticksPerOutputFrame = spec.Speed * StepSchedule.TicksPerSecond / spec.Fps;
        FrameCount = OutputFrameCount(spec.StartTick, spec.EndTick, spec.Fps, spec.Speed);

        // Resolved once per slot, in TokenSlots.All order, so FrameAt indexes rather than searches.
        _labels = new string[TokenSlots.All.Count];
        _teams = new int[TokenSlots.All.Count];
        for (int i = 0; i < _labels.Length; i++)
        {
            _labels[i] = TokenSlots.All[i];
        }

        foreach (TokenLabel label in spec.Labels)
        {
            int order = TokenSlots.OrderOf(label.Slot);
            if (order < 0)
            {
                continue;
            }

            _teams[order] = label.Team;
            if (!string.IsNullOrEmpty(label.Label))
            {
                _labels[order] = label.Label;
            }
        }

        _map = new SceneMapInfo
        {
            MapName = spec.MapName,
            NetworkedBounds = spec.MapBounds,
            ObservedBounds = spec.MapBounds,
            SectionHeights = spec.SectionHeights,
            Radars = spec.Radars
        };
    }

    /// <inheritdoc />
    public int FrameCount { get; }

    /// <summary>
    ///     How many output frames a strat tick range produces: <c>TrackerFrameSource.OutputFrameCount</c>'s
    ///     arithmetic with the rate fixed at <see cref="StepSchedule.TicksPerSecond" />. Static so the export
    ///     dialog sizes its request the way the source will, before building one.
    /// </summary>
    /// <param name="startTick">First strat tick.</param>
    /// <param name="endTick">Last strat tick, inclusive.</param>
    /// <param name="fps">Output frame rate.</param>
    /// <param name="speed">Playback-rate multiplier.</param>
    public static int OutputFrameCount(int startTick, int endTick, int fps, double speed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speed);

        if (endTick < startTick)
        {
            return 0;
        }

        double ticksPerOutputFrame = speed * StepSchedule.TicksPerSecond / fps;
        long tickSpan = (long)endTick - startTick;
        return tickSpan == 0
            ? 1
            : 1 + (int)Math.Floor(tickSpan / ticksPerOutputFrame);
    }

    /// <summary>The strat tick one output frame shows.</summary>
    /// <param name="frameIndex">Output frame index, 0-based.</param>
    public int TickAt(int frameIndex) =>
        _spec.StartTick + (int)Math.Round(frameIndex * _ticksPerOutputFrame, MidpointRounding.AwayFromZero);

    /// <inheritdoc />
    public SceneTime TimeAt(int frameIndex)
    {
        int tick = TickAt(frameIndex);
        return new SceneTime(
            tick,
            frameIndex,
            (tick - _spec.StartTick) / (double)StepSchedule.TicksPerSecond,
            _spec.Speed / _spec.Fps,
            frameIndex == 0);
    }

    /// <inheritdoc />
    public Scene2DFrame FrameAt(int frameIndex)
    {
        SceneTime time = TimeAt(frameIndex);
        int tick = time.Tick;

        FrameSlot slot = _slots[_next];
        _next ^= 1;

        slot.Markers.Clear();
        _spec.Tracks.Sample(tick, _samples);
        foreach (TokenSample sample in _samples)
        {
            int order = TokenSlots.OrderOf(sample.Slot);
            TokenKeyframe at = sample.Position;
            slot.Markers.Add(new PlayerMarker(
                order, _teams[order], at.X, at.Y, (float)at.MarkerZ, at.YawDegrees,
                RingState.Team, 1, _labels[order], true));
        }

        slot.AreaEffects.Clear();
        AddAreaEffects(tick, slot.AreaEffects);
        slot.Trails.Clear();
        AddTrails(tick, slot);
        slot.Routes.Clear();
        if (_spec.Routes)
        {
            AddRoutes(tick, slot);
        }

        double remaining = _spec.CountsUp ? ElapsedAt(tick) : RoundSecondsAt(_spec.RoundSeconds, tick);
        Scene2DFrame frame = slot.Frame;
        frame.TimeField = time;
        frame.MapField = _map;
        frame.GameInfoField = SceneGameInfo.Empty with
        {
            Phase = "Live",
            RoundSeconds = remaining,
            RoundTime = _spec.CountsUp || remaining > 0 ? FormatClock(remaining) : "0:00",
            TScore = 0,
            CtScore = 0
        };
        return frame;
    }

    /// <summary>The round clock at a strat tick: <paramref name="roundSeconds" /> at tick 0, counting down.</summary>
    /// <param name="roundSeconds">The strat's round length.</param>
    /// <param name="tick">A strat frame-clock tick.</param>
    public static double RoundSecondsAt(int roundSeconds, int tick) =>
        roundSeconds - tick / (double)StepSchedule.TicksPerSecond;

    /// <summary>Seconds since the strat's start at a strat tick: the clock of a strat timed from its trigger.</summary>
    /// <param name="tick">A strat frame-clock tick.</param>
    public static double ElapsedAt(int tick) => tick / (double)StepSchedule.TicksPerSecond;

    /// <summary>How many ticks a kind's effect shows from the moment it goes off.</summary>
    /// <param name="kind">The grenade.</param>
    public static int EffectTicks(GrenadeKind kind) => (int)(StepSchedule.TicksPerSecond * kind switch
    {
        GrenadeKind.Smoke => SmokeSeconds,
        GrenadeKind.Molotov => FireSeconds,
        GrenadeKind.Decoy => DecoySeconds,
        GrenadeKind.Flash => FlashSeconds,
        _ => HeSeconds
    });

    /// <summary>The first tick a cue draws nothing: past its effect and its faded flight line.</summary>
    /// <param name="cue">A throw.</param>
    public static int EndTickOf(UtilityCue cue) =>
        Math.Max(cue.Tick + EffectTicks(cue.Kind), cue.Flight is { Count: > 0 } flight ? flight[^1].Tick + TrailFadeTicks : 0);

    private void AddAreaEffects(int tick, List<AreaEffect> into)
    {
        foreach (UtilityCue cue in _spec.Utility)
        {
            int lasts = EffectTicks(cue.Kind);
            if (tick < cue.Tick || tick >= cue.Tick + lasts)
            {
                continue;
            }

            float t = (tick - cue.Tick) / (float)lasts;
            float seconds = (tick - cue.Tick) / (float)StepSchedule.TicksPerSecond;
            switch (cue.Kind)
            {
                case GrenadeKind.Smoke:
                    into.Add(new AreaEffect(AreaEffectKind.Smoke, cue.X, cue.Y, cue.Z, SmokeRadiusWorld * Bloom(seconds)));
                    continue;
                case GrenadeKind.Flash:
                    into.Add(new AreaEffect(AreaEffectKind.Flash, cue.X, cue.Y, cue.Z,
                        FlashRadiusFrom + (FlashRadiusTo - FlashRadiusFrom) * EaseOut(t), 1 - t));
                    continue;
                case GrenadeKind.He:
                    into.Add(new AreaEffect(AreaEffectKind.Explosion, cue.X, cue.Y, cue.Z,
                        HeRadiusFrom + (HeRadiusTo - HeRadiusFrom) * EaseOut(t), 1 - t));
                    continue;
                case GrenadeKind.Decoy:
                    into.Add(new AreaEffect(AreaEffectKind.Decoy, cue.X, cue.Y, cue.Z, DecoyRadiusWorld));
                    continue;
            }

            into.Add(new AreaEffect(AreaEffectKind.Fire, cue.X, cue.Y, cue.Z, FireCellRadiusWorld));
            for (int i = 0; i < FireRingCells; i++)
            {
                double angle = i * (2 * Math.PI / FireRingCells);
                into.Add(new AreaEffect(AreaEffectKind.Fire,
                    cue.X + (float)(FireSpreadWorld * Math.Cos(angle)),
                    cue.Y + (float)(FireSpreadWorld * Math.Sin(angle)),
                    cue.Z, FireCellRadiusWorld));
            }
        }
    }

    // Starts at SmokeBloomFrom of the radius and eases out to the full cloud.
    private static float Bloom(float seconds) =>
        seconds >= SmokeBloomSeconds
            ? 1f
            : SmokeBloomFrom + (1 - SmokeBloomFrom) * EaseOut(seconds / (float)SmokeBloomSeconds);

    private static float EaseOut(float t) => 1 - (1 - t) * (1 - t);

    // Each cue owns one pooled trail per frame slot, refilled from its flight up to the tick.
    private void AddTrails(int tick, FrameSlot slot)
    {
        IReadOnlyList<UtilityCue> cues = _spec.Utility;
        for (int i = 0; i < cues.Count; i++)
        {
            UtilityCue cue = cues[i];
            if (cue.Flight is not { Count: >= 2 } flight || tick < flight[0].Tick)
            {
                continue;
            }

            int landed = flight[^1].Tick;
            if (tick >= landed + TrailFadeTicks)
            {
                continue;
            }

            GrenadeTrail trail = slot.TrailFor(i, cue.Kind);
            trail.Team = cue.Team;
            trail.Points.Clear();
            trail.Points.Add(new GrenadeTrailPoint(flight[0].X, flight[0].Y, flight[0].Z));
            for (int k = 1; k < flight.Count; k++)
            {
                FlightPoint to = flight[k];
                if (to.Tick <= tick)
                {
                    trail.Points.Add(new GrenadeTrailPoint(to.X, to.Y, to.Z));
                    continue;
                }

                FlightPoint from = flight[k - 1];
                if (tick == from.Tick)
                {
                    break;
                }

                float f = (tick - from.Tick) / (float)(to.Tick - from.Tick);
                trail.Points.Add(new GrenadeTrailPoint(from.X + (to.X - from.X) * f, from.Y + (to.Y - from.Y) * f,
                    from.Z + (to.Z - from.Z) * f));
                break;
            }

            trail.LastTick = Math.Min(tick, landed);
            trail.Alpha = tick <= landed ? 1.0 : 1.0 - (tick - landed) / (double)TrailFadeTicks;
            if (trail.Points.Count >= 2)
            {
                slot.Trails.Add(trail);
            }
        }
    }

    // A token mid-move: the line from where it is through each keyframe of the same move to where it stops. A move
    // ends at the last keyframe, a hold, a jump, or a keyframe it does not leave from straight away.
    private void AddRoutes(int tick, FrameSlot slot)
    {
        foreach (TokenTrack track in _spec.Tracks.Tracks)
        {
            IReadOnlyList<TokenKeyframe> keys = track.Keyframes;
            int k = keys.Count - 1;
            while (k >= 0 && keys[k].Tick > tick)
            {
                k--;
            }

            if (k < 0 || k >= keys.Count - 1 || !Moving(track, k) || tick < (long)keys[k].Tick + track.HoldTicks[k]
                || !track.TrySample(tick, out TokenKeyframe at))
            {
                continue;
            }

            int order = TokenSlots.OrderOf(track.Slot);
            TokenRouteLine line = slot.RouteFor(order);
            line.Team = _teams[order];
            line.Points.Clear();
            line.Points.Add(new GrenadeTrailPoint(at.X, at.Y, (float)at.MarkerZ));
            for (int j = k + 1; j < keys.Count; j++)
            {
                line.Points.Add(new GrenadeTrailPoint(keys[j].X, keys[j].Y, (float)keys[j].MarkerZ));
                if (j == keys.Count - 1 || track.HoldTicks[j] > 0 || !Moving(track, j))
                {
                    break;
                }
            }

            slot.Routes.Add(line);
        }
    }

    private static bool Moving(TokenTrack track, int k) =>
        track.Segments[k] == TokenInterpolation.Linear
        && (track.Keyframes[k].X != track.Keyframes[k + 1].X || track.Keyframes[k].Y != track.Keyframes[k + 1].Y);

    // SceneFrameBuilder's m:ss cache: the text changes once a second, so a steady frame allocates nothing.
    private string FormatClock(double seconds)
    {
        int s = (int)Math.Round(seconds);
        if (s == _clockCacheSeconds)
        {
            return _clockCacheText;
        }

        _clockCacheSeconds = s;
        _clockCacheText = string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:D2}");
        return _clockCacheText;
    }

    // One published frame and the pooled lists wired into it, the SceneFrameBuilder shape. The kill feed
    // stays the frame's empty default: a strat has none.
    private sealed class FrameSlot
    {
        private readonly Dictionary<int, GrenadeTrail> _trailPool = [];
        private readonly Dictionary<int, TokenRouteLine> _routePool = [];

        public FrameSlot() =>
            Frame = new Scene2DFrame
            {
                Markers = Markers,
                AreaEffects = AreaEffects,
                Trails = Trails,
                Routes = Routes
            };

        public Scene2DFrame Frame { get; }
        public List<PlayerMarker> Markers { get; } = new(TokenSlots.All.Count);
        public List<AreaEffect> AreaEffects { get; } = new(16);
        public List<GrenadeTrail> Trails { get; } = new(8);
        public List<TokenRouteLine> Routes { get; } = new(10);

        public TokenRouteLine RouteFor(int order)
        {
            if (!_routePool.TryGetValue(order, out TokenRouteLine? line))
            {
                line = new TokenRouteLine();
                _routePool[order] = line;
            }

            return line;
        }

        public GrenadeTrail TrailFor(int cue, GrenadeKind kind)
        {
            if (!_trailPool.TryGetValue(cue, out GrenadeTrail? trail))
            {
                trail = new GrenadeTrail { Kind = kind };
                _trailPool[cue] = trail;
            }

            return trail;
        }
    }
}
