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

        /// <summary>One entry per request, as "scheme parameter" or "none".</summary>
        public List<string> Authorizations { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            Authorizations.Add(request.Headers.Authorization is { } a
                ? $"{a.Scheme} {a.Parameter}"
                : "none");
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

    /// <summary>A one-demo manifest for <c>subject.dem</c> with no directory hint.</summary>
    private static CorpusManifest ManifestOf(byte[] bytes) =>
        CorpusManifest.Parse(
            $$"""
              { "demos": [ { "name": "subject", "file": "subject.dem", "bytes": {{bytes.Length}},
                             "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(bytes))}}",
                             "sets": ["s"] } ] }
              """);

    /// <summary>Constructs a source and discards it, so a ctor-rejection case reads as a call.</summary>
    private static FileBrowserDemoSource Build(string token) =>
        new FileBrowserDemoSource(_instance, token, new StubHandler(_ => Status(HttpStatusCode.NotFound)));

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
                // Without this the whole suite stays green with the Authorization line deleted,
                // while every request to a real instance 401s.
                await Assert.That(handler.Authorizations[0]).IsEqualTo("Bearer tok");
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
        using (Assert.Multiple())
        {
            await Assert.That(thrown.Message).Contains("text/html");
            await Assert.That(thrown.Message).Contains("application/octet-stream");
        }
    }

    [Test]
    public async Task AShortBody_IsATransportFailure_NotAnIntegrityOne()
    {
        byte[] body = [1, 2, 3];
        StubHandler handler = new(_ => Bytes(body, body.Length + 64));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);
        string dest = TempFile();
        try
        {
            // Returning true here would reach Verify and report the manifest's bytes as wrong,
            // which fails the whole run for what is only a dropped socket.
            DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
                () => source.TryCopyTo(Demo(body, null), dest));
            using (Assert.Multiple())
            {
                await Assert.That(thrown.Message).Contains("of a declared");
                await Assert.That(thrown).IsNotTypeOf<DemoCorpusIntegrityException>();
            }
        }
        finally
        {
            File.Delete(dest);
        }
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
        string cacheRoot = Path.Combine(Path.GetTempPath(), $"dv-fb-{Guid.NewGuid():N}");
        DemoCorpus corpus = new(manifest, [], cacheRoot, [source]);
        try
        {
            Assert.Throws<DemoCorpusConfigurationException>(() => corpus.TryResolve("subject.dem", out _));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, true);
            }
        }
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
        // items probe for that prefix answers 500. No status allowlist can tell those apart.
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
            await Assert.That(userInfo.Message).Contains(DemoTestHelper.DemoSourcesTokenEnvVar);
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

    [Test]
    public async Task TheDefaultTransport_DoesNotFollowRedirects()
    {
        // Every other test injects a handler, so the production transport settings are not
        // otherwise executed by this suite at all.
        using HttpMessageHandler handler = FileBrowserDemoSource.CreateDefaultHandler();
        await Assert.That(((SocketsHttpHandler)handler).AllowAutoRedirect).IsFalse();
    }

    [Test]
    public async Task TheSourceProbe_RunsOncePerSource_NotOncePerDemo()
    {
        StubHandler handler = new(request =>
            request.RequestUri!.AbsolutePath.Contains("/items", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"folders\":[]}", Encoding.UTF8, "application/json"),
                }
                : Status(HttpStatusCode.NotFound));
        using FileBrowserDemoSource source = new(_instance, "tok", handler);

        using (Assert.Multiple())
        {
            await Assert.That(source.TryCopyTo(Demo([1], null), TempFile())).IsFalse();
            await Assert.That(handler.Requests.Count).IsEqualTo(2);
            // One more probe, not one more probe plus another source check.
            await Assert.That(source.TryCopyTo(Demo([2], null), TempFile())).IsFalse();
            await Assert.That(handler.Requests.Count).IsEqualTo(3);
        }
    }

    [Test]
    public async Task ATransferThatOutlastsItsDeadline_IsAConfigurationFailure()
    {
        byte[] body = new byte[4 << 20];
        StubHandler handler = new(_ => Bytes(body));
        using FileBrowserDemoSource source = new(
            _instance, "tok", handler, TimeSpan.Zero);
        string dest = TempFile();
        try
        {
            DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
                () => source.TryCopyTo(Demo(body, null), dest));
            await Assert.That(thrown.Message).Contains("exceeded");
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Test]
    public async Task ABodyWithNoContentLength_IsCheckedAgainstTheManifest_NotLeftToVerify()
    {
        // A chunked response that dies early has no declared length to compare against. Left
        // unchecked it reaches Verify and the manifest gets blamed for a dropped socket.
        byte[] truncated = [1, 2, 3];
        StreamContent content = new(new MemoryStream(truncated));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = null;
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using FileBrowserDemoSource source = new(_instance, "tok", handler);
        string dest = TempFile();
        try
        {
            DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
                () => source.TryCopyTo(Demo([1, 2, 3, 4, 5, 6], null), dest));
            await Assert.That(thrown.Message).Contains("no Content-Length");
        }
        finally
        {
            File.Delete(dest);
        }
    }

    [Test]
    public async Task AJsonErrorBodyOnA200_IsRefused_RatherThanWrittenOutAsADemo()
    {
        // A gateway or portal answering 200 with a JSON error body is the same class of thing as a
        // bot challenge. Denying only text/html would write this out and blame the manifest.
        StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"status\":403,\"message\":\"forbidden\"}", Encoding.UTF8, "application/json"),
        });
        using FileBrowserDemoSource source = new(_instance, "tok", handler);

        DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
            () => source.TryCopyTo(Demo([1], null), TempFile()));
        await Assert.That(thrown.Message).Contains("application/json");
    }

    [Test]
    public async Task AnUnparsableEntry_IsNotEchoed_BecauseItMayCarryACredential()
    {
        // Each of these fails Uri.TryCreate, so it reaches the branch that has no sanitized form
        // to report. The message is cached by the static corpus and reprinted for every corpus
        // test in the run, so an echo here would land in a CI log.
        string[] withSecrets =
        [
            "https://user:sup3rs3cr3t@demos.example.net:notaport/Demos",
            "https://user:sup3rs3cr3t@dem os.example.net/Demos",
            "https://user:sup3rs3cr3t@demos.example.net:99999/Demos",
            "https://demos.example.net:99999/Demos?auth=sup3rs3cr3t",
        ];
        using (Assert.Multiple())
        {
            foreach (string entry in withSecrets)
            {
                DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
                    () => DemoSources.FromSpecifier(entry, "tok"));
                await Assert.That(thrown.Message).DoesNotContain("sup3rs3cr3t");
            }

            // The unsupported-scheme path is on the same dispatch and reports the scheme only.
            NotSupportedException scheme = Assert.Throws<NotSupportedException>(
                () => DemoSources.FromSpecifier("s3://bucket/demos?token=sup3rs3cr3t", "tok"));
            await Assert.That(scheme.Message).DoesNotContain("sup3rs3cr3t");
            await Assert.That(scheme.Message).Contains("s3");
        }
    }

    [Test]
    public async Task ATokenWithAStrayLineBreak_IsTrimmed_OrNamedIfItStillCannotBeAHeader()
    {
        using (Assert.Multiple())
        {
            // A secret mounted from a file arrives with a trailing newline.
            using FileBrowserDemoSource trimmed = new(
                _instance, "tok\n", new StubHandler(_ => Status(HttpStatusCode.NotFound)));
            await Assert.That(trimmed.Description).Contains("demos.example.net");

            DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
                () => Build("to k"));
            await Assert.That(thrown.Message).Contains(DemoTestHelper.DemoSourcesTokenEnvVar);
        }
    }

    [Test]
    public async Task AnUnusableFirstSource_DoesNotHideASecondThatHoldsIt()
    {
        // Sources are tried in order. Aborting on the first unusable one would mean a laptop off
        // VPN never asks the local share that holds every demo.
        byte[] body = [8, 8, 8];
        StubHandler dead = new(_ => Status(HttpStatusCode.Unauthorized));
        using FileBrowserDemoSource refusing = new(_instance, "tok", dead);
        string root = Path.Combine(Path.GetTempPath(), $"dv-fb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "subject.dem"), body);
            CorpusManifest manifest = ManifestOf(body);
            string cacheRoot = Path.Combine(root, "cache");
            DemoCorpus corpus = new(manifest, [], cacheRoot, [refusing, new DirectoryDemoSource(root)]);

            string? resolved = corpus.TryResolve("subject.dem", out string? problem);
            using (Assert.Multiple())
            {
                await Assert.That(resolved).IsNotNull();
                await Assert.That(problem).IsNull();
                await Assert.That(await File.ReadAllBytesAsync(resolved!)).IsEquivalentTo(body);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AnUnusableSource_StillFailsTheRun_WhenNoOtherSourceHeldIt()
    {
        // The other half of the rule: a dead credential must not end as a green skip once every
        // source has been asked.
        byte[] body = [9];
        StubHandler dead = new(_ => Status(HttpStatusCode.Unauthorized));
        using FileBrowserDemoSource refusing = new(_instance, "tok", dead);
        string root = Path.Combine(Path.GetTempPath(), $"dv-fb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            DemoCorpus corpus = new(
                ManifestOf(body), [], Path.Combine(root, "cache"),
                [refusing, new DirectoryDemoSource(root)]);

            DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
                () => corpus.TryResolve("subject.dem", out _));
            await Assert.That(thrown.Message).Contains(DemoTestHelper.DemoSourcesTokenEnvVar);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
