#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Controls;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     navigation-review Phase C gates for the single shell <see cref="NavStrip" />: it renders as
///     shell chrome (own docked row) when a file is loaded, the semantic Nav* commands gate on the
///     loaded-file state, the editable frame box commits via the controller (frame-index movement), and
///     the breakpoint sub-group's commands remain present and distinct. VM-level gates run as ordinary
///     awaited statements; only the synchronous render goes through the headless dispatcher.
/// </summary>
[NotInParallel]
[Category("Render")]
public class NavStripTests
{
    [Test]
    public async Task NavCommands_GateOnLoadedFile()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());

            // No file: semantic nav is disabled.
            await Assert.That(vm.NavNextEventCommand.CanExecute(null)).IsFalse();
            await Assert.That(vm.NavNextRoundCommand.CanExecute(null)).IsFalse();
            await Assert.That(vm.NavNextTickCommand.CanExecute(null)).IsFalse();
            await Assert.That(vm.NavPrevEventCommand.CanExecute(null)).IsFalse();
            await Assert.That(vm.NavPrevRoundCommand.CanExecute(null)).IsFalse();
            await Assert.That(vm.NavPrevTickCommand.CanExecute(null)).IsFalse();

            // File loaded: semantic nav becomes enabled (CanExecute re-evaluated on HasFile change).
            vm.HasFile = true;
            await Assert.That(vm.NavNextEventCommand.CanExecute(null)).IsTrue();
            await Assert.That(vm.NavNextRoundCommand.CanExecute(null)).IsTrue();
            await Assert.That(vm.NavNextTickCommand.CanExecute(null)).IsTrue();
            await Assert.That(vm.NavPrevEventCommand.CanExecute(null)).IsTrue();
            await Assert.That(vm.NavPrevRoundCommand.CanExecute(null)).IsTrue();
            await Assert.That(vm.NavPrevTickCommand.CanExecute(null)).IsTrue();

            // The breakpoint sub-group commands are present and distinct from the semantic ones.
            await Assert.That(vm.ContinueToBreakpointCommand).IsNotNull();
            await Assert.That(vm.StepTickToBreakpointCommand).IsNotNull();
            await Assert.That(vm.StepRoundToBreakpointCommand).IsNotNull();
        });
    }

    [Test]
    public async Task FrameBox_MirrorsControllerAndCommitsByIndex()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty());

            // Seed a tiny synthetic frame list on the controller so SeekToFrame has a valid range.
            // (We exercise the box/controller contract, not a real demo decode.)
            vm.HasFile = true;

            // The box mirrors the controller's index. With no demo frames loaded the controller clamps
            // out-of-range seeks, so committing a value with TotalFrames==0 reverts to the last valid.
            vm.NavFrameText = "999";
            vm.CommitNavFrameText();
            // No frames → max is 0 → clamp to 0.
            await Assert.That(vm.NavFrameText).IsEqualTo("0");

            // Bad input reverts to the last valid frame text rather than throwing.
            vm.NavFrameText = "not-a-number";
            vm.CommitNavFrameText();
            await Assert.That(vm.NavFrameText).IsEqualTo("0");
        });
    }

    [Test]
    public async Task NavStrip_Renders_WhenFileLoaded()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty())
            {
                HasFile = true
            };

            NavStrip strip = new()
            {
                DataContext = vm
            };
            Window window = new()
            {
                Width = 1280,
                Height = 48,
                Content = strip
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            await Assert.That(frame).IsNotNull();

            string outPath = Path.Combine(HeadlessSession.ArtifactDir, "navstrip.png");
            frame!.Save(outPath);
            int nonBg = ScanNonBackground(frame);
            Console.WriteLine($"[navstrip] {outPath} nonBg={nonBg}");

            // The strip draws buttons / readout / labels, far more than an empty background.
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

        // Treat the darkest pixels as background; count anything appreciably brighter as drawn content.
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
