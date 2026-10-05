#region

using DemoViewer.NET.Extensions.StratBook.Modules.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook;

/// <summary>
///     Which of the pack's resident singletons hold live state this session. Set by the factory that
///     builds each one (<see cref="StratBookPack.Register" />) through <see cref="Record" />, never by a
///     caller that merely asks for it: <c>GetService&lt;T&gt;</c> on a singleton factory constructs it, so
///     reading the container is not a safe "was it built" check.
///     <para>
///         The typed properties are the live view: null after <see cref="Clear" /> (a release), so shutdown
///         flushes nothing that was dropped. <see cref="Residents" /> is the built view: the same objects,
///         kept across a release because they are container singletons and come back on the next enable,
///         when <see cref="Restore" /> re-points the typed properties at them.
///     </para>
/// </summary>
internal sealed class StratBookPackInstances
{
    private readonly object _gate = new();
    private readonly List<IExtensionResident> _residents = [];

    public SituationIndex? Situations { get; set; }
    public GrenadeIndex? Grenades { get; set; }
    public TeamIdentityService? Teams { get; set; }
    public LineupClipService? Lineups { get; set; }
    public TagFactsRefresher? TagFacts { get; set; }
    public WatchedSituationsService? Watched { get; set; }
    public StratMiningService? Mining { get; set; }

    // The three evaluator-registry contributions, set inline by their own factories, not
    // through Record: they are plain fan-out evaluators, not IExtensionResident, so Clear/Restore leave
    // them alone. Once set they stay set for the session: "was ever constructed", not "is live now".
    public RoundIndexEvaluator? RoundIndex { get; set; }
    public SuggestedTagsService? SuggestedTags { get; set; }
    public GrenadeIndexEvaluator? GrenadeWalk { get; set; }

    // The Tag Store, same rule: set by its factory, never cleared. Shutdown flushes its index only when
    // something built it, so a never-opened pack reads no tags directory at exit.
    public TagStore? Tags { get; set; }

    // The Strat Book tab's module, set by Contribute. Shutdown commits the open strat through it.
    public StratBookModule? StratBook { get; set; }

    /// <summary>Every resident built this session, in build order; survives a release.</summary>
    public IReadOnlyList<IExtensionResident> Residents
    {
        get
        {
            lock (_gate)
            {
                return [.. _residents];
            }
        }
    }

    /// <summary>A factory built one: remembered for the next release and attach, and live from now.</summary>
    public void Record(IExtensionResident built)
    {
        ArgumentNullException.ThrowIfNull(built);
        lock (_gate)
        {
            if (!_residents.Contains(built))
            {
                _residents.Add(built);
            }
        }

        Assign(built);
    }

    /// <summary>Points the typed properties at the built residents again, after a release.</summary>
    public void Restore()
    {
        foreach (IExtensionResident resident in Residents)
        {
            Assign(resident);
        }
    }

    /// <summary>Nothing is live: every typed property is null. The built list is untouched.</summary>
    public void Clear()
    {
        Situations = null;
        Grenades = null;
        Teams = null;
        Lineups = null;
        TagFacts = null;
        Watched = null;
        Mining = null;
    }

    private void Assign(IExtensionResident resident)
    {
        switch (resident)
        {
            case SituationIndex situations:
                Situations = situations;
                break;
            case GrenadeIndex grenades:
                Grenades = grenades;
                break;
            case TeamIdentityService teams:
                Teams = teams;
                break;
            case LineupClipService lineups:
                Lineups = lineups;
                break;
            case TagFactsRefresher tagFacts:
                TagFacts = tagFacts;
                break;
            case WatchedSituationsService watched:
                Watched = watched;
                break;
            case StratMiningService mining:
                Mining = mining;
                break;
        }
    }
}
