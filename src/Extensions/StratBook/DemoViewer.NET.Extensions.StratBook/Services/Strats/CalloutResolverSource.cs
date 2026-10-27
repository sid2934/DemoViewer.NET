#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Strats;

/// <summary>
///     Builds a <see cref="CalloutResolver" /> for one owner on one map: the store's alias table over the map's
///     zones, so every caller agrees on the same vocabulary and the same team words.
///     <para>
///         This is the one seam that turns a map name into a <see cref="CalloutResolver" />; the Strat Book,
///         the Query Canvas and a future TagQuery place filter all build theirs through it rather than each
///         probing a map's art on its own.
///     </para>
///     <para>
///         The hold is per call, not per instance: these are built on the UI thread as a tab opens, and a
///         long-lived hold here would pin a bundle for every map anyone ever typed a callout against. The
///         refresh is what keeps an alias table over the user's latest saved overlay.
///     </para>
/// </summary>
public sealed class CalloutResolverSource
{
    private readonly Func<string, IMapAssetLease?> _acquire;
    private readonly StratStore _store;

    /// <param name="store">The alias tables. The app's own map art, through the one bundle registry.</param>
    public CalloutResolverSource(StratStore store)
        : this(store, MapAssets.Acquire)
    {
    }

    /// <param name="store">The alias tables.</param>
    /// <param name="acquire">Takes a hold on a map's art, or answers null when the app ships none for it.</param>
    public CalloutResolverSource(StratStore store, Func<string, IMapAssetLease?> acquire)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(acquire);
        _store = store;
        _acquire = acquire;
    }

    /// <summary>The resolver for an owner's aliases over a map's zones, else the embedded canonical list.</summary>
    /// <param name="owner">Whose aliases to load.</param>
    /// <param name="map">The map, in the parser's spelling.</param>
    public CalloutResolver For(StratOwner owner, string map)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(map);

        // Nothing here throws: a missing bundle, no overlay directory or a malformed overlay each fall
        // back to the baked set or, with no art at all, to null, which CalloutResolver.For reads as "use
        // the embedded list".
        using IMapAssetLease? lease = _acquire(map);
        return CalloutResolver.For(map, lease?.RefreshZones()?.Zones, _store.LoadCallouts(owner, map));
    }

    /// <summary>
    ///     Canonical names only, no owner's aliases: what the callouts editor checks a typed place against
    ///     before it becomes someone's word for it.
    /// </summary>
    /// <param name="map">The map, in the parser's spelling.</param>
    public CalloutResolver Canonical(string map)
    {
        ArgumentException.ThrowIfNullOrEmpty(map);
        using IMapAssetLease? lease = _acquire(map);
        return CalloutResolver.For(map, lease?.RefreshZones()?.Zones, null);
    }

    /// <summary>
    ///     The resolver for a surface with no book of its own (the Query Canvas, a TagQuery place filter): the
    ///     "us" team's aliases when Team Identity has one, else <c>me</c>'s, the same default the Strat Book
    ///     falls back to (<c>StratBookTabViewModel.RefreshOwners</c>).
    /// </summary>
    /// <param name="teams">Team Identity, for the "us" team; null reads as no team marked.</param>
    /// <param name="map">The map, in the parser's spelling.</param>
    public CalloutResolver ForDefaultOwner(TeamIdentityService? teams, string map)
    {
        ArgumentException.ThrowIfNullOrEmpty(map);
        StratOwner owner = teams?.Us is { } us ? StratOwner.Team(us.Id) : StratOwner.Me();
        return For(owner, map);
    }
}
