#region

using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.UtilityBook;
using DemoViewer.NET.ViewModels.UtilityBook;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;
using TabPlacement = DemoViewer.NET.Modules.Abstractions.TabPlacement;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Strat Book editor fits its column at 1280 wide with the hub rail and the strat list open: no field
///     reaches past the editor's viewport, which has no horizontal scroll bar to reach it with.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class StratEditorRoomTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task At1280Wide_NoEditorFieldReachesPastTheViewport(bool collapsed) =>
        await HeadlessSession.RunOnUi(async () =>
        {
            StratBookLayout layout = new() { IsRailCollapsed = collapsed, IsListCollapsed = collapsed };
            using StratBookTabViewModel strats = Seeded(layout);
            StratBookHubViewModel hub = new(layout);
            hub.Sections.Reconcile([
                new WorkspaceTabDescriptor
                {
                    TabId = "stratbook.browser", Header = "Strats", Order = 0, Placement = TabPlacement.StratBook,
                    ViewModelFactory = () => strats, ViewFactory = () => new StratBookTabView()
                },
                new WorkspaceTabDescriptor
                {
                    TabId = "review.queue", Header = "Review", Order = 1, Placement = TabPlacement.StratBook, Badge = "12",
                    ViewFactory = () => new TextBlock { Text = "Review" }
                }
            ]);
            hub.OnActivated(new StillContext());

            Window window = new() { Width = 1280, Height = 800, Content = new StratBookHubView { DataContext = hub } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            ScrollViewer scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "EditorScroll");
            Control content = (Control)scroll.Content!;
            double viewport = scroll.Viewport.Width;
            List<string> overflow = [];
            foreach (Control control in content.GetVisualDescendants().OfType<Control>()
                         .Where(c => c is TextBox or ComboBox or Button or ToggleButton or TextBlock && c.IsEffectivelyVisible))
            {
                if (control.TranslatePoint(new Point(control.Bounds.Width, 0), scroll) is { } right && right.X > viewport + 0.5)
                {
                    overflow.Add($"{control.GetType().Name} '{(control as TextBlock)?.Text ?? control.Name}' ends at {right.X:F0}");
                }
            }

            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir,
                collapsed ? "strat-editor-1280-collapsed.png" : "strat-editor-1280.png"), new PngBitmapEncoderOptions());
            window.Close();

            using (Assert.Multiple())
            {
                await Assert.That(viewport).IsGreaterThan(collapsed ? 400 : 250);
                await Assert.That(scroll.Extent.Width).IsLessThanOrEqualTo(viewport + 0.5);
                await Assert.That(overflow).IsEmpty()
                    .Because($"the editor is {viewport:F0} wide: {string.Join("; ", overflow.Take(8))}");
            }
        });

    [Test]
    public async Task TheCollapsedList_KeepsAStratPicker_AndTheRailKeepsItsSections() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            StratBookLayout layout = new();
            using StratBookTabViewModel strats = Seeded(layout);
            StratBookTabView view = new() { DataContext = strats };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Control picker = view.FindControl<Control>("StratPicker")!;
            Control expand = view.FindControl<Control>("ExpandList")!;
            await Assert.That(picker.IsEffectivelyVisible).IsFalse().Because("the list is the picker while it is open");

            view.FindControl<Button>("CollapseList")!.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(layout.IsListCollapsed).IsTrue();
                await Assert.That(picker.IsEffectivelyVisible).IsTrue();
                await Assert.That(expand.IsEffectivelyVisible).IsTrue();
            }

            window.Close();
        });

    [Test]
    public async Task WithTheListCollapsed_ThePickerSwitchesStrats_AndSurvivesARefresh() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            StratBookLayout layout = new() { IsListCollapsed = true };
            using StratBookTabViewModel vm = new(new StratStore(null), null, null, false, layout: layout);
            vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
            vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
            vm.SelectedMap = "de_mirage";
            vm.NewStratCommand.Execute(null);
            Guid first = vm.Session.Document!.Id;
            vm.NewStratCommand.Execute(null);
            Guid second = vm.Session.Document!.Id;

            StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            ComboBox picker = view.FindControl<ComboBox>("StratPickerCombo")!;
            await Assert.That(((StratListRow)picker.SelectedItem!).Id).IsEqualTo(second);

            picker.SelectedItem = vm.Strats.Single(r => r.Id == first);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(vm.Session.Document!.Id).IsEqualTo(first);

            vm.Editor.Name = "renamed";
            vm.SaveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(vm.HasOpenStrat).IsTrue().Because("a list refresh must not close the open strat");
                await Assert.That(vm.Session.Document!.Id).IsEqualTo(first);
                await Assert.That(vm.SelectedStrat?.Id).IsEqualTo(first);
                await Assert.That((picker.SelectedItem as StratListRow)?.Id).IsEqualTo(first);
                await Assert.That((picker.SelectedItem as StratListRow)?.Name).IsEqualTo("renamed");
            }

            // Detected: the header picker follows the toggle, and the callouts stay one click away.
            vm.IsDetectedView = true;
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(picker.IsEffectivelyVisible).IsFalse();
                await Assert.That(view.FindControl<ComboBox>("DetectedPickerCombo")!.IsEffectivelyVisible).IsTrue();
                await Assert.That(view.FindControl<Button>("CalloutsButton")!.IsEffectivelyVisible).IsTrue();
                await Assert.That(view.FindControl<Button>("CalloutsButton")!.Flyout).IsNotNull();
            }

            window.Close();
        });

    // With the Grenade Index fixture (one smoke lineup, two techniques), so the throw row carries the lineup,
    // technique and "pick on map" controls the width test must measure.
    private static StratBookTabViewModel Seeded(StratBookLayout layout)
    {
        (GrenadeIndex index, GrenadeLineup lineup) = StratThrowOriginTests.Indexed();
        StratBookTabViewModel vm = new(new StratStore(null), null, a => Dispatcher.UIThread.Post(a), false, grenades: index,
            layout: layout,
            lineupMap: (map, asset) => new UtilityBookTabViewModel(index, loadMapAsset: _ => asset, retire: a => a(), lockedMap: map,
                ownsMapAsset: false));
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.SelectedMap = "de_mirage";
        vm.NewStratCommand.Execute(null);
        vm.Editor.Name = "A split through palace with a long name that has to wrap or trim";
        vm.Editor.Notes = "a note";

        StratEditorViewModel editor = vm.Editor;
        foreach (string verb in StratVocabulary.Verbs)
        {
            editor.AddStepCommand.Execute(null);
            editor.Steps[^1].Verb = verb;
        }

        // Every field of the widest rows holds a value: other shows them all, and a throw has a lineup.
        int other = StratVocabulary.Verbs.ToList().IndexOf("other");
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{other}/from", null, new JsonObject { ["place"] = "TSpawn" }));
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{other}/to", null, new JsonObject { ["place"] = "TopofMid" }));
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{other}/utility", null, new JsonObject
        {
            ["kind"] = "smoke", ["lineupId"] = Guid.NewGuid().ToString(), ["landing"] = new JsonObject { ["place"] = "Connector" }
        }));
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{other}/note", null, JsonValue.Create("a long note")));
        int thrown = StratVocabulary.Verbs.ToList().IndexOf("throw");
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{thrown}/utility", null,
            new JsonObject { ["kind"] = "smoke", ["lineupId"] = lineup.Id.ToString() }));
        vm.Editor.AddBranchCommand.Execute(null);

        // The editor's catalog groups the map off the UI thread and posts back.
        for (int i = 0; i < 500 && vm.Editor.ResolveLineup(lineup.Id) is null; i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    [Test]
    public async Task ThePicker_OpensOverTheTab_FromTheThrowRow_AndConfirmWrites() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            StratBookLayout layout = new();
            using StratBookTabViewModel vm = Seeded(layout);
            StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            int thrown = StratVocabulary.Verbs.ToList().IndexOf("throw");
            StratStepRow row = vm.Editor.Steps[thrown];
            await Assert.That(row.ShowTechnique).IsTrue().Because("the fixture lineup is thrown two ways");
            Button pick = view.GetVisualDescendants().OfType<Button>()
                .First(b => Equals(b.Content, "Pick on map") && ReferenceEquals(b.CommandParameter, row));
            await Assert.That(pick.IsEffectivelyVisible).IsTrue();
            pick.Command!.Execute(pick.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            LineupPickerViewModel picker = vm.LineupPicker!;
            Control overlay = view.FindControl<Control>("LineupPickerOverlay")!;
            Control body = view.FindControl<Control>("TabBody")!;
            IInputElement? focused = TopLevel.GetTopLevel(view)!.FocusManager!.GetFocusedElement();
            using (Assert.Multiple())
            {
                await Assert.That(overlay.IsEffectivelyVisible).IsTrue();
                await Assert.That(picker.SelectedPosition).IsNotNull().Because("the step's lineup opens selected");
                await Assert.That(body.IsEnabled).IsFalse().Because("Tab and Enter must not reach the editor behind the picker");
                await Assert.That(focused is Visual v && v.GetVisualAncestors().OfType<LineupPickerView>().Any()).IsTrue()
                    .Because("the picker takes the focus, so Escape works at once");
            }

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "strat-lineup-picker.png"),
                new PngBitmapEncoderOptions());

            int depth = vm.Session.UndoDepth;
            picker.SelectedPosition = picker.Positions.First(p => p != picker.SelectedPosition);
            view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmPick").Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
                await Assert.That(vm.Session.Document!.Steps[thrown].Utility!.Technique).IsNotNull();
                await Assert.That(overlay.IsEffectivelyVisible).IsFalse();
            }

            // Escape cancels at once: the focus is already in the picker.
            pick.Command!.Execute(pick.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(vm.LineupPicker).IsNotNull();
            int before = vm.Session.UndoDepth;
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(vm.LineupPicker).IsNull();
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(before);
                await Assert.That(body.IsEnabled).IsTrue();
            }

            window.Close();
        });

    private sealed class StillContext : IModuleContext
    {
        public bool HasDemo => false;
        public string? DemoPath => null;
        public int TickRate => 64;
        public int CurrentFrameIndex => 0;
        public int CurrentTick => 0;
        public bool IsPlaying => false;
        public double Speed => 1;
        public IReadOnlyEntityView Entities => null!;
        public IReadOnlyList<PlayerRosterEntry> Players => [];
        public IReadOnlyList<IPlayerState> CurrentPlayers => [];
        public double CurtimeSeconds(int tick) => 0;
        public void RequestSeekToFrame(int frameIndex) { }
        public void RequestSeekToTick(int tick) { }
        public void RequestPlay() { }
        public void RequestPause() { }

        public event Action<IPlaybackSnapshot>? Advanced
        {
            add { }
            remove { }
        }
    }
}
