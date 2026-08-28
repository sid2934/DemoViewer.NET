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
///         itself with no shared-assembly dependency. The Playback2D and dv2d suites could not take one
///         without breaking their own "no Avalonia in this process" architecture assertions.
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
    ///     than of any particular test, so it is asserted on the sets. A tier that dropped a tag its
    ///     cheaper neighbour keeps would break the widening, and a green fast run would say nothing
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
        // Strictly widening: two tiers that ran the same set would be one tier with two names.
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
        // And nothing excluded is outside the vocabulary: the other direction of the same rot.
        await Assert.That(excludedSomewhere.Where(c => !TestTiers.KnownCategories.Contains(c, StringComparer.Ordinal)))
            .IsEmpty();
    }

    /// <summary>
    ///     The scripts are what a human and CI actually run, and they carry the filter strings as
    ///     literal text. This asserts that text is exactly what <see cref="TestTiers" /> derives, so the
    ///     documented taxonomy and the executed one cannot drift apart.
    ///     <para>
    ///         <c>scripts/test.sh</c> carries whole filters; <c>scripts/test-app-suite.sh</c> composes
    ///         the same exclusions onto a class-partition path, so it carries only the bracket. Both
    ///         are checked, because two hand-maintained copies of one list drift.
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
                        continue; // The full tier has no bracket to compose.
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
    ///     out of the demo-free tiers: with <see cref="TestTiers.RealDemo" />, or with
    ///     <see cref="TestTiers.Integration" />, which already excludes them everywhere
    ///     <see cref="TestTiers.RealDemo" /> would.
    ///     <para>
    ///         <b>Scope:</b> this is a per-class guard driven by a source scan. It catches a whole new
    ///         demo-reading class arriving with no tag, but not a demo-reading method added to an
    ///         already-tagged class where the tag sits on the siblings rather than the class. That
    ///         narrower case is left to review; the alternative is parsing C# inside a test.
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
                    continue; // Declares no tests in this assembly: a helper, a fake, a harness.
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
    ///     declares test classes that must be tagged out of <see cref="TestTiers.Fast" />: normally
    ///     with <see cref="TestTiers.Render" />, but any tag that tier already drops will do, on the
    ///     same reasoning as the demo clause above.
    ///     <para>
    ///         Before this guard, <c>fast</c> and <c>standard</c> discovered the identical 929 tests on
    ///         the App suite, because the window-booting classes carried no category at all.
    ///     </para>
    ///     <para>
    ///         The markers are the Avalonia ones (a <c>Window</c> construction, a window handed back by
    ///         a harness, a <c>CaptureRenderedFrame</c>), so this guards the App suite. It says nothing
    ///         about the direct-execution suites, which rasterise through Skia with no window and tag
    ///         themselves. Unlike the demo clause it attributes per class rather than per file: three
    ///         suites share <c>Playback2DExportSurfaceTests.cs</c> and only some of them boot a window.
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
                    continue; // Declares no tests in this assembly: a helper, a fake, a harness.
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
    ///     A test that asserts an allocation figure must carry <see cref="TestTiers.Budget" />, the one
    ///     tag the <c>playback2d-budget</c> lane selects back after every correctness lane has dropped it.
    ///     <para>
    ///         An exact-zero allocation window is GC- and JIT-timing sensitive, and three of them went
    ///         red once and green on the re-run inside a single day. Untagged, each was running in
    ///         <c>playback2d-tests</c>, both <c>render-backends</c> passes and the GPU lane: four
    ///         blocking lanes for a figure the budget lane exists to measure, once, with
    ///         <c>DV2D_BUDGET_SCALE</c> set.
    ///     </para>
    ///     <para>
    ///         <b>Per method, and aimed at the assertion rather than at the counter.</b> An allocation
    ///         window is normally one or two methods inside an otherwise behavioural class, so a
    ///         class-scoped rule would drag their siblings out of the standard tier to silence itself. A
    ///         method that reads the counter and only prints the number does not offend; what offends
    ///         is an <c>Assert.That</c> on a value the counter produced, including one handed back by a
    ///         measuring helper in the same class.
    ///     </para>
    /// </summary>
    [Test]
    public async Task EveryTestThatAssertsAnAllocationFigure_IsTaggedBudget()
    {
        string projectDirectory = RequireProjectDirectory();
        Dictionary<string, ImmutableArray<string>> byMethod = CategoriesByMethod();

        // Budget is the required tag because the standard tier drops it AND the budget lane selects it
        // back; any other cost tag would drop the test out of the correctness lanes into nothing.
        // Derived rather than assumed, so a change to the exclusion sets fails here rather than silently.
        ImmutableArray<string> droppedByStandard = TestTiers.Exclusions[TestTiers.Standard];
        await Assert.That(droppedByStandard.Contains(TestTiers.Budget, StringComparer.Ordinal)).IsTrue();

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            // Comment lines go before anything is matched, so a file that only DISCUSSES the counter is
            // skipped, and a doc comment naming it is attributed to no member at all.
            string code = WithoutCommentLines(await File.ReadAllTextAsync(file));
            if (!MeasuresAllocation().IsMatch(code))
            {
                continue;
            }

            foreach ((string className, string classBody) in TopLevelClassBodies(code))
            {
                (string Name, string Body)[] members = [.. MemberBodies(classBody)];
                HashSet<string> measuring = MeasuringMembers(members);

                foreach ((string name, string body) in members)
                {
                    if (!byMethod.TryGetValue($"{className}.{name}", out ImmutableArray<string> categories))
                    {
                        continue; // Not a [Test] in this assembly: a helper, a fixture, a constructor.
                    }

                    if (!AssertsAMeasurement(body, measuring)
                        || categories.Contains(TestTiers.Budget, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    offenders.Add($"{Path.GetFileName(file)}::{className}.{name} asserts an allocation "
                                  + $"figure but carries no [Category(\"{TestTiers.Budget}\")]");
                }
            }
        }

        offenders.ForEach(Console.WriteLine);
        await Assert.That(offenders).IsEmpty();
    }

    /// <summary>
    ///     <c>[Explicit]</c> is banned outright, because on the pinned TUnit (0.25.21) it breaks
    ///     filtering rather than extending it: when a filter's match set contains both explicit
    ///     and non-explicit tests, <c>TestFilterService</c> discards the filter and runs every
    ///     non-explicit test in the assembly instead. A single <c>[Explicit]</c> test is therefore
    ///     enough to turn <c>-t fast</c> into a full run with no error and no warning. Use
    ///     <c>[Category]</c> and a tier, or <c>[Skip]</c>.
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
    ///     Every <c>[Test]</c> method in this assembly with its <b>effective</b> category set: method
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
                    .. method.GetCustomAttributes<CategoryAttribute>(true).Select(a => a.Category),
                    .. typeCategories,
                    .. assemblyCategories
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

    /// <summary><c>Class.Method</c> → the effective categories of that one test.</summary>
    private static Dictionary<string, ImmutableArray<string>> CategoriesByMethod()
    {
        Dictionary<string, ImmutableArray<string>> byMethod = new(StringComparer.Ordinal);
        foreach ((MethodInfo method, ImmutableArray<string> categories) in DiscoverTests())
        {
            if (method.DeclaringType?.Name is { } name)
            {
                byMethod[$"{name}.{method.Name}"] = categories;
            }
        }

        return byMethod;
    }

    /// <summary>
    ///     Each member declaration in a class body paired with the source that follows it, up to the
    ///     next one. <see cref="TopLevelClassBodies" /> one level down, and just as crude: a field
    ///     between two methods lands in the earlier method's slice, which changes no answer here.
    /// </summary>
    /// <param name="classBody">One slice from <see cref="TopLevelClassBodies" />.</param>
    private static IEnumerable<(string Name, string Body)> MemberBodies(string classBody)
    {
        MatchCollection declarations = MemberDeclaration().Matches(classBody);
        for (int i = 0; i < declarations.Count; i++)
        {
            int end = i + 1 < declarations.Count ? declarations[i + 1].Index : classBody.Length;
            yield return (declarations[i].Groups["name"].Value, classBody[declarations[i].Index..end]);
        }
    }

    /// <summary>
    ///     The members of one class that hand back an allocation figure: those that read the counter,
    ///     plus everything that calls one of those, to a fixed point. One hop is not enough: the tree
    ///     already has a <c>Window</c> that runs a <c>Measure</c> twice and returns the second.
    /// </summary>
    private static HashSet<string> MeasuringMembers(IReadOnlyList<(string Name, string Body)> members)
    {
        HashSet<string> measuring = new(StringComparer.Ordinal);
        foreach ((string name, string body) in members)
        {
            if (MeasuresAllocation().IsMatch(body))
            {
                measuring.Add(name);
            }
        }

        for (bool grew = true; grew;)
        {
            grew = false;
            foreach ((string name, string body) in members)
            {
                if (!measuring.Contains(name) && measuring.Any(m => MentionsWord(body, m)))
                {
                    grew = measuring.Add(name);
                }
            }
        }

        return measuring;
    }

    /// <summary>
    ///     Whether one member's body asserts on an allocation figure rather than only producing one.
    ///     <para>
    ///         Every local bound to a figure is collected first: read straight off the counter, handed
    ///         back by a measuring member, or computed from one already bound. That last arm is why it
    ///         iterates to a fixed point, since the per-frame numbers divide a delta by a frame count
    ///         one statement after the delta exists.
    ///     </para>
    /// </summary>
    /// <param name="body">One slice from <see cref="MemberBodies" />, already stripped of comments.</param>
    /// <param name="measuring">The measuring members of the declaring class.</param>
    private static bool AssertsAMeasurement(string body, HashSet<string> measuring)
    {
        HashSet<string> measured = new(StringComparer.Ordinal);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Match binding in LocalBinding().Matches(body))
            {
                string name = binding.Groups["name"].Value;
                string right = binding.Groups["rhs"].Value;
                if (measured.Contains(name))
                {
                    continue;
                }

                if (MeasuresAllocation().IsMatch(right)
                    || measuring.Any(m => MentionsWord(right, m))
                    || measured.Any(m => MentionsWord(right, m)))
                {
                    grew = measured.Add(name);
                }
            }
        }

        return measured.Count > 0
               && AssertionSubjects(body).Any(subject => measured.Any(m => MentionsWord(subject, m)));
    }

    /// <summary>The argument of each <c>Assert.That(…)</c> in a body, parens balanced.</summary>
    private static IEnumerable<string> AssertionSubjects(string body)
    {
        const string call = "Assert.That(";
        for (int at = body.IndexOf(call, StringComparison.Ordinal);
             at >= 0;
             at = body.IndexOf(call, at + call.Length, StringComparison.Ordinal))
        {
            int start = at + call.Length;
            int depth = 1;
            int end = start;
            while (end < body.Length && depth > 0)
            {
                depth += body[end] switch
                {
                    '(' => 1,
                    ')' => -1,
                    _ => 0
                };
                end++;
            }

            yield return body[start..(depth == 0 ? end - 1 : end)];
        }
    }

    /// <summary>Whether <paramref name="text" /> contains <paramref name="word" /> as a whole identifier.</summary>
    private static bool MentionsWord(string text, string word)
    {
        for (int at = text.IndexOf(word, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            int after = at + word.Length;
            if ((at == 0 || !IsIdentifierChar(text[at - 1]))
                && (after == text.Length || !IsIdentifierChar(text[after])))
            {
                return true;
            }
        }

        return false;

        static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }

    /// <summary>
    ///     The source with every whole-line comment removed, line structure intact. Trailing comments
    ///     after code on the same line survive, which no rule here depends on.
    /// </summary>
    private static string WithoutCommentLines(string source) =>
        string.Join('\n', source.Split('\n').Where(line =>
        {
            string trimmed = line.TrimStart();
            return !trimmed.StartsWith("//", StringComparison.Ordinal)
                   && !trimmed.StartsWith("/*", StringComparison.Ordinal)
                   && !trimmed.StartsWith('*');
        }));

    /// <summary>
    ///     Each top-level class declaration paired with the source that follows it, up to the next one.
    ///     Crude, and deliberately so: a private nested type stays inside its owner's slice, which is
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

    /// <summary>
    ///     A member declaration: an access modifier, then the <b>last</b> name on the line that is
    ///     followed by an open paren, with no <c>=</c> before it so an initialised field cannot pass as
    ///     a method. Last rather than first, because a tuple return type puts a paren in front of the
    ///     name: <c>private static (double Micros, long Bytes) Cost(…)</c> reads as a member called
    ///     <c>static</c> under the other spelling, and <c>static</c> then matches every sibling.
    /// </summary>
    [GeneratedRegex(@"^[ \t]+(?:public|private|internal|protected)\s[^\n=]*\b(?<name>\w+)\s*\(",
        RegexOptions.Multiline)]
    private static partial Regex MemberDeclaration();

    /// <summary>A numeric local and the expression it is bound to, on one line.</summary>
    [GeneratedRegex(@"^[ \t]*(?:long|double|float|int|decimal|var)\s+(?<name>\w+)\s*=\s*(?<rhs>[^;\r\n]*);",
        RegexOptions.Multiline)]
    private static partial Regex LocalBinding();

    /// <summary>Reading one of the runtime's cumulative allocation counters.</summary>
    [GeneratedRegex(@"\bGC\.Get(?:AllocatedBytesForCurrentThread|TotalAllocatedBytes)\s*\(")]
    private static partial Regex MeasuresAllocation();
}
