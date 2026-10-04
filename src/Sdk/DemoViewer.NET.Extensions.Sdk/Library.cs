namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>A demo as the Library lists it.</summary>
/// <param name="FilePath">Full path: the key every other store uses.</param>
/// <param name="FileName">The file name.</param>
/// <param name="MapName">The map, once the demo is indexed.</param>
/// <param name="Modified">The file's last write time.</param>
/// <param name="FileSizeBytes">The file's size.</param>
public sealed record LibraryDemo(string FilePath, string FileName, string? MapName, DateTime Modified, long FileSizeBytes);

/// <summary>One choice in a <see cref="LibraryFilter" />.</summary>
/// <param name="Key">What <see cref="LibraryFilter.Matches" /> receives.</param>
/// <param name="Display">What the list shows.</param>
public sealed record LibraryFilterItem(string Key, string Display);

/// <summary>A drop-down filter in the Library's filter bar.</summary>
/// <param name="Label">The drop-down's label.</param>
/// <param name="Items">Its choices.</param>
/// <param name="Matches">Whether a demo passes for the chosen key.</param>
/// <param name="Tooltip">The drop-down's tooltip.</param>
public sealed record LibraryFilter(
    string Label, IReadOnlyList<LibraryFilterItem> Items, Func<LibraryDemo, string, bool> Matches, string? Tooltip = null);

/// <summary>A chip on a Library row.</summary>
/// <param name="Label">The chip text.</param>
/// <param name="Tooltip">The chip tooltip.</param>
/// <param name="IsPinned">True when the user chose it rather than a heuristic.</param>
public sealed record LibraryBadge(string Label, string? Tooltip, bool IsPinned);

/// <summary>A Library filter, a per-row badge, or both. Raise <see cref="Changed" /> when either may have changed.</summary>
public interface ILibraryContribution
{
    /// <summary>Shown only while this feature is on; null for while the extension is on.</summary>
    string? FeatureId => null;

    /// <summary>Raised when the filter's items or any badge may have changed.</summary>
    event Action? Changed;

    /// <summary>The filter, or null for none.</summary>
    LibraryFilter? Filter { get; }

    /// <summary>True when this contribution draws badges.</summary>
    bool HasBadge { get; }

    /// <summary>The badge for <paramref name="demo" />, or null for none.</summary>
    LibraryBadge? BadgeFor(LibraryDemo demo);

    /// <summary>Badges for many rows at once, keyed by <see cref="LibraryDemo.FilePath" />. Override to batch lookups.</summary>
    IReadOnlyDictionary<string, LibraryBadge?> BadgesFor(IEnumerable<LibraryDemo> demos)
    {
        ArgumentNullException.ThrowIfNull(demos);
        Dictionary<string, LibraryBadge?> result = new(StringComparer.Ordinal);
        foreach (LibraryDemo demo in demos)
        {
            result[demo.FilePath] = BadgeFor(demo);
        }

        return result;
    }

    /// <summary>The labels the user can pin from a row's menu.</summary>
    IReadOnlyList<string> BadgeLabels { get; }

    /// <summary>The menu entry that clears a pinned label, or null for none.</summary>
    string? BadgeResetLabel { get; }

    /// <summary>The tooltip of that entry.</summary>
    string? BadgeResetTooltip => null;

    /// <summary>Pins <paramref name="label" /> on <paramref name="demo" />, or clears the pin for null.</summary>
    void SetLabel(LibraryDemo demo, string? label);
}
