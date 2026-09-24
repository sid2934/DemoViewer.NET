namespace DemoViewer.NET.Services.RoundIndex;

/// <summary>
///     The per-side situation token: the alive players on one side grouped by place, written as
///     <c>place:count</c> pairs joined by <c>|</c> and sorted by place under ordinal comparison. One
///     encoder shared by the index builder, the query service and Find Rounds Like This, so the token
///     the canvas builds from the current tick is byte for byte the token the sidecar stores.
///     <para>
///         The count is always written, <c>:1</c> included: one decoder with no special case is worth
///         the fifteen percent of token bytes it costs. A null place is written as <c>?</c> so the
///         alive man-count survives a pawn whose place field never networked; a query never targets it.
///         Names are the raw pawn strings, case preserved and never normalised, so a token round-trips.
///     </para>
/// </summary>
public static class PlaceCountToken
{
    /// <summary>Bumped when the grammar changes; part of the index fingerprint.</summary>
    public const int TokenVersion = 1;

    /// <summary>The place written for an alive player whose place is unknown.</summary>
    public const string NullPlace = "?";

    private const char PairSeparator = '|';
    private const char CountSeparator = ':';

    /// <summary>
    ///     Encodes place counts in canonical order. Pairs naming the same place are summed; a null or
    ///     empty place counts under <see cref="NullPlace" />; a zero or negative count is dropped.
    /// </summary>
    /// <param name="pairs">The places and how many alive players stand in each.</param>
    /// <exception cref="ArgumentException">A place contains <c>:</c> or <c>|</c>.</exception>
    public static string Encode(IEnumerable<(string? Place, int Count)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        SortedDictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach ((string? place, int count) in pairs)
        {
            if (count <= 0)
            {
                continue;
            }

            string key = Normalize(place);
            counts[key] = counts.GetValueOrDefault(key) + count;
        }

        if (counts.Count == 0)
        {
            return "";
        }

        System.Text.StringBuilder builder = new();
        foreach ((string place, int count) in counts)
        {
            if (builder.Length > 0)
            {
                builder.Append(PairSeparator);
            }

            builder.Append(place).Append(CountSeparator).Append(count);
        }

        return builder.ToString();
    }

    /// <summary>Encodes one place per alive player, counting repeats.</summary>
    /// <param name="places">One entry per alive player; null means the place is unknown.</param>
    public static string EncodePlaces(IEnumerable<string?> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        return Encode(places.Select(p => (p, 1)));
    }

    /// <summary>
    ///     Decodes a token into its pairs in canonical order. The inverse of <see cref="Encode" /> on
    ///     every token it writes; a token this build did not write is decoded as far as it parses and
    ///     re-sorted, so a hand-edited sidecar still reads.
    /// </summary>
    /// <param name="token">The stored token; empty means nobody alive.</param>
    /// <exception cref="FormatException">A pair has no count, or its count is not a positive integer.</exception>
    public static IReadOnlyList<(string Place, int Count)> Decode(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length == 0)
        {
            return [];
        }

        List<(string Place, int Count)> pairs = [];
        foreach (string pair in token.Split(PairSeparator))
        {
            int at = pair.LastIndexOf(CountSeparator);
            if (at <= 0
                || !int.TryParse(pair.AsSpan(at + 1), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int count)
                || count <= 0)
            {
                throw new FormatException($"'{pair}' is not a place:count pair");
            }

            pairs.Add((pair[..at], count));
        }

        pairs.Sort((a, b) => string.CompareOrdinal(a.Place, b.Place));
        return pairs;
    }

    /// <summary>The alive total a token carries: the sum of its counts.</summary>
    /// <param name="token">The stored token.</param>
    public static int AliveCount(string token) => Decode(token).Sum(p => p.Count);

    private static string Normalize(string? place)
    {
        if (string.IsNullOrEmpty(place))
        {
            return NullPlace;
        }

        if (place.Contains(CountSeparator) || place.Contains(PairSeparator))
        {
            throw new ArgumentException($"a place name cannot contain ':' or '|': '{place}'", nameof(place));
        }

        return place;
    }
}
