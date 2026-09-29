#region

using DemoViewer.NET.Services.Generated;
using DemoViewer.NET.Services.Strats.Mining;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>The shared state of machine-generated items (docs/strat-book/generated-content.md).</summary>
public class GeneratedStateTests
{
    [Test]
    public async Task NewOnly_ShowsNewAlone_AndAllShowsEverything()
    {
        GeneratedState[] states = Enum.GetValues<GeneratedState>();
        using (Assert.Multiple())
        {
            await Assert.That(states.Where(GeneratedFilter.NewOnly.Shows)).IsEquivalentTo([GeneratedState.New]);
            await Assert.That(states.Where(GeneratedFilter.All.Shows)).IsEquivalentTo(states);
            await Assert.That(new GeneratedFilter(Reviewed: true, Dismissed: false, Accepted: false).Shows(GeneratedState.Dismissed)).IsFalse();
        }
    }

    [Test]
    public async Task Counts_SplitNewFromSettled()
    {
        GeneratedCounts counts = GeneratedCounts.Of([GeneratedState.New, GeneratedState.New, GeneratedState.Reviewed,
            GeneratedState.Dismissed, GeneratedState.Accepted, GeneratedState.Accepted]);

        using (Assert.Multiple())
        {
            await Assert.That(counts).IsEqualTo(new GeneratedCounts(2, 1, 1, 2));
            await Assert.That(counts.Settled).IsEqualTo(4);
            await Assert.That(counts.Total).IsEqualTo(6);
        }
    }

    [Test]
    public async Task ADetectedPattern_DismissedWinsOverPromoted()
    {
        MinedPattern pattern = StratMiningServiceTests.Pattern("k", 1, 2);
        using (Assert.Multiple())
        {
            await Assert.That(new DetectedPattern(pattern, false, null).State).IsEqualTo(GeneratedState.New);
            await Assert.That(new DetectedPattern(pattern, false, Guid.NewGuid()).State).IsEqualTo(GeneratedState.Accepted);
            await Assert.That(new DetectedPattern(pattern, true, Guid.NewGuid()).State).IsEqualTo(GeneratedState.Dismissed)
                .Because("the inbox hides a dismissed pattern even when it is in a book");
        }
    }
}
