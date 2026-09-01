#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

#endregion

namespace DemoViewer.NET.AppTests;

[Category("Render")]
public class HarnessSmokeTest
{
    [Test]
    public async Task Headless_RendersAndCapturesAFrame()
    {
        await HeadlessSession.RunOnUi(() =>
        {
            Window window = new()
            {
                Width = 400,
                Height = 120,
                Content = new TextBlock
                {
                    Text = "headless render OK",
                    Foreground = Brushes.White,
                    Background = Brushes.Black
                }
            };
            window.Show();

            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            string path = Path.Combine(HeadlessSession.ArtifactDir, "smoke.png");
            frame!.Save(path);
            Console.WriteLine($"[capture] {path}");

            return Task.CompletedTask;
        });

        await Assert.That(File.Exists(Path.Combine(HeadlessSession.ArtifactDir, "smoke.png"))).IsTrue();
    }
}
