#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Feed JSON for the update tests: one <c>extensions.json</c> built from entry descriptions, each entry's
///     manifest in the shape the release workflow publishes, with the sha and size of whatever bytes the test serves.
/// </summary>
internal static class FakeFeeds
{
    public const string Id = "net.demoviewer.pack.fake";

    /// <summary>One feed entry's inputs.</summary>
    /// <param name="Version">The version, also the manifest's.</param>
    /// <param name="Bytes">The zip the test serves for it, or null for a placeholder sha and size.</param>
    /// <param name="RequiresHost">The manifest's host range.</param>
    /// <param name="RequiresCs2DemoKit">The manifest's CS2DemoKit range.</param>
    /// <param name="MinAppVersion">The manifest's app floor, or null.</param>
    /// <param name="Sha256">Overrides the hash of <paramref name="Bytes" /> when set (a lying feed).</param>
    /// <param name="Size">Overrides the length of <paramref name="Bytes" /> when set.</param>
    public sealed record Entry(
        string Version,
        byte[]? Bytes = null,
        string RequiresHost = "^2.0",
        string RequiresCs2DemoKit = "*",
        string? MinAppVersion = null,
        string? Sha256 = null,
        long? Size = null)
    {
        public Uri Url => new($"https://example.invalid/releases/{Id}-v{Version}/Fake-{Version}.zip");
    }

    /// <summary>The feed JSON for <paramref name="entries" />, in the order given.</summary>
    public static string Json(string id, params Entry[] entries)
    {
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"{{ \"id\": \"{id}\", \"entries\": [");
        for (int i = 0; i < entries.Length; i++)
        {
            Entry e = entries[i];
            string sha = e.Sha256 ?? (e.Bytes is null ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(e.Bytes)));
            long size = e.Size ?? e.Bytes?.LongLength ?? 1;
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(CultureInfo.InvariantCulture, $$"""
                {
                  "version": "{{e.Version}}",
                  "manifest": {{Manifest(id, e)}},
                  "url": "{{e.Url}}",
                  "sha256": "{{sha}}",
                  "size": {{size}},
                  "publishedAt": "2026-10-03T12:00:00Z"
                }
                """);
        }

        sb.Append("] }");
        return sb.ToString();
    }

    /// <summary>The feed JSON for <see cref="Id" />.</summary>
    public static string Json(params Entry[] entries) => Json(Id, entries);

    /// <summary>The manifest JSON an entry carries, and the one its zip must contain.</summary>
    public static string Manifest(string id, Entry e) =>
        $$"""
          {
            "id": "{{id}}",
            "name": "Fake",
            "version": "{{e.Version}}",
            "assembly": "Fake.dll",
            "entryType": "Fake.Pack",
            "requiresHost": "{{e.RequiresHost}}",
            "requiresCs2DemoKit": "{{e.RequiresCs2DemoKit}}"{{(e.MinAppVersion is null ? "" : $",\n  \"minAppVersion\": \"{e.MinAppVersion}\"")}}
          }
          """;
}
