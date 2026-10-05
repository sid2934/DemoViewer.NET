#region

using System.Text.Json;
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
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.ViewModels.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using DemoViewer.NET.Extensions.StratBook.Views.StratBook;

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
            StratBookHubViewModel hub = new(layout) { RailLabel = "STRAT BOOK" };
            hub.Sections.Reconcile([
                new WorkspaceTabDescriptor
                {
                    TabId = "stratbook.browser", Header = "Strats", Order = 0, HostId = StratBookHubViewModel.HostId,
                    ViewModelFactory = () => strats, ViewFactory = () => new StratBookTabView()
                },
                new WorkspaceTabDescriptor
                {
                    TabId = "review.queue", Header = "Review", Order = 1, HostId = StratBookHubViewModel.HostId, Badge = "12",
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

            // Each location field keeps room for its text beside its buttons.
            List<string> narrow = [];
            List<DemoViewer.NET.Extensions.StratBook.Controls.PlaceField> fields = [.. content.GetVisualDescendants().OfType<DemoViewer.NET.Extensions.StratBook.Controls.PlaceField>().Where(f => f.IsEffectivelyVisible)];
            foreach (DemoViewer.NET.Extensions.StratBook.Controls.PlaceField field in fields)
            {
                double text = field.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault()?.Bounds.Width ?? 0;
                if (text < 45)
                {
                    narrow.Add($"{field.Name} text {text:F0}");
                }
            }

            int chips = content.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("placedChip") && b.IsEffectivelyVisible);
            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir,
                collapsed ? "strat-editor-1280-collapsed.png" : "strat-editor-1280.png"), new PngBitmapEncoderOptions());
            window.Close();

            using (Assert.Multiple())
            {
                await Assert.That(viewport).IsGreaterThan(collapsed ? 400 : 250);
                await Assert.That(scroll.Extent.Width).IsLessThanOrEqualTo(viewport + 0.5);
                await Assert.That(overflow).IsEmpty()
                    .Because($"the editor is {viewport:F0} wide: {string.Join("; ", overflow.Take(8))}");
                await Assert.That(fields.Count).IsGreaterThan(10).Because("the seeded rows show their location fields");
                await Assert.That(fields.Max(f => f.Chips.Count)).IsGreaterThanOrEqualTo(6).Because("the long lists show as chips the scan measures");
                await Assert.That(narrow).IsEmpty().Because(string.Join("; ", narrow.Take(8)));
                await Assert.That(chips).IsGreaterThanOrEqualTo(4).Because("the seeded entries show as placed chips the scan measures");
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

        // A start with a place and a point per token, the row open: its ten fields are measured with the rest.
        vm.Session.Apply(StratStartBlock.Write(vm.Session.Document!, new StratStart
        {
            Kind = StratStart.CustomKind,
            Positions = [.. StratStartBlock.Tokens.Select((t, i) => new StartPosition { Slot = t, Place = "TSpawn", X = -1234.4 - i, Y = -2560.6, LevelMinZ = -256 })]
        }));
        vm.Editor.Start.IsExpanded = true;

        StratEditorViewModel editor = vm.Editor;
        foreach (string verb in StratVocabulary.Verbs)
        {
            editor.AddStepCommand.Execute(null);
            editor.Steps[^1].Verb = verb;
        }

        // Every field of the widest rows holds a value: other shows them all, and a throw has a lineup.
        int other = StratVocabulary.Verbs.ToList().IndexOf("other");
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{other}/from", null, new JsonObject { ["x"] = -1234.4, ["y"] = -2560.6, ["levelMinZ"] = -256 }));
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{other}/to", null, new JsonObject { ["place"] = "TopofMid" }));
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{other}/utility", null, new JsonObject
        {
            ["kind"] = "smoke", ["lineupId"] = Guid.NewGuid().ToString(), ["landing"] = new JsonObject { ["place"] = "Connector" }
        }));
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{other}/note", null, JsonValue.Create("a long note")));

        // A long via on a travelling step: the chips wrap inside the field.
        int moving = StratVocabulary.Verbs.ToList().IndexOf("move");
        vm.Session.Apply(PatchOp.AddOp($"/steps/{moving}/via", new JsonArray("TopofMid", "Connector", "Underpass", "Apartments", "PalaceAlley")));
        vm.Session.Apply(PatchOp.AddOp($"/steps/{moving}/viaPoints",
            new JsonArray(new JsonObject { ["x"] = -1234.4, ["y"] = -2560.6, ["levelMinZ"] = -256 })));
        int thrown = StratVocabulary.Verbs.ToList().IndexOf("throw");
        vm.Session.Apply(PatchOp.ReplaceOp($"/steps/{thrown}/utility", null,
            new JsonObject { ["kind"] = "smoke", ["lineupId"] = lineup.Id.ToString() }));
        vm.Editor.AddBranchCommand.Execute(null);

        // A step with three lines, each watching two callouts, one at a set angle: the widest line row.
        StratStep lines = new()
        {
            Id = Guid.NewGuid(), AtSeconds = -50, Actor = StratVocabulary.ActorAll, Verb = "move",
            Assignments =
            [
                new StepAssignment
                {
                    Slot = "B", To = new PlaceRef { Place = "PalaceInterior" }, Watch = new StepWatch { Places = ["BombsiteA", "CTSpawn", "TopofMid", "PalaceAlley"] },
                    Via = ["TopofMid", "Connector", "Underpass", "Apartments"], ViaPoints = [new PlaceRef { X = -1234.4, Y = -2560.6, LevelMinZ = -256 }]
                },
                new StepAssignment { Slot = "C", To = new PlaceRef { Place = "Connector" }, Watch = new StepWatch { Places = ["Stairs", "Jungle"] } },
                new StepAssignment
                {
                    Slot = "D", To = new PlaceRef { X = -1234.4, Y = -2560.6, LevelMinZ = -256 },
                    Watch = new StepWatch { Places = ["TRamp", "PalaceAlley"], Points = [new PlaceRef { X = -900, Y = -1500, LevelMinZ = -256 }], YawDegrees = 135 }
                }
            ]
        };
        vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(lines, StratJsonContext.Default.StratStep)));

        // Two players shown as one, with a shared angle and split: the widest compact row. And a lurk with every field set.
        StratStep pair = new()
        {
            Id = Guid.NewGuid(), AtSeconds = -52, Actor = StratVocabulary.ActorAll, Verb = "hold",
            Assignments =
            [
                .. StratVocabulary.Slots.Skip(1).Take(2).Select(s => new StepAssignment
                {
                    Slot = s, To = new PlaceRef { Place = "PalaceInterior" }, Watch = new StepWatch { Places = ["BombsiteA", "CTSpawn", "Jungle", "TopofMid", "PalaceAlley", "Underpass"], YawDegrees = 135 }
                })
            ]
        };
        vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(pair, StratJsonContext.Default.StratStep)));
        int lurk = StratVocabulary.Verbs.ToList().IndexOf("lurk");
        vm.Session.Apply(PatchOp.AddOp($"/steps/{lurk}/lurk", new JsonObject
        {
            ["areas"] = new JsonArray("PalaceInterior", "Connector", "Jungle", "TopofMid", "Underpass", "Apartments"),
            ["areaPoints"] = new JsonArray(new JsonObject { ["x"] = -900, ["y"] = -1500, ["levelMinZ"] = -256 }),
            ["rotate"] = new JsonObject
            {
                ["atSeconds"] = 40, ["when"] = "after first kill", ["to"] = new JsonObject { ["x"] = -1234.4, ["y"] = -2560.6, ["levelMinZ"] = -256 }
            }
        }));

        // Position entries as placed chips: a departure on the lurk with seen spots and opponents beside it, and a pin on
        // the pair's hold.
        static JsonObject Entry(string slot, double x, double y, bool observed = false, double? yaw = null)
        {
            JsonObject entry = new() { ["slot"] = slot, ["x"] = x, ["y"] = y, ["levelMinZ"] = -256 };
            if (observed)
            {
                entry["observed"] = true;
            }

            if (yaw is { } angle)
            {
                entry["yawDegrees"] = angle;
            }

            return entry;
        }

        foreach (JsonObject entry in (JsonObject[])
                 [
                     Entry("E", -1374.25, -412.75), Entry("A", -800, -1200, true), Entry("B", -820, -1210, true),
                     Entry("O1", -300, -900, yaw: 135), Entry("O2", -320, -950, yaw: 90), Entry("O3", -350, -990)
                 ])
        {
            vm.Session.Apply(PatchOp.AddOp($"/steps/{lurk}/positions/-", entry));
        }

        vm.Session.Apply(PatchOp.AddOp($"/steps/{vm.Session.Document!.Steps.Count - 1}/positions/-", Entry("B", -1700.5, -2000.25)));

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
