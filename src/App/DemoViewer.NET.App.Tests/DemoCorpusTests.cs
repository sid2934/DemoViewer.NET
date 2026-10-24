#region

using System.Security.Cryptography;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.TestSupport.Corpus;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The demo fetch layer, built on temp directories with every input passed in. No real demo is
///     read and no environment variable is set: every RealDemo test in this process reads the real
///     <c>DEMO_PATH</c> and <c>DEMO_CORPUS</c>, and a test that mutated either would race them.
///     The files here are small and only their bytes and names matter.
///     <para>
///         Deliberately NOT tagged <c>RealDemo</c>. These tests are the layer's own contract and
///         must run in every tier, including CI, where the corpus has never existed.
///     </para>
/// </summary>
public class DemoCorpusTests
{
    private const string ManifestJson = """
                                        {
                                          "demos": [
                                            { "name": "alpha", "file": "alpha.dem", "bytes": 8,
                                              "sha256": "a9e3b6d6ad6eae34eb7e4bbbe2c6b4d4b58c6e32e4f8c1b7b06a9e51a4b22e3f",
                                              "dir": "benchmarks", "sets": ["bench"] },
                                            { "name": "beta", "file": "beta.dem", "bytes": 4, "sets": ["bench"] }
                                          ]
                                        }
                                        """;

    /// <summary>Ordinal order, so "B" sorts before "a". A field because CA1861 forbids the literal.</summary>
    private static readonly string[] OrdinalPair = ["B.dem", "a.dem"];

    private static readonly string[] OrdinalTriple = ["B.dem", "a.dem", "c.dem"];
    private static readonly string[] AThenB = ["a.dem", "b.dem"];

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"dv-corpus-{Guid.NewGuid():N}");

    private static string Write(string dir, string name, byte[] bytes)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A manifest whose one entry's hash is the real hash of <paramref name="bytes" />.</summary>
    private static CorpusManifest ManifestFor(string file, byte[] bytes, string? dir) =>
        CorpusManifest.Parse(
            $$"""
              { "demos": [ { "name": "subject", "file": "{{file}}", "bytes": {{bytes.Length}},
                             "sha256": "{{Sha256Of(bytes)}}",
                             "dir": {{(dir is null ? "null" : $"\"{dir}\"")}}, "sets": ["s"] } ] }
              """);

    // ── Manifest ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Manifest_Parses_WithOptionalFieldsAbsent()
    {
        CorpusManifest manifest = CorpusManifest.Parse(ManifestJson);
        using (Assert.Multiple())
        {
            await Assert.That(manifest.Demos.Count).IsEqualTo(2);
            await Assert.That(manifest.ByFile("alpha.dem")!.Dir).IsEqualTo("benchmarks");
            await Assert.That(manifest.ByName("beta")!.Sha256).IsNull();
            await Assert.That(manifest.ByName("beta")!.Dir).IsNull();
            await Assert.That(manifest.Set("bench").Count).IsEqualTo(2);
            await Assert.That(manifest.Set("nope")).IsEmpty();
        }
    }

    [Test]
    public async Task Manifest_LooksUpByFileName_IgnoringAnyDirectory()
    {
        CorpusManifest manifest = CorpusManifest.Parse(ManifestJson);
        await Assert.That(manifest.ByFile("/anywhere/else/alpha.dem")!.Name).IsEqualTo("alpha");
    }

    /// <summary>
    ///     A malformed manifest is a defect in the repository. It must throw rather than resolve
    ///     nothing, because resolving nothing reads as "no demo available" and skips tests green.
    /// </summary>
    [Test]
    public async Task Manifest_Rejects_WhatItCannotUse()
    {
        using (Assert.Multiple())
        {
            Assert.Throws<InvalidDataException>(() => CorpusManifest.Parse("{ not json"));
            Assert.Throws<InvalidDataException>(() => CorpusManifest.Parse("{}"));
            Assert.Throws<InvalidDataException>(
                () => CorpusManifest.Parse("""{ "demos": [ { "file": "a.dem", "bytes": 1 } ] }"""));
            Assert.Throws<InvalidDataException>(
                () => CorpusManifest.Parse("""{ "demos": [ { "name": "a", "file": "a.dem" } ] }"""));
            Assert.Throws<InvalidDataException>(() => CorpusManifest.Parse(
                """{ "demos": [ { "name": "a", "file": "a.dem", "bytes": 1, "sha256": "xyz" } ] }"""));
            Assert.Throws<InvalidDataException>(() => CorpusManifest.Parse(
                """{ "demos": [ { "name": "a", "file": "a.dem", "bytes": 1 }, { "name": "a", "file": "b.dem", "bytes": 1 } ] }"""));
            await Task.CompletedTask;
        }
    }

    /// <summary>The committed manifest must parse, and every entry must name a plain file.</summary>
    [Test]
    public async Task Manifest_Embedded_ParsesAndNamesBareFiles()
    {
        CorpusManifest manifest = CorpusManifest.Embedded;
        using (Assert.Multiple())
        {
            await Assert.That(manifest.Demos).IsNotEmpty();
            await Assert.That(manifest.ByFile(DemoTestHelper.ReferenceDemoFileName)).IsNotNull();
            foreach (CorpusDemo demo in manifest.Demos)
            {
                await Assert.That(Path.GetFileName(demo.File)).IsEqualTo(demo.File);
                await Assert.That(demo.Sha256).IsNotNull();
            }
        }
    }

    /// <summary>
    ///     <c>demos/CORPUS.md</c> records that staging this demo re-points
    ///     <c>Nuke_TwoFloors_MatchesGolden</c> off the committed tour sample. A manifest entry
    ///     would make a configured fetch stage it, so it must have none.
    /// </summary>
    [Test]
    public async Task Manifest_Excludes_TheDemoThatRepointsTheNukeGolden()
    {
        await Assert.That(CorpusManifest.Embedded.ByFile("match730_003826256877184877003_0981591541_410.dem"))
            .IsNull();
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    [Test]
    public async Task ExistingLocation_Wins_AndNoSourceIsTouched()
    {
        string root = TempRoot();
        try
        {
            byte[] bytes = [1, 2, 3, 4];
            string local = Write(Path.Combine(root, "demos"), "subject.dem", bytes);
            DemoCorpus corpus = new(ManifestFor("subject.dem", bytes, null),
                [Path.Combine(root, "demos")], Path.Combine(root, "cache"), [new ThrowingSource()]);

            await Assert.That(corpus.TryResolve("subject.dem", out string? problem)).IsEqualTo(local);
            await Assert.That(problem).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Fetch_VerifiesTheHash_AndLandsInTheContentAddressedCache()
    {
        string root = TempRoot();
        try
        {
            byte[] bytes = [9, 8, 7, 6, 5];
            Write(Path.Combine(root, "source", "benchmarks"), "subject.dem", bytes);
            CorpusManifest manifest = ManifestFor("subject.dem", bytes, "benchmarks");
            string cache = Path.Combine(root, "cache");
            DemoCorpus corpus = new(manifest, [Path.Combine(root, "demos")], cache,
                [new DirectoryDemoSource(Path.Combine(root, "source"))]);

            string? resolved = corpus.TryResolve("subject.dem", out string? problem);
            using (Assert.Multiple())
            {
                await Assert.That(problem).IsNull();
                await Assert.That(resolved).IsEqualTo(corpus.CachePath(manifest.ByName("subject")!));
                await Assert.That(await File.ReadAllBytesAsync(resolved!)).IsEquivalentTo(bytes, CollectionOrdering.Matching);
                // Content addressed: the sha is the directory, the original file name is kept.
                await Assert.That(Path.GetFileName(Path.GetDirectoryName(resolved)!))
                    .IsEqualTo(Sha256Of(bytes));
                await Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(resolved)!, "*.partial"))
                    .IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A warm cache must not reach a source at all. Proved by deleting the source directory
    ///     after the first fetch: a second resolve that touched it would fail.
    /// </summary>
    [Test]
    public async Task CacheHit_ReachesNoSource()
    {
        string root = TempRoot();
        try
        {
            byte[] bytes = [4, 4, 4, 4, 4, 4];
            string source = Path.Combine(root, "source");
            Write(source, "subject.dem", bytes);
            CorpusManifest manifest = ManifestFor("subject.dem", bytes, null);
            string cache = Path.Combine(root, "cache");

            string? first = new DemoCorpus(manifest, [], cache, [new DirectoryDemoSource(source)])
                .TryResolve("subject.dem", out _);
            await Assert.That(first).IsNotNull();

            Directory.Delete(source, true);

            // A fresh instance, so the in-process memo cannot be what answers.
            DemoCorpus warm = new(manifest, [], cache, [new ThrowingSource()]);
            await Assert.That(warm.TryResolve("subject.dem", out string? problem)).IsEqualTo(first);
            await Assert.That(problem).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Same length, different bytes. A size check cannot catch this, so the hash comparison is
    ///     what runs, and a mismatch must FAIL rather than skip: a wrong demo invalidates every
    ///     golden captured against the right one.
    /// </summary>
    [Test]
    public async Task HashMismatch_Fails_AndNamesBothHashes()
    {
        string root = TempRoot();
        try
        {
            byte[] expected = [1, 1, 1, 1, 1, 1, 1, 1];
            byte[] wrong = [2, 2, 2, 2, 2, 2, 2, 2];
            await Assert.That(wrong.Length).IsEqualTo(expected.Length);

            Write(Path.Combine(root, "source"), "subject.dem", wrong);
            DemoCorpus corpus = new(ManifestFor("subject.dem", expected, null), [],
                Path.Combine(root, "cache"), [new DirectoryDemoSource(Path.Combine(root, "source"))]);

            DemoCorpusIntegrityException thrown = Assert.Throws<DemoCorpusIntegrityException>(
                () => corpus.TryResolve("subject.dem", out _))!;

            using (Assert.Multiple())
            {
                // A mismatch must not be reportable as a skip, whatever the call site catches.
                await Assert.That(typeof(SkipTestException)
                    .IsAssignableFrom(typeof(DemoCorpusIntegrityException))).IsFalse();
                await Assert.That(thrown.Message).Contains(Sha256Of(expected));
                await Assert.That(thrown.Message).Contains(Sha256Of(wrong));
                // The rejected bytes are never left where discovery could find them.
                await Assert.That(File.Exists(corpus.CachePath(corpus.Manifest.ByName("subject")!))).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task WrongLength_Fails_BeforeHashing()
    {
        string root = TempRoot();
        try
        {
            byte[] expected = [1, 2, 3, 4, 5, 6, 7, 8];
            Write(Path.Combine(root, "source"), "subject.dem", [1, 2, 3]);
            DemoCorpus corpus = new(ManifestFor("subject.dem", expected, null), [],
                Path.Combine(root, "cache"), [new DirectoryDemoSource(Path.Combine(root, "source"))]);

            DemoCorpusIntegrityException thrown = Assert.Throws<DemoCorpusIntegrityException>(
                () => corpus.TryResolve("subject.dem", out _))!;
            await Assert.That(thrown.Message).Contains("manifest says 8");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── Missing source: a skip, never a pass ──────────────────────────────────

    /// <summary>
    ///     The value of the whole layer. A demo that cannot be obtained must report
    ///     <c>skipped:</c>, and the throw is asserted rather than left to propagate: an uncaught
    ///     <see cref="SkipTestException" /> would skip THIS test, which proves nothing.
    /// </summary>
    [Test]
    public async Task MissingFromEverySource_Skips_AndNamesEverySourceTried()
    {
        string root = TempRoot();
        try
        {
            byte[] bytes = [1, 2, 3];
            string a = Path.Combine(root, "sourceA");
            string b = Path.Combine(root, "sourceB");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            DemoCorpus corpus = new(ManifestFor("subject.dem", bytes, null), [],
                Path.Combine(root, "cache"), [new DirectoryDemoSource(a), new DirectoryDemoSource(b)]);

            SkipTestException thrown = Assert.Throws<SkipTestException>(
                () => corpus.Require("subject.dem"))!;
            using (Assert.Multiple())
            {
                await Assert.That(thrown.Message).Contains("subject");
                await Assert.That(thrown.Message).Contains(a);
                await Assert.That(thrown.Message).Contains(b);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task NoSourceConfigured_Skips_AndSaysHowToConfigureOne()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            DemoCorpus corpus = new(ManifestFor("subject.dem", [1, 2, 3], null), [],
                Path.Combine(root, "cache"), []);
            using (Assert.Multiple())
            {
                await Assert.That(corpus.CanFetch).IsFalse();
                SkipTestException thrown = Assert.Throws<SkipTestException>(
                    () => corpus.Require("subject.dem"))!;
                await Assert.That(thrown.Message).Contains(DemoTestHelper.DemoSourcesEnvVar);
                await Assert.That(thrown.Message).Contains(DemoTestHelper.CorpusRootEnvVar);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task AnUnmanifestedName_Skips_RatherThanBeingFetched()
    {
        string root = TempRoot();
        try
        {
            Write(Path.Combine(root, "source"), "stranger.dem", [7]);
            DemoCorpus corpus = new(ManifestFor("subject.dem", [1, 2, 3], null), [],
                Path.Combine(root, "cache"), [new DirectoryDemoSource(Path.Combine(root, "source"))]);

            SkipTestException thrown = Assert.Throws<SkipTestException>(
                () => corpus.Require("stranger.dem"))!;
            await Assert.That(thrown.Message).Contains("not in the corpus manifest");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A source that throws is recorded against its own name and resolution moves on, so one
    ///     unavailable mount does not hide a second source that holds the demo.
    /// </summary>
    [Test]
    public async Task AFailingSource_DoesNotStopTheNextOne()
    {
        string root = TempRoot();
        try
        {
            byte[] bytes = [5, 5, 5];
            Write(Path.Combine(root, "good"), "subject.dem", bytes);
            DemoCorpus corpus = new(ManifestFor("subject.dem", bytes, null), [],
                Path.Combine(root, "cache"),
                [new ThrowingSource(), new DirectoryDemoSource(Path.Combine(root, "good"))]);

            await Assert.That(corpus.TryResolve("subject.dem", out string? problem)).IsNotNull();
            await Assert.That(problem).IsNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── Source configuration ──────────────────────────────────────────────────

    [Test]
    public async Task ASourceSpecifier_TakesAPathOrAFileUri_AndRejectsOtherSchemes()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            byte[] bytes = [3, 3];
            Write(root, "subject.dem", bytes);
            CorpusDemo demo = ManifestFor("subject.dem", bytes, null).ByName("subject")!;
            string dest = Path.Combine(root, "copy.dem");

            using (Assert.Multiple())
            {
                await Assert.That(DirectoryDemoSource.FromSpecifier(root).TryCopyTo(demo, dest)).IsTrue();
                await Assert.That(DirectoryDemoSource.FromSpecifier(new Uri(root).AbsoluteUri)
                    .TryCopyTo(demo, dest)).IsTrue();
                // Silently ignoring a configured source would read as "nothing to fetch from".
                Assert.Throws<NotSupportedException>(
                    () => DirectoryDemoSource.FromSpecifier("s3://bucket/demos"));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A source is probed for one named file, never listed. That is why an unavailable mount can
    ///     never present itself as an empty corpus, which once caused a cache prune.
    /// </summary>
    [Test]
    public async Task AnAbsentSourceRoot_IsNotHoldingIt_RatherThanThrowing()
    {
        CorpusDemo demo = ManifestFor("subject.dem", [1], "benchmarks").ByName("subject")!;
        string gone = Path.Combine(TempRoot(), "never-existed");
        await Assert.That(new DirectoryDemoSource(gone).TryCopyTo(demo, Path.Combine(TempRoot(), "x.dem")))
            .IsFalse();
    }

    /// <summary>
    ///     A cached file of the wrong length is corruption in a content-addressed cache. It must
    ///     never be returned, and it must not be silently overwritten either: the message says
    ///     which file to remove.
    /// </summary>
    [Test]
    public async Task ACachedFileOfTheWrongLength_IsNeverReturned()
    {
        string root = TempRoot();
        try
        {
            byte[] bytes = [6, 6, 6, 6, 6, 6, 6];
            Write(Path.Combine(root, "source"), "subject.dem", bytes);
            CorpusManifest manifest = ManifestFor("subject.dem", bytes, null);
            string cache = Path.Combine(root, "cache");
            DemoCorpus corpus = new(manifest, [], cache,
                [new DirectoryDemoSource(Path.Combine(root, "source"))]);

            string poisoned = corpus.CachePath(manifest.ByName("subject")!);
            Write(Path.GetDirectoryName(poisoned)!, Path.GetFileName(poisoned), [1, 2]);

            SkipTestException thrown = Assert.Throws<SkipTestException>(
                () => corpus.Require("subject.dem"))!;
            using (Assert.Multiple())
            {
                await Assert.That(thrown.Message).Contains("wrong length");
                await Assert.That(thrown.Message).Contains("manifest says 7");
                // Untouched, not overwritten: the layer removes nothing.
                await Assert.That(new FileInfo(poisoned).Length).IsEqualTo(2);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── The corpus root override ──────────────────────────────────────────────

    /// <summary>
    ///     The override rules, with the environment passed in rather than read, so they are
    ///     checked against temp folders without touching the variable every RealDemo test in this
    ///     process reads.
    /// </summary>
    [Test]
    public async Task CorpusRootOverride_WinsWhenItIsADirectory()
    {
        string root = TempRoot();
        string configured = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(configured);
        Directory.CreateDirectory(Path.Combine(root, "repo", "demos"));
        try
        {
            string? resolved = DemoTestHelper.ResolveCorpusRoot(
                configured, Path.Combine(root, "repo"), out string? problem);
            using (Assert.Multiple())
            {
                await Assert.That(resolved).IsEqualTo(configured);
                await Assert.That(problem).IsNull();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The load-bearing rule: a configured root that does not exist resolves to NOTHING even
    ///     though the repo has a usable <c>demos/</c>. Falling back is what makes a misconfigured
    ///     run look configured.
    /// </summary>
    [Test]
    public async Task CorpusRootOverride_SetButMissing_DoesNotFallBackToTheRepo()
    {
        string root = TempRoot();
        string repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "demos"));
        try
        {
            string missing = Path.Combine(root, "no-such-corpus");
            string? resolved = DemoTestHelper.ResolveCorpusRoot(missing, repo, out string? problem);
            using (Assert.Multiple())
            {
                await Assert.That(resolved).IsNull();
                await Assert.That(problem).Contains(DemoTestHelper.CorpusRootEnvVar);
                await Assert.That(problem).Contains(missing);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task CorpusRootOverride_Unset_IsTheRepoDemosDirectory()
    {
        string root = TempRoot();
        string repo = Path.Combine(root, "repo");
        string demos = Path.Combine(repo, "demos");
        Directory.CreateDirectory(demos);
        try
        {
            using (Assert.Multiple())
            {
                foreach (string? unset in new[] { null, "", "   " })
                {
                    await Assert.That(DemoTestHelper.ResolveCorpusRoot(unset, repo, out string? problem))
                        .IsEqualTo(demos);
                    await Assert.That(problem).IsNull();
                }
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task CorpusRootOverride_Unset_WithNoDemosDirectory_SaysSo()
    {
        string root = TempRoot();
        string repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        try
        {
            using (Assert.Multiple())
            {
                await Assert.That(DemoTestHelper.ResolveCorpusRoot(null, repo, out string? problem)).IsNull();
                await Assert.That(problem).Contains("no corpus");

                await Assert.That(DemoTestHelper.ResolveCorpusRoot(null, null, out string? noRepo)).IsNull();
                await Assert.That(noRepo).Contains("repo root not found");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     The override is read from the environment, which this test must not mutate, so the rule
    ///     checked here is the one that matters and cannot be checked any other way: the default is
    ///     the repo's own <c>demos/</c>, and a configured root is reported as configured.
    /// </summary>
    [Test]
    public async Task CorpusRoot_DefaultsToTheRepoDemosDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable(DemoTestHelper.CorpusRootEnvVar);
        string? root = DemoTestHelper.ResolveCorpusRoot(out string? problem);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Configured in this process: it resolves to the configuration, or says why not.
            await Assert.That(root is not null || problem is not null).IsTrue();
            return;
        }

        string? repoRoot = DemoTestHelper.FindRepoRoot();
        if (repoRoot is null)
        {
            throw new SkipTestException("repo root not found from the test output directory");
        }

        string demos = Path.Combine(repoRoot, "demos");
        if (!Directory.Exists(demos))
        {
            // A checkout with no demos/ at all: the reason must say so, not resolve silently.
            using (Assert.Multiple())
            {
                await Assert.That(root).IsNull();
                await Assert.That(problem).IsNotNull();
            }

            return;
        }

        using (Assert.Multiple())
        {
            await Assert.That(root).IsEqualTo(demos);
            await Assert.That(problem).IsNull();
            await Assert.That(DemoTestHelper.CorpusDir()).IsEqualTo(demos);
            await Assert.That(DemoTestHelper.CorpusDir("no-such-subdirectory")).IsNull();
        }
    }

    [Test]
    public async Task TheProcessCorpus_HasNoSourcesUnlessConfigured()
    {
        bool configured = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(DemoTestHelper.DemoSourcesEnvVar));
        await Assert.That(DemoTestHelper.Corpus.CanFetch).IsEqualTo(configured);
    }

    /// <summary>The cache must never sit inside the corpus: this layer has no write path there.</summary>
    [Test]
    public async Task TheCacheRoot_IsOutsideTheCorpus()
    {
        string cache = Path.GetFullPath(DemoTestHelper.Corpus.CacheRoot);
        if (DemoTestHelper.ResolveCorpusRoot(out _) is not { } corpus)
        {
            throw new SkipTestException("no corpus root to compare the cache root against");
        }

        await Assert.That(cache.StartsWith(
                Path.GetFullPath(corpus) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .IsFalse();
    }

    // ── Folder-shaped DEMO_PATH ───────────────────────────────────────────────

    [Test]
    public async Task FolderOverride_ListsEveryDemo_InOrdinalOrder_AndSkipsSidecars()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            Write(root, "B.dem", [1]);
            Write(root, "a.dem", [1]);
            Write(root, "c.dem.info", [1]);
            string[] demos = DemoTestHelper.ResolveDemoFolderOverride(root, out string? problem);
            using (Assert.Multiple())
            {
                await Assert.That(problem).IsNull();
                await Assert.That(demos.Select(p => Path.GetFileName(p)!)).IsEquivalentTo(OrdinalPair, CollectionOrdering.Matching);
                // Ordinal, not culture: "B" before "a".
                await Assert.That(Path.GetFileName(demos[0])).IsEqualTo("B.dem");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task FolderOverride_Distinguishes_Unset_Missing_AndEmpty()
    {
        string root = TempRoot();
        Directory.CreateDirectory(root);
        try
        {
            using (Assert.Multiple())
            {
                // Unset is not a problem: the caller decides whether that is a skip.
                await Assert.That(DemoTestHelper.ResolveDemoFolderOverride(null, out string? unset)).IsEmpty();
                await Assert.That(unset).IsNull();

                await Assert.That(DemoTestHelper.ResolveDemoFolderOverride("   ", out string? blank)).IsEmpty();
                await Assert.That(blank).IsNull();

                string missing = Path.Combine(root, "nowhere");
                await Assert.That(DemoTestHelper.ResolveDemoFolderOverride(missing, out string? gone)).IsEmpty();
                await Assert.That(gone).Contains("does not exist");

                await Assert.That(DemoTestHelper.ResolveDemoFolderOverride(root, out string? empty)).IsEmpty();
                await Assert.That(empty).Contains("holds no .dem");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task FolderOverride_AFileYieldsItself()
    {
        string root = TempRoot();
        try
        {
            string demo = Write(root, "one.dem", [1]);
            string[] demos = DemoTestHelper.ResolveDemoFolderOverride(demo, out string? problem);
            using (Assert.Multiple())
            {
                await Assert.That(demos.Length).IsEqualTo(1);
                await Assert.That(demos[0]).IsEqualTo(demo);
                await Assert.That(problem).IsNull();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── Sets ──────────────────────────────────────────────────────────────────

    private static CorpusManifest SetManifest(params (string File, byte[] Bytes, string? Dir)[] demos) =>
        CorpusManifest.Parse("{ \"demos\": [" + string.Join(",", demos.Select(d =>
            $$"""
              { "name": "n-{{d.File}}", "file": "{{d.File}}", "bytes": {{d.Bytes.Length}},
                "sha256": "{{Sha256Of(d.Bytes)}}", "dir": {{(d.Dir is null ? "null" : $"\"{d.Dir}\"")}},
                "sets": ["s"] }
              """)) + "] }");

    [Test]
    public async Task SetMembers_AreInOrdinalFileNameOrder_WhateverTheManifestOrder()
    {
        CorpusManifest manifest = SetManifest(("c.dem", [1], null), ("a.dem", [2], null), ("B.dem", [3], null));
        DemoCorpus corpus = new(manifest, [], Path.Combine(TempRoot(), "cache"), []);

        // Ordinal puts "B" before "a"; a culture-aware sort would not. Callers index by position.
        await Assert.That(corpus.SetMembers("s").Select(d => d.File).ToArray())
            .IsEquivalentTo(OrdinalTriple, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EmbeddedSets_MatchTheOrdinalOrderOfTheirDirectories()
    {
        DemoCorpus corpus = new(CorpusManifest.Embedded, [], Path.Combine(TempRoot(), "cache"), []);
        foreach (string set in new[] { "benchmarks", "mm", "pro", "valve-mm" })
        {
            string[] files = [.. corpus.SetMembers(set).Select(d => d.File)];
            using (Assert.Multiple())
            {
                await Assert.That(files.Length).IsGreaterThan(0);
                await Assert.That(files).IsEquivalentTo(files.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
            }
        }
    }

    [Test]
    public async Task Set_FetchesEachMemberFromASource_AndKeepsTheOrder_WithNoLocalCorpus()
    {
        string root = TempRoot();
        try
        {
            byte[] one = [1, 1], two = [2, 2, 2];
            Write(Path.Combine(root, "source"), "b.dem", two);
            Write(Path.Combine(root, "source"), "a.dem", one);
            CorpusManifest manifest = SetManifest(("b.dem", two, null), ("a.dem", one, null));
            DemoCorpus corpus = new(manifest, [], Path.Combine(root, "cache"),
                [new DirectoryDemoSource(Path.Combine(root, "source"))]);

            string[] paths = corpus.RequireSet("s");
            using (Assert.Multiple())
            {
                await Assert.That(paths.Select(p => Path.GetFileName(p)!).ToArray())
                    .IsEquivalentTo(AThenB, CollectionOrdering.Matching);
                await Assert.That(await File.ReadAllBytesAsync(paths[0])).IsEquivalentTo(one, CollectionOrdering.Matching);
                await Assert.That(corpus.RequireSetMember("s", 1)).IsEqualTo(paths[1]);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task SetMember_FetchesOnlyThatMember()
    {
        string root = TempRoot();
        try
        {
            byte[] one = [1, 1], two = [2, 2, 2];
            Write(Path.Combine(root, "source"), "a.dem", one);
            Write(Path.Combine(root, "source"), "b.dem", two);
            CorpusManifest manifest = SetManifest(("a.dem", one, null), ("b.dem", two, null));
            DemoCorpus corpus = new(manifest, [], Path.Combine(root, "cache"),
                [new DirectoryDemoSource(Path.Combine(root, "source"))]);

            string path = corpus.RequireSetMember("s", 1);
            using (Assert.Multiple())
            {
                await Assert.That(Path.GetFileName(path)).IsEqualTo("b.dem");
                await Assert.That(File.Exists(corpus.CachePath(manifest.ByFile("a.dem")!))).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task SetMember_ThatCannotBeObtained_SkipsNamingIt_AndNeverPasses()
    {
        string root = TempRoot();
        try
        {
            byte[] one = [1, 1], two = [2, 2, 2];
            Write(Path.Combine(root, "source"), "a.dem", one);
            CorpusManifest manifest = SetManifest(("a.dem", one, null), ("b.dem", two, null));
            DemoCorpus corpus = new(manifest, [], Path.Combine(root, "cache"),
                [new DirectoryDemoSource(Path.Combine(root, "source"))]);

            SkipTestException whole = Assert.Throws<SkipTestException>(() => corpus.RequireSet("s"));
            SkipTestException member = Assert.Throws<SkipTestException>(() => corpus.RequireSetMember("s", 1));
            using (Assert.Multiple())
            {
                await Assert.That(whole.Message).Contains("b.dem");
                await Assert.That(whole.Message).Contains("'s'");
                await Assert.That(member.Message).Contains("b.dem");
                await Assert.That(corpus.TryResolveSet("s", out string? problem)).IsNull();
                await Assert.That(problem).Contains("b.dem");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task Set_WithNoSourceAndNoCorpus_SkipsNamingTheEnvironment()
    {
        CorpusManifest manifest = SetManifest(("a.dem", [1], null));
        DemoCorpus corpus = new(manifest, [], Path.Combine(TempRoot(), "cache"), []);

        SkipTestException thrown = Assert.Throws<SkipTestException>(() => corpus.RequireSet("s"));
        using (Assert.Multiple())
        {
            await Assert.That(thrown.Message).Contains(DemoTestHelper.DemoSourcesEnvVar);
            await Assert.That(thrown.Message).Contains("a.dem");
        }
    }

    [Test]
    public async Task Set_ShortOrUnknown_SkipsInsteadOfIndexingPastTheEnd()
    {
        CorpusManifest manifest = SetManifest(("a.dem", [1], null));
        DemoCorpus corpus = new(manifest, [], Path.Combine(TempRoot(), "cache"), []);

        SkipTestException shortSet = Assert.Throws<SkipTestException>(() => corpus.RequireSetMember("s", 3));
        SkipTestException unknown = Assert.Throws<SkipTestException>(() => corpus.RequireSetMember("nope", 0));
        using (Assert.Multiple())
        {
            await Assert.That(shortSet.Message).Contains("fewer than 4");
            await Assert.That(unknown.Message).Contains("'nope'");
        }
    }

    [Test]
    public async Task SetMember_InASubdirectoryOfTheCorpusRoot_IsFoundWithoutAFetch()
    {
        string root = TempRoot();
        try
        {
            byte[] bytes = [5, 5];
            string home = Write(Path.Combine(root, "demos", "pro-demos"), "a.dem", bytes);
            CorpusManifest manifest = SetManifest(("a.dem", bytes, "pro-demos"));
            DemoCorpus corpus = new(manifest, [Path.Combine(root, "demos", "benchmarks"), Path.Combine(root, "demos")],
                Path.Combine(root, "cache"), [], Path.Combine(root, "demos"));

            await Assert.That(corpus.RequireSetMember("s", 0)).IsEqualTo(home);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ── A set-but-invalid DEMO_CORPUS fails; it does not skip ──────────────────

    [Test]
    public async Task RequireCorpusDir_WithAnInvalidConfiguredRoot_FailsAndDoesNotSkip()
    {
        string missing = Path.Combine(TempRoot(), "not-here");

        DemoCorpusConfigurationException thrown = Assert.Throws<DemoCorpusConfigurationException>(
            () => DemoTestHelper.RequireCorpusDir("benchmarks", missing, null));
        using (Assert.Multiple())
        {
            await Assert.That(thrown.Message).Contains(DemoTestHelper.CorpusRootEnvVar);
            await Assert.That(thrown.Message).Contains(missing);
        }
    }

    [Test]
    public async Task RequireCorpusDir_Unconfigured_StillSkipsWhenThereIsNoCorpus()
    {
        SkipTestException thrown = Assert.Throws<SkipTestException>(
            () => DemoTestHelper.RequireCorpusDir("benchmarks", null, null));
        await Assert.That(thrown.Message).Contains(DemoTestHelper.CorpusRootEnvVar);
    }

    [Test]
    public async Task RequireCorpusDir_ReturnsTheDirectory_UnderAValidConfiguredRoot()
    {
        string root = TempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "benchmarks"));
            await Assert.That(DemoTestHelper.RequireCorpusDir("benchmarks", root, null))
                .IsEqualTo(Path.Combine(root, "benchmarks"));
            Assert.Throws<SkipTestException>(() => DemoTestHelper.RequireCorpusDir("absent", root, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task ThrowIfCorpusRootMisconfigured_IgnoresUnsetAndBlank()
    {
        await Assert.That(() => DemoTestHelper.ThrowIfCorpusRootMisconfigured(null)).ThrowsNothing();
        await Assert.That(() => DemoTestHelper.ThrowIfCorpusRootMisconfigured("  ")).ThrowsNothing();
    }

    // ── The replays folder ────────────────────────────────────────────────────

    [Test]
    public async Task ReplaysFolder_IsTheFolder_OrTheFolderOfTheFile_OrNothingWithAReason()
    {
        string root = TempRoot();
        try
        {
            string demo = Write(root, "one.dem", [1]);
            using (Assert.Multiple())
            {
                await Assert.That(DemoTestHelper.ResolveReplaysFolder(root, out _)).IsEqualTo(root);
                await Assert.That(DemoTestHelper.ResolveReplaysFolder(demo, out _)).IsEqualTo(root);
                await Assert.That(DemoTestHelper.ResolveReplaysFolder(null, out string? unset)).IsNull();
                await Assert.That(unset).Contains(DemoTestHelper.DemoPathEnvVar);
                await Assert.That(DemoTestHelper.ResolveReplaysFolder(Path.Combine(root, "gone"), out string? gone)).IsNull();
                await Assert.That(gone).Contains("does not exist");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>A source that is reachable but fails. Stands in for an unavailable mount.</summary>
    private sealed class ThrowingSource : IDemoSource
    {
        public string Description => "throwing-source";

        public bool TryCopyTo(CorpusDemo demo, string destination) =>
            throw new IOException("source unavailable");
    }
}
