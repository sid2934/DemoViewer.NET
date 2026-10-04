#region

using System.Runtime.Loader;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="ExtensionLoader" /> (strat-book-plugin.md §7.8): discovery ranks versions and refuses a
///     folder that disagrees with its manifest or escapes the tree; selection takes the highest staged
///     version that is newer, compatible and trusted and falls back otherwise; loading a real staged copy
///     of the Strat Book puts it in its own context, and every way a staged copy can be wrong is a reason,
///     never an exception.
/// </summary>
public class ExtensionLoaderTests
{
    private const string FakeId = "net.demoviewer.pack.fake";
    private static readonly ExtensionHostInfo Host = new(SemVersion.Parse("1.0.0"), null, SemVersion.Parse("0.13.0-beta0001"));
    private static readonly ITrustPolicy TrustAll = new AllTrust();

    // ── Discover ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Discover_RanksVersionsHighestFirst_AndRejectsFolderAndManifestMismatches()
    {
        string root = NewRoot();
        try
        {
            Stage(root, FakeId, "1.0.0", ManifestJson(FakeId, "1.0.0"));
            Stage(root, FakeId, "1.2.0", ManifestJson(FakeId, "1.2.0"));
            Stage(root, FakeId, "1.1.0-rc1", ManifestJson(FakeId, "1.1.0-rc1"));
            Stage(root, FakeId, "2.0.0", ManifestJson(FakeId, "1.0.0")); // folder says 2.0.0
            Stage(root, "wrong.id", "1.0.0", ManifestJson(FakeId, "1.0.0")); // folder says wrong.id
            Stage(root, FakeId, "3.0.0", "{ not json");
            Directory.CreateDirectory(Path.Combine(root, "extensions", FakeId, "4.0.0")); // no manifest

            ExtensionLoader.Discovery found = ExtensionLoader.Discover(Path.Combine(root, "extensions"));

            using (Assert.Multiple())
            {
                await Assert.That(found.Candidates.Select(c => c.Manifest.Version.ToString())).IsEquivalentTo(["1.2.0", "1.1.0-rc1", "1.0.0"]);
                await Assert.That(found.Candidates.All(c => c.AssemblyPath == Path.Combine(c.Directory, "Fake.dll"))).IsTrue();
                await Assert.That(found.Rejected.Count).IsEqualTo(4);
                await Assert.That(found.Rejected.Count(o => o.Failure == LoadFailure.FolderMismatch)).IsEqualTo(2);
                await Assert.That(found.Rejected.Count(o => o.Failure == LoadFailure.ManifestInvalid)).IsEqualTo(2);
                await Assert.That(found.Rejected.Single(o => o.Directory.EndsWith("2.0.0", StringComparison.Ordinal)).Detail)
                    .Contains($"'{FakeId}/2.0.0'").And.Contains($"'{FakeId}/1.0.0'");
                await Assert.That(found.Rejected.Single(o => o.Directory.EndsWith("4.0.0", StringComparison.Ordinal)).UserMessage)
                    .IsEqualTo("An update in '4.0.0' was not loaded: no extension.json");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Discover_MissingDirectory_FindsNothing()
    {
        ExtensionLoader.Discovery found = ExtensionLoader.Discover(Path.Combine(Path.GetTempPath(), "dvext-missing-" + Guid.NewGuid().ToString("N")));
        using (Assert.Multiple())
        {
            await Assert.That(found.Candidates).IsEmpty();
            await Assert.That(found.Rejected).IsEmpty();
            await Assert.That(ExtensionLoader.ExtensionsDirectory(null)).IsNull().Because("the browser has no config root");
        }
    }

    // A version folder that is a symlink, and a manifest that is one, are refused the way
    // PackDataRemover.ResolveSafe refuses a reparse point: never followed, whatever they point at.
    [Test]
    public async Task Discover_RejectsALinkedFolderOrManifest()
    {
        string root = NewRoot();
        try
        {
            string outside = Path.Combine(root, "outside", "1.5.0");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, ExtensionManifest.FileName), ManifestJson(FakeId, "1.5.0"));
            string idDir = Path.Combine(root, "extensions", FakeId);
            Directory.CreateDirectory(idDir);
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(idDir, "1.5.0"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SkipTestException("symbolic links are not available here: " + ex.Message);
            }

            string linkedManifestDir = Path.Combine(idDir, "1.6.0");
            Directory.CreateDirectory(linkedManifestDir);
            File.WriteAllText(Path.Combine(root, "loose.json"), ManifestJson(FakeId, "1.6.0"));
            File.CreateSymbolicLink(Path.Combine(linkedManifestDir, ExtensionManifest.FileName), Path.Combine(root, "loose.json"));

            ExtensionLoader.Discovery found = ExtensionLoader.Discover(Path.Combine(root, "extensions"));

            using (Assert.Multiple())
            {
                await Assert.That(found.Candidates).IsEmpty();
                await Assert.That(found.Rejected.Select(o => o.Failure)).IsEquivalentTo([LoadFailure.PathEscapes, LoadFailure.PathEscapes]);
                await Assert.That(File.Exists(Path.Combine(outside, ExtensionManifest.FileName))).IsTrue().Because("the loader never writes or deletes");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Select ───────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Select_TakesTheHighestNewerCompatibleTrustedCandidate_AndRecordsTheHigherOnesItRefused()
    {
        ExtensionCandidate[] candidates =
        [
            Candidate("1.3.0", requiresHost: "^2.0"),
            Candidate("1.2.0"),
            Candidate("1.1.0"),
            Candidate("1.4.0", id: "net.demoviewer.pack.other")
        ];

        ExtensionLoader.Selection selection = ExtensionLoader.Select(candidates, FakeId, SemVersion.Parse("1.0.0"), Host, TrustAll);

        using (Assert.Multiple())
        {
            await Assert.That(selection.Chosen?.Manifest.Version.ToString()).IsEqualTo("1.2.0");
            await Assert.That(selection.Rejected.Select(o => (o.Version!.ToString(), o.Failure))).IsEquivalentTo([("1.3.0", LoadFailure.Incompatible)]);
            await Assert.That(selection.Rejected[0].UserMessage).IsEqualTo("Update 1.3.0 was not loaded: Fake 1.3.0 needs app contract ^2.0; this app provides 1.0.0");
        }
    }

    [Test]
    public async Task Select_FallsBack_WhenNothingIsNewerCompatibleAndTrusted()
    {
        ExtensionCandidate[] equalAndOlder = [Candidate("1.2.0"), Candidate("1.1.0")];
        ExtensionLoader.Selection notNewer = ExtensionLoader.Select(equalAndOlder, FakeId, SemVersion.Parse("1.2.0"), Host, TrustAll);
        ExtensionLoader.Selection untrusted = ExtensionLoader.Select([Candidate("1.3.0")], FakeId, SemVersion.Parse("1.0.0"), Host, TrustPolicy.Nothing);
        ExtensionLoader.Selection unknownShipped = ExtensionLoader.Select([Candidate("1.3.0")], FakeId, null, Host, TrustAll);
        ExtensionLoader.Selection throwingTrust = ExtensionLoader.Select([Candidate("1.3.0")], FakeId, SemVersion.Parse("1.0.0"), Host, new ThrowingTrust());
        ExtensionLoader.Selection prerelease = ExtensionLoader.Select([Candidate("1.3.0-beta1", requiresCs2DemoKit: "0.14.0")], FakeId, SemVersion.Parse("1.0.0"), Host, TrustAll);

        using (Assert.Multiple())
        {
            await Assert.That(notNewer.Chosen).IsNull();
            await Assert.That(notNewer.Rejected.Select(o => o.Failure)).IsEquivalentTo([LoadFailure.NotNewer, LoadFailure.NotNewer]);
            await Assert.That(notNewer.Rejected[0].UserMessage).IsEqualTo("Update 1.2.0 was not loaded: the bundled extension is 1.2.0, which is not older");

            await Assert.That(untrusted.Chosen).IsNull();
            await Assert.That(untrusted.Rejected.Single().Failure).IsEqualTo(LoadFailure.Untrusted);

            await Assert.That(unknownShipped.Chosen).IsNull();
            await Assert.That(unknownShipped.Rejected.Single().Failure).IsEqualTo(LoadFailure.ShippedUnknown);

            await Assert.That(throwingTrust.Chosen).IsNull().Because("a policy that throws reads as untrusted, not as a crash");
            await Assert.That(throwingTrust.Rejected.Single().Detail).Contains("the trust check failed");

            await Assert.That(prerelease.Chosen).IsNull();
            await Assert.That(prerelease.Rejected.Single().Failure).IsEqualTo(LoadFailure.Incompatible);
        }
    }

    [Test]
    public async Task TrustPolicy_TrustsNothingByDefault_UnlessTheDeveloperOptInIsSet()
    {
        ExtensionManifest manifest = FakeManifests.For(FakeId);
        using (Assert.Multiple())
        {
            await Assert.That(TrustPolicy.Nothing.IsTrusted("/x", manifest)).IsFalse();
            await Assert.That(TrustPolicy.UnsignedOptIn(_ => null).IsTrusted("/x", manifest)).IsFalse();
            await Assert.That(TrustPolicy.UnsignedOptIn(_ => "true").IsTrusted("/x", manifest)).IsFalse().Because("only the documented value opts in");
            await Assert.That(TrustPolicy.UnsignedOptIn(name => name == TrustPolicy.TrustUnsignedEnvVar ? "1" : null).IsTrusted("/x", manifest)).IsTrue();
        }
    }

    // ── Load: a real staged copy of the Strat Book ───────────────────────────────────────────────

    // The shipped assembly is already in this process (CompiledInPacks), so a loaded staged copy must be a
    // second Assembly in its own context, with the shipped type untouched by the load.
    [Test]
    public async Task Load_RealStagedCopy_LoadsASecondAssemblyIntoItsOwnContext()
    {
        string root = NewRoot();
        try
        {
            ExtensionCandidate candidate = StageRealCopy(root, versionOverride: null);

            ExtensionLoader.LoadResult result = ExtensionLoader.Load(candidate);

            using (Assert.Multiple())
            {
                await Assert.That(result.Failure).IsNull().Because(result.Failure?.Detail ?? "loaded");
                IFeaturePack pack = result.Pack!;
                await Assert.That(pack.Id).IsEqualTo(StratBookPack.PackId);
                await Assert.That(pack.GetType()).IsNotEqualTo(typeof(StratBookPack)).Because("same name, different assembly");
                await Assert.That(pack.GetType().FullName).IsEqualTo(typeof(StratBookPack).FullName);
                await Assert.That(ReferenceEquals(pack.GetType().Assembly, typeof(StratBookPack).Assembly)).IsFalse();
                await Assert.That(Path.GetFullPath(pack.GetType().Assembly.Location)).IsEqualTo(Path.GetFullPath(candidate.AssemblyPath));
                AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(pack.GetType().Assembly);
                await Assert.That(context).IsTypeOf<ExtensionLoadContext>();
                await Assert.That(context!.Name).IsEqualTo($"extension:{StratBookPack.PackId}@{candidate.Manifest.Version}");
                await Assert.That(context.IsCollectible).IsFalse();
                await Assert.That(pack.Manifest.Version).IsEqualTo(candidate.Manifest.Version);
                // Its dependencies bound to the copies this process runs on, so the pack contract is one type.
                await Assert.That(AssemblyLoadContext.GetLoadContext(pack.GetType().GetInterface(nameof(IFeaturePack))!.Assembly))
                    .IsEqualTo(AssemblyLoadContext.Default);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Load_ManifestBumpedOverAnUnchangedAssembly_IsAnIdentityMismatch()
    {
        string root = NewRoot();
        try
        {
            ExtensionCandidate candidate = StageRealCopy(root, versionOverride: "9.9.9");

            ExtensionLoader.LoadResult result = ExtensionLoader.Load(candidate);

            using (Assert.Multiple())
            {
                await Assert.That(result.Pack).IsNull();
                await Assert.That(result.Failure!.Failure).IsEqualTo(LoadFailure.IdentityMismatch);
                await Assert.That(result.Failure.Detail).Contains($"not {StratBookPack.PackId} 9.9.9");
                await Assert.That(result.Failure.UserMessage).StartsWith("Update 9.9.9 was not loaded: the assembly is");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Load_CorruptOrMissingAssembly_IsAssemblyLoadFailed()
    {
        string root = NewRoot();
        try
        {
            ExtensionCandidate corrupt = StageRealCopy(root, versionOverride: null);
            File.WriteAllBytes(corrupt.AssemblyPath, [0x4D, 0x5A, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
            ExtensionCandidate missing = new(Path.Combine(root, "nowhere"), corrupt.Manifest);

            ExtensionLoader.LoadResult corruptResult = ExtensionLoader.Load(corrupt);
            ExtensionLoader.LoadResult missingResult = ExtensionLoader.Load(missing);

            using (Assert.Multiple())
            {
                await Assert.That(corruptResult.Failure?.Failure).IsEqualTo(LoadFailure.AssemblyLoadFailed);
                await Assert.That(corruptResult.Failure!.Detail).Contains(corrupt.Manifest.Assembly);
                await Assert.That(missingResult.Failure?.Failure).IsEqualTo(LoadFailure.AssemblyLoadFailed);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Load_MissingEntryType_OrATypeThatIsNotAPack_IsAReason()
    {
        string root = NewRoot();
        try
        {
            ExtensionCandidate real = StageRealCopy(root, versionOverride: null);
            ExtensionCandidate noSuchType = real with { Manifest = real.Manifest with { EntryType = "DemoViewer.NET.Extensions.StratBook.NoSuchPack" } };
            ExtensionCandidate notAPack = real with { Manifest = real.Manifest with { EntryType = typeof(StratBookCache).FullName! } };

            ExtensionLoader.LoadResult missing = ExtensionLoader.Load(noSuchType);
            ExtensionLoader.LoadResult wrongKind = ExtensionLoader.Load(notAPack);

            using (Assert.Multiple())
            {
                await Assert.That(missing.Failure?.Failure).IsEqualTo(LoadFailure.EntryTypeMissing);
                await Assert.That(missing.Failure!.Detail).Contains("NoSuchPack");
                await Assert.That(wrongKind.Failure?.Failure).IsEqualTo(LoadFailure.NotAPack);
                await Assert.That(wrongKind.Failure!.Detail).Contains("StratBookCache");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Resolve: the head's call, end to end ─────────────────────────────────────────────────────

    // The shipped manifest the test writes says 0.9.0, so the real 1.0.0 copy on disk is the newer one:
    // it loads, the status says where from, and the shipped factory is never invoked.
    [Test]
    public async Task Resolve_LoadsTheStagedCopy_WhenNewerCompatibleAndTrusted_AndNeverBuildsTheShippedOne()
    {
        string root = NewRoot();
        try
        {
            ExtensionCandidate staged = StageRealCopy(root, versionOverride: null);
            int created = 0;
            ShippedPack shipped = new(StratBookPack.PackId, () =>
            {
                created++;
                return new StratBookPack();
            }, WriteShippedManifest(root, "0.9.0"));

            IReadOnlyList<PackStatus> statuses = ExtensionLoader.Resolve(root, [shipped], Host, TrustAll);

            using (Assert.Multiple())
            {
                await Assert.That(statuses.Count).IsEqualTo(1);
                PackStatus status = statuses[0];
                await Assert.That(status.IsCompatible).IsTrue().Because(status.Problem ?? "compatible");
                await Assert.That(status.Source).IsEqualTo(new PackSource.Staged(staged.Directory));
                await Assert.That(status.Source.Label).IsEqualTo("installed update");
                await Assert.That(status.Rejected).IsEmpty();
                await Assert.That(status.Manifest!.Version).IsEqualTo(staged.Manifest.Version);
                await Assert.That(ReferenceEquals(status.Pack.GetType().Assembly, typeof(StratBookPack).Assembly)).IsFalse();
                await Assert.That(created).IsEqualTo(0).Because("the shipped copy must not be instantiated when the staged one wins");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Resolve_FallsBackToTheShippedCopy_WhenTheStagedOneFailsToLoad_AndSaysWhy()
    {
        string root = NewRoot();
        try
        {
            ExtensionCandidate staged = StageRealCopy(root, versionOverride: "1.0.5");
            int created = 0;
            ShippedPack shipped = new(StratBookPack.PackId, () =>
            {
                created++;
                return new StratBookPack();
            }, WriteShippedManifest(root, "0.9.0"));

            IReadOnlyList<PackStatus> statuses = ExtensionLoader.Resolve(root, [shipped], Host, TrustAll);

            using (Assert.Multiple())
            {
                PackStatus status = statuses[0];
                await Assert.That(status.Source).IsEqualTo(PackSource.Bundled);
                await Assert.That(status.IsCompatible).IsTrue();
                await Assert.That(status.Pack.GetType()).IsEqualTo(typeof(StratBookPack));
                await Assert.That(created).IsEqualTo(1);
                await Assert.That(status.Rejected.Select(o => (o.Directory, o.Failure))).IsEquivalentTo([(staged.Directory, LoadFailure.IdentityMismatch)]);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Resolve_WithoutTheOptIn_KeepsTheShippedCopy_AndRecordsUntrusted()
    {
        string root = NewRoot();
        try
        {
            StageRealCopy(root, versionOverride: null);
            ShippedPack shipped = new(StratBookPack.PackId, static () => new StratBookPack(), WriteShippedManifest(root, "0.9.0"));

            IReadOnlyList<PackStatus> statuses = ExtensionLoader.Resolve(root, [shipped], Host, TrustPolicy.UnsignedOptIn(_ => null));

            using (Assert.Multiple())
            {
                await Assert.That(statuses[0].Source).IsEqualTo(PackSource.Bundled);
                await Assert.That(statuses[0].Rejected.Single().Failure).IsEqualTo(LoadFailure.Untrusted);
                await Assert.That(statuses[0].Rejected.Single().UserMessage)
                    .IsEqualTo("Update 1.0.0 was not loaded: the copy is not signed by this app's publisher");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Resolve_ShippedOnly_WhenThereIsNoConfigRoot_OrNothingStaged_OrTheShippedVersionIsUnknown()
    {
        string root = NewRoot();
        try
        {
            ShippedPack shipped = new(StratBookPack.PackId, static () => new StratBookPack(), WriteShippedManifest(root, "1.0.0"));
            IReadOnlyList<PackStatus> noRoot = ExtensionLoader.Resolve(null, [shipped], Host, TrustAll);
            IReadOnlyList<PackStatus> empty = ExtensionLoader.Resolve(root, [shipped], Host, TrustAll);

            StageRealCopy(root, versionOverride: null);
            ShippedPack unreadable = shipped with { ManifestPath = Path.Combine(root, "no-such-manifest.json") };
            IReadOnlyList<PackStatus> unknown = ExtensionLoader.Resolve(root, [unreadable], Host, TrustAll);

            using (Assert.Multiple())
            {
                await Assert.That(noRoot[0].Source).IsEqualTo(PackSource.Bundled);
                await Assert.That(noRoot[0].Rejected).IsEmpty();
                await Assert.That(empty[0].Source).IsEqualTo(PackSource.Bundled);
                await Assert.That(empty[0].Rejected).IsEmpty();
                await Assert.That(unknown[0].Source).IsEqualTo(PackSource.Bundled);
                await Assert.That(unknown[0].Rejected.Single().Failure).IsEqualTo(LoadFailure.ShippedUnknown);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── The constraint the child context imposes (§7.8) ──────────────────────────────────────────

    // Two assemblies named DemoViewer.NET.Extensions.StratBook can be in the process once a staged copy
    // loads. Anything that resolves a type or a resource by assembly NAME lands on the shipped copy in the
    // default context, so the extension must not: no avares:// URIs, no assembly= in an xmlns, no
    // Type.GetType(string) or Assembly.Load. The ViewLocator reads pack.GetType().Assembly instead.
    [Test]
    public async Task TheExtension_ResolvesNothingByAssemblyName()
    {
        string repoRoot = DemoTestHelper.FindRepoRoot()
            ?? throw new SkipTestException("repo root not found (no DemoViewer.NET.slnx above the test binary)");
        string extension = Path.Combine(repoRoot, "src", "Extensions", "StratBook", "DemoViewer.NET.Extensions.StratBook");
        if (!Directory.Exists(extension))
        {
            throw new SkipTestException("extension source tree not found");
        }

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(extension, "*.axaml", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (text.Contains("avares://", StringComparison.Ordinal) || text.Contains(";assembly=", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(repoRoot, file));
            }
        }

        foreach (string file in Directory.EnumerateFiles(extension, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (text.Contains("Type.GetType(", StringComparison.Ordinal)
                || text.Contains("Assembly.Load(", StringComparison.Ordinal)
                || text.Contains("Assembly.LoadFrom(", StringComparison.Ordinal)
                || text.Contains("avares://", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(repoRoot, file));
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort
        }
    }

    private static string Stage(string root, string id, string version, string manifestJson)
    {
        string dir = Path.Combine(root, "extensions", id, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionManifest.FileName), manifestJson);
        return dir;
    }

    private static string ManifestJson(string id, string version, string requiresHost = "^1.0", string requiresCs2DemoKit = "*") =>
        $$"""
          {
            "id": "{{id}}",
            "name": "Fake",
            "version": "{{version}}",
            "assembly": "Fake.dll",
            "entryType": "Fake.Pack",
            "requiresHost": "{{requiresHost}}",
            "requiresCs2DemoKit": "{{requiresCs2DemoKit}}"
          }
          """;

    private static ExtensionCandidate Candidate(string version, string id = FakeId, string requiresHost = "^1.0", string requiresCs2DemoKit = "*") =>
        new(Path.Combine("/extensions", id, version), FakeManifests.For(id, "Fake", version, requiresHost, requiresCs2DemoKit));

    // Copies the extension assembly this test process runs (and the manifest beside it) into
    // <root>/extensions/<id>/<version>/. With versionOverride the on-disk manifest and folder say that
    // version while the embedded manifest still says the built one.
    private static ExtensionCandidate StageRealCopy(string root, string? versionOverride)
    {
        string shippedDll = typeof(StratBookPack).Assembly.Location;
        string shippedManifestPath = Path.Combine(Path.GetDirectoryName(shippedDll)!, ExtensionManifest.FileName);
        if (!File.Exists(shippedManifestPath))
        {
            throw new SkipTestException($"no {ExtensionManifest.FileName} beside {shippedDll}");
        }

        string json = File.ReadAllText(shippedManifestPath);
        ExtensionManifest built = ExtensionManifest.Parse(json);
        string version = versionOverride ?? built.Version.ToString();
        if (versionOverride is not null)
        {
            json = json.Replace($"\"{built.Version}\"", $"\"{versionOverride}\"", StringComparison.Ordinal);
        }

        string dir = Stage(root, built.Id, version, json);
        File.Copy(shippedDll, Path.Combine(dir, built.Assembly));
        return new ExtensionCandidate(dir, ExtensionManifest.Parse(json));
    }

    private static string WriteShippedManifest(string root, string version)
    {
        string shippedDll = typeof(StratBookPack).Assembly.Location;
        string json = File.ReadAllText(Path.Combine(Path.GetDirectoryName(shippedDll)!, ExtensionManifest.FileName));
        ExtensionManifest built = ExtensionManifest.Parse(json);
        string path = Path.Combine(root, "shipped.extension.json");
        File.WriteAllText(path, json.Replace($"\"{built.Version}\"", $"\"{version}\"", StringComparison.Ordinal));
        return path;
    }

    private sealed class AllTrust : ITrustPolicy
    {
        public bool IsTrusted(string directory, ExtensionManifest manifest) => true;
    }

    private sealed class ThrowingTrust : ITrustPolicy
    {
        public bool IsTrusted(string directory, ExtensionManifest manifest) => throw new InvalidOperationException("boom");
    }
}
