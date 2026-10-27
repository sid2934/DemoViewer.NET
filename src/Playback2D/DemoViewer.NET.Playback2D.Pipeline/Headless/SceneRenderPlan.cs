#region

using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Headless;

/// <summary>
///     Everything a headless render resolves once before it draws anything: the backend, the layer
///     stack, the map art and the output size. Built by <see cref="Build" /> so <c>dv2d render</c>,
///     <c>bench</c>, <c>golden</c> and the direct-execution golden suite cannot disagree about what a
///     request means.
/// </summary>
internal sealed class SceneRenderPlan : IDisposable
{
    private readonly IReadOnlyList<MapRadarImage> _radars;
    private bool _disposed;
    private Scene2DFrame? _enriched;
    private Scene2DFrame? _enrichedFrom;

    private SceneRenderPlan(ResolvedBackend backend, SceneCompositor compositor,
        HeadlessSceneRenderer renderer, IReadOnlyList<string> layerIds, AssetsRoot assets,
        LoadedMapAsset? mapAssets, ZoneLoadResult zones, SKSizeI size)
    {
        Backend = backend;
        Compositor = compositor;
        Renderer = renderer;
        LayerIds = layerIds;
        Assets = assets;
        MapAssets = mapAssets;
        Zones = zones;
        Size = size;

        // Described once: DescribeRadars materialises an array, and a bench run re-enriches the same
        // frame thousands of times.
        _radars = mapAssets is null ? [] : MapAssetPipeline.DescribeRadars(mapAssets);
        RadarBinder = mapAssets is null ? null : new MapRadarBinder(mapAssets);
    }

    /// <summary>The binder that gives each floor band its radar image, or null with no bundle.</summary>
    public ILevelRadarBinder? RadarBinder { get; }

    /// <summary>The bundle's nav-derived floor bands, or null with no bundle.</summary>
    public IReadOnlyList<FloorSlice>? AuthoritativeFloors => MapAssets?.Floors;

    /// <summary>The resolved surface backend.</summary>
    public ResolvedBackend Backend { get; }

    /// <summary>The layer stack, owned by this plan.</summary>
    public SceneCompositor Compositor { get; }

    /// <summary>The renderer, owned by this plan.</summary>
    public HeadlessSceneRenderer Renderer { get; }

    /// <summary>The registered layer ids, in draw order.</summary>
    public IReadOnlyList<string> LayerIds { get; }

    /// <summary>Where map art was resolved from.</summary>
    public AssetsRoot Assets { get; }

    /// <summary>The loaded map bundle, or null when there is none.</summary>
    public LoadedMapAsset? MapAssets { get; }

    /// <summary>
    ///     The map's zones, with the overlay applied when one was given: what feeds
    ///     <c>playback2d.zones</c>. <see cref="ZoneLoadResult.Empty" /> with no bundle or no
    ///     <c>zones.json</c>. Its <see cref="ZoneLoadResult.Diagnostics" /> are the caller's to report.
    /// </summary>
    public ZoneLoadResult Zones { get; }

    /// <summary>The output size.</summary>
    public SKSizeI Size { get; }

    /// <summary>Releases the renderer, the layer stack, the provider and the decoded radar art.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Renderer.Dispose();
        Compositor.Dispose();
        Backend.Provider.Dispose();
        MapAssets?.Dispose();
    }

    /// <summary>
    ///     Resolves a request into a backend, a stack and a renderer. Takes nothing further from the
    ///     process: <see cref="ScenePlanOptions" /> is already settled.
    /// </summary>
    /// <param name="options">The resolved request.</param>
    /// <exception cref="ArgumentException">
    ///     An id in <see cref="ScenePlanOptions.IncludeLayers" /> or
    ///     <see cref="ScenePlanOptions.ExcludeLayers" /> is not in
    ///     <see cref="SceneLayerCatalog.SceneStackIds" />.
    /// </exception>
    /// <exception cref="BackendUnavailableException">A strict backend request cannot be satisfied.</exception>
    public static SceneRenderPlan Build(ScenePlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ResolvedBackend backend = RenderBackendResolver.Resolve(options.Backend, options.Log);
        IReadOnlyList<string>? include = options.IncludeLayers;

        // The bundle comes first: the zone outlines are fed from its directory, and a stack cannot know
        // whether it can register playback2d.zones until the bundle has been looked for. The zones file
        // is only read when the layer is asked for: it is a few hundred kilobytes and a grid build, and
        // a render that did not name the layer must not pay for it or report it.
        LoadedMapAsset? mapAssets = null;
        ZoneLoadResult zones = ZoneLoadResult.Empty;
        if (options.Assets.Path is { } root && options.MapName is not null)
        {
            mapAssets = MapAssetPipeline.TryLoad(root, options.MapName);
            if (mapAssets is not null && WantsZones(include))
            {
                zones = ZoneAssetPipeline.LoadWithOverlayFile(mapAssets.BakedDir, options.ZonesOverlayPath);
            }
        }

        SceneCompositor compositor;
        try
        {
            // The SAME builder `dv2d export` and the app's export use. A second table would let a
            // golden and a real export draw two different stacks silently.
            compositor = SceneLayerCatalog.CreateSceneStack(include, options.ExcludeLayers,
                annotations: options.Annotations, zones: zones.Resolver, query: options.Query,
                overlay: options.Overlay);
        }
        catch
        {
            mapAssets?.Dispose();
            backend.Provider.Dispose();
            throw;
        }

        HeadlessSceneRenderer renderer = new(backend.Provider, compositor)
        {
            Palette = ScenePalette.Dark,
            Size = options.Size,
            Purpose = RenderPurpose.Export
        };

        // Bound exactly as SceneStage and Scene2DHost bind it: authoritative nav floors override the Z
        // histogram, and the binder gives each band its radar image. Skip it and a headless render
        // derives a different level set from the app for the same frame.
        string[] layerIds = [.. compositor.Layers.Select(static l => l.Id)];
        SceneRenderPlan plan = new(backend, compositor, renderer, layerIds, options.Assets, mapAssets,
            zones, options.Size);
        renderer.Levels.SetAuthoritativeFloors(plan.AuthoritativeFloors);
        renderer.Levels.RadarBinder = plan.RadarBinder;
        return plan;
    }

    /// <summary>
    ///     Re-attaches decoded radar art to a frame's map info. A fixture never carries pixels (only the
    ///     layer names and bands), so this is how a resolved asset root reaches the render.
    /// </summary>
    /// <param name="frame">The frame to enrich. Init-only, so an enriched copy is returned.</param>
    public Scene2DFrame WithRadarArt(Scene2DFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (MapAssets is null || _radars.Count == 0)
        {
            return frame;
        }

        // One-entry memo. A bench run re-renders the same fixture frame thousands of times, and an
        // allocation here would land straight in the bytes/frame figure the gate reads.
        if (ReferenceEquals(frame, _enrichedFrom))
        {
            return _enriched!;
        }

        // Frames are init-only by design, so enrichment builds a new one over the same lists rather
        // than mutating a published frame.
        Scene2DFrame enriched = new()
        {
            Time = frame.Time,
            Markers = frame.Markers,
            AreaEffects = frame.AreaEffects,
            Trails = frame.Trails,
            Bomb = frame.Bomb,
            KillFeed = frame.KillFeed,
            GameInfo = frame.GameInfo,
            Vision = frame.Vision,
            FollowSlot = frame.FollowSlot,
            Map = new SceneMapInfo
            {
                MapName = string.IsNullOrEmpty(frame.Map.MapName)
                    ? MapAssets.Bundle.MapName
                    : frame.Map.MapName,
                NetworkedBounds = frame.Map.NetworkedBounds,
                ObservedBounds = frame.Map.ObservedBounds,
                SectionHeights = frame.Map.SectionHeights,
                Radars = _radars
            }
        };

        _enrichedFrom = frame;
        _enriched = enriched;
        return enriched;
    }

    private static bool WantsZones(IReadOnlyList<string>? include)
    {
        if (include is null)
        {
            return false;
        }

        foreach (string id in include)
        {
            if (string.Equals(SceneLayerCatalog.Normalize(id), SceneLayerIds.Zones, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
