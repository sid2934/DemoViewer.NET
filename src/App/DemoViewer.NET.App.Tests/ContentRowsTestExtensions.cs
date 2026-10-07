#region

using DemoViewer.NET.Services.DemoCache;

#endregion

namespace DemoViewer.NET.AppTests;

internal static class ContentRowsTestExtensions
{
    /// <summary>A content id's row seen from each confirmed path, primary first, then in ordinal path order.</summary>
    public static IReadOnlyList<DemoCacheIndexEntry> RowsForContentId(this DemoCacheStore store, string? contentId) =>
        store.TryGetByContentId(contentId) is { } primary
            ?
            [
                primary,
                .. primary.Locations
                    .Where(l => l.Confirmed && !string.Equals(l.Path, primary.Path, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(l => l.Path, StringComparer.Ordinal)
                    .Select(l => store.TryGetIndex(l.Path)!)
            ]
            : [];
}
