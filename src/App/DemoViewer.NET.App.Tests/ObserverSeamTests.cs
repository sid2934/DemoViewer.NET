#region

using System.Globalization;
using System.Reflection;
using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.TestSupport;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The collector seam: an extension's per-frame reader declared on the host's rules run through the engine's
///     frame observer. The run declares the union of the visit's reads, a collector writes the same result off a
///     forward read as off a held parse, a throwing collector fails only its own pass, the engine's unknown names
///     are refused when the extension registers, and a collector's stamp moves with what it reads.
/// </summary>
[NotInParallel]
public class ObserverSeamTests
{
    private const string Demo = "/d/match.dem";

    [Test]
    public async Task TheAdapterUnionsDeclaredReadsIntoTheOptions()
    {
        CollectorSet set = CollectorSet.Of(
        [
            CollectorKit.Register(CollectorKit.Counting("a", providers: ["entity.pawn.pos_x", "entity.pawn.place"], classes: ["CSmokeGrenadeProjectile"])),
            CollectorKit.Register(CollectorKit.Counting("b", providers: ["entity.pawn.place", "entity.pawn.health"],
                classes: ["CMolotovProjectile", "CSmokeGrenadeProjectile"]))
        ]);
        AnalysisOptions plain = new() { CaptureSnapshots = false };

        (AnalysisOptions declared, CollectorObserver? observer) = set.Declare(plain);
        (AnalysisOptions untouched, CollectorObserver? none) = CollectorSet.Empty.Declare(plain);

        using (Assert.Multiple())
        {
            await Assert.That(declared.ObservedProviders).IsEquivalentTo(["entity.pawn.health", "entity.pawn.place", "entity.pawn.pos_x"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(declared.ObservedProjectileClasses).IsEquivalentTo(["CMolotovProjectile", "CSmokeGrenadeProjectile"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(declared.FrameObserver).IsSameReferenceAs(observer);
            await Assert.That(declared.CaptureSnapshots).IsEqualTo(false).Because("the caller's options are kept");
            await Assert.That(untouched).IsSameReferenceAs(plain).Because("a run with no collector declares nothing");
            await Assert.That(none).IsNull();
            await Assert.That(set.IsSupersetOf(CollectorSet.Of([CollectorKit.Register(CollectorKit.Counting("a",
                providers: ["entity.pawn.pos_x", "entity.pawn.place"], classes: ["CSmokeGrenadeProjectile"]))]))).IsTrue();
        }
    }

    [Test]
    [Category("RealDemo")]
    public async Task ACollectorRunsForwardAndRetained_WithIdenticalSidecars()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        CollectorSet set = CollectorSet.Of([CollectorKit.Register(PositionsText.Contribution)]);
        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null))
        {
            CollectorsFor = _ => set
        };

        string? retained = rules.Collected(parsed, path).Get<PositionsText.Result>(PositionsText.Id)?.Text;
        rules.Forget(parsed);
        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult forward = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, rules.Docs, null, null, set);
        string? read = forward.Collected.Get<PositionsText.Result>(PositionsText.Id)?.Text;

        using (Assert.Multiple())
        {
            await Assert.That(retained).IsNotNull();
            await Assert.That(retained!.Length).IsGreaterThan(1000).Because("the collector saw pawns on the held parse");
            await Assert.That(read).IsEqualTo(retained).Because("the forward read's rules run carries the same frames and columns");
        }
    }

    [Test]
    public async Task ACollectorThatThrows_FailsItsPassNotTheVisit()
    {
        ExtensionGuard guard = ExtensionPassHostTests.StandaloneGuard();
        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null));
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
            parseBytes: _ => MergedRulesBuildTests.TwoFrameDemo(),
            compactHeap: () => Task.CompletedTask,
            parseFileWithPlan: (_, _) => MergedRulesBuildTests.TwoFrameDemo());
        rules.CollectorsFor = queue.CollectorsFor;
        queue.CollectedFor = rules.Collected;

        ReadingPass broken = new("ext.broken", "throws");
        ReadingPass healthy = new("ext.healthy", "counts");
        Dictionary<string, PassOutcome> outcomes = [];
        IDemoQueueHandle handle = queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Background,
        [
            new ExtensionPassHost(broken, [], guard, static () => false,
                collectors: CollectorSet.Of([new RegisteredCollector(CollectorKit.Throwing("throws", "ext.broken"), guard)])),
            new ExtensionPassHost(healthy, [], guard, static () => false,
                collectors: CollectorSet.Of([new RegisteredCollector(CollectorKit.Counting("counts", "ext.healthy"), guard)]))
        ], 0, "match.dem", (pass, outcome, _) =>
        {
            lock (outcomes)
            {
                outcomes[pass.Id] = outcome;
            }
        }));
        await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(handle.State).IsEqualTo(DemoQueueItemState.Completed).Because("the visit outlives one collector's throw");
            await Assert.That(outcomes["ext.broken"]).IsEqualTo(PassOutcome.Failed);
            await Assert.That(outcomes["ext.healthy"]).IsEqualTo(PassOutcome.Ran);
            await Assert.That(healthy.Frames).IsEqualTo(2).Because("the other collector saw every frame of the run");
            await Assert.That(broken.Failure?.InnerException?.Message).IsEqualTo(CollectorKit.ThrowMessage);
        }
    }

    [Test]
    public async Task ACollectorReadsOnlyWhatItDeclared_EvenBesideOneThatDeclaredMore()
    {
        ExtensionGuard guard = ExtensionPassHostTests.StandaloneGuard();
        MergedRulesBuild rules = new(() => YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null));
        using DemoProcessingQueue queue = new(new HeavyJobGate(), a => a(),
            parseBytes: _ => MergedRulesBuildTests.TwoFrameDemo(),
            compactHeap: () => Task.CompletedTask,
            parseFileWithPlan: (_, _) => MergedRulesBuildTests.TwoFrameDemo());
        rules.CollectorsFor = queue.CollectorsFor;
        queue.CollectedFor = rules.Collected;

        ReadingPass declaring = new("ext.declaring", "declares");
        ReadingPass borrowing = new("ext.borrowing", "borrows");
        Dictionary<string, PassOutcome> outcomes = [];
        IDemoQueueHandle handle = queue.SubmitVisit(new DemoVisitRequest(Demo, PassLevel.Background,
        [
            new ExtensionPassHost(declaring, [], guard, static () => false,
                collectors: CollectorSet.Of([new RegisteredCollector(
                    CollectorKit.Reading("declares", "ext.declaring", ["entity.pawn.place"], "entity.pawn.place"), guard)])),
            new ExtensionPassHost(borrowing, [], guard, static () => false,
                collectors: CollectorSet.Of([new RegisteredCollector(
                    CollectorKit.Reading("borrows", "ext.borrowing", [], "entity.pawn.place"), guard)]))
        ], 0, "match.dem", (pass, outcome, _) =>
        {
            lock (outcomes)
            {
                outcomes[pass.Id] = outcome;
            }
        }));
        await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(outcomes["ext.declaring"]).IsEqualTo(PassOutcome.Ran);
            await Assert.That(outcomes["ext.borrowing"]).IsEqualTo(PassOutcome.Failed)
                .Because("the provider is on the run only because the other collector declared it");
            await Assert.That(borrowing.Failure?.InnerException?.Message).Contains("not declared by collector borrows");
        }
    }

    [Test]
    public async Task ACollectorReadingUserCommands_WidensThePlan()
    {
        CollectorSet reading = CollectorSet.Of([CollectorKit.Register(CollectorKit.Counting("cmds") with
        {
            ReadsUserCommands = true, GameEvents = ["decoy_started", "round_freeze_end"]
        })]);
        CollectorSet silent = CollectorSet.Of([CollectorKit.Register(CollectorKit.Counting("none"))]);
        IReadOnlyList<RulesetDoc> docs = YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null).Rulesets;
        ParsedDemo parsed = MergedRulesBuildTests.TwoFrameDemo();

        // Forward: the host's own plan, from the pass's needs and the collectors' events.
        BuildResult plain = ForwardDemoPass.Build(parsed, docs, null, silent);
        DecodePlan forward = ForwardDemoPass.PlanFor(plain, ForwardNeeds.Rules | ForwardNeeds.UserCommands, reading.GameEvents);
        DecodePlan without = ForwardDemoPass.PlanFor(plain, ForwardNeeds.Rules, silent.GameEvents);
        // Retained: the build the run is made from, whose engine plan the visit's parse must cover.
        BuildResult declared = ForwardDemoPass.Build(parsed, docs, null, reading);
        (AnalysisOptions evaluate, _) = reading.Declare(new AnalysisOptions { CaptureSnapshots = false });
        DemoVisit visit = new(new VisitedDemo(Demo), PassLevel.Background);
        ExtensionGuard guard = ExtensionPassHostTests.StandaloneGuard();
        ExtensionPassHost host = new(new ExtensionPassHostTests.FakePass { Reads = PassRead.Forward }, [], guard, static () => false,
            collectors: CollectorSet.Of([new RegisteredCollector(CollectorKit.Counting("cmds", ExtensionPassHostTests.FakePass.PassId) with { ReadsUserCommands = true }, guard)]));
        TestPass other = new("retained") { NeedsValue = PassNeeds.RetainedWithoutUserCommands };
        visit.Add(host, host.Needs(visit.Demo), null);
        visit.Add(other, other.Needs(visit.Demo), null);

        using (Assert.Multiple())
        {
            await Assert.That(forward.Categories.HasFlag(MessageCategories.UserCmds)).IsTrue();
            await Assert.That(without.Categories.HasFlag(MessageCategories.UserCmds)).IsFalse();
            await Assert.That(forward.GameEventNames!.Contains("decoy_started")).IsTrue().Because("the collector reads it off the frames");
            await Assert.That(without.GameEventNames!.Contains("decoy_started")).IsFalse();
            await Assert.That(declared.DecodesUserCommands).IsTrue().Because("the build plans the lane");
            await Assert.That(DemoAnalysis.PlanDecode(declared).Categories.HasFlag(MessageCategories.UserCmds)).IsTrue();
            await Assert.That(plain.DecodesUserCommands).IsFalse();
            await Assert.That(evaluate.DecodeUserCommands).IsTrue();
            await Assert.That(visit.ChooseMode(true)).IsEqualTo((false, ForwardNeeds.Rules | ForwardNeeds.UserCommands, true))
                .Because("the retained visit the forward pass joins keeps the commands in its parse");
        }
    }

    [Test]
    public async Task AnUnknownProviderName_IsRefusedAtRegistration()
    {
        ExtensionPassHostTestsPack pack = new();
        ExtensionFaults faults = ExtensionFaults.For([pack], static a => a());
        PackContributions contributions = new(pack, () => null!, guard: faults.GuardFor(pack));

        contributions.Collector(CollectorKit.Counting("dev.example.collectors.bad.provider", providers: ["entity.pawn.no_such_read"]));
        contributions.Collector(CollectorKit.Counting("dev.example.collectors.bad.class", classes: ["CNoSuchProjectile"]));
        contributions.Collector(CollectorKit.Counting("dev.example.collectors.good", providers: ["entity.pawn.place"], classes: ["CSmokeGrenadeProjectile"]));
        contributions.Collector(CollectorKit.Counting("dev.example.collectors.good"));

        using (Assert.Multiple())
        {
            await Assert.That(contributions.Collectors.Select(c => c.Id)).IsEquivalentTo(["dev.example.collectors.good"]);
            await Assert.That(RegisteredCollector.Refusal(CollectorKit.Counting("x", providers: ["entity.pawn.no_such_read"]))).Contains("entity.pawn.no_such_read");
            await Assert.That(faults.StateOf(pack.FeatureId).Count).IsEqualTo(3).Because("each refusal is reported against the extension");
        }
    }

    [Test]
    public async Task ACollectorIdOutsideTheExtensionsOwn_IsRefusedAtRegistration()
    {
        ExtensionPassHostTestsPack pack = new();
        ExtensionFaults faults = ExtensionFaults.For([pack], static a => a());
        PackContributions contributions = new(pack, () => null!, guard: faults.GuardFor(pack));

        contributions.Collector(CollectorKit.Counting("dev.other.positions"));
        contributions.Collector(CollectorKit.Counting("dev.example.collectors.positions"));

        using (Assert.Multiple())
        {
            await Assert.That(contributions.Collectors.Select(c => c.Id)).IsEquivalentTo(["dev.example.collectors.positions"])
                .Because("two extensions must never share a collector's results");
            await Assert.That(faults.StateOf(pack.FeatureId).Count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ACollectorStamp_MovesWithTheDeclaredReads()
    {
        CollectorContribution base_ = CollectorKit.Counting("stamp", providers: ["entity.pawn.pos_x", "entity.pawn.place"], classes: ["CSmokeGrenadeProjectile"]);

        using (Assert.Multiple())
        {
            await Assert.That((base_ with { Providers = ["entity.pawn.place", "entity.pawn.pos_x"] }).Stamp).IsEqualTo(base_.Stamp)
                .Because("declaration order is not a different read");
            await Assert.That((base_ with { Providers = ["entity.pawn.place"] }).Stamp).IsNotEqualTo(base_.Stamp);
            await Assert.That((base_ with { ProjectileClasses = [] }).Stamp).IsNotEqualTo(base_.Stamp);
            await Assert.That((base_ with { Version = 2 }).Stamp).IsNotEqualTo(base_.Stamp);
            await Assert.That((base_ with { ReadsUserCommands = true }).Stamp).IsNotEqualTo(base_.Stamp);
            await Assert.That((base_ with { GameEvents = ["weapon_fire"] }).Stamp).IsNotEqualTo(base_.Stamp);
            await Assert.That((base_ with { GameEvents = ["weapon_fire", "decoy_started"] }).Stamp)
                .IsEqualTo((base_ with { GameEvents = ["decoy_started", "weapon_fire"] }).Stamp);
            await Assert.That(base_.Stamp)
                .Contains(typeof(DemoFrame).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion)
                .Because("an engine build moves it, a patch or prerelease included");
            await Assert.That(CollectorSet.Of([CollectorKit.Register(base_ with { Version = 2 })])
                .IsSupersetOf(CollectorSet.Of([CollectorKit.Register(base_)]))).IsFalse().Because("a run carried an older collector");
        }
    }

    // A pass that reads its one collector's result.
    private sealed class ReadingPass(string id, string collectorId) : IExtensionPass
    {
        public int Frames { get; private set; }

        public Exception? Failure { get; private set; }

        public string Id => id;

        public DemoInterest Interest(string demoPath) => DemoInterest.Yes;

        public void Run(IPassContext context)
        {
            try
            {
                Frames = context.Collected<CollectorKit.Count>(collectorId)?.Frames ?? -1;
            }
            catch (Exception ex)
            {
                Failure = ex;
                throw;
            }
        }
    }

    // Every 64th frame's pawn positions, places and teams as text: a stand-in for a sidecar a pass writes.
    private sealed class PositionsText : IFrameCollector
    {
        public const string Id = "positions.text";

        private static readonly string[] Reads = ["entity.pawn.pos_x", "entity.pawn.pos_y", "entity.pawn.place"];

        private readonly StringBuilder _text = new();

        public static CollectorContribution Contribution { get; } = new(Id, "ext.positions", 1, Reads, [], () => new PositionsText());

        public void OnFrame(IFrameReadView view)
        {
            if (view.FrameIndex % 64 != 0)
            {
                return;
            }

            ObservedRead x = view.Provider(Reads[0]), y = view.Provider(Reads[1]), place = view.Provider(Reads[2]);
            for (int slot = 0; slot < 64; slot++)
            {
                if (view.TryReadFloat(x, slot, out float px) && view.TryReadFloat(y, slot, out float py))
                {
                    view.TryReadString(place, slot, out string? where);
                    _text.Append(CultureInfo.InvariantCulture, $"{view.FrameIndex}/{view.Frame.ServerTick} {slot} {px:R} {py:R} {where} {view.Team(slot)}\n");
                }
            }
        }

        public void Finish(ICollectorSink sink) => sink.Put(new Result(_text.ToString()));

        public sealed record Result(string Text);
    }

    private sealed class ExtensionPassHostTestsPack : IExtension
    {
        public string Id => "dev.example.collectors";
        public string FeatureId => "pack.collectors";
        public IEnumerable<ExtensionFeature> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}

/// <summary>Collectors for the seam tests.</summary>
internal static class CollectorKit
{
    public const string ThrowMessage = "collector boom";

    public static RegisteredCollector Register(CollectorContribution contribution) => new(contribution, null);

    /// <summary>Counts frames; reads nothing unless told to declare something.</summary>
    public static CollectorContribution Counting(string id, string passId = "ext.pass", IReadOnlyList<string>? providers = null,
        IReadOnlyList<string>? classes = null) =>
        new(id, passId, 1, providers ?? [], classes ?? [], () => new Counter());

    public static CollectorContribution Throwing(string id, string passId = "ext.pass") =>
        new(id, passId, 1, [], [], () => new Thrower());

    /// <summary>Counts frames and resolves <paramref name="reads" /> on each, declaring only <paramref name="declared" />.</summary>
    public static CollectorContribution Reading(string id, string passId, IReadOnlyList<string> declared, string reads) =>
        new(id, passId, 1, declared, [], () => new Reader(reads));

    public sealed record Count(int Frames);

    private sealed class Counter : IFrameCollector
    {
        private int _frames;

        public void OnFrame(IFrameReadView view) => _frames++;

        public void Finish(ICollectorSink sink) => sink.Put(new Count(_frames));
    }

    private sealed class Reader(string reads) : IFrameCollector
    {
        private int _frames;

        public void OnFrame(IFrameReadView view)
        {
            _ = view.Provider(reads);
            _frames++;
        }

        public void Finish(ICollectorSink sink) => sink.Put(new Count(_frames));
    }

    private sealed class Thrower : IFrameCollector
    {
        public void OnFrame(IFrameReadView view) => throw new InvalidOperationException(ThrowMessage);

        public void Finish(ICollectorSink sink) => throw new InvalidOperationException(ThrowMessage);
    }
}
