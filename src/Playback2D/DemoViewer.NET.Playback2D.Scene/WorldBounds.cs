namespace DemoViewer.NET.Playback2D.Core;

/// <summary>
///     An axis-aligned world-space X/Y rectangle. Deliberately not <c>SKRect</c>: world Y is up and
///     Skia's Y is down, so reusing the screen-space rectangle here would invite a sign error at every
///     call site.
/// </summary>
/// <param name="MinX">Lower world X.</param>
/// <param name="MinY">Lower world Y.</param>
/// <param name="MaxX">Upper world X.</param>
/// <param name="MaxY">Upper world Y.</param>
public readonly record struct WorldBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>
    ///     The fixed fallback rectangle drawn before any position is observed: ±3000 on both axes,
    ///     matching the pre-move <c>Playback2DViewport.DefaultWorldExtent</c>.
    /// </summary>
    public static readonly WorldBounds Default = new(-3000, -3000, 3000, 3000);

    /// <summary>Width in world units. Negative when the rectangle is inverted.</summary>
    public double Width => MaxX - MinX;

    /// <summary>Height in world units. Negative when the rectangle is inverted.</summary>
    public double Height => MaxY - MinY;

    /// <summary>The smallest rectangle containing both inputs.</summary>
    public static WorldBounds Union(WorldBounds a, WorldBounds b) => new(
        Math.Min(a.MinX, b.MinX),
        Math.Min(a.MinY, b.MinY),
        Math.Max(a.MaxX, b.MaxX),
        Math.Max(a.MaxY, b.MaxY));

    /// <summary>
    ///     The smallest rectangle containing this one and the given world point.
    ///     <para>
    ///         <b>Filter the point before you get here.</b> <c>Math.Min</c>/<c>Math.Max</c> propagate
    ///         <c>NaN</c>, and a rectangle that is only ever widened never un-poisons itself, so one bad
    ///         sample is permanent: see <c>SceneFrameBuilder.Observe</c> (the gate) and
    ///         <see cref="ViewportTransform.Fit" /> (the backstop).
    ///     </para>
    /// </summary>
    public WorldBounds Extend(double worldX, double worldY) => new(
        Math.Min(MinX, worldX),
        Math.Min(MinY, worldY),
        Math.Max(MaxX, worldX),
        Math.Max(MaxY, worldY));
}
