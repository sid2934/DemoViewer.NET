namespace DemoViewer.NET.Services.Update;

/// <summary>
///     Fetches the human-readable release notes for a specific app version — the markdown body the
///     release workflow publishes with every GitHub release. Consumed by the update-notice pop-up
///     (notes for the OFFERED version) and the post-update "What's new" window (notes for the
///     RUNNING version).
///     <para>
///         Contract mirrors <see cref="IUpdateService" />: implementations never throw and never
///         block the UI thread — a network failure, a missing release, or an offline machine all
///         return <c>null</c>, and the callers degrade to a "notes unavailable" line with a link.
///         Notes are cosmetic; their absence must never break the update flow itself.
///     </para>
/// </summary>
public interface IReleaseNotesService
{
    /// <summary>
    ///     Returns the notes for <paramref name="version" /> (any reasonable form — "0.6.0",
    ///     "v0.6.0", or a full informational version with prerelease/build metadata — is
    ///     normalized to the release tag), or <c>null</c> when they cannot be fetched.
    /// </summary>
    Task<ReleaseNotes?> GetForVersionAsync(string version, CancellationToken ct = default);
}

/// <summary>One release's user-facing notes, as published on the releases feed.</summary>
/// <param name="Version">The normalized x.y.z version the notes belong to.</param>
/// <param name="Title">The release's display title (e.g. "DemoViewer.NET v0.6.0").</param>
/// <param name="BodyMarkdown">The release body — GitHub-flavored markdown.</param>
/// <param name="PublishedAt">Publication timestamp, when the feed supplied one.</param>
/// <param name="HtmlUrl">Browser URL of the release page, for a "View on GitHub" link.</param>
public sealed record ReleaseNotes(
    string Version,
    string Title,
    string BodyMarkdown,
    DateTimeOffset? PublishedAt,
    string? HtmlUrl);
