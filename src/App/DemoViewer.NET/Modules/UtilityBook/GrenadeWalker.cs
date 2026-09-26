#region

using System.Globalization;
using System.Numerics;
using System.Reflection;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>What one walk is asked for.</summary>
/// <param name="TrajectoryStride">Keep every n-th moved sample of each flight; bounce vertices are always kept (D1).</param>
/// <param name="MaxFrames">Stop after this many frames; a projectile still alive ends as <see cref="GrenadeEndKind.DemoEnded" />.</param>
/// <param name="Inputs">The thrower inputs; null rebuilds the demo's own commands through <see cref="UserCmdReconstructor" />.</param>
/// <param name="Rounds">The round starts; null derives them with <see cref="ClipRounds.Derive(ParsedDemo)" />.</param>
/// <param name="FilterClasses">
///     Store only the classes the pawn pass reads (<see cref="EntityTracker.StoreClassFilter" />). Off until
///     the parity test pins the two walks equal row for row, because an omitted class is silent.
/// </param>
public sealed record GrenadeWalkOptions(
    int TrajectoryStride = 4,
    int MaxFrames = int.MaxValue,
    IThrowerInputSource? Inputs = null,
    IReadOnlyList<ClipRound>? Rounds = null,
    bool FilterClasses = false);

/// <summary>A walk's output: the rows, and what the header records about how they were made.</summary>
/// <param name="Rows">One per grenade, in spawn order.</param>
/// <param name="InputCoverage">Share of the demo's commands that decoded.</param>
/// <param name="InputDecoder">The input source's name.</param>
/// <param name="TrajectoryStride">The stride the trajectories were thinned at.</param>
public sealed record GrenadeWalk(IReadOnlyList<GrenadeRow> Rows, double InputCoverage, string InputDecoder, int TrajectoryStride);

/// <summary>The thrower's pawn on one frame, as far as it decoded.</summary>
public readonly record struct PawnRead(
    int Tick,
    Vector3? Position,
    float? Pitch,
    float? Yaw,
    uint? Flags,
    bool? Ducked,
    bool? Walking,
    int Team,
    float? ThrowStrength,
    bool HeldGrenade)
{
    public bool? OnGround => Flags is { } f ? (f & GrenadeRules.OnGroundFlag) != 0 : null;
}

/// <summary>The per-class fields of a projectile the engine sample does not carry.</summary>
/// <param name="IsIncendiary"><c>m_bIsIncGrenade</c> on a molotov-class projectile, read on the spawn frame.</param>
/// <param name="ExplodeTickBegin"><c>m_nExplodeEffectTickBegin</c> (server clock) on the last frame, or null.</param>
/// <param name="SmokeTickBegin"><c>m_nSmokeEffectTickBegin</c> (server clock) on the last frame, or null.</param>
/// <param name="SmokeDetonationPosition"><c>m_vSmokeDetonationPos</c> on the last frame, or null.</param>
public readonly record struct ProjectileRead(bool? IsIncendiary, int? ExplodeTickBegin, int? SmokeTickBegin, Vector3? SmokeDetonationPosition);

/// <summary>A projectile's thrower and release, decided from the events before any pawn is read.</summary>
public sealed record ThrowPlan(
    ProjectileTrack Track,
    int ThrowerSlot,
    ThrowerSource ThrowerSource,
    int ReleaseTick,
    int? ReleaseFrame,
    ReleaseSource ReleaseSource,
    int? SpeedFrame,
    string? Weapon);

/// <summary>What the read pass found: pawns by (frame, slot) and projectile fields by (index, serial).</summary>
public sealed class GrenadeReads
{
    public Dictionary<(int Frame, int Slot), PawnRead> Pawns { get; } = [];
    public Dictionary<(int Index, int Serial), ProjectileRead> Projectiles { get; } = [];
}

/// <summary>
///     Turns every grenade in a demo into one <see cref="GrenadeRow" /> (grenade-walk.md §3.2 to §3.5),
///     rebased on CS2DemoKit 0.13's <see cref="ProjectileSampler" /> (#59): the engine walks the projectile
///     slots, resolves the thrower chain and rejects zero-cell positions, and this is the thin app-side layer
///     the sampler's own contract leaves to the consumer, the release, detonation and jump-throw joins.
///     <para>
///         <b>Two passes over one held parse.</b> The sampler walks the projectiles; its tracks and the
///         events decide which frames need the thrower's pawn; one tracker pass then reads only those frames
///         (release, eight ticks before it, spawn) and the last frame of each projectile, and rebuilds the
///         commands inside each jump window through <see cref="UserCmdReconstructor" />. The design's single
///         walk with a pawn ring is not possible over the sampler, which exposes no tracker; the second
///         decode is the price, about the same as the sampler's own.
///     </para>
///     <para>
///         Every step between the passes is pure (<see cref="Plan" />, <see cref="Assemble" />), so the
///         joins are tested on synthetic samples and events without a demo.
///     </para>
/// </summary>
public static class GrenadeWalker
{
    /// <summary>Bumped when a row's meaning changes; a demo walked under another version re-indexes.</summary>
    public const string Version = "2";

    private const string ControllerClass = "CCSPlayerController";
    private const string PawnClass = "CCSPlayerPawn";

    /// <summary>The engine the rows were read with, for the sidecar header.</summary>
    public static string EngineVersion { get; } = DescribeEngine();

    /// <summary>
    ///     Walks <paramref name="demo" />. Blocking and CPU-bound, about two decodes of the demo: call it
    ///     off the UI thread.
    /// </summary>
    /// <param name="demo">The held parse.</param>
    /// <param name="options">Stride, bounds and seams; null for the defaults.</param>
    /// <param name="samples">The projectile samples; null walks the demo with <see cref="ProjectileSampler.Walk(ParsedDemo, int, int)" />.</param>
    /// <param name="ct">Checked between frames.</param>
    public static GrenadeWalk Walk(ParsedDemo demo, GrenadeWalkOptions? options = null,
        IEnumerable<ProjectileSample>? samples = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demo);
        options ??= new GrenadeWalkOptions();
        int stride = Math.Max(1, options.TrajectoryStride);

        List<ProjectileTrack> tracks = ProjectileTrack.Collect(
            samples ?? ProjectileSampler.Walk(demo, 1, options.MaxFrames), stride, ct);
        GrenadeEventIndex events = GrenadeEventIndex.From(demo.AllGameEvents);
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        List<ThrowPlan> plans = Plan(tracks, events, tick => FrameAtOrBefore(frames, tick));

        ReconstructedInputSource? rebuilt = options.Inputs is null
            ? new ReconstructedInputSource(plans
                .Where(p => p.ThrowerSlot >= 0)
                .Select(p => (p.ThrowerSlot, p.Track.SpawnTick - GrenadeRules.JumpWindowTicks, p.Track.SpawnTick)))
            : null;
        GrenadeReads reads = ReadPass(demo, plans, rebuilt, options, ct);
        IThrowerInputSource inputs = options.Inputs ?? rebuilt!;

        List<GrenadeRow> rows = Assemble(plans, reads, events, inputs, options.Rounds ?? ClipRounds.Derive(demo),
            demo.Players.Values, demo.TickRate > 0 ? demo.TickRate : 64, demo.ServerStartTick);
        return new GrenadeWalk(rows, inputs.Coverage, inputs.Decoder, stride);
    }

    /// <summary>
    ///     Decides each projectile's thrower and release from the events (§3.3 step 4): the sampler's
    ///     resolved slot, else the thrower join; then the latest matching <c>weapon_fire</c> within
    ///     <see cref="GrenadeRules.ReleaseLookbackTicks" /> before the spawn, else <c>grenade_thrown</c>, else
    ///     the spawn minus the measured gap. Resolved throwers claim their releases first, so a join never
    ///     takes a release that belongs to a known thrower.
    /// </summary>
    /// <param name="tracks">The projectiles, in spawn order.</param>
    /// <param name="events">The demo's event index.</param>
    /// <param name="frameAtOrBefore">Frame clock to the last frame index at or before it, or -1.</param>
    public static List<ThrowPlan> Plan(IReadOnlyList<ProjectileTrack> tracks, GrenadeEventIndex events, Func<int, int> frameAtOrBefore)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(frameAtOrBefore);

        HashSet<GrenadeRelease> claimed = [];
        ThrowPlan?[] plans = new ThrowPlan?[tracks.Count];

        // Two rounds: known throwers first, then the joins over what they left.
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                ProjectileTrack track = tracks[i];
                bool resolved = track.ThrowerSlot >= 0;
                if (resolved != (round == 0))
                {
                    continue;
                }

                int slot = track.ThrowerSlot;
                ThrowerSource throwerSource = resolved ? ThrowerSource.Entity : ThrowerSource.None;
                GrenadeRelease? fire = null;
                if (resolved)
                {
                    fire = GrenadeEventIndex.Latest(events.Fires, slot, track.ClassName,
                        track.SpawnTick - GrenadeRules.ReleaseLookbackTicks, track.SpawnTick, claimed);
                }
                else if (events.JoinThrower(track.ClassName, track.SpawnTick, claimed) is { } joined)
                {
                    fire = joined;
                    slot = joined.Slot;
                    throwerSource = ThrowerSource.WeaponFire;
                }

                int releaseTick;
                int? releaseFrame;
                ReleaseSource releaseSource;
                string? weapon = fire?.Weapon;
                if (fire is not null)
                {
                    claimed.Add(fire);
                    (releaseTick, releaseFrame, releaseSource) = (fire.Tick, fire.FrameIndex, ReleaseSource.WeaponFire);
                }
                else if (slot >= 0 && GrenadeEventIndex.Latest(events.Thrown, slot, track.ClassName,
                             track.SpawnTick - GrenadeRules.ReleaseLookbackTicks, track.SpawnTick, claimed) is { } thrown)
                {
                    claimed.Add(thrown);
                    weapon = thrown.Weapon;
                    (releaseTick, releaseFrame, releaseSource) = (thrown.Tick, thrown.FrameIndex, ReleaseSource.GrenadeThrown);
                }
                else
                {
                    releaseTick = track.SpawnTick - GrenadeRules.ReleaseToSpawnTicks;
                    int frame = frameAtOrBefore(releaseTick);
                    releaseFrame = frame >= 0 ? frame : null;
                    releaseSource = ReleaseSource.SpawnOffset;
                }

                int speedFrame = frameAtOrBefore(releaseTick - GrenadeRules.SpeedWindowTicks);
                plans[i] = new ThrowPlan(track, slot, throwerSource, releaseTick, releaseFrame, releaseSource,
                    speedFrame >= 0 ? speedFrame : null, weapon);
            }
        }

        return [.. plans.Select(p => p!)];
    }

    /// <summary>
    ///     Builds the rows from the plans and what the read pass found (§3.3 step 5, §3.4, §3.5). Pure.
    /// </summary>
    /// <param name="plans">From <see cref="Plan" />.</param>
    /// <param name="reads">The pawn and projectile reads.</param>
    /// <param name="events">The demo's event index.</param>
    /// <param name="inputs">The thrower inputs.</param>
    /// <param name="rounds">Round starts, frame clock.</param>
    /// <param name="players">The demo's players, for the thrower's SteamID.</param>
    /// <param name="tickRate">Ticks per second.</param>
    /// <param name="serverStartTick">Subtracted from the server-clock effect ticks.</param>
    public static List<GrenadeRow> Assemble(IReadOnlyList<ThrowPlan> plans, GrenadeReads reads, GrenadeEventIndex events,
        IThrowerInputSource inputs, IReadOnlyList<ClipRound> rounds, IEnumerable<PlayerInfo> players, int tickRate,
        int serverStartTick)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(players);

        Dictionary<int, ulong> steamBySlot = [];
        foreach (PlayerInfo player in players)
        {
            if (player.SteamId64 != 0)
            {
                steamBySlot.TryAdd(player.Slot, player.SteamId64);
            }
        }

        HashSet<GrenadeDetonation> claimedFires = [];
        List<GrenadeRow> rows = new(plans.Count);
        foreach (ThrowPlan plan in plans)
        {
            ProjectileTrack track = plan.Track;
            int slot = plan.ThrowerSlot;
            PawnRead? release = Pawn(reads, plan.ReleaseFrame, slot);
            PawnRead? before = Pawn(reads, plan.SpeedFrame, slot);
            PawnRead? atSpawn = Pawn(reads, track.SpawnFrame, slot);
            ProjectileRead projectile = reads.Projectiles.GetValueOrDefault((track.EntityIndex, track.Serial));

            GrenadeKind kind = GrenadeRules.KindOf(track.ClassName, plan.Weapon, projectile.IsIncendiary) ?? GrenadeKind.Smoke;
            float? speed = GrenadeRules.HorizontalSpeed(before?.Position, before?.Tick ?? 0, release?.Position,
                release?.Tick ?? 0, tickRate);

            bool covered = slot >= 0 && inputs.HasCoverage(slot, track.SpawnTick - GrenadeRules.JumpWindowTicks, track.SpawnTick);
            int? press = covered ? inputs.FindJumpPress(slot, track.SpawnTick - GrenadeRules.JumpWindowTicks, track.SpawnTick) : null;
            (bool jumpThrow, JumpThrowSource jumpSource, int? pressTick) =
                GrenadeRules.ClassifyJumpThrow(covered, press, atSpawn?.OnGround);

            (int? detTick, Vector3? detPosition, DetonationSource detSource, int? inferno) =
                ResolveDetonation(track, projectile, events, serverStartTick, claimedFires);

            int restTick = track.LastMovedTick;
            int airEnd = detTick is { } d && d < restTick ? d : restTick;
            GrenadeEndKind endKind = detTick is not null ? GrenadeEndKind.Detonated
                : track.Removed ? GrenadeEndKind.Removed
                : GrenadeEndKind.DemoEnded;

            rows.Add(new GrenadeRow
            {
                Id = track.Id,
                Kind = kind,
                ThrowerSlot = slot,
                ThrowerSteamId64 = slot >= 0 && steamBySlot.TryGetValue(slot, out ulong steam)
                    ? steam.ToString(CultureInfo.InvariantCulture)
                    : null,
                ThrowerTeam = atSpawn is { Team: > 0 } spawned ? spawned.Team : release?.Team ?? 0,
                ThrowerSource = slot >= 0 ? plan.ThrowerSource : ThrowerSource.None,
                RoundNumber = GrenadeRules.RoundAt(rounds, track.SpawnTick),
                ReleaseTick = plan.ReleaseTick,
                ReleaseSource = plan.ReleaseSource,
                ReleasePosition = Point(release?.Position),
                ReleaseEyePitch = release?.Pitch,
                ReleaseEyeYaw = release?.Yaw,
                ReleaseOnGround = release?.OnGround,
                ReleaseCrouched = release?.Ducked,
                ThrowStrength = release?.ThrowStrength,
                ThrowStrengthClass = GrenadeRules.ClassifyStrength(release?.ThrowStrength, release?.HeldGrenade ?? false),
                Movement = GrenadeRules.ClassifyMovement(speed),
                SpeedAtRelease = speed,
                SpawnTick = track.SpawnTick,
                SpawnPosition = Point(track.InitialPosition),
                SpawnVelocity = Point(track.InitialVelocity),
                ThrowerPositionAtSpawn = Point(atSpawn?.Position),
                ThrowerOnGroundAtSpawn = atSpawn?.OnGround,
                JumpThrow = jumpThrow,
                JumpThrowSource = jumpSource,
                JumpPressTick = pressTick,
                Trajectory = track.Trajectory,
                BounceCount = track.MaxBounces,
                AirTimeTicks = Math.Max(0, airEnd - track.SpawnTick),
                DetonationTick = detTick,
                DetonationPosition = Point(detPosition),
                DetonationSource = detSource,
                EndTick = track.EndTick,
                EndKind = endKind,
                InfernoEntityIndex = inferno
            });
        }

        return rows;
    }

    /// <summary>
    ///     A projectile's detonation per the §2.4 table: the kind's event by entity index (a fire by tick,
    ///     since <c>inferno_startburn</c> names the inferno), else the entity's effect-tick field, else for
    ///     an HE or a flash the last position seen. Pure.
    /// </summary>
    public static (int? Tick, Vector3? Position, DetonationSource Source, int? InfernoIndex) ResolveDetonation(
        ProjectileTrack track, ProjectileRead projectile, GrenadeEventIndex events, int serverStartTick,
        HashSet<GrenadeDetonation> claimedFires)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(claimedFires);

        if (track.ClassName == GrenadeProjectileClasses.Molotov)
        {
            if (events.NearestInferno(track.LastTick, track.EndTick, track.LastPosition, claimedFires)
                is { } fire)
            {
                claimedFires.Add(fire);
                return (fire.Tick, fire.Position, DetonationSource.Event, fire.EntityIndex);
            }

            return (null, null, DetonationSource.None, null);
        }

        // The event names the projectile's index, which is reused once it is gone: only its own life counts.
        if (GrenadeEventIndex.DetonationEventOf(track.ClassName) is { } name
            && events.ByEntity(name, track.EntityIndex, track.SpawnTick, track.EndTick + 1) is { } hit)
        {
            return (hit.Tick, hit.Position, DetonationSource.Event, null);
        }

        switch (track.ClassName)
        {
            case GrenadeProjectileClasses.HEGrenade when projectile.ExplodeTickBegin is > 0 and var explode:
                return (explode - serverStartTick, track.LastPosition, DetonationSource.Entity, null);
            case GrenadeProjectileClasses.Smoke when projectile.SmokeTickBegin is > 0 and var pop:
                return (pop - serverStartTick, projectile.SmokeDetonationPosition ?? track.LastPosition, DetonationSource.Entity, null);
            case GrenadeProjectileClasses.HEGrenade or GrenadeProjectileClasses.Flashbang
                when track.Removed && track.LastPosition is not null:
                return (track.LastTick, track.LastPosition, DetonationSource.LastSample, null);
            default:
                return (null, null, DetonationSource.None, null);
        }
    }

    /// <summary>The last frame index whose tick is at or before <paramref name="tick" />, or -1 before the first.</summary>
    /// <param name="frames">The frame list, tick-ordered.</param>
    /// <param name="tick">Frame clock.</param>
    public static int FrameAtOrBefore(IReadOnlyList<DemoFrame> frames, int tick)
    {
        ArgumentNullException.ThrowIfNull(frames);
        int lo = 0, hi = frames.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].ServerTick <= tick)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    ///     The one tracker pass: advances from frame 0 to the last frame any plan needs, reading the thrower
    ///     pawns and projectile fields on the frames the plans name, and feeding every frame's commands to the
    ///     reconstructor when the walk rebuilds its own inputs.
    /// </summary>
    private static GrenadeReads ReadPass(ParsedDemo demo, IReadOnlyList<ThrowPlan> plans, ReconstructedInputSource? inputs,
        GrenadeWalkOptions options, CancellationToken ct)
    {
        GrenadeReads reads = new();
        Dictionary<int, List<int>> pawnFrames = [];
        Dictionary<int, List<(ProjectileTrack Track, bool Spawn)>> projectileFrames = [];
        int lastFrame = -1;
        int lastWindowTick = int.MinValue;

        foreach (ThrowPlan plan in plans)
        {
            ProjectileTrack track = plan.Track;
            if (plan.ThrowerSlot >= 0)
            {
                foreach (int? frame in new[] { plan.ReleaseFrame, plan.SpeedFrame, (int?)track.SpawnFrame })
                {
                    if (frame is { } f)
                    {
                        Add(pawnFrames, f, plan.ThrowerSlot);
                        lastFrame = Math.Max(lastFrame, f);
                    }
                }

                lastWindowTick = Math.Max(lastWindowTick, track.SpawnTick);
            }

            Add(projectileFrames, track.SpawnFrame, (track, true));
            Add(projectileFrames, track.LastFrame, (track, false));
            lastFrame = Math.Max(lastFrame, track.LastFrame);
        }

        IReadOnlyList<DemoFrame> frames = demo.Frames;
        if (inputs is not null && lastWindowTick > int.MinValue)
        {
            lastFrame = Math.Max(lastFrame, Math.Min(frames.Count - 1, FrameAtOrBefore(frames, lastWindowTick) + 1));
        }

        lastFrame = Math.Min(lastFrame, Math.Min(frames.Count, options.MaxFrames) - 1);
        if (lastFrame < 0)
        {
            return reads;
        }

        EntityTracker tracker = options.FilterClasses
            ? new EntityTracker { StoreClassFilter = FilteredClasses() }
            : EntityTrackerFactory.CreateCurated();
        UserCmdReconstructor? reconstructor = inputs is null ? null : new UserCmdReconstructor();
        int serverStartTick = demo.ServerStartTick;

        for (int i = 0; i <= lastFrame; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            DemoFrame frame = frames[i];
            tracker.AdvanceOneFrame(frame);
            if (reconstructor is not null)
            {
                foreach (ReconstructedUserCmd command in reconstructor.AdvanceOneFrame(frame))
                {
                    inputs!.Observe(command, serverStartTick);
                }
            }

            if (pawnFrames.TryGetValue(i, out List<int>? slots))
            {
                foreach (int slot in slots)
                {
                    if (!reads.Pawns.ContainsKey((i, slot)) && PawnLookup.ResolvePawn(tracker, slot) is { } pawn)
                    {
                        reads.Pawns[(i, slot)] = ReadPawn(tracker, pawn, frame.ServerTick);
                    }
                }
            }

            if (projectileFrames.TryGetValue(i, out List<(ProjectileTrack Track, bool Spawn)>? wanted))
            {
                foreach ((ProjectileTrack track, bool spawn) in wanted)
                {
                    ReadProjectile(tracker, track, spawn, reads);
                }
            }
        }

        if (reconstructor is not null)
        {
            inputs!.Finish(reconstructor.Stats);
        }

        return reads;
    }

    private static PawnRead ReadPawn(EntityTracker tracker, EntityState pawn, int tick)
    {
        float? strength = null;
        bool heldGrenade = false;
        if (pawn.TryGetValue("m_pWeaponServices.m_hActiveWeapon", out object? handle) && handle is not null
                                                                                       && PawnLookup.ResolveHandle(tracker, handle) is { } weapon)
        {
            heldGrenade = GrenadeRules.IsGrenadeWeaponClass(weapon.ClassName);
            strength = EntityReads.Float(weapon, "m_flThrowStrength");
        }

        Vector3? eye = EntityReads.Vector(pawn, "m_angEyeAngles");
        uint? flags = EntityReads.UInt(pawn, "m_fFlags");
        return new PawnRead(tick,
            PositionUtil.CellToWorld(pawn),
            eye?.X,
            eye?.Y,
            flags,
            flags is { } f ? (f & GrenadeRules.DuckingFlag) != 0 : EntityReads.Bool(pawn, "m_pMovementServices.m_bDucked"),
            EntityReads.Bool(pawn, "m_bIsWalking"),
            EntityReads.Int(pawn, "m_iTeamNum") ?? 0,
            strength,
            heldGrenade);
    }

    private static void ReadProjectile(EntityTracker tracker, ProjectileTrack track, bool spawn, GrenadeReads reads)
    {
        if (tracker.CurrentEntities[track.EntityIndex] is not { } entity || entity.Serial != track.Serial
                                                                         || !string.Equals(entity.ClassName, track.ClassName, StringComparison.Ordinal))
        {
            return;
        }

        ProjectileRead current = reads.Projectiles.GetValueOrDefault((track.EntityIndex, track.Serial));
        reads.Projectiles[(track.EntityIndex, track.Serial)] = spawn
            ? current with { IsIncendiary = EntityReads.Bool(entity, "m_bIsIncGrenade") ?? current.IsIncendiary }
            : current with
            {
                ExplodeTickBegin = EntityReads.Int(entity, "m_nExplodeEffectTickBegin"),
                SmokeTickBegin = EntityReads.Int(entity, "m_nSmokeEffectTickBegin"),
                SmokeDetonationPosition = EntityReads.Vector(entity, "m_vSmokeDetonationPos"),
                IsIncendiary = current.IsIncendiary ?? EntityReads.Bool(entity, "m_bIsIncGrenade")
            };
    }

    private static HashSet<string> FilteredClasses()
    {
        HashSet<string> classes = new(StringComparer.Ordinal) { PawnClass, ControllerClass };
        classes.UnionWith(GrenadeProjectileClasses.All);
        classes.UnionWith(GrenadeRules.GrenadeWeaponClasses);
        return classes;
    }

    private static PawnRead? Pawn(GrenadeReads reads, int? frame, int slot) =>
        frame is { } f && slot >= 0 && reads.Pawns.TryGetValue((f, slot), out PawnRead read) ? read : null;

    private static WorldPoint? Point(Vector3? v) => v is { } p ? WorldPoint.From(p) : null;

    private static void Add<T>(Dictionary<int, List<T>> map, int key, T value)
    {
        if (!map.TryGetValue(key, out List<T>? list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(value);
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

/// <summary>Seen-gated, boxing-tolerant reads of one entity field.</summary>
internal static class EntityReads
{
    public static Vector3? Vector(EntityState entity, string path) =>
        entity.TryGetValue(path, out object? value) && value is Vector3 v ? v : null;

    public static float? Float(EntityState entity, string path) =>
        entity.TryGetValue(path, out object? value)
            ? value switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                _ => null
            }
            : null;

    public static int? Int(EntityState entity, string path) =>
        entity.TryGetValue(path, out object? value)
            ? value switch
            {
                int i => i,
                uint u => (int)u,
                short s => s,
                ushort us => us,
                byte b => b,
                sbyte sb => sb,
                long l => (int)l,
                ulong ul => (int)ul,
                _ => null
            }
            : null;

    public static uint? UInt(EntityState entity, string path) =>
        entity.TryGetValue(path, out object? value)
            ? value switch
            {
                uint u => u,
                int i => (uint)i,
                ulong ul => (uint)ul,
                long l => (uint)l,
                ushort us => us,
                byte b => b,
                _ => null
            }
            : null;

    public static bool? Bool(EntityState entity, string path) =>
        entity.TryGetValue(path, out object? value)
            ? value switch
            {
                bool b => b,
                int i => i != 0,
                uint u => u != 0,
                byte b => b != 0,
                _ => null
            }
            : null;
}
