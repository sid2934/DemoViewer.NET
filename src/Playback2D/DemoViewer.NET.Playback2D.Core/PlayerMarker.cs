namespace DemoViewer.NET.Playback2D.Core;

/// <summary>The event-driven ring state of a marker, highest precedence first.</summary>
public enum RingState
{
    /// <summary>None of the event states apply; ring is the team colour.</summary>
    Team,

    /// <summary>Shooting (m_iShotsFired increased). Yellow flash, decays over a short window.</summary>
    Shooting,

    /// <summary>Taking damage (m_iHealth decreased). Red flash, decays over a short window.</summary>
    TakingDamage,

    /// <summary>Blinded (m_flFlashDuration &gt; 0). White, alpha ∝ remaining flash.</summary>
    Blinded,

    /// <summary>Dead (m_lifeState != 0 or m_iHealth &lt;= 0). Grey / hollow.</summary>
    Dead
}

/// <summary>
///     A copied-out, immutable snapshot of one player's draw state at the current tick. Built INSIDE
///     the <c>Advanced</c> callback from the transient/pooled <c>IPlayerState</c>
///     (scalars only; never retain the pooled entity), then handed to the custom-drawn viewport. Plain
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
    // Eye pitch + duck amount, for the 3D line-of-sight ("Vision") overlay (eye height + view frustum).
    // Trailing optional so pre-vision constructions and tests are unaffected.
    float PitchDegrees = 0,
    float DuckAmount = 0,
    // Annotations anchor by SteamId because SLOTS RECYCLE, and CameraScript.FollowPlayer(steamId) needs
    // the same join. 0 = unresolved.
    ulong SteamId = 0);
