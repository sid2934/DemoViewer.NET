#region

using System.Collections.Immutable;
using System.Globalization;

#endregion

namespace DemoViewer.NET.Testing.Tiers;

/// <summary>
///     The canonical definition of this repository's test tiers, compiled into <b>every</b> test
///     assembly as a linked source file (there is no shared test-support assembly on this path: the
///     Playback2D suite deliberately references none, so that its "no Avalonia is even loaded"
///     architecture assertion stays true by construction).
///     <para>
///         <b>Tiers are defined by exclusion, never by inclusion.</b> A test with no categories at all
///         is in every tier, so a newly written unit test is covered by the cheapest tier the moment it
///         is written and nobody has to remember to opt it in. What costs something — a demo read off
///         disk, a rasterised frame, a spawned process, a benchmark — is what carries a tag, and a tag
///         is what a tier drops. <c>fast ⊆ standard ⊆ full</c> holds by construction because the
///         exclusion sets nest, which <c>TestTierContractTests</c> asserts rather than assumes.
///     </para>
///     <para>
///         See <c>docs/playback2d-v2/plans/P3-test-tiers.md</c> for the working agreement and the
///         Microsoft.Testing.Platform filter-grammar findings this file's
///         <see cref="TreeNodeFilterFor" /> is written against — in particular, that every operand
///         inside <c>[…]</c> must be a parenthesised <c>Key=Value</c> / <c>Key!=Value</c> comparison,
///         because the unparenthesised form crashes the filter parser outright.
///     </para>
/// </summary>
public static class TestTiers
{
    // ── Cost tags: a tier drops these ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Measures rather than asserts behaviour — frame-time and allocation benchmarks. Pre-existing
    ///     and load-bearing: the <c>playback2d-budget</c> CI lane selects on exactly this string, and
    ///     the correctness lanes exclude it, so its membership must not drift.
    /// </summary>
    public const string Budget = "Budget";

    /// <summary>
    ///     Depends on machine or OS state this repository does not own — file-lock semantics, symlink
    ///     creation privilege, a per-user settings path. Known to fail on some developer machines while
    ///     passing on others; carried so an in-flight run is not asked to interpret a red that means
    ///     nothing about the change under test.
    /// </summary>
    public const string Environmental = "Environmental";

    /// <summary>Needs a real GPU render surface (ANGLE/EGL). Pre-existing.</summary>
    public const string Gpu = "Gpu";

    /// <summary>
    ///     Crosses a host or process boundary: an Avalonia headless application, a web host on a fixed
    ///     port, a spawned <c>dv2d</c> subprocess. Pre-existing, and already the dominant cost in the
    ///     App and LiveSync suites.
    /// </summary>
    public const string Integration = "Integration";

    /// <summary>Reads a CS2 <c>.dem</c> file off disk (and usually parses and replays it).</summary>
    public const string RealDemo = "RealDemo";

    /// <summary>
    ///     Rasterises real pixels through a render surface, or compares them against a committed golden
    ///     image. Distinct from <see cref="Gpu" />, which is about the backend rather than the cost.
    /// </summary>
    public const string Render = "Render";

    // ── Informational tags: no tier reads these ──────────────────────────────────────────────────

    /// <summary>Pre-existing descriptive tag. No tier excludes it.</summary>
    public const string Probe = "Probe";

    /// <summary>Pre-existing descriptive tag. No tier excludes it.</summary>
    public const string Unit = "Unit";

    // ── Tier names ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Pure unit and contract tests: no demo, no pixels, no process, no benchmark.</summary>
    public const string Fast = "fast";

    /// <summary>The in-flight default: <see cref="Fast" /> plus the render and golden gates.</summary>
    public const string Standard = "standard";

    /// <summary>Everything. What CI and a pre-push review run.</summary>
    public const string Full = "full";

    /// <summary>
    ///     Every category string this repository is allowed to put on a test. A category outside this
    ///     set is a typo or an undeclared tag, and either way it silently changes which tier a test
    ///     lands in — which is why <c>TestTierContractTests</c> fails on one.
    /// </summary>
    public static ImmutableArray<string> KnownCategories { get; } =
        [Budget, Environmental, Gpu, Integration, Probe, RealDemo, Render, Unit];

    /// <summary>The subset of <see cref="KnownCategories" /> that at least one tier excludes.</summary>
    public static ImmutableArray<string> CostCategories { get; } =
        [Budget, Environmental, Gpu, Integration, RealDemo, Render];

    /// <summary>Tier name → the categories that tier drops. Ordered; the filter string is derived.</summary>
    public static ImmutableDictionary<string, ImmutableArray<string>> Exclusions { get; } =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            // Alphabetical inside each tier so the derived filter string is stable, and so a diff of
            // scripts/test.sh against this file is a diff of intent rather than of ordering.
            KeyValuePair.Create(Fast,
                ImmutableArray.Create(Budget, Environmental, Gpu, Integration, RealDemo, Render)),
            KeyValuePair.Create(Standard,
                ImmutableArray.Create(Budget, Environmental, Integration, RealDemo)),
            KeyValuePair.Create(Full, ImmutableArray<string>.Empty)
        ]);

    /// <summary>The tier names, cheapest first.</summary>
    public static ImmutableArray<string> Names { get; } = [Fast, Standard, Full];

    /// <summary>
    ///     The <c>--treenode-filter</c> expression for a tier.
    ///     <para>
    ///         Shape: <c>/Assembly/Namespace/Class/Method[FILTER]</c>. Every operand is a parenthesised
    ///         <c>Category!=Tag</c>; the parentheses are mandatory, not cosmetic — <c>&amp;</c> and
    ///         <c>|</c> bind <i>tighter</i> than <c>=</c> in the platform's parser, so the bare form
    ///         <c>[Category!=A&amp;Category!=B]</c> throws
    ///         <see cref="InvalidOperationException" /> out of the filter parser before a single test
    ///         runs. <c>full</c> carries no bracket at all, because an empty <c>[]</c> is not valid
    ///         either.
    ///     </para>
    /// </summary>
    /// <param name="tier">One of <see cref="Names" />.</param>
    /// <returns>The filter expression to pass to <c>--treenode-filter</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier" /> is not a known tier.</exception>
    public static string TreeNodeFilterFor(string tier)
    {
        if (!Exclusions.TryGetValue(tier, out ImmutableArray<string> excluded))
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier,
                $"unknown tier; expected one of {string.Join(", ", Names)}");
        }

        return excluded.IsEmpty
            ? "/*/*/*/*"
            : string.Create(CultureInfo.InvariantCulture,
                $"/*/*/*/*[{string.Join("&", excluded.Select(c => $"(Category!={c})"))}]");
    }

    /// <summary>
    ///     Walks up from the running assembly until it finds the directory holding
    ///     <c>DemoViewer.NET.slnx</c>. Returns <c>null</c> when the assembly is running detached from a
    ///     checkout, which is the case the source-reading contract tests skip on rather than fail.
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
}
