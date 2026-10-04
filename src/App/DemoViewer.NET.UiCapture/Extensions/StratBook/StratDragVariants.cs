#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Core.Input;
using DemoViewer.NET.Services.RoundIndex;
using DemoViewer.NET.Services.Strats;
using DemoViewer.NET.Services.Zones;
using DemoViewer.NET.ViewModels.StratBook;
using DemoViewer.NET.Views.StratBook;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>A token drag on Execute B's lurk step: mid-drag with its ghost and label, after the release, and an old drag as a chip.</summary>
public static partial class Variants
{
    private const int LurkStep = 2;

    private static string LongDoors(IZonePlaceResolver? zones) =>
        zones?.PlaceNames.FirstOrDefault(n => n.Equals("LongDoors", StringComparison.Ordinal))
        ?? zones?.PlaceNames.FirstOrDefault(n => n.Contains("Long", StringComparison.Ordinal)) ?? "LongDoors";

    // E's lurk selected and E dragged to Long Doors: released or not.
    private static StratBookHubView StratDrag(bool release)
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
            strats!.StepSelection.Select(strats.Editor.Steps[LurkStep].Id);
            BringRowIntoView(view, LurkStep);
            Dispatcher.UIThread.Post(() =>
            {
                (double X, double Y) doors = Centre(zones, LongDoors(zones));
                (double X, double Y) start = Centre(zones, "TSpawn");
                SKPoint drop = new((float)doors.X + 40, (float)doors.Y - 30);
                strats.Canvas.BeginDrag("E", TokenGrip.Body);
                strats.Canvas.MoveTo("E", new SKPoint((float)(start.X + doors.X) / 2, (float)(start.Y + doors.Y) / 2), Dust2Floor);
                strats.Canvas.MoveTo("E", drop, Dust2Floor);
                if (release)
                {
                    strats.Canvas.EndDrag();
                    return;
                }

                Dispatcher.UIThread.Post(() => PlaceLabel(view, strats.Canvas, drop), DispatcherPriority.Background);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
        return view;
    }

    // The file before this change: E's lurk carries an old drag's departure across the map, and the seed's spawn
    // spots and opponents sit on step 1.
    private static StratBookHubView StratDragPlaced()
    {
        IZonePlaceResolver? zones = new AssetZonePlaceResolverSource().TryGet("de_dust2");
        StratBookTabViewModel? strats = null;
        StratBookHubView view = StratEditor(true, true, true, vm =>
        {
            strats = vm;
            List<StratStep> steps = ExecuteB(zones);
            (double X, double Y) doors = Centre(zones, LongDoors(zones));
            steps[LurkStep].Positions = [At("E", doors.X, doors.Y)];
            Replace(vm, "Execute B", steps);
        }, zones, null, "de_dust2");
        view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            strats!.StepSelection.Select(strats.Editor.Steps[LurkStep].Id);
            BringRowIntoView(view, LurkStep);
        }, DispatcherPriority.Background);
        return view;
    }

    private static void BringRowIntoView(Control view, int index)
    {
        if (view.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(c => c.Name == "StepRows") is { } rows
            && rows.ContainerFromIndex(index) is { } row)
        {
            row.BringIntoView();
        }
    }

    // The label sits where the pointer let go: the scene's world point on the canvas host.
    private static void PlaceLabel(Control view, object canvasVm, SKPoint world)
    {
        if (view.GetVisualDescendants().OfType<StratCanvasView>().FirstOrDefault(c => ReferenceEquals(c.DataContext, canvasVm)) is not { } canvas
            || canvas.GetVisualDescendants().OfType<Scene2DHost>().FirstOrDefault() is not { } host)
        {
            return;
        }

        (double x, double y) = host.PrimaryCameraTransform.WorldToScreen(world.X, world.Y);
        canvas.PlaceDragLabel(new Point(x, y));
    }
}
