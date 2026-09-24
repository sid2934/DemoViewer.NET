#region

using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Levels;
using DemoViewer.NET.Playback2D.Core.Zones;

#endregion

namespace DemoViewer.NET.Services.Tags;

/// <summary>
///     Turns a click on a 2D pane into a <see cref="TagPosition" /> (tag-store.md §3.6): the world point, the
///     pane's floor key, the tick, and the place with the <c>placeSource</c> that says how it was found.
///     <para>
///         <b>Zones first.</b> A map with zones answers through <see cref="PlaceResolver.ResolveOnFloor" />,
///         the call overview correction 14 gives a consumer that has a floor key and no world Z, stamped
///         <c>zones:&lt;EffectiveVersion&gt;</c> (correction 13) so a user's overlay edit re-labels it later.
///         A miss there is left unresolved rather than guessed from a pawn: the zones are the authority on
///         that map, and the refresh pass retries an unresolved point.
///     </para>
///     <para>
///         <b>Else the nearest pawn.</b> With no zones (a map without a bake, every map on the browser host)
///         the place is the <c>m_szLastPlaceName</c> of the nearest alive player standing on the clicked
///         floor at that tick, within <see cref="PawnMaxDistance" />, stamped <c>pawn</c>. The markers are the
///         scene frame's copied-out scalars, so nothing pooled is read from the click.
///     </para>
/// </summary>
public static class TagPositionResolver
{
    /// <summary>The <c>placeSource</c> of a place read from the nearest pawn.</summary>
    public const string PawnSource = "pawn";

    /// <summary>World units beyond which no pawn is near enough to name a click; the place snap's radius.</summary>
    public const double PawnMaxDistance = 512;

    /// <summary>The <c>placeSource</c> of a place resolved against a zone set at <paramref name="effectiveVersion" />.</summary>
    /// <param name="effectiveVersion"><c>ZoneSet.EffectiveVersion</c>.</param>
    public static string ZonesSource(string effectiveVersion) => $"zones:{effectiveVersion}";

    /// <summary>The position a click makes.</summary>
    /// <param name="x">World X of the click.</param>
    /// <param name="y">World Y of the click.</param>
    /// <param name="level">The floor the clicked pane shows.</param>
    /// <param name="tick">The frame-clock tick the click was made at, or null when it is not time-specific.</param>
    /// <param name="zones">The map's zone resolver, or null when the map has none.</param>
    /// <param name="markers">The players at <paramref name="tick" />, for the pawn fallback.</param>
    public static TagPosition Resolve(double x, double y, MapLevel level, int? tick, PlaceResolver? zones,
        IReadOnlyList<PlayerMarker>? markers)
    {
        ArgumentNullException.ThrowIfNull(level);

        // The annotation anchor rule: the band's lower Z, quantized, never a floor index. It is also the
        // floor key ResolveOnFloor takes, so the stored value and the resolved one cannot disagree.
        double levelMinZ = MapSpace.QuantizeZ(level.ZMin);
        TagPosition position = new() { X = x, Y = y, LevelMinZ = levelMinZ, Tick = tick };

        if (zones is not null)
        {
            if (zones.ResolveOnFloor(x, y, levelMinZ).Name is { Length: > 0 } place)
            {
                position.Place = place;
                position.PlaceSource = ZonesSource(zones.Zones.EffectiveVersion);
            }

            return position;
        }

        if (NearestPawnPlace(x, y, level, markers) is { } pawn)
        {
            position.Place = pawn;
            position.PlaceSource = PawnSource;
        }

        return position;
    }

    private static string? NearestPawnPlace(double x, double y, MapLevel level, IReadOnlyList<PlayerMarker>? markers)
    {
        if (markers is null)
        {
            return null;
        }

        string? best = null;
        double bestDistance = PawnMaxDistance * PawnMaxDistance;
        foreach (PlayerMarker marker in markers)
        {
            // A dead marker is held where the player fell and carries no place; a player on another floor
            // of a stacked map is not standing where the click is, however close in XY.
            if (!marker.IsAlive || marker.Place is not { Length: > 0 } place || !level.Contains(marker.WorldZ))
            {
                continue;
            }

            double dx = marker.WorldX - x;
            double dy = marker.WorldY - y;
            double distance = dx * dx + dy * dy;
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = place;
            }
        }

        return best;
    }
}
