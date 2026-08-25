#region

using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Core.Rendering;

/// <summary>
///     Software-raster surfaces. Always available, no native dependency beyond SkiaSharp itself, and it
///     runs anywhere the runtime does — CI containers and WASM included (verified in B0's WASM spike,
///     decision D11).
///     <para>
///         It is the <b>contract baseline</b>: golden images are authored on it, and every feature must
///         be correct — not necessarily fastest — here before any GPU backend is considered.
///     </para>
/// </summary>
public sealed class CpuSurfaceProvider : IRenderSurfaceProvider
{
    /// <inheritdoc />
    public RenderBackend Backend => RenderBackend.CpuRaster;

    /// <inheritdoc />
    public SKSurface CreateSurface(SKSizeI size)
    {
        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        return SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A no-op: a raster surface's pixels are already readable. Declared so callers can be written
    ///     against the interface without branching on the backend.
    /// </remarks>
    public void Flush(SKSurface surface)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A no-op: this provider owns no GPU context and no unmanaged handle. Implemented anyway so the
    ///     interface's disposal contract holds and a caller's <c>using</c> is honest.
    /// </remarks>
    public void Dispose()
    {
    }
}
