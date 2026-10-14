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
///     A record pass registered with <c>dependsOn</c> is held until the named inputs are written, runs again when
///     their stamp moves and not on other row writes, waits unreported while a named ruleset or facet owner is off,
///     and is refused on every run while it names a key the library does not write.
/// </summary>
[NotInParallel]
public class RecordPassDependsOnTests
{
    private const string Demo = "/d/one.dem";
    private static readonly FactKey Kast = new("kast", "kast_game_totals");
    private static readonly Action<Action> _inline = a => a();

    private static readonly string[] Refusals =
    [
        "test.library.scoreboard: depends on kast/scoreboard, which the library does not write.",
        "test.library.unknown: depends on kast/no_such_table, which the library does not write."
    ];

    private static readonly string[] UnknownOwnerRefusal =
        ["test.library.reader: depends on test.nobody:round-index, which the library does not write."];

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
    public async Task ADependsOnOnAnUnknownKey_IsRefusedByTheRunner()
    {
        using DemoProcessingQueue queue = Queue();
        DemoCacheStore store = new(null);
        FactKey scoreboard = new("kast", FactKey.ScoreboardOutput);
        FactKey unknown = new("kast", "no_such_table");
        (PackContributions contributions, HostLibrary library) = Host(store, Kast, scoreboard);
        SdkPass unknownPass = new("test.library.unknown");
        SdkPass scoreboardPass = new("test.library.scoreboard");
        SdkPass known = new("test.library.known");
        contributions.RecordPass(unknownPass.Id, () => unknownPass, Kast, unknown);
        contributions.RecordPass(scoreboardPass.Id, () => scoreboardPass, scoreboard);
        contributions.RecordPass(known.Id, () => known, Kast);
        List<IRecordPass> passes = Resolve(contributions, library);
        ConcurrentQueue<string> faults = new();
        using RecordPassRunner runner = new(() => passes, store, queue)
        {
            Faulted = (pass, _, ex) => faults.Enqueue(pass.Id + ": " + ex.Message)
        };

        store.Upsert(HostLibraryTests.Parsed(Demo));
        Stamp(store, "kast", 1);
        await WaitFor(() => known.Seen.Count == 1, "the pass naming a declared key");
        Stamp(store, "kast", 2);
        await WaitFor(() => known.Seen.Count == 2, "the moved stamp");

        await Assert.That(unknownPass.Seen).IsEmpty();
        await Assert.That(scoreboardPass.Seen).IsEmpty();
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

    [Test]
    public async Task ARecordPassNamingRoundFacts_RunsWhenTheRowsAreWritten()
    {
        using DemoProcessingQueue queue = Queue();
        DemoCacheStore store = new(null);
        (PackContributions contributions, HostLibrary library) = Host(store);
        SdkPass reader = new("test.library.reader");
        SdkPass barrier = new("test.library.barrier");
        contributions.RecordPass(reader.Id, () => reader, DemoDataKey.RoundFacts);
        contributions.RecordPass(barrier.Id, () => barrier);
        List<IRecordPass> passes = Resolve(contributions, library);
        ConcurrentQueue<string> faults = new();
        using RecordPassRunner runner = new(() => passes, store, queue)
        {
            Faulted = (pass, _, ex) => faults.Enqueue(pass.Id + ": " + ex.Message)
        };

        store.Upsert(HostLibraryTests.Parsed(Demo));
        await WaitFor(() => barrier.Seen.Count == 1, "the row");
        await Assert.That(reader.Seen.Count).IsEqualTo(0).Because("no rows are written yet");

        store.UpdateExisting(Demo, r => r.SetStamp(new PackStamp(RoundFactsRows.FacetId, RoundFactsRows.CurrentSchema, "a")));
        await WaitFor(() => reader.Seen.Count == 1, "the rows");
        store.UpdateExisting(Demo, r => r.Map = "de_nuke");
        await WaitFor(() => barrier.Seen.Count == 3, "the unrelated row write");
        await Assert.That(reader.Seen.Count).IsEqualTo(1);

        store.UpdateExisting(Demo, r => r.SetStamp(new PackStamp(RoundFactsRows.FacetId, RoundFactsRows.CurrentSchema, "b")));
        await WaitFor(() => reader.Seen.Count == 2, "the rows written again");
        await Assert.That(faults).IsEmpty();
    }

    [Test]
    public async Task ARecordPassNamingAnExtensionsFacet_ReRunsWhenItsStampMoves()
    {
        using DemoProcessingQueue queue = Queue();
        DemoCacheStore store = new(null);
        (PackContributions contributions, HostLibrary library) = Host(store);
        DemoDataKey index = new("test.owner", "round-index");
        FakeData data = new();
        bool ownerOn = true;
        DemoDataOwner owner = new(data, () => ownerOn);
        SdkPass reader = new("test.library.reader");
        SdkPass barrier = new("test.library.barrier");
        contributions.RecordPass(reader.Id, () => reader, index, DemoDataKey.RoundFacts);
        contributions.RecordPass(barrier.Id, () => barrier);
        List<IRecordPass> passes =
        [
            .. contributions.RecordPasses.Select(r =>
                ExtensionRecordPassHost.Cached(r, () => library, id => id == index.Owner ? owner : null)())
        ];
        ConcurrentQueue<string> faults = new();
        using RecordPassRunner runner = new(() => passes, store, queue)
        {
            Faulted = (pass, _, ex) => faults.Enqueue(pass.Id + ": " + ex.Message)
        };

        store.Upsert(HostLibraryTests.Parsed(Demo));
        await WaitFor(() => barrier.Seen.Count == 1, "the row");
        store.UpdateExisting(Demo, r => r.SetStamp(new PackStamp(RoundFactsRows.FacetId, RoundFactsRows.CurrentSchema, "a")));
        await WaitFor(() => barrier.Seen.Count == 2, "the rows");
        await Assert.That(reader.Seen.Count).IsEqualTo(0).Because("the facet is not written yet");

        data.Set(Demo, index.Facet, "fp", 1);
        runner.DemoChanged(Demo);
        await WaitFor(() => reader.Seen.Count == 1, "the facet's write");

        runner.DemoChanged(Demo);
        store.UpdateExisting(Demo, r => r.Map = "de_nuke");
        await WaitFor(() => barrier.Seen.Count == 3, "the unrelated row write");
        await Assert.That(reader.Seen.Count).IsEqualTo(1).Because("the facet's stamp did not move");

        ownerOn = false;
        data.Set(Demo, index.Facet, "fp", 2);
        store.UpdateExisting(Demo, r => r.Map = "de_inferno");
        await WaitFor(() => barrier.Seen.Count == 4, "a row write while the owner is off");
        await Assert.That(reader.Seen.Count).IsEqualTo(1).Because("the owner is off");

        ownerOn = true;
        runner.RecheckAll();
        await WaitFor(() => reader.Seen.Count == 2, "the owner coming on with a moved stamp");
        await Assert.That(faults).IsEmpty();
    }

    [Test]
    public async Task AFacetOfAnUnknownExtension_IsRefusedByTheRunner()
    {
        using DemoProcessingQueue queue = Queue();
        DemoCacheStore store = new(null);
        (PackContributions contributions, HostLibrary library) = Host(store);
        SdkPass reader = new("test.library.reader");
        SdkPass barrier = new("test.library.barrier");
        contributions.RecordPass(reader.Id, () => reader, new DemoDataKey("test.nobody", "round-index"));
        contributions.RecordPass(barrier.Id, () => barrier);
        List<IRecordPass> passes = Resolve(contributions, library);
        ConcurrentQueue<string> faults = new();
        using RecordPassRunner runner = new(() => passes, store, queue)
        {
            Faulted = (pass, _, ex) => faults.Enqueue(pass.Id + ": " + ex.Message)
        };

        store.Upsert(HostLibraryTests.Parsed(Demo));
        await WaitFor(() => barrier.Seen.Count == 1, "the row");
        await Assert.That(reader.Seen).IsEmpty();
        await Assert.That(faults.ToArray()).IsEquivalentTo(UnknownOwnerRefusal);
    }

    private sealed class FakeData : IExtensionDemoData
    {
        private readonly ConcurrentDictionary<(string Path, string Facet), DemoDataStamp> _stamps = new();

        public bool IsAvailable => true;

        public void Set(string path, string facet, string fingerprint, long writtenAt) =>
            _stamps[(path, facet)] = new DemoDataStamp(path, null, facet, 1, fingerprint, DemoDataState.Written, writtenAt, 0);

        public DemoDataStamp? Stamp(string demoPath, string facet) => _stamps.GetValueOrDefault((demoPath, facet));

        public IReadOnlyList<DemoDataStamp> Stamps(string facet) => [.. _stamps.Values.Where(s => s.Facet == facet)];

        public byte[]? Read(string demoPath, string facet, int schema, string? fingerprint, string? part = null) => null;

        public DemoDataRecord? ReadAny(string demoPath, string facet, string? part = null) => null;

        public DemoDataStamp? Write(string demoPath, DemoDataWrite write) => throw new NotSupportedException();

        public void MarkFailed(string demoPath, string facet) => throw new NotSupportedException();

        public void ClearFailed(string demoPath, string facet) => throw new NotSupportedException();

        public void Invalidate(string facet, string? demoPath = null) => throw new NotSupportedException();

        public void SetCount(string demoPath, string facet, int count) => throw new NotSupportedException();

        public void Delete(string demoPath, string facet) => throw new NotSupportedException();

        public event Action<string?>? Changed
        {
            add { }
            remove { }
        }
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
