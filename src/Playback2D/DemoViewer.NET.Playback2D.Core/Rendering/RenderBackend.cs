namespace DemoViewer.NET.Playback2D.Core.Rendering;

/// <summary>
///     Which Skia backend an <see cref="IRenderSurfaceProvider" /> hands out surfaces from (design §5.8).
///     <see cref="Vulkan" /> is declared so the enum does not churn later; nothing reaches it in v1.
/// </summary>
public enum RenderBackend
{
    /// <summary>Software raster. Always available, and the contract baseline goldens are authored on it.</summary>
    CpuRaster,

    /// <summary>A windowless desktop GL context.</summary>
    OpenGl,

    /// <summary>ANGLE over D3D11 via an EGL pbuffer — the Windows GPU path.</summary>
    Angle,

    /// <summary>Declared, unreachable in v1.</summary>
    Vulkan
}
