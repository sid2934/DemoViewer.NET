#region

using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using TUnit.Core.Exceptions;
using SysAssembly = System.Reflection.Assembly;

#endregion

namespace DemoViewer.NET.Testing.Tiers;

/// <summary>
///     Tests about the tests: the guard that stops the tier taxonomy rotting silently.
///     <para>
///         Everything a tier does is exclusion by category string, and every failure mode of that
///         design is silent. A mistyped <c>[Category("Bugdet")]</c> compiles, runs, and quietly promotes
///         a benchmark into the fast tier. A tier definition that drifts out of step with
///         <c>scripts/test.sh</c> means the documented tier and the executed tier are different things.
///         A new demo-reading test class with no tag lands in <c>fast</c> and adds a multi-second parse
///         to the tier whose entire purpose is to have none. None of that produces a red test on its
///         own, so each one gets an assertion here.
///     </para>
///     <para>
///         This file is compiled into every test assembly (linked, not referenced), so each suite polices
///         itself with no shared-assembly dependency — which the Playback2D and dv2d suites could not
///         take without breaking their own "no Avalonia in this process" architecture assertions.
///     </para>
/// </summary>
public partial class TestTierContractTests
{
    /// <summary>
    ///     A category outside the declared vocabulary is a typo or an undeclared tag. Either way it
    ///     changes tier membership silently, because exclusion filters match on the literal string.
    /// </summary>
    [Test]
    public async Task EveryCategoryInThisAssembly_IsInTheKnownVocabulary()
    {
        List<string> offenders = [];
        foreach ((MethodInfo method, ImmutableArray<string> categories) in DiscoverTests())
        {
            foreach (string category in categories)
            {
                if (!TestTiers.KnownCategories.Contains(category, StringComparer.Ordinal))
                {
                    offenders.Add($"{method.DeclaringType?.Name}.{method.Name} → [Category(\"{category}\")]");
                }
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    ///     The tiers must nest: every test the standard tier runs, the full tier runs, and every test
    ///     the fast tier runs, the standard tier runs. That is a property of the exclusion sets rather
    ///     than of any particular test, so it is asserted on the sets — a tier that dropped a tag its
    ///     cheaper neighbour keeps would otherwise make "run the cheaper tier, then the dearer one"
    ///     stop being a strictly widening operation, and a green fast run would no longer mean anything
    ///     about standard.
    /// </summary>
    [Test]
    public async Task TierExclusionSets_Nest_FromFastDownToFull()
    {
        ImmutableArray<string> fast = TestTiers.Exclusions[TestTiers.Fast];
        ImmutableArray<string> standard = TestTiers.Exclusions[TestTiers.Standard];
        ImmutableArray<string> full = TestTiers.Exclusions[TestTiers.Full];

        await Assert.That(standard.Except(fast, StringComparer.Ordinal)).IsEmpty();
        await Assert.That(full.Except(standard, StringComparer.Ordinal)).IsEmpty();
        await Assert.That(full).IsEmpty();
        // Strictly widening: two tiers that ran the same set would be one tier with two names, and the
        // cheaper one would be paying for a distinction it does not make.
        await Assert.That(fast.Length).IsGreaterThan(standard.Length);
        await Assert.That(standard.Length).IsGreaterThan(full.Length);
    }

    /// <summary>
    ///     A cost tag no tier excludes is dead weight: it reads as "this test is expensive" while
    ///     changing nothing about when it runs. Either a tier should drop it or it belongs in the
    ///     informational half of the vocabulary.
    /// </summary>
    [Test]
    public async Task EveryCostCategory_IsExcludedBySomeTier()
    {
        HashSet<string> excludedSomewhere = new(StringComparer.Ordinal);
        foreach (ImmutableArray<string> exclusions in TestTiers.Exclusions.Values)
        {
            excludedSomewhere.UnionWith(exclusions);
        }

        await Assert.That(TestTiers.CostCategories.Where(c => !excludedSomewhere.Contains(c))).IsEmpty();
        // And nothing excluded is outside the vocabulary — the other direction of the same rot.
        await Assert.That(excludedSomewhere.Where(c => !TestTiers.KnownCategories.Contains(c, StringComparer.Ordinal)))
            .IsEmpty();
    }

    /// <summary>
    ///     The scripts are what a human and CI actually run, and they carry the filter strings as
    ///     literal text. This asserts that text is exactly what <see cref="TestTiers" /> derives, so the
    ///     documented taxonomy and the executed one cannot drift apart — the single most likely way for
    ///     this design to become a lie.
    ///     <para>
    ///         <c>scripts/test.sh</c> carries whole filters; <c>scripts/test-app-suite.sh</c> composes
    ///         the same exclusions onto a class-partition path, so it carries only the bracket. Both
    ///         are checked, because two hand-maintained copies of one list is precisely the shape of
    ///         drift this file exists to catch.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ScriptTierFilters_AreExactlyTheCanonicalOnes()
    {
        string root = RequireRepoRoot();
        List<string> missing = [];

        foreach ((string relative, bool bracketOnly) in
                 (ValueTuple<string, bool>[])[("test.sh", false), ("test-app-suite.sh", true)])
        {
            string script = Path.Combine(root, "scripts", relative);
            if (!File.Exists(script))
            {
                throw new SkipTestException($"scripts/{relative} not found at {script}");
            }

            string text = await File.ReadAllTextAsync(script);
            foreach (string tier in TestTiers.Names)
            {
                string filter = TestTiers.TreeNodeFilterFor(tier);
                if (bracketOnly)
                {
                    int bracket = filter.IndexOf('[', StringComparison.Ordinal);
                    if (bracket < 0)
                    {
                        continue;       // The full tier has no bracket to compose.
                    }

                    filter = filter[bracket..];
                }

                if (!text.Contains(filter, StringComparison.Ordinal))
                {
                    missing.Add($"scripts/{relative}: {tier} → {filter}");
                }
            }
        }

        await Assert.That(missing).IsEmpty();
    }

    /// <summary>
    ///     Any source file that resolves a real <c>.dem</c> declares test classes that must be tagged
    ///     out of the demo-free tiers — with <see cref="TestTiers.RealDemo" />, or with
    ///     <see cref="TestTiers.Integration" />, which already excludes them everywhere
    ///     <see cref="TestTiers.RealDemo" /> would.
    ///     <para>
    ///         <b>Scope:</b> this is a per-class guard driven by a source scan. It catches a whole new
    ///         demo-reading class arriving with no tag, but not a demo-reading method added to an
    ///         already-tagged class where the tag sits on the siblings rather than the class. That
    ///         narrower case is left to review; the alternative is parsing C# in a test, which trades a
    ///         real guard for a brittle one.
    ///     </para>
    /// </summary>
    [Test]
    public async Task EveryClassThatResolvesADemo_IsTaggedOutOfTheDemoFreeTiers()
    {
        string projectDirectory = RequireProjectDirectory();
        Dictionary<string, ImmutableArray<string>> byClass = CategoriesByClass();

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string source = await File.ReadAllTextAsync(file);
            if (!source.Contains("RequireDemo(", StringComparison.Ordinal)
                && !source.Contains("FindDemoPath(", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in ClassDeclaration().Matches(source))
            {
                string name = match.Groups["name"].Value;
                if (!byClass.TryGetValue(name, out ImmutableArray<string> categories))
                {
                    continue;   // Declares no tests in this assembly — a helper, a fake, a harness.
                }

                if (!categories.Contains(TestTiers.RealDemo, StringComparer.Ordinal)
                    && !categories.Contains(TestTiers.Integration, StringComparer.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}::{name} resolves a demo but carries neither "
                                  + $"[Category(\"{TestTiers.RealDemo}\")] nor [Category(\"{TestTiers.Integration}\")]");
                }
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    ///     Any source file that constructs a headless Avalonia window or rasterises a visual tree
    ///     declares test classes that must be tagged out of <see cref="TestTiers.Fast" /> — normally
    ///     with <see cref="TestTiers.Render" />, but any tag that tier already drops will do, on the
    ///     same reasoning as the demo clause above.
    ///     <para>
    ///         Before this guard, <c>fast</c> and <c>standard</c> discovered the identical 929 tests on
    ///         the App suite, because the window-booting classes carried no category at all: the tier
    ///         whose whole purpose is "no pixels" was paying for every one of them.
    ///     </para>
    ///     <para>
    ///         The markers are the Avalonia ones — a <c>Window</c> construction, a window handed back by
    ///         a harness, a <c>CaptureRenderedFrame</c> — so this guards the App suite, where the hole
    ///         was. It says nothing about the direct-execution suites, which rasterise through Skia with
    ///         no window and tag themselves. Unlike the demo clause it attributes per class rather than
    ///         per file: three suites share <c>Playback2DExportSurfaceTests.cs</c> and only some of them
    ///         boot a window.
    ///     </para>
    /// </summary>
    [Test]
    public async Task EveryClassThatBootsAWindow_IsTaggedOutOfTheFastTier()
    {
        string projectDirectory = RequireProjectDirectory();
        Dictionary<string, ImmutableArray<string>> byClass = CategoriesByClass();
        ImmutableArray<string> droppedByFast = TestTiers.Exclusions[TestTiers.Fast];

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string source = await File.ReadAllTextAsync(file);
            foreach ((string name, string body) in TopLevelClassBodies(source))
            {
                if (!BootsAWindow().IsMatch(body))
                {
                    continue;
                }

                if (!byClass.TryGetValue(name, out ImmutableArray<string> categories))
                {
                    continue;   // Declares no tests in this assembly — a helper, a fake, a harness.
                }

                if (!categories.Intersect(droppedByFast, StringComparer.Ordinal).Any())
                {
                    offenders.Add($"{Path.GetFileName(file)}::{name} constructs a window but carries no "
                                  + $"category the fast tier drops — add [Category(\"{TestTiers.Render}\")]");
                }
            }
        }

        // The count alone is useless when this goes red, and it goes red on a whole suite at a time.
        offenders.ForEach(Console.WriteLine);
        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    ///     <c>[Explicit]</c> is banned outright, because on the pinned TUnit (0.25.21) it breaks
    ///     filtering rather than extending it: when a filter's match set contains both explicit
    ///     and non-explicit tests, <c>TestFilterService</c> discards the filter and runs every
    ///     non-explicit test in the assembly instead. A single <c>[Explicit]</c> test is therefore
    ///     enough to turn <c>-t fast</c> into a full run with no error and no warning — the exact
    ///     failure this whole taxonomy exists to prevent. Use <c>[Category]</c> and a tier, or
    ///     <c>[Skip]</c>.
    /// </summary>
    [Test]
    public async Task NoTestIsMarkedExplicit()
    {
        List<string> offenders = [];
        foreach ((MethodInfo method, _) in DiscoverTests())
        {
            bool explicitly = method.GetCustomAttributes<ExplicitAttribute>(true).Any()
                              || method.DeclaringType?.GetCustomAttributes<ExplicitAttribute>(true).Any() == true;
            if (explicitly)
            {
                offenders.Add($"{method.DeclaringType?.Name}.{method.Name}");
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    // ── Discovery helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every <c>[Test]</c> method in this assembly with its <b>effective</b> category set — method
    ///     attributes, then the declaring type and its bases, then the assembly. That is the same
    ///     three-level union TUnit's source generator builds, reproduced here through reflection so the
    ///     guard reads the same categories the filter does.
    /// </summary>
    private static IEnumerable<(MethodInfo Method, ImmutableArray<string> Categories)> DiscoverTests()
    {
        SysAssembly assembly = typeof(TestTierContractTests).Assembly;
        string[] assemblyCategories = assembly.GetCustomAttributes<CategoryAttribute>()
            .Select(a => a.Category).ToArray();

        foreach (Type type in assembly.GetTypes())
        {
            string[] typeCategories = type.GetCustomAttributes<CategoryAttribute>(true)
                .Select(a => a.Category).ToArray();

            foreach (MethodInfo method in type.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                         | BindingFlags.DeclaredOnly))
            {
                if (!method.GetCustomAttributes<TestAttribute>(true).Any())
                {
                    continue;
                }

                yield return (method, [
                    ..method.GetCustomAttributes<CategoryAttribute>(true).Select(a => a.Category),
                    ..typeCategories,
                    ..assemblyCategories
                ]);
            }
        }
    }

    /// <summary>Class name → the union of the effective categories of every test it declares.</summary>
    private static Dictionary<string, ImmutableArray<string>> CategoriesByClass()
    {
        Dictionary<string, HashSet<string>> accumulator = new(StringComparer.Ordinal);
        foreach ((MethodInfo method, ImmutableArray<string> categories) in DiscoverTests())
        {
            string? name = method.DeclaringType?.Name;
            if (name is null)
            {
                continue;
            }

            if (!accumulator.TryGetValue(name, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                accumulator[name] = set;
            }

            set.UnionWith(categories);
        }

        return accumulator.ToDictionary(kv => kv.Key, kv => kv.Value.ToImmutableArray(), StringComparer.Ordinal);
    }

    /// <summary>
    ///     Each top-level class declaration paired with the source that follows it, up to the next one.
    ///     Crude, and deliberately so — a private nested type stays inside its owner's slice, which is
    ///     what attributes a fixture's <c>Window</c> to the suite that uses it.
    /// </summary>
    /// <param name="source">One C# file.</param>
    private static IEnumerable<(string Name, string Body)> TopLevelClassBodies(string source)
    {
        MatchCollection declarations = ClassDeclaration().Matches(source);
        for (int i = 0; i < declarations.Count; i++)
        {
            int end = i + 1 < declarations.Count ? declarations[i + 1].Index : source.Length;
            yield return (declarations[i].Groups["name"].Value, source[declarations[i].Index..end]);
        }
    }

    /// <summary>The repo root, or a skip when this assembly is running detached from a checkout.</summary>
    /// <exception cref="SkipTestException">No <c>DemoViewer.NET.slnx</c> above the test binaries.</exception>
    private static string RequireRepoRoot() =>
        TestTiers.FindRepoRoot()
        ?? throw new SkipTestException("no DemoViewer.NET.slnx above the test binaries — not a checkout.");

    /// <summary>
    ///     The directory holding this test assembly's own sources, found by locating
    ///     <c>&lt;AssemblyName&gt;.csproj</c> under the repo's source trees. Skips rather than fails
    ///     when it cannot be found, on the same principle as <see cref="RequireRepoRoot" />.
    /// </summary>
    /// <exception cref="SkipTestException">The project file could not be located.</exception>
    private static string RequireProjectDirectory()
    {
        string root = RequireRepoRoot();
        string projectFile = typeof(TestTierContractTests).Assembly.GetName().Name + ".csproj";

        string[] trees = ["src", "tools", "tests"];
        foreach (string tree in trees)
        {
            string treePath = Path.Combine(root, tree);
            if (!Directory.Exists(treePath))
            {
                continue;
            }

            string? match = Directory
                .EnumerateFiles(treePath, projectFile, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (match is not null)
            {
                return Path.GetDirectoryName(match)!;
            }
        }

        throw new SkipTestException($"could not locate {projectFile} under {root}.");
    }

    [GeneratedRegex(@"^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*class\s+(?<name>\w+)",
        RegexOptions.Multiline)]
    private static partial Regex ClassDeclaration();

    /// <summary>
    ///     Constructing a <c>Window</c> (both spellings, target-typed included), taking one back out of
    ///     a harness by deconstruction or return type, or rasterising a visual tree.
    /// </summary>
    [GeneratedRegex(@"\bnew\s+Window\s*[({]|\bWindow\s+\w+\s*=\s*new\b|\(\s*Window\s+\w+\s*,"
                    + @"|\bCaptureRenderedFrame\s*\(")]
    private static partial Regex BootsAWindow();
}
