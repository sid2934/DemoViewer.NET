#region

using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Abstractions;
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

    private static StratBookTabViewModel Seeded(StratBookLayout layout)
    {
        StratBookTabViewModel vm = new(new StratStore(null), null, null, false, layout: layout);
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
        vm.Editor.AddBranchCommand.Execute(null);
        return vm;
    }

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
