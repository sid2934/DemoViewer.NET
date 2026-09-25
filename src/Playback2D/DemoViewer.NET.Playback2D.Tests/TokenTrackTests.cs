#region

using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The keyframe track's sampling table (step-authoring.md §3.3), row by row, plus the two approved
///     decisions it carries: the stationary rule for absent entries and the midpoint level snap.
/// </summary>
public class TokenTrackTests
{
    private const double Lower = -448;
    private const double Upper = -384;

    [Test]
    public async Task BeforeTheFirstKeyframe_ThereIsNoSample()
    {
        TokenTrack track = new("A", [Key(64, 0, 0)]);

        await Assert.That(track.TrySample(63, out _)).IsFalse()
            .Because("a token is not drawn before its first keyframe");
        await Assert.That(track.TrySample(64, out _)).IsTrue();
        await Assert.That(new TokenTrack("B", []).TrySample(0, out _)).IsFalse()
            .Because("a slot with no keyframe at all is never drawn");
    }

    [Test]
    public async Task AfterTheLastKeyframe_ItHoldsForever()
    {
        TokenTrack track = new("A", [Key(0, 0, 0), Key(100, 50, 60, yaw: 30)]);

        await Assert.That(track.TrySample(100, out TokenKeyframe at)).IsTrue();
        await Assert.That(at.X).IsEqualTo(50f);
        await Assert.That(track.TrySample(1_000_000, out TokenKeyframe late)).IsTrue();
        await Assert.That(late.X).IsEqualTo(50f);
        await Assert.That(late.Y).IsEqualTo(60f);
        await Assert.That(late.YawDegrees).IsEqualTo(30f);
        await Assert.That(late.Tick).IsEqualTo(1_000_000)
            .Because("a sample reports the tick that was asked for, not the keyframe's");
    }

    [Test]
    public async Task InsideTheHold_TheTokenStaysAtItsKeyframe_ThenMoves()
    {
        TokenTrack track = new("A", [Key(0, 0, 0), Key(100, 100, 0)], holdTicks: [60, 0]);

        await Assert.That(X(track, 0)).IsEqualTo(0f);
        await Assert.That(X(track, 59)).IsEqualTo(0f).Because("still inside the 60-tick hold");
        await Assert.That(X(track, 60)).IsEqualTo(0f).Because("u is 0 where the motion starts");
        await Assert.That(X(track, 80)).IsEqualTo(50f).Because("halfway through the 40 ticks of motion");
        await Assert.That(X(track, 100)).IsEqualTo(100f);
    }

    [Test]
    public async Task LinearMidpoint_LandsOnTheArithmeticMean()
    {
        TokenTrack track = new("A", [Key(0, -120.5f, 300), Key(64, 879.5f, -700)]);

        await Assert.That(track.TrySample(32, out TokenKeyframe mid)).IsTrue();
        await Assert.That(mid.X).IsEqualTo((-120.5f + 879.5f) / 2);
        await Assert.That(mid.Y).IsEqualTo((300f + -700f) / 2);
    }

    [Test]
    public async Task DegenerateHold_StaysThenJumpsAtTheNextKeyframe()
    {
        TokenTrack track = new("A", [Key(0, 0, 0), Key(100, 100, 0)], holdTicks: [100, 0]);
        TokenTrack longer = new("B", [Key(0, 0, 0), Key(100, 100, 0)], holdTicks: [int.MaxValue, 0]);

        await Assert.That(X(track, 99)).IsEqualTo(0f);
        await Assert.That(X(track, 100)).IsEqualTo(100f);
        await Assert.That(X(longer, 99)).IsEqualTo(0f)
            .Because("a hold past the next keyframe must degenerate, not overflow into a motion");
        await Assert.That(X(longer, 100)).IsEqualTo(100f);
    }

    [Test]
    public async Task HoldSegment_JumpsAtTheNextKeyframe()
    {
        TokenTrack track = new("A", [Key(0, 0, 0), Key(100, 100, 0), Key(200, 300, 0)],
            segments: [TokenInterpolation.Hold, TokenInterpolation.Linear, TokenInterpolation.Linear]);

        await Assert.That(X(track, 50)).IsEqualTo(0f);
        await Assert.That(X(track, 99)).IsEqualTo(0f);
        await Assert.That(X(track, 100)).IsEqualTo(100f);
        await Assert.That(X(track, 150)).IsEqualTo(200f).Because("the next segment is linear again");
    }

    [Test]
    public async Task Yaw_TurnsAlongTheShorterArc_Across350To10()
    {
        TokenTrack track = new("A", [Key(0, 0, 0, yaw: 350), Key(100, 0, 0, yaw: 10)]);
        TokenTrack back = new("B", [Key(0, 0, 0, yaw: 10), Key(100, 0, 0, yaw: 350)]);

        await Assert.That(Heading(track, 50)).IsEqualTo(0).Within(1e-4)
            .Because("350 to 10 passes through 0, not through 180");
        await Assert.That(Heading(track, 25)).IsEqualTo(355).Within(1e-4);
        await Assert.That(Heading(back, 25)).IsEqualTo(5).Within(1e-4);
        await Assert.That(Heading(back, 100)).IsEqualTo(350).Within(1e-4);
    }

    [Test]
    public async Task Level_SwitchesAtTheSegmentMidpoint_AndNotBefore()
    {
        TokenTrack track = new("A", [Key(0, 0, 0, level: Lower), Key(100, 100, 0, level: Upper)]);

        await Assert.That(Level(track, 0)).IsEqualTo(Lower);
        await Assert.That(Level(track, 49)).IsEqualTo(Lower).Because("u is below one half");
        await Assert.That(Level(track, 50)).IsEqualTo(Upper).Because("the snap is at u = 0.5");
        await Assert.That(Level(track, 99)).IsEqualTo(Upper);

        TokenTrack held = new("B", [Key(0, 0, 0, level: Lower), Key(100, 100, 0, level: Upper)], holdTicks: [60, 0]);
        await Assert.That(Level(held, 79)).IsEqualTo(Lower)
            .Because("the midpoint is of the motion, which starts after the hold");
        await Assert.That(Level(held, 80)).IsEqualTo(Upper);
    }

    [Test]
    public async Task MarkerZ_IsTheMiddleOfTheLevelsFirstQuantum()
    {
        TokenKeyframe key = Key(0, 0, 0, level: Lower);

        await Assert.That(key.MarkerZ).IsEqualTo(Lower + MapSpace.LevelQuantum / 2);
    }

    [Test]
    public async Task ForwardAndReverseScrubs_AgreeAtEveryTick()
    {
        TokenTrack track = new("C",
            [Key(0, 0, 0, yaw: 350, level: Lower), Key(90, 400, -200, yaw: 20, level: Upper), Key(300, -50, 60, yaw: 170)],
            holdTicks: [10, 45, 0],
            segments: [TokenInterpolation.Linear, TokenInterpolation.Linear, TokenInterpolation.Hold]);

        Dictionary<int, TokenKeyframe> forward = [];
        for (int t = -10; t <= 320; t++)
        {
            if (track.TrySample(t, out TokenKeyframe s))
            {
                forward[t] = s;
            }
        }

        bool agree = true;
        for (int t = 320; t >= -10; t--)
        {
            bool has = track.TrySample(t, out TokenKeyframe s);
            agree &= has == forward.ContainsKey(t) && (!has || forward[t] == s);
        }

        await Assert.That(agree).IsTrue().Because("sampling is a pure function of the tick");
    }

    /// <summary>
    ///     Decision 1, the stationary rule: B placed at steps 1 and 4 of a four-step strat sits still
    ///     through step 2 and moves only in step 3's window, the segment that ends at its next explicit
    ///     entry. Interpolating across the gap is the alternative §4 rejects.
    /// </summary>
    [Test]
    public async Task Builder_AbsentEntries_AreStationary_AndMoveOnlyInTheLastSegment()
    {
        TokenStep[] steps = [Step(0), Step(640), Step(1280), Step(1920)];
        TokenTrack track = TokenTrackBuilder.Build("B", steps,
            [Place(0, 0), null, null, Place(900, 0)]);

        await Assert.That(X(track, 640)).IsEqualTo(0f).Because("stationary through step 2");
        await Assert.That(X(track, 1279)).IsEqualTo(0f).Because("still until step 3's window opens");
        await Assert.That(X(track, 1600)).IsEqualTo(450f).Because("it moves across step 3's window");
        await Assert.That(X(track, 1920)).IsEqualTo(900f);
        await Assert.That(string.Join("|", track.Keyframes.Select(k => k.Tick))).IsEqualTo("0|1280|1920");
    }

    /// <summary>
    ///     The design's own example: adding a position for B at step 4 changes B's motion between steps 3
    ///     and 4 and nothing else, so every tick up to step 3 samples exactly as the author already saw it.
    /// </summary>
    [Test]
    public async Task Builder_AddingAnEntry_NeverRetimesAMoveAlreadySeen()
    {
        TokenStep[] steps = [Step(0), Step(640), Step(1280), Step(1920)];
        TokenTrack before = TokenTrackBuilder.Build("C", steps, [Place(0, 0), null, Place(300, 0), null]);
        TokenTrack after = TokenTrackBuilder.Build("C", steps, [Place(0, 0), null, Place(300, 0), Place(300, 600)]);

        bool untouched = true;
        for (int t = 0; t <= 1280; t++)
        {
            untouched &= X(before, t) == X(after, t) && Y(before, t) == Y(after, t);
        }

        await Assert.That(untouched).IsTrue().Because("nothing up to step 3 is re-timed");
        await Assert.That(Y(after, 1600)).IsEqualTo(300f).Because("the new move plays across step 3's window");
        await Assert.That(Y(before, 1600)).IsEqualTo(0f);
    }

    [Test]
    public async Task Builder_TheMovingStepsHoldAndInterpolation_ShapeTheMove()
    {
        TokenStep[] steps = [Step(0), Step(640, hold: 320), Step(1280), Step(1920, interpolation: TokenInterpolation.Hold), Step(2560)];
        TokenTrack track = TokenTrackBuilder.Build("A", steps,
            [Place(0, 0), null, Place(640, 0), null, Place(0, 0)]);

        await Assert.That(X(track, 900)).IsEqualTo(0f).Because("step 2 holds 320 ticks before the move starts");
        await Assert.That(X(track, 1120)).IsEqualTo(320f).Because("then crosses the remaining 320 ticks");
        await Assert.That(X(track, 2559)).IsEqualTo(640f).Because("step 4's hold interpolation keeps it until step 5");
        await Assert.That(X(track, 2560)).IsEqualTo(0f);
    }

    [Test]
    public async Task Builder_SharedTicks_LaterStepWins_AndAbsentYawKeepsThePrevious()
    {
        TokenStep[] steps = [Step(0), Step(640), Step(640), Step(1280)];
        TokenTrack track = TokenTrackBuilder.Build("O2", steps,
            [new TokenPlacement(0, 0, 0, 45), Place(10, 0), Place(20, 0), Place(40, 0)]);

        await Assert.That(track.Keyframes.Count).IsEqualTo(3);
        await Assert.That(track.Keyframes[1].X).IsEqualTo(20f).Because("the later of two steps sharing a tick wins");
        await Assert.That(track.Keyframes[2].YawDegrees).IsEqualTo(45f)
            .Because("an absent yaw keeps the previous keyframe's");
        await Assert.That(TokenTrackBuilder.Build("A", [Step(0)], [Place(1, 1)]).Keyframes[0].YawDegrees).IsEqualTo(0f)
            .Because("with no previous keyframe an absent yaw is 0");
        Assert.Throws<ArgumentException>(() => TokenTrackBuilder.Build("A", [Step(0)], []));
    }

    /// <summary>Decision 8 end to end: a cross-floor move built from two entries snaps at its midpoint.</summary>
    [Test]
    public async Task Builder_CrossFloorMove_SnapsLevelAtTheMidpoint()
    {
        TokenTrack track = TokenTrackBuilder.Build("D", [Step(0), Step(128)],
            [new TokenPlacement(0, 0, Lower, null), new TokenPlacement(100, 0, Upper, null)]);

        await Assert.That(Level(track, 63)).IsEqualTo(Lower);
        await Assert.That(Level(track, 64)).IsEqualTo(Upper);
    }

    [Test]
    public void Construction_RefusesUnsortedTicks_UnknownSlots_AndMismatchedLists()
    {
        Assert.Throws<ArgumentException>(() => _ = new TokenTrack("A", [Key(10, 0, 0), Key(10, 1, 1)]));
        Assert.Throws<ArgumentException>(() => _ = new TokenTrack("A", [Key(10, 0, 0), Key(5, 1, 1)]));
        Assert.Throws<ArgumentException>(() => _ = new TokenTrack("F", []));
        Assert.Throws<ArgumentException>(() => _ = new TokenTrack("o1", []));
        Assert.Throws<ArgumentException>(() => _ = new TokenTrack("A", [Key(0, 0, 0)], holdTicks: [1, 2]));
        Assert.Throws<ArgumentException>(() => _ = new TokenTrack("A", [Key(0, 0, 0)], holdTicks: [-1]));
        Assert.Throws<ArgumentException>(() => _ = new TokenTrack("A", [Key(0, 0, 0)], segments: []));
    }

    /// <summary>Decision 2: ten tokens, <c>O1..O5</c> additive, sampled after the own side.</summary>
    [Test]
    public async Task TrackSet_SamplesTenTokens_OwnSideFirst_AndCanHideOpponents()
    {
        TokenTrackSet set = new();
        foreach (string slot in TokenSlots.All.Reverse())
        {
            set.Replace(new TokenTrack(slot, [Key(0, TokenSlots.OrderOf(slot), 0)]));
        }

        List<TokenSample> samples = [];
        set.Sample(0, samples);

        await Assert.That(string.Join("|", samples.Select(s => s.Slot))).IsEqualTo(string.Join("|", TokenSlots.All));
        await Assert.That(samples.Count(s => s.IsOpponent)).IsEqualTo(5);

        set.Sample(0, samples, includeOpponents: false);
        await Assert.That(string.Join("|", samples.Select(s => s.Slot))).IsEqualTo(string.Join("|", TokenSlots.Own));
    }

    [Test]
    public async Task TrackSet_OmitsTracksWithNoSample_AndBumpsVersionOnEveryChange()
    {
        TokenTrackSet set = new([new TokenTrack("A", [Key(0, 1, 1)]), new TokenTrack("O3", [Key(500, 2, 2)])]);
        int before = set.Version;

        List<TokenSample> samples = [new TokenSample("E", default)];
        set.Sample(100, samples);
        await Assert.That(string.Join("|", samples.Select(s => s.Slot))).IsEqualTo("A");

        set.Replace(new TokenTrack("A", [Key(0, 9, 9)]));
        await Assert.That(set.Get("A")!.Keyframes[0].X).IsEqualTo(9f);
        await Assert.That(set.Tracks.Count).IsEqualTo(2).Because("Replace swaps the slot's track in place");
        await Assert.That(set.Remove("O3")).IsTrue();
        await Assert.That(set.Remove("O3")).IsFalse();

        await Assert.That(set.Version).IsEqualTo(before + 2);
        Assert.Throws<ArgumentException>(() => _ = new TokenTrackSet([new TokenTrack("A", []), new TokenTrack("A", [])]));
    }

    private static TokenKeyframe Key(int tick, float x, float y, float yaw = 0, double level = 0) =>
        new(tick, x, y, level, yaw);

    private static float X(TokenTrack track, int tick) =>
        track.TrySample(tick, out TokenKeyframe s) ? s.X : float.NaN;

    private static float Y(TokenTrack track, int tick) =>
        track.TrySample(tick, out TokenKeyframe s) ? s.Y : float.NaN;

    private static TokenStep Step(int tick, int hold = 0, TokenInterpolation interpolation = TokenInterpolation.Linear) =>
        new(tick, hold, interpolation);

    private static TokenPlacement? Place(float x, float y) => new TokenPlacement(x, y, 0, null);

    private static double Level(TokenTrack track, int tick) =>
        track.TrySample(tick, out TokenKeyframe s) ? s.LevelMinZ : double.NaN;

    // Yaw folded into [0, 360): the track leaves it unwrapped by contract.
    private static double Heading(TokenTrack track, int tick) =>
        track.TrySample(tick, out TokenKeyframe s) ? ((s.YawDegrees % 360) + 360) % 360 : double.NaN;
}
