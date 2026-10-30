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

    /// <summary>What a demo download may answer with. Measured: the instance sends octet-stream.</summary>
    private static readonly string[] _binaryBody = ["application/octet-stream"];

    /// <summary>What the source probe may answer with.</summary>
    private static readonly string[] _jsonBody = ["application/json"];

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
            // Not echoed: a relative URI cannot be decomposed, so there is no sanitized form to
            // report and the raw text may carry a credential.
            throw new DemoCorpusConfigurationException(
                $"a {DemoTestHelper.DemoSourcesEnvVar} entry is not an absolute URI. An HTTP entry "
                + "is https://host/SourceName, optionally with a directory inside it.");
        }

        // Everything below reports the SANITIZED form. A credential someone put in the URI would
        // otherwise reach a skip message through these very errors. Scheme, host, port and path
        // only: GetLeftPart keeps user info, for both UriPartial.Path and UriPartial.Authority.
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

        // A token read from a mounted secret file arrives with a trailing newline, which
        // AuthenticationHeaderValue rejects as a FormatException naming neither the variable nor
        // the source. Trim first, then refuse what is left if it still cannot be a header value.
        string trimmedToken = token.Trim();
        if (trimmedToken.Any(char.IsControl) || trimmedToken.Any(c => c == ' '))
        {
            throw new DemoCorpusConfigurationException(
                $"{DemoTestHelper.DemoSourcesTokenEnvVar} contains whitespace or control characters "
                + "inside the value, so it cannot be sent as a header. Check for a stray line break.");
        }

        _sourceName = Uri.UnescapeDataString(segments[0]);
        _prefix = segments.Length > 1
            ? "/" + string.Join('/', segments.Skip(1).Select(Uri.UnescapeDataString))
            : string.Empty;
        // Components, not GetLeftPart: that keeps user info, so the sanitization would depend on
        // the guard above still running first.
        _authority = new Uri(
            instance.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped),
            UriKind.Absolute);
        _transferTimeout = transferTimeout ?? TimeSpan.FromMinutes(30);
        _client = new HttpClient(handler ?? CreateDefaultHandler(), handler is null)
        {
            Timeout = _headerTimeout,
        };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", trimmedToken);
        _sourceProblem = new Lazy<string?>(SourceProblem, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    ///     The transport this source uses when no handler is injected.
    ///     <para>
    ///         Redirects are OFF. A 3xx from this API is always a misconfiguration, a login bounce
    ///         or a scheme bounce, and following one would decide for itself where the bearer
    ///         header goes. Public so a test can assert that, which injecting a handler cannot.
    ///     </para>
    /// </summary>
    public static HttpMessageHandler CreateDefaultHandler() =>
        new SocketsHttpHandler { AllowAutoRedirect = false };

    /// <inheritdoc />
    /// <remarks>
    ///     Host, source and prefix: the prefix is where this source actually looked, so a skip
    ///     message without it would not say enough to diagnose one. The token must never appear.
    /// </remarks>
    public string Description => $"{_authority} source '{_sourceName}{_prefix}'";

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

            Guard(response, path, _binaryBody);
            Download(response, destination, demo);
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
    ///         answers 403 and a mistyped prefix answers 500, so no status allowlist can tell the
    ///         two apart. 401 still routes to <see cref="Guard" />, which is the one case where
    ///         naming the token is the right advice.
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
            // "the source probe", not the source name: Description already carries it.
            Guard(response, "the source probe", _jsonBody);
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
            // One read, not ReadAtLeast: this is a diagnostic, and a body whose terminator never
            // arrives would otherwise block here with nothing bounding it.
            int read = body.Read(buffer, 0, buffer.Length);
            string text = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();
            return text.Length == 0 ? string.Empty : $", saying: {text}";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    ///     Turns anything that is not a usable response into a configuration failure. A 200 with an
    ///     HTML body is the instance's SPA fallback or a bot challenge, never demo bytes.
    /// </summary>
    private void Guard(HttpResponseMessage response, string what, string[] acceptable)
    {
        if (response.IsSuccessStatusCode)
        {
            string? media = response.Content.Headers.ContentType?.MediaType;
            // An allowlist, not a denylist. A gateway or SSO portal answering 200 with a JSON or
            // text error body is the same class of thing as the bot challenge this started with,
            // and denying only text/html would write that body out as a demo.
            if (media is not null && !acceptable.Contains(media, StringComparer.OrdinalIgnoreCase))
            {
                throw new DemoCorpusConfigurationException(
                    $"{Description} answered {what} with content type '{media}' rather than "
                    + $"{string.Join(" or ", acceptable)}. The request was served by something other "
                    + "than the API: a web UI, a bot challenge or a portal.");
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

    /// <summary>
    ///     Streams the body to <paramref name="destination" />, or throws
    ///     <see cref="DemoCorpusConfigurationException" />.
    ///     <para>
    ///         <b>Every transport failure is configuration, never integrity.</b> Returning true on a
    ///         truncated file would reach <c>Verify</c> and be reported as the manifest's bytes
    ///         being wrong, which blames the corpus for a dropped socket. Throwing the
    ///         configuration type instead keeps it a per-source failure that
    ///         <see cref="DemoCorpus" /> records and retries past, and that fails the run only when
    ///         no source served the demo.
    ///     </para>
    /// </summary>
    private void Download(HttpResponseMessage response, string destination, CorpusDemo demo)
    {
        long declared = response.Content.Headers.ContentLength ?? -1;
        long written = 0;
        try
        {
            using Stream body = response.Content.ReadAsStream();
            using FileStream file = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            // HttpClient.Timeout stops applying once the headers land under ResponseHeadersRead, so
            // the body needs its own bound. Checked between reads: a socket wedged inside one read
            // still waits on TCP.
            DateTime deadline = DateTime.UtcNow + _transferTimeout;
            byte[] buffer = new byte[1 << 20];
            int read;
            while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
            {
                file.Write(buffer, 0, read);
                written += read;
                if (DateTime.UtcNow > deadline)
                {
                    throw new DemoCorpusConfigurationException(
                        $"{Description} transfer exceeded {_transferTimeout} after {written} bytes.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new DemoCorpusConfigurationException(
                $"{Description} transfer failed after {written} bytes: {ex.GetType().Name}: {ex.Message}");
        }

        // A declared length that MATCHES but differs from the manifest is left alone: the instance
        // holds a different file under this name, which is integrity and Verify's to report.
        if (declared >= 0)
        {
            if (written != declared)
            {
                throw new DemoCorpusConfigurationException(
                    $"{Description} sent {written} bytes of a declared {declared}.");
            }

            if (written == 0)
            {
                throw new DemoCorpusConfigurationException(
                    $"{Description} sent an empty body with a 200. The API did not serve the demo.");
            }

            return;
        }

        // No Content-Length, so truncation cannot be told from a wrong file by the transport alone.
        // Checking the manifest here keeps a dropped chunked socket from reaching Verify and being
        // reported as the corpus being wrong.
        if (written != demo.Bytes)
        {
            throw new DemoCorpusConfigurationException(
                $"{Description} sent {written} bytes with no Content-Length, and the manifest says "
                + $"{demo.Bytes}. Truncation cannot be ruled out, so this is not reported as a "
                + "corpus mismatch.");
        }
    }
}
