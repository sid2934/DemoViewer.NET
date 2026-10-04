#region

using CS2DemoKit.Parser;
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

        // 2. Recursive search under the repo-root demo directories
        foreach (string dir in RepoRelativeDemoDirs())
        {
            string? match = Directory.EnumerateFiles(dir, filename, SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return null;
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
        string why = problem is null ? "" : $" ({problem})";
        throw new SkipTestException(
            $"No CS2 demo available{why}. Set the {DemoPathEnvVar} env var to a .dem file or a folder of them " +
            $"(optionally {DemoPathPickEnvVar}=<filename>), place one under TestData/ next to the test assembly, " +
            "or under <repo-root>/demos/benchmarks/ or <repo-root>/demos/.");
    }

    /// <summary>
    ///     Returns the path to a specific demo by filename or throws <see cref="SkipTestException" />.
    ///     Use this for oracle/regression tests pinned to a known reference demo.
    /// </summary>
    public static string RequireDemo(string filename) =>
        FindDemoPath(filename) ?? throw new SkipTestException(
            $"Required demo '{filename}' was not found. " +
            $"Place it under <repo-root>/demos/ (recursive lookup) or " +
            $"under TestData/ next to the test assembly.");

    /// <summary>
    ///     Walks up from <see cref="AppContext.BaseDirectory" /> until it finds a directory
    ///     containing <c>DemoViewer.NET.slnx</c>. Used to locate <c>demos/</c> relative to
    ///     the repo, not relative to the build output. Returns <c>null</c> if no slnx is
    ///     found within 8 levels (defensive cap; the test assembly is typically 5–6 levels deep).
    ///     <para>
    ///         Public because tests other than the demo locators need it: a source-reading architecture
    ///         or wiring test has to find the repo, not the build output.
    ///     </para>
    /// </summary>
    public static string? FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "DemoViewer.NET.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    // ── Internal: repo-root and demo-directory resolution ─────────────────────

    private static IEnumerable<string> RepoRelativeDemoDirs()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            yield break;
        }

        string benchmarks = Path.Combine(repoRoot, "demos", "benchmarks");
        if (Directory.Exists(benchmarks))
        {
            yield return benchmarks;
        }

        string demos = Path.Combine(repoRoot, "demos");
        if (Directory.Exists(demos))
        {
            yield return demos;
        }
    }
}
