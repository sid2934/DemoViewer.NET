#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The shell's open is the demo's visit: a queued visit of the demo waits for the open and runs on the
///     parse the open produced, one submitted during the open joins it, an open that ends without running them
///     lets them read the file themselves, and a visit of the demo loaded in the shell runs on the held parse.
/// </summary>
public class DemoOpenVisitTests
{
    private const string Demo = "/d/match.dem";
    private static readonly byte[] Bytes = [1];

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeavyJobGate Gate = new();
        public readonly DemoProcessingQueue Queue;
        public readonly ParsedDemo OpenParse = SyntheticParsedDemo.Create();
        public int FileParses;
        public int ByteParses;

        public Rig(Action<ParsedDemo>? parseReleased = null) =>
            Queue = new DemoProcessingQueue(Gate, a => a(),
                parseBytes: _ =>
                {
                    Interlocked.Increment(ref ByteParses);
                    return OpenParse;
                },
                compactHeap: () => Task.CompletedTask,
                parseFileWithPlan: (_, plan) =>
                {
                    Interlocked.Increment(ref FileParses);
                    return SyntheticParsedDemo.Create(plan: plan);
                },
                parseReleased: parseReleased);

        public void Dispose()
        {
            Queue.Dispose();
            Gate.Dispose();
        }
    }

    private static DemoVisitRequest Visit(Action<IDemoPass, PassOutcome, Exception?>? ended, params IDemoPass[] passes) =>
        new(Demo, PassLevel.Backlog, passes, 0, "match.dem", ended);

    [Test]
    public async Task AQueuedVisit_WaitsForTheOpen_AndRunsOnItsParse_WithoutReadingTheFile()
    {
        using Rig rig = new();
        List<PassOutcome> outcomes = [];
        List<ParsedDemo> seen = [];
        FakePass library = new("library") { OnRun = input => seen.Add(input.Retained!) };
        FakePass facts = new("facts", "library") { OnRun = input => seen.Add(input.Retained!) };

        rig.Queue.Pause();
        IDemoQueueHandle handle = rig.Queue.SubmitVisit(Visit((_, outcome, _) => outcomes.Add(outcome), facts, library));
        using IDemoOpenTicket open = rig.Queue.BeginOpen(Demo, "match.dem");
        rig.Queue.Resume();

        await Assert.That(rig.Queue.Snapshot().Single(s => s.Kind == QueueJobKind.DemoProcessing).Detail)
            .IsEqualTo("Runs on the open of this demo");
        ParsedDemo parsed = await open.ParseAsync(Bytes);
        await open.RunPassesAsync(parsed);
        open.Complete();
        await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(rig.FileParses).IsEqualTo(0).Because("the open's parse served the visit");
            await Assert.That(rig.ByteParses).IsEqualTo(1);
            await Assert.That(library.Ran.Concat(facts.Ran)).IsEquivalentTo(["library", "facts"]);
            await Assert.That(seen).IsEquivalentTo([rig.OpenParse, rig.OpenParse]);
            await Assert.That(outcomes).IsEquivalentTo([PassOutcome.Ran, PassOutcome.Ran]);
            await Assert.That(handle.State).IsEqualTo(DemoQueueItemState.Completed);
        }
    }

    [Test]
    public async Task TheOpenRunsPassesInAfterOrder_AndAsksEachAgainFirst()
    {
        using Rig rig = new();
        List<string> ran = [];
        FakePass a = new("a") { Ran = ran };
        FakePass b = new("b", "a") { Ran = ran };
        FakePass c = new("c", "b") { Ran = ran, Answer = PassInterest.No };
        rig.Queue.Pause();
        IDemoQueueHandle handle = rig.Queue.SubmitVisit(Visit(null, c, b, a));
        using IDemoOpenTicket open = rig.Queue.BeginOpen(Demo, "match.dem");
        rig.Queue.Resume();

        await open.RunPassesAsync(await open.ParseAsync(Bytes));
        open.Complete();
        await handle.Completion;

        await Assert.That(ran).IsEquivalentTo(["a", "b"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task AVisitSubmittedDuringTheOpen_JoinsIt()
    {
        using Rig rig = new();
        FakePass library = new("library");
        using IDemoOpenTicket open = rig.Queue.BeginOpen(Demo, "match.dem");
        ParsedDemo parsed = await open.ParseAsync(Bytes);

        IDemoQueueHandle handle = rig.Queue.SubmitVisit(Visit(null, library));
        await Assert.That(handle.State).IsEqualTo(DemoQueueItemState.Queued);

        await open.RunPassesAsync(parsed);
        open.Complete();
        await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(library.Ran).IsEquivalentTo(["library"]);
            await Assert.That(rig.FileParses).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ThePlanRunsBeforeTheParkedVisitsAreTaken_SoWhatItSubmitsRunsOnTheOpen()
    {
        using Rig rig = new();
        FakePass library = new("library");
        using IDemoOpenTicket open = rig.Queue.BeginOpen(Demo, "match.dem");
        ParsedDemo parsed = await open.ParseAsync(Bytes);
        IDemoQueueHandle? handle = null;

        await open.RunPassesAsync(parsed, () => handle = rig.Queue.SubmitVisit(Visit(null, library)));
        open.Complete();
        await handle!.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(library.Ran).IsEquivalentTo(["library"]);
            await Assert.That(rig.FileParses).IsEqualTo(0);
        }
    }

    [Test]
    public async Task AnOpenThatEndsWithoutRunningPasses_LetsTheVisitReadTheFileItself()
    {
        using Rig rig = new();
        FakePass library = new("library");
        rig.Queue.Pause();
        IDemoQueueHandle handle = rig.Queue.SubmitVisit(Visit(null, library));
        IDemoOpenTicket open = rig.Queue.BeginOpen(Demo, "match.dem");
        rig.Queue.Resume();
        await Task.Delay(50);
        await Assert.That(library.Ran).IsEmpty().Because("the visit waits for the open");

        open.Dispose();
        await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(library.Ran).IsEquivalentTo(["library"]);
            await Assert.That(rig.FileParses).IsEqualTo(1).Because("no open parse came, so the visit read the file");
        }
    }

    [Test]
    public async Task ANewerOpenOfAnotherDemo_ReleasesTheVisitParkedBehindTheReplacedOne()
    {
        using Rig rig = new();
        FakePass library = new("library");
        rig.Queue.Pause();
        IDemoQueueHandle handle = rig.Queue.SubmitVisit(Visit(null, library));
        using IDemoOpenTicket first = rig.Queue.BeginOpen(Demo, "match.dem");
        using IDemoOpenTicket second = rig.Queue.BeginOpen("/d/other.dem", "other.dem");
        rig.Queue.Resume();

        ParsedDemo parsed = await second.ParseAsync(Bytes);
        await second.RunPassesAsync(parsed);
        second.Complete();
        await handle.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(library.Ran).IsEquivalentTo(["library"]);
            await Assert.That(rig.FileParses).IsEqualTo(1).Because("the replaced open never produced a parse for it");
        }
    }

    [Test]
    public async Task TheOpenReleasesTheParse_OnceItsPassesHaveRun()
    {
        List<ParsedDemo> released = [];
        using Rig rig = new(released.Add);
        using IDemoOpenTicket open = rig.Queue.BeginOpen(Demo, "match.dem");
        ParsedDemo parsed = await open.ParseAsync(Bytes);

        await open.RunPassesAsync(parsed);
        open.Complete();

        await Assert.That(released).IsEquivalentTo([rig.OpenParse]);
    }

    [Test]
    public async Task AVisitOfTheLoadedDemo_RunsOnTheShellsParse_AndHoldsItOnlyWhileItRuns()
    {
        using Rig rig = new();
        Lease lease = new(Demo, rig.OpenParse);
        rig.Queue.ShellDemo = lease;
        List<ParsedDemo> seen = [];
        bool heldDuringRun = false;
        FakePass grenades = new("grenades")
        {
            NeedsValue = PassNeeds.RetainedParse,
            OnRun = input =>
            {
                seen.Add(input.Retained!);
                heldDuringRun = lease.Holds == 1;
            }
        };

        IDemoQueueHandle handle = rig.Queue.SubmitVisit(Visit(null, grenades));
        await handle.Completion;
        IDemoQueueHandle other = rig.Queue.SubmitVisit(new DemoVisitRequest("/d/other.dem", PassLevel.Backlog, [new FakePass("library")]));
        await other.Completion;

        using (Assert.Multiple())
        {
            await Assert.That(seen).IsEquivalentTo([rig.OpenParse]).Because("the loaded demo is never read again");
            await Assert.That(heldDuringRun).IsTrue();
            await Assert.That(lease.Holds).IsEqualTo(0).Because("the hold ends with the passes");
            await Assert.That(rig.FileParses).IsEqualTo(1).Because("only the other demo was read");
            await Assert.That(handle.State).IsEqualTo(DemoQueueItemState.Completed);
        }
    }

    private sealed class Lease(string path, ParsedDemo parsed) : IShellDemoLease
    {
        public int Holds;

        public IHeldParse? TryHold(string demo) =>
            string.Equals(demo, path, StringComparison.OrdinalIgnoreCase) ? new Hold(this, parsed) : null;

        private sealed class Hold : IHeldParse
        {
            private readonly Lease _owner;

            public Hold(Lease owner, ParsedDemo parsed)
            {
                _owner = owner;
                Parsed = parsed;
                Interlocked.Increment(ref owner.Holds);
            }

            public ParsedDemo Parsed { get; }

            public void Dispose() => Interlocked.Decrement(ref _owner.Holds);
        }
    }

    private sealed class FakePass(string id, params string[] after) : IDemoPass
    {
        public List<string> Ran { get; init; } = [];
        public PassNeeds NeedsValue { get; init; } = PassNeeds.RetainedWithoutUserCommands;
        public PassInterest Answer { get; init; } = PassInterest.Yes;
        public Action<PassInput>? OnRun { get; init; }

        public string Id => id;
        public IReadOnlyList<string> After => after;

        public PassNeeds Needs(VisitedDemo demo) => NeedsValue;

        public PassInterest Interest(VisitedDemo demo, PassLevel level) => Answer;

        public void Run(PassInput input)
        {
            lock (Ran)
            {
                Ran.Add(id);
            }

            OnRun?.Invoke(input);
        }
    }
}
