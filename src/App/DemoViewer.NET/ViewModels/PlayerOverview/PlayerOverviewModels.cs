#region

using System.Globalization;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;

#endregion

namespace DemoViewer.NET.ViewModels.PlayerOverview;

/// <summary>What the Player Overview can show for its subject, the first that holds winning.</summary>
public enum PlayerOverviewState
{
    /// <summary>No demo in the library names an account.</summary>
    NoPlayers,

    /// <summary>Nobody is open and no Me account is active.</summary>
    NoSubject,

    /// <summary>The subject is in no demo of this library.</summary>
    SubjectNotFound,

    /// <summary>Some of the subject's demos have no player stats yet and the queue is computing them.</summary>
    Computing,

    /// <summary>Some of the subject's demos have no player stats and background processing is switched off.</summary>
    ProcessingOff,

    /// <summary>Some of the subject's demos have no player stats and the queue is paused.</summary>
    Paused,

    /// <summary>Every demo of the subject has its player stats.</summary>
    Ready
}

/// <summary>Where a section sits once the tab is wide enough for two columns.</summary>
public enum SectionColumn
{
    /// <summary>The wide left column; the whole width below the breakpoint.</summary>
    Main,

    /// <summary>The narrow right column; stacked under the main column below the breakpoint.</summary>
    Side
}

/// <summary>The tab's session blob: who was open and whether the user picked them.</summary>
/// <param name="SubjectSteamId64">The subject as a decimal SteamID64, or null.</param>
/// <param name="Pinned">True when the user opened the subject rather than following the active Me account.</param>
public sealed record PlayerOverviewSession(string? SubjectSteamId64, bool Pinned);

/// <summary>What the tab and its sections reach outside themselves. Every delegate is a no-op when unwired.</summary>
/// <param name="Directory">Every account in the library.</param>
/// <param name="Identity">Who the user is.</param>
/// <param name="Rows">The facet rows of the accounts shown; null where nothing reads stats yet.</param>
/// <param name="OpenDemo">Opens a demo by path.</param>
/// <param name="SelectTab">Selects a shell tab by its TabId.</param>
/// <param name="FocusPlayerInStats">Opens a demo by path and shows the account in Stats.</param>
/// <param name="OpenSettings">Opens Settings scrolled to a section anchor, or the top when null.</param>
public sealed record PlayerOverviewServices(
    PlayerDirectory Directory,
    IdentityService Identity,
    PlayerRowCache? Rows,
    Action<string> OpenDemo,
    Action<string> SelectTab,
    Action<string, ulong> FocusPlayerInStats,
    Action<string?> OpenSettings);

/// <summary>What a section renders from: the subject and the demos they are in, read once per refresh.</summary>
/// <param name="SteamId64">The subject, or null when nobody is open.</param>
/// <param name="Player">The subject's directory entry; null when nobody is open or no demo holds them.</param>
/// <param name="Matches">The subject's demos, newest match first; empty without a player.</param>
/// <param name="WithStats">How many of <paramref name="Matches" /> have their player stats written.</param>
/// <param name="IsMine">True when the subject is one of the user's Me accounts.</param>
/// <param name="Services">What the sections can reach.</param>
public sealed record PlayerOverviewContext(
    ulong? SteamId64,
    PlayerEntry? Player,
    IReadOnlyList<PlayerMatch> Matches,
    int WithStats,
    bool IsMine,
    PlayerOverviewServices Services);

/// <summary>One Me account in the tab's header switcher.</summary>
/// <param name="steamId64">The account.</param>
/// <param name="title">The label the user gave it, else its latest in-demo name sanitized, else the SteamID64.</param>
/// <param name="isActive">True for the active account.</param>
public sealed class MeAccountItem(string steamId64, string title, bool isActive)
{
    /// <summary>The account as a decimal SteamID64.</summary>
    public string SteamId64 { get; } = steamId64;

    /// <summary>What the switcher shows.</summary>
    public string Title { get; } = title;

    /// <summary>True for the active account.</summary>
    public bool IsActive { get; } = isActive;

    internal static MeAccountItem From(IdentityAccount account, PlayerDirectory directory, string? active)
    {
        string title = account.Label is { Length: > 0 } label
            ? label
            : ulong.TryParse(account.SteamId64, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
              && directory.Get(id) is { Name.Length: > 0 } entry
                ? DisplayText.Sanitize(entry.Name)
                : account.SteamId64;
        return new MeAccountItem(account.SteamId64, title, string.Equals(account.SteamId64, active, StringComparison.Ordinal));
    }
}

/// <summary>One search result: a player the typed text matched.</summary>
public sealed class PlayerCandidateItem
{
    private PlayerCandidateItem(ulong steamId64, string name, string demos, string lastSeen, string aliases)
    {
        SteamId64 = steamId64;
        Name = name;
        Demos = demos;
        LastSeen = lastSeen;
        Aliases = aliases;
    }

    /// <summary>The account.</summary>
    public ulong SteamId64 { get; }

    /// <summary>The latest name, sanitized.</summary>
    public string Name { get; }

    /// <summary>The account as a decimal string.</summary>
    public string SteamIdText => SteamId64.ToString(CultureInfo.InvariantCulture);

    /// <summary>"N demos".</summary>
    public string Demos { get; }

    /// <summary>"last seen yyyy-MM-dd", or empty when no demo has a time.</summary>
    public string LastSeen { get; }

    /// <summary>The other names the account used, sanitized, or empty.</summary>
    public string Aliases { get; }

    /// <summary>True when there is an alias line.</summary>
    public bool HasAliases => Aliases.Length > 0;

    internal static PlayerCandidateItem From(PlayerEntry entry)
    {
        string name = entry.Name.Length > 0 ? DisplayText.Sanitize(entry.Name) : "Unnamed player";
        string demos = entry.DemoCount == 1 ? "1 demo" : string.Create(CultureInfo.InvariantCulture, $"{entry.DemoCount} demos");
        string lastSeen = entry.LastSeenTicks > 0 && entry.LastSeenTicks <= DateTime.MaxValue.Ticks
            ? "last seen " + new DateTime(entry.LastSeenTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "";
        string aliases = string.Join(", ", entry.Aliases.Skip(1).Take(3).Select(a => DisplayText.Sanitize(a.Name)));
        return new PlayerCandidateItem(entry.SteamId64, name, demos, lastSeen, aliases);
    }
}
