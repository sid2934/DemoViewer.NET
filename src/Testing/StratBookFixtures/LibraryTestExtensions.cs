#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The pack's views of a test's demo cache: the library rows and records as the SDK hands them to the pack,
///     and the Round Facts rows through the pack's own source. One library per store, so every service a test
///     builds over one store reads the same rows and the same change events.
/// </summary>
internal static class LibraryTestExtensions
{
    /// <summary>The library over <paramref name="store" />, as the host builds it.</summary>
    public static IExtensionLibrary Library(this DemoCacheStore store) => HostLibrary.For(store, null);

    /// <summary>The Round Facts rows on <paramref name="store" />'s records, ungated.</summary>
    public static IRoundFactsSource RoundFacts(this DemoCacheStore store) => new RoundFactsSource(store);

    /// <summary>A demo's library row.</summary>
    public static LibraryDemo Row(this DemoCacheStore store, string path) => store.Library().Find(path)!;

    /// <summary>A record as the library hands it to a record pass.</summary>
    public static LibraryDemoDetail Detail(this DemoCacheStore store, DemoCacheRecord record) =>
        HostLibrary.For(store, null).DetailOf(record);

    // Projections need a store only for the rows it already holds; a record or row from a fixture projects as itself.
    private static readonly DemoCacheStore Memory = new(null);

    /// <summary>A fixture's record as the library hands it to a record pass.</summary>
    public static LibraryDemoDetail AsDetail(this DemoCacheRecord record) => HostLibrary.For(Memory, null).DetailOf(record);

    /// <summary>A fixture's index row as the library hands it out.</summary>
    public static LibraryDemo AsRow(this DemoCacheIndexEntry entry) => HostLibrary.For(Memory, null).Project(entry);

    /// <summary>A cached round as the library hands it out.</summary>
    public static LibraryRound ToLibrary(this CachedRound round) => new(round.Number, round.StartTickFrameClock);

    /// <summary>Cached rounds as the library hands them out.</summary>
    public static IReadOnlyList<LibraryRound> ToLibrary(this IEnumerable<CachedRound> rounds) => [.. rounds.Select(ToLibrary)];

    /// <summary>A rounds lookup for a tag session over fixed rounds.</summary>
    public static Task<IReadOnlyList<LibraryRound>?> AsRounds(this IEnumerable<CachedRound>? rounds) =>
        Task.FromResult<IReadOnlyList<LibraryRound>?>(rounds?.ToLibrary());
}
