#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Modules.RoundTagger.Palette;
using DemoViewer.NET.Modules.RoundTagger.Review;
using DemoViewer.NET.Modules.RoundTagger.Timeline;
using DemoViewer.NET.Modules.SuggestedTags;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Views.Playback2D;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Review mode's panel over a real tag session: the Labels list, editing a written tag's code, span, labels
///     and positions, deleting it and undoing the delete, a label over a whole round, the round clamp, and keys
///     typed into the editor never reaching the keymap.
/// </summary>
[NotInParallel]
public class ReviewPanelTests
{
    private const string DemoPath = "/d/match.dem";

    private static readonly List<CachedRound> _rounds =
    [
        new() { Number = 1, StartTickFrameClock = 0 },
        new() { Number = 2, StartTickFrameClock = 10_000 },
        new() { Number = 3, StartTickFrameClock = 20_000 }
    ];

    private static async Task<(TagSession Session, ReviewPanelViewModel Panel, List<int> Seeks, Func<int> SetPlayhead)> Panel(int playhead = 12_000)
    {
        TagSession session = new(null, _ => _rounds, () => false, () => Created) { AutoSaveDelay = TimeSpan.FromHours(1) };
        await session.AttachAsync(Demo, Clock, DemoPath);
        int tick = playhead;
        TagPaletteViewModel palette = new(session, null, () => tick);
        SuggestionQueueViewModel queue = new(null, new ProposalTrack(), _ => { }, () => 64, static a => a());
        List<int> seeks = [];
        ReviewPanelViewModel panel = new(session, queue, palette, () => tick, () => 64, seeks.Add);
        return (session, panel, seeks, () => tick);
    }

    private static TagInstance Tag(string code, int from, int to) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        FromTick = from,
        ToTick = to,
        CreatedUtc = Created,
        ModifiedUtc = Created,
        Labels = [new TagLabel("site", "A")]
    };

    [Test]
    public async Task TheLabelsTab_ListsMachineWrittenLabels_ApartFromHandMadeOnes()
    {
        (TagSession session, ReviewPanelViewModel panel, _, _) = await Panel();
        TagInstance hand = Tag("A execute", 11_000, 11_500);
        TagInstance accepted = Tag("Retake", 12_000, 12_500);
        accepted.Source = TagSources.Suggested;
        accepted.Provenance = new System.Text.Json.Nodes.JsonObject { ["detector"] = "retake" };
        TagInstance run = Tag("Default", 1_000, 2_000);
        run.Source = TagSources.Suggested;
        run.Provenance = new System.Text.Json.Nodes.JsonObject { ["detector"] = "strat-mining" };
        session.Apply(new TagDelta.Add(hand));
        session.Apply(new TagDelta.Add(accepted));
        session.Apply(new TagDelta.Add(run));

        using (Assert.Multiple())
        {
            await Assert.That(panel.Labels.Count).IsEqualTo(3);
            await Assert.That(panel.HandLabels.Select(r => r.Id)).IsEquivalentTo([hand.Id]);
            await Assert.That(panel.MachineLabels.Select(r => r.Id)).IsEquivalentTo([run.Id, accepted.Id]);
            await Assert.That(panel.MachineLabels.Select(r => r.SourceText)).IsEquivalentTo(["strat run", "suggested: retake"]);
            await Assert.That(panel.HandHeader).IsEqualTo("Yours (1)");
            await Assert.That(panel.MachineHeader).IsEqualTo("From suggestions (2)");
        }
    }

    [Test]
    public async Task ALabel_IsListed_EditedInEveryField_Deleted_AndTheDeleteUndone()
    {
        (TagSession session, ReviewPanelViewModel panel, List<int> seeks, _) = await Panel();
        TagInstance tag = Tag("A execute", 11_000, 11_500);
        session.Apply(new TagDelta.Add(tag));

        await Assert.That(panel.LabelsHeader).IsEqualTo("Labels: 1");
        panel.SelectLabelCommand.Execute(panel.Labels.Single());
        TagEditorViewModel editor = panel.ActiveEditor!;
        using (Assert.Multiple())
        {
            await Assert.That(seeks.Single()).IsEqualTo(11_000).Because("selecting a label seeks to its start");
            await Assert.That(editor.FromText).IsEqualTo("15.63").Because("(11000 - 10000) / 64 = 15.625 seconds into round 2");
            await Assert.That(editor.CanDelete).IsTrue();
        }

        editor.SelectedCode = "Retake";
        editor.FromText = "20";
        editor.ToText = "25";
        editor.RemoveLabelCommand.Execute(editor.Labels.Single());
        editor.NewGroup = "outcome";
        editor.NewValue = "won";
        editor.AddLabelCommand.Execute(null);
        editor.AddPosition(new TagPosition { X = 1, Y = 2, Place = "Ramp" });
        editor.SaveCommand.Execute(null);

        TagInstance saved = session.Document!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(panel.HasEditor).IsFalse();
            await Assert.That(saved.Id).IsEqualTo(tag.Id).Because("an edit replaces the tag, it does not add one");
            await Assert.That(saved.Code).IsEqualTo("Retake");
            await Assert.That(saved.FromTick).IsEqualTo(10_000 + 20 * 64);
            await Assert.That(saved.ToTick).IsEqualTo(10_000 + 25 * 64);
            await Assert.That(saved.Labels.Select(l => $"{l.Group}={l.Value}")).IsEquivalentTo(["outcome=won"]);
            await Assert.That(saved.Positions.Single().Place).IsEqualTo("Ramp");
            await Assert.That(saved.Round).IsEqualTo(2);
        }

        panel.SelectLabelCommand.Execute(panel.Labels.Single());
        panel.ActiveEditor!.DeleteCommand.Execute(null);
        await Assert.That(session.Document!.Instances).IsEmpty();
        await Assert.That(panel.LabelsHeader).IsEqualTo("Labels: 0");

        await Assert.That(session.Undo()).IsTrue();
        await Assert.That(session.Document!.Instances.Single().Code).IsEqualTo("Retake").Because("a delete undoes like any edit");
        await Assert.That(session.Redo()).IsTrue();
        await Assert.That(session.Document!.Instances).IsEmpty();
    }

    [Test]
    public async Task LabelThisRound_SpansTheRound_NeedsACode_AndTheRoundClampHolds()
    {
        (TagSession session, ReviewPanelViewModel panel, _, _) = await Panel(playhead: 12_000);
        panel.LabelRoundCommand.Execute(null);
        TagEditorViewModel editor = panel.ActiveEditor!;
        using (Assert.Multiple())
        {
            await Assert.That(editor.SelectedCode).IsNull().Because("the user picks the code");
            await Assert.That(editor.FromText).IsEqualTo("0");
            await Assert.That(editor.ToText).IsEqualTo("156.23").Because("to the tick before round 3: 9999 / 64");
            await Assert.That(editor.CanDelete).IsFalse();
        }

        editor.SaveCommand.Execute(null);
        await Assert.That(editor.Problem).IsEqualTo("Pick a code.");
        await Assert.That(session.Document!.Instances).IsEmpty();

        editor.SelectedCode = "Default";
        editor.ToText = "400"; // past the round: the palette clamps to rounds by default
        editor.SaveCommand.Execute(null);
        TagInstance saved = session.Document!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(saved.FromTick).IsEqualTo(10_000);
            await Assert.That(saved.ToTick).IsEqualTo(19_999).Because("clamped to the round the start falls in");
            await Assert.That(saved.Source).IsEqualTo(TagSources.Human);
        }
    }

    [Test]
    public async Task TheEnd_BeforeTheStart_IsRefused()
    {
        (TagSession session, ReviewPanelViewModel panel, _, _) = await Panel();
        panel.LabelHereCommand.Execute(null);
        TagEditorViewModel editor = panel.ActiveEditor!;
        editor.SelectedCode = "Default";
        editor.FromText = "30";
        editor.ToText = "10";
        editor.SaveCommand.Execute(null);

        await Assert.That(editor.Problem).IsEqualTo("The end is before the start.");
        await Assert.That(session.Document!.Instances).IsEmpty();
    }

    [Test]
    public async Task KeysTypedIntoTheEditor_NeverReachTheKeymap() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            await vm.Tags.AttachAsync(Demo, Clock, DemoPath);
            vm.IsReviewMode = true;
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm, 1280, 800);
            vm.ReviewPanel.LabelHereCommand.Execute(null);
            Playback2DTimelineHarness.Pump();

            TextBox from = view.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "FromBox");
            from.Focus();
            Playback2DTimelineHarness.Pump();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            using (Assert.Multiple())
            {
                await Assert.That(ctx.PlayCount).IsEqualTo(0).Because("Space in a box is a space, not play");
                await Assert.That(ctx.NextEvents).IsEmpty().Because("E in a box is a letter, not next round");
                await Assert.That(vm.ReviewPanel.HasEditor).IsTrue();
            }

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();
            await Assert.That(vm.ReviewPanel.HasEditor).IsFalse().Because("Esc in the editor cancels it");
        });

    [Test]
    public async Task OnTheLane_TheHandlesMoveTheEditor_AClickStartsALabel_AndABandOffersEditAndDelete() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            vm.Timeline.PixelWidth = 999; // one px per frame over the harness's 1000 frames
            await vm.Tags.AttachAsync(Demo, Clock, DemoPath);
            TagInstance tag = Tag("A execute", 800, 1_200);
            vm.Tags.Apply(new TagDelta.Add(tag));
            Playback2DTimelineHarness.Pump();

            await Assert.That(vm.Timeline.IsLaneEditable).IsFalse().Because("the lane takes edits in Review mode only");
            vm.IsReviewMode = true;
            await Assert.That(vm.Timeline.IsLaneEditable).IsTrue();

            vm.ReviewPanel.EditTag(tag.Id);
            TagEditorViewModel editor = vm.ReviewPanel.ActiveEditor!;
            await Assert.That(vm.Timeline.HasEditSpan).IsTrue();
            await Assert.That(vm.Timeline.EditX).IsEqualTo(400).Because("tick 800 is frame 400 on the fake");

            int seeks = ctx.SeekFrames.Count + ctx.SeekTicks.Count;
            vm.Timeline.DragEditEdge(true, 300);
            vm.Timeline.DragEditEdge(false, 700);
            using (Assert.Multiple())
            {
                await Assert.That(editor.CurrentSpan).IsEqualTo((600, 1_400));
                await Assert.That(ctx.SeekFrames.Count + ctx.SeekTicks.Count).IsEqualTo(seeks).Because("a handle drag never seeks");
                await Assert.That(vm.Tags.Document!.Instances.Single().FromTick).IsEqualTo(800).Because("the drag moves the draft; Save writes it");
            }

            vm.Timeline.DragEditEdge(true, 900);
            await Assert.That(editor.CurrentSpan!.Value.From).IsEqualTo(1_400).Because("the start never passes the end");
            editor.SaveCommand.Execute(null);
            await Assert.That(vm.Tags.Document!.Instances.Single().FromTick).IsEqualTo(1_400);
            await Assert.That(vm.Timeline.HasEditSpan).IsFalse();
            Playback2DTimelineHarness.Pump();

            TimelineBandViewModel band = vm.Timeline.LaneBands.Single(b => b.TrackId == TagTrack.TrackId);
            List<(string Header, Action Run)> menu = vm.Timeline.LaneMenu!(band).ToList();
            await Assert.That(menu.Select(m => m.Header)).IsEquivalentTo(["Edit A execute", "Delete A execute"]);
            menu[1].Run();
            await Assert.That(vm.Tags.Document!.Instances).IsEmpty();

            vm.Timeline.RequestLaneLabel(100);
            TagEditorViewModel created = vm.ReviewPanel.ActiveEditor!;
            using (Assert.Multiple())
            {
                await Assert.That(created.CanDelete).IsFalse().Because("a click on empty lane starts a new label");
                await Assert.That(created.CurrentSpan).IsEqualTo((200, 200 + 10 * 64));
            }

            vm.IsReviewMode = false;
            using (Assert.Multiple())
            {
                await Assert.That(vm.Timeline.IsLaneEditable).IsFalse();
                await Assert.That(vm.Timeline.HasEditSpan).IsFalse();
                await Assert.That(vm.Timeline.LaneMenu!(band)).IsEmpty();
            }

            vm.OnDeactivated();
            vm.Dispose();
        });

    [Test]
    [Category("Integration")]
    public async Task ThePanel_RendersTheLabelsTab_WithTheEditorOpen() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.Push(1, 2);
            await vm.Tags.AttachAsync(Demo, Clock, DemoPath);
            vm.Tags.Apply(new TagDelta.Add(Tag("A execute", 300, 700)));
            vm.Tags.Apply(new TagDelta.Add(Tag("Retake", 800, 900)));
            vm.IsReviewMode = true;
            (Window window, Playback2DView _) = Playback2DTimelineHarness.Show(vm, 1280, 900);
            vm.ReviewPanel.ShowLabels();
            vm.ReviewPanel.SelectLabelCommand.Execute(vm.ReviewPanel.Labels[0]);
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "review-labels-editor.png"), new PngBitmapEncoderOptions());
            await Assert.That(vm.ReviewPanel.HasEditor).IsTrue();
            window.Close();
        });

    [Test]
    [Category("Integration")]
    public async Task ThePanel_RendersHandMadeAndMachineWrittenLabelsApart() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            ctx.Push(1, 2);
            await vm.Tags.AttachAsync(Demo, Clock, DemoPath);
            vm.Tags.Apply(new TagDelta.Add(Tag("A execute", 300, 700)));
            TagInstance accepted = Tag("Retake", 800, 900);
            accepted.Source = TagSources.Suggested;
            accepted.Provenance = new System.Text.Json.Nodes.JsonObject { ["detector"] = "retake" };
            TagInstance run = Tag("Default", 100, 250);
            run.Source = TagSources.Suggested;
            run.Provenance = new System.Text.Json.Nodes.JsonObject { ["detector"] = "strat-mining" };
            vm.Tags.Apply(new TagDelta.Add(accepted));
            vm.Tags.Apply(new TagDelta.Add(run));
            vm.IsReviewMode = true;
            (Window window, Playback2DView _) = Playback2DTimelineHarness.Show(vm, 1280, 900);
            vm.ReviewPanel.ShowLabels();
            Playback2DTimelineHarness.Pump();
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "review-labels-grouped.png"), new PngBitmapEncoderOptions());
            await Assert.That(vm.ReviewPanel.MachineLabels.Count).IsEqualTo(2);
            window.Close();
        });
}
