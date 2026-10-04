#region

using Avalonia.Threading;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RoundTagger.Review;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;

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
    /// <summary>
    ///     The production post: the contribution's session changes, the lane's re-query and the queue's reload
    ///     land on the dispatcher. A test body on the UI thread sees them after a <see cref="Playback2DTimelineHarness.Pump" />.
    /// </summary>
    public static readonly Action<Action> DispatcherPost = static action => Dispatcher.UIThread.Post(action);

    /// <summary>
    ///     Synchronous delivery, for a test body that runs off the UI thread (a plain test, no <c>RunOnUi</c>):
    ///     nothing pumps the dispatcher there, so a posted change would never land.
    /// </summary>
    public static readonly Action<Action> SynchronousPost = static action => action();

    /// <summary>A tab with the review panels attached and activated. A null gate reads the pack as on.</summary>
    /// <param name="gate">The pack gate, or null for on.</param>
    /// <param name="totalFrames">The fake demo's length.</param>
    /// <param name="configure">Runs on the context before activation: the services the contribution resolves, the demo path.</param>
    /// <param name="post">The contribution's post; <see cref="DispatcherPost" /> when omitted.</param>
    /// <param name="identity">The contribution's demo identity resolver, or null for the real one.</param>
    public static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx, ReviewPanelsPlaybackContribution Review) Tab(
        IFeatureGate? gate = null, int totalFrames = 1000, Action<Playback2DFakeContext>? configure = null,
        Action<Action>? post = null, Func<string, string?, Task<DemoIdentity?>>? identity = null)
    {
        ReviewPanelsPlaybackContribution review = new(post ?? DispatcherPost, identity);
        PlaybackContributionHost host = new([(new StratBookPack(), [review])], gate);
        (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab(totalFrames, host, configure);
        return (vm, ctx, review);
    }

    /// <summary>The view's key funnel, key for key: the contributions first, then the tab's resolved keymap.</summary>
    public static bool Press(Playback2DTabViewModel vm, Avalonia.Input.Key key,
        Avalonia.Input.KeyModifiers modifiers = Avalonia.Input.KeyModifiers.None) =>
        vm.Surface.TryHandleKey(key, modifiers)
        || vm.Keymap.TryResolve(key, modifiers, false, out Playback2DAction action) && vm.ExecuteAction(action);
}
