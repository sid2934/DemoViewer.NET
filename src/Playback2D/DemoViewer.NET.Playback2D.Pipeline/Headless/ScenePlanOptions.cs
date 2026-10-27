#region

using DemoViewer.NET.Playback2D.Annotations;
using DemoViewer.NET.Playback2D.Overlay;
using DemoViewer.NET.Playback2D.Query;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     Everything <see cref="SceneRenderPlan.Build" /> needs, already resolved: no argument vector and
///     no walk-up probe. Reading process state is the caller's half of the job, so <c>dv2d</c>'s parser
///     does not come along with the plan and the test lane can build the same stack without inventing
///     one. The one hole is <see cref="RenderBackendPreference.Auto" />, which the surface factory
///     resolves against <c>DV2D_RENDER_BACKEND</c> itself; a caller that must not be overridden from a
///     shell states the backend outright.
/// </summary>
internal sealed record ScenePlanOptions
{
    /// <summary>The output size, which is also the size a golden is named for.</summary>
    public required SKSizeI Size { get; init; }

    /// <summary>The map whose art to load, or null for none.</summary>
    public string? MapName { get; init; }

    /// <summary>The layer ids to register, or null for the default scene stack.</summary>
    public IReadOnlyList<string>? IncludeLayers { get; init; }

    /// <summary>The layer ids to subtract, or null for none.</summary>
    public IReadOnlyList<string>? ExcludeLayers { get; init; }

    /// <summary>The settled surface-backend preference. See <see cref="RenderBackendResolver" />.</summary>
    public RenderBackendPreference Backend { get; init; } = RenderBackendPreference.Auto;

    /// <summary>Where map art is resolved from. <see cref="AssetsRoot.None" /> loads none.</summary>
    public AssetsRoot Assets { get; init; } = AssetsRoot.None;

    /// <summary>
    ///     A user zones overlay to apply over the map's baked <c>zones.json</c>, or null for the baked
    ///     set. Its diagnostics land on <see cref="SceneRenderPlan.Zones" /> for the caller to report.
    /// </summary>
    public string? ZonesOverlayPath { get; init; }

    /// <summary>Ink to burn in, or null. A single-frame render has no demo and so no sidecar of its own.</summary>
    public AnnotationSession? Annotations { get; init; }

    /// <summary>The Query Canvas tokens to draw, or null.</summary>
    public QueryCanvasDocument? Query { get; init; }

    /// <summary>The Overlay View points to draw, or null.</summary>
    public OverlayDocument? Overlay { get; init; }

    /// <summary>Where the backend factory narrates its probe, or null to drop it.</summary>
    public Action<string>? Log { get; init; }
}
