#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.ViewModels.Idle;
using DemoViewer.NET.Views.Idle;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Render smoke for the idle-mode surface: the real <see cref="IdleView" /> bound to a real
///     <see cref="IdleViewModel" /> draws far more than an empty background (header, message, buttons,
///     session readout). Confirms the overlay's XAML + bindings resolve headlessly. The synchronous render
///     goes through the headless dispatcher.
/// </summary>
[NotInParallel]
public class IdleViewRenderTests
{
    [Test]
    public async Task IdleView_Renders_NonBlank()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            IdleViewModel vm = new(() => { }, () => { }, null)
            {
                MessageText = IdleViewModel.BuildMessage(TimeSpan.FromMinutes(15)),
                SessionStateText = "Closed match.dem — resumes at frame 12345."
            };

            IdleView view = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 640,
                Height = 640,
                Content = view
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            await Assert.That(frame).IsNotNull();

            string outPath = Path.Combine(HeadlessSession.ArtifactDir, "idle.png");
            frame!.Save(outPath);
            int nonBg = ScanNonBackground(frame);
            Console.WriteLine($"[idle] {outPath} nonBg={nonBg}");

            await Assert.That(nonBg).IsGreaterThan(200);
            await Assert.That(File.Exists(outPath)).IsTrue();
        });
    }

    private static int ScanNonBackground(WriteableBitmap bmp)
    {
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int nonBg = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
            if (r > 60 || g > 60 || b > 60)
            {
                nonBg++;
            }
        }

        return nonBg;
    }
}
