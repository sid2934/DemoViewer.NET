namespace DemoViewer.NET.Playback2D.Core.Query;

/// <summary>Which side of the rail a token belongs to. The value is the side's row on the rail, never a CS2 team number.</summary>
public enum QuerySide
{
    /// <summary>The counter-terrorist row.</summary>
    Ct,

    /// <summary>The terrorist row.</summary>
    T
}

/// <summary>
///     One rail token placed on the map: where it was dropped, which floor it was dropped on, and the
///     place the drop resolved to.
///     <para>
///         <b>The floor is keyed the way ink is.</b> <see cref="LevelMinZ" /> is the quantized lower Z of
///         the pane's level (<c>MapSpace.QuantizeZ(pane.Level.ZMin)</c>), the same key
///         <c>SpaceRef.World</c> carries, so the layer resolves the pane through
///         <c>MapSpace.IdForAnchor</c> and a token on Nuke lower stays on Nuke lower when the band list
///         is rebuilt. A world Z is not stored: a drop on a 2D pane has none.
///     </para>
///     <para>
///         <see cref="Place" /> is the raw place name the drop resolved to, or null when nothing was near
///         enough. A null place is still a token on the map (the user put it there) but it contributes
///         nothing to the query, and the layer draws it hollow so the difference is visible.
///     </para>
/// </summary>
/// <param name="Side">The rail row.</param>
/// <param name="Slot">The slot within the row, 0 to <see cref="QueryCanvasDocument.SlotsPerSide" /> - 1.</param>
/// <param name="WorldX">World X of the drop.</param>
/// <param name="WorldY">World Y of the drop.</param>
/// <param name="LevelMinZ">The quantized lower Z of the level the token sits on.</param>
/// <param name="Place">The resolved raw place name, or null when unresolved.</param>
public readonly record struct QueryToken(
    QuerySide Side,
    int Slot,
    float WorldX,
    float WorldY,
    double LevelMinZ,
    string? Place)
{
    /// <summary>Whether the drop resolved to a place; only resolved tokens reach the query.</summary>
    public bool IsResolved => Place is not null;
}
