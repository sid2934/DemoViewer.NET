#region

using System.Runtime.CompilerServices;
using DemoViewer.NET.Services.Players;

#endregion

namespace DemoViewer.NET.Extensions;

/// <summary>
///     The app's <see cref="PlayerDirectory" /> as one extension reads it, in the SDK's types. An account is mapped
///     once per directory entry, which the directory keeps until the index moves, and a demo is the library's own
///     row for it, so an unchanged account and demo are the same instances on every read. Each of the extension's
///     change handlers runs under its guard.
/// </summary>
internal sealed class HostPlayerDirectory : IPlayerDirectory
{
    private readonly Lazy<PlayerDirectory?> _lazy;
    private readonly HostLibrary _library;
    private readonly ExtensionGuard _guard;
    private readonly Action _forward;
    private readonly ConditionalWeakTable<PlayerEntry, LibraryPlayerAccount> _mapped = new();
    private readonly object _gate = new();
    private IReadOnlyList<PlayerEntry>? _source;
    private IReadOnlyList<LibraryPlayerAccount> _accounts = [];
    private Action? _handlers;

    /// <param name="directory">
    ///     Resolves the app's directory on first use, so a context built at startup pulls in nothing; null answers
    ///     no accounts and never changes.
    /// </param>
    /// <param name="library">The library whose rows the matches carry.</param>
    /// <param name="guard">The extension's guard.</param>
    public HostPlayerDirectory(Func<PlayerDirectory?> directory, HostLibrary library, ExtensionGuard guard)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _lazy = new Lazy<PlayerDirectory?>(directory);
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _forward = OnDirectoryChanged;
    }

    private PlayerDirectory? Directory => _lazy.Value;

    /// <inheritdoc />
    public IReadOnlyList<LibraryPlayerAccount> Accounts
    {
        get
        {
            if (Directory is not { } directory)
            {
                return [];
            }

            IReadOnlyList<PlayerEntry> source = directory.Players;
            lock (_gate)
            {
                if (!ReferenceEquals(source, _source))
                {
                    _accounts = [.. source.Select(Map)];
                    _source = source;
                }

                return _accounts;
            }
        }
    }

    /// <inheritdoc />
    public LibraryPlayerAccount? Account(ulong steamId64) => Directory?.Get(steamId64) is { } entry ? Map(entry) : null;

    /// <inheritdoc />
    public IReadOnlyList<LibraryPlayerAccount> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Directory is { } directory ? [.. directory.Find(text).Select(Map)] : [];
    }

    /// <inheritdoc />
    public IReadOnlyList<LibraryPlayerMatch> Matches(ulong steamId64) =>
        Directory is { } directory ? [.. directory.Matches(steamId64).Select(MapMatch)] : [];

    /// <inheritdoc />
    /// <remarks>Joined to the directory only while the extension listens.</remarks>
    public event Action? Changed
    {
        add
        {
            if (Directory is null || value is null)
            {
                return;
            }

            bool first = _handlers is null;
            _handlers += value;
            if (first)
            {
                Directory.Changed += _forward;
            }
        }
        remove
        {
            if (Directory is null || value is null)
            {
                return;
            }

            _handlers -= value;
            if (_handlers is null)
            {
                Directory.Changed -= _forward;
            }
        }
    }

    private LibraryPlayerAccount Map(PlayerEntry entry) => _mapped.GetValue(entry, static e => new LibraryPlayerAccount(
        e.SteamId64,
        [.. e.Aliases.Select(a => new LibraryPlayerAlias(a.Name, new DateTime(a.FirstSeenTicks, DateTimeKind.Utc), new DateTime(a.LastSeenTicks, DateTimeKind.Utc), a.Demos))],
        e.DemoCount,
        new DateTime(e.FirstSeenTicks, DateTimeKind.Utc),
        new DateTime(e.LastSeenTicks, DateTimeKind.Utc)));

    private LibraryPlayerMatch MapMatch(PlayerMatch match) => new(
        _library.Project(match.Row),
        match.Side,
        match.Names,
        match.Slots,
        match.LeftEarly,
        match.Row.MatchTimeUtcTicks is { } utc
            ? new DateTime(utc, DateTimeKind.Utc)
            : new DateTime(match.Row.ModifiedTicks, DateTimeKind.Local));

    private void OnDirectoryChanged()
    {
        if (_handlers is not { } handlers)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            _guard.Run("player directory change handler", handler);
        }
    }
}
