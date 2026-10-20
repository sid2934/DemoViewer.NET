#region

using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview;

/// <summary>
///     One "Open player overview" entry as a row or card carries it: the account it opens, why it is off when it
///     is, and the command that opens it. A row binds the entry as the menu item's DataContext, so every site
///     follows the <see cref="PlayerOverviewLink" /> rule the same way.
/// </summary>
public sealed class PlayerOverviewEntry
{
    private readonly Action<ulong, string?>? _opener;

    /// <param name="steamId64">The account, or 0 when the row offers no action (<see cref="PlayerOverviewLink" />).</param>
    /// <param name="name">The name the row shows for the account.</param>
    /// <param name="opener">Opens an account in the tab; null where the shell has no such tab, which hides the entry.</param>
    /// <param name="whyNot">The tooltip on a disabled entry; null for the SteamID rule's.</param>
    public PlayerOverviewEntry(ulong steamId64, string? name, Action<ulong, string?>? opener, string? whyNot = null)
    {
        SteamId64 = steamId64;
        Name = name;
        _opener = opener;
        Tip = steamId64 == 0 ? whyNot ?? PlayerOverviewLink.NoSteamIdTip : null;
        OpenCommand = new RelayCommand(Open);
    }

    /// <summary>An entry that is hidden: no opener.</summary>
    public static PlayerOverviewEntry None { get; } = new(0, null, null);

    /// <summary>The account the entry opens, or 0.</summary>
    public ulong SteamId64 { get; }

    /// <summary>The name the row shows for the account.</summary>
    public string? Name { get; }

    /// <summary>True when the shell has a Player Overview tab to open.</summary>
    public bool Offers => _opener is not null;

    /// <summary>True when the entry is live: the row has an account to open.</summary>
    public bool CanOpen => SteamId64 != 0;

    /// <summary>Why the entry is off, or null when it is live.</summary>
    public string? Tip { get; }

    /// <summary>Opens the account in the Player Overview tab.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Opens the account; an entry with no account does nothing.</summary>
    public void Open()
    {
        if (CanOpen)
        {
            _opener?.Invoke(SteamId64, Name);
        }
    }
}
