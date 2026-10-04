#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Modules.Library;

#endregion

namespace DemoViewer.NET.Services.Teams;

/// <summary>
///     The Library's Team filter: "All teams", "Us", then every visible team. Offers no badge.
///     <see cref="Resolve" /> is the lazy point: the Library only reaches <see cref="Filter" /> while this
///     contribution's gate resolves on, so <see cref="TeamIdentityService" /> is never touched while off.
/// </summary>
/// <param name="resolve">Resolves the live <see cref="TeamIdentityService" />; called at most once.</param>
/// <param name="featureId">
///     The gate id this shows under; null (the pack's own usage) lets <c>PackContributions.Library</c>
///     stamp it to the owning pack's id. A caller that builds the shell directly (bypassing the pack)
///     names it explicitly, e.g. <c>StratBookPack.PackFeatureId</c>.
/// </param>
public sealed class TeamLibraryContribution(Func<TeamIdentityService> resolve, string? featureId = null) : ILibraryContribution
{
    private TeamIdentityService? _teams;

    /// <inheritdoc />
    public string? FeatureId => featureId;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public LibraryFilter? Filter
    {
        get
        {
            TeamIdentityService teams = Resolve();
            List<LibraryFilterItem> items =
            [
                new LibraryFilterItem("", "All teams"),
                new LibraryFilterItem("us", "Us")
            ];
            foreach (Team team in teams.Teams)
            {
                items.Add(new LibraryFilterItem(team.Id.ToString(), DisplayText.Sanitize(team.Name)));
            }

            return new LibraryFilter("Team", items, Matches(teams), Tooltip: "Filter by team");
        }
    }

    /// <inheritdoc />
    public bool HasBadge => false;

    /// <inheritdoc />
    public LibraryBadge? BadgeFor(DemoEntry entry) => null;

    /// <inheritdoc />
    public IReadOnlyList<string> BadgeLabels => [];

    /// <inheritdoc />
    public string? BadgeResetLabel => null;

    /// <inheritdoc />
    public void SetLabel(DemoEntry entry, string? label)
    {
        // No badge offered; nothing to set.
    }

    // "us" keeps demos whose our side resolved; a team key keeps demos with either end-of-demo side
    // assigned to that team.
    private static Func<DemoEntry, string, bool> Matches(TeamIdentityService teams) => (entry, key) =>
    {
        if (teams.GetAssignment(entry.FilePath) is not { } a)
        {
            return false;
        }

        if (key == "us")
        {
            return a.OurSide is not null;
        }

        return Guid.TryParse(key, out Guid teamId) && (a.T.TeamId == teamId || a.Ct.TeamId == teamId);
    };

    private TeamIdentityService Resolve()
    {
        if (_teams is null)
        {
            _teams = resolve();
            _teams.Changed += () => Changed?.Invoke();
        }

        return _teams;
    }
}
