#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Playback2D.Core;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>One alive player at the snapshot tick: where they stood, and the place that position minted.</summary>
/// <param name="Slot">The roster slot.</param>
/// <param name="WorldX">World X.</param>
/// <param name="WorldY">World Y.</param>
/// <param name="WorldZ">World Z, raw; the canvas keys it to a floor when it places the token.</param>
/// <param name="Place">The raw place name, or null when the source had none (the token writes <c>?</c>).</param>
public readonly record struct SituationSnapshotPlayer(int Slot, float WorldX, float WorldY, float WorldZ, string? Place);

/// <summary>
///     The 2D scene's current tick, cut down to what the Query Canvas places: the alive players per
///     side, each with the place their position minted through the same <see cref="IPlaceSource" />
///     the round index builder mints its rows through.
///     <para>
///         <b>One function applied twice.</b> <see cref="TokenFor" /> encodes a side exactly as
///         <c>RoundIndexBuilder</c> encodes a sampled second, unknown places included, so the token
///         this snapshot mints for a tick is the token the index stored for that tick. The canvas then
///         drops the same players into its rail, and <c>SituationQueryDraft</c> encodes the resolved
///         ones through the same function again.
///     </para>
///     <para>
///         Side is the marker's team number (3 CT, 2 T), the same rule the attributes panel shows a
///         card by; spectators and coaches carry another number and are left out. Alive is the
///         marker's own flag, which the scene decided from <c>m_lifeState</c> and <c>m_iHealth</c>.
///     </para>
/// </summary>
public sealed class SituationSnapshot
{
    private SituationSnapshot(string map, SceneTime time, IReadOnlyList<SituationSnapshotPlayer> ct,
        IReadOnlyList<SituationSnapshotPlayer> t)
    {
        Map = map;
        Time = time;
        Ct = ct;
        T = t;
    }

    /// <summary>The map, as the demo header spells it.</summary>
    public string Map { get; }

    /// <summary>The scene time the markers belong to. Frame clock, like every tick the app holds.</summary>
    public SceneTime Time { get; }

    /// <summary>The alive CT players, in marker order.</summary>
    public IReadOnlyList<SituationSnapshotPlayer> Ct { get; }

    /// <summary>The alive T players, in marker order.</summary>
    public IReadOnlyList<SituationSnapshotPlayer> T { get; }

    /// <summary>Nobody alive on either side: nothing to search for.</summary>
    public bool IsEmpty => Ct.Count == 0 && T.Count == 0;

    /// <summary>One side's players.</summary>
    /// <param name="side">The rail row.</param>
    public IReadOnlyList<SituationSnapshotPlayer> Players(QuerySide side) => side == QuerySide.Ct ? Ct : T;

    /// <summary>
    ///     One side's token as the index stores it for this tick: every alive player, an unknown place
    ///     written as <c>?</c>, through <see cref="PlaceCountToken.EncodePlaces" />.
    /// </summary>
    /// <param name="side">The rail row.</param>
    public string TokenFor(QuerySide side) => PlaceCountToken.EncodePlaces(Players(side).Select(p => p.Place));

    /// <summary>
    ///     Cuts the scene's markers down to the alive players per side and mints each one's place
    ///     through <paramref name="source" />: the pawn's own field in the default mode, the zone set
    ///     over the marker's position in the zones mode.
    /// </summary>
    /// <param name="map">The map, as the demo header spells it.</param>
    /// <param name="time">The scene time the markers were built for.</param>
    /// <param name="markers">The scene's markers at that time.</param>
    /// <param name="source">The place source the index mints this map's rows through.</param>
    public static SituationSnapshot Capture(string map, SceneTime time, IReadOnlyList<PlayerMarker> markers,
        IPlaceSource source)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(source);

        List<SituationSnapshotPlayer> ct = [];
        List<SituationSnapshotPlayer> t = [];
        foreach (PlayerMarker marker in markers)
        {
            if (!marker.IsAlive || marker.Team is not (2 or 3))
            {
                continue;
            }

            // The sample the builder would have seen for this pawn at this tick, so a zone source
            // resolves the same position and the pawn source reads the same field.
            PositionSample sample = new(time.FrameIndex, time.Tick, marker.Slot,
                new Vector3(marker.WorldX, marker.WorldY, marker.WorldZ), marker.Place, marker.Team, marker.IsAlive);
            SituationSnapshotPlayer player = new(marker.Slot, marker.WorldX, marker.WorldY, marker.WorldZ,
                source.PlaceFor(in sample));
            (marker.Team == 3 ? ct : t).Add(player);
        }

        return new SituationSnapshot(map, time, ct, t);
    }
}
