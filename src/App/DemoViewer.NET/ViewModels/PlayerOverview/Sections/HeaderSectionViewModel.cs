#region

using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview.Sections;

/// <summary>
///     Who the page is about: the subject's latest name and the others it played under, its SteamID64 and Steam
///     profile address, how many demos hold it and when it was first and last seen, and whether it is the user,
///     with the action that makes it one of the user's accounts or the active one. Beside the name, the latest
///     Premier rating and Competitive Skill Group over every demo of the subject. The avatar is a placeholder with
///     the name's initials until an online extension can fetch the Steam picture.
/// </summary>
public sealed partial class HeaderSectionViewModel : PlayerOverviewSection
{
    /// <summary>How many other names the alias line lists before it counts the rest.</summary>
    public const int AliasesShown = 5;

    private readonly PlayerOverviewServices _services;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAliases))]
    private string _aliases = "";

    [ObservableProperty]
    private bool _canSetActive;

    [ObservableProperty]
    private bool _canSetAsMe;

    [ObservableProperty]
    private string _demoCountText = "";

    [ObservableProperty]
    private bool _isMine;

    [ObservableProperty]
    private string _initials = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRanks))]
    private IReadOnlyList<RankCardItem> _ranks = [];

    [ObservableProperty]
    private string _profileUrl = "";

    [ObservableProperty]
    private string _seenText = "";

    [ObservableProperty]
    private string _steamId64 = "";

    /// <param name="services">Where the header adds the subject to the user's accounts.</param>
    public HeaderSectionViewModel(PlayerOverviewServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public override string Id => "header";

    /// <inheritdoc />
    public override bool IsPersistent => true;

    /// <summary>True when there is an alias line.</summary>
    public bool HasAliases => Aliases.Length > 0;

    /// <summary>True when the card shows a rank.</summary>
    public bool HasRanks => Ranks.Count > 0;


    /// <inheritdoc />
    public override void Update(PlayerOverviewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Player is not { } player)
        {
            IsVisible = false;
            return;
        }

        string id = player.SteamId64.ToString(CultureInfo.InvariantCulture);
        Name = player.Name.Length > 0 ? DisplayText.Sanitize(player.Name) : "Unnamed player";
        Initials = InitialsOf(DisplayText.Sanitize(player.Name));
        Aliases = AliasLine(player.Aliases);
        SteamId64 = id;
        ProfileUrl = "https://steamcommunity.com/profiles/" + id;
        DemoCountText = player.DemoCount == 1 ? "1 demo" : string.Create(CultureInfo.InvariantCulture, $"{player.DemoCount} demos");
        SeenText = player.FirstSeenTicks == player.LastSeenTicks
            ? "seen " + DateOf(player.LastSeenTicks)
            : $"first seen {DateOf(player.FirstSeenTicks)} · last seen {DateOf(player.LastSeenTicks)}";
        Ranks = context.Ranks is { } ranks ? RankSectionViewModel.Latest(context.Matches, ranks) : [];
        IsMine = context.IsMine;
        CanSetAsMe = !context.IsMine;
        CanSetActive = context.IsMine && !string.Equals(_services.Identity.ActiveSteamId64, id, StringComparison.Ordinal);
        IsVisible = true;
    }

    // The names after the latest, sanitized, the most recent first.
    private static string AliasLine(IReadOnlyList<PlayerAlias> aliases)
    {
        if (aliases.Count < 2)
        {
            return "";
        }

        string shown = string.Join(", ", aliases.Skip(1).Take(AliasesShown).Select(a => DisplayText.Sanitize(a.Name)));
        int more = aliases.Count - 1 - AliasesShown;
        return more > 0 ? string.Create(CultureInfo.InvariantCulture, $"also {shown} and {more} more") : "also " + shown;
    }

    // The first letter or digit of each of the first two words; "?" for a name with none.
    internal static string InitialsOf(string name)
    {
        StringBuilder letters = new();
        int taken = 0;
        foreach (string word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (Rune rune in word.EnumerateRunes())
            {
                if (Rune.IsLetterOrDigit(rune))
                {
                    letters.Append(Rune.ToUpperInvariant(rune).ToString());
                    taken++;
                    break;
                }
            }

            if (taken == 2)
            {
                break;
            }
        }

        return taken > 0 ? letters.ToString() : "?";
    }

    private static string DateOf(long ticks) =>
        ticks > 0 && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "unknown";

    /// <summary>Adds the subject to the user's accounts. It becomes active only when the user had none.</summary>
    [RelayCommand]
    private void SetAsMe()
    {
        IdentityService identity = _services.Identity;
        if (SteamId64.Length > 0 && !identity.IsMine(SteamId64))
        {
            identity.SetAccounts([.. identity.Accounts, new IdentityAccount(SteamId64, null, DateTime.UtcNow)]);
        }
    }

    /// <summary>Makes the subject, already one of the user's accounts, the active one.</summary>
    [RelayCommand]
    private void SetActive()
    {
        if (SteamId64.Length > 0)
        {
            _services.Identity.SetActive(SteamId64);
        }
    }
}
