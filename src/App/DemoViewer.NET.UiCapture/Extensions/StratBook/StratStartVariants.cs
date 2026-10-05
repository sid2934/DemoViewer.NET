#region

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Strats;
using DemoViewer.NET.Extensions.StratBook.Services.Zones;
using DemoViewer.NET.Extensions.StratBook.ViewModels.StratBook;
using DemoViewer.NET.Extensions.StratBook.Views.StratBook;
using DemoViewer.NET.Views.Shell;

#endregion

namespace DemoViewer.NET.UiCapture;

public static partial class Variants
{
    // A new de_dust2 strat with its spawn start and a move at 1:55, the Start row opened and selected: the tokens stand
    // in spawn at tick 0 and the move's routes run from there.
    private static HubTabView StratStartRow(bool panesCollapsed)
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_dust2");
        StratBookTabViewModel? strats = null;
        HubTabView view = StratEditor(panesCollapsed, panesCollapsed, true, vm =>
        {
            strats = vm;
            vm.Editor.Name = "Execute B";
            vm.Editor.TriggerText = "on the call";
            StratDocument document = vm.Session.Document!;
            if (StratSpawnSource.LoadShipped("de_dust2", null)?.StartFor(document) is { } start)
            {
                vm.Session.Apply(StratStartBlock.Write(document, start));
            }

            StratStep move = new()
            {
                Id = Guid.NewGuid(), AtSeconds = 115, Actor = "all", Verb = "move", Note = "default 1-2-2",
                Assignments =
                [
                    new StepAssignment { Slot = "A", To = new PlaceRef { Place = "UpperTunnel" } },
                    new StepAssignment { Slot = "B", To = new PlaceRef { Place = "TopofMid" } },
                    new StepAssignment { Slot = "C", To = new PlaceRef { Place = "LongDoors" } },
                    new StepAssignment { Slot = "D", To = new PlaceRef { Place = "UpperTunnel" } },
                    new StepAssignment { Slot = "E", To = new PlaceRef { Place = "OutsideLong" } }
                ]
            };
            vm.Session.Apply(PatchOp.ReplaceOp("/steps", null, new JsonArray(JsonSerializer.SerializeToNode(move, StratJsonContext.Default.StratStep))));
            vm.Editor.Start.IsExpanded = true;
        }, zones, null, "de_dust2");
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.StepSelection.SelectStart();
            if (view.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "StartRow") is { } row)
            {
                row.BringIntoView();
            }
        }, DispatcherPriority.Background);
        return view;
    }

    // A strat timed from its trigger: contact at B apps, then a flash, the push and the plant counted up from it, with the
    // playhead five seconds in.
    private static HubTabView StratTriggerClock()
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_mirage");
        StratBookTabViewModel? strats = null;
        HubTabView view = StratEditor(true, true, true, vm =>
        {
            strats = vm;
            vm.Editor.Name = "B apps hit after contact";
            vm.Editor.TriggerText = "contact at B apps";
            StratDocument document = vm.Session.Document!;
            if (StratSpawnSource.LoadShipped("de_mirage", null)?.StartFor(document) is { } start)
            {
                vm.Session.Apply(StratStartBlock.Write(document, start));
            }

            vm.Editor.ClockChoice = StratEditorViewModel.TriggerClockChoice;
            StratStep[] steps =
            [
                new() { Id = Guid.NewGuid(), AtSeconds = 0, Actor = "all", Verb = "move", To = new PlaceRef { Place = "Apartments" }, Note = "on contact" },
                new() { Id = Guid.NewGuid(), AtSeconds = 12, Actor = "C", Verb = "throw", Utility = new UtilityRef { Kind = "flash" }, Note = "pop over apps" },
                new()
                {
                    Id = Guid.NewGuid(), AtSeconds = 14, Actor = "all", Verb = "push", Note = "entry and trade",
                    Assignments =
                    [
                        new StepAssignment { Slot = "A", To = new PlaceRef { Place = "BombsiteB" } },
                        new StepAssignment { Slot = "B", To = new PlaceRef { Place = "BombsiteB" } }
                    ]
                },
                new() { Id = Guid.NewGuid(), AtSeconds = 22, Actor = "B", Verb = "plant", To = new PlaceRef { Place = "BombsiteB" } }
            ];
            JsonArray json = [.. steps.Select(s => JsonSerializer.SerializeToNode(s, StratJsonContext.Default.StratStep))];
            vm.Session.Apply(PatchOp.ReplaceOp("/steps", null, json));
        }, zones, null, "de_mirage");
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.Canvas.Transport.Pause();
            strats.Canvas.Transport.Seek(5 * DemoViewer.NET.Extensions.StratBook.Playback2D.Keyframes.StepSchedule.TicksPerSecond);
            if (view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "StepRows") is { } rows)
            {
                rows.BringIntoView();
            }
        }, DispatcherPriority.Background);
        return view;
    }
}
