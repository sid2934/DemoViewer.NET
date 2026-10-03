#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RoundTagger.Review;

#endregion

namespace DemoViewer.NET.AppTests.Extensions.StratBook;

/// <summary>
///     The 2D tab with the Strat Book's review panels attached the way the app attaches them: the pack's
///     <see cref="ReviewPanelsPlaybackContribution" /> through a <see cref="PlaybackContributionHost" />,
///     on the timeline harness's three-player, three-round fake. The tests that used to reach the palette,
///     the queue and the review panel as tab properties reach them through the contribution.
/// </summary>
internal static class ReviewPanelsHarness
{
    /// <summary>A tab with the review panels attached and activated. A null gate reads the pack as on.</summary>
    public static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx, ReviewPanelsPlaybackContribution Review) Tab(
        IFeatureGate? gate = null, int totalFrames = 1000)
    {
        ReviewPanelsPlaybackContribution review = new();
        PlaybackContributionHost host = new([(new StratBookPack(), [review])], gate);
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab(totalFrames, host);
        return (vm, ctx, review);
    }

    /// <summary>The view's key funnel, key for key: the contributions first, then the tab's resolved keymap.</summary>
    public static bool Press(Playback2DTabViewModel vm, Avalonia.Input.Key key,
        Avalonia.Input.KeyModifiers modifiers = Avalonia.Input.KeyModifiers.None) =>
        vm.Surface.TryHandleKey(key, modifiers)
        || vm.Keymap.TryResolve(key, modifiers, false, out Playback2DAction action) && vm.ExecuteAction(action);
}
