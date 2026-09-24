#region

using DemoViewer.NET.Playback2D.Core.Query;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Modules.Situations;

/// <summary>
///     The query the canvas is building: the placed tokens per side, the tolerance and the filters.
///     The object model the later Situation Search items extend in place: Find Rounds Like This fills
///     the tokens from a tick, the Tolerance Slider drives <see cref="Tolerance" />, Search Filters And
///     Live Count fills <see cref="Facts" /> and <see cref="Demos" />, Watched Situations saves the
///     <see cref="SituationQuery" /> it produces.
///     <para>
///         <b>The token is the index's token.</b> <see cref="TokenFor" /> encodes a side's resolved
///         places through <see cref="PlaceCountToken.EncodePlaces" />, the same function the builder
///         applies to a sampled second, so a query placed over the places a row holds encodes byte for
///         byte to that row's token. That is the done bar, and it is one function applied twice.
///     </para>
///     <para>
///         Partial queries are the normal case: a side with no resolved token is unconstrained, and a
///         query with no resolved token on either side matches every indexed round of the map.
///     </para>
/// </summary>
public sealed class SituationQueryDraft
{
    /// <param name="document">The canvas slots. Shared with the layer and the tool.</param>
    public SituationQueryDraft(QueryCanvasDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
    }

    /// <summary>The ten slots.</summary>
    public QueryCanvasDocument Document { get; }

    /// <summary>The map the query runs over.</summary>
    public string Map => Document.MapName;

    /// <summary>How loosely the pairs match. Exact until the Tolerance Slider lands.</summary>
    public SituationTolerance Tolerance { get; set; } = SituationTolerance.Exact;

    /// <summary>The Round Facts filter; null until Search Filters lands.</summary>
    public RoundFactsFilter? Facts { get; set; }

    /// <summary>Stable keys to restrict to; null until the Library or Team filters land.</summary>
    public IReadOnlySet<string>? Demos { get; set; }

    /// <summary>Whether the query constrains anything at all.</summary>
    public bool IsEmpty =>
        Document.ResolvedPlaces(QuerySide.Ct).Count == 0 && Document.ResolvedPlaces(QuerySide.T).Count == 0;

    /// <summary>
    ///     One side's token, exactly as the index stores it for a sampled second on which those players
    ///     stood in those places: the resolved places encoded through <see cref="PlaceCountToken" />.
    ///     Empty for an unconstrained side.
    /// </summary>
    /// <param name="side">The rail row.</param>
    public string TokenFor(QuerySide side) => PlaceCountToken.EncodePlaces(Document.ResolvedPlaces(side));

    /// <summary>One side's pairs, in the index's canonical order: the decoded token.</summary>
    /// <param name="side">The rail row.</param>
    public IReadOnlyList<PlaceQuery> PairsFor(QuerySide side) =>
        [.. PlaceCountToken.Decode(TokenFor(side)).Select(p => new PlaceQuery(p.Place, p.Count))];

    /// <summary>The query the index answers, over the current map, tokens, tolerance and filters.</summary>
    public SituationQuery ToQuery() =>
        new(Map, PairsFor(QuerySide.Ct), PairsFor(QuerySide.T), Tolerance, Facts, Demos);
}
