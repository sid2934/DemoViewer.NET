#region

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.Players;
using Microsoft.Extensions.Primitives;

#endregion

namespace DemoViewer.NET.Services.Identity;

/// <summary>One of the user's accounts.</summary>
/// <param name="SteamId64">The account's SteamID64, in decimal.</param>
/// <param name="Label">The user's own name for it, or null.</param>
/// <param name="AddedUtc">When it was added, UTC.</param>
public sealed record IdentityAccount(string SteamId64, string? Label, DateTime AddedUtc);

/// <summary>An account the library suggests is the user: the one in more than half of its demos.</summary>
/// <param name="SteamId64">The account.</param>
/// <param name="Name">Its most recently seen name, RAW: sanitise at render.</param>
/// <param name="DemoCount">How many demos hold it.</param>
/// <param name="Share">Its demos over every demo that holds any account.</param>
public sealed record IdentitySuggestion(string SteamId64, string Name, int DemoCount, double Share);

/// <summary>What <see cref="IdentityService.ParseAccount" /> read from what the user typed.</summary>
/// <param name="SteamId64">The account, or null when the text names none.</param>
/// <param name="FromAccountId">True when the text was a bare account id, read as an individual account.</param>
/// <param name="Problem">Why the text names no account, or null.</param>
public sealed record AccountInput(string? SteamId64, bool FromAccountId, string? Problem);

/// <summary>
///     Who the user is: the accounts in <see cref="AppSettings.Identity" /> and the one that is active
///     (<c>docs/identity-format.md</c>). The only writer of that section, through <see cref="SettingsService.Write" />,
///     and the keeper of its rule: the active account is a member of <see cref="Accounts" />, the first when unset, and
///     null only when there are none.
///     <para>
///         <see cref="Changed" /> is raised inline for this service's own writes and through the post action for an
///         edit made elsewhere (the file watcher's threadpool thread).
///     </para>
/// </summary>
public sealed partial class IdentityService : IDisposable
{
    /// <summary>The fewest demos holding any account before a suggestion is made.</summary>
    public const int SuggestionMinDemos = 20;

    // An individual account's SteamID64 is this plus its 32-bit account id.
    private const ulong IndividualBase = 76561197960265728UL;

    private readonly PlayerDirectory? _directory;
    private readonly object _gate = new();
    private readonly IDisposable _onReload;
    private readonly Action<Action> _post;
    private readonly SettingsService _settings;
    private State _state;
    private bool _writing;

    /// <param name="settings">The app's settings.</param>
    /// <param name="directory">The library's accounts, read by <see cref="Suggest" />; null makes no suggestion.</param>
    /// <param name="post">Runs <see cref="Changed" /> for an edit made elsewhere; inline when null.</param>
    public IdentityService(SettingsService settings, PlayerDirectory? directory = null, Action<Action>? post = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _directory = directory;
        _post = post ?? (a => a());
        _state = Read(settings.Current.Identity);
        _onReload = ChangeToken.OnChange(settings.Configuration.GetReloadToken, OnReload);
    }

    /// <summary>The user's own work changed: the accounts, the active one, or the dismissed suggestions.</summary>
    public event Action? Changed;

    /// <summary>Every account that is the user, in the order they were added.</summary>
    public IReadOnlyList<IdentityAccount> Accounts => Snapshot().Accounts;

    /// <summary>The active account, or null when there are none.</summary>
    public string? ActiveSteamId64 => Snapshot().Active;

    /// <summary>Accounts the user turned down as a suggestion.</summary>
    public IReadOnlyList<string> DismissedSuggestions => Snapshot().Dismissed;

    /// <summary>Whether <paramref name="steamId64" /> is one of the user's accounts.</summary>
    /// <param name="steamId64">A decimal SteamID64.</param>
    public bool IsMine(string? steamId64) =>
        steamId64 is not null && Snapshot().Accounts.Any(a => string.Equals(a.SteamId64, steamId64, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="steamId64" /> is one of the user's accounts.</summary>
    /// <param name="steamId64">A SteamID64.</param>
    public bool IsMine(ulong steamId64) => IsMine(steamId64.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    ///     Replaces the accounts. Ids that are not SteamID64s and repeats are dropped, the first of a repeat kept. The
    ///     active account stays when it is still a member, else the first becomes active.
    /// </summary>
    /// <param name="accounts">The accounts, in order.</param>
    public void SetAccounts(IEnumerable<IdentityAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        List<IdentityAccount> kept = [];
        foreach (IdentityAccount account in accounts)
        {
            if (IsSteamId64(account.SteamId64) && !kept.Any(k => string.Equals(k.SteamId64, account.SteamId64, StringComparison.Ordinal)))
            {
                kept.Add(account with { AddedUtc = Utc(account.AddedUtc) });
            }
        }

        Update(state => state with { Accounts = kept, Active = ActiveOf(kept, state.Active) });
    }

    /// <summary>Makes <paramref name="steamId64" /> the active account. False, and nothing changes, when it is not a member.</summary>
    /// <param name="steamId64">One of <see cref="Accounts" />.</param>
    public bool SetActive(string steamId64)
    {
        ArgumentNullException.ThrowIfNull(steamId64);
        if (!IsMine(steamId64))
        {
            return false;
        }

        Update(state => state with { Active = steamId64 });
        return true;
    }

    /// <summary>
    ///     The account in more than half of the library's demos, once at least <see cref="SuggestionMinDemos" /> demos
    ///     hold any account; null when it is already the user's, was dismissed, or there is none.
    /// </summary>
    public IdentitySuggestion? Suggest()
    {
        if (_directory is null || _directory.Players is not [{ } top, ..])
        {
            return null;
        }

        int demos = _directory.DemoCount;
        string id = top.SteamId64.ToString(CultureInfo.InvariantCulture);
        State state = Snapshot();
        if (demos < SuggestionMinDemos || (double) top.DemoCount / demos <= 0.5 || IsMine(id)
            || state.Dismissed.Contains(id, StringComparer.Ordinal))
        {
            return null;
        }

        return new IdentitySuggestion(id, top.Name, top.DemoCount, (double) top.DemoCount / demos);
    }

    /// <summary>Adds the suggested account. It becomes active only when the user had no account.</summary>
    /// <param name="suggestion">From <see cref="Suggest" />.</param>
    public void AcceptSuggestion(IdentitySuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (IsMine(suggestion.SteamId64) || !IsSteamId64(suggestion.SteamId64))
        {
            return;
        }

        IdentityAccount account = new(suggestion.SteamId64, null, DateTime.UtcNow);
        Update(state => state with
        {
            Accounts = [.. state.Accounts, account],
            Active = state.Accounts.Count == 0 ? account.SteamId64 : state.Active
        });
    }

    /// <summary>Never suggests <paramref name="suggestion" />'s account again.</summary>
    /// <param name="suggestion">From <see cref="Suggest" />.</param>
    public void DismissSuggestion(IdentitySuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (Snapshot().Dismissed.Contains(suggestion.SteamId64, StringComparer.Ordinal))
        {
            return;
        }

        Update(state => state with { Dismissed = [.. state.Dismissed, suggestion.SteamId64] });
    }

    /// <summary>
    ///     Reads an account from what the user typed: a SteamID64, a <c>steamcommunity.com/profiles/</c> link,
    ///     <c>[U:1:n]</c>, <c>STEAM_X:Y:Z</c>, or a bare account id, which is read as an individual account and
    ///     flagged. A <c>/id/</c> vanity link needs a lookup this app does not make offline.
    /// </summary>
    /// <param name="text">What the user typed.</param>
    public static AccountInput ParseAccount(string? text)
    {
        string input = (text ?? "").Trim();
        if (input.Length == 0)
        {
            return new AccountInput(null, false, "Enter a SteamID64, a profile link, [U:1:n] or STEAM_X:Y:Z.");
        }

        if (ProfileLink().Match(input) is { Success: true } link)
        {
            if (string.Equals(link.Groups["kind"].Value, "id", StringComparison.OrdinalIgnoreCase))
            {
                return new AccountInput(null, false,
                    "A custom profile link needs the Online Connectivity extension to look up; paste the /profiles/ link or the SteamID instead.");
            }

            input = link.Groups["value"].Value;
        }

        if (SteamId3().Match(input) is { Success: true } id3)
        {
            return FromAccountId(id3.Groups[1].Value, false);
        }

        if (SteamId2().Match(input) is { Success: true } id2
            && ulong.TryParse(id2.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong half)
            && half <= uint.MaxValue / 2)
        {
            return Of(IndividualBase + half * 2 + (id2.Groups[1].Value == "1" ? 1UL : 0UL), false);
        }

        if (ulong.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number))
        {
            return number <= uint.MaxValue ? FromAccountId(input, true) : Of(number, false);
        }

        return new AccountInput(null, false, $"\"{input}\" is not a SteamID.");

        static AccountInput FromAccountId(string digits, bool flagged) =>
            ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong accountId) && accountId <= uint.MaxValue
                ? Of(IndividualBase + accountId, flagged)
                : new AccountInput(null, false, "That account id is out of range.");

        static AccountInput Of(ulong id, bool flagged) =>
            IsIndividual(id)
                ? new AccountInput(id.ToString(CultureInfo.InvariantCulture), flagged, null)
                : new AccountInput(null, false, "That number is not an individual account's SteamID64.");
    }

    /// <summary>
    ///     The one-time import of the Strat Book's <c>teams.json</c> <c>me.steamIds</c>, run at startup before any
    ///     extension attaches. Nothing happens once <see cref="IdentitySettings.MigratedFromTeamsJson" /> is set, where
    ///     there is no file system, or while there is no <c>teams.json</c>. With no accounts yet, the file's ids become
    ///     the accounts and the first is active; either way the flag is set. The file is only read. A file that cannot be
    ///     read now is tried again at the next start. Returns how many accounts it added.
    /// </summary>
    /// <param name="settings">The app's settings.</param>
    /// <param name="teamsFile">The <c>teams.json</c> path, or null (the browser).</param>
    public static int ImportTeamsJson(SettingsService settings, string? teamsFile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (teamsFile is null || settings.Current.Identity.MigratedFromTeamsJson || !File.Exists(teamsFile))
        {
            return 0;
        }

        List<string> ids;
        try
        {
            ids = MeSteamIds(File.ReadAllText(teamsFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        int added = 0;
        settings.Write(s =>
        {
            s.Identity.MigratedFromTeamsJson = true;
            if (s.Identity.Accounts.Length > 0 || ids.Count == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            s.Identity.Accounts = [.. ids.Select(id => new IdentityAccountSettings { SteamId64 = id, AddedUtc = now })];
            s.Identity.ActiveSteamId64 = ids[0];
            added = ids.Count;
        });
        return added;
    }

    /// <inheritdoc />
    public void Dispose() => _onReload.Dispose();

    // The me.steamIds of a teams.json, read as nodes so a file this build cannot bind still yields them. Keys match
    // without regard to case; an entry that is not a SteamID64 string is skipped.
    internal static List<string> MeSteamIds(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException)
        {
            return [];
        }

        List<string> ids = [];
        if (Child(Child(root, "me"), "steamIds") is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                if (node is JsonValue value && value.TryGetValue(out string? id) && IsSteamId64(id) && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;

        static JsonNode? Child(JsonNode? node, string name) =>
            node is JsonObject obj ? obj.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value : null;
    }

    // What the directory counts as an account: a non-zero SteamID64 in plain decimal.
    private static bool IsSteamId64(string? id) =>
        ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) && value != 0
                                                                                           && value.ToString(CultureInfo.InvariantCulture) == id;

    private static bool IsIndividual(ulong id) => id > IndividualBase && id - IndividualBase <= uint.MaxValue;

    private static string? ActiveOf(List<IdentityAccount> accounts, string? active) =>
        accounts.Any(a => string.Equals(a.SteamId64, active, StringComparison.Ordinal)) ? active : accounts.Count > 0 ? accounts[0].SteamId64 : null;

    // The binder reads "…Z" as local time; a value with no zone is taken as UTC.
    private static DateTime Utc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Utc => time,
        DateTimeKind.Local => time.ToUniversalTime(),
        _ => DateTime.SpecifyKind(time, DateTimeKind.Utc)
    };

    // The section as the service sees it: invalid and repeated ids dropped, the active account repaired. A hand edit
    // that breaks the rule is read through it and written back corrected on the next write.
    private static State Read(IdentitySettings identity)
    {
        List<IdentityAccount> accounts = [];
        foreach (IdentityAccountSettings account in identity.Accounts)
        {
            if (IsSteamId64(account.SteamId64) && !accounts.Any(a => string.Equals(a.SteamId64, account.SteamId64, StringComparison.Ordinal)))
            {
                accounts.Add(new IdentityAccount(account.SteamId64, string.IsNullOrEmpty(account.Label) ? null : account.Label, Utc(account.AddedUtc)));
            }
        }

        return new State(accounts, ActiveOf(accounts, identity.ActiveSteamId64), [.. identity.DismissedSuggestions.Distinct(StringComparer.Ordinal)]);
    }

    private State Snapshot()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    private void Update(Func<State, State> change)
    {
        State next;
        lock (_gate)
        {
            next = change(_state);
            if (next.SameAs(_state))
            {
                return;
            }

            _state = next;
            _writing = true;
        }

        try
        {
            _settings.Write(s =>
            {
                s.Identity.Accounts = [.. next.Accounts.Select(a => new IdentityAccountSettings { SteamId64 = a.SteamId64, Label = a.Label, AddedUtc = a.AddedUtc })];
                s.Identity.ActiveSteamId64 = next.Active;
                s.Identity.DismissedSuggestions = [.. next.Dismissed];
            });
        }
        finally
        {
            lock (_gate)
            {
                _writing = false;
            }
        }

        Changed?.Invoke();
    }

    private void OnReload()
    {
        State read;
        lock (_gate)
        {
            if (_writing)
            {
                return;
            }

            read = Read(_settings.Current.Identity);
            if (read.SameAs(_state))
            {
                return;
            }

            _state = read;
        }

        _post(() => Changed?.Invoke());
    }

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?steamcommunity\.com/(?<kind>profiles|id)/(?<value>[^/?#\s]+)/?(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileLink();

    [GeneratedRegex(@"^\[U:1:(\d+)\]$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamId3();

    [GeneratedRegex(@"^STEAM_[0-5]:([01]):(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SteamId2();

    private sealed record State(List<IdentityAccount> Accounts, string? Active, List<string> Dismissed)
    {
        public bool SameAs(State other) =>
            Accounts.SequenceEqual(other.Accounts) && Active == other.Active && Dismissed.SequenceEqual(other.Dismissed);
    }
}
