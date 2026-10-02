#region

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Playback2D.Core.Keyframes;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Zones;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>Tokens routed round dust2's walls, with the faint line ahead of each moving token.</summary>
public static partial class Variants
{
    private const double Dust2Floor = -99968;

    // The owner's Execute B on dust2, paused at a round clock time: tokens on their routes, each with its way ahead.
    private static StratBookHubView StratRoutingExecuteB(double atSeconds)
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_dust2");
        StratBookTabViewModel? strats = null;
        StratBookHubView view = StratEditor(true, true, true, vm =>
        {
            strats = vm;
            Replace(vm, "Execute B", ExecuteB(zones));
        }, zones, null, "de_dust2");
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.Canvas.Transport.Pause();
            strats.Canvas.Transport.Seek(StepSchedule.TickFor(atSeconds, 115));
        }, DispatcherPriority.Background);
        return view;
    }

    // B goes to B site through Middle rather than the tunnels, the shortest way: the step row shows its via, and the
    // canvas the route through mid, mid-run.
    private static StratBookHubView StratRoutingVia()
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_dust2");
        StratBookTabViewModel? strats = null;
        StratBookHubView view = StratEditor(true, true, true, vm =>
        {
            strats = vm;
            (double X, double Y) spawn = Centre(zones, "TSpawn");
            StratStep seed = new()
            {
                Id = Guid.NewGuid(), AtSeconds = 115, Actor = StratVocabulary.ActorAll, Verb = "hold",
                Positions = [.. StratVocabulary.Slots.Select((s, i) => At(s, spawn.X - 120 + 60 * i, spawn.Y))]
            };
            StratStep via = new()
            {
                Id = Guid.NewGuid(), AtSeconds = 110, Actor = "B", Verb = "move", From = new PlaceRef { Place = "TSpawn" },
                To = new PlaceRef { Place = "BombsiteB" }, Via = ["Middle"]
            };
            StratStep tunnels = new()
            {
                Id = Guid.NewGuid(), AtSeconds = 110, Actor = "C", Verb = "move", From = new PlaceRef { Place = "TSpawn" },
                To = new PlaceRef { Place = "BombsiteB" }
            };
            Replace(vm, "B through mid", [seed, tunnels, via]);
        }, zones, null, "de_dust2");
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.Canvas.Transport.Pause();
            strats.StepSelection.Select(strats.Editor.Steps[^1].Id);
            strats.Canvas.Transport.Seek(StepSchedule.TickFor(99, 115));
            if (view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "StepRows") is { } rows
                && rows.ContainerFromIndex(strats.Editor.Steps.Count - 1) is { } row)
            {
                row.BringIntoView();
            }
        }, DispatcherPriority.Background);
        return view;
    }

    private static void Replace(StratBookTabViewModel vm, string name, IReadOnlyList<StratStep> steps)
    {
        vm.Editor.Name = name;
        vm.Session.Apply(PatchOp.ReplaceOp("/steps", null,
            new JsonArray(steps.Select(s => JsonSerializer.SerializeToNode(s, StratJsonContext.Default.StratStep)).ToArray())));
    }

    private static (double X, double Y) Centre(IZonePlaceResolver? zones, string place) =>
        zones?.PlaceArrival(place, Dust2Floor) is { } a ? (Math.Round(a.X), Math.Round(a.Y)) : (0, 0);

    private static StepPosition At(string slot, double x, double y) => new() { Slot = slot, X = x, Y = y, LevelMinZ = Dust2Floor };

    // The shape StratRoutingTests.ExecuteB measures: eight steps, ten tokens, a lurk, two throws, dragged opponents.
    private static List<StratStep> ExecuteB(IZonePlaceResolver? zones)
    {
        (double X, double Y) spawn = Centre(zones, "TSpawn"), ct = Centre(zones, "CTSpawn"), site = Centre(zones, "BombsiteB");
        (double X, double Y) doors = Centre(zones, "BDoors"), mid = Centre(zones, "Middle");
        StratStep Step(double at, string actor, string verb) => new() { Id = Guid.NewGuid(), AtSeconds = at, Actor = actor, Verb = verb };
        StepAssignment Line(string slot, string place) => new() { Slot = slot, To = new PlaceRef { Place = place } };

        StratStep seed = Step(115, StratVocabulary.ActorAll, "hold");
        seed.Positions =
        [
            .. StratVocabulary.Slots.Select((s, i) => At(s, spawn.X - 120 + 60 * i, spawn.Y)),
            At("O1", ct.X, ct.Y), At("O2", ct.X + 60, ct.Y), At("O3", site.X, site.Y), At("O4", doors.X, doors.Y), At("O5", mid.X, mid.Y)
        ];
        StratStep split = Step(108, StratVocabulary.ActorAll, "move");
        split.Assignments = [Line("A", "UpperTunnel"), Line("B", "UpperTunnel"), Line("C", "OutsideTunnel"), Line("D", "TopofMid")];
        StratStep lurk = Step(108, "E", "lurk");
        lurk.Lurk = new StepLurk { Areas = ["Middle"], Rotate = new LurkRotate { AtSeconds = 70, To = new PlaceRef { Place = "BombsiteB" } } };
        StratStep stack = Step(96, StratVocabulary.ActorAll, "move");
        stack.Assignments = [Line("A", "UpperTunnel"), Line("C", "UpperTunnel"), Line("D", "LowerTunnel")];
        stack.Positions = [At("O3", site.X + 150, site.Y + 80), At("O5", mid.X + 200, mid.Y + 300)];
        StratStep smoke = Step(90, "B", "throw");
        smoke.Utility = new UtilityRef { Kind = "smoke", Landing = new UtilityLanding { Place = "BDoors" } };
        StratStep flash = Step(88, "C", "throw");
        flash.Utility = new UtilityRef { Kind = "flash", Landing = new UtilityLanding { Place = "BombsiteB" } };
        StratStep push = Step(85, StratVocabulary.ActorAll, "push");
        push.Assignments = [Line("A", "BombsiteB"), Line("B", "BombsiteB"), Line("C", "BDoors"), Line("D", "BombsiteB")];
        push.Positions = [At("O1", doors.X + 200, doors.Y - 200), At("O2", mid.X - 150, mid.Y + 400)];
        StratStep plant = Step(75, "A", "plant");
        plant.To = new PlaceRef { Place = "BombsiteB" };
        plant.Positions = [At("O1", doors.X, doors.Y + 100), At("O4", site.X - 100, site.Y)];
        return [seed, split, lurk, stack, smoke, flash, push, plant];
    }
}
