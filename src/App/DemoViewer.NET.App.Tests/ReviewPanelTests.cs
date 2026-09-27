#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RoundTagger.Palette;
using DemoViewer.NET.Modules.RoundTagger.Review;
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
}
