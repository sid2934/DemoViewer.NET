#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.DependencyInjection;

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

        await Assert.That(resolved.Select(e => e.Id))
            .IsEquivalentTo(["a", "c", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
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
            .IsEquivalentTo(["library", "highlights", "roundfacts", "roundindex", "suggestedtags", "grenades"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
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

    [Test]
    public void Validate_Cycle_Throws()
    {
        EvaluatorRegistry registry = new();
        registry.AddCore("a", () => new Fake("a"), "b");
        registry.AddCore("b", () => new Fake("b"), "a");

        Assert.Throws<InvalidOperationException>(() => registry.Validate());
    }

    [Test]
    public void Validate_UnknownAfterId_Throws()
    {
        EvaluatorRegistry registry = new();
        registry.AddCore("a", () => new Fake("a"), "ghost");

        Assert.Throws<InvalidOperationException>(() => registry.Validate());
    }

    [Test]
    public async Task Validate_PopulatesAndSorts_ButNeverMaterializesAnEvaluator()
    {
        EvaluatorRegistry registry = new();
        int coreFactoryCalls = 0;
        int packFactoryCalls = 0;
        registry.AddCore("library", () =>
        {
            coreFactoryCalls++;
            return new Fake("library");
        });
        registry.AddPacksLazily(() =>
        {
            registry.AddPackEvaluator("roundfacts", () =>
            {
                packFactoryCalls++;
                return new Fake("roundfacts");
            }, ["library"], () => true);
        });

        registry.Validate();

        await Assert.That(coreFactoryCalls).IsEqualTo(0);
        await Assert.That(packFactoryCalls).IsEqualTo(0);

        // The sort Validate computed is reused: Resolve right after still materializes correctly.
        IReadOnlyList<IDemoEvaluator> resolved = registry.Resolve();

        await Assert.That(resolved.Select(e => e.Id))
            .IsEquivalentTo(["library", "roundfacts"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(coreFactoryCalls).IsEqualTo(1);
        await Assert.That(packFactoryCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Resolve_PopulateThrows_IsNeverRetriedOnALaterCall()
    {
        EvaluatorRegistry registry = new();
        registry.AddCore("library", () => new Fake("library"));
        int populateCalls = 0;
        registry.AddPacksLazily(() =>
        {
            populateCalls++;
            registry.AddPackEvaluator("roundfacts", () => new Fake("roundfacts"), [], () => true);
            throw new InvalidOperationException("boom");
        });

        Assert.Throws<InvalidOperationException>(() => registry.Resolve());

        // A retried populate would try to re-add "roundfacts" and throw "registered more than once",
        // masking the real failure above behind a confusing second one.
        try
        {
            registry.Resolve();
        }
        catch (InvalidOperationException)
        {
            // whatever a second call over the partial state does, it must not come from re-populating
        }

        await Assert.That(populateCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Resolve_ExtensionEvaluator_KeepsOneAdapterPerInstance()
    {
        PackContributions contributions = new(new EvaluatorPack(), () => throw new InvalidOperationException());
        SdkFake shared = new("ext.shared");
        int built = 0;
        contributions.Evaluator("ext.shared", () => shared);
        contributions.Evaluator("ext.fresh", () => new SdkFake("ext.fresh", ++built));

        EvaluatorRegistry registry = new();
        foreach (EvaluatorContribution c in contributions.Evaluators)
        {
            registry.AddPackEvaluator(c.Id, c.Factory, c.After, () => true);
        }

        IReadOnlyList<IDemoEvaluator> first = registry.Resolve();
        IReadOnlyList<IDemoEvaluator> second = registry.Resolve();

        await Assert.That(ReferenceEquals(first[0], second[0])).IsTrue()
            .Because("the same evaluator instance is not re-wrapped on every resolve");
        await Assert.That(ReferenceEquals(first[1], second[1])).IsFalse()
            .Because("a factory that builds a new evaluator gets an adapter over that new instance");
        await Assert.That(((ExtensionEvaluatorAdapter)second[1]).Inner).IsTypeOf<SdkFake>();
        await Assert.That(built).IsEqualTo(2);
    }

    private sealed class EvaluatorPack : IExtension
    {
        public string Id => "net.demoviewer.pack.evaluators";
        public string FeatureId => "pack.evaluators";
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }

    private sealed class SdkFake(string id, int generation = 0) : IExtensionEvaluator
    {
        public int Generation { get; } = generation;
        public string Id { get; } = id;

        public bool Wants(string path) => false;

        public void Evaluate(string path, ParsedDemo parsed)
        {
            // not exercised here
        }
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
