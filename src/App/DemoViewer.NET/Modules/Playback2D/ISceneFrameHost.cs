#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Annotations;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     What <see cref="Scene2DHost" /> reads from whatever it is bound to: the frame to show, the push
///     signal, and the per-push state the scene cannot derive from the frame. The 2D Playback tab is one
///     implementation; the strat canvas is the other, with no demo behind it (step-authoring.md §3.10).
///     <para>
///         Exactly the members the host touches and nothing more. Read-only on purpose: the host never
///         writes back, so a second implementation cannot be surprised by state the host pushes into it.
///         <see cref="ApplyAnnotationLevelRebuild" /> and <see cref="TryTagPositionAt" /> are the only
///         calls, and both hand the decision to the implementation.
///     </para>
///     <para>
///         App-internal, not Pipeline: it names no Avalonia type, so it would compile lower down,
///         but nothing headless consumes it and moving it there invites a Pipeline reference to the
///         App's panel view-models.
///     </para>
/// </summary>
internal interface ISceneFrameHost
{
    /// <summary>
    ///     The frame to show. A stable reference until the next <see cref="FrameUpdated" />: the render
    ///     thread replays the submitted frame, so a published frame is never mutated in place.
    /// </summary>
    Scene2DFrame CurrentFrame { get; }

    /// <summary>
    ///     The map bundle: authoritative floors and the radar binding. Null falls back to the grid and
    ///     the observed level split.
    /// </summary>
    LoadedMapAsset? MapAsset { get; }

    /// <summary>The vision engine the cone solve reads at solve time. Null draws no cones.</summary>
    VisibilityEngine? VisionEngine { get; }

    /// <summary>The ink the annotation layer draws and the tools mutate. Null mounts no ink layer.</summary>
    AnnotationSession? AnnotationSession { get; }

    /// <summary>Whether the annotation layer is enabled on the compositor.</summary>
    bool IsAnnotationsEnabled { get; }

    /// <summary>Radar image under the scene, or the grid.</summary>
    bool ShowRadar { get; }

    /// <summary>Grenade trails.</summary>
    bool ShowTrails { get; }

    /// <summary>Smokes, fires and flashes.</summary>
    bool ShowAreaEffects { get; }

    /// <summary>Vision cones.</summary>
    bool ShowVision { get; }

    /// <summary>The bomb and its ring.</summary>
    bool ShowBombRing { get; }

    /// <summary>Zone outlines. <see cref="Zones" /> is only read while this is on.</summary>
    bool ShowZones { get; }

    /// <summary>
    ///     The map's place resolver, or null when the map has none. May be a lazy parse, which is why the
    ///     host asks for it only while <see cref="ShowZones" /> is on.
    /// </summary>
    PlaceResolver? Zones { get; }

    /// <summary>
    ///     The editor the token tool drags through, or null where there are no tokens (the 2D Playback
    ///     tab). Surfaced to the tools as <see cref="IToolServices.Tokens" />.
    /// </summary>
    ITokenEditor? TokenEditor { get; }

    /// <summary>
    ///     Raised on every push AND every toggle change, on the UI thread. The host re-reads every member
    ///     above only from here and from a bind, so a toggle that does not raise it never reaches the
    ///     compositor.
    /// </summary>
    event Action? FrameUpdated;

    /// <summary>
    ///     Rebases world-anchored ink after the level set moved a band. Consumes no undo slot.
    /// </summary>
    /// <param name="zMinMap">Old quantized level ZMin → new quantized level ZMin.</param>
    void ApplyAnnotationLevelRebuild(IReadOnlyDictionary<double, double> zMinMap);

    /// <summary>
    ///     Offers a plain left click to Click To Tag Position ahead of the pointer tools. False sends the
    ///     press on to the router, which is what an implementation with nothing to tag always returns.
    /// </summary>
    /// <param name="level">The floor the clicked pane shows.</param>
    /// <param name="worldX">World X of the click.</param>
    /// <param name="worldY">World Y of the click.</param>
    bool TryTagPositionAt(MapLevel level, double worldX, double worldY);
}
