using DemoViewer.NET.Modules.Abstractions;

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>The event-driven ring state of a marker, highest precedence first.</summary>
public enum RingState
{
    /// <summary>None of the event states apply — ring is the team colour.</summary>
    Team,

    /// <summary>Shooting (m_iShotsFired increased) — yellow flash, decays over a short window.</summary>
    Shooting,

    /// <summary>Taking damage (m_iHealth decreased) — red flash, decays over a short window.</summary>
    TakingDamage,

    /// <summary>Blinded (m_flFlashDuration &gt; 0) — white, alpha ∝ remaining flash.</summary>
    Blinded,

    /// <summary>Dead (m_lifeState != 0 or m_iHealth &lt;= 0) — grey / hollow.</summary>
    Dead
}

/// <summary>
///     A copied-out, immutable snapshot of one player's draw state at the current tick. Built INSIDE
///     the <c>Advanced</c> callback from the transient/pooled <see cref="IPlayerState" />
///     (scalars only — never retain the pooled entity), then handed to the custom-drawn viewport. Plain
///     value type: no Avalonia dependency, trivially testable.
/// </summary>
public readonly record struct PlayerMarker(
    int Slot,
    int Team,
    float WorldX,
    float WorldY,
    float WorldZ,
    float YawDegrees,
    RingState Ring,
    double RingAlpha,
    string Label,
    bool IsAlive,
    // Eye pitch + duck amount — carried for the 3D line-of-sight ("Vision") overlay (eye height + view
    // frustum). Trailing optional so pre-vision constructions and tests are unaffected.
    float PitchDegrees = 0,
    float DuckAmount = 0);
