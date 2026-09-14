#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.AppTests.AimParity;

/// <summary>
///     An independent hand-written fold of the spray-control rule, written from the specification
///     rather than from the engine's code, and used as that metric's oracle.
///     <para>
///         <b>Why spray control needs one at all.</b> No public tool publishes a spray-residual
///         column, so there is nothing to compare the shipped number against: the choice is an
///         oracle or nothing, and nothing is how a metric ends up shipping a mean over a population
///         of seven shots without anyone noticing.
///     </para>
///     <para>
///         <b>What the rule is.</b> A spray is a maximal run over which the recoil index never
///         decays, which is the engine's own segmentation rather than the "three or more shots"
///         convention the analytics sites use. The first shot of a run is its anchor and has no
///         residual by construction. Every later shot carries one residual against that anchor,
///         reported three ways: the pitch and yaw components, with yaw taken the short way round
///         the circle, and the 3D angle between the two directions, which is the rule the shipped
///         Spray column uses. A shot whose aim punch does not decode to a physically possible punch
///         is UNMEASURED, not zero: zero is indistinguishable from perfect control, which is the
///         one reading this metric must never invent.
///     </para>
///     <para>
///         <b>What it folds is shot directions, not view angles.</b> Every angular input is a
///         RESOLVED shot direction, <c>bullet_damage</c>'s <c>ShootAng</c>, probed per shot as
///         <c>eyeAngle + AimPunch</c>, so no arm of this fold adds the punch to it. The aim punch
///         reaches the fold only as the plausibility gate deciding whether a shot is measured at
///         all. Rebuilding an effective aim out of a raw view angle and the entity punch column is
///         the FIRED arm's job and lives engine-side; doing it here, on top of a direction that
///         already carries the kick, counts that kick three times over.
///     </para>
///     <para>
///         <b>Deliberately duplicated constants.</b> The five thresholds below are copies of the
///         engine's, not imports. An oracle that reads the constant it is checking cannot catch a
///         change to it, which is the failure this is here to catch;
///         <c>SprayControlOracleTests</c> pins the copies against the originals so the duplication
///         cannot rot silently instead.
///     </para>
/// </summary>
public static class SprayControlOracle
{
    /// <summary>Recoil index at or below which a shot is the first bullet out of the barrel.</summary>
    public const float FirstBulletRecoilEpsilon = 0.01f;

    /// <summary>
    ///     Largest normalised aim-punch component, in degrees, that is taken as an aim punch at all.
    ///     Generous by design: recoil kick is a couple of degrees and the largest punch in the game
    ///     is well inside this, so it rejects only the physically impossible.
    /// </summary>
    public const double MaxPlausibleAimPunchDegrees = 45.0;

    /// <summary>Tolerance multiplier on a run's measured cycle time, absorbing tick quantisation.</summary>
    public const double SprayCycleTolerance = 1.10;

    /// <summary>
    ///     Widest gap, in ticks, that may OPEN a run, used only until the run's second shot measures
    ///     its weapon's own cycle time. 16 ticks is 250 ms at 64-tick, which covers the slowest
    ///     repeat-fire cycle in CS2.
    /// </summary>
    public const int SprayOpenGapTicks = 16;

    /// <summary>Float-noise tolerance on the recoil-monotonicity test.</summary>
    public const float SprayRecoilEpsilon = 0.25f;

    /// <summary>
    ///     What the engine multiplies the aim-punch angle by when it reconstructs a shot direction
    ///     from a raw view angle and the entity punch column. That reconstruction is the FIRED
    ///     arm's, and this fold never performs it, because every angle it is handed is already a
    ///     resolved <c>ShootAng</c>. The copy stays because <c>SprayControlOracleTests</c> pins it
    ///     against the engine's, which is drift cover the fold itself no longer needs.
    /// </summary>
    public const double WeaponRecoilScale = 2.0;

    /// <summary>
    ///     Folds a shot stream into per-slot spray-control numbers. The stream must be in firing
    ///     order per slot; interleaved slots are fine, since each keeps its own run.
    /// </summary>
    /// <param name="shots">The shots, in order.</param>
    /// <returns>Slot to result.</returns>
    public static IReadOnlyDictionary<int, SprayPlayerResult> Fold(IEnumerable<OracleShot> shots)
    {
        ArgumentNullException.ThrowIfNull(shots);

        Dictionary<int, RunState> states = [];
        Dictionary<int, Tally> tallies = [];

        foreach (OracleShot shot in shots)
        {
            if (!states.TryGetValue(shot.Slot, out RunState? state))
            {
                state = new RunState();
                states[shot.Slot] = state;
                tallies[shot.Slot] = new Tally();
            }

            // A run must never span a round boundary: two unrelated engagements welded together
            // produce a residual against an anchor from a different fight, which is a plausible
            // number with nothing to flag it.
            if (state.Round != shot.Round)
            {
                state.Reset();
                state.Round = shot.Round;
            }

            // The punch is a GATE here and never a term. What it decides is whether this shot's aim
            // telemetry decoded at all, which is what keeps the components and the angle over one
            // population; the direction it would otherwise be added to already carries it.
            double? punchPitch = PlausiblePunch(shot.PunchPitch);
            double? punchYaw = PlausiblePunch(shot.PunchYaw);
            bool haveAim = shot.ShotPitch.HasValue && shot.ShotYaw.HasValue
                                                   && punchPitch.HasValue && punchYaw.HasValue;

            double shotPitch = haveAim ? shot.ShotPitch!.Value : 0.0;
            double shotYaw = haveAim ? shot.ShotYaw!.Value : 0.0;

            bool opensRun = Advance(state, shot.Tick, shot.RecoilIndex, shotPitch, shotYaw, haveAim);

            Tally tally = tallies[shot.Slot];
            tally.Shots++;
            if (opensRun)
            {
                tally.Runs++;
            }

            // The anchor has no residual by construction, and a run whose aim never resolved has
            // none at all. Both would come out as 0.0, which is why they are counted out of the
            // population rather than into it with a zero.
            if (haveAim && state.HasAnchor && !opensRun)
            {
                tally.Measured++;
                tally.PitchErrorSum += Math.Abs(shotPitch - state.AnchorPitch);
                tally.YawErrorSum += Math.Abs(WrapDegrees(shotYaw - state.AnchorYaw));
                tally.AngleErrorSum += AngleDeltaDegrees(state.AnchorPitch, state.AnchorYaw, shotPitch, shotYaw);
            }
        }

        Dictionary<int, SprayPlayerResult> results = [];
        foreach ((int slot, Tally tally) in tallies)
        {
            results[slot] = new SprayPlayerResult(
                tally.Shots, tally.Runs, tally.Measured, tally.PitchErrorSum, tally.YawErrorSum, tally.AngleErrorSum);
        }

        return results;
    }

    /// <summary>
    ///     Normalises one aim-punch component to (-180, 180] and returns it only when it is
    ///     physically a punch. <c>null</c> means "this column did not give an aim punch", which is
    ///     NOT the same as zero punch and must not be folded into an effective aim.
    /// </summary>
    /// <param name="raw">The raw component, or <c>null</c> when the column read nothing.</param>
    /// <returns>The normalised punch, or <c>null</c>.</returns>
    public static double? PlausiblePunch(float? raw)
    {
        if (raw is not { } value)
        {
            return null;
        }

        // Normalisation first: a QAngle is networked over [0, 360), so a real -2 degree kick
        // arrives as 358 and would fail any magnitude test taken on the raw value.
        double normalised = WrapDegrees(value);
        return Math.Abs(normalised) <= MaxPlausibleAimPunchDegrees ? normalised : null;
    }

    /// <summary>
    ///     3D angular distance in degrees between two view directions given as pitch/yaw pairs, the
    ///     rule the shipped Spray column aggregates. Both are converted to unit vectors and the
    ///     delta is <c>acos(dot)</c>, so yaw wraparound needs no special case. A deliberate copy of
    ///     the engine's <c>ShotEnrichmentEdge.AngleDeltaDegrees</c>, for the reason the constants
    ///     above are copies.
    /// </summary>
    /// <param name="pitchA">First pitch, degrees.</param>
    /// <param name="yawA">First yaw, degrees.</param>
    /// <param name="pitchB">Second pitch, degrees.</param>
    /// <param name="yawB">Second yaw, degrees.</param>
    /// <returns>The angle between the two directions, in [0, 180].</returns>
    public static double AngleDeltaDegrees(double pitchA, double yawA, double pitchB, double yawB)
    {
        const double toRad = Math.PI / 180.0;
        double pa = pitchA * toRad;
        double ya = yawA * toRad;
        double pb = pitchB * toRad;
        double yb = yawB * toRad;

        double xa = Math.Cos(pa) * Math.Cos(ya);
        double ya2 = Math.Cos(pa) * Math.Sin(ya);
        double za = -Math.Sin(pa);
        double xb = Math.Cos(pb) * Math.Cos(yb);
        double yb2 = Math.Cos(pb) * Math.Sin(yb);
        double zb = -Math.Sin(pb);

        double dot = Math.Clamp((xa * xb) + (ya2 * yb2) + (za * zb), -1.0, 1.0);
        return Math.Acos(dot) / toRad;
    }

    /// <summary>Wraps an angle in degrees to (-180, 180].</summary>
    /// <param name="degrees">The angle.</param>
    /// <returns>The wrapped angle.</returns>
    public static double WrapDegrees(double degrees)
    {
        double wrapped = degrees % 360.0;
        if (wrapped > 180.0)
        {
            wrapped -= 360.0;
        }
        else if (wrapped <= -180.0)
        {
            wrapped += 360.0;
        }

        return wrapped;
    }

    // Returns whether this shot OPENED a run. Two shots at the same tick continue on a zero gap:
    // they share one pre-frame snapshot, so their recoil readings are identical and only the gap
    // bound could separate them, and separating them would restart the run mid-spray.
    private static bool Advance(
        RunState state, int tick, float? recoil, double shotPitch, double shotYaw, bool haveAim)
    {
        int gap = tick - state.LastShotTick;
        int gapBound = state.RunGapBoundTicks > 0 ? state.RunGapBoundTicks : SprayOpenGapTicks;

        // A missing recoil column is not evidence of a new run, so the gap decides alone. Reading
        // an absent column as "recoil dropped" would split every spray into single-shot runs and
        // make every shot look like a first bullet.
        bool continues = state.ShotsInRun > 0
                         && state.LastShotTick >= 0
                         && gap >= 0
                         && gap <= gapBound
                         && (recoil is null || recoil.Value >= state.LastRecoil - SprayRecoilEpsilon);

        if (continues)
        {
            state.ShotsInRun++;

            // The run's first non-zero gap IS the weapon's cycle time (continuous fire has no other
            // spacing), so the run measures its own bound rather than carrying a per-weapon table
            // that would need keeping in step with every balance patch.
            if (state.RunGapBoundTicks == 0 && gap > 0)
            {
                state.RunGapBoundTicks = Math.Max(1, (int)Math.Ceiling(gap * SprayCycleTolerance));
            }
        }
        else
        {
            state.ShotsInRun = 1;
            state.RunGapBoundTicks = 0;
            state.AnchorPitch = shotPitch;
            state.AnchorYaw = shotYaw;
            state.HasAnchor = haveAim;
        }

        state.LastShotTick = tick;
        if (recoil is { } value)
        {
            state.LastRecoil = value;
        }

        return !continues;
    }

    private sealed class RunState
    {
        // One anchor serves every residual. The components and the 3D angle are the same deviation
        // from the same shot direction, and a second anchor beside this one is what once let the
        // two of them mean different things.
        internal double AnchorPitch;
        internal double AnchorYaw;
        internal bool HasAnchor;
        internal float LastRecoil;
        internal int LastShotTick = -1;
        internal int Round = -1;
        internal int RunGapBoundTicks;
        internal int ShotsInRun;

        internal void Reset()
        {
            ShotsInRun = 0;
            RunGapBoundTicks = 0;
            LastShotTick = -1;
            LastRecoil = 0f;
            AnchorPitch = 0;
            AnchorYaw = 0;
            HasAnchor = false;
        }
    }

    private sealed class Tally
    {
        internal double AngleErrorSum;
        internal int Measured;
        internal double PitchErrorSum;
        internal int Runs;
        internal int Shots;
        internal double YawErrorSum;
    }
}

/// <summary>
///     One shot as the oracle sees it. Every angular field is nullable because "the column read
///     nothing" is a distinct fact from "the value was zero", and collapsing the two is the defect
///     the whole oracle exists to detect.
/// </summary>
/// <param name="Slot">The shooter.</param>
/// <param name="Round">Round ordinal; a change resets the shooter's run.</param>
/// <param name="Tick">The tick the shot dispatched on.</param>
/// <param name="RecoilIndex">The weapon's recoil index, or <c>null</c> when the column was unreadable.</param>
/// <param name="ShotPitch">
///     Pitch of the RESOLVED shot direction in degrees, the kick already in it:
///     <c>bullet_damage</c>'s <c>ShootAngX</c>, which is <c>eyeAngle + AimPunch</c>. NOT a view
///     angle. A raw <c>m_angEyeAngles</c> passed here leaves every residual short by the kick, and
///     a fold that adds the punch back on top of a <c>ShootAng</c> counts that kick three times.
/// </param>
/// <param name="ShotYaw">Yaw of that same resolved direction, in degrees: <c>ShootAngY</c>.</param>
/// <param name="PunchPitch">
///     Aim-punch pitch as networked, before wrapping or the plausibility gate. Read only to decide
///     whether the shot is measurable, never to adjust <see cref="ShotPitch" />.
/// </param>
/// <param name="PunchYaw">Aim-punch yaw as networked, read on the same terms.</param>
public readonly record struct OracleShot(
    int Slot,
    int Round,
    int Tick,
    float? RecoilIndex,
    float? ShotPitch,
    float? ShotYaw,
    float? PunchPitch,
    float? PunchYaw);

/// <summary>One player's spray-control numbers from the oracle fold.</summary>
/// <param name="Shots">Every shot the fold saw for this player.</param>
/// <param name="Runs">
///     Spray runs opened. <c>Shots - Runs</c> is the largest the measured population could be, so a
///     measured count far below it says the aim columns are not decoding.
/// </param>
/// <param name="MeasuredShots">
///     Shots with a residual: not an anchor, and with a plausible aim punch on both axes. The
///     denominator of all three means, and the population marker a reader needs to tell a score
///     from an empty set.
/// </param>
/// <param name="PitchErrorSum">Sum of absolute pitch residuals between shot directions, in degrees.</param>
/// <param name="YawErrorSum">Sum of absolute wrapped yaw residuals between the same directions, in degrees.</param>
/// <param name="AngleErrorSum">
///     Sum of the 3D angles between each measured shot's direction and its anchor's, in degrees:
///     the rule the shipped column aggregates, over the anchor and the population the two
///     components use.
/// </param>
public sealed record SprayPlayerResult(
    int Shots,
    int Runs,
    int MeasuredShots,
    double PitchErrorSum,
    double YawErrorSum,
    double AngleErrorSum)
{
    /// <summary>Mean 3D angle from the anchor in degrees, or 0 over an empty population.</summary>
    public double MeanAngleError => MeasuredShots > 0 ? AngleErrorSum / MeasuredShots : 0.0;

    /// <summary>Mean absolute pitch residual in degrees, or 0 over an empty population.</summary>
    public double MeanPitchError => MeasuredShots > 0 ? PitchErrorSum / MeasuredShots : 0.0;

    /// <summary>Mean absolute yaw residual in degrees, or 0 over an empty population.</summary>
    public double MeanYawError => MeasuredShots > 0 ? YawErrorSum / MeasuredShots : 0.0;

    /// <summary>A one-line rendering for the diagnostic table.</summary>
    /// <returns>The line.</returns>
    public string Render() => string.Create(
        CultureInfo.InvariantCulture,
        $"shots={Shots,5} runs={Runs,5} measured={MeasuredShots,5} pitch={MeanPitchError,7:F2} yaw={MeanYawError,7:F2} angle={MeanAngleError,7:F2}");
}
