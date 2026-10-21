namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>One name an account played under.</summary>
/// <param name="Name">The name as the demo carried it, unsanitized.</param>
/// <param name="FirstSeen">The <see cref="LibraryPlayerMatch.Time" /> of the oldest demo with the name.</param>
/// <param name="LastSeen">The <see cref="LibraryPlayerMatch.Time" /> of the newest demo with the name.</param>
/// <param name="Demos">How many demos carry the name.</param>
public sealed record LibraryPlayerAlias(string Name, DateTime FirstSeen, DateTime LastSeen, int Demos);

/// <summary>One account across the library: every name it played under and how many demos hold it.</summary>
/// <param name="SteamId64">The account's SteamID64.</param>
/// <param name="Aliases">Every name, the most recently seen first.</param>
/// <param name="DemoCount">How many demos hold the account, a demo with several copies once.</param>
/// <param name="FirstSeen">The <see cref="LibraryPlayerMatch.Time" /> of its oldest demo.</param>
/// <param name="LastSeen">The <see cref="LibraryPlayerMatch.Time" /> of its newest demo.</param>
public sealed record LibraryPlayerAccount(
    ulong SteamId64, IReadOnlyList<LibraryPlayerAlias> Aliases, int DemoCount, DateTime FirstSeen, DateTime LastSeen)
{
    /// <summary>The most recently seen name, unsanitized, or empty when no demo named the account.</summary>
    public string Name => Aliases.Count > 0 ? Aliases[0].Name : "";
}

/// <summary>One demo an account is in.</summary>
/// <param name="Demo">The demo's row, as <see cref="IExtensionLibrary.Demos" /> lists it.</param>
/// <param name="Side">The side the account ended the demo on, 3 for CT, 2 for T; null when it left early.</param>
/// <param name="Names">The account's names in the demo, distinct and unsanitized.</param>
/// <param name="Slots">Every slot the account held, ascending.</param>
/// <param name="LeftEarly">True when the account ended the demo as a spectator or on an abandoned slot.</param>
/// <param name="Time">
///     When the match was played, in UTC, when the demo says; else the file's last write time, local. The
///     <see cref="DateTime.Kind" /> tells which. History is ordered by it.
/// </param>
public sealed record LibraryPlayerMatch(
    LibraryDemo Demo, int? Side, IReadOnlyList<string> Names, IReadOnlyList<int> Slots, bool LeftEarly, DateTime Time);

/// <summary>
///     Every account in the library and the demos it is in, read only. It reads the index the host keeps in
///     memory, opens no file and is safe on any thread. An account is in a demo when the demo's sides have it
///     (no bot, no coach, a real SteamID) or the demo's player stats do: one only the stats have left early.
/// </summary>
public interface IPlayerDirectory
{
    /// <summary>Every account, the one in the most demos first, then the most recently seen. The same list until the library changes.</summary>
    IReadOnlyList<LibraryPlayerAccount> Accounts { get; }

    /// <summary>The account, or null when no demo holds it.</summary>
    /// <param name="steamId64">The account's SteamID64.</param>
    LibraryPlayerAccount? Account(ulong steamId64);

    /// <summary>
    ///     The accounts <paramref name="text" /> names: the one whose decimal SteamID64 it is, else every account
    ///     with a name containing it, ignoring case, in the order of <see cref="Accounts" />. Empty for blank text.
    /// </summary>
    /// <param name="text">A decimal SteamID64 or part of a name.</param>
    IReadOnlyList<LibraryPlayerAccount> Find(string text);

    /// <summary>The account's demos, the newest match first. Empty when no demo holds it.</summary>
    /// <param name="steamId64">The account's SteamID64.</param>
    IReadOnlyList<LibraryPlayerMatch> Matches(ulong steamId64);

    /// <summary>Raised on the UI thread after the library changed in a way that may move an account.</summary>
    event Action? Changed;
}

/// <summary>The directory of a host that has none: no accounts, never changes.</summary>
internal sealed class NoPlayerDirectory : IPlayerDirectory
{
    public static readonly NoPlayerDirectory Instance = new();

    public IReadOnlyList<LibraryPlayerAccount> Accounts => [];

    public LibraryPlayerAccount? Account(ulong steamId64) => null;

    public IReadOnlyList<LibraryPlayerAccount> Find(string text) => [];

    public IReadOnlyList<LibraryPlayerMatch> Matches(ulong steamId64) => [];

    public event Action? Changed
    {
        add { }
        remove { }
    }
}
