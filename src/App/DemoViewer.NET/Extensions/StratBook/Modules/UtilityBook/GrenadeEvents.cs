#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;

#endregion

namespace DemoViewer.NET.Modules.UtilityBook;

/// <summary>A grenade <c>weapon_fire</c> or <c>grenade_thrown</c>: who released what, when.</summary>
/// <param name="Slot">The thrower's player slot (<c>UserId</c>).</param>
/// <param name="FrameIndex"><c>GameEvent.FrameNumber</c>: the frame the event arrived on.</param>
/// <param name="Tick">Frame clock.</param>
/// <param name="ProjectileClass">The projectile class it throws.</param>
/// <param name="Weapon">The event's weapon name, which tells a molotov from an incendiary.</param>
public sealed record GrenadeRelease(int Slot, int FrameIndex, int Tick, string ProjectileClass, string Weapon);

/// <summary>A detonation event with the entity it names.</summary>
/// <param name="Name">The event name, e.g. <c>smokegrenade_detonate</c>.</param>
/// <param name="Tick">Frame clock.</param>
/// <param name="EntityIndex">The projectile's (or, for a fire, the inferno's) entity index.</param>
/// <param name="Position">Where it went off.</param>
public sealed record GrenadeDetonation(string Name, int Tick, int EntityIndex, Vector3 Position);

/// <summary>
///     The events a walk joins, indexed once per demo (grenade-walk.md §3.3): grenade releases per slot,
///     <c>grenade_thrown</c> where the source has it (HLTV only), and the five detonation events. The joins
///     are pure over this index so they are tested without a demo.
/// </summary>
public sealed class GrenadeEventIndex
{
    public const string HeDetonate = "hegrenade_detonate";
    public const string FlashDetonate = "flashbang_detonate";
    public const string SmokeDetonate = "smokegrenade_detonate";
    public const string InfernoStart = "inferno_startburn";
    public const string DecoyStarted = "decoy_started";

    private readonly Dictionary<string, List<GrenadeDetonation>> _detonations = new(StringComparer.Ordinal);
    private readonly List<GrenadeRelease> _fires = [];
    private readonly List<GrenadeRelease> _thrown = [];

    /// <summary>Every grenade <c>weapon_fire</c>, in tick order.</summary>
    public IReadOnlyList<GrenadeRelease> Fires => _fires;

    /// <summary>Every <c>grenade_thrown</c>, in tick order; empty on GOTV.</summary>
    public IReadOnlyList<GrenadeRelease> Thrown => _thrown;

    /// <summary>Indexes a demo's events.</summary>
    /// <param name="events"><c>ParsedDemo.AllGameEvents</c> or a synthetic list.</param>
    public static GrenadeEventIndex From(IEnumerable<GameEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        GrenadeEventIndex index = new();
        foreach (GameEvent e in events)
        {
            switch (e.Payload)
            {
                case WeaponFireEvent fire when GrenadeRules.ProjectileClassOfWeapon(fire.Weapon) is { } cls:
                    index._fires.Add(new GrenadeRelease(fire.UserId, e.FrameNumber, e.GameTick, cls, fire.Weapon));
                    break;
                case GrenadeThrownEvent thrown when GrenadeRules.ProjectileClassOfWeapon(thrown.Weapon) is { } cls:
                    index._thrown.Add(new GrenadeRelease(thrown.UserId, e.FrameNumber, e.GameTick, cls, thrown.Weapon));
                    break;
                case HegrenadeDetonateEvent he:
                    index.Add(HeDetonate, e.GameTick, he.EntityId, new Vector3(he.X, he.Y, he.Z));
                    break;
                case FlashbangDetonateEvent flash:
                    index.Add(FlashDetonate, e.GameTick, flash.EntityId, new Vector3(flash.X, flash.Y, flash.Z));
                    break;
                case SmokeGrenadeDetonateEvent smoke:
                    index.Add(SmokeDetonate, e.GameTick, smoke.EntityId, new Vector3(smoke.X, smoke.Y, smoke.Z));
                    break;
                case InfernoStartburnEvent fire:
                    index.Add(InfernoStart, e.GameTick, fire.EntityId, new Vector3(fire.X, fire.Y, fire.Z));
                    break;
                case DecoyStartedEvent decoy:
                    index.Add(DecoyStarted, e.GameTick, decoy.EntityId, new Vector3(decoy.X, decoy.Y, decoy.Z));
                    break;
            }
        }

        index._fires.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        index._thrown.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        foreach (List<GrenadeDetonation> list in index._detonations.Values)
        {
            list.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        }

        return index;
    }

    /// <summary>The detonation event a projectile class answers to, or null for a molotov (joined by tick instead).</summary>
    public static string? DetonationEventOf(string className) => className switch
    {
        GrenadeProjectileClasses.HEGrenade => HeDetonate,
        GrenadeProjectileClasses.Flashbang => FlashDetonate,
        GrenadeProjectileClasses.Smoke => SmokeDetonate,
        GrenadeProjectileClasses.Decoy => DecoyStarted,
        _ => null
    };

    /// <summary>
    ///     The latest unclaimed release of <paramref name="projectileClass" /> by <paramref name="slot" /> in
    ///     <c>[fromTick, toTick]</c>, from <paramref name="releases" />.
    /// </summary>
    public static GrenadeRelease? Latest(IReadOnlyList<GrenadeRelease> releases, int slot, string projectileClass,
        int fromTick, int toTick, IReadOnlySet<GrenadeRelease> claimed)
    {
        ArgumentNullException.ThrowIfNull(releases);
        GrenadeRelease? best = null;
        foreach (GrenadeRelease r in releases)
        {
            if (r.Tick > toTick)
            {
                break;
            }

            if (r.Tick >= fromTick && r.Slot == slot
                                   && string.Equals(r.ProjectileClass, projectileClass, StringComparison.Ordinal)
                                   && !claimed.Contains(r))
            {
                best = r;
            }
        }

        return best;
    }

    /// <summary>
    ///     The thrower join for a projectile whose <c>m_hThrower</c> never resolved: the unclaimed grenade
    ///     <c>weapon_fire</c> of the same family in <c>[spawn - 15, spawn - 6]</c>, the one nearest the
    ///     measured seven-tick gap when several fit.
    /// </summary>
    public GrenadeRelease? JoinThrower(string projectileClass, int spawnTick, IReadOnlySet<GrenadeRelease> claimed)
    {
        GrenadeRelease? best = null;
        int bestGap = int.MaxValue;
        foreach (GrenadeRelease r in _fires)
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

    /// <summary>The first <paramref name="name" /> event naming <paramref name="entityIndex" /> in <c>[fromTick, toTick]</c>.</summary>
    public GrenadeDetonation? ByEntity(string name, int entityIndex, int fromTick, int toTick)
    {
        if (!_detonations.TryGetValue(name, out List<GrenadeDetonation>? list))
        {
            return null;
        }

        foreach (GrenadeDetonation d in list)
        {
            if (d.Tick > toTick)
            {
                break;
            }

            if (d.Tick >= fromTick && d.EntityIndex == entityIndex)
            {
                return d;
            }
        }

        return null;
    }

    /// <summary>
    ///     The unclaimed <c>inferno_startburn</c> nearest a molotov's end: within
    ///     <see cref="GrenadeRules.InfernoJoinTicks" /> of <c>[lastTick, endTick]</c>, the closest in tick
    ///     then in distance from <paramref name="lastPosition" />. The event names no thrower, so this is the
    ///     only way to tie a fire to its grenade.
    /// </summary>
    public GrenadeDetonation? NearestInferno(int lastTick, int endTick, Vector3? lastPosition, IReadOnlySet<GrenadeDetonation> claimed)
    {
        if (!_detonations.TryGetValue(InfernoStart, out List<GrenadeDetonation>? list))
        {
            return null;
        }

        GrenadeDetonation? best = null;
        (int Gap, float Distance) bestKey = (int.MaxValue, float.MaxValue);
        foreach (GrenadeDetonation d in list)
        {
            if (d.Tick > endTick + GrenadeRules.InfernoJoinTicks)
            {
                break;
            }

            if (d.Tick < lastTick - GrenadeRules.InfernoJoinTicks || claimed.Contains(d))
            {
                continue;
            }

            int gap = d.Tick < lastTick ? lastTick - d.Tick : d.Tick > endTick ? d.Tick - endTick : 0;
            float distance = lastPosition is { } p ? Vector3.Distance(p, d.Position) : 0;
            if (gap < bestKey.Gap || (gap == bestKey.Gap && distance < bestKey.Distance))
            {
                bestKey = (gap, distance);
                best = d;
            }
        }

        return best;
    }

    private void Add(string name, int tick, int entityIndex, Vector3 position)
    {
        if (!_detonations.TryGetValue(name, out List<GrenadeDetonation>? list))
        {
            list = [];
            _detonations[name] = list;
        }

        list.Add(new GrenadeDetonation(name, tick, entityIndex, position));
    }
}
