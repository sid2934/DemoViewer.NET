#region

using System.Globalization;
using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     What decides whether a demo's index rows are current. Anything that changes what a row means is
///     in it: the sidecar schema, the cadence, the token grammar, the Round Facts schema the alive and
///     side joins read, the token source with the per-map zone version in the zones mode, and the
///     positions file's tuple meaning. Anything joined at query time (buy thresholds, team identity) is
///     not, so a Round Facts <c>params:</c> edit never re-indexes the library. A sidecar written before
///     the positions file existed carries no <c>pos=</c> and is stale, which is what lets a Result Card
///     treat "never indexed" and "indexed before positions" as one state.
/// </summary>
public static class RoundIndexFingerprint
{
    /// <summary>The fingerprint the rows a builder writes with <paramref name="source" /> carry.</summary>
    /// <param name="options">The sampling parameters.</param>
    /// <param name="source">The place source of the rows.</param>
    public static string Compose(RoundIndexOptions options, IPlaceSource source)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);

        string fingerprint =
            $"ri{DemoCacheRecord.RoundIndexSchema};cadence={options.CadenceSeconds.ToString(CultureInfo.InvariantCulture)}"
            + $";token={PlaceCountToken.TokenVersion};rf={DemoCacheRecord.RoundFactsSchema};src={source.SourceId}";
        if (source.ZonesVersion is { } version)
        {
            fingerprint = $"{fingerprint};zv={version}";
        }

        return $"{fingerprint};pos={RoundPositionsDocument.PositionSchema}";
    }
}

/// <summary>
///     The place source and fingerprint in force for a map under the current settings. One instance
///     serves the evaluator, the query service and the strip, so they can never disagree on which rows
///     are current. In the zones mode a map without zones falls back to the pawn for that map alone,
///     and <see cref="IsZoneFallback" /> is how the strip says so.
/// </summary>
public sealed class RoundIndexPlaceSources
{
    private readonly RoundIndexOptions _options;
    private readonly Func<RoundIndexTokenSource> _tokenSource;
    private readonly IZonePlaceResolverSource _zones;

    /// <param name="tokenSource">The live <c>SituationsSettings.TokenSource</c>.</param>
    /// <param name="zones">Where a map's zone resolver comes from; none until Zone Baking's resolver lands.</param>
    /// <param name="options">The sampling parameters; the shipped defaults when null.</param>
    public RoundIndexPlaceSources(
        Func<RoundIndexTokenSource> tokenSource,
        IZonePlaceResolverSource? zones = null,
        RoundIndexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tokenSource);
        _tokenSource = tokenSource;
        _zones = zones ?? NoZonePlaceResolverSource.Instance;
        _options = options ?? RoundIndexOptions.Default;
    }

    /// <summary>The sampling parameters every build uses.</summary>
    public RoundIndexOptions Options => _options;

    /// <summary>Where a map's zone resolver comes from. The Query Canvas resolves a drop through the same source the builder mints tokens through.</summary>
    public IZonePlaceResolverSource Zones => _zones;

    /// <summary>The source rows for <paramref name="map" /> are minted through right now.</summary>
    /// <param name="map">The map, or null when the demo's header has not been read; the pawn then.</param>
    public IPlaceSource SourceFor(string? map)
    {
        if (_tokenSource() == RoundIndexTokenSource.Zones
            && map is { Length: > 0 }
            && _zones.TryGet(map) is { } resolver)
        {
            return new ZonePlaceSource(resolver);
        }

        return PawnPlaceSource.Instance;
    }

    /// <summary>The fingerprint current rows for <paramref name="map" /> must carry.</summary>
    /// <param name="map">The map, or null when unknown.</param>
    public string FingerprintFor(string? map) => RoundIndexFingerprint.Compose(_options, SourceFor(map));

    /// <summary>True when the zones mode is on and <paramref name="map" /> has no zones, so its rows use the pawn.</summary>
    /// <param name="map">The map.</param>
    public bool IsZoneFallback(string? map) =>
        _tokenSource() == RoundIndexTokenSource.Zones
        && (map is not { Length: > 0 } || _zones.TryGet(map) is null);
}
