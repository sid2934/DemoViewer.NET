#region

using DemoViewer.NET.Services.Update;

#endregion

namespace DemoViewer.NET.Extensions.Updates;

/// <summary>
///     Where an extension's feed lives. The default is a release asset in this repository, the repository
///     the app updater and the release notes already read from: one rolling release per extension, tagged
///     <c>extensions-&lt;id&gt;</c>, whose <c>extensions.json</c> item 37's workflow replaces on every
///     extension release (strat-book-plugin.md §7.10). A setting may point at another https URL; the signed
///     zip is what the trust policy judges, so the feed's origin is not the gate.
/// </summary>
public static class ExtensionFeedSource
{
    /// <summary>The placeholder in a feed URL template that the extension id replaces.</summary>
    public const string IdPlaceholder = "{id}";

    /// <summary>The default template, over this repository's releases.</summary>
    public static string DefaultTemplate { get; } =
        $"https://github.com/{GitHubReleaseNotesService.Repo}/releases/download/extensions-{IdPlaceholder}/{ExtensionFeed.FileName}";

    /// <summary>
    ///     The feed URL for <paramref name="packId" /> from <paramref name="template" />, or from
    ///     <see cref="DefaultTemplate" /> when the template is null, blank or does not resolve to an https URL.
    /// </summary>
    public static Uri Resolve(string? template, string packId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        if (!string.IsNullOrWhiteSpace(template)
            && Uri.TryCreate(template.Trim().Replace(IdPlaceholder, packId, StringComparison.Ordinal), UriKind.Absolute, out Uri? custom)
            && custom.Scheme == Uri.UriSchemeHttps)
        {
            return custom;
        }

        return new Uri(DefaultTemplate.Replace(IdPlaceholder, packId, StringComparison.Ordinal));
    }
}
