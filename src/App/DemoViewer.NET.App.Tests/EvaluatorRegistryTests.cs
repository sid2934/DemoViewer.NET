#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="EvaluatorRegistry" /> on its own, with fakes: the topological sort, its tie-break, the
///     cycle and unknown-id guards, and the lazy pack gate (a disabled pack's factory is never invoked).
///     Pure logic; no container, no queue.
/// </summary>
public class EvaluatorRegistryTests
{
    [Test]
    public async Task Resolve_OrdersByAfter_DeclarationOrderBreaksTies()
    {
        EvaluatorRegistry registry = new();
        // "c" is declared first but depends on "a", declared after it. "b" has no dependency at all.
        // Once "a" resolves, "c" becomes ready and, being declared before "b", is picked next.
        registry.AddCore("c", () => new Fake("c"), "a");
        registry.AddCore("a", () => new Fake("a"));
        registry.AddCore("b", () => new Fake("b"));

        IReadOnlyList<IDemoEvaluator> resolved = registry.Resolve();

        await Assert.That(resolved.Select(e => e.Id)).IsEquivalentTo(["a", "c", "b"]);
    }

    [Test]
    public async Task Resolve_SixEntries_MatchesTheLibraryHighlightsPackChain()
    {
        // The exact chain StratBookPack.Contribute declares: roundFacts after library, roundIndex after
        // roundFacts, suggestedTags after roundIndex, grenades after library.
        EvaluatorRegistry registry = new();
        registry.AddCore("library", () => new Fake("library"));
        registry.AddCore("highlights", () => new Fake("highlights"));
        registry.AddPacksLazily(() =>
        {
            registry.AddPackEvaluator("roundfacts", () => new Fake("roundfacts"), ["library"], () => true);
            registry.AddPackEvaluator("roundindex", () => new Fake("roundindex"), ["roundfacts"], () => true);
            registry.AddPackEvaluator("suggestedtags", () => new Fake("suggestedtags"), ["roundindex"], () => true);
            registry.AddPackEvaluator("grenades", () => new Fake("grenades"), ["library"], () => true);
        });

        IReadOnlyList<IDemoEvaluator> resolved = registry.Resolve();

        await Assert.That(resolved.Select(e => e.Id))
            .IsEquivalentTo(["library", "highlights", "roundfacts", "roundindex", "suggestedtags", "grenades"]);
    }

    [Test]
    public async Task Resolve_UnknownAfterId_Throws()
    {
        EvaluatorRegistry registry = new();
        registry.AddCore("a", () => new Fake("a"), "ghost");

        Assert.Throws<InvalidOperationException>(() => registry.Resolve());
    }

    [Test]
    public async Task Resolve_Cycle_Throws()
    {
        EvaluatorRegistry registry = new();
        registry.AddCore("a", () => new Fake("a"), "b");
        registry.AddCore("b", () => new Fake("b"), "a");

        Assert.Throws<InvalidOperationException>(() => registry.Resolve());
    }

    [Test]
    public void AddCore_DuplicateId_ThrowsImmediately()
    {
        EvaluatorRegistry registry = new();
        registry.AddCore("a", () => new Fake("a"));

        // Caught at the point of declaration, not deferred to Resolve(): a second pack landing on the
        // same id is a composition-time mistake, not a runtime gate decision.
        Assert.Throws<InvalidOperationException>(() => registry.AddCore("a", () => new Fake("a")));
    }

    [Test]
    public async Task Resolve_FactoryBuildsAnInstanceWithTheWrongId_Throws()
    {
        EvaluatorRegistry registry = new();
        registry.AddCore("a", () => new Fake("not-a"));

        Assert.Throws<InvalidOperationException>(() => registry.Resolve());
    }

    [Test]
    public async Task Resolve_PackDisabled_NeverInvokesItsFactory_AndExcludesItFromTheOrder()
    {
        EvaluatorRegistry registry = new();
        int packFactoryCalls = 0;
        registry.AddCore("library", () => new Fake("library"));
        registry.AddPacksLazily(() =>
        {
            registry.AddPackEvaluator("pack", () =>
            {
                packFactoryCalls++;
                return new Fake("pack");
            }, [], () => false);
        });

        IReadOnlyList<IDemoEvaluator> resolved = registry.Resolve();

        await Assert.That(resolved.Select(e => e.Id)).IsEquivalentTo(["library"]);
        await Assert.That(packFactoryCalls).IsEqualTo(0)
            .Because("a disabled pack's evaluator must never be constructed, not merely excluded after construction");
    }

    [Test]
    public async Task Resolve_PackEnabledAfterAnEarlierCall_IsIncludedOnTheNextResolve()
    {
        EvaluatorRegistry registry = new();
        bool packOn = false;
        registry.AddCore("library", () => new Fake("library"));
        registry.AddPacksLazily(() =>
        {
            registry.AddPackEvaluator("pack", () => new Fake("pack"), [], () => packOn);
        });

        await Assert.That(registry.Resolve().Select(e => e.Id)).IsEquivalentTo(["library"]);

        packOn = true;

        await Assert.That(registry.Resolve().Select(e => e.Id)).IsEquivalentTo(["library", "pack"]);
    }

    [Test]
    public async Task Resolve_PendingPathsUnion_IsTheUnionOfTheResolvedEvaluatorsPendingPaths()
    {
        EvaluatorRegistry registry = new();
        registry.AddCore("library", () => new Fake("library") { Pending = ["/a.dem", "/b.dem"] });
        registry.AddCore("highlights", () => new Fake("highlights") { Pending = ["/b.dem", "/c.dem"] });
        registry.AddPacksLazily(() =>
        {
            // Disabled: its paths must not appear in the union, and its factory must not run.
            registry.AddPackEvaluator("pack", () => throw new InvalidOperationException("must not run"),
                [], () => false);
        });

        IReadOnlyList<string> union = [.. registry.Resolve().SelectMany(e => e.PendingPaths())
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        await Assert.That(union).IsEquivalentTo(["/a.dem", "/b.dem", "/c.dem"]);
    }

    private sealed class Fake(string id) : IDemoEvaluator
    {
        public string Id { get; } = id;
        public IReadOnlyList<string> Pending { get; init; } = [];

        public bool Wants(string path) => false;

        public void Evaluate(string path, ParsedDemo parsed)
        {
            // not exercised here
        }

        public IReadOnlyList<string> PendingPaths() => Pending;
    }
}
