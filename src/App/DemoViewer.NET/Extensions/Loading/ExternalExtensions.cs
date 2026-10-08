#region

using System.Reflection;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>Which third-party extensions may load.</summary>
/// <param name="PublisherKeys">The keys whose signature makes a copy verified.</param>
/// <param name="AllowUnverified">The user's "Allow unverified and potentially dangerous extensions" setting.</param>
public sealed record ExternalTrust(IReadOnlyList<string> PublisherKeys, bool AllowUnverified);

/// <summary>What <see cref="ExternalExtensions.Resolve" /> loaded, and every copy it refused with the reason.</summary>
public sealed record ExternalResolution(IReadOnlyList<PackStatus> Loaded, IReadOnlyList<LoadOutcome> Rejected)
{
    /// <summary>Nothing installed, or nothing looked at.</summary>
    public static ExternalResolution None { get; } = new([], []);

    /// <summary>The loaded extensions.</summary>
    public IReadOnlyList<IExtension> Extensions => [.. Loaded.Select(s => s.Pack)];
}

/// <summary>
///     Finds and loads third-party extensions: every id under the extensions folder that is not one this app
///     ships. A copy loads only when it is compatible, its signature is not broken, it is verified or the
///     user allowed unverified extensions, it loads and probes cleanly, and nothing it declares collides with
///     what is already loaded. The newest such version of each id wins.
/// </summary>
public static class ExternalExtensions
{
    /// <summary>Ids under this prefix belong to the extensions this app ships; a third-party copy never takes one.</summary>
    public const string ReservedIdPrefix = "net.demoviewer.";

    /// <summary>The setting's label, verbatim.</summary>
    public const string AllowUnverifiedLabel = "Allow unverified and potentially dangerous extensions";

    /// <summary>How a copy's signature reads.</summary>
    public enum Trust
    {
        /// <summary>Signed by a publisher key and unchanged since.</summary>
        Verified,

        /// <summary>Unsigned, or signed by a key this app does not know.</summary>
        Unverified,

        /// <summary>A signature that does not hold: malformed, wrong, or files changed after signing.</summary>
        Invalid
    }

    /// <summary>Resolves the third-party extensions installed under <paramref name="configRoot" />.</summary>
    /// <param name="configRoot">The config root; null (the browser) loads nothing.</param>
    /// <param name="shipped">The extensions this app ships, already resolved: their ids are not third-party, and nothing may collide with them.</param>
    /// <param name="host">What this app provides.</param>
    /// <param name="trust">The publisher keys and the user's setting.</param>
    public static ExternalResolution Resolve(string? configRoot, IReadOnlyList<IExtension> shipped, ExtensionHostInfo host,
        ExternalTrust trust)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(trust);
        if (ExtensionLoader.ExtensionsDirectory(configRoot) is not { } directory || !Directory.Exists(directory))
        {
            return ExternalResolution.None;
        }

        List<PackStatus> loaded = [];
        List<LoadOutcome> rejected = [];
        try
        {
            HashSet<string> shippedIds = new(shipped.Select(p => p.Id), StringComparer.Ordinal);
            ExtensionLoader.Discovery discovery = ExtensionLoader.Discover(directory);
            rejected.AddRange(discovery.Rejected
                .Where(o => !shippedIds.Contains(IdFolderOf(directory, o.Directory)))
                .Select(o => o with { External = true }));

            List<IExtension> accepted = [.. shipped];
            foreach (IGrouping<string, ExtensionCandidate> id in discovery.Candidates
                         .Where(c => !shippedIds.Contains(c.Manifest.Id))
                         .GroupBy(c => c.Manifest.Id, StringComparer.Ordinal)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (id.Key.StartsWith(ReservedIdPrefix, StringComparison.Ordinal))
                {
                    rejected.AddRange(id.Select(c => Outcome(c, LoadFailure.ReservedId,
                        $"the id '{id.Key}' is reserved for extensions this app ships")));
                    continue;
                }

                foreach (ExtensionCandidate candidate in id.OrderByDescending(c => c.Manifest.Version))
                {
                    (PackStatus? status, LoadOutcome? failure) = TryLoad(candidate, accepted, host, trust);
                    if (status is null)
                    {
                        rejected.Add(failure!);
                        continue;
                    }

                    accepted.Add(status.Pack);
                    loaded.Add(status);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            rejected.Add(new LoadOutcome(directory, null, LoadFailure.LoaderFailed, "the extension loader failed",
                $"{ex.GetType().Name}: {ex.Message}", External: true));
        }

        return new ExternalResolution(loaded, rejected);
    }

    /// <summary>Reads a copy's signature against <paramref name="publisherKeys" />.</summary>
    public static (Trust Trust, string? Detail) Classify(string directory, IReadOnlyList<string> publisherKeys)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(publisherKeys);
        try
        {
            SignatureCheck check = ExtensionSignature.Verify(directory, publisherKeys);
            return check switch
            {
                { Verified: true } => (Trust.Verified, null),
                { Failure: SignatureFailure.Missing or SignatureFailure.UnknownKey } => (Trust.Unverified, null),
                _ => (Trust.Invalid, check.Detail)
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (Trust.Invalid, ex.Message);
        }
    }

    private static (PackStatus? Status, LoadOutcome? Failure) TryLoad(ExtensionCandidate candidate,
        IReadOnlyList<IExtension> accepted, ExtensionHostInfo host, ExternalTrust trust)
    {
        ExtensionManifest m = candidate.Manifest;
        PackCompatibility compatibility = PackCompatibility.Check(m, host);
        if (!compatibility.IsCompatible)
        {
            return (null, Outcome(candidate, LoadFailure.Incompatible, compatibility.Describe(m, m.Id)!));
        }

        (Trust verdict, string? detail) = Classify(candidate.Directory, trust.PublisherKeys);
        if (verdict == Trust.Invalid)
        {
            return (null, Outcome(candidate, LoadFailure.Untrusted, detail ?? "signature invalid"));
        }

        if (verdict == Trust.Unverified && !trust.AllowUnverified)
        {
            return (null, Outcome(candidate, LoadFailure.Unverified,
                $"it is not verified; turn on \"{AllowUnverifiedLabel}\" in Settings to load it"));
        }

        ExtensionLoader.LoadResult result = ExtensionLoader.Load(candidate, new ExternalLoadContext(candidate));
        if (result.Failure is { } loadFailure)
        {
            return (null, loadFailure with { External = true });
        }

        IExtension pack = result.Pack!;
        LoadOutcome? failure = CheckSdk(candidate, pack.GetType().Assembly)
                               ?? CheckAppReference(candidate, pack.GetType().Assembly)
                               ?? ExtensionLoader.Probe(candidate, pack)
                               ?? Conflicts(candidate, accepted, pack);
        if (failure is not null)
        {
            return (null, failure with { External = true });
        }

        PackStatus status = PackStatus.Evaluate(pack, host) with
        {
            Source = new PackSource.External(candidate.Directory, verdict == Trust.Verified)
        };
        return (status, null);
    }

    // The app's assembly is shared with every extension's load context, so an extension built against it would
    // bind to the live composition root, catalog and registries. Only the SDK packages are a contract.
    internal static LoadOutcome? CheckAppReference(ExtensionCandidate candidate, Assembly assembly)
    {
        string[] app = [.. new[] { typeof(App).Assembly, Assembly.GetEntryAssembly() }
            .Where(a => a is not null)
            .Select(a => a!.GetName().Name!)];
        string? referenced = assembly.GetReferencedAssemblies()
            .Select(r => r.Name)
            .FirstOrDefault(n => n is not null && app.Contains(n, StringComparer.OrdinalIgnoreCase));
        return referenced is null
            ? null
            : Outcome(candidate, LoadFailure.ReferencesApp,
                $"it references the app's own {referenced}.dll; an extension builds against the SDK packages only");
    }

    // The SDK assembly version is Major.0.0.0, so a different major is a contract the host does not implement.
    private static LoadOutcome? CheckSdk(ExtensionCandidate candidate, Assembly assembly)
    {
        AssemblyName sdk = typeof(IExtension).Assembly.GetName();
        AssemblyName? built = assembly.GetReferencedAssemblies()
            .FirstOrDefault(r => string.Equals(r.Name, sdk.Name, StringComparison.Ordinal));
        if (built?.Version is not { } builtVersion || sdk.Version is not { } running || builtVersion.Major == running.Major)
        {
            return null;
        }

        return Outcome(candidate, LoadFailure.ReferenceMismatch,
            $"it was built against extension SDK {builtVersion.Major}.x; this app provides {running.Major}.x");
    }

    // Composes the catalogs the app builds at startup with the candidate added, so a collision refuses the
    // candidate here instead of failing the app's own composition.
    private static LoadOutcome? Conflicts(ExtensionCandidate candidate, IReadOnlyList<IExtension> accepted, IExtension pack)
    {
        if (accepted.Any(a => string.Equals(a.FeatureId, pack.FeatureId, StringComparison.Ordinal)))
        {
            return Outcome(candidate, LoadFailure.Conflicts, $"its feature id '{pack.FeatureId}' is already taken");
        }

        IExtension[] together = [.. accepted, pack];
        try
        {
            _ = FeatureCatalog.Build(together);
            _ = JobKindRegistry.Build(together);
            _ = CommandRegistry.Build(together);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return Outcome(candidate, LoadFailure.Conflicts, "it collides with an extension already loaded", ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The candidate's own Features, Commands or JobKinds getter threw.
            return Outcome(candidate, LoadFailure.ProbeFailed, "it failed while describing itself", ex.ToString());
        }
    }

    private static LoadOutcome Outcome(ExtensionCandidate candidate, LoadFailure failure, string detail, string? logDetail = null) =>
        new(candidate.Directory, candidate.Manifest, failure, detail, logDetail, External: true);

    // <extensions>/<id>/<version>: the id folder of a discovery outcome.
    private static string IdFolderOf(string extensionsDirectory, string path)
    {
        string relative = Path.GetRelativePath(extensionsDirectory, path);
        int separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator < 0 ? relative : relative[..separator];
    }
}
