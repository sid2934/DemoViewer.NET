#region

using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.Tutorial;
using DemoViewer.NET.Views;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The load-bearing gateway-wait path over the REAL shell + a real demo: when the first run reaches the
///     open-a-demo gateway with nothing open, the demo segment (stats / playback / transport / outro) WAITS in
///     place, the overlay stays visible with advance disabled, and opening a demo auto-resumes the tour at
///     the Stats step. This exercises the shell's <c>NotifyDemoLoaded</c> wiring at the end of the interactive
///     load, the whole-script step indicator continuing across the two runs, the tab-switch to Stats, and the
///     overlay measuring the Stats / playback / transport spotlights over live tab content (rendered to PNGs
///     for inspection). Requires a demo. Skips cleanly via <see cref="DemoTestHelper.RequireDemo()" />.
///     <para>
///         <b>Load path:</b> resumption is driven through <see cref="MainViewModel.LoadDemoFromPathAsync" />,
///         the real interactive-open funnel (Library browser / Open-file picker → <c>LoadDemoFromBytesAsync</c>,
///         which calls <c>NotifyDemoLoaded</c>). The <c>MemoryReleaseWiredTests</c> idiom's
///         <c>AutoLoadDemoAsync</c> is a SEPARATE (CLI auto-open) path that also wires the resume, but is
///         deliberately not used here.
///     </para>
/// </summary>
[NotInParallel]
[TUnit.Core.Category("RealDemo")]
public class TutorialDeferredSegmentTests
{
    [Test]
    public async Task FirstRunEnds_ThenOpeningDemo_ResumesAtStats_ThroughToFinish()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            // Register the first-party 2D Playback module the way App.axaml.cs does, so the
            // "playback2d.viewport" tab (and its PlaybackTab / PlaybackTransport anchors) actually exist.
            // Without it the tour's tab-switch for steps 5–6 no-ops and the anchors never realize.
            ModuleRegistry registry = new();
            registry.Register(new Playback2DModule());
            MainViewModel vm = new(null, registry, TestLibraries.Empty());
            try
            {
                MainView view = new()
                {
                    DataContext = vm
                };
                Window window = new()
                {
                    Width = 1280,
                    Height = 800,
                    Content = view
                };
                window.Show();

                // First-run run: welcome → tabs → library → open-demo gateway. With no demo open the gateway
                // WAITS in place (overlay stays visible, advance disabled) rather than hiding: the fix for the
                // "left on the Library tab with no guidance" dead-end.
                vm.StartWalkthrough(); // welcome (1/8)
                vm.Tutorial.NextCommand.Execute(null); // tab strip (2/8)
                vm.Tutorial.NextCommand.Execute(null); // library (3/8)
                vm.Tutorial.NextCommand.Execute(null); // open-demo gateway (4/8) → waiting

                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tutorial.IsActive).IsTrue()
                        .Because("the gateway waits in place — it does not hide and strand the user");
                    await Assert.That(vm.Tutorial.IsWaiting).IsTrue();
                    await Assert.That(vm.Tutorial.CanGoNext).IsFalse()
                        .Because("advance is disabled until a demo is opened");
                    await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.OpenDemo);
                }

                // The real interactive open path: the funnel that carries NotifyDemoLoaded. Recorded as a
                // TRAIL of tab selections, because the Match Overview landing page must not hijack navigation
                // while the tour owns it: a normal open switches to "builtin.matchoverview" the instant the
                // parse starts, but during the gateway that would unload the Library (the spotlit card
                // vanishes) and strand the coach-mark's spotlight over stale coordinates for the whole
                // multi-second parse. Both the gated and ungated paths END on Stats, so only the mid-load
                // trail discriminates: an end-state assert would pass either way.
                List<string> tabTrail = new();
                PropertyChangedEventHandler onTabChanged = (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.SelectedTab) && vm.SelectedTab is { } t)
                    {
                        tabTrail.Add(t.TabId);
                    }
                };

                vm.PropertyChanged += onTabChanged;
                try
                {
                    await vm.LoadDemoFromPathAsync(demo);
                }
                finally
                {
                    vm.PropertyChanged -= onTabChanged;
                }

                await Assert.That(tabTrail).DoesNotContain("builtin.matchoverview")
                    .Because("the tour owns navigation at the gateway — the landing page must not steal it mid-parse");
                await Assert.That(tabTrail).Contains("builtin.stats")
                    .Because("the trail is live — it captured the tour's own resume switch");

                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tutorial.IsActive).IsTrue()
                        .Because("opening a demo auto-resumes the waiting walkthrough");
                    await Assert.That(vm.Tutorial.IsWaiting).IsFalse();
                    await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.StatsContent);
                    await Assert.That(vm.Tutorial.StepNumber).IsEqualTo(5)
                        .Because("the indicator continues across the two runs (stats is 5 of 8)");
                    await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.stats")
                        .Because("the resumed step switches to the tab hosting its region");
                }

                // Pump layout so the Stats tab realizes, its anchor registers, and the overlay measures.
                PumpLayout();

                await Assert.That(vm.Tutorial.SpotlightRect.Width).IsGreaterThan(0)
                    .Because("the overlay resolved the Stats anchor and measured its rect");
                await Assert.That(vm.Tutorial.SpotlightRect.Height).IsGreaterThan(0);

                WriteableBitmap? frame = window.CaptureRenderedFrame();
                await Assert.That(frame).IsNotNull();
                string outPath = Path.Combine(HeadlessSession.ArtifactDir, "tutorial-live-stats.png");
                frame!.Save(outPath);
                Console.WriteLine(
                    $"[tutorial-live-stats] {outPath} rect={vm.Tutorial.SpotlightRect} nonBg={ScanNonBackground(frame)}");

                // Advance the rest of the demo segment: playback (6) → transport (7) → outro (8) → Finish.
                // Steps 6–7 ARE the "2d playback and controls" the tour was built to show, so each is measured
                // for a real non-zero spotlight over the live shell, not merely advanced past (a zero-size
                // spotlight would still let the tour sail through, so the enum/step asserts alone prove nothing).
                vm.Tutorial.NextCommand.Execute(null); // 2D playback (6/8)
                await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.PlaybackTab);
                await Assert.That(vm.Tutorial.StepNumber).IsEqualTo(6);
                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("playback2d.viewport")
                    .Because("the playback step switches to the 2D Playback tab");

                PumpLayout();
                await Assert.That(vm.Tutorial.SpotlightRect.Width).IsGreaterThan(0)
                    .Because("the overlay resolved the 2D Playback viewport anchor and measured its rect");
                await Assert.That(vm.Tutorial.SpotlightRect.Height).IsGreaterThan(0);

                vm.Tutorial.NextCommand.Execute(null); // transport (7/8)
                await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.PlaybackTransport);
                await Assert.That(vm.Tutorial.StepNumber).IsEqualTo(7);

                // The transport anchor is structurally different from every prior one: it sits on the NavStrip
                // clock-group cluster (app chrome), not a tab-root control, so its resolution is proven, not
                // assumed. Rendered to a PNG for eyeball inspection of the spotlight landing on the controls.
                PumpLayout();
                await Assert.That(vm.Tutorial.SpotlightRect.Width).IsGreaterThan(0)
                    .Because("the overlay resolved the NavStrip transport-cluster anchor and measured its rect");
                await Assert.That(vm.Tutorial.SpotlightRect.Height).IsGreaterThan(0);

                WriteableBitmap? transportFrame = window.CaptureRenderedFrame();
                await Assert.That(transportFrame).IsNotNull();
                string transportPath = Path.Combine(HeadlessSession.ArtifactDir, "tutorial-live-transport.png");
                transportFrame!.Save(transportPath);
                Console.WriteLine(
                    $"[tutorial-live-transport] {transportPath} rect={vm.Tutorial.SpotlightRect} nonBg={ScanNonBackground(transportFrame)}");

                vm.Tutorial.NextCommand.Execute(null); // outro (8/8)
                await Assert.That(vm.Tutorial.StepNumber).IsEqualTo(8);
                await Assert.That(vm.Tutorial.NextLabel).IsEqualTo("Finish");

                vm.Tutorial.NextCommand.Execute(null); // Finish

                await Assert.That(vm.Tutorial.IsActive).IsFalse().Because("Finish tears the tour down");
                await Assert.That(vm.Tutorial.CurrentStep).IsNull();
            }
            finally
            {
                vm.Dispose();
            }
        });

        // Drains queued UI jobs + forces render ticks so a just-switched tab realizes, its anchor registers,
        // and the overlay's LayoutUpdated pass measures the new target before the rect is asserted.
        static void PumpLayout()
        {
            for (int i = 0; i < 4; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            Dispatcher.UIThread.RunJobs();
        }
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
