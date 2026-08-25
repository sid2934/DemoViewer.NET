#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Cli;

/// <summary>
///     Everything the CLI resolves once before it renders anything: the backend, the layer stack, the
///     asset root, the map art and the output size. Built by <see cref="Build" /> so <c>render</c>,
///     <c>bench</c> and <c>golden</c> cannot disagree about what a flag means.
/// </summary>
internal sealed class SceneRenderPlan : IDisposable
{
    private bool _disposed;
    private Scene2DFrame? _enriched;
    private Scene2DFrame? _enrichedFrom;

    private readonly IReadOnlyList<MapRadarImage> _radars;

    private SceneRenderPlan(ResolvedBackend backend, SceneCompositor compositor,
        HeadlessSceneRenderer renderer, IReadOnlyList<string> layerIds, AssetsRoot assets,
        LoadedMapAsset? mapAssets, SKSizeI size)
    {
        Backend = backend;
        Compositor = compositor;
        Renderer = renderer;
        LayerIds = layerIds;
        Assets = assets;
        MapAssets = mapAssets;
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

    /// <summary>The loaded map bundle, or null when there is none (or <c>--no-radar</c>).</summary>
    public LoadedMapAsset? MapAssets { get; }

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
    ///     Resolves the shared flags. Consumes <c>--cpu/--gpu/--strict-backend</c>,
    ///     <c>--assets/--no-radar</c>, <c>--layers/--exclude-layers</c>, <c>--size</c>,
    ///     <c>--layout/--level</c>.
    /// </summary>
    /// <param name="args">The parsed arguments.</param>
    /// <param name="defaultSize">The size to use when <c>--size</c> is absent.</param>
    /// <param name="mapName">The map whose art to load, or null for none.</param>
    /// <param name="entryLayers">Layer ids from a corpus entry, overridden by <c>--layers</c>.</param>
    /// <param name="allowSizeOverride">
    ///     False on the golden lane: a golden is named for its size, so <c>--size</c> there would compare
    ///     one image against a differently-named other. Leaving the option unconsumed turns it into the
    ///     usage error it is.
    /// </param>
    public static SceneRenderPlan Build(CliArgs args, SKSizeI defaultSize, string? mapName,
        IReadOnlyList<string>? entryLayers = null, bool allowSizeOverride = true)
    {
        ArgumentNullException.ThrowIfNull(args);

        SKSizeI size = allowSizeOverride ? args.Size("size", defaultSize) : defaultSize;
        RequireSingleLevelLayout(args);

        ResolvedBackend backend = BackendResolver.Resolve(args);
        AssetsRoot assets = AssetsRootResolver.Resolve(args);

        IReadOnlyList<string>? include = args.List("layers") ?? entryLayers;
        IReadOnlyList<string>? exclude = args.List("exclude-layers");

        SceneCompositor compositor;
        try
        {
            compositor = SceneLayerCatalog.Create(include, exclude);
        }
        catch (ArgumentException e)
        {
            backend.Provider.Dispose();
            throw new CliUsageException(e.Message, e);
        }

        LoadedMapAsset? mapAssets = null;
        if (assets.Source != AssetsRootSource.Disabled && assets.Path is { } root && mapName is not null)
        {
            mapAssets = MapAssetPipeline.TryLoad(root, mapName);
        }

        HeadlessSceneRenderer renderer = new(backend.Provider, compositor)
        {
            Palette = ScenePalette.Dark,
            Size = size,
            Purpose = RenderPurpose.Export
        };

        // Bound exactly as SceneStage and Scene2DHost bind it: authoritative nav floors override the Z
        // histogram, and the binder gives each band its radar image. Skipping this would make dv2d
        // derive a different level set from the app for the same frame — the one thing a headless
        // reproduction of the app's picture must not do.
        string[] layerIds = [.. compositor.Layers.Select(static l => l.Id)];
        SceneRenderPlan plan = new(backend, compositor, renderer, layerIds, assets, mapAssets, size);
        renderer.Levels.SetAuthoritativeFloors(plan.AuthoritativeFloors);
        renderer.Levels.RadarBinder = plan.RadarBinder;
        return plan;
    }

    /// <summary>
    ///     Re-attaches decoded radar art to a frame's map info. A fixture never carries pixels (only the
    ///     layer names and bands), so this is how <c>--assets</c> reaches the render.
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
        // allocation here would land straight in the bytes/frame figure the §6 gate reads.
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

    // B1's merge landed MapSpace + StackedLayout, so --layout stacked is now a real multi-pane render.
    // `single` and a per-level selection are B3's SingleLayout, which has not landed: accepting either
    // and quietly rendering the stacked set would be the worst outcome — a golden captured "with
    // --level 1" that in fact shows every level. So anything but the default is an honest exit 6.
    private static void RequireSingleLevelLayout(CliArgs args)
    {
        string layout = args.String("layout") ?? "stacked";
        string? level = args.String("level");

        if (!string.Equals(layout, "stacked", StringComparison.OrdinalIgnoreCase))
        {
            throw new BackendUnavailableException(
                $"--layout {layout} needs the level model's single-level half (B3 SingleLayout), which " +
                "is not in this build. Only --layout stacked renders today.");
        }

        if (level is not null)
        {
            throw new BackendUnavailableException(
                "--level needs the level model's single-level half (B3 SingleLayout), which is not in " +
                "this build.");
        }
    }
}
