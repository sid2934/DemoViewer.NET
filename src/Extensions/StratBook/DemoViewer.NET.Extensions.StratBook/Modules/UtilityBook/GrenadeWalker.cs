#region

using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>A walk's output: the rows, and what the header records about how they were made.</summary>
/// <param name="Rows">One per grenade, in spawn order.</param>
/// <param name="InputCoverage">Share of the throws with a thrower read at the spawn whose thrower had commands on the lane there.</param>
/// <param name="InputDecoder">The input source's name.</param>
/// <param name="TrajectoryStride">The stride the trajectories were thinned at.</param>
public sealed record GrenadeWalk(IReadOnlyList<GrenadeRow> Rows, double InputCoverage, string InputDecoder, int TrajectoryStride);

/// <summary>
///     Turns every grenade in a demo into one <see cref="GrenadeRow" /> from the three grenade tables the visit's
///     rules run recorded (<see cref="GrenadeTables" />) and the flights the frame observer kept
///     (<see cref="GrenadeTrajectoryCollector" />). The engine carries the projectile lifecycle, the thrower chain,
///     the thrower's state and commands and the look-backs; this is the thin layer its contract leaves to the
///     consumer: the release, detonation and jump-throw joins.
///     <para>
///         Every step is pure (<see cref="Plan" />, <see cref="Assemble(IReadOnlyList{GrenadeThrow}, GrenadeEventIndex, GrenadeTrajectories, IReadOnlyList{ClipRound}, IEnumerable{PlayerInfo}, int, int)" />),
///         so the joins are tested on synthetic rows without a demo.
///     </para>
/// </summary>
public static class GrenadeWalker
{
    /// <summary>Bumped when a row's meaning changes in the fold itself.</summary>
    private const string WalkVersion = "5";

    /// <summary>The engine the rows were read with, for the sidecar header.</summary>
    public static string EngineVersion { get; } = DescribeEngine();

    /// <summary>
    ///     What stored rows are current under: <see cref="WalkVersion" />, the three grenade rulesets' YAML, the
    ///     trajectory collector's version and the engine, so a change to any of them re-indexes the demo.
    /// </summary>
    public static string Version { get; } = ComputeVersion();

    /// <summary>The sidecar's input decoder name: the jump inputs come off the rules run's command lane.</summary>
    public const string InputDecoderName = "rules-command-lane";

    /// <summary>
    ///     The rows off a visit's rules run: the three grenade tables joined, with the flights the observer kept and
    ///     the demo's roster and clock values. A grenade whose thrower never resolved takes its own fields from the
    ///     observer's flight. The input coverage is the share of throws read at the spawn whose thrower had
    ///     commands on the lane there, which is how often the jump-throw flag came from inputs.
    /// </summary>
    /// <param name="inputs">The tables and the flights.</param>
    /// <param name="rounds">Round starts, frame clock.</param>
    /// <param name="players">The demo's players, for the thrower's SteamID and name.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <param name="serverStartTick">Subtracted from the server-clock effect ticks.</param>
    public static GrenadeWalk Assemble(GrenadeWalkInputs inputs, IReadOnlyList<ClipRound> rounds, IEnumerable<PlayerInfo> players,
        int tickRate, int serverStartTick)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        List<GrenadeThrow> throws = Plan(WithFlights(inputs.Tables.Projectiles, inputs.Flights), inputs.Tables.Releases);
        List<GrenadeRow> rows = Assemble(throws, inputs.Tables.Events, inputs.Flights, rounds, players, tickRate, serverStartTick);
        int read = 0, covered = 0;
        foreach (GrenadeThrow t in throws)
        {
            if (t.ThrowerSlot < 0 || t.Projectile.AtSpawn is not { } atSpawn)
            {
                continue;
            }

            read++;
            if (atSpawn.Coverage > 0)
            {
                covered++;
            }
        }

        return new GrenadeWalk(rows, read == 0 ? 0 : (double)covered / read, InputDecoderName, inputs.Flights.Stride);
    }

    /// <summary>
    ///     The table grenades completed from the observer's flights: a landing row takes the projectile's own spawn
    ///     fields and rest tick, and a flight with no row at all, a grenade whose thrower never resolved and that
    ///     was still live when the run ended, becomes one. Spawn order. Pure.
    /// </summary>
    /// <param name="projectiles">The folded table grenades.</param>
    /// <param name="flights">The observer's flights.</param>
    public static List<ProjectileFacts> WithFlights(IReadOnlyList<ProjectileFacts> projectiles, GrenadeTrajectories flights)
    {
        ArgumentNullException.ThrowIfNull(projectiles);
        ArgumentNullException.ThrowIfNull(flights);
        List<ProjectileFacts> all = new(projectiles.Count);
        HashSet<(int, int)> known = [];
        foreach (ProjectileFacts p in projectiles)
        {
            known.Add((p.EntityIndex, p.Serial));
            all.Add(p.ThrowerSlot < 0 && p.AtSpawn is null && flights.Facts.TryGetValue((p.EntityIndex, p.Serial), out FlightFacts? f)
                ? p with
                {
                    InitialPosition = p.InitialPosition ?? f.InitialPosition, InitialVelocity = p.InitialVelocity ?? f.InitialVelocity,
                    IsIncendiary = p.IsIncendiary ?? f.IsIncendiary, LastMovedTick = f.LastMovedTick
                }
                : p);
        }

        int added = 0;
        foreach (((int index, int serial), FlightFacts f) in flights.Facts)
        {
            if (known.Contains((index, serial)) || f.ClassName.Length == 0)
            {
                continue;
            }

            added++;
            all.Add(new ProjectileFacts(index, serial, f.ClassName, -1, f.SpawnTick, f.SpawnTick, f.EndTick, f.Removed, f.InitialPosition,
                f.InitialVelocity, f.LastPosition, f.LastMovedTick, f.Bounces, f.IsIncendiary, null, null, null, null, null, null));
        }

        if (added > 0)
        {
            all.Sort((a, b) => a.SpawnTick != b.SpawnTick ? a.SpawnTick.CompareTo(b.SpawnTick) : a.EntityIndex.CompareTo(b.EntityIndex));
        }

        return all;
    }

    /// <summary>
    ///     Decides each projectile's thrower and release from the tables: the row's resolved slot, else the thrower
    ///     join; then the latest matching release within <see cref="GrenadeRules.ReleaseLookbackTicks" /> before the
    ///     spawn, else the spawn minus the measured gap, read through the row's look-backs. Resolved throwers claim
    ///     their releases first, so a join never takes a release that belongs to a known thrower.
    /// </summary>
    /// <param name="projectiles">The grenades, in spawn order.</param>
    /// <param name="releases">The grenade releases, in tick order.</param>
    public static List<GrenadeThrow> Plan(IReadOnlyList<ProjectileFacts> projectiles, IReadOnlyList<ReleaseFacts> releases)
    {
        ArgumentNullException.ThrowIfNull(projectiles);
        ArgumentNullException.ThrowIfNull(releases);

        HashSet<ReleaseFacts> claimed = [];
        GrenadeThrow?[] throws = new GrenadeThrow?[projectiles.Count];
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < projectiles.Count; i++)
            {
                ProjectileFacts p = projectiles[i];
                bool resolved = p.ThrowerSlot >= 0;
                if (resolved != (round == 0))
                {
                    continue;
                }

                int slot = p.ThrowerSlot;
                ThrowerSource throwerSource = resolved ? ThrowerSource.Entity : ThrowerSource.None;
                ReleaseFacts? fire = null;
                if (resolved)
                {
                    fire = LatestRelease(releases, slot, p.ClassName, p.SpawnTick - GrenadeRules.ReleaseLookbackTicks, p.SpawnTick, claimed);
                }
                else if (JoinThrower(releases, p.ClassName, p.SpawnTick, claimed) is { } joined)
                {
                    fire = joined;
                    slot = joined.Slot;
                    throwerSource = ThrowerSource.WeaponFire;
                }

                if (fire is not null)
                {
                    claimed.Add(fire);
                    throws[i] = new GrenadeThrow(p, slot, throwerSource, fire.Tick, ReleaseSource.WeaponFire, fire.Weapon, fire.Pawn, fire.Before);
                }
                else
                {
                    throws[i] = new GrenadeThrow(p, slot, throwerSource, p.SpawnTick - GrenadeRules.ReleaseToSpawnTicks, ReleaseSource.SpawnOffset,
                        null, resolved ? p.OffsetRelease : null, resolved ? p.OffsetBefore : null);
                }
            }
        }

        return [.. throws.Select(t => t!)];
    }

    /// <summary>
    ///     The latest unclaimed release of <paramref name="projectileClass" /> by <paramref name="slot" /> in
    ///     <c>[fromTick, toTick]</c>.
    /// </summary>
    public static ReleaseFacts? LatestRelease(IReadOnlyList<ReleaseFacts> releases, int slot, string projectileClass,
        int fromTick, int toTick, IReadOnlySet<ReleaseFacts> claimed)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(claimed);
        ReleaseFacts? best = null;
        foreach (ReleaseFacts r in releases)
        {
            if (r.Tick > toTick)
            {
                break;
            }

            if (r.Tick >= fromTick && r.Slot == slot && string.Equals(r.ProjectileClass, projectileClass, StringComparison.Ordinal)
                && !claimed.Contains(r))
            {
                best = r;
            }
        }

        return best;
    }

    /// <summary>
    ///     The thrower join for a projectile whose thrower never resolved: the unclaimed release of the same family
    ///     in <c>[spawn - 15, spawn - 6]</c>, the one nearest the measured seven-tick gap when several fit.
    /// </summary>
    public static ReleaseFacts? JoinThrower(IReadOnlyList<ReleaseFacts> releases, string projectileClass, int spawnTick,
        IReadOnlySet<ReleaseFacts> claimed)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(claimed);
        ReleaseFacts? best = null;
        int bestGap = int.MaxValue;
        foreach (ReleaseFacts r in releases)
        {
            if (r.Tick > spawnTick - GrenadeRules.ReleaseToSpawnMinTicks)
            {
                break;
            }

            if (r.Tick < spawnTick - GrenadeRules.ReleaseToSpawnMaxTicks
                || !string.Equals(r.ProjectileClass, projectileClass, StringComparison.Ordinal) || claimed.Contains(r))
            {
                continue;
            }

            int gap = Math.Abs(spawnTick - r.Tick - GrenadeRules.ReleaseToSpawnTicks);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = r;
            }
        }

        return best;
    }

    /// <summary>Builds the rows from the throws, the detonations and the flights. Pure.</summary>
    /// <param name="throws">From <see cref="Plan" />.</param>
    /// <param name="events">The detonation events.</param>
    /// <param name="flights">The trajectories.</param>
    /// <param name="rounds">Round starts, frame clock.</param>
    /// <param name="players">The demo's players, for the thrower's SteamID.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <param name="serverStartTick">Subtracted from the server-clock effect ticks.</param>
    public static List<GrenadeRow> Assemble(IReadOnlyList<GrenadeThrow> throws, GrenadeEventIndex events, GrenadeTrajectories flights,
        IReadOnlyList<ClipRound> rounds, IEnumerable<PlayerInfo> players, int tickRate, int serverStartTick)
    {
        ArgumentNullException.ThrowIfNull(throws);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(flights);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(players);

        Dictionary<int, ulong> steamBySlot = [];
        Dictionary<int, string> nameBySlot = [];
        foreach (PlayerInfo player in players)
        {
            if (player.SteamId64 != 0)
            {
                steamBySlot.TryAdd(player.Slot, player.SteamId64);
            }

            if (!string.IsNullOrEmpty(player.Name))
            {
                nameBySlot.TryAdd(player.Slot, player.Name);
            }
        }

        HashSet<GrenadeDetonation> claimedFires = [];
        List<GrenadeRow> rows = new(throws.Count);
        foreach (GrenadeThrow t in throws)
        {
            ProjectileFacts p = t.Projectile;
            int slot = t.ThrowerSlot;
            ThrowerRead? release = t.Release;
            ThrowerAtSpawn? atSpawn = slot >= 0 ? p.AtSpawn : null;

            GrenadeKind kind = GrenadeRules.KindOf(p.ClassName, t.Weapon, p.IsIncendiary) ?? GrenadeKind.Smoke;
            // The look-back is a fixed eight ticks behind the release read.
            float? speed = GrenadeRules.HorizontalSpeed(t.Before, t.ReleaseTick - GrenadeRules.SpeedWindowTicks, release?.Position,
                t.ReleaseTick, tickRate);

            bool covered = atSpawn is { Coverage: > 0 };
            int? press = covered && atSpawn!.Value.LastJumpTick >= p.SpawnTick - GrenadeRules.JumpWindowTicks
                                 && atSpawn.Value.LastJumpTick <= p.SpawnTick
                ? atSpawn.Value.LastJumpTick
                : null;
            (bool jumpThrow, JumpThrowSource jumpSource, int? pressTick) = GrenadeRules.ClassifyJumpThrow(covered, press, atSpawn?.OnGround);

            (int? detTick, Vector3? detPosition, DetonationSource detSource, int? inferno) =
                ResolveDetonation(p, events, serverStartTick, claimedFires);

            int restTick = p.LastMovedTick;
            int airEnd = detTick is { } d && d < restTick ? d : restTick;
            GrenadeEndKind endKind = detTick is not null ? GrenadeEndKind.Detonated
                : p.Removed ? GrenadeEndKind.Removed
                : GrenadeEndKind.DemoEnded;

            rows.Add(new GrenadeRow
            {
                Id = p.Id,
                Kind = kind,
                ThrowerSlot = slot,
                ThrowerSteamId64 = slot >= 0 && steamBySlot.TryGetValue(slot, out ulong steam)
                    ? steam.ToString(CultureInfo.InvariantCulture)
                    : null,
                ThrowerName = slot >= 0 ? nameBySlot.GetValueOrDefault(slot) : null,
                ThrowerTeam = atSpawn is { Team: > 0 } spawned ? spawned.Team : release?.Team ?? 0,
                ThrowerSource = slot >= 0 ? t.ThrowerSource : ThrowerSource.None,
                RoundNumber = GrenadeRules.RoundAt(rounds, p.SpawnTick),
                ReleaseTick = t.ReleaseTick,
                ReleaseSource = t.ReleaseSource,
                ReleasePosition = Point(release?.Position),
                ReleaseEyePitch = release?.Pitch,
                ReleaseEyeYaw = release?.Yaw,
                ReleaseOnGround = release?.OnGround,
                ReleaseCrouched = release?.Ducked,
                ThrowStrength = release?.ThrowStrength,
                ThrowStrengthClass = GrenadeRules.ClassifyStrength(release?.ThrowStrength, release?.HeldGrenade ?? false),
                Movement = GrenadeRules.ClassifyMovement(speed),
                SpeedAtRelease = speed,
                SpawnTick = p.SpawnTick,
                SpawnPosition = Point(p.InitialPosition),
                SpawnVelocity = Point(p.InitialVelocity),
                ThrowerPositionAtSpawn = Point(atSpawn?.Position),
                ThrowerOnGroundAtSpawn = atSpawn?.OnGround,
                JumpThrow = jumpThrow,
                JumpThrowSource = jumpSource,
                JumpPressTick = pressTick,
                Trajectory = flights.Of(p.EntityIndex, p.Serial),
                BounceCount = p.Bounces,
                AirTimeTicks = Math.Max(0, airEnd - p.SpawnTick),
                DetonationTick = detTick,
                DetonationPosition = Point(detPosition),
                DetonationSource = detSource,
                EndTick = p.EndTick,
                EndKind = endKind,
                InfernoEntityIndex = inferno
            });
        }

        return rows;
    }

    /// <summary>
    ///     A projectile's detonation: the kind's event by entity index (a fire by tick, since
    ///     <c>inferno_startburn</c> names the inferno), else the row's effect-tick field, else for an HE or a flash the
    ///     last position seen. Pure.
    /// </summary>
    public static (int? Tick, Vector3? Position, DetonationSource Source, int? InfernoIndex) ResolveDetonation(
        ProjectileFacts projectile, GrenadeEventIndex events, int serverStartTick, HashSet<GrenadeDetonation> claimedFires)
    {
        ArgumentNullException.ThrowIfNull(projectile);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(claimedFires);

        if (projectile.ClassName == GrenadeProjectileClasses.Molotov)
        {
            if (events.NearestInferno(projectile.LastTick, projectile.EndTick, projectile.LastPosition, claimedFires) is { } fire)
            {
                claimedFires.Add(fire);
                return (fire.Tick, fire.Position, DetonationSource.Event, fire.EntityIndex);
            }

            return (null, null, DetonationSource.None, null);
        }

        // The event names the projectile's index, which is reused once it is gone: only its own life counts.
        if (GrenadeEventIndex.DetonationEventOf(projectile.ClassName) is { } name
            && events.ByEntity(name, projectile.EntityIndex, projectile.SpawnTick, projectile.EndTick + 1) is { } hit)
        {
            return (hit.Tick, hit.Position, DetonationSource.Event, null);
        }

        switch (projectile.ClassName)
        {
            case GrenadeProjectileClasses.HEGrenade when projectile.ExplodeTickBegin is > 0 and var explode:
                return (explode - serverStartTick, projectile.LastPosition, DetonationSource.Entity, null);
            case GrenadeProjectileClasses.Smoke when projectile.SmokeTickBegin is > 0 and var pop:
                return (pop - serverStartTick, projectile.SmokeDetonationPosition ?? projectile.LastPosition, DetonationSource.Entity, null);
            case GrenadeProjectileClasses.HEGrenade or GrenadeProjectileClasses.Flashbang
                when projectile.Removed && projectile.LastPosition is not null:
                return (projectile.LastTick, projectile.LastPosition, DetonationSource.LastSample, null);
            default:
                return (null, null, DetonationSource.None, null);
        }
    }

    private static WorldPoint? Point(Vector3? v) => v is { } p ? WorldPoint.From(p) : null;

    private static string ComputeVersion()
    {
        string inputs = string.Join("\n", GrenadeTables.Rulesets.Select(GrenadeTables.ReadYaml))
                        + "\n" + GrenadeTrajectoryCollector.Version.ToString(CultureInfo.InvariantCulture) + "\n" + EngineVersion;
        return WalkVersion + ";" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inputs)), 0, 8);
    }

    private static string DescribeEngine()
    {
        Assembly engine = typeof(ProjectileSampler).Assembly;
        string? version = engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version is not null && version.IndexOf('+') is var plus and > 0)
        {
            version = version[..plus];
        }

        return $"CS2DemoKit.Parser {version ?? engine.GetName().Version?.ToString() ?? "unknown"}";
    }
}
