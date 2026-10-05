#region

using System.Collections.Concurrent;
using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.MatchOverview;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Match Overview's extension actions: offered while the action says so for the page's demo, handed
///     that demo, stepping aside once pressed, and hidden while the owning feature is off.
/// </summary>
public class MatchOverviewDemoActionTests
{
    private const string Demo = "/demos/grenades.dem";
    private const string Feature = "pack.fake";

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

    private static (MatchOverviewTabViewModel Vm, DemoActionRow Row) Page(DemoAction action, Func<bool>? featureOn = null)
    {
        MatchOverviewTabViewModel vm = new();
        vm.AttachDemoActions([new GatedDemoAction(action, Feature)], _ => featureOn?.Invoke() ?? true);
        vm.SetCachedRecord(Parsed());
        return (vm, vm.DemoActions.Single());
    }

    [Test]
    public async Task AnAvailableAction_IsOffered_AndHandedThePagesDemo_ThenStepsAside()
    {
        List<string> requested = [];
        (_, DemoActionRow row) = Page(new DemoAction("fake.index", "Index", "tip", _ => true, requested.Add));

        await Assert.That(row.IsVisible).IsTrue();

        row.RunCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(requested).IsEquivalentTo(new[] { Demo });
            await Assert.That(row.IsVisible).IsFalse().Because("pressed once, it steps aside");
        }
    }

    [Test]
    public async Task AnUnavailableAction_IsHidden_UntilItSaysOtherwise()
    {
        bool available = false;
        DemoAction action = new("fake.index", "Index", "tip", _ => available, _ => { });
        (_, DemoActionRow row) = Page(action);

        await Assert.That(row.IsVisible).IsFalse();

        available = true;
        action.NotifyChanged();
        await Assert.That(row.IsVisible).IsTrue();
    }

    [Test]
    public async Task ANotifyFromAnotherThread_RefreshesTheRow_OnlyThroughTheMarshal()
    {
        bool available = false;
        DemoAction action = new("fake.index", "Index", "tip", _ => available, _ => { });
        ConcurrentQueue<Action> posted = new();
        MatchOverviewTabViewModel vm = new();
        vm.AttachDemoActions([new GatedDemoAction(action, Feature) { ToUiThread = posted.Enqueue }], _ => true);
        vm.SetCachedRecord(Parsed());
        DemoActionRow row = vm.DemoActions.Single();
        await Assert.That(row.IsVisible).IsFalse();

        available = true;
        await Task.Run(action.NotifyChanged);
        await Assert.That(row.IsVisible).IsFalse().Because("the raising thread never writes the row");

        while (posted.TryDequeue(out Action? run))
        {
            run();
        }

        await Assert.That(row.IsVisible).IsTrue();
    }

    [Test]
    public async Task WithTheFeatureOff_ThePressDoesNothing_AndDoesNotMarkTheDemoRequested()
    {
        bool on = true;
        List<string> requested = [];
        (MatchOverviewTabViewModel vm, DemoActionRow row) =
            Page(new DemoAction("fake.index", "Index", "tip", _ => true, requested.Add), () => on);
        await Assert.That(row.IsVisible).IsTrue();

        on = false;
        row.RunCommand.Execute(null);
        await Assert.That(requested).IsEmpty().Because("the action must not run while its feature is off");

        on = true;
        vm.RefreshDemoActions();
        await Assert.That(row.IsVisible).IsTrue().Because("the refused press must not have marked the demo requested");
    }

    [Test]
    public async Task APageWithNoExtensions_OffersNothing()
    {
        MatchOverviewTabViewModel vm = new();
        vm.SetCachedRecord(Parsed());

        await Assert.That(vm.DemoActions).IsEmpty();
    }

    // Collected through the pack's contributions: availability that throws reads as unavailable, a run that
    // throws is contained, both count against the extension, and NotifyChanged on the extension's own action
    // still reaches the page.
    [Test]
    [NotInParallel]
    public async Task AnActionThatThrows_IsHiddenOrContained_AndCountedAgainstItsExtension()
    {
        FaultRig rig = new();
        bool throwOnAsk = true;
        DemoAction action = new("fake.index", "Index", "tip",
            _ => throwOnAsk ? throw new InvalidOperationException("ask") : true,
            _ => throw new InvalidOperationException("run"));
        PackContributions contributions = new(new StubExtension(Feature), () => null!, null, rig.Guard);
        contributions.DemoAction(action);

        MatchOverviewTabViewModel vm = new();
        vm.AttachDemoActions(contributions.DemoActions, _ => true);
        vm.SetCachedRecord(Parsed());
        DemoActionRow row = vm.DemoActions.Single();
        await Assert.That(row.IsVisible).IsFalse().Because("a throwing availability check reads as unavailable");

        throwOnAsk = false;
        action.NotifyChanged();
        await Assert.That(row.IsVisible).IsTrue().Because("the extension's own NotifyChanged still reaches the page");

        int beforeRun = rig.Faults.StateOf(Feature).Count;
        row.RunCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(beforeRun).IsGreaterThanOrEqualTo(1);
            await Assert.That(rig.Faults.StateOf(Feature).Count).IsEqualTo(beforeRun + 1);
            await Assert.That(rig.Faults.StateOf(Feature).LastSite).IsEqualTo("demo action");
        }
    }
}
