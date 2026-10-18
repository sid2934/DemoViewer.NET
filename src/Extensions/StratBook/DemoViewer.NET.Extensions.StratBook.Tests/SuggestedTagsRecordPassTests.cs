#region

using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Suggested Tags' record pass: after a visit stored a demo's detonations, the proposals follow a moved
///     round index without a parse, and a fresh session asks for nothing while the stored proposals are newer
///     than every input.
/// </summary>
public class SuggestedTagsRecordPassTests
{
    private sealed record Wired(
        SuggestedTagsReviewHarness Harness,
        MemoryDemoData Data,
        RoundIndexStore Index,
        RoundIndexPlaceSources Sources,
        DetonationStore Detonations)
    {
        public SuggestedTagsService Session(Action? onWalk = null) =>
            new(Harness.Cache.Library(), Harness.Cache.RoundFacts(), Harness.Proposals, Harness.Tags, Harness.Regions,
                () => DetectorProfile.Default, () => true, () => true, Index, Sources,
                walk: _ =>
                {
                    onWalk?.Invoke();
                    return Walk();
                },
                utcNow: () => Now, detonations: Detonations);

        public LibraryDemo Row => Harness.Cache.Library().Find(DemoPath)!;

        // The visit: the index first, then Suggested Tags on the same parse.
        public void Visit(SuggestedTagsService service)
        {
            CS2DemoKit.Parser.ParsedDemo parsed = Parse();
            new RoundIndexEvaluator(Harness.Cache.Library(), Harness.Cache.RoundFacts(), Index, Sources, () => true, walk: _ => Walk())
                .Evaluate(DemoPath, parsed);
            service.Evaluate(DemoPath, parsed, []);
        }

        // Writes the index again with the same content, so only its stamp moves.
        public async Task MoveTheIndex()
        {
            long before = Index.Stamp(DemoPath)!.WrittenAtTicks;
            RoundIndexDocument document = Index.TryRead(DemoPath)!;
            RoundPositionsDocument positions = Index.TryReadPositions(DemoPath)!;
            while (DateTime.UtcNow.Ticks <= before)
            {
                await Task.Delay(1);
            }

            Index.Write(DemoPath, document, positions, Index.Stamp(DemoPath)!.Fingerprint!);
        }
    }

    private static Wired Wire()
    {
        SuggestedTagsReviewHarness h = new();
        h.Seed();
        MemoryDemoData data = h.Cache.Data();
        return new Wired(h, data, new RoundIndexStore(data), new RoundIndexPlaceSources(() => RoundIndexTokenSource.Pawn),
            new DetonationStore(data));
    }

    [Test]
    public async Task AFreshSession_WantsNothing_WhileTheProposalsAreNewerThanTheirInputs()
    {
        Wired w = Wire();
        w.Visit(w.Session());

        SuggestedTagsService fresh = w.Session();

        using (Assert.Multiple())
        {
            await Assert.That(w.Detonations.IsCurrent(DemoPath)).IsTrue().Because("the visit stored what it read off the parse");
            await Assert.That(fresh.RecordPass.Wants(w.Row)).IsFalse();
            await Assert.That(fresh.RecordPass.Wants(w.Row)).IsFalse().Because("asking again changes nothing");
        }
    }

    [Test]
    public async Task AMovedIndex_RebuildsTheProposalsWithoutAParse()
    {
        Wired w = Wire();
        SuggestedTagsService session = w.Session();
        w.Visit(session);
        string visitProposals = Proposals(w);
        await Assert.That(session.RecordPass.Wants(w.Row)).IsFalse();

        await w.MoveTheIndex();
        await Assert.That(session.RecordPass.Wants(w.Row)).IsTrue();
        int walks = 0;
        SuggestedTagsService rebuilder = w.Session(() => walks++);
        await Assert.That(rebuilder.RecordPass.Wants(w.Row)).IsTrue().Because("a fresh session sees the index newer than the proposals");
        long before = w.Harness.Proposals.Stamp(DemoPath)!.WrittenAtTicks;
        rebuilder.RecordPass.Run(new LibraryDemoDetail(w.Row, 64, 20000, 0, [], []), CancellationToken.None);

        using (Assert.Multiple())
        {
            await Assert.That(w.Harness.Proposals.Stamp(DemoPath)!.WrittenAtTicks).IsGreaterThan(before);
            await Assert.That(Proposals(w)).IsEqualTo(visitProposals).Because("the same inputs, read back from storage");
            await Assert.That(walks).IsEqualTo(0).Because("the index is current, so nothing walks");
            await Assert.That(rebuilder.RecordPass.Wants(w.Row)).IsFalse();
        }
    }

    [Test]
    public async Task AFreshSession_RebuildsProposalsOlderThanTheirRoundFacts()
    {
        Wired w = Wire();
        w.Visit(w.Session());
        long built = w.Harness.Proposals.Stamp(DemoPath)!.WrittenAtTicks;
        while (DateTime.UtcNow.Ticks <= built)
        {
            await Task.Delay(1);
        }

        // Round Facts alone move, under a new fingerprint, before any session has seen the demo again.
        w.Harness.Cache.UpdateExisting(DemoPath, r => r.SetRoundFacts(Rows(), "rf-B"));
        SuggestedTagsService fresh = w.Session();
        await Assert.That(fresh.RecordPass.Wants(w.Row)).IsTrue().Because("the proposals were built from the old rows");

        fresh.RecordPass.Run(new LibraryDemoDetail(w.Row, 64, 20000, 0, [], []), CancellationToken.None);
        using (Assert.Multiple())
        {
            await Assert.That(w.Harness.Proposals.Stamp(DemoPath)!.WrittenAtTicks).IsGreaterThan(built);
            await Assert.That(fresh.RecordPass.Wants(w.Row)).IsFalse();
            await Assert.That(w.Session().RecordPass.Wants(w.Row)).IsFalse().Because("the rebuilt proposals are newer than every input");
        }
    }

    [Test]
    public async Task AStaleIndex_LeavesTheBuildToTheVisit()
    {
        Wired w = Wire();
        SuggestedTagsService session = w.Session();
        w.Visit(session);

        w.Index.Write(DemoPath, w.Index.TryRead(DemoPath)!, w.Index.TryReadPositions(DemoPath)!, "another-fingerprint");

        await Assert.That(session.RecordPass.Wants(w.Row)).IsFalse();
    }

    private static string Proposals(Wired w) =>
        string.Join('\n', w.Harness.Proposals.TryRead(DemoPath)!.Proposals.Select(p => $"{p.Id} {p.FromTick} {p.ToTick} {p.Confidence}"));
}
