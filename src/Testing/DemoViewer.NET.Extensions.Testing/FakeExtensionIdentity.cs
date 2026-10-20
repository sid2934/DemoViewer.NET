#region

using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Extensions.Testing;

/// <summary>
///     The user's accounts as a test sets them: what a Settings edit does to <see cref="IExtensionIdentity" />.
///     <see cref="Set" /> replaces the list and raises <see cref="Changed" /> inline, on the calling thread.
/// </summary>
internal sealed class FakeExtensionIdentity : IExtensionIdentity
{
    private string[] _accounts;

    /// <param name="accounts">The accounts to start with, the first active.</param>
    public FakeExtensionIdentity(params string[] accounts)
    {
        _accounts = [.. accounts];
        ActiveAccount = _accounts.FirstOrDefault();
    }

    public IReadOnlyList<string> Accounts => _accounts;

    public string? ActiveAccount { get; private set; }

    public bool IsMine(string? steamId64) => steamId64 is not null && _accounts.Contains(steamId64, StringComparer.Ordinal);

    public event Action? Changed;

    /// <summary>Handlers currently joined, so a test can see a released service let go.</summary>
    public int HandlerCount => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Replaces the accounts. The active one is <paramref name="active" />, else the first, as the host's rule.</summary>
    /// <param name="accounts">The accounts, in order.</param>
    /// <param name="active">One of them, or null for the first.</param>
    public void Set(IReadOnlyList<string> accounts, string? active = null)
    {
        _accounts = [.. accounts.Distinct(StringComparer.Ordinal)];
        ActiveAccount = active is not null && _accounts.Contains(active, StringComparer.Ordinal) ? active : _accounts.FirstOrDefault();
        Changed?.Invoke();
    }
}
