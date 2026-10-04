#region

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Start row and the clock switch in a window at 1280 by 800: the row is one line until opened, opened it shows a
///     field per token inside the editor's width, focusing it selects the start, and the switch puts the step times on
///     the trigger clock.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class StratStartRowWindowTests
{
    private static readonly StratSpawns Spawns = new(
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(-700 - 40 * i, -800, 0, "TSpawn"))],
        [.. Enumerable.Range(0, 5).Select(i => new SpawnSpot(200 + 40 * i, 2200, 0, "CTSpawn"))]);

    [Test]
    public async Task TheStartRow_OpensToAFieldPerToken_SelectsTheStart_AndTheSwitchMovesTheClock() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            StratSpawnSource spawns = new(_ => Spawns);
            await spawns.ForAsync("de_dust2");
            using StratBookTabViewModel vm = new(new StratStore(null), null, a => Dispatcher.UIThread.Post(a), false, spawns: spawns);
            vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
            vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
            vm.SelectedMap = "de_dust2";
            vm.NewStratCommand.Execute(null);
            vm.Editor.TriggerText = "on the call";
            StratStep move = new() { Id = Guid.NewGuid(), AtSeconds = 115, Actor = StratVocabulary.ActorAll, Verb = "move", To = new PlaceRef { Place = "LongDoors" } };
            vm.Session.Apply(PatchOp.AddOp("/steps/-", JsonSerializer.SerializeToNode(move, StratJsonContext.Default.StratStep)));

            StratBookTabView view = new() { DataContext = vm };
            Window window = new() { Width = 1280, Height = 800, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Border row = view.FindControl<Border>("StartRow")!;
            TextBlock summary = view.FindControl<TextBlock>("StartSummary")!;
            int Fields() => row.GetVisualDescendants().OfType<Controls.PlaceField>().Count(f => f.IsEffectivelyVisible);
            using (Assert.Multiple())
            {
                await Assert.That(row.IsEffectivelyVisible).IsTrue();
                await Assert.That(summary.Text).IsEqualTo("spawn · on the call");
                await Assert.That(Fields()).IsEqualTo(0).Because("the row is one line until opened");
            }

            view.FindControl<Button>("StartExpand")!.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            ScrollViewer scroll = view.FindControl<ScrollViewer>("EditorScroll")!;
            List<string> overflow = [];
            foreach (Control control in row.GetVisualDescendants().OfType<Control>().Where(c => c is TextBox or Button or TextBlock && c.IsEffectivelyVisible))
            {
                if (control.TranslatePoint(new Point(control.Bounds.Width, 0), scroll) is { } right && right.X > scroll.Viewport.Width + 0.5)
                {
                    overflow.Add($"{control.GetType().Name} '{(control as TextBlock)?.Text ?? control.Name}' ends at {right.X:F0}");
                }
            }

            using (Assert.Multiple())
            {
                await Assert.That(Fields()).IsEqualTo(10);
                await Assert.That(overflow).IsEmpty().Because(string.Join("; ", overflow.Take(6)));
                await Assert.That(view.FindControl<TextBox>("StartTrigger")!.Text).IsEqualTo("on the call");
            }

            view.FindControl<TextBox>("StartTrigger")!.Focus();
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Canvas.IsStartSelected).IsTrue().Because("focusing the row selects the start");
                await Assert.That(vm.Editor.Start.IsSelected).IsTrue();
                await Assert.That(vm.Editor.Steps[0].IsSelected).IsFalse();
                await Assert.That(vm.Canvas.Transport.Tick).IsEqualTo(0);
            }

            ComboBox clock = view.FindControl<ComboBox>("ClockSwitch")!;
            clock.SelectedItem = StratEditorViewModel.TriggerClockChoice;
            Dispatcher.UIThread.RunJobs();
            TextBox time = view.GetVisualDescendants().OfType<TextBox>()
                .First(t => t.DataContext is StratStepRow && t.Text?.Contains(':', StringComparison.Ordinal) == true);
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.Document!.Clock.Kind).IsEqualTo(StratClock.TriggerKind);
                await Assert.That(time.Text).IsEqualTo("+0:00");
                await Assert.That(vm.Canvas.ClockText).StartsWith("+0:00");
            }

            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "strat-start-row-window.png"), new PngBitmapEncoderOptions());
            window.Close();
        });

    // A B execute as an older build left it, and a captured strat.
    private static IEnumerable<StratDocument> Opened()
    {
        yield return StratStartBlockTests.Legacy();
        StratDocument captured = StratStartBlockTests.Legacy();
        captured.Id = Guid.NewGuid();
        captured.Origin = new StratOrigin { DemoSha256 = "ab", Round = 4 };
        captured.Start = new StratStart
        {
            Kind = StratStart.CapturedKind,
            Positions = [.. StratVocabulary.Slots.Select((s, i) => new StartPosition { Slot = s, Place = "TSpawn", X = -750 + i, Y = -791, LevelMinZ = -99968, YawDegrees = 45, Observed = true })]
        };
        yield return captured;
    }

    [Test]
    public async Task TabbingThroughTheOpenStartRow_WritesNothing_ToAnOlderOrACapturedStrat() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            foreach (StratDocument document in Opened())
            {
                document.Owner = StratOwner.Me();
                StratStore store = new(null);
                store.Save(document, [], "created");
                using StratBookTabViewModel vm = new(store, null, a => Dispatcher.UIThread.Post(a), false);
                vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
                vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
                vm.OpenStrat(document.Id);
                await Assert.That(vm.Session.Document?.Id).IsEqualTo(document.Id);
                string before = StratStore.Serialize(vm.Session.Document!);

                StratBookTabView view = new() { DataContext = vm };
                Window window = new() { Width = 1280, Height = 800, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                view.FindControl<Button>("StartExpand")!.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                foreach (TextBox box in view.FindControl<Border>("StartRow")!.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible).ToList())
                {
                    box.Focus();
                    Dispatcher.UIThread.RunJobs();
                }

                view.FindControl<TextBox>("StartTrigger")!.Focus();
                Dispatcher.UIThread.RunJobs();
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Session.UndoDepth).IsEqualTo(0).Because(document.Name);
                    await Assert.That(StratStore.Serialize(vm.Session.Document!)).IsEqualTo(before).Because(document.Name);
                }

                window.Close();
            }
        });
}
