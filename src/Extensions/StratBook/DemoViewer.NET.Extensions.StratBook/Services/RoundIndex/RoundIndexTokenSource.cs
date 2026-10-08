namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

/// <summary>
///     Which string a row's place comes from. A setting (<see cref="Extensions.StratBook.StratBookSettings.TokenSource" />);
///     changing it re-indexes the library.
/// </summary>
public enum RoundIndexTokenSource
{
    /// <summary>The pawn's <c>m_szLastPlaceName</c>: Valve's names, no asset needed.</summary>
    Pawn,

    /// <summary>The effective zone set's resolver over the sample position: the team's names, per map.</summary>
    Zones
}
