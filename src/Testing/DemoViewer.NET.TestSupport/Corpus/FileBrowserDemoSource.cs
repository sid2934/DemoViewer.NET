#region

using System.Net;
using System.Net.Http.Headers;

#endregion

namespace DemoViewer.NET.TestSupport.Corpus;

/// <summary>
///     A FileBrowser Quantum instance over HTTP, so a machine with no corpus and no mounted share
///     can still fetch the manifest's demos.
///     <para>
///         <b>Probed, never listed.</b> Two candidates per demo, the same pair
///         <see cref="DirectoryDemoSource" /> tries: <c>&lt;dir&gt;/&lt;file&gt;</c> then
///         <c>&lt;file&gt;</c>. The instance's search API answers out of an index carrying its own
///         scan clock, so a stale index would report a present demo as absent, which the
///         <see cref="IDemoSource" /> contract forbids. The demos must therefore sit on the
///         instance under the manifest's own names.
///     </para>
///     <para>
///         <b>Only 404 means "does not hold it".</b> Everything else throws
///         <see cref="DemoCorpusConfigurationException" />, which <see cref="DemoCorpus" />
///         rethrows. Left as an ordinary miss, a dead token makes every demo look absent and the
///         lane still reads green on skips.
///     </para>
/// </summary>
public sealed class FileBrowserDemoSource : IDemoSource, IDisposable
{
    /// <summary>Headers only. The body is bounded separately, see <see cref="_transferTimeout" />.</summary>
    private static readonly TimeSpan _headerTimeout = TimeSpan.FromMinutes(2);

    private readonly Uri _authority;
    private readonly string _sourceName;

    /// <summary>Leading <c>/</c>, no trailing one, or empty for the source root.</summary>
    private readonly string _prefix;
    private readonly HttpClient _client;
    private readonly TimeSpan _transferTimeout;

    /// <summary>
    ///     Why the configured source and prefix are unusable, or null when they are fine. Asked
    ///     once per process and only when every candidate 404s: a wrong source name or a mistyped
    ///     prefix answers 404 for every demo, which is indistinguishable from an absent one until
    ///     this is known, so the happy path never pays for it.
    /// </summary>
    private readonly Lazy<string?> _sourceProblem;

    /// <param name="instance">
    ///     The instance, the source it serves and optionally a directory inside it, as
    ///     <c>https://host/SourceName</c> or <c>https://host/SourceName/Sub/Dir</c>. The first
    ///     segment names a FileBrowser source; anything after it prefixes every probed path, which
    ///     is how a source shared with non-test demos is partitioned.
    /// </param>
    /// <param name="token">
    ///     The API token, sent as a bearer header. Never placed in a URI: <see cref="Description" />
    ///     is printed into skip messages verbatim, and a query string also reaches proxy logs.
    /// </param>
    /// <param name="handler">
    ///     Transport override for tests. When given, the caller owns its lifetime.
    /// </param>
    /// <param name="transferTimeout">Bound on one body transfer. Defaults to 30 minutes.</param>
    public FileBrowserDemoSource(
        Uri instance,
        string token,
        HttpMessageHandler? handler = null,
        TimeSpan? transferTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!instance.IsAbsoluteUri)
        {
            throw new DemoCorpusConfigurationException(
                $"demo source '{instance.OriginalString}' must be an absolute URI of the form "
                + "https://host/SourceName.");
        }

        // Everything below reports the SANITIZED form. A credential someone put in the URI would
        // otherwise reach a skip message through these very errors. Scheme, host, port and path
        // only: GetLeftPart(UriPartial.Path) keeps user info, which a test caught the hard way.
        string shown = instance.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        if (instance.UserInfo.Length > 0 || instance.Query.Length > 0)
        {
            throw new DemoCorpusConfigurationException(
                $"demo source '{shown}' carries a query string or user info. Neither is used, and a "
                + $"credential belongs in {DemoTestHelper.DemoSourcesTokenEnvVar}, not in a URI that "
                + "gets printed into skip messages.");
        }

        if (!instance.IsLoopback && !instance.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new DemoCorpusConfigurationException(
                $"demo source '{shown}' is not https. The token is sent as a request header, so a "
                + "cleartext scheme would put it on the wire. Only a loopback host may use http.");
        }

        string[] segments = instance.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new DemoCorpusConfigurationException(
                $"demo source '{shown}' must name the FileBrowser source in its first path "
                + "segment, as in https://host/SourceName or https://host/SourceName/SubDir.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new DemoCorpusConfigurationException(
                $"demo source '{shown}' needs an API token. Set {DemoTestHelper.DemoSourcesTokenEnvVar}.");
        }

        _sourceName = Uri.UnescapeDataString(segments[0]);
        _prefix = segments.Length > 1
            ? "/" + string.Join('/', segments.Skip(1).Select(Uri.UnescapeDataString))
            : string.Empty;
        _authority = new Uri(instance.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        _transferTimeout = transferTimeout ?? TimeSpan.FromMinutes(30);
        // Redirects off: a 3xx from this API is always a misconfiguration (a login bounce, a
        // scheme bounce), and following one decides for itself where the bearer header goes.
        _client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, handler is null)
        {
            Timeout = _headerTimeout,
        };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _sourceProblem = new Lazy<string?>(SourceProblem, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Host, source and prefix: the prefix is where this source actually looked, so a skip
    ///     message without it would not say enough to diagnose one. The token must never appear.
    /// </remarks>
    public string Description =>
        $"{_authority.GetLeftPart(UriPartial.Authority)} source '{_sourceName}{_prefix}'";

    /// <inheritdoc />
    public bool TryCopyTo(CorpusDemo demo, string destination)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        foreach (string path in Candidates(demo))
        {
            using HttpResponseMessage response = Send(DownloadUri(path));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            Guard(response, path);
            Download(response, destination);
            return true;
        }

        if (_sourceProblem.Value is { } problem)
        {
            throw new DemoCorpusConfigurationException(
                $"{Description} is not usable: {problem}. In the "
                + $"{DemoTestHelper.DemoSourcesEnvVar} entry the first path segment must be one of "
                + "the instance's source names and the rest a directory inside it, and paths are "
                + "case-sensitive.");
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>Server paths, always forward-slashed: these are URIs, not local paths.</summary>
    private IEnumerable<string> Candidates(CorpusDemo demo)
    {
        if (demo.Dir is not null)
        {
            yield return $"{_prefix}/{demo.Dir}/{demo.File}";
        }

        yield return $"{_prefix}/{demo.File}";
    }

    private Uri DownloadUri(string path) =>
        new(_authority,
            "/api/resources/download"
            + $"?source={Uri.EscapeDataString(_sourceName)}"
            + $"&file={Uri.EscapeDataString(path)}");

    private HttpResponseMessage Send(Uri uri)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        try
        {
            return _client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Reaching here means the cache missed, so the run genuinely needed the network.
            // Recording it as an ordinary miss would skip tests the configuration says to run.
            throw new DemoCorpusConfigurationException(
                $"{Description} is unreachable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Asks for the prefix directory, not just the source root: a mistyped prefix is as silent
    ///     as a mistyped source name, and both answer 404 for every demo.
    ///     <para>
    ///         Any status but 200 means unusable, and the instance's own message is carried back
    ///         rather than interpreted. Measured against the live instance: a wrong source name
    ///         answers 403 and a mistyped prefix answers 500, so a status allowlist would have
    ///         mapped both onto the wrong diagnosis. 401 still routes to <see cref="Guard" />,
    ///         which is the one case where naming the token is the right advice.
    ///     </para>
    /// </summary>
    private string? SourceProblem()
    {
        string probe = _prefix.Length == 0 ? "/" : _prefix;
        Uri uri = new(_authority,
            "/api/resources/items"
            + $"?path={Uri.EscapeDataString(probe)}"
            + $"&source={Uri.EscapeDataString(_sourceName)}"
            + "&only=folders");
        using HttpResponseMessage response = Send(uri);
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.IsSuccessStatusCode)
        {
            Guard(response, $"source '{_sourceName}{_prefix}'");
            return null;
        }

        return $"the instance answered {(int)response.StatusCode} for it{Detail(response)}";
    }

    /// <summary>The instance's own error text, bounded, or empty when there is none worth showing.</summary>
    private static string Detail(HttpResponseMessage response)
    {
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html",
                StringComparison.OrdinalIgnoreCase))
        {
            return " (an HTML page, so the request did not reach the API)";
        }

        try
        {
            using Stream body = response.Content.ReadAsStream();
            byte[] buffer = new byte[512];
            int read = body.ReadAtLeast(buffer, buffer.Length, false);
            string text = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();
            return text.Length == 0 ? string.Empty : $", saying: {text}";
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Turns anything that is not a usable response into a configuration failure. A 200 with an
    ///     HTML body is the instance's SPA fallback or a bot challenge, never demo bytes.
    /// </summary>
    private void Guard(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            string? media = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(media, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new DemoCorpusConfigurationException(
                    $"{Description} answered {what} with an HTML page, not demo bytes. The request was "
                    + "served by the web UI or intercepted by a bot challenge.");
            }

            return;
        }

        string hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $" Check {DemoTestHelper.DemoSourcesTokenEnvVar}: the token may be expired or revoked.",
            // The live instance answers 403 both for an unknown source NAME and for a token
            // without download access, so the hint has to name both.
            HttpStatusCode.Forbidden =>
                $" Either the source name in {DemoTestHelper.DemoSourcesEnvVar} is not one the "
                + $"instance serves, or {DemoTestHelper.DemoSourcesTokenEnvVar} lacks download "
                + "access to it.",
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest =>
                $" Redirects are not followed. Check the {DemoTestHelper.DemoSourcesEnvVar} host and scheme.",
            _ => string.Empty,
        };

        throw new DemoCorpusConfigurationException(
            $"{Description} answered {(int)response.StatusCode} for {what}.{hint}");
    }

    private void Download(HttpResponseMessage response, string destination)
    {
        long declared = response.Content.Headers.ContentLength ?? -1;
        using Stream body = response.Content.ReadAsStream();
        using FileStream file = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        // HttpClient.Timeout stops applying once the headers land under ResponseHeadersRead, so the
        // body needs its own bound. Checked between reads: a socket wedged inside one read still
        // waits on TCP.
        DateTime deadline = DateTime.UtcNow + _transferTimeout;
        byte[] buffer = new byte[1 << 20];
        long written = 0;
        int read;
        while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
        {
            file.Write(buffer, 0, read);
            written += read;
            if (DateTime.UtcNow > deadline)
            {
                throw new IOException(
                    $"{Description} transfer exceeded {_transferTimeout} after {written} bytes.");
            }
        }

        // Not an integrity problem: a truncated transfer returned true would reach Verify and be
        // reported as the manifest's bytes being wrong, which fails the run for a dropped socket.
        if (declared >= 0 && written != declared)
        {
            throw new IOException(
                $"{Description} sent {written} bytes of a declared {declared}.");
        }
    }
}
