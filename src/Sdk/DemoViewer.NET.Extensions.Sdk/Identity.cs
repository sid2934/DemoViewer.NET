namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     Who the user is: the accounts they marked as their own under Settings, Identity, one of them active. Read
///     only; the user edits it in Settings. Every id is a decimal SteamID64.
/// </summary>
public interface IExtensionIdentity
{
    /// <summary>Every account that is the user, in the order they were added. Safe to read from any thread.</summary>
    IReadOnlyList<string> Accounts { get; }

    /// <summary>
    ///     The account the user's own stats are about, one of <see cref="Accounts" />, or null when there are none.
    ///     Anything that asks "which side was the user on" reads every account, not only this one.
    /// </summary>
    string? ActiveAccount { get; }

    /// <summary>Whether <paramref name="steamId64" /> is one of <see cref="Accounts" />. Safe to call from any thread.</summary>
    /// <param name="steamId64">A decimal SteamID64.</param>
    bool IsMine(string? steamId64);

    /// <summary>Raised on the UI thread after the accounts or the active one change.</summary>
    event Action? Changed;
}
