#region

using System.Numerics;
using System.Runtime.CompilerServices;
using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Modules.StratBook;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     Create Strat From Round as the pack's playback contribution (item 16): with the pack on, a round band
///     offers the entry and only a round band, the entry opens the review pane on that round's request, and
///     Create writes the strat and hands the id to the pack's navigation; with the pack off, the band offers
///     nothing and no pane exists. The capture itself is covered by <see cref="CreateStratFromRoundTests" />.
/// </summary>
[NotInParallel]
public class CreateStratPlaybackContributionTests
{
    private static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx, CreateStratPlaybackContribution Contribution)
        Attached(IFeatureGate? gate = null)
    {
        Playback2DFakeContext ctx = new();
        CreateStratPlaybackContribution contribution = new();
        PlaybackContributionHost host = new([(new StratBookPack(), [contribution])], gate);
        Playback2DTabViewModel vm = new() { Contributions = host };
        vm.OnActivated(ctx);
        return (vm, ctx, contribution);
    }

    // The entry only checks the parse reference is there; it never reads the demo, so an uninitialized
    // instance (no constructor run) stands in for a real one.
    private static StratCaptureHost HostWithADemo(StratStore? store = null, Action<Guid>? open = null) =>
        new(() => (ParsedDemo)RuntimeHelpers.GetUninitializedObject(typeof(ParsedDemo)), store ?? new StratStore(null), null, open);

    [Test]
    public async Task TheRoundBand_OffersTheEntry_OnlyForARound_AndOnlyWithACaptureThatHasADemo()
    {
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx, _) = Attached();
        TimelineBandViewModel round = CreateStratFromRoundTests.Band("round", "7");

        await Assert.That(vm.Timeline.MenuFor(round)).IsEmpty().Because("nothing registers IStratCapture");

        ctx.SetService<IStratCapture>(new StratCaptureHost(() => null, new StratStore(null), null));
        await Assert.That(vm.Timeline.MenuFor(round)).IsEmpty().Because("a capture without a demo offers nothing");

        ctx.SetService<IStratCapture>(HostWithADemo());
        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.MenuFor(round).Select(m => m.Header))
                .IsEquivalentTo([CreateStratPlaybackContribution.Label]);
            await Assert.That(vm.Timeline.MenuFor(CreateStratFromRoundTests.Band("round", "wu"))).IsEmpty();
            await Assert.That(vm.Timeline.MenuFor(CreateStratFromRoundTests.Band("tags", "3"))).IsEmpty();
            await Assert.That(vm.Surface.Panes.Count).IsEqualTo(1);
        }

        ctx.HasDemo = false;
        await Assert.That(vm.Timeline.MenuFor(round)).IsEmpty();

        vm.Dispose();
    }

    [Test]
    public async Task WithThePackOff_NoEntryAndNoPane_AndTheToggleBringsThemBack()
    {
        FakeGate gate = new() { On = false };
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx, _) = Attached(gate);
        ctx.SetService<IStratCapture>(HostWithADemo());
        TimelineBandViewModel round = CreateStratFromRoundTests.Band("round", "7");

        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.MenuFor(round)).IsEmpty();
            await Assert.That(vm.Surface.Panes).IsEmpty();
        }

        gate.On = true;
        gate.Raise();
        using (Assert.Multiple())
        {
            await Assert.That(vm.Timeline.MenuFor(round).Count).IsEqualTo(1);
            await Assert.That(vm.Surface.Panes.Count).IsEqualTo(1);
        }

        vm.Dispose();
    }

    [Test]
    public async Task TheEntry_OpensTheReviewOnTheRoundsRequest_AndCreateWritesTheStratAndNavigates()
    {
        (Playback2DTabViewModel vm, _, CreateStratPlaybackContribution contribution) = Attached();
        StratStore store = new(null);
        Guid? opened = null;
        StratCaptureHost host = HostWithADemo(store, id => opened = id);
        RoundCapture capture = CreateStratFromRoundTests.Round(new CaptureMoment(CreateStratFromRoundTests.At(12),
            CaptureTrigger.Utility, CreateStratFromRoundTests.Everyone(), "smoke", 1, 2, new Vector3(1, 2, 3)));

        contribution.OpenWith(CreateStratFromRoundTests.Request(["100", "101", "102"]), (_, _) => capture, host);

        CreateStratDialogViewModel review = (CreateStratDialogViewModel)vm.Surface.SidePane!;
        await review.Walking;
        using (Assert.Multiple())
        {
            await Assert.That(review.Title).IsEqualTo("Create strat from round 7");
            await Assert.That(review.SelectedSide).IsEqualTo(StratVocabulary.SideT);
            await Assert.That(review.CanCreate).IsTrue();
        }

        review.CreateCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(opened).IsNotNull().Because("Create hands the id to the pack's navigation");
            await Assert.That(store.TryLoad(opened!.Value)?.Origin?.Round).IsEqualTo(7);
            await Assert.That(vm.Surface.SidePane).IsNull().Because("the review closes once the strat exists");
        }

        vm.Dispose();
    }

    [Test]
    public async Task TheReviewsOwnClose_ClosesThePane_AndAnotherRoundReplacesAnOpenReview()
    {
        (Playback2DTabViewModel vm, _, CreateStratPlaybackContribution contribution) = Attached();
        StratCaptureHost host = HostWithADemo();
        RoundCapture capture = CreateStratFromRoundTests.Round();

        contribution.OpenWith(CreateStratFromRoundTests.Request([]), (_, _) => capture, host);
        CreateStratDialogViewModel first = (CreateStratDialogViewModel)vm.Surface.SidePane!;
        contribution.OpenWith(CreateStratFromRoundTests.Request([]) with { Round = 9 }, (_, _) => capture, host);
        CreateStratDialogViewModel second = (CreateStratDialogViewModel)vm.Surface.SidePane!;
        using (Assert.Multiple())
        {
            await Assert.That(second).IsNotSameReferenceAs(first);
            await Assert.That(second.Title).IsEqualTo("Create strat from round 9");
        }

        second.CloseCommand.Execute(null);
        await Assert.That(vm.Surface.SidePane).IsNull();

        vm.Dispose();
    }

    private sealed class FakeGate : IFeatureGate
    {
        public bool On { get; set; } = true;
        public UserCategory Category => UserCategory.PowerUser;
        public int HiddenCount => 0;
        public bool IsEnabled(string featureId) => On;
        public event EventHandler? Changed;
        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
