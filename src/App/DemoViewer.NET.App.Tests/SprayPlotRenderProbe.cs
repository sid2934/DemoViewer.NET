#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CS2DemoKit.Parser;
using DemoViewer.NET.Controls.Stats;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Stats;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Renders <see cref="SprayPlot" /> against a real demo's sprays, in both origin modes, so the
///     shapes can be eyeballed rather than inferred from numbers.
///     <para>
///         The assertion is that the two modes DIFFER. A plot that silently drew first-bullet offsets
///         under the tracking label would look entirely reasonable, and comparing ink is the cheapest
///         way to prove the origin actually moved.
///     </para>
/// </summary>
[Category("RealDemo")]
[Category("Render")]
[NotInParallel]
public class SprayPlotRenderProbe
{
    [Test]
    public async Task Plot_DrawsBothOriginModes_AndTheyDiffer()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        SprayModel anchored = SpraySampler.Sample(demo, SprayOrigin.FirstBullet);
        SprayModel tracked = SpraySampler.Sample(demo, SprayOrigin.TargetCentre);

        PlayerSprays subject = anchored.Players
            .OrderByDescending(p => p.Runs.Sum(r => r.Shots.Count))
            .First();
        IReadOnlyList<SprayPatternPoint> pattern =
            anchored.IdealByWeapon.TryGetValue(subject.Weapon, out IReadOnlyList<SprayPatternPoint>? pts)
                ? pts
                : [];

        SprayRun anchoredRun = subject.Runs.OrderByDescending(r => r.Shots.Count).First();
        SprayRun? trackedRun = tracked.Players
            .FirstOrDefault(p => p.Slot == subject.Slot && p.Weapon == subject.Weapon)
            ?.Runs.OrderByDescending(r => r.Shots.Count).FirstOrDefault();

        Console.WriteLine($"[plot] {subject.Weapon} slot={subject.Slot} "
                          + $"pattern={pattern.Count} anchored={anchoredRun.Shots.Count} "
                          + $"tracked={trackedRun?.Shots.Count ?? 0}");

        int anchoredInk = 0;
        int trackedInk = 0;

        await HeadlessSession.RunOnUi(async () =>
        {
            anchoredInk = Capture(pattern, anchoredRun.Shots, "spray-plot-anchored");
            if (trackedRun is not null)
            {
                trackedInk = Capture(pattern, trackedRun.Shots, "spray-plot-tracked");
            }

            await Assert.That(anchoredInk).IsGreaterThan(0)
                .Because("a run with shots must draw something");
        });

        if (trackedRun is not null)
        {
            await Assert.That(trackedInk).IsNotEqualTo(anchoredInk)
                .Because("the tracking origin moves with the victim, so its trace cannot be identical");
        }
    }

    private static int Capture(
        IReadOnlyList<SprayPatternPoint> pattern, IReadOnlyList<SpraySample> shots, string name)
    {
        // Magenta for the player's trace: no palette token is near it, so any hit is this brush.
        SprayPlot plot = new()
        {
            Pattern = pattern,
            Shots = shots,
            PatternBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x8F, 0x99)),
            ShotBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF)),
            AxisBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD4, 0xD8)),
            Width = 360,
            Height = 360
        };

        Window window = new()
        {
            SystemDecorations = SystemDecorations.None,
            Width = 360,
            Height = 360,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xF9)),
                Child = plot
            }
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        WriteableBitmap frame = window.CaptureRenderedFrame()!;
        frame.Save(Path.Combine(HeadlessSession.ArtifactDir, $"{name}.png"));
        Console.WriteLine($"[plot] {HeadlessSession.ArtifactDir}/{name}.png");
        return FrameProbe.CountPixels(FrameProbe.ToBytes(frame), 0xFF00FF);
    }
}
