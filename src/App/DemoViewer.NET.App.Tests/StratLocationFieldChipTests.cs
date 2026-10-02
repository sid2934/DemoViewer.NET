#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Modules.StratBook.Canvas;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;
using static DemoViewer.NET.AppTests.StratMapFirstTests;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A multi location field (watching, via, lurk areas): one chip per entry over an add field. A pick, a typed name or
///     a typed coordinate appends; a duplicate is refused; chips are removed and moved one at a time, each one undo
///     entry with exact bytes back.
/// </summary>
public partial class StratLocationFieldTests
{
    private static PlaceRef Named(string place) => new() { Place = place };

    private static string Bytes(StratBookTabViewModel vm) => StratStore.Serialize(vm.Session.Document!);

    [Test]
    public async Task TheAddField_AppendsByPickNameAndCoordinate_RefusesDuplicates_AndDoesNotOfferChosenCallouts()
    {
        PlaceFieldModel model = new(true) { Options = PlaceFieldOptions.For(Mirage), CurrentLevelMinZ = -256 };
        model.Load([Named("Stairs")]);
        model.Open();
        await Assert.That(model.Items.Any(o => o.Place == "Stairs")).IsFalse().Because("a chosen callout is not offered again");

        model.Type("jun");
        IReadOnlyList<PlaceRef> picked = model.Accept()!;
        model.Type("palace");
        IReadOnlyList<PlaceRef> typed = model.Commit()!;
        model.Type("(10, -20.5)");
        IReadOnlyList<PlaceRef> point = model.Commit()!;
        using (Assert.Multiple())
        {
            await Assert.That(picked.Select(p => p.Place ?? "")).IsEquivalentTo(["Stairs", "Jungle"]);
            await Assert.That(typed.Select(p => p.Place ?? "")).IsEquivalentTo(["Stairs", "Jungle", "PalaceInterior"]).Because("an alias resolves");
            await Assert.That((point[3].X, point[3].Y, point[3].LevelMinZ)).IsEqualTo(((double?)10, (double?)-20.5, (double?)-256));
            await Assert.That(model.Text).IsEmpty();
        }

        // Typed exactly, a chosen callout highlights nothing, so Enter cannot add a neighbour that starts the same.
        model.Type("stairs");
        await Assert.That(model.Highlight).IsEqualTo(-1);
        using (Assert.Multiple())
        {
            await Assert.That(model.Accept()).IsNull();
            await Assert.That(model.Commit()).IsNull().Because("a duplicate name is refused quietly");
            await Assert.That(model.Text).IsEmpty();
        }

        model.Type("10 -20.5");
        await Assert.That(model.Commit()).IsNull().Because("a duplicate point is refused quietly");
        await Assert.That(model.Value.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Chips_AreRemovedAndMovedOneAtATime_WithinThePlacesOrThePoints_AndPointsStayExact()
    {
        PlaceFieldModel model = new(true) { Options = PlaceFieldOptions.For(Mirage) };
        PlaceRef a = Point(1.2, 2, -256), b = Point(1.4, 2, -256);
        model.Load([Named("Stairs"), Named("Jungle"), a, b]);
        using (Assert.Multiple())
        {
            await Assert.That(model.Shift(1, -1)!.Select(p => p.Place)).IsEquivalentTo(["Jungle", "Stairs", null, null]);
            await Assert.That(model.Shift(0, -1)).IsNull().Because("the first chip has nowhere to go");
            await Assert.That(model.Shift(1, 1)).IsNull().Because("a list is stored places first, then points");
            await Assert.That(model.Shift(3, -1)!.Select(p => p.X)).IsEquivalentTo([null, null, (double?)1.4, 1.2]);
        }

        IReadOnlyList<PlaceRef> left = model.Remove(0);
        using (Assert.Multiple())
        {
            await Assert.That(left.Select(p => p.Place ?? $"{p.X}")).IsEquivalentTo(["Stairs", "1.4", "1.2"])
                .Because("two points that print alike stay two points");
            await Assert.That(StratLocations.Same(left[1], b)).IsTrue();
            await Assert.That(StratLocations.Same(left[2], a)).IsTrue();
            await Assert.That(PlaceFieldModel.DisplayOf([left[1]], Mirage)).IsEqualTo(PlaceFieldModel.DisplayOf([left[2]], Mirage));
        }
    }

    [Test]
    public async Task TheAddField_EnterAppends_BackspaceSelectsThenRemoves_AltArrowMoves_AndAnUntouchedListPassesEnter() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (Window window, RowLike row, PlaceField field) = ShowField(VerticalAlignment.Top, true);
            field.Value = [Named("Stairs")];
            Dispatcher.UIThread.RunJobs();
            await Assert.That(field.Chips.Count).IsEqualTo(1);

            BoxOf(field).Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(row.Enters).IsEqualTo(1).Because("a list only opened by focus passes Enter to the row");

            BoxOf(field).Focus();
            window.KeyTextInput("jun");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyTextInput("(10, 20)");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(field.IsDropDownOpen).IsFalse().Because("no callout matches a coordinate");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyTextInput("stairs");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(field.Value!.Select(p => p.Place ?? $"{p.X},{p.Y}")).IsEquivalentTo(["Stairs", "Jungle", "10,20"]);
                await Assert.That(field.Chips.Count).IsEqualTo(3);
                await Assert.That(BoxOf(field).Text ?? "").IsEmpty();
                await Assert.That(row.Enters).IsEqualTo(1).Because("Enter on typed text stays with the field");
            }

            // Backspace in the empty field selects the last chip; a second removes it and returns to the field.
            window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(field.Chips[2].IsFocused).IsTrue();
            window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(field.Value!.Select(p => p.Place ?? "")).IsEquivalentTo(["Stairs", "Jungle"]);
                await Assert.That(BoxOf(field).IsFocused).IsTrue();
            }

            // Alt+Left moves the focused chip, which keeps the focus.
            window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(field.Value!.Select(p => p.Place ?? "")).IsEquivalentTo(["Jungle", "Stairs"]);
                await Assert.That(field.Chips[0].IsFocused).IsTrue();
                await Assert.That(row.Enters).IsEqualTo(1);
            }

            window.Close();
        });

    // A window at the editor's size with the Strats tab open on a seeded step.
    private static (Window Window, StratBookTabView View, Control Row, int Index) ShowStep(StratBookTabViewModel vm)
    {
        StratBookTabView view = new() { DataContext = vm };
        Window window = new() { Width = 1280, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        int index = vm.Editor.Steps.Count - 1;
        Control row = (Control)view.FindControl<ItemsControl>("StepRows")!.ContainerFromIndex(index)!;
        row.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        return (window, view, row, index);
    }

    private static StratBookTabViewModel OpenOnMirage()
    {
        StratBookTabViewModel vm = new(new StratStore(null), null, null, false,
            canvasPlaces: _ => Task.FromResult<IZonePlaceResolver?>(new EverywhereIs("BombsiteB")));
        vm.Session.AutoSaveDelay = TimeSpan.FromHours(1);
        vm.Session.IdleCommitDelay = TimeSpan.FromHours(1);
        vm.SelectedMap = "de_mirage";
        vm.NewStratCommand.Execute(null);
        return vm;
    }

    private static PlaceField FieldIn(Control row, params string[] names) =>
        row.GetVisualDescendants().OfType<PlaceField>().First(f => names.Contains(f.Name) && f.IsEffectivelyVisible);

    private static Point Centre(Visual visual, TopLevel top) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), top)!.Value;

    private static void Click(Window window, Visual visual)
    {
        Point at = Centre(visual, window);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [Test]
    public async Task InTheStepRow_AChipsCloseAndAClickInTheAddList_AreOneEntryEach_WithExactUndoAndRedo() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = OpenOnMirage();
            StratStep lurk = StratStepEditingTests.Step(100, "E", "lurk");
            lurk.Lurk = new StepLurk { Areas = ["PalaceInterior", "Connector"], AreaPoints = [Point(-500.4, -900.6)] };
            StratStepEditingTests.Seed(vm, lurk);
            (Window window, StratBookTabView _, Control row, int index) = ShowStep(vm);
            PlaceField areas = FieldIn(row, "LurkAreasField");
            await Assert.That(areas.Chips.Count).IsEqualTo(3);

            // The point chip's close: areaPoints goes, and undo brings it back to the byte.
            string before = Bytes(vm);
            int depth = vm.Session.UndoDepth;
            Click(window, areas.Chips[2].GetVisualDescendants().OfType<Button>().Single());
            string removed = Bytes(vm);
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.Document!.Steps[index].Lurk!.AreaPoints).IsNull();
                await Assert.That(vm.Session.Document!.Steps[index].Lurk!.Areas).IsEquivalentTo(["PalaceInterior", "Connector"]);
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
                await Assert.That(areas.Chips.Count).IsEqualTo(2);
            }

            vm.Session.Undo();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(Bytes(vm)).IsEqualTo(before);
            await Assert.That(areas.Chips.Count).IsEqualTo(3).Because("an undo pushes the list back into the chips");
            vm.Session.Redo();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(Bytes(vm)).IsEqualTo(removed);

            // A click on a row of the add list appends it.
            BoxOf(areas).Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("jung");
            Dispatcher.UIThread.RunJobs();
            ListBox list = areas.FindControl<ListBox>("OptionList")!;
            using (Assert.Multiple())
            {
                await Assert.That(areas.IsDropDownOpen).IsTrue();
                await Assert.That(areas.Model.Items.Any(o => o.Place is "PalaceInterior" or "Connector")).IsFalse();
            }

            string beforeAdd = Bytes(vm);
            Click(window, list.ContainerFromIndex(0)!);
            string added = Bytes(vm);
            using (Assert.Multiple())
            {
                await Assert.That(vm.Session.Document!.Steps[index].Lurk!.Areas).IsEquivalentTo(["PalaceInterior", "Connector", "Jungle"]);
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 2);
                await Assert.That(areas.Chips.Count).IsEqualTo(3);
                await Assert.That(BoxOf(areas).Text ?? "").IsEmpty();
            }

            vm.Session.Undo();
            await Assert.That(Bytes(vm)).IsEqualTo(beforeAdd);
            vm.Session.Redo();
            await Assert.That(Bytes(vm)).IsEqualTo(added);
            window.Close();
        });

    [Test]
    public async Task InTheStepRow_AltLeftMovesAWatchedCallout_OneEntry_AndTheConeFacesTheNewFirst() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = OpenOnMirage();
            StratStep hold = StratStepEditingTests.Step(100, "B", "hold", StratStepEditingTests.At("B", 0));
            hold.Assignments = [new StepAssignment { Slot = "B", Watch = new StepWatch { Places = ["Stairs", "Jungle"] } }];
            StratStepEditingTests.Seed(vm, hold);
            (Window window, StratBookTabView view, Control row, int index) = ShowStep(vm);
            PlaceField watch = FieldIn(row, "LineWatchField", "GroupWatchField");
            string before = Bytes(vm);
            int depth = vm.Session.UndoDepth;
            int steps = vm.Session.Document!.Steps.Count;

            // The untouched add field still passes Enter to the row, which adds a step; undo takes it away.
            BoxOf(watch).Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(vm.Session.Document!.Steps.Count).IsEqualTo(steps + 1);
            vm.Session.Undo();
            Dispatcher.UIThread.RunJobs();
            row = (Control)view.FindControl<ItemsControl>("StepRows")!.ContainerFromIndex(index)!;
            watch = FieldIn(row, "LineWatchField", "GroupWatchField");

            BoxOf(watch).Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();
            StratDocument document = vm.Session.Document!;
            using (Assert.Multiple())
            {
                await Assert.That(document.Steps[index].Assignments!.Single().Watch!.Places).IsEquivalentTo(["Jungle", "Stairs"]);
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
                await Assert.That(document.Steps.Count).IsEqualTo(steps).Because("Alt+Left on a chip is not a step move");
            }

            // Stairs up and to the right of B, Jungle up and to the left: the token now faces Jungle.
            PlaceCentreResolver centres = (place, _) => place switch { "Stairs" => (100, 100), "Jungle" => (-100, 100), _ => null };
            TokenTrack b = StratSceneProjection.Build(document, StratPath.MainLine(document), null, centres).Tracks.Single(t => t.Slot == "B");
            await Assert.That(b.TrySample(StepSchedule.TickFor(100, 115), out TokenKeyframe sample) ? sample.YawDegrees : float.NaN).IsEqualTo(135f);

            string moved = Bytes(vm);
            vm.Session.Undo();
            await Assert.That(Bytes(vm)).IsEqualTo(before);
            vm.Session.Redo();
            await Assert.That(Bytes(vm)).IsEqualTo(moved);
            window.Close();
        });

    [Test]
    public async Task AMapPick_OnAListField_AppendsAChip_AsOneEntry() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using StratBookTabViewModel vm = OpenOnMirage();
            StratStep move = StratStepEditingTests.Step(100, "C", "move");
            move.Via = ["Connector"];
            StratStepEditingTests.Seed(vm, move);
            (Window window, StratBookTabView _, Control row, int index) = ShowStep(vm);
            PlaceField via = FieldIn(row, "GroupViaField", "LineViaField");
            await Assert.That(via.Chips.Count).IsEqualTo(1);

            int depth = vm.Session.UndoDepth;
            via.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PickButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            await Assert.That(via.IsPicking).IsTrue();
            vm.Canvas.TryTagPositionAt(Upper, 700, 800);
            Dispatcher.UIThread.RunJobs();
            using (Assert.Multiple())
            {
                await Assert.That(StratLocations.Via(vm.Session.Document!.Steps[index].Via, vm.Session.Document!.Steps[index].ViaPoints)
                    .Select(p => p.Place ?? "")).IsEquivalentTo(["Connector", "BombsiteB"]);
                await Assert.That(vm.Session.UndoDepth).IsEqualTo(depth + 1);
                await Assert.That(via.Chips.Count).IsEqualTo(2);
            }

            window.Close();
        });
}
