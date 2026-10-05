#region

using DemoViewer.NET.Services.Generated;
using DemoViewer.NET.Services.RoundFacts;
using DemoViewer.NET.Extensions.StratBook.Services.Strats.Mining;
using DemoViewer.NET.Extensions.StratBook.Services.RoundFactsPass;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>The shared state of machine-generated items.</summary>
public class GeneratedStateTests
{
    [Test]
    public async Task ThereIsNoReviewedState_OutsideReviewClips() =>
        await Assert.That(Enum.GetNames<GeneratedState>()).IsEquivalentTo(["New", "Dismissed", "Accepted"]);

    [Test]
    public async Task OnlyNewShows_UntilSettledIsOn()
    {
        GeneratedState[] states = Enum.GetValues<GeneratedState>();
        using (Assert.Multiple())
        {
            await Assert.That(states.Where(s => GeneratedInbox.Shows(s, false))).IsEquivalentTo([GeneratedState.New]);
            await Assert.That(states.Where(s => GeneratedInbox.Shows(s, true))).IsEquivalentTo(states);
            await Assert.That(GeneratedInbox.SettledLabel(3)).IsEqualTo("Show settled (3)");
        }
    }

    [Test]
    public async Task Counts_SplitNewFromSettled()
    {
        GeneratedCounts counts = GeneratedCounts.Of([GeneratedState.New, GeneratedState.New,
            GeneratedState.Dismissed, GeneratedState.Accepted, GeneratedState.Accepted]);

        using (Assert.Multiple())
        {
            await Assert.That(counts).IsEqualTo(new GeneratedCounts(2, 1, 2));
            await Assert.That(counts.Settled).IsEqualTo(3);
            await Assert.That(counts.Total).IsEqualTo(5);
        }
    }

    [Test]
    public async Task ADetectedPattern_DismissedWinsOverPromoted()
    {
        // A minimal MinedPattern, not StratMiningServiceTests.Pattern: that helper lives in the
        // extension test project and this file tests the core GeneratedState contract only.
        MinedPattern pattern = new()
        {
            Key = "k",
            Kind = PatternKind.Setup,
            Map = "de_nuke",
            Side = 2,
            Members = [new MinedMember("/d/m1.dem", null, 1, null, true, BuyType.Full, 0, 0, 64, 0)],
            Medoid = new RoundSignature
            {
                DemoPath = "/d/m1.dem", Round = 1, Map = "de_nuke", Side = 2, Kind = PatternKind.Setup, Anchors = []
            }
        };
        using (Assert.Multiple())
        {
            await Assert.That(new DetectedPattern(pattern, false, null).State).IsEqualTo(GeneratedState.New);
            await Assert.That(new DetectedPattern(pattern, false, Guid.NewGuid()).State).IsEqualTo(GeneratedState.Accepted);
            await Assert.That(new DetectedPattern(pattern, true, Guid.NewGuid()).State).IsEqualTo(GeneratedState.Dismissed);
        }
    }
}
