#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Layers;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Core.Vision;
using DemoViewer.NET.Playback2D.Pipeline;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     The seven production layers wired exactly as <c>Scene2DHost</c> wires them, over a CPU surface
///     and with no Avalonia anywhere. Goldens, determinism, allocation and budget tests all build one of
///     these, so none of them can quietly test a different layer stack from the one that ships.
///     <para>
///         <b>That last sentence is now enforced rather than asserted in prose</b> (D6 G-3). The array
///         below is hand-written — deliberately, because this class needs typed handles and a
///         reverse-registration mode that <c>SceneLayerCatalog.CreateSceneStack</c> cannot give it — so
///         <c>SceneStageParityTests</c> pins its id set to <see cref="SceneLayerCatalog.SceneStackIds" />
///         minus the opt-in four. Add a scene layer to the catalog without adding it here and that test
///         goes red, which is what the doc claimed and nothing checked.
///     </para>
/// </summary>
internal sealed class SceneStage : IDisposable
{
    private readonly SceneCompositor _compositor;
    private readonly CpuSurfaceProvider _provider = new();
    private readonly TextBlobCache _text = new();

    /// <summary>Builds the production layer stack over a CPU surface.</summary>
    /// <param name="size">Surface size.</param>
    /// <param name="vision">Vision solver, or null for none.</param>
    /// <param name="palette">Palette; dark when null.</param>
    /// <param name="options">Compositor caching policy.</param>
    /// <param name="reverseRegistration">Registers the layers backwards, to prove sort order wins.</param>
    /// <param name="extra">
    ///     Additional layers registered alongside the seven. B2's ink layer needs a session, so it cannot
    ///     be one of the fixed seven — but it must still be exercised over the SAME stack the app ships.
    /// </param>
    public SceneStage(SKSizeI size, IVisionSolver? vision = null, ScenePalette? palette = null,
        SceneCompositorOptions? options = null, bool reverseRegistration = false,
        params ISceneLayer[] extra)
    {
        Smoother = new MarkerSmoother();
        Radar = new RadarLayer();
        Markers = new MarkerLayer(Smoother, _text);
        Vision = new VisionLayer(vision, Smoother);
        FloorLabel = new FloorLabelLayer(_text);

        ISceneLayer[] layers =
        [
            Radar, new TrailLayer(), new AreaEffectLayer(), Vision, Markers, new BombLayer(), FloorLabel
        ];
        if (reverseRegistration)
        {
            Array.Reverse(layers);
        }

        _compositor = new SceneCompositor(options);
        foreach (ISceneLayer layer in layers)
        {
            _compositor.Add(layer);
        }

        foreach (ISceneLayer layer in extra ?? [])
        {
            _compositor.Add(layer);
        }

        Renderer = new HeadlessSceneRenderer(_compositor, _provider, new StackedLayout(),
            palette ?? ScenePalette.Dark)
        {
            Size = size,
            Purpose = RenderPurpose.Export
        };

        // Exactly as Scene2DHost wires it (B3 T3), so the level-crossing snap and its per-frame cost
        // are inside the allocation and budget gates rather than beside them.
        Smoother.LevelCrossings = Renderer.Crossings;
    }

    public SceneCompositor Compositor => _compositor;
    public HeadlessSceneRenderer Renderer { get; }
    public MarkerSmoother Smoother { get; }
    public RadarLayer Radar { get; }
    public MarkerLayer Markers { get; }
    public VisionLayer Vision { get; }
    public FloorLabelLayer FloorLabel { get; }

    /// <summary>The map bundle bound to this stage, when one was loaded. Owned; disposed with the stage.</summary>
    public LoadedMapAsset? MapAsset { get; private set; }

    public void Dispose()
    {
        Renderer.Dispose();
        _compositor.Dispose();
        _text.Dispose();
        _provider.Dispose();
        MapAsset?.Dispose();
    }

    /// <summary>
    ///     Loads the baked bundle for a map from the repo's committed <c>assets/</c> tree and binds it
    ///     the way the host does: authoritative floor bands plus a radar binder. Returns false when the
    ///     map has no bundle, which leaves the scene on its grid fallback.
    /// </summary>
    /// <param name="mapName">e.g. <c>de_nuke</c>.</param>
    /// <param name="bindRadar">
    ///     False loads the bundle's floors but binds no radar images, which is how the
    ///     <c>nuke-multilevel-noradar</c> corpus entry pins the visible no-radar state — a map whose
    ///     bundle has floors but no usable pictures is a real shape, and the canvas must fall through to
    ///     the grid rather than draw nothing.
    /// </param>
    public bool TryBindMap(string? mapName, bool bindRadar = true)
    {
        if (string.IsNullOrEmpty(mapName))
        {
            return false;
        }

        MapAsset = MapAssetPipeline.TryLoad(mapName);
        if (MapAsset is null)
        {
            return false;
        }

        Renderer.Levels.SetAuthoritativeFloors(MapAsset.Floors);
        Renderer.Levels.RadarBinder = bindRadar ? new MapRadarBinder(MapAsset) : null;
        Radar.RadarBoundsOverride = MapAssetPipeline.RadarBounds(MapAsset);
        return true;
    }

    /// <summary>
    ///     Renders one fixture at its captured camera and returns the PNG bytes. Two advances: the first
    ///     derives the levels and arranges the panes, the second runs with the cameras already pinned, so
    ///     the picture does not depend on whether the panes existed when the camera was applied.
    /// </summary>
    /// <param name="fixture">The scene to render.</param>
    public byte[] RenderFixturePng(SceneFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        SceneTime time = fixture.Time;
        Renderer.Advance(fixture.Frame, in time);
        Renderer.SetAllCameras(fixture.Camera);
        Renderer.Advance(fixture.Frame, in time);
        Renderer.Render();
        return Renderer.SnapshotPng();
    }
}
