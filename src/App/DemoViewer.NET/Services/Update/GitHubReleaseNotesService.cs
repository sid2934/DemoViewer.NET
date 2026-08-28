#region

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.Services.Update;

/// <summary>
///     <see cref="IReleaseNotesService" /> over the GitHub Releases API — the same repository
///     <c>VelopackUpdateService</c> updates from, so the notes shown always describe the exact
///     packages the updater installs. Unauthenticated on purpose (the repo is public), one small
///     JSON request per version, cached for the process lifetime.
/// </summary>
public sealed partial class GitHubReleaseNotesService : IReleaseNotesService
{
    // Must match VelopackUpdateService.RepoUrl's repo. Hardcoded for the same reason the
    // updater's URL is: notes are rendered into trusted UI surfaces, and a settable endpoint
    // would let ambient config point them somewhere attacker-controlled.
    private const string Repo = "sid2934/DemoViewer.NET";

    private static readonly HttpClient _http = CreateClient();

    private readonly ConcurrentDictionary<string, ReleaseNotes?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Process-wide instance. Notes are version-keyed and immutable once published, so one
    ///     shared cache serves every consumer (update notice, What's New).
    /// </summary>
    public static GitHubReleaseNotesService Shared { get; } = new();

    /// <inheritdoc />
    public async Task<ReleaseNotes?> GetForVersionAsync(string version, CancellationToken ct = default)
    {
        string? normalized = NormalizeVersion(version);
        if (normalized is null)
        {
            return null;
        }

        if (_cache.TryGetValue(normalized, out ReleaseNotes? cached))
        {
            return cached;
        }

        ReleaseNotes? fetched = await FetchAsync(normalized, ct).ConfigureAwait(false);
        // Cache failures too: a launch-time fetch that failed offline should not retry on every
        // re-open of the same window this run — the next launch gets a fresh chance.
        _cache[normalized] = fetched;
        return fetched;
    }

    /// <summary>
    ///     Reduces any version string the app encounters — Velopack's "0.6.0", NBGV's
    ///     "0.6.0-alpha+g1a2b3c4", a "v0.6.0" tag — to the bare x.y.z the release tags use.
    ///     Null when no x.y.z can be found (e.g. "(unknown)").
    /// </summary>
    public static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        Match m = VersionCore().Match(version);
        return m.Success ? m.Value : null;
    }

    private static async Task<ReleaseNotes?> FetchAsync(string version, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync($"https://api.github.com/repos/{Repo}/releases/tags/v{version}", ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null; // 404 (tag has no release), rate-limited, etc. — all degrade the same
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            JsonElement root = doc.RootElement;

            string body = root.TryGetProperty("body", out JsonElement bodyEl)
                ? bodyEl.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(body))
            {
                return null; // a bodyless release renders as an empty pane — treat as unavailable
            }

            string title = root.TryGetProperty("name", out JsonElement nameEl)
                ? nameEl.GetString() ?? $"v{version}"
                : $"v{version}";
            DateTimeOffset? published =
                root.TryGetProperty("published_at", out JsonElement pubEl)
                && pubEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(pubEl.GetString(), out DateTimeOffset parsed)
                    ? parsed
                    : null;
            string? htmlUrl = root.TryGetProperty("html_url", out JsonElement urlEl) ? urlEl.GetString() : null;

            return new ReleaseNotes(version, title, body, published, htmlUrl);
        }
        catch
        {
            // Offline, DNS, TLS, malformed JSON — notes are cosmetic, so every failure is "no notes".
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DemoViewer.NET", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionCore();
}
