#region

using CS2DemoKit.Parser;
using DemoViewer.NET.TestSupport.Corpus;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.TestSupport;

/// <summary>
///     Resolves CS2 <c>.dem</c> files for integration tests, and provides a
///     <see cref="RequireDemo()" /> helper that throws <see cref="SkipTestException" /> when no
///     demo is available, so TUnit reports the test under <c>skipped:</c> rather than the
///     misleading <c>succeeded:</c> count that an early <c>return</c> produces.
///     <para>
///         <b>Discovery order</b> (first match wins):
///         <list type="number">
///             <item>
///                 The <c>DEMO_PATH</c> environment variable, if it points at an existing file or
///                 folder. A folder (the Steam replays directory, read in place, is the usual one)
///                 resolves to ONE demo inside it, by the rules under
///                 <see cref="ResolveDemoPathOverride" />: <c>DEMO_PATH_PICK=&lt;filename&gt;</c> when
///                 set, else <see cref="ReferenceDemoFileName" /> when the folder holds it, else the
///                 <c>*.dem</c> whose file name sorts first by ordinal comparison. Top level only, no
///                 recursion. A path that resolves to nothing falls through to the next step, and
///                 <see cref="RequireDemo()" /> says why in its skip reason.
///             </item>
///             <item><see cref="ReferenceDemoFileName" />, via the pinned lookup below, when present.</item>
///             <item>The first <c>*.dem</c> under <c>TestData/</c> next to the test assembly.</item>
///             <item>
///                 The first <c>*.dem</c> under <c>&lt;repo-root&gt;/demos/benchmarks/</c> or
///                 <c>&lt;repo-root&gt;/demos/</c>.
///             </item>
///         </list>
///         The repo root is located by walking up from <see cref="AppContext.BaseDirectory" /> until
///         a folder containing <c>DemoViewer.NET.slnx</c> is found. No hard-coded personal paths.
///     </para>
///     <para>
///         The pinned lookup, <see cref="FindDemoPath(string)" />, never looks inside a folder
///         <c>DEMO_PATH</c>. The replays folder holds demos whose names match golden candidates
///         captured against a different file (see <c>demos/CORPUS.md</c> on
///         <c>match730_..._410.dem</c>), and pointing the pinned tests at it would silently re-point
///         those captures.
///     </para>
/// </summary>
public static class DemoTestHelper
{
    /// <summary>The developer override: a <c>.dem</c> file, or a folder of them.</summary>
    public const string DemoPathEnvVar = "DEMO_PATH";

    /// <summary>
    ///     The file name to take when <see cref="DemoPathEnvVar" /> is a folder. Ignored when it is a
    ///     file. A pick that is not in the folder resolves to nothing rather than to the first file,
    ///     because a run pinned to one demo must not quietly swap to another.
    /// </summary>
    public const string DemoPathPickEnvVar = "DEMO_PATH_PICK";

    /// <summary>
    ///     The corpus root, replacing <c>&lt;repo-root&gt;/demos</c> everywhere this class looks.
    ///     Unset means <c>&lt;repo-root&gt;/demos</c>, so the main checkout needs no configuration.
    ///     <para>
    ///         <c>demos/**/*.dem</c> is gitignored, so a git worktree's own <c>demos/</c> is empty
    ///         and every RealDemo test there skips. Configuration is the ONLY supported way to point
    ///         a second tree at the corpus: removing a tree that links <c>demos/</c> deletes the
    ///         link's target, not the link.
    ///     </para>
    ///     <para>
    ///         A set-but-invalid value fails the run rather than skipping it: someone who set the
    ///         variable meant to run those tests.
    ///     </para>
    /// </summary>
    public const string CorpusRootEnvVar = "DEMO_CORPUS";

    /// <summary>
    ///     Ordered, <c>';'</c>-separated list of directories or <c>file://</c> URIs to fetch absent
    ///     manifest demos from. Unset means no fetching, which is the default and is exactly the
    ///     repo's behaviour before the layer existed.
    ///     <para>
    ///         The separator is <c>';'</c> and not <see cref="Path.PathSeparator" />, which is
    ///         <c>':'</c> on Unix and would split a <c>file://</c> URI in half.
    ///     </para>
    /// </summary>
    public const string DemoSourcesEnvVar = "DEMO_SOURCES";

    /// <summary>
    ///     Where fetched demos are cached. Defaults to a per-user directory rather than anywhere
    ///     under the repo, so every worktree on a machine shares one copy instead of holding its
    ///     own, and so no write path of this layer points into <c>demos/</c>.
    /// </summary>
    public const string CorpusCacheEnvVar = "DEMO_CORPUS_CACHE";

    /// <summary>
    ///     Canonical "reference" demo for integration tests that need a
    ///     deterministic structural shape (5v5 MM, ~22 rounds, no OT). Pinned
    ///     here so every demo-agnostic test gets the SAME demo across runs and
    ///     machines, addressing audit S10's "first-found means whichever
    ///     contributor's TestData/ happened to be at the front of an
    ///     enumeration." The plain <see cref="FindDemoPath()" /> chain prefers
    ///     this demo when available before falling back to first-found.
    /// </summary>
    public const string ReferenceDemoFileName = "003816248937665266002_0544286934.dem";

    /// <summary>
    ///     Maximum number of <see cref="ParsedDemo" /> instances the process-wide cache retains.
    ///     The bound is load-bearing, not a tuning knob: the full App suite touches ~6 distinct
    ///     large demos, and an unbounded cache accumulates all of them for process lifetime,
    ///     enough to get the test process killed by the OS mid-suite on a memory-pressured
    ///     16 GB dev machine (measured: the suite process peaks ~4.7 GB with the machine's
    ///     compressor already holding ~8 GB). Capacity 1 caches only the current demo: the
    ///     reference demo shared by most classes stays hot across long class runs, and the
    ///     handful of pro-demo classes pay one re-parse each. Override with the
    ///     <c>DEMOVIEWER_TEST_PARSE_CACHE</c> env var on machines with more headroom.
    /// </summary>
    private static readonly int _parseCacheCapacity =
        int.TryParse(Environment.GetEnvironmentVariable("DEMOVIEWER_TEST_PARSE_CACHE"), out int cap)
        && cap >= 1
            ? cap
            : 1;

    /// <summary>Guards <see cref="_parseCacheMap" /> and <see cref="_parseCacheOrder" />.</summary>
    private static readonly Lock _parseCacheLock = new();

    /// <summary>LRU order for the parse cache: most recently used at the front.</summary>
    private static readonly LinkedList<(string Path, Lazy<ParsedDemo> Parse)> _parseCacheOrder = new();

    /// <summary>Path-keyed index into <see cref="_parseCacheOrder" />.</summary>
    private static readonly Dictionary<string, LinkedListNode<(string Path, Lazy<ParsedDemo> Parse)>>
        _parseCacheMap = new();

    /// <summary>
    ///     Returns the shared, cached <see cref="ParsedDemo" /> for <paramref name="path" />,
    ///     parsing it on first use. The result is shared across test classes and MUST be treated
    ///     as read-only: <see cref="ParsedDemo" /> exposes only immutable/read-only surface, and
    ///     stateful consumers (e.g. <c>EntityTracker</c>) build their own state from
    ///     <see cref="ParsedDemo.Frames" />. Tests that need to mutate parser output (or hold the
    ///     raw demo bytes alongside) should keep a private <c>DemoParser.Parse</c> call instead.
    ///     The cache is a small LRU (see <see cref="_parseCacheCapacity" />). An evicted demo is
    ///     re-parsed on next use, and callers still holding an evicted instance keep it alive
    ///     until they finish (eviction only drops the cache's reference, so sharing stays safe).
    /// </summary>
    public static ParsedDemo GetOrParse(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Lazy<ParsedDemo> parse;
        bool evicted = false;
        lock (_parseCacheLock)
        {
            if (_parseCacheMap.TryGetValue(fullPath, out LinkedListNode<(string Path, Lazy<ParsedDemo> Parse)>? node))
            {
                _parseCacheOrder.Remove(node);
                _parseCacheOrder.AddFirst(node);
                parse = node.Value.Parse;
            }
            else
            {
                parse = new Lazy<ParsedDemo>(
                    () => DemoParser.Parse(File.ReadAllBytes(fullPath).AsMemory()),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                LinkedListNode<(string, Lazy<ParsedDemo>)> fresh = new((fullPath, parse));
                _parseCacheOrder.AddFirst(fresh);
                _parseCacheMap[fullPath] = fresh;
                while (_parseCacheMap.Count > _parseCacheCapacity)
                {
                    LinkedListNode<(string Path, Lazy<ParsedDemo> Parse)> evict = _parseCacheOrder.Last!;
                    _parseCacheOrder.RemoveLast();
                    _parseCacheMap.Remove(evict.Value.Path);
                    evicted = true;
                }
            }
        }

        if (evicted)
        {
            // Decommit the evicted demo BEFORE parsing the next one: without this the old
            // multi-GB ParsedDemo is garbage-but-resident exactly while the new parse
            // allocates its own, the peak that gets the process OS-killed on a
            // memory-pressured machine.
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
        }

        // Parse OUTSIDE the lock: Lazy(ExecutionAndPublication) still guarantees exactly one
        // parse per cached entry when classes race, without serializing unrelated lookups
        // behind a multi-second parse.
        return parse.Value;
    }

    /// <summary>
    ///     Locates a demo file via the discovery order in the class summary. Returns <c>null</c>
    ///     when nothing matches. Prefer <see cref="RequireDemo()" /> in tests so the missing-demo
    ///     state surfaces as a skip rather than a misleading pass.
    /// </summary>
    public static string? FindDemoPath()
    {
        // 1. Explicit env var (developer override, always wins). A folder was silently ignored
        // here until the folder rules moved into ResolveDemoPathOverride; 119 RealDemo tests
        // skipped under a folder DEMO_PATH while looking like a configured run.
        string? env = ResolveDemoPathOverride(
            Environment.GetEnvironmentVariable(DemoPathEnvVar),
            Environment.GetEnvironmentVariable(DemoPathPickEnvVar),
            out _);
        if (env is not null)
        {
            return env;
        }

        // 2. Reference demo, if available. Pinning to a specific filename when
        // possible gives tests a deterministic structural shape (addresses
        // audit S10). Falls through to first-found if the reference isn't
        // present locally.
        string? reference = FindDemoPath(ReferenceDemoFileName);
        if (reference is not null)
        {
            return reference;
        }

        // 3. TestData/ next to the test assembly.
        string testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        if (Directory.Exists(testData))
        {
            string? first = Directory.EnumerateFiles(testData, "*.dem").FirstOrDefault();
            if (first is not null)
            {
                return first;
            }
        }

        // 4. <repo-root>/demos/benchmarks/ then <repo-root>/demos/.
        foreach (string dir in RepoRelativeDemoDirs())
        {
            string? first = Directory.EnumerateFiles(dir, "*.dem").FirstOrDefault();
            if (first is not null)
            {
                return first;
            }
        }

        return null;
    }

    /// <summary>
    ///     Resolves the <c>DEMO_PATH</c> override on its own, with the process environment passed in
    ///     rather than read, so the rules can be tested against temp folders without touching the
    ///     env var every other test in the process reads. A file is returned as given. A folder
    ///     yields <paramref name="pick" /> when set, else <see cref="ReferenceDemoFileName" /> when
    ///     present, else the <c>*.dem</c> whose name sorts first by ordinal comparison; the ordinal
    ///     sort is what keeps the pick identical across machines and cultures. Returns <c>null</c>
    ///     when <paramref name="demoPath" /> is unset or resolves to nothing, and then
    ///     <paramref name="problem" /> says why for every case except unset.
    /// </summary>
    public static string? ResolveDemoPathOverride(string? demoPath, string? pick, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(demoPath))
        {
            return null;
        }

        if (File.Exists(demoPath))
        {
            return demoPath;
        }

        if (!Directory.Exists(demoPath))
        {
            problem = $"{DemoPathEnvVar} does not exist: {demoPath}";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(pick))
        {
            string picked = Path.Combine(demoPath, pick);
            if (File.Exists(picked))
            {
                return picked;
            }

            problem = $"{DemoPathPickEnvVar} '{pick}' is not a file under the {DemoPathEnvVar} folder: {demoPath}";
            return null;
        }

        string reference = Path.Combine(demoPath, ReferenceDemoFileName);
        if (File.Exists(reference))
        {
            return reference;
        }

        // The extension check is belt and braces over the "*.dem" pattern: the replays folder
        // keeps a ".dem.info" beside every demo, and a pick that landed on one would parse as garbage.
        string? first = Directory.EnumerateFiles(demoPath, "*.dem")
            .Where(f => Path.GetExtension(f).Equals(".dem", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (first is null)
        {
            problem = $"{DemoPathEnvVar} folder holds no .dem: {demoPath}";
        }

        return first;
    }

    /// <summary>
    ///     Locates a specific demo by filename. Used by oracle tests that need a deterministic
    ///     known-good demo (e.g. <c>furia-vs-vitality-m1-mirage.dem</c>). Search is recursive
    ///     under <c>TestData/</c> and the repo-root demo directories.
    /// </summary>
    public static string? FindDemoPath(string filename)
    {
        // 1. TestData/<filename>
        string testDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", filename);
        if (File.Exists(testDataPath))
        {
            return testDataPath;
        }

        // 2. Recursive search under the corpus directories
        foreach (string dir in RepoRelativeDemoDirs())
        {
            string? match = Directory.EnumerateFiles(dir, filename, SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        // 3. The fetch layer: an already-cached copy, else a fetch from a configured source. With
        // no DEMO_SOURCES this costs two File.Exists and never reaches a network.
        return Corpus.TryResolve(filename, out _);
    }

    /// <summary>
    ///     Returns a demo path or throws <see cref="SkipTestException" />. Tests should call this
    ///     as the first line: any test that reaches the assertion stage is guaranteed to have a
    ///     real demo. TUnit catches the exception and reports the test under <c>skipped:</c>.
    /// </summary>
    public static string RequireDemo()
    {
        string? path = FindDemoPath();
        if (path is not null)
        {
            return path;
        }

        // Re-run the override alone for its diagnosis: a folder DEMO_PATH that resolved to
        // nothing is a configuration mistake, and the skip reason is the only place it shows.
        ResolveDemoPathOverride(
            Environment.GetEnvironmentVariable(DemoPathEnvVar),
            Environment.GetEnvironmentVariable(DemoPathPickEnvVar),
            out string? problem);
        ThrowIfCorpusRootMisconfigured();
        ResolveCorpusRoot(out string? corpusProblem);
        string why = string.Join("; ", new[] { problem, corpusProblem }.Where(p => p is not null));
        throw new SkipTestException(
            $"No CS2 demo available{(why.Length == 0 ? "" : $" ({why})")}. " +
            $"Set the {DemoPathEnvVar} env var to a .dem file or a folder of them " +
            $"(optionally {DemoPathPickEnvVar}=<filename>), set {CorpusRootEnvVar} to a corpus root, " +
            "place one under TestData/ next to the test assembly, " +
            "or under <corpus-root>/benchmarks/ or <corpus-root>/.");
    }

    /// <summary>
    ///     Returns the path to a specific demo by filename or throws <see cref="SkipTestException" />.
    ///     Use this for oracle/regression tests pinned to a known reference demo. A manifest demo
    ///     that a configured source holds is fetched here; one whose bytes do not match the
    ///     manifest throws <see cref="DemoCorpusIntegrityException" /> and FAILS the test.
    /// </summary>
    public static string RequireDemo(string filename)
    {
        string testDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", filename);
        if (File.Exists(testDataPath))
        {
            return testDataPath;
        }

        foreach (string dir in RepoRelativeDemoDirs())
        {
            string? match = Directory.EnumerateFiles(dir, filename, SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        // The layer's own reason names the demo, its size and every source tried, which a generic
        // "was not found" cannot.
        return Corpus.Require(filename);
    }

    /// <summary>
    ///     Walks up from <see cref="AppContext.BaseDirectory" /> until it finds a directory
    ///     containing <c>DemoViewer.NET.slnx</c>, with no depth cap. Returns <c>null</c> if there is
    ///     none. The slnx is the sentinel on purpose: a <c>.git</c> match resolves to a different
    ///     directory inside a worktree. Tests that need the repo call <see cref="RequireRepoRoot" />.
    /// </summary>
    // Restated in tests/shared/TestTiers.cs for the suites that reference no test-support assembly.
    // Keep the two walks identical.
    public static string? FindRepoRoot()
    {
        string? dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "DemoViewer.NET.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    ///     The repo root, or a <see cref="SkipTestException" />. For a source-reading test, where a
    ///     detached binary is a legitimate case to shrug off: it reports skipped, and never passes on
    ///     an empty input. A release gate wants <see cref="RequireRepoRootOrFail" /> instead.
    /// </summary>
    public static string RequireRepoRoot() =>
        FindRepoRoot() ?? throw new SkipTestException(
            $"repo root not found: no DemoViewer.NET.slnx above {AppContext.BaseDirectory}");

    /// <summary>
    ///     The repo root, or an <see cref="InvalidOperationException" />. A release gate must fail, not
    ///     skip: a runner that exits 0 because every case skipped has asserted nothing, and the artefact
    ///     ships anyway.
    /// </summary>
    public static string RequireRepoRootOrFail(string gate) =>
        FindRepoRoot() ?? throw new InvalidOperationException(
            $"{gate} is a release gate and needs the checkout: no DemoViewer.NET.slnx above {AppContext.BaseDirectory}");

    // ── The corpus root, and the override that lets a worktree reach it ───────

    /// <summary>
    ///     The corpus root, with the environment passed in rather than read, so the rules can be
    ///     tested against temp folders without touching the variable every other test in the
    ///     process reads. <paramref name="configured" /> wins when set, else
    ///     <c>&lt;repoRoot&gt;/demos</c>.
    ///     <para>
    ///         A <paramref name="configured" /> root that is not a directory resolves to NOTHING and
    ///         never falls back to <c>&lt;repoRoot&gt;/demos</c>. A fallback would let a
    ///         misconfigured run resolve demos and report as though it were configured.
    ///     </para>
    /// </summary>
    public static string? ResolveCorpusRoot(string? configured, string? repoRoot, out string? problem)
    {
        problem = null;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Directory.Exists(configured))
            {
                return configured;
            }

            problem = $"{CorpusRootEnvVar} is set but is not a directory: {configured}";
            return null;
        }

        if (repoRoot is null)
        {
            problem = $"repo root not found above {AppContext.BaseDirectory} and {CorpusRootEnvVar} is unset";
            return null;
        }

        string demos = Path.Combine(repoRoot, "demos");
        if (Directory.Exists(demos))
        {
            return demos;
        }

        problem = $"no corpus: {demos} does not exist and {CorpusRootEnvVar} is unset";
        return null;
    }

    /// <summary><see cref="ResolveCorpusRoot(string?,string?,out string?)" /> against the process environment.</summary>
    public static string? ResolveCorpusRoot(out string? problem) =>
        ResolveCorpusRoot(Environment.GetEnvironmentVariable(CorpusRootEnvVar), FindRepoRoot(), out problem);

    /// <summary>
    ///     A directory under the corpus root, or null when the corpus or the directory is absent.
    ///     Call this instead of composing <c>FindRepoRoot() + "demos"</c>: a composed path cannot
    ///     see <see cref="CorpusRootEnvVar" />, so a worktree gets its own empty directory.
    /// </summary>
    /// <param name="subdirectory">Corpus-relative directory, e.g. <c>"benchmarks"</c>; null for the root.</param>
    public static string? CorpusDir(string? subdirectory = null)
    {
        string? root = ResolveCorpusRoot(out _);
        if (root is null)
        {
            return null;
        }

        string dir = subdirectory is null ? root : Path.Combine(root, subdirectory);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    ///     <see cref="CorpusDir" /> or a <see cref="SkipTestException" /> carrying the reason. For
    ///     tests that read a whole corpus directory, where an empty path silently selects zero
    ///     demos and passes. A set-but-invalid <see cref="CorpusRootEnvVar" /> throws
    ///     <see cref="DemoCorpusConfigurationException" /> instead: that is a failure.
    /// </summary>
    public static string RequireCorpusDir(string? subdirectory = null) =>
        RequireCorpusDir(subdirectory, Environment.GetEnvironmentVariable(CorpusRootEnvVar), FindRepoRoot());

    /// <summary><see cref="RequireCorpusDir(string?)" /> with the environment passed in.</summary>
    public static string RequireCorpusDir(string? subdirectory, string? configured, string? repoRoot)
    {
        ThrowIfCorpusRootMisconfigured(configured);
        string? root = ResolveCorpusRoot(configured, repoRoot, out string? problem);
        if (root is null)
        {
            throw new SkipTestException(problem ?? "no demo corpus available");
        }

        string dir = subdirectory is null ? root : Path.Combine(root, subdirectory);
        return Directory.Exists(dir)
            ? dir
            : throw new SkipTestException($"corpus directory does not exist: {dir}");
    }

    /// <summary>
    ///     Throws <see cref="DemoCorpusConfigurationException" /> when <see cref="CorpusRootEnvVar" />
    ///     is set to something that is not a directory. Unset is fine: that is an unconfigured run.
    /// </summary>
    public static void ThrowIfCorpusRootMisconfigured() =>
        ThrowIfCorpusRootMisconfigured(Environment.GetEnvironmentVariable(CorpusRootEnvVar));

    /// <summary><see cref="ThrowIfCorpusRootMisconfigured()" /> with the value passed in.</summary>
    public static void ThrowIfCorpusRootMisconfigured(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && !Directory.Exists(configured))
        {
            throw new DemoCorpusConfigurationException(
                $"{CorpusRootEnvVar} is set but is not a directory: {configured}");
        }
    }

    // ── Sets: the manifest's groups, resolved to files ────────────────────────

    /// <summary>
    ///     The <paramref name="which" />th demo of a manifest set in ordinal file-name order, from a
    ///     local corpus, the fetch cache or a configured source, in that order. Fetches only that
    ///     demo. Skips, naming the cause, when the set is short or the demo cannot be obtained; fails
    ///     on a set-but-invalid <see cref="CorpusRootEnvVar" /> or on bytes that are not the
    ///     manifest's. Needs no local corpus when <see cref="DemoSourcesEnvVar" /> is configured.
    /// </summary>
    public static string RequireSetDemo(string set, int which)
    {
        ThrowIfCorpusRootMisconfigured();
        return Corpus.RequireSetMember(set, which);
    }

    /// <summary>
    ///     <see cref="RequireSetDemo" /> that reports instead of skipping, for callers with a
    ///     fallback. A misconfigured root and corrupt bytes still throw.
    /// </summary>
    public static string? TrySetDemo(string set, int which, out string? problem)
    {
        ThrowIfCorpusRootMisconfigured();
        return Corpus.TryResolveSetMember(set, which, out problem);
    }

    /// <summary>Every demo of a manifest set in ordinal file-name order, fetching each as needed.</summary>
    public static string[] RequireSet(string set)
    {
        ThrowIfCorpusRootMisconfigured();
        return Corpus.RequireSet(set);
    }

    // ── Folder-shaped DEMO_PATH, for tests that walk every demo ───────────────

    /// <summary>
    ///     Every <c>.dem</c> a folder-shaped <c>DEMO_PATH</c> names, in ordinal name order, with
    ///     the environment passed in rather than read. The folder counterpart of
    ///     <see cref="ResolveDemoPathOverride" />, which resolves to ONE demo. Tests that walk the
    ///     whole replays folder need the list and must take it from here, or they get neither the
    ///     ordinal order nor the sidecar guard below.
    ///     <para>
    ///         A file yields itself. A folder yields its top-level <c>.dem</c> files, no recursion.
    ///         Unset yields empty with no <paramref name="problem" />, which the caller reads as
    ///         "not configured" and handles its own way. Every other empty result sets
    ///         <paramref name="problem" />.
    ///     </para>
    /// </summary>
    public static string[] ResolveDemoFolderOverride(string? demoPath, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(demoPath))
        {
            return [];
        }

        if (File.Exists(demoPath))
        {
            return [demoPath];
        }

        if (!Directory.Exists(demoPath))
        {
            problem = $"{DemoPathEnvVar} does not exist: {demoPath}";
            return [];
        }

        // The extension check is belt and braces over the "*.dem" pattern: the replays folder keeps
        // a ".dem.info" beside every demo, and one of those parses as garbage.
        string[] demos =
        [
            .. Directory.EnumerateFiles(demoPath, "*.dem")
                .Where(f => Path.GetExtension(f).Equals(".dem", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
        ];
        if (demos.Length == 0)
        {
            problem = $"{DemoPathEnvVar} folder holds no .dem: {demoPath}";
        }

        return demos;
    }

    /// <summary>
    ///     <see cref="ResolveDemoFolderOverride" /> against the process environment.
    /// </summary>
    public static string[] DemoFolderFromEnvironment(out string? problem) =>
        ResolveDemoFolderOverride(Environment.GetEnvironmentVariable(DemoPathEnvVar), out problem);

    // ── The replays folder: DEMO_PATH read as a folder, for tests pinned to its files ─────

    /// <summary>
    ///     The folder <c>DEMO_PATH</c> names, or the folder of the file it names, with the value
    ///     passed in. Null with <paramref name="problem" /> when unset or missing.
    /// </summary>
    public static string? ResolveReplaysFolder(string? demoPath, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(demoPath))
        {
            problem = $"{DemoPathEnvVar} is not set; it names a .dem or the folder they were written against";
            return null;
        }

        if (Directory.Exists(demoPath))
        {
            return demoPath;
        }

        if (File.Exists(demoPath))
        {
            return Path.GetDirectoryName(Path.GetFullPath(demoPath));
        }

        problem = $"{DemoPathEnvVar} does not exist: {demoPath}";
        return null;
    }

    /// <summary>
    ///     A named demo out of the <c>DEMO_PATH</c> folder, or a skip naming the demo and the cause.
    ///     Never reads the corpus: these tests are pinned to files the corpus does not hold.
    /// </summary>
    public static string RequireReplayDemo(string fileName)
    {
        string? folder = ResolveReplaysFolder(Environment.GetEnvironmentVariable(DemoPathEnvVar), out string? problem);
        if (folder is null)
        {
            throw new SkipTestException($"{fileName}: {problem}");
        }

        string path = Path.Combine(folder, fileName);
        return File.Exists(path) ? path : throw new SkipTestException($"{fileName} is not in the {DemoPathEnvVar} folder: {folder}");
    }

    /// <summary>
    ///     Every <c>.dem</c> in the <c>DEMO_PATH</c> folder, ordinal name order, with the extension
    ///     guard. Skips when the folder is unset, missing or empty.
    /// </summary>
    public static string[] RequireReplayDemos()
    {
        string? folder = ResolveReplaysFolder(Environment.GetEnvironmentVariable(DemoPathEnvVar), out string? problem);
        if (folder is null)
        {
            throw new SkipTestException(problem ?? $"{DemoPathEnvVar} names no demo folder");
        }

        string[] demos = ResolveDemoFolderOverride(folder, out problem);
        return demos.Length > 0 ? demos : throw new SkipTestException(problem ?? $"no .dem in {folder}");
    }

    /// <summary>
    ///     The <paramref name="which" />th demo, ordinal name order, of a <c>DEMO_PATH</c> that names
    ///     a FOLDER, else null. A file-shaped <c>DEMO_PATH</c> yields null: the callers use this as a
    ///     stand-in for a whole set, and one demo is not a set.
    /// </summary>
    public static string? DemoPathFolderDemo(int which)
    {
        string? env = Environment.GetEnvironmentVariable(DemoPathEnvVar);
        if (string.IsNullOrWhiteSpace(env) || !Directory.Exists(env))
        {
            return null;
        }

        string[] demos = ResolveDemoFolderOverride(env, out _);
        return which >= 0 && which < demos.Length ? demos[which] : null;
    }

    // ── The fetch layer's process-wide default ────────────────────────────────

    /// <summary>
    ///     The process's <see cref="DemoCorpus" />, built once from the environment. Everything the
    ///     layer needs is configuration: <see cref="CorpusRootEnvVar" />,
    ///     <see cref="DemoSourcesEnvVar" /> and <see cref="CorpusCacheEnvVar" />. With none set it
    ///     has no sources, so it resolves out of the existing demo directories and never touches a
    ///     network.
    /// </summary>
    public static DemoCorpus Corpus => _corpus.Value;

    private static readonly Lazy<DemoCorpus> _corpus = new(BuildCorpus,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static DemoCorpus BuildCorpus() =>
        new(CorpusManifest.Embedded, RepoRelativeDemoDirs(), DefaultCacheRoot(), ConfiguredSources(),
            ResolveCorpusRoot(out _));

    private static string DefaultCacheRoot()
    {
        string? configured = Environment.GetEnvironmentVariable(CorpusCacheEnvVar);
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(PlatformCacheHome(), "DemoViewer.NET", "demo-corpus");
    }

    // Multi-GB and regenerable, so it must land in a cache location rather than an application-data
    // one. On macOS LocalApplicationData is ~/Library/Application Support, which Time Machine backs
    // up by default; ~/Library/Caches is excluded. On Windows AppData\Local is already correct.
    private static string PlatformCacheHome()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
        }

        if (OperatingSystem.IsLinux())
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            return !string.IsNullOrWhiteSpace(xdg)
                ? xdg
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private static IEnumerable<IDemoSource> ConfiguredSources()
    {
        string? configured = Environment.GetEnvironmentVariable(DemoSourcesEnvVar);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        return
        [
            .. configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(DirectoryDemoSource.FromSpecifier)
        ];
    }

    // ── Internal: repo-root and demo-directory resolution ─────────────────────

    private static IEnumerable<string> RepoRelativeDemoDirs()
    {
        string? corpusRoot = ResolveCorpusRoot(out _);
        if (corpusRoot is null)
        {
            yield break;
        }

        string benchmarks = Path.Combine(corpusRoot, "benchmarks");
        if (Directory.Exists(benchmarks))
        {
            yield return benchmarks;
        }

        yield return corpusRoot;
    }
}
