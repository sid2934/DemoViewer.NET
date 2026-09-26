#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Match Overview's "Index grenades" (grenade-walk.md §3.11): offered on a cached page whose demo has no
///     current walk, handing the page's own demo to the evaluator, stepping aside once pressed, and absent
///     where the host wired no evaluator (the browser).
/// </summary>
public class MatchOverviewGrenadeActionTests
{
    private const string Demo = "/demos/grenades.dem";

    private static DemoCacheRecord Parsed() => new()
    {
        Path = Demo,
        Size = 10,
        ModifiedTicks = 20,
        Map = "de_mirage",
        Parse = new TierStamp
        {
            Schema = DemoCacheRecord.ParseSchema,
            ComputedAtTicks = 1
        },
        TickRate = 64
    };

    [Test]
    public async Task ACachedPageWithoutAWalk_OffersTheAction_AndHandsOverItsDemo()
    {
        List<string> requested = [];
        MatchOverviewTabViewModel vm = new()
        {
            IndexGrenades = requested.Add,
            AreGrenadesIndexed = _ => false
        };
        vm.SetCachedRecord(Parsed());

        await Assert.That(vm.HasIndexGrenadesAction).IsTrue();

        vm.RequestGrenadeIndexCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(requested).IsEquivalentTo(new[] { Demo });
            await Assert.That(vm.HasIndexGrenadesAction).IsFalse().Because("pressed once, it steps aside");
        }
    }

    [Test]
    public async Task AWalkedDemo_OrAHostWithoutTheEvaluator_OffersNothing()
    {
        MatchOverviewTabViewModel walked = new()
        {
            IndexGrenades = _ => { },
            AreGrenadesIndexed = _ => true
        };
        walked.SetCachedRecord(Parsed());
        MatchOverviewTabViewModel browser = new();
        browser.SetCachedRecord(Parsed());

        using (Assert.Multiple())
        {
            await Assert.That(walked.HasIndexGrenadesAction).IsFalse();
            await Assert.That(browser.HasIndexGrenadesAction).IsFalse().Because("absent rather than inert");
        }
    }
}
