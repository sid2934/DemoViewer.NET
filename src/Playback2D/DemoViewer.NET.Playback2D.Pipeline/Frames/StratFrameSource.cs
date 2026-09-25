#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Export;
using DemoViewer.NET.Playback2D.Core.Keyframes;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Frames;

/// <summary>
///     A strat as a frame source: tokens sampled off their tracks, smokes and fires from the utility
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

    // SceneFrameBuilder's own sizes, so a strat smoke reads the same as a demo one.
    private const float SmokeRadiusWorld = 144;
    private const float FireCellRadiusWorld = 28;

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

        double remaining = RoundSecondsAt(_spec.RoundSeconds, tick);
        Scene2DFrame frame = slot.Frame;
        frame.TimeField = time;
        frame.MapField = _map;
        frame.GameInfoField = SceneGameInfo.Empty with
        {
            Phase = "Live",
            RoundSeconds = remaining,
            RoundTime = remaining > 0 ? FormatClock(remaining) : "0:00",
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

    private void AddAreaEffects(int tick, List<AreaEffect> into)
    {
        const int smokeTicks = (int)(SmokeSeconds * StepSchedule.TicksPerSecond);
        const int fireTicks = (int)(FireSeconds * StepSchedule.TicksPerSecond);

        foreach (UtilityCue cue in _spec.Utility)
        {
            // A flash or an HE is over in an instant and leaves nothing on the floor to draw.
            int lasts = cue.Kind switch
            {
                GrenadeKind.Smoke => smokeTicks,
                GrenadeKind.Molotov => fireTicks,
                _ => 0
            };

            if (tick < cue.Tick || tick >= cue.Tick + lasts)
            {
                continue;
            }

            if (cue.Kind == GrenadeKind.Smoke)
            {
                into.Add(new AreaEffect(AreaEffectKind.Smoke, cue.X, cue.Y, cue.Z, SmokeRadiusWorld));
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

    // One published frame and the pooled lists wired into it, the SceneFrameBuilder shape. Trails and the
    // kill feed stay the frame's empty defaults: a strat has neither.
    private sealed class FrameSlot
    {
        public FrameSlot() =>
            Frame = new Scene2DFrame
            {
                Markers = Markers,
                AreaEffects = AreaEffects
            };

        public Scene2DFrame Frame { get; }
        public List<PlayerMarker> Markers { get; } = new(TokenSlots.All.Count);
        public List<AreaEffect> AreaEffects { get; } = new(16);
    }
}
