#region

using CS2DemoKit.Analysis.Visibility;
using DemoViewer.NET.Playback2D.Core.Zones;
using DemoViewer.NET.Playback2D.Pipeline.Assets;
using DemoViewer.NET.Services.Teams;

#endregion

namespace DemoViewer.NET.Services.Strats;

/// <summary>
///     Builds a <see cref="CalloutResolver" /> for one owner on one map: the store's alias table over the map's
///     zones (overview correction 15), so every caller agrees on the same vocabulary and the same team words.
///     <para>
///         This is the one seam that turns a bundle directory and an overlay directory into a
///         <see cref="CalloutResolver" />; the Strat Book, the Query Canvas and a future TagQuery place filter
///         all build theirs through it rather than each probing <see cref="ZoneAssetPipeline" /> on its own.
///     </para>
/// </summary>
public sealed class CalloutResolverSource
{
    private readonly Func<string, string?> _bundleDirFor;
    private readonly Func<string?> _overlayDir;
    private readonly StratStore _store;

    /// <param name="store">The alias tables.</param>
    public CalloutResolverSource(StratStore store)
        : this(store, MapAssetBundleReader.FindBundleDirectory, () => AppPaths.ZonesDirectory)
    {
    }

    /// <param name="store">The alias tables.</param>
    /// <param name="bundleDirFor">The map's bundle directory, or null when it has none.</param>
    /// <param name="overlayDir">The user's <c>zones/</c> directory; null for none.</param>
    public CalloutResolverSource(StratStore store, Func<string, string?> bundleDirFor, Func<string?> overlayDir)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(bundleDirFor);
        ArgumentNullException.ThrowIfNull(overlayDir);
        _store = store;
        _bundleDirFor = bundleDirFor;
        _overlayDir = overlayDir;
    }

    /// <summary>The resolver for an owner's aliases over a map's zones, else the embedded canonical list.</summary>
    /// <param name="owner">Whose aliases to load.</param>
    /// <param name="map">The map, in the parser's spelling.</param>
    public CalloutResolver For(StratOwner owner, string map)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(map);

        // The pipeline never throws: a missing bundle, no overlay directory or a malformed overlay each
        // fall back to the baked set or, with no bundle at all, to null, which CalloutResolver.For reads as
        // "use the embedded list".
        PlaceResolver? zones = ZoneAssetPipeline.TryLoad(_bundleDirFor(map), _overlayDir());
        return CalloutResolver.For(map, zones?.Zones, _store.LoadCallouts(owner, map));
    }

    /// <summary>
    ///     Canonical names only, no owner's aliases: what the callouts editor checks a typed place against
    ///     before it becomes someone's word for it.
    /// </summary>
    /// <param name="map">The map, in the parser's spelling.</param>
    public CalloutResolver Canonical(string map)
    {
        ArgumentException.ThrowIfNullOrEmpty(map);
        PlaceResolver? zones = ZoneAssetPipeline.TryLoad(_bundleDirFor(map), _overlayDir());
        return CalloutResolver.For(map, zones?.Zones, null);
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
