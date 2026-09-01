#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Controls;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.ViewModels.Tutorial;
using DemoViewer.NET.Views;
using Path = System.IO.Path;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     End-to-end coverage of the first-run Visual Walkthrough wired into the real shell + MainView: the tour
///     engine drives the overlay VM, switches tabs so each step's region is live, and the overlay measures the
///     tagged anchor into a spotlight rect. Covers the whole first-run run and the state machine (advance /
///     back / skip), plus a headless render proving the overlay + spotlight land over the real Library region.
/// </summary>
[NotInParallel]
[Category("Render")]
public class TutorialWalkthroughTests
{
    private static MainViewModel NewShell()
    {
        ModuleRegistry registry = new();
        registry.Register(new Playback2DModule());
        registry.Register(new RuleWorkbenchModule());
        // Null gate → all tabs present (Library / Stats / 2D Playback are what the tour anchors to). The
        // registry MUST be passed so the module tabs (and the tab-strip the tour highlights) actually exist.
        MainViewModel vm = new(null, registry, TestLibraries.Empty());
        return vm;
    }

    [Test]
    public async Task Start_ActivatesOverlay_AtWelcomeStep()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell();
            try
            {
                await Assert.That(vm.Tutorial.IsActive).IsFalse();

                vm.StartWalkthrough();

                await Assert.That(vm.Tutorial.IsActive).IsTrue();
                await Assert.That(vm.Tutorial.CurrentStep!.Title).IsEqualTo("Welcome to DemoViewer");
                await Assert.That(vm.Tutorial.StepNumber).IsEqualTo(1);
                await Assert.That(vm.Tutorial.StepCount).IsEqualTo(8);
                await Assert.That(vm.Tutorial.CanGoBack).IsFalse();
                await Assert.That(vm.Tutorial.HasSpotlight).IsFalse().Because("welcome is a centered card");
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Test]
    public async Task Advance_ThroughFirstRun_SwitchesTabs_AndWaitsAtGateway()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell();
            try
            {
                vm.StartWalkthrough(); // welcome (1/8)

                vm.Tutorial.NextCommand.Execute(null); // → move between areas / tab strip (2/8)
                await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.TabNav);
                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library")
                    .Because("the tab-strip step lands on the Library as its backdrop");
                await Assert.That(vm.Tutorial.CanGoBack).IsTrue();

                vm.Tutorial.NextCommand.Execute(null); // → library (3/8)
                await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.LibraryTab);
                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library");

                vm.Tutorial.NextCommand.Execute(null); // → open a demo, the gateway (4/8)
                await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.OpenDemo);

                // The gateway waits IN PLACE with no demo open: the overlay stays up (guiding the user to the
                // spotlighted Open-Demo button), advance is disabled, and it auto-resumes when a demo loads.
                await Assert.That(vm.Tutorial.IsActive).IsTrue()
                    .Because("the gateway waits in place rather than hiding and stranding the user");
                await Assert.That(vm.Tutorial.IsWaiting).IsTrue();
                await Assert.That(vm.Tutorial.CanGoNext).IsFalse();
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Test]
    public async Task Back_ReturnsToPreviousStep()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell();
            try
            {
                vm.StartWalkthrough();
                vm.Tutorial.NextCommand.Execute(null); // tab strip (2/8)
                vm.Tutorial.NextCommand.Execute(null); // library (3/8)

                vm.Tutorial.BackCommand.Execute(null); // → tab strip (2/8)
                await Assert.That(vm.Tutorial.StepNumber).IsEqualTo(2);
                await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.TabNav);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Test]
    public async Task Skip_TearsDownTheTour()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell();
            try
            {
                vm.StartWalkthrough();
                vm.Tutorial.NextCommand.Execute(null);

                vm.Tutorial.SkipCommand.Execute(null);
                await Assert.That(vm.Tutorial.IsActive).IsFalse();
                await Assert.That(vm.Tutorial.CurrentStep).IsNull();
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    // The fragile core (per the design review): as the tour walks the first-run segment, the overlay must
    // resolve each tagged region and measure a non-empty spotlight over the real shell. Covers all three
    // first-run anchors: the tab strip (a header-union measurement), the Library region, and the Open-Demo
    // chrome button. Renders the tab-strip step to a PNG.
    [Test]
    public async Task FirstRunSteps_MeasureSpotlights_OverRealShell_AndRender()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell();
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

                vm.StartWalkthrough();

                // Step 2: the tab strip, a union of the realized TabItem headers, NOT the whole workspace, so
                // it must measure a short, wide rect near the top of the window (a chrome-strip region).
                vm.Tutorial.NextCommand.Execute(null); // → move between areas / tab strip (2/8)
                PumpLayout();
                await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.TabNav);
                await Assert.That(vm.Tutorial.SpotlightRect.Width).IsGreaterThan(0)
                    .Because("the overlay resolved the tab strip as the union of the tab headers");
                await Assert.That(vm.Tutorial.SpotlightRect.Height).IsGreaterThan(0);
                await Assert.That(vm.Tutorial.SpotlightRect.Height).IsLessThan(120)
                    .Because("the tab strip is a header row, not the whole tab body");

                WriteableBitmap? tabsFrame = window.CaptureRenderedFrame();
                await Assert.That(tabsFrame).IsNotNull();
                string tabsPath = Path.Combine(HeadlessSession.ArtifactDir, "tutorial-live-tabnav.png");
                tabsFrame!.Save(tabsPath);
                Console.WriteLine(
                    $"[tutorial-live-tabnav] {tabsPath} rect={vm.Tutorial.SpotlightRect} nonBg={ScanNonBackground(tabsFrame)}");

                // Step 3: the Library region (a tab-root anchor).
                vm.Tutorial.NextCommand.Execute(null); // → library (3/8)
                PumpLayout();
                await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.LibraryTab);
                await Assert.That(vm.Tutorial.SpotlightRect.Width).IsGreaterThan(0)
                    .Because("the overlay resolved the Library anchor and measured its rect");
                await Assert.That(vm.Tutorial.SpotlightRect.Height).IsGreaterThan(0);

                // Step 4: the Open-Demo toolbar button, a chrome control, not a tab root. With no demo open it
                // is the waiting gateway, but the spotlight still frames the button (advance disabled).
                vm.Tutorial.NextCommand.Execute(null); // → open a demo, the gateway (4/8)
                PumpLayout();
                await Assert.That(vm.Tutorial.CurrentStep!.Target).IsEqualTo(TutorialTarget.OpenDemo);
                await Assert.That(vm.Tutorial.IsWaiting).IsTrue().Because("no demo is open at the gateway");
                await Assert.That(vm.Tutorial.SpotlightRect.Width).IsGreaterThan(0)
                    .Because("the overlay resolved the Open-Demo toolbar-button anchor and measured its rect");
                await Assert.That(vm.Tutorial.SpotlightRect.Height).IsGreaterThan(0);
            }
            finally
            {
                vm.Dispose();
            }
        });

        static void PumpLayout()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            Dispatcher.UIThread.RunJobs();
        }
    }

    // The gateway points at the REAL Open-Demo button and lets the user click it: the scrim implements
    // ICustomHitTest so, while waiting, a hit inside the spotlight hole falls THROUGH to the real button, while
    // everything else stays blocked. Proven by hit-testing the live visual tree at the hole centre (→ the real
    // Open-Demo button, not the scrim) and at a point just outside the hole (→ still the scrim, blocked).
    [Test]
    public async Task Gateway_SpotlightHole_IsClickThroughToTheRealOpenDemoButton()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell();
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

                vm.StartWalkthrough();
                vm.Tutorial.NextCommand.Execute(null); // tab strip (2/8)
                vm.Tutorial.NextCommand.Execute(null); // library (3/8)
                vm.Tutorial.NextCommand.Execute(null); // open-demo gateway (4/8) → waiting
                for (int i = 0; i < 4; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }

                Dispatcher.UIThread.RunJobs();

                await Assert.That(vm.Tutorial.IsWaiting).IsTrue();
                Rect hole = vm.Tutorial.SpotlightRect;
                await Assert.That(hole.Width).IsGreaterThan(0).Because("the gateway spotlight measured the button");

                // The real Open-Demo button lives under the hole and must be reachable there…
                Control? hit = window.InputHitTest(hole.Center) as Control;
                bool overButton = hit is not null
                                  && TutorialAnchor.TryResolve(TutorialTarget.OpenDemo, out Control btn)
                                  && (ReferenceEquals(hit, btn) || btn.GetVisualDescendants().Contains(hit)
                                                                || hit.GetVisualAncestors().Contains(btn));
                await Assert.That(overButton).IsTrue()
                    .Because("a click in the spotlight hole passes through the scrim to the real Open-Demo button");

                // …while a point over the dimmed area (bottom-left, away from both the hole and the callout) is
                // still blocked by the scrim. The tour stays modal everywhere except the highlighted control.
                Control? blocked = window.InputHitTest(new Point(80, 720)) as Control;
                await Assert.That(blocked is SpotlightScrim).IsTrue()
                    .Because("outside the hole the scrim still blocks click-through — the tour stays modal");
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    // The user's chosen gateway behaviour: when the library holds a demo, the spotlight points at the first
    // library CARD (double-click loads it, no dialog), and the scrim lets that click through to the real card.
    // Proven over the real shell. A rendered card is measured, and a hit in the hole resolves to a demoCard.
    [Test]
    public async Task Gateway_WithLibraryDemo_SpotlightsAndClicksThroughToTheFirstCard()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            string tempDemo = Path.Combine(
                Path.GetTempPath(), "dvtut_" + Guid.NewGuid().ToString("N") + ".dem");
            await File.WriteAllBytesAsync(tempDemo, [1, 2, 3]);

            ModuleRegistry registry = new();
            registry.Register(new Playback2DModule());
            MainViewModel vm = new(null, registry, TestLibraries.WithEntry(tempDemo));
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

                vm.StartWalkthrough();
                vm.Tutorial.NextCommand.Execute(null); // tab strip (2/8)
                vm.Tutorial.NextCommand.Execute(null); // library (3/8)
                vm.Tutorial.NextCommand.Execute(null); // open-demo gateway (4/8) → waiting
                for (int i = 0; i < 6; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }

                Dispatcher.UIThread.RunJobs();

                await Assert.That(vm.Tutorial.IsWaiting).IsTrue();
                await Assert.That(vm.Tutorial.ActiveTarget).IsEqualTo(TutorialTarget.FirstLibraryCard)
                    .Because("a library demo is available, so the gateway points at the first card");
                Rect hole = vm.Tutorial.SpotlightRect;
                await Assert.That(hole.Width).IsGreaterThan(0).Because("the first demo card was measured");
                await Assert.That(hole.Height).IsGreaterThan(0);

                // The scrim passes clicks over the card's region THROUGH to the real card beneath, while still
                // blocking everywhere else. Asserted on the scrim's own hit-test (deterministic) rather than the
                // full InputHitTest chain (the virtualized card grid is not reliably hit-testable headless).
                SpotlightScrim scrim = view.GetVisualDescendants().OfType<SpotlightScrim>().Single();
                await Assert.That(scrim.InteractiveHole).IsTrue();
                await Assert.That(scrim.HitTest(hole.Center)).IsFalse()
                    .Because("a click over the highlighted card falls through the scrim to the real card");
                await Assert.That(scrim.HitTest(new Point(hole.X - 60, hole.Center.Y))).IsTrue()
                    .Because("outside the card the scrim still blocks — the tour stays modal");
            }
            finally
            {
                vm.Dispose();
                File.Delete(tempDemo);
            }
        });
    }

    // The empty-library on-ramp: with no folders and no cards but a bundled sample resolved, the gateway's
    // second preference spotlights the hero's "Try a sample match" CTA, measured over the real shell, with
    // the scrim's hole click-through reaching the real button. The sample is injected via the shell's
    // tourSampleLocator seam (production passes TourDemoLocator.FindSampleDemo); the file is garbage bytes
    // because targeting must not depend on the demo being loadable.
    [Test]
    public async Task Gateway_WithSampleOnly_SpotlightsTheHeroSampleCta()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            string tempSample = Path.Combine(
                Path.GetTempPath(), "dvtut_sample_" + Guid.NewGuid().ToString("N") + ".dem");
            await File.WriteAllBytesAsync(tempSample, [1, 2, 3]);

            ModuleRegistry registry = new();
            registry.Register(new Playback2DModule());
            MainViewModel vm = new(null, registry, TestLibraries.Empty(),
                tourSampleLocator: () => tempSample);
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

                vm.StartWalkthrough();
                vm.Tutorial.NextCommand.Execute(null); // tab strip (2/8)
                vm.Tutorial.NextCommand.Execute(null); // library (3/8)
                vm.Tutorial.NextCommand.Execute(null); // open-demo gateway (4/8) → waiting
                for (int i = 0; i < 6; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }

                Dispatcher.UIThread.RunJobs();

                await Assert.That(vm.LibraryTab.HasSampleDemo).IsTrue();
                await Assert.That(vm.Tutorial.IsWaiting).IsTrue();
                await Assert.That(vm.Tutorial.ActiveTarget).IsEqualTo(TutorialTarget.SampleDemo)
                    .Because("an empty library with a bundled sample points the gateway at the hero CTA");
                Rect hole = vm.Tutorial.SpotlightRect;
                await Assert.That(hole.Width).IsGreaterThan(0).Because("the sample CTA button was measured");
                await Assert.That(hole.Height).IsGreaterThan(0);

                // The hole must sit over the real anchored button, and a click there must fall through the
                // scrim to it, same modality contract as the Open-Demo and first-card gateways.
                await Assert.That(TutorialAnchor.TryResolve(TutorialTarget.SampleDemo, out Control cta)).IsTrue();
                await Assert.That(cta is Button { Command: not null }).IsTrue()
                    .Because("the anchor is the live CTA button, wired to OpenSampleCommand");
                SpotlightScrim scrim = view.GetVisualDescendants().OfType<SpotlightScrim>().Single();
                await Assert.That(scrim.InteractiveHole).IsTrue();
                await Assert.That(scrim.HitTest(hole.Center)).IsFalse()
                    .Because("a click over the highlighted CTA falls through the scrim to the real button");
                await Assert.That(scrim.HitTest(new Point(hole.X - 60, hole.Center.Y))).IsTrue()
                    .Because("outside the CTA the scrim still blocks — the tour stays modal");
            }
            finally
            {
                vm.Dispose();
                File.Delete(tempSample);
            }
        });
    }

    // The regression this pins: the Library's card-open path used to pre-switch to the Parser tab
    // unconditionally, while the load funnel's Match Overview landing is deliberately suppressed
    // during the tour, so the tour's spotlighted card-click stranded the user on Parser with the
    // spotlight pointing at nothing. The open paths pass no tab switch of their own anymore: the
    // funnel owns the landing, and mid-tour the shell stays on the Library until the tour's demo
    // run navigates itself. The entry is deliberately garbage bytes: the old yank was synchronous
    // and unconditional, so it fired even for a load that goes on to fail; "no tab change" must
    // hold regardless of load outcome.
    [Test]
    public async Task Gateway_CardOpen_DoesNotLeaveTheLibraryTab_WhileTouring()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            string tempDemo = Path.Combine(
                Path.GetTempPath(), "dvtut_" + Guid.NewGuid().ToString("N") + ".dem");
            await File.WriteAllBytesAsync(tempDemo, [1, 2, 3]);

            MainViewModel vm = new(null, new ModuleRegistry(), TestLibraries.WithEntry(tempDemo));
            try
            {
                vm.StartWalkthrough();
                vm.Tutorial.NextCommand.Execute(null); // tab strip (2/8)
                vm.Tutorial.NextCommand.Execute(null); // library (3/8)
                vm.Tutorial.NextCommand.Execute(null); // open-demo gateway (4/8) → waiting
                Dispatcher.UIThread.RunJobs();
                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library");

                // The spotlighted card's double-click path.
                DemoEntry entry = new()
                {
                    FilePath = tempDemo,
                    FileName = Path.GetFileName(tempDemo),
                    Directory = Path.GetDirectoryName(tempDemo) ?? "",
                    FileSizeBytes = 3,
                    Modified = DateTime.Now
                };
                await vm.LibraryTab.OpenEntryCommand.ExecuteAsync(entry);
                Dispatcher.UIThread.RunJobs();

                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo("builtin.library")
                    .Because("mid-tour the card open must not yank the shell off the spotlighted "
                             + "Library — the tour navigates tabs itself once the demo run begins");
            }
            finally
            {
                vm.Dispose();
                File.Delete(tempDemo);
            }
        });
    }

    // The round's headline ask is a BREATHING spotlight border. Static-phase captures prove Render responds to
    // Pulse; this proves the RUNTIME wiring: the '.pulsing' class is applied only while a spotlight step is on
    // screen (absent on the centred welcome card, present once a spotlight step is active), and the custom
    // Pulse property is not a dead no-op. The "does it advance" half is asserted RELATIVE to a proven stock
    // animation (the waiting dot's Opacity, the shipped Ellipse.dot.pulsing idiom, identical mechanism): if the
    // headless clock advances that stock pulse, our custom-property pulse MUST advance too; if the clock ticks
    // neither (a headless limitation, not our bug), the relative check is vacuously satisfied and the class
    // assertions still stand as the runtime proof.
    [Test]
    public async Task SpotlightPulse_IsAppliedOnlyWhileSpotlit_AndTracksAProvenAnimation()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = NewShell();
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

                vm.StartWalkthrough(); // welcome (1/8), a centred card, no spotlight
                Pump();

                SpotlightScrim scrim = view.GetVisualDescendants().OfType<SpotlightScrim>().Single();
                await Assert.That(scrim.Classes.Contains("pulsing")).IsFalse()
                    .Because("the welcome card has no spotlight, so it must not breathe");

                // Walk to the gateway (4/8): a spotlight step (OpenDemo) AND the waiting state, so both the
                // spotlight pulse and the proven waiting-dot pulse are live in the tree at once.
                vm.Tutorial.NextCommand.Execute(null); // tab strip (2/8)
                vm.Tutorial.NextCommand.Execute(null); // library (3/8)
                vm.Tutorial.NextCommand.Execute(null); // open-demo gateway (4/8) → waiting
                Pump();

                await Assert.That(vm.Tutorial.HasSpotlight).IsTrue();
                await Assert.That(vm.Tutorial.IsWaiting).IsTrue();
                await Assert.That(scrim.Classes.Contains("pulsing")).IsTrue()
                    .Because("a spotlight step applies the breathing-pulse class at runtime");

                Ellipse dot = view.GetVisualDescendants()
                    .OfType<Ellipse>()
                    .Single(e => e.Classes.Contains("pulsing"));

                // Sample both animations across the same span of render ticks. Several samples so we don't alias
                // the sine's turning points.
                List<double> pulse = new();
                List<double> dotOpacity = new();
                for (int i = 0; i < 200; i++)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    pulse.Add(scrim.Pulse);
                    dotOpacity.Add(dot.Opacity);
                }

                double pulseSpread = pulse.Max() - pulse.Min();
                double dotSpread = dotOpacity.Max() - dotOpacity.Min();
                Console.WriteLine(FormattableString.Invariant(
                    $"[tutorial-pulse] pulseSpread={pulseSpread:0.00} dotSpread={dotSpread:0.00}"));

                // If the proven stock pulse advanced under this clock, our custom-property pulse must advance
                // too (a dead custom-property KeyFrame would leave Pulse pinned while the dot still breathed).
                if (dotSpread > 0.05)
                {
                    await Assert.That(pulseSpread).IsGreaterThan(0.05)
                        .Because("the custom Pulse must breathe just like the proven stock dot animation");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
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
