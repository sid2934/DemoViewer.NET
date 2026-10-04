#region

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Updates;

/// <summary>
///     One extension's update feed, <c>extensions.json</c> (strat-book-plugin.md §7.10): the extension id and
///     every published version, each with its manifest (so the app can judge compatibility before it downloads
///     anything), the zip to fetch, and what that zip must weigh and hash to. Published as a release asset by
///     item 37's workflow; read by <see cref="ExtensionUpdateService" />. Parsing is strict about every member
///     it knows and ignores the ones it does not, so a newer feed still reads on an older app.
/// </summary>
/// <param name="Id">The extension id every entry's manifest must carry.</param>
/// <param name="Entries">Every published version, highest first.</param>
public sealed partial record ExtensionFeed(string Id, IReadOnlyList<ExtensionFeedEntry> Entries)
{
    /// <summary>The asset name the feed is published under.</summary>
    public const string FileName = "extensions.json";

    /// <summary>More entries than this is not a feed.</summary>
    public const int MaxEntries = 500;

    /// <summary>The feed may not be larger than this, in characters, before it is parsed.</summary>
    public const int MaxLength = 4 * 1024 * 1024;

    private static readonly JsonDocumentOptions _json = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16
    };

    /// <summary>The highest version published, or null for an empty feed.</summary>
    public ExtensionFeedEntry? Latest => Entries.Count == 0 ? null : Entries[0];

    /// <summary>Parses feed JSON.</summary>
    /// <exception cref="ExtensionFeedException">Malformed JSON, a missing or bad member, a manifest that does not match its entry.</exception>
    public static ExtensionFeed Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxLength)
        {
            throw new ExtensionFeedException("The feed is too large.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, _json);
        }
        catch (JsonException ex)
        {
            throw new ExtensionFeedException("The feed is not valid JSON: " + ex.Message, ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ExtensionFeedException("The feed must be a JSON object.");
            }

            string id = RequiredString(root, "id");
            if (!IdPattern().IsMatch(id) || !id.Contains('.', StringComparison.Ordinal))
            {
                throw new ExtensionFeedException($"'id' must be a reverse-DNS name; got '{id}'.");
            }

            if (!root.TryGetProperty("entries", out JsonElement entriesElement) || entriesElement.ValueKind != JsonValueKind.Array)
            {
                throw new ExtensionFeedException("'entries' is required and must be an array.");
            }

            if (entriesElement.GetArrayLength() > MaxEntries)
            {
                throw new ExtensionFeedException($"The feed has more than {MaxEntries} entries.");
            }

            List<ExtensionFeedEntry> entries = [];
            HashSet<SemVersion> seen = [];
            foreach (JsonElement element in entriesElement.EnumerateArray())
            {
                ExtensionFeedEntry entry = ParseEntry(element, id);
                if (!seen.Add(entry.Version))
                {
                    throw new ExtensionFeedException($"Version {entry.Version} appears twice.");
                }

                entries.Add(entry);
            }

            entries.Sort((a, b) => b.Version.CompareTo(a.Version));
            return new ExtensionFeed(id, entries);
        }
    }

    private static ExtensionFeedEntry ParseEntry(JsonElement element, string feedId)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ExtensionFeedException("Every entry must be a JSON object.");
        }

        string versionText = RequiredString(element, "version");
        if (!SemVersion.TryParse(versionText, out SemVersion? version))
        {
            throw new ExtensionFeedException($"'version' is not a semantic version: '{versionText}'.");
        }

        if (!element.TryGetProperty("manifest", out JsonElement manifestElement) || manifestElement.ValueKind != JsonValueKind.Object)
        {
            throw new ExtensionFeedException($"Entry {version}: 'manifest' is required and must be an object.");
        }

        ExtensionManifest manifest;
        try
        {
            manifest = ExtensionManifest.Parse(manifestElement.GetRawText());
        }
        catch (ExtensionManifestException ex)
        {
            throw new ExtensionFeedException($"Entry {version}: {ex.Message}", ex);
        }

        if (!string.Equals(manifest.Id, feedId, StringComparison.Ordinal))
        {
            throw new ExtensionFeedException($"Entry {version}: the manifest is for '{manifest.Id}', not '{feedId}'.");
        }

        if (manifest.Version != version)
        {
            throw new ExtensionFeedException($"Entry {version}: the manifest says version {manifest.Version}.");
        }

        string urlText = RequiredString(element, "url");
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ExtensionFeedException($"Entry {version}: 'url' must be an absolute https URL.");
        }

        string sha256 = RequiredString(element, "sha256");
        if (!Sha256Pattern().IsMatch(sha256))
        {
            throw new ExtensionFeedException($"Entry {version}: 'sha256' must be 64 hex characters.");
        }

        if (!element.TryGetProperty("size", out JsonElement sizeElement) || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out long size) || size <= 0)
        {
            throw new ExtensionFeedException($"Entry {version}: 'size' must be a positive integer.");
        }

        DateTimeOffset? publishedAt = null;
        if (element.TryGetProperty("publishedAt", out JsonElement publishedElement) && publishedElement.ValueKind != JsonValueKind.Null)
        {
            if (publishedElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(publishedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset published))
            {
                throw new ExtensionFeedException($"Entry {version}: 'publishedAt' is not a timestamp.");
            }

            publishedAt = published;
        }

        return new ExtensionFeedEntry(version, manifest, url, sha256.ToLowerInvariant(), size, publishedAt);
    }

    private static string RequiredString(JsonElement element, string member)
    {
        if (!element.TryGetProperty(member, out JsonElement value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ExtensionFeedException($"'{member}' is required.");
        }

        return value.GetString()!.Trim();
    }

    // The id names a folder under the config root, so only characters that are a plain name everywhere.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();
}

/// <summary>
///     One published extension version. The manifest is the <c>extension.json</c> inside the zip, repeated
///     here so the app can judge the version without downloading it; the sha and size are checked against
///     the download before it is opened.
/// </summary>
/// <param name="Version">The extension version; equals <c>Manifest.Version</c>.</param>
/// <param name="Manifest">The manifest the zip carries.</param>
/// <param name="Url">The zip, an absolute https URL.</param>
/// <param name="Sha256">The zip's SHA-256, lower-case hex.</param>
/// <param name="Size">The zip's length in bytes.</param>
/// <param name="PublishedAt">When the version was published, when the feed says.</param>
public sealed record ExtensionFeedEntry(
    SemVersion Version,
    ExtensionManifest Manifest,
    Uri Url,
    string Sha256,
    long Size,
    DateTimeOffset? PublishedAt);

/// <summary>A feed that could not be read or did not validate.</summary>
public sealed class ExtensionFeedException : Exception
{
    public ExtensionFeedException()
    {
    }

    public ExtensionFeedException(string message) : base(message)
    {
    }

    public ExtensionFeedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
