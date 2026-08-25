namespace DemoViewer.NET.Playback2D.Core;

/// <summary>Why a scene is being rendered (design §5.1). Layers may trade quality for latency on it.</summary>
public enum RenderPurpose
{
    /// <summary>On-screen playback: latency wins over fidelity.</summary>
    Interactive,

    /// <summary>Offscreen video/still export: fidelity wins, and the timestep is fixed.</summary>
    Export,

    /// <summary>A small preview still — cheapest acceptable output.</summary>
    Thumbnail
}
