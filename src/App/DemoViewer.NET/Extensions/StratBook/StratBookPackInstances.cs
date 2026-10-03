#region

using DemoViewer.NET.Modules.Situations;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     Which of the pack's startup-built singletons actually got constructed this session. Set once, by
///     the factory that builds each one (<see cref="StratBookPack.Register" />), never by a caller that
///     merely asks for it: <c>GetService&lt;T&gt;</c> on a singleton factory constructs it, so reading the
///     container is not a safe "was it built" check. Every field here is the pack's own doing; nothing
///     observes a construction triggered some other way.
/// </summary>
internal sealed class StratBookPackInstances
{
    public SituationIndex? Situations { get; set; }
    public GrenadeIndex? Grenades { get; set; }
    public TeamIdentityService? Teams { get; set; }
    public LineupClipService? Lineups { get; set; }
    public TagFactsRefresher? TagFacts { get; set; }
    public WatchedSituationsService? Watched { get; set; }
}
