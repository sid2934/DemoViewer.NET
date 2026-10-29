#region

using System.Globalization;
using System.Security.Cryptography;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.TestSupport.Corpus;

/// <summary>
///     Resolves a manifest demo to a local path, fetching it from a configured source on first use.
///     <para>
///         <b>Resolution order</b>, first hit wins:
///         <list type="number">
///             <item>the search directories handed in (the existing <c>demos/</c> discovery), probed by name;</item>
///             <item>the fetch cache, <c>&lt;cacheRoot&gt;/&lt;sha256&gt;/&lt;file&gt;</c>, a hit when the file exists at the manifest's length;</item>
///             <item>each source in order, copied down and verified.</item>
///         </list>
///         Steps 2 and 3 apply only to names the manifest carries. With no sources configured the
///         whole thing costs one <see cref="File.Exists" /> per search directory, which is what the
///         repo does today, so an unconfigured checkout behaves exactly as before.
///     </para>
///     <para>
///         <b>Eviction: none, by decision.</b> The corpus is 7.5 GB against 46 GB free, and the
///         cache holds only demos the manifest names, so every byte in it is re-fetchable and
///         nothing original lives there. Reclaiming space is <c>rm -rf</c> on the cache root by
///         hand. The layer itself has no delete path for a whole demo, which is the point: it owns
///         no code that could remove a corpus file.
///     </para>
///     <para>
///         This type is constructed from explicit inputs and reads no environment. The process-wide
///         default, built from the environment, is <see cref="DemoTestHelper" />'s.
///     </para>
/// </summary>
public sealed class DemoCorpus
{
    /// <summary>Resolutions already made in this process, so a second ask costs nothing.</summary>
    private readonly Dictionary<string, Lazy<Resolution>> _resolved = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _resolvedLock = new();
    private readonly string? _corpusRoot;
    private readonly IReadOnlyList<string> _searchDirs;
    private readonly IReadOnlyList<IDemoSource> _sources;

    /// <param name="manifest">The entries that may be fetched. A name outside it is never fetched.</param>
    /// <param name="searchDirs">Existing locations, probed before the cache. Usually the repo's demo directories.</param>
    /// <param name="cacheRoot">Where fetched demos land. Never inside the corpus.</param>
    /// <param name="sources">Sources in priority order. Empty means fetching is off, which is the default.</param>
    /// <param name="corpusRoot">
    ///     The corpus root, when there is one. A manifest demo is also probed at its own
    ///     <see cref="CorpusDemo.Dir" /> under it, which the flat search directories cannot reach
    ///     (<c>pro-demos/</c>).
    /// </param>
    public DemoCorpus(
        CorpusManifest manifest,
        IEnumerable<string> searchDirs,
        string cacheRoot,
        IEnumerable<IDemoSource> sources,
        string? corpusRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        Manifest = manifest;
        _searchDirs = [.. searchDirs];
        CacheRoot = cacheRoot;
        _sources = [.. sources];
        _corpusRoot = corpusRoot;
    }

    /// <summary>The manifest this corpus resolves against.</summary>
    public CorpusManifest Manifest { get; }

    /// <summary>Where fetched demos are cached.</summary>
    public string CacheRoot { get; }

    /// <summary>Whether any source is configured. False means no fetch is ever attempted.</summary>
    public bool CanFetch => _sources.Count > 0;

    /// <summary>
    ///     The local path for <paramref name="fileName" />, or null with
    ///     <paramref name="problem" /> explaining what was tried.
    ///     <para>
    ///         Throws <see cref="DemoCorpusIntegrityException" /> when a fetched demo's bytes are
    ///         not the manifest's, and <see cref="DemoCorpusConfigurationException" /> when a
    ///         configured source cannot serve anything and no other source held the demo. Both are
    ///         failures rather than skips: a skip is green, and nobody reads a green lane.
    ///     </para>
    /// </summary>
    public string? TryResolve(string fileName, out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        string key = Path.GetFileName(fileName);

        Lazy<Resolution> lazy;
        lock (_resolvedLock)
        {
            if (!_resolved.TryGetValue(key, out lazy!))
            {
                // One fetch per demo per process even when classes race. The work happens outside
                // this lock, so an unrelated lookup never queues behind a multi-GB copy.
                lazy = new Lazy<Resolution>(() => Resolve(key), LazyThreadSafetyMode.ExecutionAndPublication);
                _resolved[key] = lazy;
            }
        }

        Resolution resolution = lazy.Value;
        problem = resolution.Problem;
        return resolution.Path;
    }

    /// <summary>
    ///     The local path for <paramref name="fileName" />, or a <see cref="SkipTestException" />
    ///     naming the demo and every source tried. Never returns null and never returns silently:
    ///     an early <c>return</c> in a caller's place would report the test as succeeded.
    /// </summary>
    public string Require(string fileName)
    {
        string? path = TryResolve(fileName, out string? problem);
        return path ?? throw new SkipTestException(problem ?? $"demo '{fileName}' is not available");
    }

    /// <summary>
    ///     The members of a manifest set in ordinal file-name order. Several callers index into the
    ///     result by position, so this order must not change: it is the order
    ///     <c>Directory.EnumerateFiles(..).Order(StringComparer.Ordinal)</c> gave them. An unknown
    ///     set is empty.
    /// </summary>
    public IReadOnlyList<CorpusDemo> SetMembers(string set) =>
        [.. Manifest.Set(set).OrderBy(d => d.File, StringComparer.Ordinal)];

    /// <summary>
    ///     The local path of the <paramref name="index" />th member of a set, fetching only that
    ///     member. Null with <paramref name="problem" /> when the set has no such member or the
    ///     member cannot be obtained.
    /// </summary>
    public string? TryResolveSetMember(string set, int index, out string? problem)
    {
        IReadOnlyList<CorpusDemo> members = SetMembers(set);
        if (members.Count == 0)
        {
            problem = $"corpus set '{set}' is unknown or empty in the manifest";
            return null;
        }

        if (index < 0 || index >= members.Count)
        {
            problem = $"fewer than {index + 1} demos in corpus set '{set}' (the manifest lists {members.Count})";
            return null;
        }

        string? path = TryResolve(members[index].File, out string? why);
        problem = path is null ? $"corpus set '{set}' member {index}: {why}" : null;
        return path;
    }

    /// <summary>
    ///     Every member of a set, in <see cref="SetMembers" /> order, fetching each as needed.
    ///     Null with <paramref name="problem" /> naming the first member that cannot be obtained.
    /// </summary>
    public string[]? TryResolveSet(string set, out string? problem)
    {
        IReadOnlyList<CorpusDemo> members = SetMembers(set);
        if (members.Count == 0)
        {
            problem = $"corpus set '{set}' is unknown or empty in the manifest";
            return null;
        }

        string[] paths = new string[members.Count];
        for (int i = 0; i < members.Count; i++)
        {
            string? path = TryResolveSetMember(set, i, out problem);
            if (path is null)
            {
                return null;
            }

            paths[i] = path;
        }

        problem = null;
        return paths;
    }

    /// <summary><see cref="TryResolveSetMember" /> or a <see cref="SkipTestException" /> carrying its reason.</summary>
    public string RequireSetMember(string set, int index) =>
        TryResolveSetMember(set, index, out string? problem)
        ?? throw new SkipTestException(problem ?? $"corpus set '{set}' is not available");

    /// <summary><see cref="TryResolveSet" /> or a <see cref="SkipTestException" /> carrying its reason.</summary>
    public string[] RequireSet(string set) =>
        TryResolveSet(set, out string? problem)
        ?? throw new SkipTestException(problem ?? $"corpus set '{set}' is not available");

    /// <summary>The cache path a demo takes. Content addressed, so two demos never collide.</summary>
    public string CachePath(CorpusDemo demo) =>
        Path.Combine(CacheRoot, demo.Sha256 ?? "unpinned-" + demo.Name, demo.File);

    /// <summary>Lower-case hex SHA-256 of a file's bytes, streamed rather than buffered.</summary>
    public static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private Resolution Resolve(string fileName)
    {
        // 1. Existing locations, by name. This is the only step an unconfigured checkout reaches.
        foreach (string dir in _searchDirs)
        {
            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return new Resolution(candidate, null);
            }
        }

        CorpusDemo? demo = Manifest.ByFile(fileName);
        if (demo is not null && _corpusRoot is not null)
        {
            string home = demo.PathUnder(_corpusRoot);
            if (File.Exists(home))
            {
                return new Resolution(home, null);
            }
        }

        if (demo is null)
        {
            return new Resolution(null,
                $"demo '{fileName}' is not in the corpus manifest, so it cannot be fetched. " +
                "Place it under the corpus root or under TestData/ next to the test assembly, or add " +
                "it to src/Testing/DemoViewer.NET.TestSupport/corpus.manifest.json to make it fetchable.");
        }

        // 2. Cache hit. Length alone, so a warm cache costs no reading.
        string cached = CachePath(demo);
        if (File.Exists(cached) && new FileInfo(cached).Length == demo.Bytes)
        {
            return new Resolution(cached, null);
        }

        // 3. Fetch.
        if (!CanFetch)
        {
            return new Resolution(null,
                $"demo '{demo.Name}' ({fileName}, {Mb(demo.Bytes)}) is not present and no demo source " +
                $"is configured. Set {DemoTestHelper.CorpusRootEnvVar} to a corpus root, or " +
                $"{DemoTestHelper.DemoSourcesEnvVar} to a ';'-separated list of directories, file:// " +
                $"URIs or https://host/SourceName instances to fetch from.");
        }

        return Fetch(demo, cached);
    }

    private Resolution Fetch(CorpusDemo demo, string cached)
    {
        if (File.Exists(cached))
        {
            // The cache hit above already rejected this file on length, so it is corrupt. Refuse
            // rather than overwrite: this layer owns no code that removes a cached demo.
            return new Resolution(null,
                $"demo '{demo.Name}' has a cached file of the wrong length at {cached}: " +
                $"{new FileInfo(cached).Length} bytes, manifest says {demo.Bytes}. Remove it to re-fetch.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);

        List<string> tried = [];
        List<DemoCorpusConfigurationException> unusable = [];
        foreach (IDemoSource source in _sources)
        {
            // Unique per attempt, and in the destination directory, so the move is same-volume and
            // two processes fetching the same demo cannot write each other's partial file.
            string partial = cached + $".{Environment.ProcessId}-{Guid.NewGuid():N}.partial";
            try
            {
                if (!source.TryCopyTo(demo, partial))
                {
                    tried.Add($"{source.Description}: does not hold it");
                    continue;
                }

                Verify(demo, partial);

                try
                {
                    File.Move(partial, cached);
                }
                catch (IOException) when (File.Exists(cached) && new FileInfo(cached).Length == demo.Bytes)
                {
                    // Another process won the race, and its copy passed the same verification.
                    // The guard is load-bearing: without it any failed move would return an
                    // unverified path. Any other IOException falls through and is recorded.
                    File.Delete(partial);
                }

                return new Resolution(cached, null);
            }
            catch (DemoCorpusIntegrityException)
            {
                Discard(partial);
                throw;
            }
            catch (DemoCorpusConfigurationException ex)
            {
                // Held, not rethrown here. Sources are tried in order, and aborting on the first
                // unusable one would hide a later source that holds the demo. It is rethrown after
                // the loop when nothing served it, which is what keeps a dead credential from
                // reading as an ordinary miss and skipping the suite behind a green lane.
                Discard(partial);
                unusable.Add(ex);
                tried.Add($"{source.Description}: {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                Discard(partial);
                tried.Add($"{source.Description}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Nothing served it and at least one source cannot serve anything. That is configuration,
        // not an absent demo, so it fails the run rather than skipping it.
        if (unusable.Count == 1)
        {
            throw unusable[0];
        }

        if (unusable.Count > 1)
        {
            throw new DemoCorpusConfigurationException(
                $"demo '{demo.Name}' ({demo.File}): {unusable.Count} of {_sources.Count} configured "
                + "sources are unusable, and no other source held it. "
                + string.Join(" ", unusable.Select(e => e.Message)));
        }

        return new Resolution(null,
            $"demo '{demo.Name}' ({demo.File}, {Mb(demo.Bytes)}) could not be obtained from any " +
            $"configured source. Tried {_sources.Count}: " + string.Join("; ", tried));
    }

    private static void Verify(CorpusDemo demo, string partial)
    {
        long length = new FileInfo(partial).Length;
        if (length != demo.Bytes)
        {
            throw new DemoCorpusIntegrityException(
                $"demo '{demo.Name}' ({demo.File}) fetched {length} bytes, manifest says {demo.Bytes}");
        }

        if (demo.Sha256 is null)
        {
            return;
        }

        string actual = ComputeSha256(partial);
        if (!actual.Equals(demo.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new DemoCorpusIntegrityException(
                $"demo '{demo.Name}' ({demo.File}) failed SHA-256 verification. " +
                $"expected {demo.Sha256}, actual {actual}");
        }
    }

    private static void Discard(string partial)
    {
        try
        {
            File.Delete(partial);
        }
        catch (IOException)
        {
            // A leftover .partial is harmless: discovery matches on the exact file name.
        }
    }

    private static string Mb(long bytes) =>
        (bytes / 1024d / 1024d).ToString("F0", CultureInfo.InvariantCulture) + " MB";

    private readonly record struct Resolution(string? Path, string? Problem);
}
