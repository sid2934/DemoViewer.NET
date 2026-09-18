#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Which player's materialized nodes the Analysis graph draws.
///     <para>
///         The graph can only show one player at a time, so this choice decides what the user sees.
///         Both methods under test are pure statics over the evaluation's
///         <c>MaterializedPlayers</c>, which is what makes them testable without an evaluation.
///     </para>
/// </summary>
public sealed class GraphPlayerSelectionTests
{
    [Test]
    public async Task NoRoster_SelectsNothing()
    {
        await Assert.That(AnalysisViewModel.ResolveGraphPlayerForTests(Players(), null)).IsEmpty()
            .Because("a demo that materialized no player draws the scaffolding and stops");
    }

    [Test]
    public async Task NoPreference_FallsBackToTheLowestSlot()
    {
        // Discovery order, not slot order: a coming engine change hands players back this way, and
        // the graph must not reshuffle because a round started differently.
        List<PerPlayerNodeTemplate.MaterializedPlayer> chosen =
            AnalysisViewModel.ResolveGraphPlayerForTests(Players((9, "c"), (1, "a"), (5, "b")), null);

        await Assert.That(chosen.Count).IsEqualTo(1);
        await Assert.That(chosen[0].PlayerSlot).IsEqualTo(1);
    }

    [Test]
    public async Task APreferredSlot_IsHonoured()
    {
        // Note what this does and does not pin. The resolver honouring a preference was always
        // correct; the bug was in RunAsync, which captured the preference AFTER ClearFullGraph had
        // nulled the field holding it, so this branch was never reached. Pinning that needs a test at
        // the RunAsync level, over a real evaluation, which nothing here does.
        List<PerPlayerNodeTemplate.MaterializedPlayer> chosen =
            AnalysisViewModel.ResolveGraphPlayerForTests(Players((1, "a"), (5, "b"), (9, "c")), 5);

        await Assert.That(chosen.Count).IsEqualTo(1);
        await Assert.That(chosen[0].PlayerSlot).IsEqualTo(5);
    }

    [Test]
    public async Task APreferredSlotThatDidNotMaterialize_FallsBackToTheLowest()
    {
        List<PerPlayerNodeTemplate.MaterializedPlayer> chosen =
            AnalysisViewModel.ResolveGraphPlayerForTests(Players((1, "a"), (5, "b")), 42);

        await Assert.That(chosen[0].PlayerSlot).IsEqualTo(1);
    }

    [Test]
    public async Task EveryMaterializationOfASlotIsReturned_NotJustTheFirst()
    {
        // A v1 config beside a v2 ruleset materializes the same human once per template. Taking only
        // the first drew one template's nodes and left the other's edges with no endpoint.
        List<PerPlayerNodeTemplate.MaterializedPlayer> chosen =
            AnalysisViewModel.ResolveGraphPlayerForTests(
                [Player(3, "x", 0), Player(3, "x", 1), Player(7, "y", 0)], 3);

        await Assert.That(chosen.Count).IsEqualTo(2);
        await Assert.That(chosen.Select(p => p.TemplateIndex).Order().ToList()).IsEquivalentTo([0, 1]);
    }

    [Test]
    public async Task TheLowestSlotFallbackAlsoReturnsEveryTemplate()
    {
        List<PerPlayerNodeTemplate.MaterializedPlayer> chosen =
            AnalysisViewModel.ResolveGraphPlayerForTests(
                [Player(7, "y", 0), Player(2, "x", 0), Player(2, "x", 1)], null);

        await Assert.That(chosen.Count).IsEqualTo(2);
        await Assert.That(chosen.All(p => p.PlayerSlot == 2)).IsTrue();
    }

    private static List<PerPlayerNodeTemplate.MaterializedPlayer> Players(params (int Slot, string Name)[] players) =>
        [.. players.Select(p => Player(p.Slot, p.Name, 0))];

    private static PerPlayerNodeTemplate.MaterializedPlayer Player(int slot, string name, int templateIndex) =>
        new(slot, name, [], [], [], [], null, templateIndex);
}
