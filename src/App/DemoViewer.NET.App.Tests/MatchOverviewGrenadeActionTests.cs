#region

using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Match Overview's "Index grenades": offered on a cached page whose demo has no
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

    // The chip is core, but the evaluator behind it is pack-owned. With the pack off,
    // GrenadeIndexEvaluator.Wants rejects even a forced path, so a click that queued nothing would still
    // look successful without this: the chip must hide instead.
    [Test]
    public async Task WithThePackOff_TheChipIsHidden_EvenOnAnOtherwiseEligiblePage()
    {
        MatchOverviewTabViewModel vm = new()
        {
            IndexGrenades = _ => { },
            AreGrenadesIndexed = _ => false,
            PackEnabled = () => false
        };
        vm.SetCachedRecord(Parsed());

        await Assert.That(vm.HasIndexGrenadesAction).IsFalse();
    }

    [Test]
    public async Task PackEnabled_Null_ReadsAsEnabled_LikeEveryExistingCallSite()
    {
        MatchOverviewTabViewModel vm = new()
        {
            IndexGrenades = _ => { },
            AreGrenadesIndexed = _ => false
        };
        vm.SetCachedRecord(Parsed());

        await Assert.That(vm.HasIndexGrenadesAction).IsTrue();
    }

    // The mid-session residual: the chip rendered while the pack was on (nothing pushes a refresh on the
    // gate flip), then the pack goes off before the stale chip is pressed. The command's
    // own guard must still refuse it, and must not mark the demo requested, or the chip would stay hidden
    // once the pack returns.
    [Test]
    public async Task WithThePackOff_APress_DoesNothing_AndDoesNotMarkTheDemoRequested()
    {
        bool packOn = true;
        List<string> requested = [];
        MatchOverviewTabViewModel vm = new()
        {
            IndexGrenades = requested.Add,
            AreGrenadesIndexed = _ => false,
            PackEnabled = () => packOn
        };
        vm.SetCachedRecord(Parsed());
        await Assert.That(vm.HasIndexGrenadesAction).IsTrue();

        packOn = false;
        vm.RequestGrenadeIndexCommand.Execute(null);
        await Assert.That(requested).IsEmpty().Because("IndexGrenades must not be called while the pack is off");

        packOn = true;
        await Assert.That(vm.HasIndexGrenadesAction).IsTrue()
            .Because("the refused press must not have marked the demo requested");
    }
}
