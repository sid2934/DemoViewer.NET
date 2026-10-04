#region

using System.Reflection;
using System.Runtime.Loader;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>
///     Chooses, per first-party pack, between the copy shipped beside the app and a newer copy staged under
///     <c>&lt;config root&gt;/extensions/&lt;id&gt;/&lt;version&gt;/</c>, and loads the staged one when it wins
///     (strat-book-plugin.md §7.8). Reads only; item 36 stages and cleans up. Nothing here throws for a bad
///     directory: every refusal is a <see cref="LoadOutcome" /> on the pack's <see cref="PackStatus" />,
///     and <see cref="Resolve" /> falls back to the shipped copy if the loader itself fails.
/// </summary>
public static class ExtensionLoader
{
    /// <summary>The directory under the config root that holds staged extensions.</summary>
    public const string ExtensionsDirectoryName = "extensions";

    /// <summary><c>&lt;configRoot&gt;/extensions</c>, or null on a host with no config root (the browser).</summary>
    public static string? ExtensionsDirectory(string? configRoot) =>
        configRoot is null ? null : Path.Combine(configRoot, ExtensionsDirectoryName);

    /// <summary>
    ///     Lists the staged directories under <paramref name="extensionsDirectory" /> whose manifest parses
    ///     and whose <c>&lt;id&gt;/&lt;version&gt;</c> folders equal the manifest's id and version, ordered by id
    ///     and then highest version first. A directory that fails is in <see cref="Discovery.Rejected" />
    ///     with the reason. A missing directory yields nothing.
    /// </summary>
    public static Discovery Discover(string extensionsDirectory)
    {
        ArgumentNullException.ThrowIfNull(extensionsDirectory);
        List<ExtensionCandidate> candidates = [];
        List<LoadOutcome> rejected = [];
        if (!Directory.Exists(extensionsDirectory))
        {
            return new Discovery(candidates, rejected);
        }

        string root = Path.GetFullPath(extensionsDirectory);
        foreach (string idDir in Directory.EnumerateDirectories(root))
        {
            if (!IsInside(root, idDir))
            {
                rejected.Add(new LoadOutcome(idDir, null, LoadFailure.PathEscapes, "the extension folder is a link or resolves outside the extensions folder"));
                continue;
            }

            foreach (string versionDir in Directory.EnumerateDirectories(idDir))
            {
                Inspect(root, idDir, versionDir, candidates, rejected);
            }
        }

        candidates.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Manifest.Id, b.Manifest.Id);
            return c != 0 ? c : b.Manifest.Version.CompareTo(a.Manifest.Version);
        });
        return new Discovery(candidates, rejected);
    }

    /// <summary>
    ///     Picks the staged copy of <paramref name="packId" /> to load: the highest version above
    ///     <paramref name="shippedVersion" /> that <see cref="PackCompatibility.Check" /> accepts and that
    ///     <paramref name="trust" /> allows. Every higher candidate that failed a check is in
    ///     <see cref="Selection.Rejected" />; candidates below the chosen one are not examined. No shipped
    ///     version means no staged copy can be shown to be newer, so none is chosen.
    /// </summary>
    public static Selection Select(
        IReadOnlyList<ExtensionCandidate> candidates, string packId, SemVersion? shippedVersion, ExtensionHostInfo host, ITrustPolicy trust)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(trust);
        List<LoadOutcome> rejected = [];
        foreach (ExtensionCandidate candidate in candidates
                     .Where(c => string.Equals(c.Manifest.Id, packId, StringComparison.Ordinal))
                     .OrderByDescending(c => c.Manifest.Version))
        {
            ExtensionManifest m = candidate.Manifest;
            if (shippedVersion is null)
            {
                rejected.Add(new LoadOutcome(candidate.Directory, m, LoadFailure.ShippedUnknown,
                    "the bundled extension's version could not be read, so the update cannot be shown to be newer"));
                continue;
            }

            if (m.Version <= shippedVersion)
            {
                rejected.Add(new LoadOutcome(candidate.Directory, m, LoadFailure.NotNewer,
                    $"the bundled extension is {shippedVersion}, which is not older"));
                continue;
            }

            PackCompatibility compatibility = PackCompatibility.Check(m, host);
            if (!compatibility.IsCompatible)
            {
                rejected.Add(new LoadOutcome(candidate.Directory, m, LoadFailure.Incompatible, compatibility.Describe(m, packId)!));
                continue;
            }

            bool trusted;
            string why = "the copy is not signed by this app's publisher";
            try
            {
                trusted = trust.IsTrusted(candidate.Directory, m);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                trusted = false;
                why = "the trust check failed: " + ex.Message;
            }

            if (!trusted)
            {
                rejected.Add(new LoadOutcome(candidate.Directory, m, LoadFailure.Untrusted, why));
                continue;
            }

            return new Selection(candidate, rejected);
        }

        return new Selection(null, rejected);
    }

    /// <summary>
    ///     Loads <paramref name="candidate" />'s assembly into <paramref name="context" /> (a fresh
    ///     <see cref="ExtensionLoadContext" /> when null), finds the manifest's entry type, constructs it
    ///     and checks that the pack it describes is the one on disk: same id, same embedded manifest version.
    ///     Every failure is a <see cref="LoadResult.Failure" />, including a corrupt file or a type that
    ///     fails to load.
    /// </summary>
    public static LoadResult Load(ExtensionCandidate candidate, AssemblyLoadContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ExtensionManifest m = candidate.Manifest;
        string dir = candidate.Directory;

        Assembly assembly;
        try
        {
            context ??= new ExtensionLoadContext(candidate);
            assembly = context.LoadFromAssemblyPath(candidate.AssemblyPath);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException or ArgumentException)
        {
            return LoadResult.Failed(new LoadOutcome(dir, m, LoadFailure.AssemblyLoadFailed, $"'{m.Assembly}' could not be loaded: {ex.Message}"));
        }

        Type? entry;
        try
        {
            entry = assembly.GetType(m.EntryType, throwOnError: false);
        }
        catch (Exception ex) when (ex is TypeLoadException or ReflectionTypeLoadException or FileLoadException or BadImageFormatException)
        {
            return LoadResult.Failed(new LoadOutcome(dir, m, LoadFailure.EntryTypeMissing, $"'{m.EntryType}' could not be loaded: {ex.Message}"));
        }

        if (entry is null)
        {
            return LoadResult.Failed(new LoadOutcome(dir, m, LoadFailure.EntryTypeMissing, $"'{m.Assembly}' has no type '{m.EntryType}'"));
        }

        if (!typeof(IFeaturePack).IsAssignableFrom(entry) || entry.IsAbstract)
        {
            return LoadResult.Failed(new LoadOutcome(dir, m, LoadFailure.NotAPack, $"'{m.EntryType}' is not an extension entry type"));
        }

        IFeaturePack pack;
        try
        {
            pack = Activator.CreateInstance(entry) as IFeaturePack
                ?? throw new MissingMethodException($"'{m.EntryType}' has no public parameterless constructor");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Exception cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return LoadResult.Failed(new LoadOutcome(dir, m, LoadFailure.NotAPack, $"'{m.EntryType}' could not be constructed: {cause.Message}"));
        }

        if (!string.Equals(pack.Id, m.Id, StringComparison.Ordinal))
        {
            return LoadResult.Failed(new LoadOutcome(dir, m, LoadFailure.IdentityMismatch, $"the loaded extension is '{pack.Id}', not '{m.Id}'"));
        }

        ExtensionManifest embedded;
        try
        {
            embedded = pack.Manifest ?? throw new ExtensionManifestException("the extension declares no manifest");
        }
        catch (ExtensionManifestException ex)
        {
            return LoadResult.Failed(new LoadOutcome(dir, m, LoadFailure.IdentityMismatch, "the extension's embedded manifest is invalid: " + ex.Message));
        }

        if (!string.Equals(embedded.Id, m.Id, StringComparison.Ordinal) || embedded.Version != m.Version)
        {
            return LoadResult.Failed(new LoadOutcome(dir, m, LoadFailure.IdentityMismatch,
                $"the assembly is {embedded.Id} {embedded.Version}, not {m.Id} {m.Version}"));
        }

        return LoadResult.Loaded(pack);
    }

    /// <summary>
    ///     The head's one call: for each shipped pack, the staged copy that <see cref="Select" /> picks and
    ///     <see cref="Load" /> loads, else the shipped one, judged by <see cref="PackStatus.Evaluate(IFeaturePack, ExtensionHostInfo)" />
    ///     and stamped with its <see cref="PackSource" /> and every rejected staged candidate. A null
    ///     <paramref name="configRoot" /> (the browser) means shipped only. A failure of the loader itself
    ///     is a <see cref="LoadFailure.LoaderFailed" /> outcome on the shipped pack, never an exception.
    ///     A shipped pack's <see cref="ShippedPack.Create" /> runs only when its shipped copy is the result.
    /// </summary>
    public static IReadOnlyList<PackStatus> Resolve(string? configRoot, IReadOnlyList<ShippedPack> shipped, ExtensionHostInfo host, ITrustPolicy trust)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(trust);
        string? extensionsDir = ExtensionsDirectory(configRoot);
        Discovery? discovery = null;
        PackStatus[] statuses = new PackStatus[shipped.Count];
        for (int i = 0; i < shipped.Count; i++)
        {
            ShippedPack s = shipped[i];
            IFeaturePack? staged = null;
            PackSource source = PackSource.Bundled;
            List<LoadOutcome> rejected = [];
            try
            {
                if (extensionsDir is not null)
                {
                    discovery ??= Discover(extensionsDir);
                    rejected.AddRange(discovery.Rejected.Where(o => BelongsTo(o, s.Id)));
                    Selection selection = Select(discovery.Candidates, s.Id, ReadShippedVersion(s), host, trust);
                    rejected.AddRange(selection.Rejected);
                    if (selection.Chosen is { } chosen)
                    {
                        LoadResult loaded = Load(chosen);
                        if (loaded.Pack is not null)
                        {
                            staged = loaded.Pack;
                            source = new PackSource.Staged(chosen.Directory);
                        }
                        else
                        {
                            rejected.Add(loaded.Failure!);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                staged = null;
                source = PackSource.Bundled;
                rejected.Add(new LoadOutcome(extensionsDir ?? string.Empty, null, LoadFailure.LoaderFailed, $"the extension loader failed: {ex.GetType().Name}: {ex.Message}"));
            }

            IFeaturePack pack = staged ?? s.Create();
            statuses[i] = PackStatus.Evaluate(pack, host) with { Source = source, Rejected = rejected };
        }

        return statuses;
    }

    // The shipped version comes from the manifest copied beside the DLL, never from the type: reading the
    // type would load the shipped assembly. Null when the file is missing or does not parse.
    private static SemVersion? ReadShippedVersion(ShippedPack shipped)
    {
        try
        {
            if (!File.Exists(shipped.ManifestPath))
            {
                return null;
            }

            ExtensionManifest manifest = ExtensionManifest.Parse(File.ReadAllText(shipped.ManifestPath));
            return string.Equals(manifest.Id, shipped.Id, StringComparison.Ordinal) ? manifest.Version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExtensionManifestException)
        {
            return null;
        }
    }

    private static bool BelongsTo(LoadOutcome outcome, string packId)
    {
        if (outcome.Manifest is not null)
        {
            return string.Equals(outcome.Manifest.Id, packId, StringComparison.Ordinal);
        }

        // Without a manifest the id folder is all there is: <extensions>/<id>/<version>, or <extensions>/<id>.
        string trimmed = Path.TrimEndingDirectorySeparator(outcome.Directory);
        string? parent = Path.GetDirectoryName(trimmed);
        return string.Equals(Path.GetFileName(trimmed), packId, StringComparison.Ordinal)
               || (parent is not null && string.Equals(Path.GetFileName(parent), packId, StringComparison.Ordinal));
    }

    private static void Inspect(string root, string idDir, string versionDir, List<ExtensionCandidate> candidates, List<LoadOutcome> rejected)
    {
        if (!IsInside(root, versionDir))
        {
            rejected.Add(new LoadOutcome(versionDir, null, LoadFailure.PathEscapes, "the version folder is a link or resolves outside the extensions folder"));
            return;
        }

        string manifestPath = Path.Combine(versionDir, ExtensionManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            rejected.Add(new LoadOutcome(versionDir, null, LoadFailure.ManifestInvalid, $"no {ExtensionManifest.FileName}"));
            return;
        }

        if (IsReparsePoint(manifestPath))
        {
            rejected.Add(new LoadOutcome(versionDir, null, LoadFailure.PathEscapes, $"{ExtensionManifest.FileName} is a link"));
            return;
        }

        ExtensionManifest manifest;
        try
        {
            manifest = ExtensionManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (ExtensionManifestException ex)
        {
            rejected.Add(new LoadOutcome(versionDir, null, LoadFailure.ManifestInvalid, ex.Message));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rejected.Add(new LoadOutcome(versionDir, null, LoadFailure.ManifestInvalid, $"{ExtensionManifest.FileName} could not be read: {ex.Message}"));
            return;
        }

        string idFolder = Path.GetFileName(idDir);
        string versionFolder = Path.GetFileName(versionDir);
        if (!string.Equals(idFolder, manifest.Id, StringComparison.Ordinal)
            || !string.Equals(versionFolder, manifest.Version.ToString(), StringComparison.Ordinal))
        {
            rejected.Add(new LoadOutcome(versionDir, manifest, LoadFailure.FolderMismatch,
                $"the folder is '{idFolder}/{versionFolder}' but the manifest says '{manifest.Id}/{manifest.Version}'"));
            return;
        }

        string assemblyPath = Path.Combine(versionDir, manifest.Assembly);
        if (File.Exists(assemblyPath) && IsReparsePoint(assemblyPath))
        {
            rejected.Add(new LoadOutcome(versionDir, manifest, LoadFailure.PathEscapes, $"'{manifest.Assembly}' is a link"));
            return;
        }

        candidates.Add(new ExtensionCandidate(versionDir, manifest));
    }

    // Same rule as PackDataRemover.ResolveSafe: the full path must sit strictly under root, and a reparse
    // point (symlink, junction) at this level is refused rather than followed.
    private static bool IsInside(string root, string path)
    {
        if (IsReparsePoint(path))
        {
            return false;
        }

        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.StartsWith(rootWithSeparator, StringComparison.Ordinal) && full.Length > rootWithSeparator.Length;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>What <see cref="Discover" /> found: the usable directories and the refused ones.</summary>
    public sealed record Discovery(IReadOnlyList<ExtensionCandidate> Candidates, IReadOnlyList<LoadOutcome> Rejected);

    /// <summary>What <see cref="Select" /> chose, and the higher candidates it refused on the way.</summary>
    public sealed record Selection(ExtensionCandidate? Chosen, IReadOnlyList<LoadOutcome> Rejected);

    /// <summary>What <see cref="Load" /> produced: exactly one of the two.</summary>
    public sealed record LoadResult(IFeaturePack? Pack, LoadOutcome? Failure)
    {
        internal static LoadResult Loaded(IFeaturePack pack) => new(pack, null);
        internal static LoadResult Failed(LoadOutcome failure) => new(null, failure);
    }
}
