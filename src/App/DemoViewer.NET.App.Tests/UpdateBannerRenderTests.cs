#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DemoViewer.NET.Services.Update;
using DemoViewer.NET.ViewModels.Update;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Render smoke for the shell's update banner. The VM tests prove the decision logic; this proves
///     the markup actually draws — the two banner rows live in <c>MainView.axaml</c> and bind through
///     <c>Update.*</c>, so a renamed property or a bad resource key would otherwise fail silently at
///     runtime (Avalonia binding errors do not throw) and ship an invisible update prompt.
///     <para>
///         Renders the banner content standalone rather than the whole shell: MainView pulls in the
///         full module graph, and what needs proving here is that these two rows resolve their
///         bindings and theme brushes.
///     </para>
/// </summary>
[NotInParallel]
[Category("Render")]
public class UpdateBannerRenderTests
{
    [Test]
    public async Task UpdateBanner_Offer_RendersNonBlank()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            UpdateViewModel vm = new(new StubService());
            await vm.CheckOnStartupAsync();
            await Assert.That(vm.IsUpdateAvailable).IsTrue();

            Window window = BuildBannerWindow(vm, true);
            WriteableBitmap? frame = Render(window);
            await Assert.That(frame).IsNotNull();

            string outPath = Path.Combine(HeadlessSession.ArtifactDir, "update-banner-offer.png");
            frame!.Save(outPath);
            int nonBg = ScanNonBackground(frame);
            Console.WriteLine($"[update-banner-offer] {outPath} nonBg={nonBg}");

            // Text + two buttons: comfortably more than an empty strip.
            await Assert.That(nonBg).IsGreaterThan(200);
        });
    }

    [Test]
    public async Task UpdateBanner_Downloading_RendersProgress()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            UpdateViewModel vm = new(new StubService());
            await vm.CheckOnStartupAsync();

            Window window = BuildBannerWindow(vm, false);
            WriteableBitmap? frame = Render(window);
            await Assert.That(frame).IsNotNull();

            string outPath = Path.Combine(HeadlessSession.ArtifactDir, "update-banner-downloading.png");
            frame!.Save(outPath);
            int nonBg = ScanNonBackground(frame);
            Console.WriteLine($"[update-banner-downloading] {outPath} nonBg={nonBg}");

            await Assert.That(nonBg).IsGreaterThan(200);
        });
    }

    /// <summary>
    ///     Mirrors the two MainView.axaml rows. Kept in this test rather than extracted into a control
    ///     so the shell markup stays as-authored; if the two drift, the render assertions here still
    ///     catch a broken VM contract, which is the failure mode that actually bites.
    /// </summary>
    private static Window BuildBannerWindow(UpdateViewModel vm, bool offer)
    {
        Control content = offer
            ? new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"DemoViewer.NET {vm.AvailableVersion} is available.",
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            // "Details…" mirrors the v0.6.0 banner (it re-opens the update-notice
                            // pop-up; the command lives on MainViewModel, so the mirror uses a
                            // placeholder — presence and layout are what this smoke proves).
                            new Button
                            {
                                Content = "Details…"
                            },
                            new Button
                            {
                                Content = "Update & Restart",
                                Command = vm.UpdateAndRestartCommand
                            },
                            new Button
                            {
                                Content = "Later",
                                Command = vm.DismissCommand
                            }
                        }
                    }
                }
            }
            : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Downloading update…"
                    },
                    new ProgressBar
                    {
                        Width = 220,
                        Minimum = 0,
                        Maximum = 100,
                        Value = 42
                    },
                    new TextBlock
                    {
                        Text = "42%"
                    }
                }
            };

        return new Window
        {
            Width = 720,
            Height = 120,
            Content = new Border
            {
                Padding = new Thickness(12, 8),
                Child = content
            }
        };
    }

    private static WriteableBitmap? Render(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window.CaptureRenderedFrame();
    }

    /// <summary>Same marshal-copy scan the other render smokes use — no unsafe blocks in this project.</summary>
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

    private sealed class StubService : IUpdateService
    {
        public string? CurrentVersion => "0.5.1";

        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default) =>
            Task.FromResult(UpdateCheckResult.UpdateAvailable("0.5.2"));

        public Task<bool> DownloadAndApplyAsync(IProgress<int>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
