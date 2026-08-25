namespace DemoViewer.NET.Playback2D.Core.Compositing;

/// <summary>
///     The coarse z-band a layer draws in (design §5.2). Layer order is one interleaved
///     <c>(Slot, Order)</c> list, so annotations sit above actors and below the HUD regardless of who
///     registered them or when.
/// </summary>
public enum LayerSlot
{
    /// <summary>Behind the world: radar, grid.</summary>
    Underlay,

    /// <summary>World-space content: trails, area effects, vision, markers, bomb.</summary>
    World,

    /// <summary>Above the world but still world-anchored: annotations, floor labels.</summary>
    Overlay,

    /// <summary>Screen-space chrome: clock, kill feed.</summary>
    Hud
}
