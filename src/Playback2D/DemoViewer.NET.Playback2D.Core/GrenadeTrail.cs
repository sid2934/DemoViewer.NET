namespace DemoViewer.NET.Playback2D.Core;

/// <summary>Grenade-projectile kind, drives the trail colour (A4).</summary>
public enum GrenadeKind
{
    He,
    Flash,
    Smoke,
    Molotov,
    Decoy
}

/// <summary>One sampled point on a grenade's flight path (world space).</summary>
public readonly record struct GrenadeTrailPoint(float X, float Y, float Z);

/// <summary>
///     A grenade's flight trail (A4 overlay): the path a thrown projectile traced, accumulated LIVE per push
///     (forward-play artifact — a trail seeked-into shows the arc from the seek point forward, which is
///     incomplete, not wrong). Keyed by the projectile's network Serial (the entity index gets
///     reused on detonation). Drawn as a fading comet line; <see cref="Alpha" /> is 1 while the projectile is
///     MOVING and decays once it stops (lands / detonates / despawns) — so a smoke or decoy projectile, which
///     lingers as a live entity long after it lands, still fades its flight line instead of holding it at full
///     opacity for the cloud's whole life. Cleared wholesale on a discontinuous frame jump so a segment never
///     streaks across the map.
/// </summary>
public sealed class GrenadeTrail
{
    public GrenadeKind Kind { get; init; }

    /// <summary>The sampled flight points, oldest → newest.</summary>
    public List<GrenadeTrailPoint> Points { get; } = new(64);

    /// <summary>
    ///     Server (game) tick the projectile last MOVED (a point was appended) — drives the fade + prune
    ///     once it stops moving, independent of how long the entity itself lingers.
    /// </summary>
    public int LastTick { get; set; }

    /// <summary>
    ///     1 while the projectile is moving; fades to 0 over the fade window after it stops (lands /
    ///     detonates / despawns), then the trail is pruned.
    /// </summary>
    public double Alpha { get; set; } = 1.0;

    /// <summary>Slice assignment uses the current (newest) point's Z.</summary>
    public float CurrentZ => Points.Count > 0 ? Points[^1].Z : 0f;
}
