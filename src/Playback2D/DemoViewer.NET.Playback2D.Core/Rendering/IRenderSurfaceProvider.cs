#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Rendering;

/// <summary>
///     The seam that makes Core a runtime rather than a library (design §5.8). Every offscreen
///     consumer — export, the CLI, tests, thumbnails — obtains its surfaces here, so swapping CPU for
///     GPU changes one construction site and no layer code.
///     <para>
///         On screen, the interactive path keeps taking Avalonia's Skia lease instead: providers are for
///         surfaces <i>we</i> own. Both paths run the same layer code, so a bug shows in both or neither.
///     </para>
/// </summary>
public interface IRenderSurfaceProvider : IDisposable
{
    /// <summary>Which backend this provider hands out. Fixed for the provider's lifetime.</summary>
    RenderBackend Backend { get; }

    /// <summary>Creates an RGBA8888 premultiplied surface. The caller owns and disposes it.</summary>
    /// <param name="size">Pixel size of the surface.</param>
    SKSurface CreateSurface(SKSizeI size);

    /// <summary>
    ///     Makes everything drawn into <paramref name="surface" /> readable. GPU providers flush and
    ///     submit their context here; the CPU provider has nothing to do.
    /// </summary>
    /// <param name="surface">The surface to flush.</param>
    void Flush(SKSurface surface);
}
