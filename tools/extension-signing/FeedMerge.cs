#region

using System.Text.Json;
using System.Text.Json.Nodes;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Updates;

/// <summary>
///     Merges one new <see cref="ExtensionFeedEntry" /> fragment into an existing <c>extensions.json</c>:
///     the release workflow runs this once per extension release, never two at once (the workflow's
///     concurrency group is per extension id). Works on
///     <see cref="JsonNode" /> rather than the <see cref="ExtensionFeed" /> records so an entry nobody
///     here understands yet (a future member) survives the round trip verbatim; the result is validated
///     with <see cref="ExtensionFeed.Parse" /> before it is returned, so a merge that produced a bad feed
///     is this type's bug, not the next reader's surprise.
/// </summary>
public static class ExtensionFeedMerge
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Merges <paramref name="entryJson" /> (one feed entry: version, manifest, url, sha256, size,
    ///     publishedAt) into <paramref name="existingFeedJson" /> (a whole <c>extensions.json</c>, or null
    ///     or blank for "no feed published yet"). Rules: an entry whose version matches one already in the
    ///     feed replaces it in place; a new version higher than every existing one is always accepted; a
    ///     new version lower than or equal to the current highest is refused unless
    ///     <paramref name="allowDowngrade" /> is true. The result is sorted highest version first.
    /// </summary>
    /// <param name="existingFeedJson">The feed as it stands, or null/blank when none has been published.</param>
    /// <param name="entryJson">The new entry, in the shape <see cref="ExtensionFeed" />'s entries carry.</param>
    /// <param name="expectedId">The extension id the feed is for and the entry's manifest must carry.</param>
    /// <param name="allowDowngrade">Accepts a new version at or below the current highest.</param>
    /// <returns>The merged feed, serialized.</returns>
    /// <exception cref="ExtensionFeedMergeException">
    ///     The entry or the existing feed does not parse, the entry's id does not match
    ///     <paramref name="expectedId" />, or the version is a refused downgrade.
    /// </exception>
    public static string Merge(string? existingFeedJson, string entryJson, string expectedId, bool allowDowngrade)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedId);

        JsonObject entry = ParseObject(entryJson, "the new entry");
        SemVersion entryVersion = RequiredVersion(entry, "version", "the new entry");
        string entryManifestId = RequiredManifestId(entry);
        if (!string.Equals(entryManifestId, expectedId, StringComparison.Ordinal))
        {
            throw new ExtensionFeedMergeException(
                $"the new entry's manifest is for '{entryManifestId}', not '{expectedId}'.");
        }

        JsonObject feed = string.IsNullOrWhiteSpace(existingFeedJson)
            ? new JsonObject { ["id"] = expectedId, ["entries"] = new JsonArray() }
            : ParseObject(existingFeedJson, "the existing feed");

        string feedId = RequiredString(feed, "id", "the existing feed");
        if (!string.Equals(feedId, expectedId, StringComparison.Ordinal))
        {
            throw new ExtensionFeedMergeException($"the existing feed is for '{feedId}', not '{expectedId}'.");
        }

        JsonArray entries = feed["entries"] as JsonArray
            ?? throw new ExtensionFeedMergeException("the existing feed has no 'entries' array.");

        List<JsonNode> kept = [];
        SemVersion? highest = null;
        int replaced = 0;
        foreach (JsonNode? node in entries)
        {
            JsonObject existing = node as JsonObject ?? throw new ExtensionFeedMergeException("an existing entry is not a JSON object.");
            SemVersion existingVersion = RequiredVersion(existing, "version", "an existing entry");
            if (highest is null || existingVersion > highest)
            {
                highest = existingVersion;
            }

            if (existingVersion == entryVersion)
            {
                replaced++;
                continue; // dropped; the new entry below takes this version's place
            }

            kept.Add(existing);
        }

        // Replacing a version already in the feed is never a downgrade, whatever its position; only a
        // version that matches nothing existing is judged against the current highest.
        if (replaced == 0 && highest is not null && entryVersion <= highest && !allowDowngrade)
        {
            throw new ExtensionFeedMergeException(
                $"version {entryVersion} is not newer than the current latest {highest}; pass --allow-downgrade to publish it anyway.");
        }

        entries.Clear();
        kept.Add(entry);
        kept.Sort(static (a, b) => Version(b).CompareTo(Version(a)));
        foreach (JsonNode node in kept)
        {
            entries.Add(node);
        }

        string merged = feed.ToJsonString(WriteOptions);
        try
        {
            ExtensionFeed.Parse(merged);
        }
        catch (ExtensionFeedException ex)
        {
            throw new ExtensionFeedMergeException($"the merged feed does not parse: {ex.Message}", ex);
        }

        return merged;
    }

    private static SemVersion Version(JsonNode node) => RequiredVersion((JsonObject)node, "version", "an entry");

    private static JsonObject ParseObject(string json, string subject)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ExtensionFeedMergeException($"{subject} is not valid JSON: {ex.Message}", ex);
        }

        return node as JsonObject ?? throw new ExtensionFeedMergeException($"{subject} must be a JSON object.");
    }

    private static string RequiredString(JsonObject obj, string member, string subject)
    {
        if (obj[member] is not JsonValue value || !value.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text))
        {
            throw new ExtensionFeedMergeException($"{subject} has no '{member}'.");
        }

        return text;
    }

    private static SemVersion RequiredVersion(JsonObject obj, string member, string subject)
    {
        string text = RequiredString(obj, member, subject);
        return SemVersion.TryParse(text, out SemVersion? version)
            ? version
            : throw new ExtensionFeedMergeException($"{subject} has a '{member}' that is not a semantic version: '{text}'.");
    }

    private static string RequiredManifestId(JsonObject entry)
    {
        if (entry["manifest"] is not JsonObject manifest)
        {
            throw new ExtensionFeedMergeException("the new entry has no 'manifest' object.");
        }

        return RequiredString(manifest, "id", "the new entry's manifest");
    }
}

/// <summary>A feed merge that was refused: the input did not parse, or the version was a refused downgrade.</summary>
public sealed class ExtensionFeedMergeException : Exception
{
    public ExtensionFeedMergeException()
    {
    }

    public ExtensionFeedMergeException(string message) : base(message)
    {
    }

    public ExtensionFeedMergeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
