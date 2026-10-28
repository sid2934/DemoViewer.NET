#region

using System.Net;
using System.Security.Cryptography;
using System.Text;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.TestSupport.Corpus;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The HTTP demo source's contract, driven through a stub transport. No network and no
///     environment variable: the point of every case here is which outcome a given response
///     produces, and the one that matters is that a credential or configuration problem FAILS
///     rather than leaving every demo looking absent behind a green lane.
///     <para>
///         Deliberately NOT tagged <c>RealDemo</c>. Like <see cref="DemoCorpusTests" /> this is the
///         layer's own contract and must run in every tier.
///     </para>
/// </summary>
public class FileBrowserDemoSourceTests
{
    private static readonly Uri _instance = new("https://demos.example.net/Demos");

    /// <summary>Routes by the request path plus query, so a case can answer each candidate differently.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return respond(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private static HttpResponseMessage Bytes(byte[] body, long? declaredLength = null)
    {
        StreamContent content = new(new MemoryStream(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = declaredLength ?? body.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpResponseMessage Html() =>
        new(HttpStatusCode.OK) { Content = new StringContent("<html><body>app</body></html>", Encoding.UTF8, "text/html") };

    private static CorpusDemo Demo(byte[] bytes, string? dir) =>
        CorpusManifest.Parse(
            $$"""
              { "demos": [ { "name": "subject", "file": "subject.dem", "bytes": {{bytes.Length}},
                             "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(bytes))}}",
                             "dir": {{(dir is null ? "null" : $"\"{dir}\"")}}, "sets": ["s"] } ] }
              """).ByName("subject")!;

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"dv-fb-{Guid.NewGuid():N}.dem");

    // ── Download ──────────────────────────────────────────────────────────────

    [Test]
    public async Task ADemoTheInstanceHolds_IsCopied_FromTheFirstCandidate()
    {
        byte[] body = [1, 2, 3, 4, 5];
        StubHandler handler = new(_ => Bytes(body));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);
        string dest = TempFile();
        try
        {
            using (Assert.Multiple())
            {
                await Assert.That(source.TryCopyTo(Demo(body, "benchmarks"), dest)).IsTrue();
                await Assert.That(File.ReadAllBytes(dest)).IsEquivalentTo(body);
                // The dir candidate is tried first and hit, so there is exactly one round trip.
                await Assert.That(handler.Requests.Count).IsEqualTo(1);
                await Assert.That(handler.Requests[0]).Contains("source=Demos");
                await Assert.That(handler.Requests[0]).Contains(Uri.EscapeDataString("/benchmarks/subject.dem"));
            }
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Test]
    public async Task AFirstCandidateThatIs404_FallsThroughToTheBareFileName()
    {
        byte[] body = [9, 9];
        StubHandler handler = new(request =>
            request.RequestUri!.Query.Contains(Uri.EscapeDataString("/pro-demos/"), StringComparison.Ordinal)
                ? Status(HttpStatusCode.NotFound)
                : Bytes(body));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);
        string dest = TempFile();
        try
        {
            using (Assert.Multiple())
            {
                await Assert.That(source.TryCopyTo(Demo(body, "pro-demos"), dest)).IsTrue();
                await Assert.That(File.ReadAllBytes(dest)).IsEquivalentTo(body);
                await Assert.That(handler.Requests.Count).IsEqualTo(2);
            }
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Test]
    public async Task ADemoTheInstanceLacks_IsNotHoldingIt_OnceTheSourceIsKnownToExist()
    {
        byte[] body = [7];
        StubHandler handler = new(request =>
            request.RequestUri!.AbsolutePath.Contains("/items", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"folders":[]}""", Encoding.UTF8, "application/json"),
                }
                : Status(HttpStatusCode.NotFound));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);

        using (Assert.Multiple())
        {
            await Assert.That(source.TryCopyTo(Demo(body, null), TempFile())).IsFalse();
            // One probe plus the one-time source check.
            await Assert.That(handler.Requests.Count).IsEqualTo(2);
        }
    }

    // ── Failures that must not read as an absent demo ─────────────────────────

    [Test]
    public async Task AnUnusableSourceOrPrefix_Fails_RatherThanMakingEveryDemoLookAbsent()
    {
        // Everything 404s, the items probe included, so the source or prefix is not there at all.
        StubHandler handler = new(_ => Status(HttpStatusCode.NotFound));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);

        DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
            () => source.TryCopyTo(Demo([1], null), TempFile()));
        using (Assert.Multiple())
        {
            await Assert.That(thrown.Message).Contains("is not usable");
            await Assert.That(thrown.Message).Contains("answered 404");
        }
    }

    [Test]
    public async Task ARefusedCredential_Fails_AndNamesTheTokenVariable()
    {
        StubHandler handler = new(_ => Status(HttpStatusCode.Unauthorized));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);

        DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
            () => source.TryCopyTo(Demo([1], null), TempFile()));
        await Assert.That(thrown.Message).Contains(DemoTestHelper.DemoSourcesTokenEnvVar);
    }

    [Test]
    public void AForbiddenResponse_Fails_LikeARefusedCredential()
    {
        StubHandler handler = new(_ => Status(HttpStatusCode.Forbidden));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);

        Assert.Throws<DemoCorpusConfigurationException>(
            () => source.TryCopyTo(Demo([1], null), TempFile()));
    }

    [Test]
    public async Task AnHtmlBody_Fails_RatherThanBeingWrittenOutAsADemo()
    {
        StubHandler handler = new(_ => Html());
        using FileBrowserDemoSource source = new(_instance, "tok", handler);

        DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
            () => source.TryCopyTo(Demo([1], null), TempFile()));
        await Assert.That(thrown.Message).Contains("HTML page");
    }

    [Test]
    public void AShortBody_IsATransportFailure_NotAnIntegrityOne()
    {
        byte[] body = [1, 2, 3];
        StubHandler handler = new(_ => Bytes(body, body.Length + 64));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);

        // Returning true here would reach Verify and report the manifest's bytes as wrong, which
        // fails the whole run for what is only a dropped socket.
        Assert.Throws<IOException>(() => source.TryCopyTo(Demo(body, null), TempFile()));
    }

    // ── The layer above must not swallow it ───────────────────────────────────

    [Test]
    public void AConfigurationFailure_EscapesTryResolve_RatherThanBecomingASkip()
    {
        byte[] body = [4, 4];
        StubHandler handler = new(_ => Status(HttpStatusCode.Unauthorized));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);
        CorpusManifest manifest = CorpusManifest.Parse(
            $$"""
              { "demos": [ { "name": "subject", "file": "subject.dem", "bytes": {{body.Length}},
                             "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(body))}}",
                             "sets": ["s"] } ] }
              """);
        DemoCorpus corpus = new(manifest, [], Path.Combine(Path.GetTempPath(), $"dv-fb-{Guid.NewGuid():N}"), [source]);

        Assert.Throws<DemoCorpusConfigurationException>(() => corpus.TryResolve("subject.dem", out _));
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    [Test]
    public void AnHttpEntry_WithNoToken_FailsAtConstruction()
    {
        using (Assert.Multiple())
        {
            Assert.Throws<DemoCorpusConfigurationException>(
                () => DemoSources.FromSpecifier("https://demos.example.net/Demos", null));
            Assert.Throws<DemoCorpusConfigurationException>(
                () => DemoSources.FromSpecifier("https://demos.example.net/Demos", "   "));
        }
    }

    [Test]
    public async Task AnInstanceUri_MustNameASource()
    {
        using (Assert.Multiple())
        {
            Assert.Throws<DemoCorpusConfigurationException>(
                () => DemoSources.FromSpecifier("https://demos.example.net", "tok"));
            // Segments after the source name are a path prefix, not an error.
            await Assert.That(DemoSources.FromSpecifier("https://demos.example.net/Demos/TestingDemos", "tok"))
                .IsTypeOf<FileBrowserDemoSource>();
        }
    }

    [Test]
    public async Task APathPrefix_IsPrependedToEveryCandidate_AndShowsInTheDescription()
    {
        byte[] body = [2, 2];
        StubHandler handler = new(request =>
            request.RequestUri!.Query.Contains(Uri.EscapeDataString("/TestingDemos/benchmarks/subject.dem"), StringComparison.Ordinal)
                ? Bytes(body)
                : Status(HttpStatusCode.NotFound));
        using FileBrowserDemoSource source = new(
            new Uri("https://demos.example.net/Demos/TestingDemos"), "tok", handler);
        string dest = TempFile();
        try
        {
            using (Assert.Multiple())
            {
                await Assert.That(source.TryCopyTo(Demo(body, "benchmarks"), dest)).IsTrue();
                await Assert.That(handler.Requests.Count).IsEqualTo(1);
                // The prefix is where this source looked, so a skip message has to carry it.
                await Assert.That(source.Description).Contains("Demos/TestingDemos");
            }
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Test]
    public async Task APathPrefix_AlsoCoversTheBareFileNameCandidate()
    {
        byte[] body = [5];
        StubHandler handler = new(request =>
            request.RequestUri!.Query.Contains(Uri.EscapeDataString("/TestingDemos/subject.dem"), StringComparison.Ordinal)
                ? Bytes(body)
                : Status(HttpStatusCode.NotFound));
        using FileBrowserDemoSource source = new(
            new Uri("https://demos.example.net/Demos/TestingDemos"), "tok", handler);
        string dest = TempFile();
        try
        {
            await Assert.That(source.TryCopyTo(Demo(body, null), dest)).IsTrue();
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Test]
    public async Task AMistypedPrefix_Fails_AndCarriesTheInstancesOwnMessage()
    {
        // Measured against the live instance: a download under a bad prefix answers 404, and the
        // items probe for that prefix answers 500. Both are reproduced here, because a status
        // allowlist is exactly what got this wrong the first time.
        StubHandler handler = new(request =>
            request.RequestUri!.AbsolutePath.Contains("/items", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        "{\"status\":500,\"message\":\"lstat /demos/TestingDemo: no such file or directory\"}",
                        Encoding.UTF8, "application/json"),
                }
                : Status(HttpStatusCode.NotFound));
        using FileBrowserDemoSource source = new(
            new Uri("https://demos.example.net/Demos/TestingDemo"), "tok", handler);

        DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
            () => source.TryCopyTo(Demo([1], null), TempFile()));
        using (Assert.Multiple())
        {
            await Assert.That(thrown.Message).Contains("is not usable");
            await Assert.That(thrown.Message).Contains("answered 500");
            // The instance's own text, not an interpretation of the status code.
            await Assert.That(thrown.Message).Contains("no such file or directory");
            await Assert.That(thrown.Message).Contains("case-sensitive");
            await Assert.That(handler.Requests.Any(r => r.Contains("/api/resources/items", StringComparison.Ordinal)
                && r.Contains(Uri.EscapeDataString("/TestingDemo"), StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    public async Task AWrongSourceName_Fails_NamingBothTheSourceAndTheToken()
    {
        // The live instance answers 403 "source name not found" on the download endpoint, so this
        // never reaches the items probe. Blaming only the token would send the reader the wrong way.
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "{\"status\":403,\"message\":\"source name not found: Nope\"}",
                Encoding.UTF8, "application/json"),
        });
        using FileBrowserDemoSource source = new(
            new Uri("https://demos.example.net/Nope/TestingDemos"), "tok", handler);

        DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
            () => source.TryCopyTo(Demo([1], null), TempFile()));
        using (Assert.Multiple())
        {
            await Assert.That(thrown.Message).Contains(DemoTestHelper.DemoSourcesEnvVar);
            await Assert.That(thrown.Message).Contains(DemoTestHelper.DemoSourcesTokenEnvVar);
        }
    }
    [Test]
    public async Task ACredentialInTheUri_IsRefused_AndNeverEchoedBack()
    {
        // ?auth= works on the instance, so someone will try it. The description and every
        // construction error are printed into skip messages, so neither may carry a secret.
        using (Assert.Multiple())
        {
            DemoCorpusConfigurationException query = Assert.Throws<DemoCorpusConfigurationException>(
                () => DemoSources.FromSpecifier("https://demos.example.net/Demos?auth=sup3rs3cr3t", "tok"));
            await Assert.That(query.Message).DoesNotContain("sup3rs3cr3t");
            await Assert.That(query.Message).Contains(DemoTestHelper.DemoSourcesTokenEnvVar);

            DemoCorpusConfigurationException userInfo = Assert.Throws<DemoCorpusConfigurationException>(
                () => DemoSources.FromSpecifier("https://user:sup3rs3cr3t@demos.example.net/Demos", "tok"));
            await Assert.That(userInfo.Message).DoesNotContain("sup3rs3cr3t");
        }
    }

    [Test]
    public async Task ACleartextInstance_IsRefused_BecauseTheTokenIsAHeader()
    {
        using (Assert.Multiple())
        {
            DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
                () => DemoSources.FromSpecifier("http://demos.example.net/Demos", "tok"));
            await Assert.That(thrown.Message).Contains("not https");
            // Loopback is exempt so a local instance stays testable.
            await Assert.That(DemoSources.FromSpecifier("http://localhost:8080/Demos", "tok"))
                .IsTypeOf<FileBrowserDemoSource>();
        }
    }

    [Test]
    public async Task ARedirect_Fails_RatherThanCarryingTheTokenElsewhere()
    {
        StubHandler handler = new(_ => Status(HttpStatusCode.Found));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);

        DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
            () => source.TryCopyTo(Demo([1], null), TempFile()));
        await Assert.That(thrown.Message).Contains("Redirects are not followed");
    }

    [Test]
    public async Task TheSpecifierDispatch_SendsHttpOneWay_AndEverythingElseTheOther()
    {
        using (Assert.Multiple())
        {
            await Assert.That(DemoSources.FromSpecifier("https://demos.example.net/Demos", "tok"))
                .IsTypeOf<FileBrowserDemoSource>();
            await Assert.That(DemoSources.FromSpecifier(Path.GetTempPath(), null))
                .IsTypeOf<DirectoryDemoSource>();
            await Assert.That(DemoSources.FromSpecifier(new Uri(Path.GetTempPath()).AbsoluteUri, null))
                .IsTypeOf<DirectoryDemoSource>();
            // Still the directory source's rule, unchanged by the new dispatch.
            Assert.Throws<NotSupportedException>(() => DemoSources.FromSpecifier("s3://bucket/demos", "tok"));
        }
    }

    [Test]
    public async Task TheDescription_CarriesTheHostAndSource_AndNeverTheToken()
    {
        using FileBrowserDemoSource source = new(_instance, "super-secret-token", new StubHandler(_ => Status(HttpStatusCode.NotFound)));
        using (Assert.Multiple())
        {
            await Assert.That(source.Description).Contains("demos.example.net");
            await Assert.That(source.Description).Contains("Demos");
            await Assert.That(source.Description).DoesNotContain("super-secret-token");
        }
    }
}
