namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     The per-map, per-demo static facts a scene needs: identity, the networked and observed world
///     extents, the networked floor boundaries, and the decoded radar layers.
///     <para>
///         Floor <b>inputs</b> only — <see cref="SectionHeights" /> plus the marker Zs on the frame.
///         Resolved levels are derived by B1's <c>MapSpaceFactory</c> and are deliberately absent from
///         the frame (decision D3).
///     </para>
/// </summary>
public sealed class SceneMapInfo
{
    /// <summary>The all-unknown map info a frame carries before anything is read.</summary>
    public static readonly SceneMapInfo Unknown = new();

    /// <summary>The map's name (e.g. <c>de_mirage</c>), or empty when not yet known.</summary>
    public string MapName { get; init; } = "";

    /// <summary>
    ///     The map's REAL networked world-space X/Y bounds — <c>m_vMinimapMins</c> / <c>m_vMinimapMaxs</c>
    ///     off the game-rules entity. Null until read; the camera rigs frame these exactly when present.
    /// </summary>
    public WorldBounds? NetworkedBounds { get; init; }

    /// <summary>
    ///     The running extent of every position observed so far — the Map-mode fallback used when
    ///     <see cref="NetworkedBounds" /> is absent. Only ever widened.
    /// </summary>
    public WorldBounds ObservedBounds { get; init; } = WorldBounds.Default;

    /// <summary>
    ///     The map's networked Z-floor boundaries (<c>m_MinimapVerticalSectionHeights</c>), strictly
    ///     ascending with sentinels dropped, or null when the map publishes fewer than two.
    /// </summary>
    public IReadOnlyList<double>? SectionHeights { get; init; }

    /// <summary>The decoded radar layers for this map, lowest band first. Empty when no bundle exists.</summary>
    public IReadOnlyList<MapRadarImage> Radars { get; init; } = [];
}
