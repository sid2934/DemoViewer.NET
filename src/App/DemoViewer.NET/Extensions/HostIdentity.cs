#region

using DemoViewer.NET.Services.Identity;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The user's accounts as one extension reads them: <see cref="IdentityService" /> without its writes. Raises
///     <see cref="Changed" /> only when the accounts or the active one moved, so a dismissed suggestion is silent,
///     and runs each of the extension's handlers under its guard, since the service raises inline for a Settings edit.
/// </summary>
internal sealed class HostIdentity : IExtensionIdentity
{
    private readonly Lazy<IdentityService?> _lazy;
    private readonly ExtensionGuard _guard;
    private readonly Action _forward;
    private Action? _handlers;
    private (string[] Accounts, string? Active) _raised;

    /// <param name="service">
    ///     Resolves the app's identity on first use, so a context built at startup pulls in nothing; null answers no
    ///     accounts and never changes.
    /// </param>
    /// <param name="guard">The extension's guard.</param>
    public HostIdentity(Func<IdentityService?> service, ExtensionGuard guard)
    {
        ArgumentNullException.ThrowIfNull(service);
        _lazy = new Lazy<IdentityService?>(service);
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _forward = OnServiceChanged;
    }

    private IdentityService? Service => _lazy.Value;

    /// <inheritdoc />
    public IReadOnlyList<string> Accounts => Service is null ? [] : [.. Service.Accounts.Select(a => a.SteamId64)];

    /// <inheritdoc />
    public string? ActiveAccount => Service?.ActiveSteamId64;

    /// <inheritdoc />
    public bool IsMine(string? steamId64) => Service?.IsMine(steamId64) ?? false;

    /// <inheritdoc />
    /// <remarks>Joined to the service only while the extension listens.</remarks>
    public event Action? Changed
    {
        add
        {
            if (Service is null || value is null)
            {
                return;
            }

            bool first = _handlers is null;
            _handlers += value;
            if (first)
            {
                _raised = Current();
                Service.Changed += _forward;
            }
        }
        remove
        {
            if (Service is null || value is null)
            {
                return;
            }

            _handlers -= value;
            if (_handlers is null)
            {
                Service.Changed -= _forward;
            }
        }
    }

    private (string[] Accounts, string? Active) Current() => ([.. Accounts], ActiveAccount);

    private void OnServiceChanged()
    {
        (string[] accounts, string? active) = Current();
        if (accounts.AsSpan().SequenceEqual(_raised.Accounts) && string.Equals(active, _raised.Active, StringComparison.Ordinal))
        {
            return;
        }

        _raised = (accounts, active);
        if (_handlers is not { } handlers)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            _guard.Run("identity change handler", handler);
        }
    }
}
