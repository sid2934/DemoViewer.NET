#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

/// <summary>A detonation event with the entity it names.</summary>
/// <param name="Name">The event name, e.g. <c>smokegrenade_detonate</c>.</param>
/// <param name="Tick">Frame clock.</param>
/// <param name="EntityIndex">The projectile's (or, for a fire, the inferno's) entity index.</param>
/// <param name="Position">Where it went off.</param>
public sealed record GrenadeDetonation(string Name, int Tick, int EntityIndex, Vector3 Position);

/// <summary>
///     The detonation events a walk joins, indexed once per demo: the five detonation events by name, each with
///     the entity it names. The joins are pure over this index so they are tested without a demo.
/// </summary>
public sealed class GrenadeEventIndex
{
    public const string HeDetonate = "hegrenade_detonate";
    public const string FlashDetonate = "flashbang_detonate";
    public const string SmokeDetonate = "smokegrenade_detonate";
    public const string InfernoStart = "inferno_startburn";
    public const string DecoyStarted = "decoy_started";

    private readonly Dictionary<string, List<GrenadeDetonation>> _detonations = new(StringComparer.Ordinal);

    /// <summary>Indexes the detonation rows a rules run recorded.</summary>
    /// <param name="detonations">The five detonation events' rows, in any order.</param>
    public static GrenadeEventIndex FromDetonations(IEnumerable<GrenadeDetonation> detonations)
    {
        ArgumentNullException.ThrowIfNull(detonations);
        GrenadeEventIndex index = new();
        foreach (GrenadeDetonation d in detonations)
        {
            index.Add(d.Name, d.Tick, d.EntityIndex, d.Position);
        }

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
