#region

using System.Collections.Concurrent;
using CS2DemoKit.Parser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The record passes run on the real queue without reading a demo: a changed row asks every pass, the
///     record is read once for all of them, a pass runs again only when its row changed, a throwing pass is
///     skipped for that demo, and an extension's pass sees the SDK's rows.
/// </summary>
[NotInParallel]
public class RecordPassRunnerTests
{
    private static readonly Action<Action> _inline = a => a();

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

    private static DemoProcessingQueue CountingQueue(Action onParse) =>
        new(new HeavyJobGate(), _inline, _ =>
        {
            onParse();
            throw new InvalidOperationException("a record pass never parses");
        });

    [Test]
    public async Task AChangedRow_RunsEveryWantingPass_WithNoParse_AndNotTwiceOnOneRow()
    {
        int parses = 0;
        using DemoProcessingQueue queue = CountingQueue(() => Interlocked.Increment(ref parses));
        DemoCacheStore store = new(null);
        Recording a = new("a", _ => true);
        Recording b = new("b", e => e.Map == "de_nuke");
        using RecordPassRunner runner = new(() => [a, b], store, queue);

        store.Upsert(HostLibraryTests.Parsed("/d/one.dem"));
        await WaitFor(() => a.Runs.Count == 1, "pass a");
        runner.RecheckAll();
        runner.DemoChanged("/d/one.dem");
        await Task.Delay(100);

        await Assert.That(a.Runs).IsEquivalentTo(["/d/one.dem"]);
        await Assert.That(b.Runs).IsEmpty();
        await Assert.That(parses).IsEqualTo(0);

        store.UpdateExisting("/d/one.dem", r => r.Map = "de_nuke");
        await WaitFor(() => a.Runs.Count == 2 && b.Runs.Count == 1, "both passes on the changed row");
        await Assert.That(parses).IsEqualTo(0);
    }

    [Test]
    public async Task AThrowingPass_IsSkippedForThatDemo_AndReported_WhileTheOtherRuns()
    {
        using DemoProcessingQueue queue = CountingQueue(() => { });
        DemoCacheStore store = new(null);
        Recording thrower = new("thrower", _ => true) { Throws = true };
        Recording other = new("other", _ => true);
        ConcurrentBag<string> faults = [];
        using RecordPassRunner runner = new(() => [thrower, other], store, queue)
        {
            Faulted = (pass, path, _) => faults.Add(pass.Id + "@" + path)
        };

        store.Upsert(HostLibraryTests.Parsed("/d/one.dem"));
        await WaitFor(() => other.Runs.Count == 1, "the other pass");
        store.UpdateExisting("/d/one.dem", r => r.Map = "de_inferno");
        await WaitFor(() => other.Runs.Count == 2, "the other pass again");

        await Assert.That(thrower.Runs.Count).IsEqualTo(1);
        await Assert.That(runner.IsFaulted("thrower", "/d/one.dem")).IsTrue();
        await Assert.That(faults).IsEquivalentTo(["thrower@/d/one.dem"]);
    }

    [Test]
    public async Task AnExtensionsPass_SeesTheSdkRowAndDetail()
    {
        using DemoProcessingQueue queue = CountingQueue(() => { });
        DemoCacheStore store = new(null);
        HostLibrary library = new(store, null, () => false);
        SdkPass inner = new();
        ExtensionRecordPassHost host = new(inner, ExtensionGuard.Standalone(new HostLibraryTests.LibraryTestExtension()), library);
        using RecordPassRunner runner = new(() => [host], store, queue);

        store.Upsert(HostLibraryTests.Parsed("/d/one.dem"));
        await WaitFor(() => inner.Seen.Count == 1, "the extension's pass");

        LibraryDemoDetail detail = inner.Seen.Single();
        await Assert.That(ReferenceEquals(detail.Demo, library.Find("/d/one.dem"))).IsTrue();
        await Assert.That(detail.Players.Count).IsEqualTo(8);
        await Assert.That(host.Owner).IsEqualTo("test.library");
    }

    private sealed class Recording(string id, Func<DemoCacheIndexEntry, bool> wants) : IRecordPass
    {
        public ConcurrentQueue<string> Runs { get; } = new();

        public bool Throws { get; init; }

        public string Id => id;

        public bool Wants(DemoCacheIndexEntry entry) => wants(entry);

        public void Run(DemoCacheRecord record, CancellationToken cancellationToken)
        {
            Runs.Enqueue(record.Path);
            if (Throws)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }

    private sealed class SdkPass : IExtensionRecordPass
    {
        public ConcurrentQueue<LibraryDemoDetail> Seen { get; } = new();

        public string Id => "sdk.records";

        public bool Wants(LibraryDemo demo) => demo.State >= LibraryDemoState.Parsed;

        public void Run(LibraryDemoDetail detail, CancellationToken cancellationToken) => Seen.Enqueue(detail);
    }
}
