#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;

#endregion

namespace DemoViewer.NET.ViewModels.Stats;

/// <summary>Which point a spray's offsets are measured FROM.</summary>
public enum SprayOrigin
{
    /// <summary>
    ///     The run's first bullet. Measures compensation alone: a tight cloud means the player held
    ///     the bullets together, wherever they were pointing.
    /// </summary>
    FirstBullet,

    /// <summary>
    ///     The target's centre at the moment of each shot, so the origin MOVES with the enemy.
    ///     Measures compensation and tracking together, which is the harder and more honest thing:
    ///     a player who sprays beautifully a foot to the left of someone scores well on
    ///     <see cref="FirstBullet" /> and badly here, which is the point.
    /// </summary>
    TargetCentre
}

/// <summary>One bullet's offset from the run's origin, in degrees.</summary>
/// <param name="ShotIndex">Position in the run, 0 for the anchor.</param>
/// <param name="RecoilIndex">The weapon's own spray position, which indexes the ideal pattern.</param>
/// <param name="OffsetYawDeg">Horizontal offset. Positive is right on screen.</param>
/// <param name="OffsetPitchDeg">
///     Vertical offset. Source pitch is positive DOWN (<c>PlayerVantage.Forward</c> builds
///     <c>-sin(pitch)</c> for Z), so a recoil kick is NEGATIVE and a plot whose Y grows downward
///     passes this through unflipped.
/// </param>
public sealed record SpraySample(int ShotIndex, int RecoilIndex, float OffsetYawDeg, float OffsetPitchDeg);

/// <summary>One uninterrupted spray, as its landed bullets.</summary>
/// <param name="StartTick">Frame tick of the anchor bullet.</param>
/// <param name="Weapon">Weapon classname, from the paired <c>player_hurt</c>.</param>
/// <param name="Shots">Offsets, anchor first.</param>
public sealed record SprayRun(int StartTick, string Weapon, IReadOnlyList<SpraySample> Shots);

/// <summary>Every run one player produced with one weapon.</summary>
/// <param name="Slot">Player slot.</param>
/// <param name="Weapon">Weapon classname.</param>
/// <param name="Runs">Runs, in demo order.</param>
public sealed record PlayerSprays(int Slot, string Weapon, IReadOnlyList<SprayRun> Runs);

/// <summary>One point of a weapon's recoil pattern, averaged over observed bullets.</summary>
/// <param name="RecoilIndex">Spray position this point describes.</param>
/// <param name="PunchYawDeg">Mean horizontal punch at that position.</param>
/// <param name="PunchPitchDeg">Mean vertical punch at that position.</param>
/// <param name="Samples">How many bullets contributed. Thin tails are normal; long sprays are rare.</param>
public sealed record SprayPatternPoint(int RecoilIndex, float PunchYawDeg, float PunchPitchDeg, int Samples);

/// <summary>Everything a spray visualiser needs from one demo.</summary>
/// <param name="Players">Per player and weapon, the runs they actually produced.</param>
/// <param name="IdealByWeapon">Per weapon, the recoil pattern observed in this demo.</param>
public sealed record SprayModel(
    IReadOnlyList<PlayerSprays> Players,
    IReadOnlyDictionary<string, IReadOnlyList<SprayPatternPoint>> IdealByWeapon);

/// <summary>
///     Extracts per-shot spray geometry from a parsed demo, for the per-player per-weapon spray plot.
///     <para>
///         <b>Everything here comes off <c>bullet_damage</c>, so there is no second entity pass and no
///         engine change.</b> That event carries the shot direction (<c>ShootAng</c>, with the recoil
///         already folded in by the server), the spray position (<c>RecoilIndex</c>) and the applied
///         recoil (<c>AimPunch</c>). The weapon it does NOT carry, so it is paired with the
///         <c>player_hurt</c> that fires for the same attacker, victim and tick.
///     </para>
///     <para>
///         <b>The ideal pattern is measured, not reconstructed.</b> <c>AimPunch</c> at recoil index n
///         is the pattern offset at position n, verified cumulative and monotonic on a real demo
///         (mean pitch -0.01, -0.23, -0.78, -1.70, -3.26, -4.18, -4.99 for indices 0 to 6: the AK's
///         climb). So aggregating it per weapon yields the pattern directly. The alternative was
///         reimplementing Valve's PRNG from the seed in <c>scripts/weapons.vdata_c</c>, which fails
///         SILENTLY if the generator differs by so much as a rounding rule.
///     </para>
///     <para>
///         <b>Landed bullets only.</b> <c>bullet_damage</c> does not fire for misses, so a run here is
///         the landed subsequence of a spray, not the whole trigger pull. That is a real limitation of
///         the source and not a modelling choice: the miss simply is not on the wire.
///     </para>
/// </summary>
public static class SpraySampler
{
    /// <summary>
    ///     Ticks after which a bullet starts a new run regardless of recoil index. Matches the
    ///     engine's own spray definition closely enough for a plot: recoil decays once firing stops,
    ///     so a non-dropping index plus a bounded gap is one continuous burst.
    /// </summary>
    public const int MaxRunGapTicks = 24;

    /// <summary>Runs shorter than this are noise on a plot rather than a spray.</summary>
    public const int MinRunShots = 3;

    /// <summary>
    ///     Builds the model. <paramref name="origin" /> selects what the offsets are measured from.
    /// </summary>
    /// <param name="demo">The parsed demo.</param>
    /// <param name="origin">Static anchor or moving target centre.</param>
    public static SprayModel Sample(ParsedDemo demo, SprayOrigin origin = SprayOrigin.FirstBullet)
    {
        ArgumentNullException.ThrowIfNull(demo);

        // Weapon lookup: player_hurt carries it and bullet_damage does not, and the two fire for the
        // same (attacker, victim, tick). Keyed on all three so two duels resolving on one tick do
        // not borrow each other's weapon.
        Dictionary<(int Attacker, int Victim, int Tick), string> weapons = [];
        List<Shot> shots = [];

        foreach (GameEvent evt in demo.AllGameEvents)
        {
            switch (evt.Payload)
            {
                case PlayerHurtEvent hurt when hurt.Weapon is { Length: > 0 }:
                    weapons[(hurt.Attacker, hurt.UserId, evt.GameTick)] = hurt.Weapon;
                    break;
                case BulletDamageEvent shot:
                    shots.Add(new Shot(
                        shot.Attacker, shot.Victim, evt.GameTick,
                        shot.ShootAngX, shot.ShootAngY,
                        shot.AimPunchX, shot.AimPunchY,
                        (int)Math.Round(shot.RecoilIndex)));
                    break;
            }
        }

        if (origin == SprayOrigin.TargetCentre)
        {
            ResolveTargetDirections(demo, shots);
        }

        return new SprayModel(BuildRuns(shots, weapons, origin), BuildPatterns(shots, weapons));
    }

    private static List<PlayerSprays> BuildRuns(
        List<Shot> shots, Dictionary<(int, int, int), string> weapons, SprayOrigin origin)
    {
        Dictionary<(int Slot, string Weapon), List<SprayRun>> byPlayerWeapon = [];
        Dictionary<int, List<Shot>> open = [];

        foreach (Shot s in shots.OrderBy(x => x.Tick))
        {
            if (!open.TryGetValue(s.Attacker, out List<Shot>? run))
            {
                run = [];
                open[s.Attacker] = run;
            }

            bool continues = run.Count > 0
                             && s.Tick - run[^1].Tick <= MaxRunGapTicks
                             && s.RecoilIndex >= run[^1].RecoilIndex;

            if (!continues && run.Count > 0)
            {
                Flush(run, weapons, byPlayerWeapon, origin);
                run.Clear();
            }

            run.Add(s);
        }

        foreach (List<Shot> run in open.Values)
        {
            Flush(run, weapons, byPlayerWeapon, origin);
        }

        return [.. byPlayerWeapon
            .Select(kv => new PlayerSprays(kv.Key.Slot, kv.Key.Weapon, kv.Value))
            .OrderBy(p => p.Slot)
            .ThenBy(p => p.Weapon, StringComparer.Ordinal)];
    }

    private static void Flush(
        List<Shot> run, Dictionary<(int, int, int), string> weapons,
        Dictionary<(int Slot, string Weapon), List<SprayRun>> into, SprayOrigin origin)
    {
        if (run.Count < MinRunShots)
        {
            return;
        }

        Shot anchor = run[0];
        string weapon = weapons.GetValueOrDefault((anchor.Attacker, anchor.Victim, anchor.Tick), "unknown");

        List<SpraySample> samples = [];
        for (int i = 0; i < run.Count; i++)
        {
            Shot s = run[i];

            // FirstBullet measures from the run's anchor, so offsets describe the SHAPE of the
            // spray wherever it was pointed. TargetCentre measures each bullet from the enemy it
            // was aimed at, so the origin moves and the offsets describe how close the bullets
            // actually landed to the person: compensation and tracking in one number. A shot whose
            // target could not be resolved is dropped rather than falling back to the anchor,
            // because a mixed run would read as a tight spray that never happened.
            if (origin == SprayOrigin.TargetCentre)
            {
                if (!s.HasTarget)
                {
                    continue;
                }

                samples.Add(new SpraySample(
                    i, s.RecoilIndex,
                    Wrap(s.ShootYaw - s.TargetYaw),
                    s.ShootPitch - s.TargetPitch));
                continue;
            }

            samples.Add(new SpraySample(
                i, s.RecoilIndex,
                Wrap(s.ShootYaw - anchor.ShootYaw),
                s.ShootPitch - anchor.ShootPitch));
        }

        // The population guard comes BEFORE the get-or-add. TargetCentre drops any bullet whose
        // victim could not be placed, so a run that passed the check at the top of this method can
        // still fall under the floor here, and adding the key first would leave a (player, weapon)
        // pair behind with an empty run list. The drilldown builds its weapon picker straight off
        // those pairs, so that is an entry the reader can select which then draws nothing.
        if (samples.Count < MinRunShots)
        {
            return;
        }

        (int, string) key = (anchor.Attacker, weapon);
        if (!into.TryGetValue(key, out List<SprayRun>? list))
        {
            list = [];
            into[key] = list;
        }

        list.Add(new SprayRun(anchor.Tick, weapon, samples));
    }

    private static Dictionary<string, IReadOnlyList<SprayPatternPoint>> BuildPatterns(
        List<Shot> shots, Dictionary<(int, int, int), string> weapons)
    {
        Dictionary<string, Dictionary<int, List<(float Yaw, float Pitch)>>> acc = [];
        foreach (Shot s in shots)
        {
            string weapon = weapons.GetValueOrDefault((s.Attacker, s.Victim, s.Tick), "unknown");
            if (weapon == "unknown")
            {
                continue;
            }

            if (!acc.TryGetValue(weapon, out Dictionary<int, List<(float, float)>>? byIndex))
            {
                byIndex = [];
                acc[weapon] = byIndex;
            }

            if (!byIndex.TryGetValue(s.RecoilIndex, out List<(float, float)>? bucket))
            {
                bucket = [];
                byIndex[s.RecoilIndex] = bucket;
            }

            bucket.Add((s.PunchYaw, s.PunchPitch));
        }

        Dictionary<string, IReadOnlyList<SprayPatternPoint>> result = [];
        foreach ((string weapon, Dictionary<int, List<(float Yaw, float Pitch)>> byIndex) in acc)
        {
            result[weapon] =
            [
                .. byIndex.OrderBy(kv => kv.Key)
                    .Select(kv => new SprayPatternPoint(
                        kv.Key,
                        kv.Value.Average(v => v.Yaw),
                        kv.Value.Average(v => v.Pitch),
                        kv.Value.Count))
            ];
        }

        return result;
    }


    /// <summary>
    ///     Fills each shot's direction to its victim's chest, by replaying entity state once and
    ///     seeking to each shot's tick in order.
    ///     <para>
    ///         Forward-only and single-pass: <see cref="EntityStateLayer.SeekToTick" /> is O(k) in
    ///         frames advanced, so walking the shots in tick order costs one traversal of the demo
    ///         rather than one per shot. Shots whose pawns cannot be resolved keep
    ///         <see cref="Shot.HasTarget" /> false and are dropped by the caller.
    ///     </para>
    ///     <para>
    ///         Chest, not head or centroid, and taken from <c>PlayerVantage.BuildAnchors</c> rather
    ///         than recomputed: it is the anchor the visibility scan already measures preaim against,
    ///         so the two numbers describe the same point on the same body.
    ///     </para>
    /// </summary>
    private static void ResolveTargetDirections(ParsedDemo demo, List<Shot> shots)
    {
        shots.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        EntityStateLayer layer = new(demo.Frames);
        Vector3[] anchors = new Vector3[PlayerVantage.MaxAnchors];

        for (int i = 0; i < shots.Count; i++)
        {
            Shot s = shots[i];
            layer.SeekToTick(s.Tick);

            EntityState? shooter = PawnLookup.ResolvePawn(layer.Tracker, s.Attacker);
            EntityState? victim = PawnLookup.ResolvePawn(layer.Tracker, s.Victim);
            if (shooter is null || victim is null)
            {
                continue;
            }

            VisibilityAnalyzer.Vantage? sv = VisibilityAnalyzer.TryVantage(
                s.Attacker, shooter, PositionUtil.CellToWorld);
            VisibilityAnalyzer.Vantage? tv = VisibilityAnalyzer.TryVantage(
                s.Victim, victim, PositionUtil.CellToWorld);
            if (sv is not { } viewer || tv is not { } target)
            {
                continue;
            }

            int count = PlayerVantage.BuildAnchors(target.Feet, target.Duck, viewer.Eye, anchors);
            if (count == 0)
            {
                continue;
            }

            Vector3 toChest = anchors[0] - viewer.Eye;
            if (toChest.LengthSquared() < 1e-6f)
            {
                continue;
            }

            (float pitch, float yaw) = ToAngles(Vector3.Normalize(toChest));
            shots[i] = s with { HasTarget = true, TargetPitch = pitch, TargetYaw = yaw };
        }
    }

    // Inverse of PlayerVantage.Forward, which builds (cos p cos y, cos p sin y, -sin p).
    private static (float Pitch, float Yaw) ToAngles(Vector3 dir) =>
        ((float)(-Math.Asin(Math.Clamp(dir.Z, -1.0, 1.0)) * 180.0 / Math.PI),
            (float)(Math.Atan2(dir.Y, dir.X) * 180.0 / Math.PI));

    // Yaw is an angle on a circle, so a spray crossing the 180 seam would otherwise read as a 350
    // degree correction rather than a 10 degree one.
    private static float Wrap(float degrees)
    {
        float d = degrees % 360f;
        if (d > 180f)
        {
            d -= 360f;
        }
        else if (d <= -180f)
        {
            d += 360f;
        }

        return d;
    }

    private readonly record struct Shot(
        int Attacker, int Victim, int Tick,
        float ShootPitch, float ShootYaw,
        float PunchPitch, float PunchYaw,
        int RecoilIndex)
    {
        /// <summary>Whether the victim's chest direction resolved for this shot.</summary>
        public bool HasTarget { get; init; }

        /// <summary>Pitch from the shooter's eye to the victim's chest, degrees.</summary>
        public float TargetPitch { get; init; }

        /// <summary>Yaw of the same.</summary>
        public float TargetYaw { get; init; }
    }
}
