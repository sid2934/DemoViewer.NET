namespace DemoViewer.NET.Services.Players;

/// <summary>What <see cref="PlayerSearch" /> found for a piece of text.</summary>
/// <param name="Candidates">The accounts it may mean, the one in the most demos first.</param>
/// <param name="SteamId64">The account the text names outright, even when no demo holds it; null for a name or a vanity URL.</param>
/// <param name="Form">The SteamID spelling recognised, null for a name.</param>
/// <param name="IsFlagged">True when the text was only a bare account id, which may be a name.</param>
/// <param name="Vanity">The vanity name of a <c>/id/</c> URL that was not resolved.</param>
/// <param name="Notice">What the person should be told, null when nothing needs saying.</param>
public sealed record PlayerSearchResult(
    IReadOnlyList<PlayerEntry> Candidates,
    ulong? SteamId64,
    SteamIdForm? Form,
    bool IsFlagged,
    string? Vanity,
    string? Notice)
{
    public static readonly PlayerSearchResult Empty = new([], null, null, false, null, null);
}

/// <summary>Finds players in the library by SteamID in any spelling or by name, offline.</summary>
public sealed class PlayerSearch(PlayerDirectory directory, IVanityResolver? resolver = null)
{
    public const string VanityNotice = "needs the Online Connectivity extension; matching by name instead";

    /// <summary>The text's accounts, with no network.</summary>
    /// <param name="text">A SteamID in any spelling, a profile URL or part of a name.</param>
    public PlayerSearchResult Search(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string query = text.Trim();
        if (query.Length == 0)
        {
            return PlayerSearchResult.Empty;
        }

        SteamIdParse? parse = SteamIdParser.Parse(query);
        if (parse is null)
        {
            return new PlayerSearchResult(ByName(query, null), null, null, false, null, null);
        }

        if (parse.Form == SteamIdForm.VanityUrl)
        {
            return new PlayerSearchResult(ByName(parse.Vanity!, null), null, parse.Form, false, parse.Vanity, VanityNotice);
        }

        ulong id = parse.SteamId64!.Value;
        List<PlayerEntry> candidates = [];
        if (directory.Get(id) is { } known)
        {
            candidates.Add(known);
        }

        if (parse.IsFlagged)
        {
            candidates.AddRange(ByName(query, id));
        }

        return new PlayerSearchResult(candidates, id, parse.Form, parse.IsFlagged, null, null);
    }

    /// <summary>As <see cref="Search" />, asking the resolver about a vanity URL when there is one.</summary>
    /// <param name="text">A SteamID in any spelling, a profile URL or part of a name.</param>
    /// <param name="cancellationToken">Cancels the vanity lookup.</param>
    public async Task<PlayerSearchResult> SearchAsync(string text, CancellationToken cancellationToken = default)
    {
        PlayerSearchResult result = Search(text);
        if (resolver is null || result.Vanity is null)
        {
            return result;
        }

        ulong? id = await resolver.ResolveAsync(result.Vanity, cancellationToken).ConfigureAwait(false);
        if (id is not { } resolved)
        {
            return result;
        }

        List<PlayerEntry> candidates = directory.Get(resolved) is { } known ? [known] : [];
        return new PlayerSearchResult(candidates, resolved, result.Form, false, result.Vanity, null);
    }

    private List<PlayerEntry> ByName(string query, ulong? except) =>
    [
        .. directory.Players.Where(p => p.SteamId64 != except && p.Aliases.Any(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
    ];
}
