#region

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Hud;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The app behind <see cref="MapAssets" />: bundles from the one lease registry every view and tab holds
///     through, so a map decodes once; icons from the game icon set; and pictures through the headless
///     renderer on the CPU, the path <c>dv2d render</c> and the goldens take.
/// </summary>
internal sealed class HostedMapAssets : IMapAssetsBackend
{
    /// <summary>
    ///     Installed when the assembly is first used, so a test that never builds the Avalonia app still draws
    ///     through the same path the app does.
    /// </summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "Application code that is a class library only because several heads reference it; "
                        + "the UI kit's map surface must be live before any extension code asks for a map.")]
    internal static void Install() => MapAssets.Backend ??= new HostedMapAssets();

    /// <inheritdoc />
    public IMapAssetLease? Acquire(string mapName) => MapBundleLeases.Acquire(mapName);

    /// <inheritdoc />
    public MapDescription? Describe(string mapName) =>
        MapAssetPipeline.TryDescribe(mapName) is not { } facts ? null : new MapDescription(facts.Bounds, facts.Floors);

    /// <inheritdoc />
    public IIconSource CreateIcons() => new SkiaIconSource();

    /// <inheritdoc />
    public byte[] RenderPng(Scene2DFrame frame, IMapAsset? map, IReadOnlyList<string> layerIds, ViewportTransform camera,
        SKSizeI size, IReadOnlyList<Func<ISceneLayer>>? layers)
    {
        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(layerIds);
        Compose(compositor, layers);
        using HeadlessSceneRenderer renderer = new(provider, compositor)
        {
            Palette = ScenePalette.Dark,
            Purpose = RenderPurpose.Export,
            Camera = camera
        };
        renderer.Levels.SetAuthoritativeFloors(map?.Floors);
        renderer.Levels.RadarBinder = map?.CreateRadarBinder();

        SceneTime time = frame.Time;
        return renderer.RenderPng(frame, in time, size);
    }

    // No ordering rule of its own: the compositor re-sorts on (Slot, Order, Id) at every Add, and where a
    // layer came from is not part of that key. Registration hands the layer over, so only one Add refused
    // outright leaves something for this method to dispose.
    private static void Compose(SceneCompositor compositor, IReadOnlyList<Func<ISceneLayer>>? layers)
    {
        if (layers is null)
        {
            return;
        }

        foreach (Func<ISceneLayer> factory in layers)
        {
            ArgumentNullException.ThrowIfNull(factory, nameof(layers));
            ISceneLayer layer = factory();
            try
            {
                compositor.Add(layer);
            }
            catch
            {
                layer.Dispose();
                throw;
            }
        }
    }
}
