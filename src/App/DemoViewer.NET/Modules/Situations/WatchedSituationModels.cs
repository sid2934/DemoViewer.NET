#region

using System.Text.Json;
using System.Text.Json.Serialization;
using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     <c>watched-situations.json</c>: the user's saved queries, beside <c>teams.json</c> under the
///     config root. User truth, never rebuilt, refused rather than overwritten when it cannot be read.
///     No tick anchors, so no clock block (the teams.json rule): the watermark is a UTC stamp compared
///     against the index's own UTC stamp, never a demo tick.
/// </summary>
public sealed class WatchedSituationsFile
{
    /// <summary>The schema this build writes.</summary>
    public const int CurrentSchema = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public int SchemaVersion { get; set; } = CurrentSchema;

    /// <summary>The watches in creation order.</summary>
    public List<WatchedSituation> Watches { get; set; } = [];
}

/// <summary>
///     One saved query: the tokens as they sit on the canvas, the tolerance, the filter rail's values
///     and the watermark that decides what is new. The query is stored as the user drew it (slots and
///     world positions) rather than as pairs, so re-running it puts the same tokens back on the map.
///     <para>
///         <b>No demo is named here.</b> The opponent is a team id and the dates a range; the demos
///         they narrow to are derived at evaluation time, which is what lets a demo indexed after the
///         watch was saved count as new. The only state the badge needs is
///         <see cref="WatermarkTicks" />: after a restart "new" is every hit in a demo whose index stamp
///         (<c>RoundIndex.ComputedAtTicks</c>) is above it.
///     </para>
/// </summary>
public sealed class WatchedSituation
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>UTC ticks at creation.</summary>
    public long CreatedTicks { get; set; }

    /// <summary>Index stamps at or below this are seen; marking a watch seen moves it to now.</summary>
    public long WatermarkTicks { get; set; }

    /// <summary>The map, as the demo header spells it.</summary>
    public string Map { get; set; } = "";

    public SituationTolerance Tolerance { get; set; }

    /// <summary>The placed tokens, resolved or not; an unresolved one is put back hollow and the query leaves it out.</summary>
    public List<WatchedToken> Tokens { get; set; } = [];

    public SearchFilterValues Filters { get; set; } = SearchFilterValues.None;

    /// <summary>The resolved places on one side, the input the token encoder takes.</summary>
    /// <param name="side">The rail row.</param>
    public IReadOnlyList<string> ResolvedPlaces(QuerySide side) =>
        [.. Tokens.Where(t => t.Side == side && t.Place is not null).OrderBy(t => t.Slot).Select(t => t.Place!)];

    /// <summary>
    ///     One side's pairs in the index's canonical order: the resolved places encoded through
    ///     <see cref="PlaceCountToken" /> and decoded again, the same path <c>SituationQueryDraft</c>
    ///     takes, so a saved query matches what the canvas it was saved from matched.
    /// </summary>
    /// <param name="side">The rail row.</param>
    public IReadOnlyList<PlaceQuery> PairsFor(QuerySide side) =>
        [.. PlaceCountToken.Decode(PlaceCountToken.EncodePlaces(ResolvedPlaces(side))).Select(p => new PlaceQuery(p.Place, p.Count))];

    /// <summary>One side's token as the index stores it; empty for an unconstrained side.</summary>
    /// <param name="side">The rail row.</param>
    public string TokenFor(QuerySide side) => PlaceCountToken.EncodePlaces(ResolvedPlaces(side));
}

/// <summary>A canvas token as saved: the slot, where it sits and what it resolved to.</summary>
/// <param name="Side">The rail row.</param>
/// <param name="Slot">The slot within the row.</param>
/// <param name="WorldX">World X of the drop.</param>
/// <param name="WorldY">World Y of the drop.</param>
/// <param name="LevelMinZ">The pane's band floor the token was dropped on.</param>
/// <param name="Place">The resolved place, or null when the drop resolved nothing.</param>
public sealed record WatchedToken(QuerySide Side, int Slot, float WorldX, float WorldY, double LevelMinZ, string? Place)
{
    /// <summary>The saved form of a canvas token.</summary>
    /// <param name="token">The token on the canvas.</param>
    public static WatchedToken From(QueryToken token) =>
        new(token.Side, token.Slot, token.WorldX, token.WorldY, token.LevelMinZ, token.Place);

    /// <summary>The canvas token this was saved from.</summary>
    public QueryToken ToToken() => new(Side, Slot, WorldX, WorldY, LevelMinZ, Place);
}
