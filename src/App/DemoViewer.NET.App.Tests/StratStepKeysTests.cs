#region

using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>The step table's keys and tab order in the real view, and the inline marker it draws.</summary>
[NotInParallel]
[Category("Integration")]
public class StratStepKeysTests
{
    private static (Window Window, StratBookTabView View) Show(StratBookTabViewModel vm)
    {
        StratBookTabView view = new() { DataContext = vm };
        Window window = new() { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static ContentPresenter RowContainer(StratBookTabView view, int index) =>
        (ContentPresenter)view.FindControl<ItemsControl>("StepRows")!.ContainerFromIndex(index)!;

    private static List<Control> Fields(ContentPresenter row) =>
    [
        .. row.GetVisualDescendants().OfType<Control>()
            .Where(c => (c is TextBox or ComboBox || c.Name == "WhoButton") && c.IsEffectivelyVisible)
    ];

    private static StratStepRow? FocusedRow(Window window) =>
        (window.FocusManager?.GetFocusedElement() as Control)?.DataContext as StratStepRow;

    [Test]
    public async Task FocusInALinesField_SelectsTheStepAndTheLine_AndPlusPlayerAddsOne() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
            StratStep step = StratStepEditingTests.Step(90, "all", "move");
            step.To = null;
            step.Assignments = [new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteA" } }, new StepAssignment { Slot = "C" }];
            StratStepEditingTests.Seed(vm, StratStepEditingTests.Step(100, "A", "move"), step);
            (Window window, StratBookTabView view) = Show(vm);
            int index = vm.Editor.Steps.Count - 1;

            ContentPresenter row = RowContainer(view, index);
            TextBox cPlace = row.GetVisualDescendants().OfType<TextBox>()
                .First(t => t.DataContext is StratLineRow { Slot: "C" } && t.IsEffectivelyVisible);
            cPlace.Focus();
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(vm.StepSelection.SelectedStepId).IsEqualTo(step.Id);
                await Assert.That(vm.Canvas.SelectedLineSlot).IsEqualTo("C");
                await Assert.That(vm.Editor.Steps[index].Lines.Single(l => l.IsSelected).Slot).IsEqualTo("C");
            }

            Button add = row.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "+ player");
            add.Command!.Execute(add.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(vm.Session.Document!.Steps[index].Assignments!.Select(l => l.Slot)).IsEquivalentTo(["B", "C", "A"]);
            window.Close();
        });

    [Test]
    public async Task EnterOnAWatchingSuggestion_PicksIt_AsOneEntry_WithoutAddingAStep() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
            StratStepEditingTests.Seed(vm, StratStepEditingTests.Step(100, "B", "hold"));
            (Window window, StratBookTabView view) = Show(vm);
            int index = vm.Editor.Steps.Count - 1;
            int steps = vm.Session.Document!.Steps.Count;
            int depth = vm.Session.UndoDepth;

            AutoCompleteBox watching = RowContainer(view, index).GetVisualDescendants().OfType<AutoCompleteBox>().Single(a => a.Name == "GroupWatchField");
            watching.GetVisualDescendants().OfType<TextBox>().First().Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("Bombsite");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(watching.IsDropDownOpen).IsTrue().Because("the map's callouts are suggested");

            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            RowContainer(view, index).Focus();
            Dispatcher.UIThread.RunJobs();

            StratStep step = vm.Session.Document!.Steps[index];
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.Document!.Steps.Count).IsEqualTo(steps).Because("Enter went to the suggestion list");
                await Assert.That(step.Assignments!.Single().Watch!.Places).IsEquivalentTo(["BombsiteA"]);
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
            }

            window.Close();
        });

    [Test]
    public async Task EnterInARowsField_AddsAStepAfterThatRow_AndFocusesIt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
            StratStepEditingTests.Seed(vm, StratStepEditingTests.Step(100, "A", "move"), StratStepEditingTests.Step(80, "B", "move"));
            (Window window, StratBookTabView view) = Show(vm);

            TextBox note = RowContainer(view, 0).GetVisualDescendants().OfType<TextBox>().Last(t => t.IsEffectivelyVisible);
            note.Focus();
            note.Text = "typed, not yet committed";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            StratDocument document = vm.Session.Document!;
            using (Assert.Multiple())
            {
                await Assert.That(document.Steps.Count).IsEqualTo(3);
                await Assert.That(document.Steps[0].Note).IsEqualTo("typed, not yet committed").Because("Enter commits the field first");
                await Assert.That(document.Steps[1].AtSeconds).IsEqualTo(95);
                await Assert.That(document.Steps[2].Actor).IsEqualTo("B");
                await Assert.That(FocusedRow(window)?.Id).IsEqualTo(document.Steps[1].Id);
                await Assert.That(window.FocusManager?.GetFocusedElement()).IsTypeOf<TextBox>();
            }

            window.Close();
        });

    [Test]
    public async Task CtrlD_Duplicates_AndDelete_RemovesOnlyFromTheRowItself() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
            StratStepEditingTests.Seed(vm, StratStepEditingTests.Step(100, "A", "move"), StratStepEditingTests.Step(80, "B", "move"));
            (Window window, StratBookTabView view) = Show(vm);

            Fields(RowContainer(view, 1))[0].Focus();
            window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            StratDocument document = vm.Session.Document!;
            await Assert.That(document.Steps.Select(s => s.Actor)).IsEquivalentTo(["A", "B", "B"]);
            await Assert.That(FocusedRow(window)?.Id).IsEqualTo(document.Steps[2].Id);

            // Inside a field, Delete edits the text.
            TextBox time = (TextBox)Fields(RowContainer(view, 0))[0];
            time.Focus();
            time.ClearSelection();
            time.CaretIndex = 0;
            string before = time.Text ?? "";
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.Document!.Steps.Count).IsEqualTo(3);
                await Assert.That(time.Text).IsEqualTo(before[1..]).Because("the text box took the key");
            }

            // Escape leaves the field for its row; Delete there removes the row.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(window.FocusManager?.GetFocusedElement()).IsSameReferenceAs(RowContainer(view, 0));
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.Document!.Steps.Select(s => s.Actor)).IsEquivalentTo(["B", "B"]);
                await Assert.That(window.FocusManager?.GetFocusedElement()).IsSameReferenceAs(RowContainer(view, 0));
            }

            window.Close();
        });

    [Test]
    public async Task Tab_WalksTheVisibleFieldsLeftToRight_ThenTheNextRow() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
            StratStepEditingTests.Seed(vm, StratStepEditingTests.Step(100, "A", "move"), StratStepEditingTests.Step(80, "B", "wait"));
            (Window window, StratBookTabView view) = Show(vm);

            List<Control> first = Fields(RowContainer(view, 0));
            List<Control> second = Fields(RowContainer(view, 1));
            List<object> expected = [.. first, second[0]];

            // A one-player move shows time, who, verb, its to (a move watches no place), from and note; the next
            // row starts at its time.
            await Assert.That(first.Count).IsEqualTo(6);
            first[0].Focus();
            List<object> walked = [window.FocusManager!.GetFocusedElement()!];
            for (int i = 1; i < expected.Count; i++)
            {
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                walked.Add(window.FocusManager!.GetFocusedElement()!);
            }

            await Assert.That(walked).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            window.Close();
        });

    [Test]
    public async Task AMoveWithoutTo_DrawsAMarkerBesideTo_AndItClearsWhenToIsSet() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = StratStepEditingTests.OpenNew();
            vm.Editor.AddStepCommand.Execute(null);
            (Window window, StratBookTabView view) = Show(vm);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            TextBlock Marker() => RowContainer(view, 0).GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("stratIssue") && t.Text == "⚠" && t.IsEffectivelyVisible
                           && t.GetVisualParent() is StackPanel { Orientation: Avalonia.Layout.Orientation.Horizontal });

            TextBlock marker = Marker();
            using (Assert.Multiple())
            {
                await Assert.That(marker.IsEffectivelyVisible).IsTrue();
                await Assert.That(ToolTip.GetTip(marker) as string).Contains("destination");
                await Assert.That(RowContainer(view, 0).GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.IsEffectivelyVisible && t.Text == "warning: a move has no destination place (to)")).IsTrue();
            }

            window.CaptureRenderedFrame()?.Save(Path.Combine(HeadlessSession.ArtifactDir, "strat-step-inline-check.png"),
                new PngBitmapEncoderOptions());

            vm.Editor.Steps[0].ToText = "BombsiteA";
            Dispatcher.UIThread.RunJobs();
            await Assert.That(marker.IsEffectivelyVisible).IsFalse();
            window.Close();
        });
}
