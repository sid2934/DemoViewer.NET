#region

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Services.Zones;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using DemoViewer.NET.Extensions.StratBook.Views.StratBook;
using DemoViewer.NET.Extensions.StratBook.Services.Zones;

#endregion

namespace DemoViewer.NET.UiCapture;

public static partial class Variants
{
    private const double Dust2Level = -99968;

    // A B execute on de_dust2 in the shape of one written before carried marks: the round-start seed turned into a move
    // with a line per player and the spawn positions still on it, then a lurk on the same tick holding copies of them.
    private static StratBookHubView StratEditorSeedMove(double atSeconds)
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_dust2");
        StratBookTabViewModel? strats = null;
        StratBookHubView view = StratEditor(true, true, true, vm =>
        {
            strats = vm;
            vm.SelectedMap = "de_dust2";
            vm.NewStratCommand.Execute(null);
            vm.Editor.Name = "B exec";
            JsonArray steps = [.. SeedMoveSteps().Select(s => JsonSerializer.SerializeToNode(s, StratJsonContext.Default.StratStep))];
            vm.Session.Apply(PatchOp.ReplaceOp("/steps", null, steps));
        }, zones);
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.Canvas.Transport.Pause();
            strats.Canvas.Transport.Seek(DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes.StepSchedule.TickFor(atSeconds, 115));
        }, DispatcherPriority.Background);
        return view;
    }

    // The same execute as it was later left: no E line on the seed, and E's lurk a second later via Long Doors,
    // working five areas. Dragged, E's entry on the lurk step sits a few units from Long Doors' centre, else it is the
    // step's unmarked copy of spawn.
    private static StratBookHubView StratEditorOwnersLurk(double atSeconds, bool dragged)
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_dust2");
        StratBookTabViewModel? strats = null;
        StratBookHubView view = StratEditor(true, true, true, vm =>
        {
            strats = vm;
            vm.SelectedMap = "de_dust2";
            vm.NewStratCommand.Execute(null);
            vm.Editor.Name = "Execute B";
            List<StratStep> steps = SeedMoveSteps();
            steps[0].Assignments!.RemoveAll(a => a.Slot == "E");
            List<string> areas = ["LongDoors", "TopofMid", "Catwalk", "Middle", "MidDoors"];
            StratStep lurk = steps[1];
            lurk.AtSeconds = 114;
            if (dragged && zones?.PlaceArrival("LongDoors", Dust2Level) is { } doors)
            {
                lurk.Positions[4] = new StepPosition { Slot = "E", X = Math.Round(doors.X - 7, 2), Y = Math.Round(doors.Y - 9, 2), LevelMinZ = Dust2Level };
            }

            lurk.Assignments = [new StepAssignment { Slot = "E", Via = ["LongDoors"], Watch = new StepWatch { Places = [.. areas] } }];
            lurk.Lurk!.Areas = [.. areas];
            JsonArray json = [.. steps.Select(s => JsonSerializer.SerializeToNode(s, StratJsonContext.Default.StratStep))];
            vm.Session.Apply(PatchOp.ReplaceOp("/steps", null, json));
        }, zones, null, "de_dust2");
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.Canvas.Transport.Pause();
            strats.Canvas.Transport.Seek(DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes.StepSchedule.TickFor(atSeconds, 115));
        }, DispatcherPriority.Background);
        return view;
    }

    private static List<StratStep> SeedMoveSteps()
    {
        static PlaceRef Point(string place, double x, double y) => new() { Place = place, X = x, Y = y, LevelMinZ = Dust2Level };
        static StepPosition At(string slot, double x, double y) => new() { Slot = slot, X = x, Y = y, LevelMinZ = Dust2Level };
        static List<StepPosition> Seeded() =>
        [
            At("A", -750, -790), At("B", -735, -910), At("C", -775, -690), At("D", -865, -735), At("E", -610, -800),
            .. StratVocabulary.OpponentSlots.Select((s, i) => At(s, 100 + 50 * i, 2250))
        ];

        List<string> watched = ["LongDoors", "OutsideLong", "TopofMid", "Catwalk", "Middle", "MidDoors"];
        return
        [
            new StratStep
            {
                Id = Guid.NewGuid(), AtSeconds = 115, Actor = "all", Verb = "move", Note = "default", Positions = Seeded(),
                Assignments =
                [
                    new StepAssignment { Slot = "A", To = new PlaceRef { Place = "UpperTunnel" } },
                    new StepAssignment { Slot = "B", To = Point("TopofMid", 25, 390) },
                    new StepAssignment { Slot = "C", To = Point("LongDoors", 645, 480) },
                    new StepAssignment { Slot = "D", To = Point("UpperTunnel", -1955, 1070) },
                    new StepAssignment { Slot = "E", To = Point("OutsideLong", 650, 140) }
                ]
            },
            new StratStep
            {
                Id = Guid.NewGuid(), AtSeconds = 115, Actor = "E", Verb = "lurk", Positions = Seeded(),
                Assignments = [new StepAssignment { Slot = "E", Watch = new StepWatch { Places = [.. watched] } }],
                Lurk = new StepLurk { Areas = [.. watched], Rotate = new LurkRotate { AtSeconds = 39, To = Point("LowerTunnel", -580, 1435) } }
            },
            new StratStep
            {
                Id = Guid.NewGuid(), AtSeconds = 75, Actor = "all", Verb = "move", Note = "group up for B",
                Assignments =
                [
                    new StepAssignment { Slot = "A", To = Point("TunnelStairs", -1090, 1105) },
                    new StepAssignment { Slot = "B", To = Point("UpperTunnel", -1750, 1365) },
                    new StepAssignment { Slot = "C", To = Point("OutsideTunnel", -1310, 480) },
                    new StepAssignment { Slot = "D", To = Point("UpperTunnel", -1795, 980) }
                ]
            },
            new StratStep
            {
                Id = Guid.NewGuid(), AtSeconds = 53, Actor = "all", Verb = "push", Note = "entry, A holds under",
                Assignments =
                [
                    new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteB" } },
                    new StepAssignment { Slot = "D", To = new PlaceRef { Place = "BombsiteB" } },
                    new StepAssignment { Slot = "C", To = Point("BombsiteB", -2050, 3015) }
                ]
            }
        ];
    }
}
