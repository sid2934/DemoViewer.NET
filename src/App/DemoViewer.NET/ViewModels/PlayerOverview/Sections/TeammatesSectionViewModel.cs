#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.Players;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     Everyone who ended a demo of the filter's window on the subject's side: the matches they shared, the win
///     rate together and the teammate's HLTV over those matches. Computed from the window the tab already read,
///     so it reads no file; each teammate opens their own overview.
/// </summary>
public sealed partial class TeammatesSectionViewModel : PlayerOverviewSection
{
    private readonly PlayerOverviewServices _services;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _noteText = "";

    [ObservableProperty]
    private IReadOnlyList<TeammateRowItem> _rows = [];

    /// <param name="services">Where a teammate's overview opens.</param>
    public TeammatesSectionViewModel(PlayerOverviewServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public override string Id => "teammates";

    /// <summary>True when there is a line under the header.</summary>
    public bool HasNote => NoteText.Length > 0;

    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyList<PlayerTeammate> mates = context.Player is { } player ? Teammates.Of(player.SteamId64, context.Window) : [];
        if (mates.Count == 0)
        {
            IsVisible = false;
            Rows = [];
            CountText = NoteText = "";
            return;
        }

        Rows = [.. mates.Select(m => new TeammateRowItem(m))];
        CountText = mates.Count == 1 ? "1 player" : string.Create(CultureInfo.InvariantCulture, $"{mates.Count} players");
        int shared = context.Window.Count(d => d.Match.Side is not null);
        int withStats = context.Window.Count(d => d.Match.Side is not null && d.Stats is not null);
        NoteText = context.IsReadingStats
            ? "Reading stats"
            : withStats == shared
                ? ""
                : string.Create(CultureInfo.InvariantCulture,
                    $"Win rates and ratings from the {withStats} of {shared} matches with stats; the library is computing the rest");
        IsVisible = true;
    }

    /// <summary>Opens the teammate's own overview.</summary>
    [RelayCommand]
    private void OpenTeammate(TeammateRowItem? row)
    {
        if (row is not null)
        {
            _services.OpenPlayer?.Invoke(row.SteamId64);
        }
    }
}

/// <summary>One teammate, every cell formatted.</summary>
public sealed class TeammateRowItem
{
    private const string None = "–";

    /// <param name="teammate">The teammate and the matches they shared.</param>
    public TeammateRowItem(PlayerTeammate teammate)
    {
        Teammate = teammate ?? throw new ArgumentNullException(nameof(teammate));
        Name = teammate.Name.Length > 0 ? DisplayText.Sanitize(teammate.Name) : SteamId64.ToString(CultureInfo.InvariantCulture);
        MatchesText = teammate.Matches.ToString(CultureInfo.InvariantCulture);
        RecordText = teammate.WithOutcome == 0
            ? None
            : teammate.Ties > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{teammate.Wins}-{teammate.Losses}-{teammate.Ties}")
                : string.Create(CultureInfo.InvariantCulture, $"{teammate.Wins}-{teammate.Losses}");
        WinRateText = teammate.WinRate is { } rate ? rate.ToString("0'%'", CultureInfo.InvariantCulture) : None;
        HltvText = teammate.Hltv is { } hltv ? hltv.ToString("0.00", CultureInfo.InvariantCulture) : None;
    }

    /// <summary>The teammate and the matches they shared.</summary>
    public PlayerTeammate Teammate { get; }

    /// <summary>The teammate's account.</summary>
    public ulong SteamId64 => Teammate.SteamId64;

    /// <summary>The newest name, sanitized, else the SteamID64.</summary>
    public string Name { get; }

    /// <summary>The matches shared.</summary>
    public string MatchesText { get; }

    /// <summary>Wins and losses together, "5-3", with ties as a third figure.</summary>
    public string RecordText { get; }

    /// <summary>The win rate together, "63%".</summary>
    public string WinRateText { get; }

    /// <summary>The teammate's HLTV over the shared matches, "1.07".</summary>
    public string HltvText { get; }
}
