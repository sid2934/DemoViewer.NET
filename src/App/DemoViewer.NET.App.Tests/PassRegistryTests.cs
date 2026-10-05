#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoProcessing;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <see cref="PassRegistry" /> on its own, with fakes: the topological sort, its tie-break, the
///     cycle and unknown-id guards, the lazy pack gate (a disabled pack's factory is never invoked), and
///     the evaluator adapter it wraps the old evaluators in. Pure logic; no container, no queue.
/// </summary>
public class PassRegistryTests
{
    [Test]
    public async Task Resolve_OrdersByAfter_DeclarationOrderBreaksTies()
    {
        PassRegistry registry = new();
        // "c" is declared first but depends on "a", declared after it. "b" has no dependency at all.
        // Once "a" resolves, "c" becomes ready and, being declared before "b", is picked next.
        registry.AddCore("c", () => new Fake("c", "a"), "a");
        registry.AddCore("a", () => new Fake("a"));
        registry.AddCore("b", () => new Fake("b"));

        IReadOnlyList<IDemoPass> resolved = registry.Resolve();

        await Assert.That(resolved.Select(e => e.Id))
            .IsEquivalentTo(["a", "c", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_SixEntries_MatchesTheLibraryHighlightsPackChain()
    {
        // The exact chain StratBookPack.Contribute declares: roundFacts after library, roundIndex after
        // roundFacts, suggestedTags after roundIndex, grenades after library.
        PassRegistry registry = new();
        registry.AddCore("library", () => new Fake("library"));
        registry.AddCore("highlights", () => new Fake("highlights"));
        registry.AddPacksLazily(() =>
        {
            registry.AddPackPass("roundfacts", () => new Fake("roundfacts", "library"), ["library"], () => true);
            registry.AddPackPass("roundindex", () => new Fake("roundindex", "roundfacts"), ["roundfacts"], () => true);
            registry.AddPackPass("suggestedtags", () => new Fake("suggestedtags", "roundindex"), ["roundindex"], () => true);
            registry.AddPackPass("grenades", () => new Fake("grenades", "library"), ["library"], () => true);
        });

        IReadOnlyList<IDemoPass> resolved = registry.Resolve();

        await Assert.That(resolved.Select(e => e.Id))
            .IsEquivalentTo(["library", "highlights", "roundfacts", "roundindex", "suggestedtags", "grenades"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public void Resolve_UnknownAfterId_Throws()
    {
        PassRegistry registry = new();
        registry.AddCore("a", () => new Fake("a", "ghost"), "ghost");

        Assert.Throws<InvalidOperationException>(() => registry.Resolve());
    }

    [Test]
    public void Resolve_Cycle_Throws()
    {
        PassRegistry registry = new();
        registry.AddCore("a", () => new Fake("a", "b"), "b");
        registry.AddCore("b", () => new Fake("b", "a"), "a");

        Assert.Throws<InvalidOperationException>(() => registry.Resolve());
    }

    [Test]
    public void AddCore_DuplicateId_ThrowsImmediately()
    {
        PassRegistry registry = new();
        registry.AddCore("a", () => new Fake("a"));

        // Caught at the point of declaration, not deferred to Resolve(): a second pack landing on the
        // same id is a composition-time mistake, not a runtime gate decision.
        Assert.Throws<InvalidOperationException>(() => registry.AddCore("a", () => new Fake("a")));
    }

    [Test]
    public void Resolve_FactoryBuildsAnInstanceWithTheWrongId_Throws()
    {
        PassRegistry registry = new();
        registry.AddCore("a", () => new Fake("not-a"));

        Assert.Throws<InvalidOperationException>(() => registry.Resolve());
    }

    [Test]
    public void Resolve_FactoryBuildsAnInstanceWithOtherAfterIds_Throws()
    {
        PassRegistry registry = new();
        registry.AddCore("library", () => new Fake("library"));
        registry.AddCore("a", () => new Fake("a"), "library");

        // The registry sorts by the declared After; a pass that orders itself differently would run out of
        // the order the visit enforces.
        Assert.Throws<InvalidOperationException>(() => registry.Resolve());
    }

    // A pack pass whose factory throws, or builds the wrong id, is left out of that resolve and
    // reported; every other pass, the core library included, still resolves.
    [Test]
    public async Task Resolve_APackFactoryThatThrows_IsLeftOutAndReported_AndTheRestGoOn()
    {
        PassRegistry registry = new();
        List<string> reported = [];
        registry.AddCore("core", () => new Fake("core"));
        registry.AddPackPass("broken", () => throw new InvalidOperationException("factory"), ["core"], () => true,
            ex => reported.Add("broken: " + ex.Message));
        registry.AddPackPass("misnamed", () => new Fake("other", "core"), ["core"], () => true,
            ex => reported.Add("misnamed"));
        registry.AddPackPass("fine", () => new Fake("fine", "core"), ["core"], () => true, _ => reported.Add("fine"));

        IReadOnlyList<IDemoPass> resolved = registry.Resolve();

        using (Assert.Multiple())
        {
            await Assert.That(resolved.Select(e => e.Id)).IsEquivalentTo(["core", "fine"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(reported).IsEquivalentTo(["broken: factory", "misnamed"]);
        }
    }

    [Test]
    public async Task Resolve_PackDisabled_NeverInvokesItsFactory_AndExcludesItFromTheOrder()
    {
        PassRegistry registry = new();
        int packFactoryCalls = 0;
        registry.AddCore("library", () => new Fake("library"));
        registry.AddPacksLazily(() =>
        {
            registry.AddPackPass("pack", () =>
            {
                packFactoryCalls++;
                return new Fake("pack");
            }, [], () => false);
        });

        IReadOnlyList<IDemoPass> resolved = registry.Resolve();

        await Assert.That(resolved.Select(e => e.Id)).IsEquivalentTo(["library"]);
        await Assert.That(packFactoryCalls).IsEqualTo(0)
            .Because("a disabled pack's pass must never be constructed, not merely excluded after construction");
    }

    [Test]
    public async Task Resolve_PackEnabledAfterAnEarlierCall_IsIncludedOnTheNextResolve()
    {
        PassRegistry registry = new();
        bool packOn = false;
        registry.AddCore("library", () => new Fake("library"));
        registry.AddPacksLazily(() =>
        {
            registry.AddPackPass("pack", () => new Fake("pack"), [], () => packOn);
        });

        await Assert.That(registry.Resolve().Select(e => e.Id)).IsEquivalentTo(["library"]);

        packOn = true;

        await Assert.That(registry.Resolve().Select(e => e.Id)).IsEquivalentTo(["library", "pack"]);
    }

    [Test]
    public async Task Resolve_PendingPathsUnion_IsTheUnionOfTheResolvedEvaluatorsPendingPaths()
    {
        PassRegistry registry = new();
        registry.AddCoreEvaluator("library", () => new EvaluatorFake("library") { Pending = ["/a.dem", "/b.dem"] });
        registry.AddCoreEvaluator("highlights", () => new EvaluatorFake("highlights") { Pending = ["/b.dem", "/c.dem"] });
        registry.AddPacksLazily(() =>
        {
            // Disabled: its paths must not appear in the union, and its factory must not run.
            registry.AddPackEvaluator("pack", () => throw new InvalidOperationException("must not run"),
                [], () => false);
        });

        IReadOnlyList<string> union = [.. registry.Resolve().OfType<EvaluatorPassAdapter>()
            .SelectMany(e => e.Evaluator.PendingPaths()).Distinct(StringComparer.OrdinalIgnoreCase)];

        await Assert.That(union).IsEquivalentTo(["/a.dem", "/b.dem", "/c.dem"]);
    }

    [Test]
    public void Validate_Cycle_Throws()
    {
        PassRegistry registry = new();
        registry.AddCore("a", () => new Fake("a", "b"), "b");
        registry.AddCore("b", () => new Fake("b", "a"), "a");

        Assert.Throws<InvalidOperationException>(() => registry.Validate());
    }

    [Test]
    public void Validate_UnknownAfterId_Throws()
    {
        PassRegistry registry = new();
        registry.AddCore("a", () => new Fake("a", "ghost"), "ghost");

        Assert.Throws<InvalidOperationException>(() => registry.Validate());
    }

    [Test]
    public async Task Validate_PopulatesAndSorts_ButNeverMaterializesAPass()
    {
        PassRegistry registry = new();
        int coreFactoryCalls = 0;
        int packFactoryCalls = 0;
        registry.AddCore("library", () =>
        {
            coreFactoryCalls++;
            return new Fake("library");
        });
        registry.AddPacksLazily(() =>
        {
            registry.AddPackPass("roundfacts", () =>
            {
                packFactoryCalls++;
                return new Fake("roundfacts", "library");
            }, ["library"], () => true);
        });

        registry.Validate();

        await Assert.That(coreFactoryCalls).IsEqualTo(0);
        await Assert.That(packFactoryCalls).IsEqualTo(0);

        // The sort Validate computed is reused: Resolve right after still materializes correctly.
        IReadOnlyList<IDemoPass> resolved = registry.Resolve();

        await Assert.That(resolved.Select(e => e.Id))
            .IsEquivalentTo(["library", "roundfacts"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(coreFactoryCalls).IsEqualTo(1);
        await Assert.That(packFactoryCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Resolve_PopulateThrows_IsNeverRetriedOnALaterCall()
    {
        PassRegistry registry = new();
        registry.AddCore("library", () => new Fake("library"));
        int populateCalls = 0;
        registry.AddPacksLazily(() =>
        {
            populateCalls++;
            registry.AddPackPass("roundfacts", () => new Fake("roundfacts"), [], () => true);
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

        PassRegistry registry = new();
        foreach (PassContribution c in contributions.Passes)
        {
            registry.AddPackPass(c.Id, c.Factory, c.After, () => true);
        }

        IReadOnlyList<IDemoPass> first = registry.Resolve();
        IReadOnlyList<IDemoPass> second = registry.Resolve();

        await Assert.That(ReferenceEquals(first[0], second[0])).IsTrue()
            .Because("the same evaluator instance is not re-wrapped on every resolve");
        await Assert.That(ReferenceEquals(first[1], second[1])).IsFalse()
            .Because("a factory that builds a new evaluator gets an adapter over that new instance");
        await Assert.That(((ExtensionEvaluatorAdapter)((EvaluatorPassAdapter)second[1]).Evaluator).Inner).IsTypeOf<SdkFake>();
        await Assert.That(built).IsEqualTo(2);
    }

    [Test]
    public async Task Adapter_MapsWantsToInterest_AndForwardForToNeeds()
    {
        EvaluatorFake evaluator = new("e") { Wanted = ["/yes.dem"], Upstream = ["/later.dem"], Forward = ForwardNeeds.Rules };
        EvaluatorPassAdapter pass = new(evaluator, ["library"]);

        using (Assert.Multiple())
        {
            await Assert.That(pass.Id).IsEqualTo("e");
            await Assert.That(pass.After).IsEquivalentTo(["library"]);
            await Assert.That(pass.Interest(new VisitedDemo("/yes.dem"), PassLevel.Background)).IsEqualTo(PassInterest.Yes);
            await Assert.That(pass.Interest(new VisitedDemo("/later.dem"), PassLevel.Background)).IsEqualTo(PassInterest.IfUpstreamRuns);
            await Assert.That(pass.Interest(new VisitedDemo("/no.dem"), PassLevel.Background)).IsEqualTo(PassInterest.No);
            await Assert.That(pass.Needs(new VisitedDemo("/yes.dem")))
                .IsEqualTo(new PassNeeds(ParseMode.Forward, ForwardNeeds.Rules, false));
            await Assert.That(new EvaluatorPassAdapter(new EvaluatorFake("r") { ReadsUserCommands = true }, []).Needs(new VisitedDemo("/x.dem")))
                .IsEqualTo(new PassNeeds(ParseMode.Retained, ForwardNeeds.None, true));
        }
    }

    [Test]
    public async Task Adapter_RunsEvaluateOnARetainedParse_EvaluateForwardOnAForwardRead_AndForwardsFailures()
    {
        EvaluatorFake evaluator = new("e") { Forward = ForwardNeeds.Rules };
        EvaluatorPassAdapter pass = new(evaluator, []);
        VisitedDemo demo = new("/x.dem");
        ForwardDemoResult forward = new()
        {
            Demo = new DemoDescriptor("de_test", 64, 1f / 64, 1000, 0, "t", "t", 0, DemoProfile.Unknown, new Dictionary<int, PlayerInfo>()),
            Rounds = [],
            FrameCount = 1,
            FirstServerTick = 0,
            LastServerTick = 1
        };

        pass.Run(new PassInput(demo, PassLevel.Background, SyntheticParsedDemo.Create(), null, CancellationToken.None));
        pass.Run(new PassInput(demo, PassLevel.Background, null, forward, CancellationToken.None));
        pass.OnFailed(demo, new InvalidDataException("bad"));

        using (Assert.Multiple())
        {
            await Assert.That(evaluator.Evaluated).IsEquivalentTo(["/x.dem"]);
            await Assert.That(evaluator.EvaluatedForward).IsEquivalentTo(["/x.dem"]);
            await Assert.That(evaluator.Failed).IsEquivalentTo(["/x.dem"]);
        }
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

    private sealed class EvaluatorFake(string id) : IDemoEvaluator
    {
        public string Id { get; } = id;
        public IReadOnlyList<string> Pending { get; init; } = [];
        public HashSet<string> Wanted { get; init; } = [];
        public HashSet<string> Upstream { get; init; } = [];
        public ForwardNeeds? Forward { get; init; }
        public bool ReadsUserCommands { get; init; }
        public List<string> Evaluated { get; } = [];
        public List<string> EvaluatedForward { get; } = [];
        public List<string> Failed { get; } = [];

        public bool Wants(string path) => Wanted.Contains(path);

        public bool WantsAfterUpstream(string path) => Upstream.Contains(path);

        public ForwardNeeds? ForwardFor(string path) => Forward;

        public void Evaluate(string path, ParsedDemo parsed) => Evaluated.Add(path);

        public void EvaluateForward(string path, ForwardDemoResult pass) => EvaluatedForward.Add(path);

        public void OnFailed(string path) => Failed.Add(path);

        public IReadOnlyList<string> PendingPaths() => Pending;
    }

    private sealed class Fake(string id, params string[] after) : IDemoPass
    {
        public string Id { get; } = id;
        public IReadOnlyList<string> After { get; } = after;

        public PassNeeds Needs(VisitedDemo demo) => PassNeeds.RetainedParse;

        public PassInterest Interest(VisitedDemo demo, PassLevel level) => PassInterest.No;

        public void Run(PassInput input)
        {
            // not exercised here
        }
    }
}
