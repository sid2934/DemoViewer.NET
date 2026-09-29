#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Generated;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.ViewModels.Teams;

/// <summary>
///     The Teams tab (design §3.9): the team list, the rosters and members of the selected team, its
///     demos, and every action of the service's write API. The me account list and its suggestion live
///     at the top of this panel, not in Settings, so team truth has one home.
///     <para>
///         Delegate-injected (the Highlights precedent): the VM owns no clustering; it reads the
///         <see cref="TeamIdentityService" /> and re-projects on its <c>Changed</c>. Names are raw in the
///         service and sanitized here, at the render boundary, like every player name.
///     </para>
/// </summary>
public sealed partial class TeamsTabViewModel : ViewModelBase, IWorkspaceTabViewModel, IDisposable
{
    private readonly DemoCacheStore _demoCache;
    private readonly Func<string, Task>? _openDemo;
    private readonly TeamIdentityService _teams;
    private bool _disposed;

    [ObservableProperty]
    private string _activeRosterLine = "";

    [ObservableProperty]
    private string _fillsLine = "";

    [ObservableProperty]
    private string _footerLine = "";

    [ObservableProperty]
    private bool _isEditingSquad;

    [ObservableProperty]
    private TeamRow? _moveTarget;

    [ObservableProperty]
    private string _myTeamLine = "";

    [ObservableProperty]
    private string _squadHint = "";

    [ObservableProperty]
    private TeamRow? _mergeTarget;

    [ObservableProperty]
    private string _meSuggestionLine = "";

    [ObservableProperty]
    private string _myAccountsText = "";

    [ObservableProperty]
    private string _renameText = "";

    [ObservableProperty]
    private DemoRow? _selectedDemo;

    [ObservableProperty]
    private TeamRow? _selectedTeam;

    [ObservableProperty]
    private bool _showHidden;

    /// <param name="teams">The service.</param>
    /// <param name="demoCache">The index rows, for a demo's map and file name.</param>
    /// <param name="openDemo">Opens a demo in the workspace; null when the host has no shell.</param>
    /// <param name="isBrowser">Whether the host is the WASM head; null reads the runtime.</param>
    public TeamsTabViewModel(TeamIdentityService teams, DemoCacheStore demoCache, Func<string, Task>? openDemo = null, bool? isBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(demoCache);
        _teams = teams;
        _demoCache = demoCache;
        _openDemo = openDemo;
        IsBrowser = isBrowser ?? OperatingSystem.IsBrowser();
        _teams.Changed += Refresh;
        Refresh();
    }

    /// <summary>The line the panel shows on the browser host: nothing here outlives the tab.</summary>
    public static string BrowserNote => TeamIdentityService.BrowserNote;

    /// <summary>True on the WASM head.</summary>
    public bool IsBrowser { get; }

    public ObservableCollection<TeamRow> Teams { get; } = [];

    /// <summary>Every other visible team: what Merge folds the selected team into.</summary>
    public ObservableCollection<TeamRow> MergeTargets { get; } = [];

    public ObservableCollection<RosterRow> Rosters { get; } = [];

    public ObservableCollection<DemoRow> Demos { get; } = [];

    /// <summary>Pending suggestions, each with its accept and dismiss.</summary>
    public ObservableCollection<SuggestionRow> Suggestions { get; } = [];

    /// <summary>The squad editor's list: the players on your side with their games, the current squad checked.</summary>
    public ObservableCollection<SquadCandidateRow> SquadCandidates { get; } = [];

    /// <summary>Every other team: where the selected demo's side can be moved.</summary>
    public ObservableCollection<TeamRow> MoveTargets { get; } = [];

    /// <summary>The card shows while anything is pending or dismissed.</summary>
    public bool HasSuggestions => _teams.Suggestions.Count + SettledCount > 0;

    private int SettledCount => _teams.DismissedSuggestions.Count + (_teams.DismissedMeSuggestion is null ? 0 : 1);

    /// <summary>Also list the dismissed suggestions that still hold, each with Restore.</summary>
    [ObservableProperty]
    private bool _showSettled;

    public string SettledLabel => GeneratedInbox.SettledLabel(SettledCount);

    partial void OnShowSettledChanged(bool value) => ProjectSuggestions();

    /// <summary>True when your accounts are set, so the squad editor has a "your side" to read.</summary>
    public bool CanEditSquad => _teams.MyAccounts.Count > 0;

    public bool HasSelection => SelectedTeam is not null;

    public bool HasMeSuggestion => _teams.MeSuggestion is not null;

    public string? TeamsFileProblem => _teams.TeamsFileProblem;

    public bool HasTeamsFileProblem => _teams.TeamsFileProblem is not null;

    public bool HasTeams => Teams.Count > 0;

    /// <summary>The selected team's hide action reads "Hide" or "Show".</summary>
    public string HideLabel => SelectedTeam is { Hidden: true } ? "Show" : "Hide";

    /// <inheritdoc />
    public void OnActivated(IModuleContext context) => Refresh();

    /// <inheritdoc />
    public void OnDeactivated()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _teams.Changed -= Refresh;
    }

    partial void OnSelectedTeamChanged(TeamRow? value)
    {
        RenameText = value?.Name ?? "";
        MergeTarget = null;
        ProjectSelection();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HideLabel));
    }

    partial void OnShowHiddenChanged(bool value) => Refresh();

    // ── Actions ──────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Rename()
    {
        if (SelectedTeam is { } team && !string.IsNullOrWhiteSpace(RenameText))
        {
            _teams.Rename(team.Id, RenameText);
        }
    }

    [RelayCommand]
    private void SetAsUs()
    {
        if (SelectedTeam is { } team)
        {
            _teams.SetUs(team.IsUs ? null : team.Id);
        }
    }

    [RelayCommand]
    private void Merge()
    {
        if (SelectedTeam is { } into && MergeTarget is { } from && into.Id != from.Id)
        {
            _teams.Merge(into.Id, from.Id);
        }
    }

    /// <summary>The checked demo rows leave for a new team.</summary>
    [RelayCommand]
    private void Split()
    {
        if (SelectedTeam is not { } team)
        {
            return;
        }

        List<DemoSideRef> sides = [.. Demos.Where(d => d.IsChecked).Select(d => new DemoSideRef(d.Path, d.Side))];
        if (sides.Count > 0)
        {
            _teams.Split(team.Id, sides, null);
        }
    }

    /// <summary>A new roster from the selected demo's date on.</summary>
    [RelayCommand]
    private void StartRoster()
    {
        if (SelectedTeam is { } team && SelectedDemo is { } demo)
        {
            _teams.StartRoster(team.Id, TeamClusterer.DateOf(demo.OrderTicks), null);
        }
    }

    [RelayCommand]
    private void Hide()
    {
        if (SelectedTeam is { } team)
        {
            _teams.SetHidden(team.Id, !team.Hidden);
        }
    }

    /// <summary>The selected demo's side is not a team: an override, and the auto team goes if nothing else holds it.</summary>
    [RelayCommand]
    private void NotATeam()
    {
        if (SelectedDemo is { } demo)
        {
            _teams.Override(demo.Path, demo.Side, null);
        }
    }

    [RelayCommand]
    private Task Recompute() => _teams.RebuildAsync();

    /// <summary>Moves the selected demo's side to another team: an override, kept by every rebuild.</summary>
    [RelayCommand]
    private void MoveDemo()
    {
        if (SelectedDemo is { } demo && MoveTarget is { } target)
        {
            _teams.Override(demo.Path, demo.Side, target.Id);
        }
    }

    [RelayCommand]
    private void AcceptSuggestion(SuggestionRow? row)
    {
        if (row is not null)
        {
            _teams.AcceptSuggestion(row.Id);
        }
    }

    [RelayCommand]
    private void DismissSuggestion(SuggestionRow? row)
    {
        if (row is not null)
        {
            _teams.DismissSuggestion(row.Id);
        }
    }

    [RelayCommand]
    private void RestoreSuggestion(SuggestionRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.Id.StartsWith("me:", StringComparison.Ordinal))
        {
            _teams.RestoreMeSuggestion();
        }
        else
        {
            _teams.RestoreSuggestion(row.Id);
        }
    }

    [RelayCommand]
    private void EditSquad()
    {
        ProjectSquadCandidates();
        IsEditingSquad = true;
    }

    [RelayCommand]
    private void CancelSquad() => IsEditingSquad = false;

    /// <summary>Your main account plus the checked players become the squad; two to five in all.</summary>
    [RelayCommand]
    private void SaveSquad()
    {
        (_, _, string? main) = _teams.CoPlayers();
        List<string> picked = [.. SquadCandidates.Where(c => c.IsChecked).Select(c => c.SteamId)];
        if (main is not null)
        {
            picked.Insert(0, main);
        }

        picked = [.. picked.Distinct(StringComparer.Ordinal)];
        if (picked.Count is < 2 or > 5)
        {
            SquadHint = "a squad is you plus one to four players";
            return;
        }

        SquadHint = "";
        IsEditingSquad = false;
        _teams.SetSquad(picked);
    }

    [RelayCommand]
    private void SaveMyAccounts() =>
        _teams.SetMyAccounts([.. MyAccountsText.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);

    [RelayCommand]
    private void ConfirmMeSuggestion() => _teams.ConfirmMeSuggestion();

    [RelayCommand]
    private void DismissMeSuggestion() => _teams.DismissMeSuggestion();

    [RelayCommand]
    private Task OpenDemo(DemoRow? row) => row is not null && _openDemo is not null ? _openDemo(row.Path) : Task.CompletedTask;

    // ── Projection ───────────────────────────────────────────────────────────────────────────────

    private void Refresh()
    {
        Guid? keep = SelectedTeam?.Id;
        Teams.Clear();
        foreach (Team team in ShowHidden ? _teams.AllTeams : _teams.Teams)
        {
            Teams.Add(new TeamRow(team, _teams.DemosOf(team.Id).Count, _teams.LastSeenTicks(team.Id)));
        }

        MyAccountsText = string.Join(", ", _teams.MyAccounts);
        MeSuggestionLine = _teams.MeSuggestion is { } s ? MeLine(s) : "";
        FooterLine = Footer();
        ProjectMyTeam();
        ProjectSuggestions();
        OnPropertyChanged(nameof(CanEditSquad));
        OnPropertyChanged(nameof(HasMeSuggestion));
        OnPropertyChanged(nameof(TeamsFileProblem));
        OnPropertyChanged(nameof(HasTeamsFileProblem));
        OnPropertyChanged(nameof(HasTeams));

        SelectedTeam = keep is { } id ? Teams.FirstOrDefault(t => t.Id == id) : null;
        if (keep is not null && SelectedTeam is null)
        {
            ProjectSelection();
        }
    }

    private void ProjectSelection()
    {
        Rosters.Clear();
        Demos.Clear();
        MergeTargets.Clear();
        MoveTargets.Clear();
        ActiveRosterLine = "";
        if (SelectedTeam is not { } row)
        {
            return;
        }

        foreach (TeamRow other in Teams.Where(t => t.Id != row.Id))
        {
            MergeTargets.Add(other);
            MoveTargets.Add(other);
        }

        IReadOnlyList<SuggestedPlayer> active = _teams.ActiveRoster(row.Id);
        ActiveRosterLine = active.Count > 0
            ? "plays now (5 or more of the last 10): " + string.Join(", ", active.Select(p => DisplayText.Sanitize(p.Name)))
            : "plays now: nobody has 5 of the last 10 yet";

        foreach (Roster roster in row.Team.Rosters)
        {
            Rosters.Add(new RosterRow(roster, _teams.MembersOf(row.Id, roster.Id)));
        }

        foreach ((DemoRef demo, int side, TeamAssignment assignment) in _teams.SidesOf(row.Id))
        {
            SideAssignment mine = assignment.Side(side);
            Guid? otherId = assignment.Side(side == 2 ? 3 : 2).TeamId;
            string opponent = otherId is { } o && _teams.AllTeams.FirstOrDefault(t => t.Id == o) is { } team
                ? DisplayText.Sanitize(team.Name)
                : "(unaffiliated)";
            DemoCacheIndexEntry? entry = _demoCache.TryGetIndex(demo.Path);
            long ticks = entry?.ModifiedTicks ?? 0;
            Demos.Add(new DemoRow(demo.Path, side, ticks)
            {
                FileName = Path.GetFileName(demo.Path),
                Map = entry?.Map ?? "",
                Date = ticks > 0 ? new DateTime(ticks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                Opponent = opponent,
                // Surfaced at tier 1 only: "with a stand-in" has no referent until a five exists.
                StandIn = mine.StandIn && mine.Tier == 1,
                Detail = mine.Tier switch
                {
                    1 => $"{mine.Overlap} of the five",
                    2 => $"{mine.Overlap} of the core",
                    3 => "squad",
                    _ => "pinned"
                }
            });
        }
    }

    // The my-team card: the squad, then the regular fills around it with their games.
    private void ProjectMyTeam()
    {
        FillsLine = "";
        if (_teams.Us is not { } us)
        {
            MyTeamLine = _teams.MyAccounts.Count == 0
                ? "Set your accounts below, then pick the players you play with."
                : "No team yet. Pick the players you play with, or accept a suggestion.";
            return;
        }

        if (us.Rosters.FirstOrDefault(r => r.IsSquad) is not { } squad)
        {
            MyTeamLine = $"{DisplayText.Sanitize(us.Name)}: no squad set; its demos come from its rosters.";
            return;
        }

        IReadOnlyDictionary<string, TeamIndexMember> members = _teams.MembersOf(us.Id, squad.Id);
        string Name(string id) => members.TryGetValue(id, out TeamIndexMember? m) ? DisplayText.Sanitize(m.LastName) : id;
        int games = _teams.DemosOf(us.Id).Count;
        MyTeamLine = $"{DisplayText.Sanitize(us.Name)} · squad: {string.Join(", ", squad.Squad!.Select(Name))} · {TeamSuggestions.Count(games, "game")}";
        HashSet<string> core = new(squad.Squad!, StringComparer.Ordinal);
        List<string> fills =
        [
            .. members.Where(m => !core.Contains(m.Key) && m.Value.Count >= 2)
                .OrderByDescending(m => m.Value.Count).ThenBy(m => m.Key, StringComparer.Ordinal)
                .Take(6)
                .Select(m => $"{DisplayText.Sanitize(m.Value.LastName)} ({m.Value.Count})")
        ];
        FillsLine = fills.Count > 0 ? "regular fills: " + string.Join(", ", fills) : "no regular fills yet";
    }

    private void ProjectSquadCandidates()
    {
        SquadCandidates.Clear();
        (IReadOnlyList<SuggestedPlayer> players, _, _) = _teams.CoPlayers();
        HashSet<string> current = new(_teams.Us?.Rosters.FirstOrDefault(r => r.IsSquad)?.Squad ?? [], StringComparer.Ordinal);
        foreach (SuggestedPlayer player in players.Take(20))
        {
            SquadCandidates.Add(new SquadCandidateRow(player.SteamId64, DisplayText.Sanitize(player.Name), player.Games)
            {
                IsChecked = current.Contains(player.SteamId64)
            });
        }
    }

    private static string MeLine(MeSuggestion s) =>
        $"{DisplayText.Sanitize(s.LastName)} ({s.SteamId64}) is on {s.DemoCount} of your demos ({s.Share:P0}). Is that you?";

    private void ProjectSuggestions()
    {
        Suggestions.Clear();
        IEnumerable<(TeamSuggestion S, bool Dismissed)> shown = _teams.Suggestions.Select(s => (s, false));
        if (ShowSettled)
        {
            shown = shown.Concat(_teams.DismissedSuggestions.Select(s => (s, true)));
        }

        foreach ((TeamSuggestion s, bool dismissed) in shown)
        {
            string text = s.Kind switch
            {
                TeamSuggestionKind.Squad =>
                    $"You played {TeamSuggestions.Count(s.GamesTogether, "game")} with {DisplayText.Sanitize(TeamSuggestions.Join(s.Players.Skip(1)))}. Make them your team?",
                TeamSuggestionKind.RosterChange =>
                    $"{DisplayText.Sanitize(s.Subject)}: {DisplayText.Sanitize(TeamSuggestions.Join(s.Players))} now plays in 5 or more of the last 10"
                    + (s.Left.Count > 0 ? $" in place of {DisplayText.Sanitize(TeamSuggestions.Join(s.Left))}" : "")
                    + $". Start a new roster from about {s.Since:yyyy-MM-dd}?",
                _ => $"Two teams are tagged {DisplayText.Sanitize(s.Subject)}. Merge them?"
            };
            Suggestions.Add(new SuggestionRow(s.Id, text) { IsDismissed = dismissed });
        }

        if (ShowSettled && _teams.DismissedMeSuggestion is { } me)
        {
            Suggestions.Add(new SuggestionRow(TeamIdentityService.MeSuggestionId(me.SteamId64), MeLine(me)) { IsDismissed = true });
        }

        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SettledLabel));
    }

    // The footer states what could not be clustered and, when no demo has a recurring opponent, that
    // the Dossier has no subject; in matchmaking that is the normal case.
    private string Footer()
    {
        int unclusterable = _teams.UnclusterableCount;
        List<string> parts = [];
        if (unclusterable > 0)
        {
            parts.Add($"{unclusterable} demo{(unclusterable == 1 ? "" : "s")} could not be clustered (team split needs a re-index)");
        }

        if (_teams.DemoCount > 0 && !_teams.HasRecurringOpponent)
        {
            parts.Add("every opponent is unaffiliated: the Dossier has no recurring opponent to describe");
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>One team in the list.</summary>
public sealed class TeamRow
{
    public TeamRow(Team team, int demoCount, long lastSeenTicks)
    {
        Team = team;
        DemoCount = demoCount;
        LastSeen = lastSeenTicks > 0 ? new DateTime(lastSeenTicks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "never";
    }

    public Team Team { get; }

    public Guid Id => Team.Id;

    public string Name => DisplayText.Sanitize(Team.Name);

    public bool IsUs => Team.IsUs;

    public bool Hidden => Team.Hidden;

    public int RosterCount => Team.Rosters.Count;

    public int DemoCount { get; }

    public string LastSeen { get; }

    public string Summary =>
        $"{RosterCount} roster{(RosterCount == 1 ? "" : "s")} · {(DemoCount == 0 ? "no demos" : $"{DemoCount} demo{(DemoCount == 1 ? "" : "s")}")} · last {LastSeen}";
}

/// <summary>One roster of the selected team, with its members.</summary>
public sealed class RosterRow
{
    public RosterRow(Roster roster, IReadOnlyDictionary<string, TeamIndexMember> members)
    {
        string Name(string id) => members.TryGetValue(id, out TeamIndexMember? m) ? DisplayText.Sanitize(m.LastName) : id;
        if (roster.IsSquad)
        {
            Label = "squad · the players you chose";
            FiveLine = "squad: " + string.Join(", ", roster.Squad!.Select(Name));
            CoreLine = "";
            foreach ((string id, TeamIndexMember m) in members.OrderByDescending(m => m.Value.Count).ThenBy(m => m.Key, StringComparer.Ordinal))
            {
                Members.Add(new MemberRow(id, m));
            }

            return;
        }

        Label = $"{roster.Id} · since {roster.Since:yyyy-MM-dd}{(roster.Label is null ? "" : $" · {DisplayText.Sanitize(roster.Label)}")}";
        FiveLine = roster.HasCoreLineup
            ? "fixed five: " + string.Join(", ", roster.CoreLineup!.Select(Name))
            : "no fixed five yet";
        CoreLine = roster.ExtendedCore is { Count: > 0 }
            ? "extended core: " + string.Join(", ", roster.ExtendedCore.Select(Name))
            : "";
        foreach ((string id, TeamIndexMember m) in members.OrderByDescending(m => m.Value.Count).ThenBy(m => m.Key, StringComparer.Ordinal))
        {
            Members.Add(new MemberRow(id, m));
        }
    }

    public string Label { get; }

    public string FiveLine { get; }

    public string CoreLine { get; }

    public ObservableCollection<MemberRow> Members { get; } = [];
}

/// <summary>One member of a roster.</summary>
public sealed class MemberRow(string steamId, TeamIndexMember member)
{
    public string SteamId { get; } = steamId;

    public string Name { get; } = DisplayText.Sanitize(member.LastName);

    public int Appearances { get; } = member.Count;

    public string FirstSeen { get; } = new DateTime(member.FirstTicks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string LastSeen { get; } = new DateTime(member.LastTicks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public int StandIns { get; } = member.StandInCount;
}

/// <summary>One pending suggestion, as the inbox shows it.</summary>
public sealed class SuggestionRow(string id, string text)
{
    public string Id { get; } = id;

    public string Text { get; } = text;

    /// <summary>Listed under "Show settled": offers Restore instead of Accept and Dismiss.</summary>
    public bool IsDismissed { get; init; }
}

/// <summary>One player the squad editor offers.</summary>
public sealed partial class SquadCandidateRow(string steamId, string name, int games) : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public string SteamId { get; } = steamId;

    public string Name { get; } = name;

    public int Games { get; } = games;

    public string Label => $"{Name} ({Games} with you)";
}

/// <summary>One demo of the selected team: the side it played and who it faced.</summary>
public sealed partial class DemoRow(string path, int side, long orderTicks) : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public string Path { get; } = path;

    /// <summary>2 = T, 3 = CT, the end-of-demo side.</summary>
    public int Side { get; } = side;

    public long OrderTicks { get; } = orderTicks;

    public string SideLabel => Side == 3 ? "CT" : "T";

    public string FileName { get; init; } = "";

    public string Map { get; init; } = "";

    public string Date { get; init; } = "";

    public string Opponent { get; init; } = "";

    public bool StandIn { get; init; }

    public string Detail { get; init; } = "";
}
