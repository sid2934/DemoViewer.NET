#region

using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.Playback2D.Timeline;
using DemoViewer.NET.Modules.RoundTagger.Palette;
using DemoViewer.NET.Modules.RoundTagger.Timeline;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Label Mode (plan §3): the palette's second pass adds labels to tags that already exist, the one under
///     the playhead or the one picked on the Tag Track, and never makes one. The Done criterion is pinned
///     directly: a first-pass tag set is enriched with the instance count unchanged, the labels added, and
///     every label taken back by undo.
/// </summary>
[NotInParallel]
public class TagLabelModeTests
{
    private const string DemoPath = "/d/match.dem";

    private static readonly List<CachedRound> _rounds =
    [
        new() { Number = 1, StartTickFrameClock = 0 },
        new() { Number = 2, StartTickFrameClock = 10_000 },
        new() { Number = 3, StartTickFrameClock = 20_000 },
        new() { Number = 4, StartTickFrameClock = 30_000 }
    ];

    private static readonly string[] OutcomeValues = ["won", "lost"];
    private static readonly string[] SiteValues = ["A", "B"];
    private static readonly string[] WonAtA = ["outcome=won", "site=A"];
    private static readonly string[] Unlabelled = [];
    private static readonly string[] Won = ["outcome=won"];
    private static readonly string[] Lost = ["outcome=lost"];
    private static readonly string[] SiteB = ["site=B"];

    private static async Task<TagSession> Attached()
    {
        TagSession session = new(null, _ => _rounds, () => false, () => Created)
        {
            AutoSaveDelay = TimeSpan.FromHours(1)
        };
        await session.AttachAsync(Demo, Clock, DemoPath);
        return session;
    }

    private static TagPaletteViewModel Palette(TagSession session, Func<int> playhead)
    {
        TagPaletteViewModel palette = new(session, null, playhead);
        palette.Focus();
        return palette;
    }

    private static bool Hit(TagPaletteViewModel palette, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        palette.TryHandleKey(key, modifiers);

    private static string[] LabelsOf(TagInstance instance) =>
        [.. instance.Labels.Select(l => $"{l.Group}={l.Value}")];

    /// <summary>
    ///     The Done criterion: a first pass of bare codes, then a second pass in Label Mode over the same
    ///     tags. Nothing is duplicated, each tag gains exactly what was pressed, and undo takes it back one
    ///     label at a time.
    /// </summary>
    [Test]
    public async Task AFirstPassTagSet_IsEnriched_WithoutDuplicatingAnything_AndUndoWorks()
    {
        using TagSession session = await Attached();
        int playhead = 12_000;
        using TagPaletteViewModel palette = Palette(session, () => playhead);

        // First pass: codes only. Esc writes the execute without waiting on its panels.
        Hit(palette, Key.D1);
        Hit(palette, Key.Escape);
        playhead = 25_000;
        Hit(palette, Key.D3);
        List<TagInstance> tags = session.Document!.Instances;
        await Assert.That(tags.Count).IsEqualTo(2);
        await Assert.That(tags.All(t => t.Labels.Count == 0)).IsTrue();
        Guid[] firstPassIds = [.. tags.Select(t => t.Id)];
        int depthAfterFirstPass = session.UndoDepth;

        // Second pass: Ctrl+L, back to the execute. Its code leads to outcome, so that panel shows first.
        await Assert.That(Hit(palette, Key.L, KeyModifiers.Control)).IsTrue();
        playhead = 12_500;
        palette.RefreshLabelTarget();
        using (Assert.Multiple())
        {
            await Assert.That(palette.IsLabelMode).IsTrue();
            await Assert.That(palette.PanelTitle).IsEqualTo("outcome");
            await Assert.That(palette.Buttons.Select(b => b.Caption)).IsEquivalentTo(OutcomeValues);
            await Assert.That(palette.LabelTargetText).StartsWith("A execute");
            await Assert.That(Hit(palette, Key.D1)).IsFalse().Because("the codes are not live in Label Mode");
        }

        Hit(palette, Key.W);
        await Assert.That(palette.PanelTitle).IsEqualTo("site").Because("the panel's then still leads on");
        Hit(palette, Key.A);
        await Assert.That(palette.PanelTitle).IsEqualTo("outcome").Because("the chain ended, so it starts again");

        // The Default tag: its code leads nowhere, so the first labels panel.
        playhead = 26_000;
        palette.RefreshLabelTarget();
        await Assert.That(palette.LabelTargetText).StartsWith("Default");
        Hit(palette, Key.L);

        tags = session.Document!.Instances;
        using (Assert.Multiple())
        {
            await Assert.That(tags.Count).IsEqualTo(2).Because("Label Mode never makes a tag");
            await Assert.That(tags.Select(t => t.Id)).IsEquivalentTo(firstPassIds);
            await Assert.That(LabelsOf(tags[0])).IsEquivalentTo(WonAtA);
            await Assert.That(tags[1].Labels.Single().Value).IsEqualTo("lost");
            await Assert.That(session.UndoDepth).IsEqualTo(depthAfterFirstPass + 3).Because("one undo entry per label");
        }

        session.Undo();
        await Assert.That(session.Document!.Instances[1].Labels).IsEmpty();
        session.Undo();
        session.Undo();
        tags = session.Document!.Instances;
        using (Assert.Multiple())
        {
            await Assert.That(tags.Count).IsEqualTo(2).Because("undoing a label never takes the tag with it");
            await Assert.That(LabelsOf(tags[0])).IsEquivalentTo(Unlabelled);
            await Assert.That(session.UndoDepth).IsEqualTo(depthAfterFirstPass);
        }

        session.Redo();
        await Assert.That(LabelsOf(session.Document!.Instances[0])).IsEquivalentTo(Won);
    }

    [Test]
    public async Task ALabelTheTagAlreadyHas_WritesNothing_AndNoTagUnderThePlayheadTakesNoPress()
    {
        using TagSession session = await Attached();
        session.Apply(new TagDelta.Add(Instance("A execute", 11_000, 13_000, ("outcome", "won"))));
        int playhead = 12_000;
        using TagPaletteViewModel palette = Palette(session, () => playhead);
        palette.SetLabelMode(true);
        int depth = session.UndoDepth;

        await Assert.That(Hit(palette, Key.W)).IsTrue();
        await Assert.That(session.UndoDepth).IsEqualTo(depth).Because("an unchanged tag is not an undo entry");
        await Assert.That(palette.PanelTitle).IsEqualTo("site");

        playhead = 15_000;
        palette.RefreshLabelTarget();
        using (Assert.Multiple())
        {
            await Assert.That(palette.LabelTargetText).IsEqualTo("no tag under the playhead");
            await Assert.That(Hit(palette, Key.L)).IsFalse().Because("the outcome panel shows, with no tag to put it on");
            await Assert.That(session.Document!.Instances.Single().Labels.Count).IsEqualTo(1);
            await Assert.That(session.UndoDepth).IsEqualTo(depth);
        }
    }

    [Test]
    public async Task OverlappingTags_ThePlayheadLabelsTheLatestStart_AndATrackPickWinsUntilEsc()
    {
        using TagSession session = await Attached();
        TagInstance execute = Instance("A execute", 11_000, 14_000);
        TagInstance retake = Instance("Retake", 12_000, 13_000);
        session.Apply(new TagDelta.Add(execute));
        session.Apply(new TagDelta.Add(retake));
        using TagPaletteViewModel palette = Palette(session, () => 12_500);

        await Assert.That(TagPaletteViewModel.InstanceAt(session.Document!, 12_500)!.Id).IsEqualTo(retake.Id);
        await Assert.That(TagPaletteViewModel.InstanceAt(session.Document!, 11_500)!.Id).IsEqualTo(execute.Id);
        await Assert.That(TagPaletteViewModel.InstanceAt(session.Document!, 20_000)).IsNull();

        await Assert.That(palette.SelectForLabels([execute.Id, retake.Id])).IsFalse()
            .Because("outside Label Mode a band click only seeks");
        palette.SetLabelMode(true);
        await Assert.That(palette.PanelTitle).IsEqualTo("site").Because("Retake leads to site");

        // A pick walks the run: first click the execute, the next one the retake, then round again.
        palette.SelectForLabels([execute.Id, retake.Id]);
        await Assert.That(palette.PanelTitle).IsEqualTo("outcome");
        Hit(palette, Key.L);
        palette.SelectForLabels([execute.Id, retake.Id]);
        await Assert.That(palette.LabelTargetText).StartsWith("Retake");
        palette.SelectForLabels([execute.Id, retake.Id]);
        await Assert.That(palette.LabelTargetText).StartsWith("A execute");

        // Esc drops the pick, back to the playhead's tag; the next Esc goes back to tagging.
        await Assert.That(Hit(palette, Key.Escape)).IsTrue();
        await Assert.That(palette.LabelTargetText).StartsWith("Retake");
        Hit(palette, Key.B);
        await Assert.That(Hit(palette, Key.Escape)).IsTrue();

        using (Assert.Multiple())
        {
            await Assert.That(palette.IsLabelMode).IsFalse();
            await Assert.That(palette.IsFocused).IsTrue();
            await Assert.That(palette.PanelTitle).IsEqualTo("codes");
            await Assert.That(session.Document!.Instances.Count).IsEqualTo(2);
            await Assert.That(session.Document.Instances[0].Id).IsEqualTo(execute.Id);
            await Assert.That(LabelsOf(session.Document.Instances[0])).IsEquivalentTo(Lost);
            await Assert.That(LabelsOf(session.Document.Instances[1])).IsEquivalentTo(SiteB);
        }
    }

    [Test]
    public async Task NextGroup_WalksEveryLabelsPanel_AndEnteringWritesAPendingTagFirst()
    {
        using TagSession session = await Attached();
        using TagPaletteViewModel palette = Palette(session, () => 12_000);

        Hit(palette, Key.D1); // pending, waiting on outcome
        await Assert.That(Hit(palette, Key.L, KeyModifiers.Control)).IsTrue();
        await Assert.That(palette.HasPending).IsFalse();
        await Assert.That(session.Document!.Instances.Count).IsEqualTo(1).Because("a press is never lost");

        await Assert.That(palette.PanelTitle).IsEqualTo("outcome");
        await Assert.That(Hit(palette, Key.G, KeyModifiers.Control)).IsTrue();
        await Assert.That(palette.Buttons.Select(b => b.Caption)).IsEquivalentTo(SiteValues);
        Hit(palette, Key.G, KeyModifiers.Control);
        await Assert.That(palette.PanelTitle).IsEqualTo("outcome").Because("the walk wraps");
        await Assert.That(palette.HintText).Contains("Ctrl+L back to tagging");

        await Assert.That(Hit(palette, Key.L, KeyModifiers.Control)).IsTrue();
        await Assert.That(palette.IsLabelMode).IsFalse();
        await Assert.That(palette.NextLabelGroup()).IsFalse();
        await Assert.That(palette.HintText).Contains("Ctrl+L label mode");
    }

    [Test]
    public async Task TheLabelModeKeys_ArePaletteScoped_AndRebindable()
    {
        Playback2DKeymapProfile profile = Playback2DKeymapProfile.Default;
        await Assert.That(profile.TryResolveInScope(Playback2DBindingScope.WhenPaletteFocused, Key.L,
            KeyModifiers.Control, out Playback2DAction mode)).IsTrue();
        await Assert.That(mode).IsEqualTo(Playback2DAction.TagLabelMode);
        await Assert.That(profile.TryResolve(Key.L, KeyModifiers.Control, false, out _)).IsFalse()
            .Because("unfocused, the palette's chords are nobody's");

        using TagSession session = await Attached();
        using TagPaletteViewModel palette = Palette(session, () => 12_000);
        palette.ApplyKeymap(Playback2DKeymapProfile.FromOverrides(["TagLabelMode=Ctrl+Shift+L"], out IReadOnlyList<string> rejected));
        await Assert.That(rejected).IsEmpty();
        await Assert.That(Hit(palette, Key.L, KeyModifiers.Control)).IsFalse();
        await Assert.That(Hit(palette, Key.L, KeyModifiers.Control | KeyModifiers.Shift)).IsTrue();
        await Assert.That(palette.IsLabelMode).IsTrue();
    }

    [Test]
    public async Task InstancesInRun_IsExactlyTheBandsTags_InTheTracksOrder()
    {
        using TagSession session = await Attached();
        TagInstance late = Instance("Retake", 300, 500);
        TagInstance early = Instance("A execute", 100, 400);
        TagInstance apart = Instance("Default", 900, 1_000);
        session.Apply(new TagDelta.Add(late));
        session.Apply(new TagDelta.Add(early));
        session.Apply(new TagDelta.Add(apart));
        using TagTrack track = new(session, static action => action());
        FakeTimelineData data = new(1_000);

        using (Assert.Multiple())
        {
            await Assert.That(track.InstancesInRun(data, 50)).IsEquivalentTo(new[] { early.Id, late.Id });
            await Assert.That(track.InstancesInRun(data, 50)[0]).IsEqualTo(early.Id);
            await Assert.That(track.InstancesInRun(data, 450)).IsEquivalentTo(new[] { apart.Id });
            await Assert.That(track.InstancesInRun(data, 150)).IsEmpty().Because("no band starts inside a run");
        }
    }

    /// <summary>Through the tab: the palette's keys and a click on the tag lane pick and label, and the lane still seeks.</summary>
    [Test]
    public async Task InTheTab_ATagBandPicksTheTag_AndTheKeysLabelIt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            vm.Timeline.PixelWidth = 1000;
            await vm.Tags.AttachAsync(Demo, Clock, DemoPath);
            TagInstance execute = Instance("A execute", 800, 1_200);
            TagInstance retake = Instance("Retake", 1_000, 1_400);
            vm.Tags.Apply(new TagDelta.Add(execute));
            vm.Tags.Apply(new TagDelta.Add(retake));
            Playback2DTimelineHarness.Pump();
            ctx.CurrentTick = 1_100;

            bool Press(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
                vm.TryHandleTagPaletteKey(key, modifiers)
                || vm.Keymap.TryResolve(key, modifiers, false, out Playback2DAction action) && vm.ExecuteAction(action);

            await Assert.That(Press(Key.C)).IsTrue();
            await Assert.That(Press(Key.L, KeyModifiers.Control)).IsTrue();
            await Assert.That(vm.TagPalette.LabelTargetText).StartsWith("Retake");

            TimelineBandViewModel band = vm.Timeline.LaneBands.Single();
            vm.Timeline.PressBand(band);
            await Assert.That(vm.TagPalette.LabelTargetText).StartsWith("A execute");
            await Assert.That(ctx.SeekFrames).Contains(band.StartFrameIndex).Because("the band still seeks");
            Press(Key.W);

            vm.Timeline.PressBand(band);
            Press(Key.B);

            List<TagInstance> tags = vm.Tags.Document!.Instances;
            using (Assert.Multiple())
            {
                await Assert.That(tags.Count).IsEqualTo(2);
                await Assert.That(LabelsOf(tags[0])).IsEquivalentTo(Won);
                await Assert.That(LabelsOf(tags[1])).IsEquivalentTo(SiteB);
            }

            await Assert.That(Press(Key.Z, KeyModifiers.Control)).IsTrue();
            await Assert.That(vm.Tags.Document!.Instances[1].Labels).IsEmpty();
            await Assert.That(vm.Tags.Document!.Instances.Count).IsEqualTo(2);

            vm.OnDeactivated();
            vm.Dispose();
        });
    }
}
