namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     Which string a row's place comes from. A setting; changing it re-indexes the library. Core because
///     <see cref="Configuration.SituationsSettings" /> stores it; the index that reads it is the Strat Book
///     extension's.
/// </summary>
public enum RoundIndexTokenSource
{
    /// <summary>The pawn's <c>m_szLastPlaceName</c>: Valve's names, no asset needed.</summary>
    Pawn,

    /// <summary>The effective zone set's resolver over the sample position: the team's names, per map.</summary>
    Zones
}
