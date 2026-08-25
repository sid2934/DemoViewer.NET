#region

using System.Collections.Immutable;
using DemoViewer.NET.Playback2D.Core.Levels;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Export;

/// <summary>
///     How the camera behaves for the whole export — design §5.7's
///     <c>Fixed(transform) | FollowPlayer(steamId) | MirrorLiveView</c>.
///     <para>
///         Every case is a pure function of the request plus the frame, so two runs of the same request
///         produce the same framing (design §5.1). Nothing here reads a wall clock or a live control.
///     </para>
/// </summary>
public abstract record CameraScript
{
    // Closed hierarchy: the three nested records below are the only cases, and a resolver switching on
    // them is exhaustive.
    private CameraScript()
    {
    }

    /// <summary>
    ///     Per-level transforms held for the whole export.
    ///     <para>
    ///         Keyed by <see cref="MapLevelId" /> and not by index (integrator correction 5): a level set
    ///         that gains a floor mid-export must not slide every camera down one band, which is design
    ///         risk 5. A level with no entry keeps whatever fit its pane was created with.
    ///     </para>
    /// </summary>
    /// <param name="PaneTransforms">Transform per level id. Re-fitted to the export pane size.</param>
    public sealed record Fixed(IReadOnlyDictionary<MapLevelId, ViewportTransform> PaneTransforms) : CameraScript;

    /// <summary>
    ///     Follow one player by SteamId. Holds the last framing while the target is unresolvable — a
    ///     player who has not spawned, or a SteamId that is not in this demo — and keeps following the
    ///     gray last-known marker after a death, exactly as the interactive rig does.
    /// </summary>
    /// <param name="SteamId">The SteamID64 to follow.</param>
    /// <param name="DeadzoneHalfExtentWorld">
    ///     Half-extent of the box the marker may move inside before the camera recentres. The default is
    ///     deliberately calm for a video: small strafes do not drag the map. 0 reproduces the pre-v2
    ///     always-centred feel.
    /// </param>
    public sealed record FollowPlayer(ulong SteamId, double DeadzoneHalfExtentWorld = 900d) : CameraScript;

    /// <summary>
    ///     The live view's framing, <b>captured once</b> when the user pressed Start and never re-read
    ///     (plan D12). Panning the real window during the export changes nothing.
    ///     <para>
    ///         Behaviourally identical to <see cref="Fixed" /> once captured; it stays a distinct case so
    ///         the dialog can label it and so a serialized headless request can refuse it with
    ///         "mirror-live-view has no meaning without a running window" instead of quietly rendering
    ///         some default camera.
    ///     </para>
    /// </summary>
    /// <param name="Panes">One entry per live pane, in the live host's order.</param>
    /// <param name="DisplayMode">The host's level-display mode at capture time.</param>
    public sealed record MirrorLiveView(
        ImmutableArray<PaneCameraSnapshot> Panes,
        LevelDisplayMode DisplayMode) : CameraScript;
}

/// <summary>One live pane's camera, frozen at export start.</summary>
/// <param name="LevelId">Which level the pane was showing.</param>
/// <param name="Transform">Its world→screen transform, at the live pane's pixel size.</param>
/// <param name="ManualOverride">Whether the user had panned/zoomed that pane by hand.</param>
public readonly record struct PaneCameraSnapshot(
    MapLevelId LevelId,
    ViewportTransform Transform,
    bool ManualOverride);
