#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RoundTagger.Palette;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Tags;
using DemoViewer.NET.Views.Playback2D;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Tag Palette (plan §3, tag-store.md §3.4): lead and lag in the document's ticks, the round clamp,
///     the panel flow revealing only the panel that follows, one undo entry per gesture, sticky session
///     labels, the note, and the Done criterion: a demo tagged without touching the mouse, pinned twice,
///     through the tab VM's key funnel and through real key events on the view.
/// </summary>
[NotInParallel]
public class TagPaletteTests
{
    private const string DemoPath = "/d/match.dem";

    private static readonly List<CachedRound> _rounds =
    [
        new() { Number = 1, StartTickFrameClock = 0 },
        new() { Number = 2, StartTickFrameClock = 10_000 },
        new() { Number = 3, StartTickFrameClock = 20_000 }
    ];

    private static readonly string[] OutcomeValues = ["won", "lost"];
    private static readonly string[] SiteValues = ["A", "B"];
    private static readonly string[] WonAtA = ["outcome=won", "site=A"];
    private static readonly string[] ExecuteThenDefault = ["A execute", "Default"];
    private static readonly string[] ThreeCodes = ["A execute", "Retake", "Default"];
    private static readonly string[] WonA = ["won", "A"];
    private static readonly string[] LostB = ["lost", "B"];

    private const string StickyPalette = """
        { "schemaVersion": 1, "id": "scrim", "name": "Scrim",
          "panels": [
            { "id": "root", "buttons": [
                { "code": "Push", "hotkey": "1", "leadSeconds": 1, "lagSeconds": 1, "then": "opponent" },
                { "code": "Hold", "hotkey": "2", "leadSeconds": 0, "lagSeconds": 1 } ] },
            { "id": "opponent", "kind": "labels", "group": "opponent", "then": "outcome", "buttons": [
                { "value": "Vitality", "hotkey": "V" }, { "value": "NaVi", "hotkey": "N" } ] },
            { "id": "outcome", "kind": "labels", "group": "outcome", "buttons": [
                { "value": "won", "hotkey": "W" }, { "value": "lost", "hotkey": "L" } ] } ],
          "stickyGroups": ["opponent"] }
        """;

    private static async Task<TagSession> Attached()
    {
        TagSession session = new(null, _ => _rounds, () => false, () => Created)
        {
            AutoSaveDelay = TimeSpan.FromHours(1)
        };
        await session.AttachAsync(Demo, Clock, DemoPath);
        return session;
    }

    private static TagPaletteViewModel Palette(TagSession session, Func<int> playhead, TagPaletteStore? store = null)
    {
        TagPaletteViewModel palette = new(session, store, playhead);
        palette.Focus();
        return palette;
    }

    private static bool Hit(TagPaletteViewModel palette, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        palette.TryHandleKey(key, modifiers);

    private static TagPaletteStore StoreWith(string json, out string dir)
    {
        dir = TempRoot();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "scrim.tagpalette.json"), json);
        TagPaletteStore store = new(dir);
        store.Reload();
        return store;
    }

    [Test]
    public async Task ACodePress_SpansLeadAndLag_InTheDocumentsTicks()
    {
        // 64 tick: A execute is 5 s before to 10 s after, 320 and 640 ticks.
        (int from, int to) = TagPaletteViewModel.SpanFor(15_000, 5, 10, 64, _rounds, true);
        await Assert.That(from).IsEqualTo(14_680);
        await Assert.That(to).IsEqualTo(15_640);
    }

    [Test]
    public async Task TheSpan_IsClampedToThePlayheadsRound_WhenThePaletteSaysSo()
    {
        // Early in round 2: the lead would reach back into round 1.
        (int earlyFrom, _) = TagPaletteViewModel.SpanFor(10_100, 5, 10, 64, _rounds, true);
        // Late in round 2: the lag would run into round 3.
        (_, int lateTo) = TagPaletteViewModel.SpanFor(19_900, 5, 10, 64, _rounds, true);
        (int freeFrom, int freeTo) = TagPaletteViewModel.SpanFor(19_900, 5, 10, 64, _rounds, false);
        // Warmup: before the first round there is no round to clamp to, only the parse's start.
        (int warmFrom, _) = TagPaletteViewModel.SpanFor(-500, 5, 10, 64, [new CachedRound { Number = 1, StartTickFrameClock = 0 }], true);

        using (Assert.Multiple())
        {
            await Assert.That(earlyFrom).IsEqualTo(10_000);
            await Assert.That(lateTo).IsEqualTo(19_999).Because("a round ends the tick before the next starts");
            await Assert.That(freeFrom).IsEqualTo(19_580);
            await Assert.That(freeTo).IsEqualTo(20_540);
            await Assert.That(warmFrom).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ACode_RevealsOnlyThePanelThatFollows_AndTheGestureIsOneUndoEntry()
    {
        using TagSession session = await Attached();
        using TagPaletteViewModel palette = Palette(session, () => 15_000);

        await Assert.That(Hit(palette, Key.D1)).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(palette.PanelTitle).IsEqualTo("outcome");
            await Assert.That(palette.Buttons.Select(b => b.Caption)).IsEquivalentTo(OutcomeValues)
                .Because("the code reveals only the panel its then names, not every panel");
            await Assert.That(session.Document!.Instances).IsEmpty().Because("the tag is pending until the flow ends");
            await Assert.That(Hit(palette, Key.D2)).IsFalse()
                .Because("the codes are not showing, so their hotkeys are not live");
        }

        await Assert.That(Hit(palette, Key.W)).IsTrue();
        await Assert.That(palette.Buttons.Select(b => b.Caption)).IsEquivalentTo(SiteValues);

        await Assert.That(Hit(palette, Key.A)).IsTrue();
        TagInstance tag = session.Document!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(tag.Code).IsEqualTo("A execute");
            await Assert.That(tag.FromTick).IsEqualTo(14_680);
            await Assert.That(tag.ToTick).IsEqualTo(15_640);
            await Assert.That(tag.Round).IsEqualTo(2);
            await Assert.That(tag.Source).IsEqualTo(TagSources.Human);
            await Assert.That(tag.Labels.Select(l => $"{l.Group}={l.Value}")).IsEquivalentTo(WonAtA);
            await Assert.That(session.UndoDepth).IsEqualTo(1);
            await Assert.That(session.Document.Palette).IsEqualTo(TagPaletteStore.DefaultId);
            await Assert.That(palette.PanelTitle).IsEqualTo("codes").Because("the flow ran out, so tagging starts again");
        }

        session.Undo();
        await Assert.That(session.Document!.Instances).IsEmpty().Because("one Ctrl+Z removes the code and its labels");
    }

    [Test]
    public async Task EscMidFlow_WritesTheTagWithTheLabelsSoFar_AndEscOnTheCodesLeaves()
    {
        using TagSession session = await Attached();
        using TagPaletteViewModel palette = Palette(session, () => 15_000);

        Hit(palette, Key.D1);
        Hit(palette, Key.L);
        await Assert.That(Hit(palette, Key.Escape)).IsTrue();

        await Assert.That(session.Document!.Instances.Single().Labels.Single().Value).IsEqualTo("lost");
        await Assert.That(palette.IsFocused).IsTrue();

        await Assert.That(Hit(palette, Key.Escape)).IsTrue();
        await Assert.That(palette.IsFocused).IsFalse();
        await Assert.That(Hit(palette, Key.D3)).IsFalse().Because("an unfocused palette claims nothing");
    }

    [Test]
    public async Task ANewCodeMidFlow_FinishesThePreviousTag()
    {
        using TagSession session = await Attached();
        int playhead = 15_000;
        using TagPaletteViewModel palette = Palette(session, () => playhead);

        Hit(palette, Key.D1); // A execute, waiting on outcome
        Hit(palette, Key.Escape);
        playhead = 16_000;
        Hit(palette, Key.D3); // Default: no then, written at once

        await Assert.That(session.Document!.Instances.Select(i => i.Code)).IsEquivalentTo(ExecuteThenDefault);
        await Assert.That(session.Document.Instances[1].FromTick).IsEqualTo(16_000);
        await Assert.That(session.Document.Instances[1].ToTick).IsEqualTo(16_000 + 20 * 64);
    }

    [Test]
    public async Task StickyLabels_AreAskedOnce_ReappliedToEveryNewTag_UntilCleared()
    {
        TagPaletteStore store = StoreWith(StickyPalette, out string dir);
        try
        {
            using TagSession session = await Attached();
            using TagPaletteViewModel palette = Palette(session, () => 15_000, store);
            palette.SelectPalette("scrim");

            Hit(palette, Key.D1);
            await Assert.That(palette.PanelTitle).IsEqualTo("opponent");
            Hit(palette, Key.V);
            Hit(palette, Key.W);
            await Assert.That(palette.StickyText).IsEqualTo("opponent=Vitality");

            // Second push: the opponent panel is answered from the sticky value and skipped.
            Hit(palette, Key.D1);
            await Assert.That(palette.PanelTitle).IsEqualTo("outcome");
            Hit(palette, Key.L);

            // A code with no panels still carries the sticky label.
            Hit(palette, Key.D2);

            List<TagInstance> tags = session.Document!.Instances;
            using (Assert.Multiple())
            {
                await Assert.That(tags.Count).IsEqualTo(3);
                await Assert.That(tags.All(t => t.Labels.Count(l => l is { Group: "opponent", Value: "Vitality" }) == 1)).IsTrue();
                await Assert.That(tags[1].Labels.Single(l => l.Group == "outcome").Value).IsEqualTo("lost");
            }

            await Assert.That(Hit(palette, Key.Back, KeyModifiers.Control)).IsTrue();
            await Assert.That(palette.HasSticky).IsFalse();
            Hit(palette, Key.D1);
            await Assert.That(palette.PanelTitle).IsEqualTo("opponent").Because("cleared, the group is asked again");
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Test]
    public async Task ANote_GoesOnThePendingTag_OrAsItsOwnEditOfTheLastOne()
    {
        using TagSession session = await Attached();
        using TagPaletteViewModel palette = Palette(session, () => 15_000);

        Hit(palette, Key.D1);
        await Assert.That(Hit(palette, Key.M, KeyModifiers.Control)).IsTrue();
        await Assert.That(palette.IsEditingNote).IsTrue();
        await Assert.That(Hit(palette, Key.W)).IsFalse().Because("the note box owns the keys while it is open");
        palette.NoteDraft = "smokes late";
        await Assert.That(palette.TryHandleNoteKey(Key.Enter)).IsTrue();
        Hit(palette, Key.W);
        Hit(palette, Key.A);

        TagInstance tag = session.Document!.Instances.Single();
        await Assert.That(tag.Note).IsEqualTo("smokes late");
        await Assert.That(session.UndoDepth).IsEqualTo(1).Because("a note typed mid-gesture is part of the gesture");

        Hit(palette, Key.M, KeyModifiers.Control);
        await Assert.That(palette.NoteDraft).IsEqualTo("smokes late");
        palette.NoteDraft = "smokes late; B rotated early";
        palette.TryHandleNoteKey(Key.Enter);

        await Assert.That(session.Document!.Instances.Single().Note).IsEqualTo("smokes late; B rotated early");
        await Assert.That(session.UndoDepth).IsEqualTo(2);

        Hit(palette, Key.M, KeyModifiers.Control);
        palette.NoteDraft = "discarded";
        palette.TryHandleNoteKey(Key.Escape);
        await Assert.That(session.Document!.Instances.Single().Note).IsEqualTo("smokes late; B rotated early");
    }

    [Test]
    public async Task ThePalettesKeymapRows_LeaveTheClaimedKeysAlone_AndOnlyActInTheirScope()
    {
        (Key Key, KeyModifiers Modifiers)[] claimed =
        [
            (Key.F, KeyModifiers.Control), (Key.J, KeyModifiers.None), (Key.K, KeyModifiers.None),
            (Key.Y, KeyModifiers.None), (Key.N, KeyModifiers.None), (Key.Enter, KeyModifiers.None),
            (Key.Y, KeyModifiers.Control)
        ];
        Playback2DAction[] palette =
            [Playback2DAction.FocusTagPalette, Playback2DAction.TagPaletteBack, Playback2DAction.TagNote, Playback2DAction.TagClearSticky];

        foreach (Playback2DBinding row in Playback2DKeymap.Default.Where(b => palette.Contains(b.Action)))
        {
            await Assert.That(claimed.Contains((row.Key, row.Modifiers))).IsFalse()
                .Because($"{row.Action} must not take a key Ctrl+F or the Suggested Tags queue claimed");
        }

        Playback2DKeymapProfile profile = Playback2DKeymapProfile.Default;
        await Assert.That(profile.TryResolve(Key.Escape, KeyModifiers.None, false, out Playback2DAction outside)).IsTrue();
        await Assert.That(outside).IsEqualTo(Playback2DAction.ClearFollow).Because("unfocused, Esc is still the tab's");
        await Assert.That(profile.TryResolveInScope(Playback2DBindingScope.WhenPaletteFocused, Key.Escape,
            KeyModifiers.None, out Playback2DAction inside)).IsTrue();
        await Assert.That(inside).IsEqualTo(Playback2DAction.TagPaletteBack);
    }

    [Test]
    public async Task ThePalettesKeys_AreRebindable_ThroughTheKeymap()
    {
        using TagSession session = await Attached();
        using TagPaletteViewModel palette = Palette(session, () => 15_000);
        palette.ApplyKeymap(Playback2DKeymapProfile.FromOverrides(["TagNote=Ctrl+Shift+M"], out IReadOnlyList<string> rejected));
        await Assert.That(rejected).IsEmpty();

        Hit(palette, Key.D3);
        await Assert.That(Hit(palette, Key.M, KeyModifiers.Control)).IsFalse().Because("the vacated gesture reaches nothing");
        await Assert.That(Hit(palette, Key.M, KeyModifiers.Control | KeyModifiers.Shift)).IsTrue();
        await Assert.That(palette.IsEditingNote).IsTrue();
        await Assert.That(palette.HintText).Contains("Ctrl+Shift+M note");
    }

    [Test]
    public async Task WithNoDemoAttached_ThePaletteTakesNoFocus()
    {
        using TagSession session = new(null, _ => _rounds, () => false, () => Created);
        using TagPaletteViewModel palette = new(session, null, () => 0);

        await Assert.That(palette.Focus()).IsFalse();
        await Assert.That(palette.TryHandleKey(Key.D1, KeyModifiers.None)).IsFalse();
    }

    // The View's funnel, key for key: the palette first, then the tab's resolved keymap.
    private static bool Press(Playback2DTabViewModel vm, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        vm.TryHandleTagPaletteKey(key, modifiers)
        || vm.Keymap.TryResolve(key, modifiers, false, out Playback2DAction action) && vm.ExecuteAction(action);

    /// <summary>
    ///     The Done criterion, through the tab: focus, three tags with their panels, a note, an undo and a
    ///     redo, and out again, every step a key. Transport keys keep working while the palette has focus.
    /// </summary>
    [Test]
    public async Task ADemoIsTagged_WithoutTheMouse_ThroughTheTabsKeys()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            await vm.Tags.AttachAsync(Demo, Clock, DemoPath);

            await Assert.That(Press(vm, Key.C)).IsTrue();
            await Assert.That(vm.IsTagPaletteFocused).IsTrue();

            ctx.CurrentTick = 12_000;
            Press(vm, Key.D1); // A execute
            Press(vm, Key.W); // outcome = won
            Press(vm, Key.A); // site = A

            ctx.CurrentTick = 30_000;
            Press(vm, Key.D4); // Retake: straight to site
            Press(vm, Key.B);

            await Assert.That(Press(vm, Key.Space)).IsTrue();
            await Assert.That(ctx.PlayCount).IsEqualTo(1).Because("the palette shadows only its own keys");

            ctx.CurrentTick = 40_000;
            Press(vm, Key.D3); // Default: no panels
            Press(vm, Key.M, KeyModifiers.Control);
            vm.TagPalette.NoteDraft = "slow default";
            vm.TagPalette.TryHandleNoteKey(Key.Enter);

            List<TagInstance> tags = vm.Tags.Document!.Instances;
            using (Assert.Multiple())
            {
                await Assert.That(tags.Select(t => t.Code)).IsEquivalentTo(ThreeCodes);
                await Assert.That(tags[0].Labels.Select(l => l.Value)).IsEquivalentTo(WonA);
                await Assert.That(tags[1].Labels.Single().Value).IsEqualTo("B");
                await Assert.That(tags[1].FromTick).IsEqualTo(30_000 - 3 * 64);
                await Assert.That(tags[2].Note).IsEqualTo("slow default");
            }

            // Ctrl+Z is the tags' while the palette has focus: the note, then the Default tag.
            await Assert.That(Press(vm, Key.Z, KeyModifiers.Control)).IsTrue();
            await Assert.That(vm.Tags.Document!.Instances[2].Note).IsNull();
            Press(vm, Key.Z, KeyModifiers.Control);
            await Assert.That(vm.Tags.Document!.Instances.Count).IsEqualTo(2);
            await Assert.That(Press(vm, Key.Z, KeyModifiers.Control | KeyModifiers.Shift)).IsTrue();
            await Assert.That(vm.Tags.Document!.Instances.Count).IsEqualTo(3);

            // Esc leaves the palette; the next Esc is the tab's again.
            await Assert.That(Press(vm, Key.Escape)).IsTrue();
            await Assert.That(vm.IsTagPaletteFocused).IsFalse();
            await Assert.That(Press(vm, Key.D1)).IsFalse().Because("unfocused, 1 is nobody's key");
            await Assert.That(vm.Tags.Document!.Instances.Count).IsEqualTo(3);

            vm.OnDeactivated();
            vm.Dispose();
        });
    }

    /// <summary>The same flow as real key events on the mounted view: the tunnelling handler routes the palette first.</summary>
    [Test]
    [Category("Render")]
    public async Task ADemoIsTagged_WithoutTheMouse_ThroughTheView()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            (Playback2DTabViewModel vm, Playback2DFakeContext ctx) = Playback2DTimelineHarness.Tab();
            await vm.Tags.AttachAsync(Demo, Clock, DemoPath);
            (Window window, Playback2DView view) = Playback2DTimelineHarness.Show(vm);
            view.Focus();
            Playback2DTimelineHarness.Pump();

            ctx.CurrentTick = 12_000;
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.None); // B execute
            window.KeyPressQwerty(PhysicalKey.L, RawInputModifiers.None); // lost
            window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None); // site B
            Playback2DTimelineHarness.Pump();

            // The note: Ctrl+M opens the box, the text is typed into it, Enter keeps it and gives the
            // keyboard back to the view.
            window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.Control);
            Playback2DTimelineHarness.Pump();
            window.KeyTextInput("rotated early");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Playback2DTimelineHarness.Pump();

            TagInstance tag = vm.Tags.Document!.Instances.Single();
            Console.WriteLine($"[palette-view] {tag.Code} [{tag.FromTick}..{tag.ToTick}] "
                              + $"{string.Join(",", tag.Labels.Select(l => l.Value))} note='{tag.Note}'");
            using (Assert.Multiple())
            {
                await Assert.That(tag.Code).IsEqualTo("B execute");
                await Assert.That(tag.Labels.Select(l => l.Value)).IsEquivalentTo(LostB);
                await Assert.That(tag.Note).IsEqualTo("rotated early");
                await Assert.That(vm.IsTagPaletteFocused).IsFalse().Because("Esc on the codes leaves the palette");
            }

            window.Close();
            vm.OnDeactivated();
            vm.Dispose();
        });
    }
}
