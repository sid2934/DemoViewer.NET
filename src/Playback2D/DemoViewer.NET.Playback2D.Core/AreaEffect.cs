namespace DemoViewer.NET.Playback2D.Core;

/// <summary>The kind of grenade area effect drawn on the viewport (A4).</summary>
public enum AreaEffectKind
{
    /// <summary>An active smoke cloud (one big translucent gray disc at the detonation centre).</summary>
    Smoke,

    /// <summary>One burning molotov/incendiary fire cell (a small translucent orange disc).</summary>
    Fire
}

/// <summary>
///     Draw-state for one grenade area effect (A4 overlay): an active smoke (<c>CSmokeGrenadeProjectile</c>,
///     centred on <c>m_vSmokeDetonationPos</c>) or one burning inferno cell (<c>CInferno.m_firePositions[i]</c>
///     where <c>m_bFireIsBurning[i]</c>). World position is networked directly — no cell reconstruction.
///     Drawn under the player markers, on the floor slice it sits on.
/// </summary>
public readonly record struct AreaEffect(
    AreaEffectKind Kind,
    float WorldX,
    float WorldY,
    float WorldZ,
    float WorldRadius);
