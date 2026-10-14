#region

using System.Collections.Concurrent;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A record pass registered with <c>dependsOn</c> is held until the named facts are written, runs again when
///     their stamp moves and not on other row writes, waits unreported while a named ruleset is off, and is refused
///     when it names a key the library does not write.
/// </summary>
[NotInParallel]
public class RecordPassDependsOnTests
{
    private const string Demo = "/d/one.dem";
    private static readonly FactKey Kast = new("kast", "kast_game_totals");
    private static readonly Action<Action> _inline = a => a();

    private static readonly string[] Refusals =
    [
        "test.library.roundfacts: depends on round_facts/round_facts, which the library does not write.",
        "test.library.scoreboard: depends on kast/scoreboard, which the library does not write.",
        "test.library.unknown: depends on kast/no_such_table, which the library does not write."
    ];

    private static async Task WaitFor(Func<bool> cond, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    private static DemoProcessingQueue Queue() =>
        new(new HeavyJobGate(), _inline, _ => throw new InvalidOperationException("a record pass never parses"));

    private static void Stamp(DemoCacheStore store, string rulesetId, long computedAt) =>
        store.UpdateExisting(Demo, r => r.SetStamp(new PackStamp(StampedFacts.StampId(rulesetId), StampedFacts.Schema, "fp")
        {
            ComputedAtTicks = computedAt
        }));

    private static (PackContributions Contributions, HostLibrary Library) Host(DemoCacheStore store, params FactKey[] declared)
    {
        PackContributions contributions = new(new HostLibraryTests.LibraryTestExtension(),
            () => throw new NotSupportedException("record passes read no context"));
        HostLibrary library = new(store, null, () => false, _ => { }, () => new DeclaredFacts(declared));
        return (contributions, library);
    }

    private static List<IRecordPass> Resolve(PackContributions contributions, HostLibrary library) =>
        [.. contributions.RecordPasses.Select(r => ExtensionRecordPassHost.Cached(r, () => library)())];

    [Test]
    public async Task ARecordPassReRuns_WhenANamedInputStampMoves()
    {
        using DemoProcessingQueue queue = Queue();
        DemoCacheStore store = new(null);
        (PackContributions contributions, HostLibrary library) = Host(store, Kast);
        SdkPass reader = new("test.library.reader");
        SdkPass barrier = new("test.library.barrier");
        contributions.RecordPass(reader.Id, () => reader, Kast);
        contributions.RecordPass(barrier.Id, () => barrier);
        List<IRecordPass> passes = Resolve(contributions, library);
        using RecordPassRunner runner = new(() => passes, store, queue);

        store.Upsert(HostLibraryTests.Parsed(Demo));
        await WaitFor(() => barrier.Seen.Count == 1, "the row");
        await Assert.That(reader.Seen.Count).IsEqualTo(0).Because("the named facts are not written yet");

        Stamp(store, "kast", 1);
        await WaitFor(() => reader.Seen.Count == 1, "the facts write");

        store.UpdateExisting(Demo, r => r.Map = "de_nuke");
        await WaitFor(() => barrier.Seen.Count == 3, "the unrelated row write");
        await Assert.That(reader.Seen.Count).IsEqualTo(1).Because("the named stamp did not move");

        Stamp(store, "other", 1);
        await WaitFor(() => barrier.Seen.Count == 4, "another ruleset's write");
        await Assert.That(reader.Seen.Count).IsEqualTo(1);

        Stamp(store, "kast", 2);
        await WaitFor(() => reader.Seen.Count == 2 && barrier.Seen.Count == 5, "the moved stamp");
    }

    [Test]
    public async Task ADependsOnOnAnUnknownKey_IsRefusedAtRegistration()
    {
        using DemoProcessingQueue queue = Queue();
        DemoCacheStore store = new(null);
        FactKey scoreboard = new("kast", FactKey.ScoreboardOutput);
        FactKey unknown = new("kast", "no_such_table");
        FactKey roundFacts = new(RoundFactsFingerprint.RulesetId, "round_facts");
        (PackContributions contributions, HostLibrary library) = Host(store, Kast, scoreboard);
        SdkPass unknownPass = new("test.library.unknown");
        SdkPass scoreboardPass = new("test.library.scoreboard");
        SdkPass roundFactsPass = new("test.library.roundfacts");
        SdkPass known = new("test.library.known");
        contributions.RecordPass(unknownPass.Id, () => unknownPass, Kast, unknown);
        contributions.RecordPass(scoreboardPass.Id, () => scoreboardPass, scoreboard);
        contributions.RecordPass(roundFactsPass.Id, () => roundFactsPass, roundFacts);
        contributions.RecordPass(known.Id, () => known, Kast);
        List<IRecordPass> passes = Resolve(contributions, library);
        ConcurrentQueue<string> faults = new();
        using RecordPassRunner runner = new(() => passes, store, queue)
        {
            Faulted = (pass, _, ex) => faults.Enqueue(pass.Id + ": " + ex.Message)
        };

        store.Upsert(HostLibraryTests.Parsed(Demo));
        Stamp(store, "kast", 1);
        Stamp(store, RoundFactsFingerprint.RulesetId, 1);
        await WaitFor(() => known.Seen.Count == 1, "the pass naming a declared key");
        Stamp(store, "kast", 2);
        await WaitFor(() => known.Seen.Count == 2, "the moved stamp");

        await Assert.That(unknownPass.Seen).IsEmpty();
        await Assert.That(scoreboardPass.Seen).IsEmpty();
        await Assert.That(roundFactsPass.Seen).IsEmpty();
        await Assert.That(faults.Order()).IsEquivalentTo(Refusals);
    }

    [Test]
    public async Task APassWaitingOnAnOffRuleset_RunsOnceItComesOn()
    {
        using DemoProcessingQueue queue = Queue();
        DemoCacheStore store = new(null);
        FactKey detonations = new("util", "detonations");
        SwitchedFacts facts = new(detonations);
        PackContributions contributions = new(new HostLibraryTests.LibraryTestExtension(),
            () => throw new NotSupportedException("record passes read no context"));
        HostLibrary library = new(store, null, () => false, _ => { }, () => facts);
        SdkPass reader = new("test.library.reader");
        SdkPass barrier = new("test.library.barrier");
        contributions.RecordPass(reader.Id, () => reader, detonations);
        contributions.RecordPass(barrier.Id, () => barrier);
        List<IRecordPass> passes = Resolve(contributions, library);
        ConcurrentQueue<string> faults = new();
        using RecordPassRunner runner = new(() => passes, store, queue)
        {
            Faulted = (pass, _, ex) => faults.Enqueue(pass.Id + ": " + ex.Message)
        };

        store.Upsert(HostLibraryTests.Parsed(Demo));
        await WaitFor(() => barrier.Seen.Count == 1, "the row");
        Stamp(store, "util", 1);
        await WaitFor(() => barrier.Seen.Count == 2, "the facts write");
        await Assert.That(reader.Seen.Count).IsEqualTo(0).Because("the ruleset's owner is off");
        await Assert.That(faults).IsEmpty().Because("an off ruleset holds the pass, it does not refuse it");

        facts.On = true;
        runner.RecheckAll();
        await WaitFor(() => reader.Seen.Count == 1, "the ruleset coming on");
        await Assert.That(faults).IsEmpty();
    }

    private sealed class SdkPass(string id) : IExtensionRecordPass
    {
        public ConcurrentQueue<LibraryDemoDetail> Seen { get; } = new();

        public string Id => id;

        public bool Wants(LibraryDemo demo) => demo.State >= LibraryDemoState.Parsed;

        public void Run(LibraryDemoDetail detail, CancellationToken cancellationToken) => Seen.Enqueue(detail);
    }

    private sealed class SwitchedFacts(FactKey key) : IAnalysisFacts, IFactsVisibility
    {
        public volatile bool On;

        public IReadOnlyList<FactKey> Declared => On ? [key] : [];

        public IRoundFacts RoundFacts => NoFacts.Instance;

        public string ShownKey => On ? key.RulesetId : "";

        public bool Shows(string stampId) => true;

        public bool IsOff(string rulesetId) => !On && string.Equals(rulesetId, key.RulesetId, StringComparison.Ordinal);

        public FactStatus Status(string demoPath, FactKey k) => FactStatus.Absent;

        public bool IsCurrent(string demoPath, FactKey k) => false;

        public FactTable? TryGet(string demoPath, FactKey k) => null;

        public IReadOnlyList<LibraryHighlight> Highlights(string demoPath) => [];
    }

    private sealed class DeclaredFacts(IReadOnlyList<FactKey> declared) : IAnalysisFacts
    {
        public IReadOnlyList<FactKey> Declared => declared;

        public IRoundFacts RoundFacts => NoFacts.Instance;

        public FactStatus Status(string demoPath, FactKey key) => FactStatus.Absent;

        public bool IsCurrent(string demoPath, FactKey key) => false;

        public FactTable? TryGet(string demoPath, FactKey key) => null;

        public IReadOnlyList<LibraryHighlight> Highlights(string demoPath) => [];
    }
}
