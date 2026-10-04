#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Views.Playback2D;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The timeline in a window: a right press on a round band opens the menu built from the contributors,
///     a contributed entry included, and a lane band it has nothing for opens none.
/// </summary>
[NotInParallel]
[Category("Render")]
public class PlaybackBandMenuWindowTests
{
    [Test]
    public async Task RightPressingARoundBand_ShowsTheContributedEntry() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            FakeContribution fake = new();
            PlaybackContributionHost host = new([(new FakePack(), [fake])], null);
            using IDisposable binding = host.Attach(vm.Surface, ctx);
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            TimelineControl timeline = Playback2DTimelineHarness.Timeline(view);

            Border band = timeline.GetVisualDescendants().OfType<Border>()
                .First(b => b.DataContext is TimelineBandViewModel band && Playback2DTimelineViewModel.IsRoundBand(band)
                                                                        && b.Bounds.Width > 0);
            Point at = Playback2DTimelineHarness.ToWindow(band, window, band.Bounds.Width / 2, band.Bounds.Height / 2);
            int seeks = ctx.SeekFrames.Count;

            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            Playback2DTimelineHarness.Pump();

            ContextMenu menu = timeline.LastBandMenu ?? throw new InvalidOperationException("no band menu opened");
            using (Assert.Multiple())
            {
                await Assert.That(menu.ItemsSource!.Cast<MenuItem>().Select(i => i.Header as string ?? ""))
                    .IsEquivalentTo([FakeContribution.Header]);
                await Assert.That(ctx.SeekFrames.Count).IsEqualTo(seeks).Because("a right press never seeks");
                await Assert.That(fake.Runs).IsEqualTo(0);
            }

            menu.Close();
            window.Close();
        });

    [Test]
    public async Task RightPressingTheWarmupBand_OpensNoMenu_WhenNoContributorHasAnEntryForIt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            // Freeze-ends away from frame 0, so the rounds row starts with the warmup band, which is on the
            // round track but is not a round: the contribution offers nothing, and outside Review mode the
            // tab's lane menu offers nothing either.
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.Frames["round_freeze_end"] = [200, 500, 800];
            FakeContribution fake = new();
            PlaybackContributionHost host = new([(new FakePack(), [fake])], null);
            using IDisposable binding = host.Attach(vm.Surface, ctx);
            ctx.RaiseDemoReset();
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            TimelineControl timeline = Playback2DTimelineHarness.Timeline(view);

            Border warmup = timeline.GetVisualDescendants().OfType<Border>()
                .First(b => b.DataContext is TimelineBandViewModel { TrackId: "round", Label: "wu" } && b.Bounds.Width > 0);
            Point at = Playback2DTimelineHarness.ToWindow(warmup, window, warmup.Bounds.Width / 2, warmup.Bounds.Height / 2);
            int seeks = ctx.SeekFrames.Count;

            window.MouseDown(at, MouseButton.Right);
            window.MouseUp(at, MouseButton.Right);
            Playback2DTimelineHarness.Pump();

            using (Assert.Multiple())
            {
                await Assert.That(timeline.LastBandMenu).IsNull();
                await Assert.That(ctx.SeekFrames.Count).IsEqualTo(seeks);
                await Assert.That(fake.Runs).IsEqualTo(0);
            }

            window.Close();
        });

    private sealed class FakeContribution : IPlaybackContribution
    {
        public const string Header = "Fake entry";
        private IDisposable? _menu;

        public int Runs { get; private set; }

        public void Attach(IPlaybackSurface surface, IModuleContext context) =>
            _menu = surface.AddBandMenu(band => Playback2DTimelineViewModel.IsRoundBand(band)
                ? [new MenuEntry(Header, () => Runs++)]
                : []);

        public void Detach()
        {
            _menu?.Dispose();
            _menu = null;
        }
    }

    private sealed class FakePack : IFeaturePack
    {
        public string Id => "net.demoviewer.pack.fake";
        public string FeatureId => "pack.fake";
        public ExtensionManifest Manifest => FakeManifests.For(Id);
        public IEnumerable<FeatureDescriptor> Features => [];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IPackContributions contributions, IServiceProvider sp)
        {
        }
    }
}
