#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Views.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Shared scaffolding for the A1 headless tests: one activated tab, one shown window, and the two
///     control lookups (viewport, scrub bar) every one of them needs.
/// </summary>
internal static class Playback2DTimelineHarness
{
    /// <summary>Builds an activated tab over a recording context carrying rounds, kills and bomb events.</summary>
    public static (Playback2DTabViewModel Vm, Playback2DFakeContext Ctx) Tab(int totalFrames = 1000)
    {
        Playback2DFakeContext ctx = new()
        {
            TotalFrames = totalFrames,
            Gate = new FakeModuleFeatureGate()
        };
        ctx.AddPlayer(0, "Alpha", 2);
        ctx.AddPlayer(1, "Bravo", 2);
        ctx.AddPlayer(2, "Charlie", 3);

        ctx.Frames["round_freeze_end"] = [0, totalFrames / 3, totalFrames * 2 / 3];
        ctx.Timelines["player_death"] =
        [
            Death(40), Death(400), Death(900), Death(1400)
        ];
        ctx.Timelines["bomb_planted"] = [Bomb("bomb_planted", 700)];

        Playback2DTabViewModel vm = new();
        vm.OnActivated(ctx);
        return (vm, ctx);
    }

    /// <summary>Shows a window hosting the 2D view and pumps enough render frames for layout to settle.</summary>
    public static (Window Window, Playback2DView View) Show(Playback2DTabViewModel vm,
        int width = 1000, int height = 700)
    {
        Playback2DView view = new()
        {
            DataContext = vm
        };
        Window window = new()
        {
            Width = width,
            Height = height,
            Content = view
        };
        window.Show();
        Pump();
        return (window, view);
    }

    public static void Pump(int frames = 3)
    {
        for (int i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static TimelineControl Timeline(Playback2DView view) =>
        view.GetVisualDescendants().OfType<TimelineControl>().FirstOrDefault()
        ?? throw new InvalidOperationException("TimelineControl not found in the view's visual tree.");

    public static Playback2DViewport Viewport(Playback2DView view) =>
        view.FindControl<Playback2DViewport>("Viewport")
        ?? throw new InvalidOperationException("viewport not found");

    public static Panel ScrubBar(TimelineControl control) =>
        control.FindControl<Panel>("ScrubBar")
        ?? throw new InvalidOperationException("scrub bar not found");

    public static ItemsControl RoundsBand(TimelineControl control) =>
        control.FindControl<ItemsControl>("RoundsBand")
        ?? throw new InvalidOperationException("rounds band not found");

    /// <summary>Maps a point inside <paramref name="from" /> into window coordinates for headless input.</summary>
    public static Point ToWindow(Visual from, Window window, double x, double y) =>
        from.TranslatePoint(new Point(x, y), window)
        ?? throw new InvalidOperationException("control is not connected to the window's visual tree.");

    private static GameEventView Death(int tick) => new()
    {
        Name = "player_death",
        Tick = tick,
        Fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Attacker"] = 0,
            ["UserId"] = 2,
            ["Weapon"] = "ak47",
            ["Headshot"] = true
        }
    };

    private static GameEventView Bomb(string name, int tick) => new()
    {
        Name = name,
        Tick = tick,
        Fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Site"] = "A"
        }
    };
}
