#region

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.Sdk.Ui.Controls;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Compositing;
using DemoViewer.NET.Playback2D.Core.Hud;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Overlay;
using DemoViewer.NET.Playback2D.Core.Rendering;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Hud;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Modules.Playback2D;

/// <summary>
///     The app behind <see cref="MapAssets" />: bundles from the asset pipeline, icons from the game icon set,
///     and pictures through the headless renderer on the CPU, the path <c>dv2d render</c> and the goldens take.
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
    public IMapAsset? TryLoad(string mapName) => MapAssetPipeline.TryLoad(mapName);

    /// <inheritdoc />
    public IIconSource CreateIcons() => new SkiaIconSource();

    /// <inheritdoc />
    public byte[] RenderPng(Scene2DFrame frame, IMapAsset? map, IReadOnlyList<string> layerIds, ViewportTransform camera,
        SKSizeI size, OverlayDocument? overlay)
    {
        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack(layerIds, overlay: overlay);
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
}
