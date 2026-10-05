#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Extensions.StratBook.Modules.RoundTagger.Review;
using DemoViewer.NET.Extensions.StratBook.Modules.Situations;
using DemoViewer.NET.Playback2D.Pipeline.Annotations;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     The 2D Playback tab in Review mode with the Strat Book's contributions attached as the pack attaches
///     them: the Review toggle and the "Rounds like this" toolbar item, the tag lane
///     on the timeline, the Tag Palette, the review panel on its Labels tab with an editor open, and the
///     Suggestion Queue under it, over two tags on a session-only document. The pack-off twin attaches the
///     same contributions behind an off gate, so the toolbar, the timeline and the column show the core
///     content alone.
/// </summary>
public static partial class Variants
{
    private static Playback2DView Playback2DReviewPanels() => ReviewPanelsTab(packOn: true);

    private static Playback2DView Playback2DReviewPanelsPackOff() => ReviewPanelsTab(packOn: false);

    private static Playback2DView ReviewPanelsTab(bool packOn)
    {
        PaneContext ctx = new();
        ReviewPanelsPlaybackContribution review = new();
        SituationsPlaybackContribution situations = new();
        PlaybackContributionHost host = new([(new StratBookPack(), [review, situations])], packOn ? null : new OffGate());
        Playback2DTabViewModel vm = new() { Contributions = host };
        vm.OnActivated(ctx);

        if (packOn && review is { Session: { } session, ReviewMode: { } mode })
        {
            mode.IsOn = true;
            // A session-only document (no store in this process) with a hand-made label and an accepted one.
            DemoIdentity demo = new("ab", "match730_capture_dust2.dem", 549_715_968);
            ClockIdentity clock = new(ClockIdentity.DvFrameClock, 64, 180_000, 0, 0);
            session.AttachAsync(demo, clock, ctx.DemoPath!).GetAwaiter().GetResult();
            DateTime now = DateTime.UtcNow;
            TagInstance execute = new()
            {
                Id = Guid.NewGuid(), Code = "A execute", FromTick = 23_600, ToTick = 26_000, Round = 7,
                Source = TagSources.Human, CreatedUtc = now, ModifiedUtc = now, Labels = [new TagLabel("site", "A")]
            };
            TagInstance retake = new()
            {
                Id = Guid.NewGuid(), Code = "Retake", FromTick = 31_000, ToTick = 32_400, Round = 8,
                Source = TagSources.Suggested, CreatedUtc = now, ModifiedUtc = now
            };
            session.Apply(new TagDelta.Add(execute));
            session.Apply(new TagDelta.Add(retake));
            review.Review?.EditTag(execute.Id);
        }

        return new Playback2DView { DataContext = vm };
    }

    private sealed class OffGate : IFeatureGate
    {
        public UserCategory Category => UserCategory.PowerUser;
        public int HiddenCount => 0;
        public bool IsEnabled(string featureId) => false;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
