#region

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Cs2DemoKit.Analysis;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoCache;
using Cs2DemoKit.Parser;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Highlights;
using DemoViewer.NET.ViewModels.LiveSync;
using DemoViewer.NET.ViewModels.Playback;
using DemoViewer.NET.Views.Highlights;
using DemoViewer.NET.Views.LiveSync;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Headless render smoke for the four CSVG-integration views: each real view
///     is built over a populated VM, attached to a window, laid out, and
///     Skia-rendered — catching XAML load, compiled-binding, DataTemplate, and converter errors
///     the compile can't. Construct-and-render only; behavioral coverage lives in the pure-VM
///     batteries (LiveSyncStatusViewModelTests, HighlightsTabViewModelTests, reel batteries).
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ZCsvgViewsRenderTests
{
    private static int RenderInk(Window window, string artifactName)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        if (window.CaptureRenderedFrame() is not { } frame)
        {
            return 0;
        }

        frame.Save(Path.Combine(HeadlessSession.ArtifactDir, artifactName));
        return NonBackground(frame);
    }

    private static int NonBackground(WriteableBitmap bmp)
    {
        const byte BgR = 0x08, BgG = 0x08, BgB = 0x16; // ShellBg #080816
        PixelSize size = bmp.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];
        using (ILockedFramebuffer fb = bmp.Lock())
        {
            Marshal.Copy(fb.Address, buffer, 0, buffer.Length);
        }

        int n = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            if (Math.Abs(buffer[i] - BgB) > 8 || Math.Abs(buffer[i + 1] - BgG) > 8 || Math.Abs(buffer[i + 2] - BgR) > 8)
            {
                n++;
            }
        }

        return n;
    }

    // The one demo every tier-3 surface here renders. Deliberately unchanged in roster and highlight
    // content from the fixture that preceded the unified cache, so the captured frames stay comparable to
    // every earlier baseline of these views.
    private static DemoCacheRecord Record() => new()
    {
        Path = "/demos/faceit_dust2.dem",
        Map = "de_dust2",
        TickRate = 64,
        TickCount = 120_000,
        Sha256 = "sha",
        Analysis = new TierStamp { Schema = DemoCacheRecord.AnalysisSchema, ComputedAtTicks = 1 },
        AnalysisState = DemoAnalysisState.Indexed,
        Players =
        [
            new CachedPlayerInfo { Slot = 0, Name = "s1mple", SteamId64 = "1", Team = 2 },
            new CachedPlayerInfo { Slot = 1, Name = "ZywOo", SteamId64 = "2", Team = 3 }
        ],
        Rounds = [new Services.DemoCache.CachedRound { Number = 1, StartTickFrameClock = 1000 }],
        Highlights =
        [
            new CachedHighlightEvent
            {
                RulesetId = "rules", HighlightId = "clutch.ace", PlayerSlot = 0,
                RoundNumber = 1, Tick = 5000, RenderedTitle = "s1mple — 1v3 clutch"
            },
            new CachedHighlightEvent
            {
                RulesetId = "rules", HighlightId = "clutch.retake", PlayerSlot = 1,
                RoundNumber = 1, Tick = 7000, RenderedTitle = "ZywOo — retake"
            }
        ]
    };

    [Test]
    public async Task HighlightsTab_And_ReelDialog_Render()
    {
        int tabInk = 0, dialogInk = 0;
        await HeadlessSession.RunOnUi(() =>
        {
            DemoCacheStore demoCache = new(null);
            demoCache.Upsert(Record());
            using HighlightScanService scanner = new(demoCache,
                new FakeHarvester(),
                () => ["/demos/faceit_dust2.dem"],
                () => false,
                processorOverride: (_, _) => null);
            HighlightsTabViewModel tabVm = new(demoCache, scanner);
            // The card grid is gone (the Reels-dashboard redesign); the tab renders a STAGED tray, so stage something or
            // the capture is the empty state and the ink assertion measures nothing.
            DemoCacheRecord record = Record();
            foreach (CachedHighlightEvent highlight in record.Highlights)
            {
                tabVm.Stage(record, highlight);
            }

            Window tabWindow = new()
            {
                Width = 1100,
                Height = 720,
                Content = new HighlightsTabView
                {
                    DataContext = tabVm
                }
            };
            tabWindow.Show();
            tabInk = RenderInk(tabWindow, "csvg-highlights-tab.png");
            tabWindow.Close();

            HighlightSelection selection = new(Record(), Record().Highlights[0]);
            HighlightReelDialogViewModel dialogVm = new(
                [selection], new HighlightsSettings
                {
                    ReelOutputDirectory = "/out/reels"
                },
                new FakeReelJob(), null,
                () => false, true,
                _ => true);
            Window dialogWindow = new()
            {
                Width = 760,
                Height = 640,
                Content = new HighlightReelDialogView
                {
                    DataContext = dialogVm
                }
            };
            dialogWindow.Show();
            dialogInk = RenderInk(dialogWindow, "csvg-reel-dialog.png");
            dialogWindow.Close();
            return Task.CompletedTask;
        });

        await Assert.That(tabInk).IsGreaterThan(1000).Because("the Highlights tab must render visible content");
        await Assert.That(dialogInk).IsGreaterThan(1000).Because("the reel dialog must render visible content");
    }

    [Test]
    public async Task LiveSyncFlyout_And_ReelChipFlyout_Render()
    {
        int syncInk = 0, reelInk = 0;
        await HeadlessSession.RunOnUi(() =>
        {
            FakeLiveSync liveSync = new();
            PlaybackController playback = new();
            LiveSyncStatusViewModel syncVm = new(liveSync, null, playback,
                () => { }, _ => Task.CompletedTask);
            // Degraded WITH a remote demo path — the densest flyout section (Open in DemoViewer
            // offer + Re-sync + the newly-bound ReasonText surface all present).
            liveSync.Raise(new LiveSyncState(LiveSyncStateKind.Degraded,
                "CS2 is now playing a different demo (other.dem).",
                RemoteDemoPath: "/cs2/replays/other.dem"));

            Window syncWindow = new()
            {
                Width = 420,
                Height = 560,
                Content = new LiveSyncStatusView
                {
                    DataContext = syncVm
                }
            };
            syncWindow.Show();
            syncInk = RenderInk(syncWindow, "csvg-livesync-flyout.png");
            syncWindow.Close();
            syncVm.Dispose();

            FakeReelJob reelJob = new();
            ReelJobStatusViewModel reelVm = new(reelJob);
            // Capturing mid-run with a player-name-bearing clip label (the sanitize boundary)
            // and a failed clip → progress, per-clip rows, and the failed glyph all render.
            reelJob.Raise(new ReelJobStatus(ReelJobPhase.Capturing, 1, 3, "s1mple — 1v3 clutch",
                null, null, [0]));

            Window reelWindow = new()
            {
                Width = 420,
                Height = 480,
                Content = new ReelJobStatusView
                {
                    DataContext = reelVm
                }
            };
            reelWindow.Show();
            reelInk = RenderInk(reelWindow, "csvg-reel-chip-flyout.png");
            reelWindow.Close();
            reelVm.Dispose();
            return Task.CompletedTask;
        });

        await Assert.That(syncInk).IsGreaterThan(500).Because("the Live Sync flyout must render visible content");
        await Assert.That(reelInk).IsGreaterThan(500).Because("the reel chip flyout must render visible content");
    }

    // ── Shared minimal fakes ──────────────────────────────────────────────────

    private sealed class FakeHarvester : IHighlightHarvester
    {
        public (string Fingerprint, IReadOnlyDictionary<string, string> Hashes) ComputeFingerprint(int tickRate) =>
            ($"fp@{tickRate}", new Dictionary<string, string>());

        public AnalysisRun RunBareAnalysis(ParsedDemo demo) =>
            throw new NotSupportedException("render smoke never parses");

        public void InvalidateRules()
        {
        }
    }

    private sealed class FakeLiveSync : ILiveSyncService
    {
        public LiveSyncState State { get; private set; } = LiveSyncState.Disconnected;
        public long? LastCs2DemoTick { get; set; }
        public LiveSyncVersionInfo? Versions { get; set; }
        public LiveSyncCapabilities? Capabilities { get; set; }

        public event EventHandler<LiveSyncStateChangedEventArgs>? StateChanged;

        public Task EnableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResyncAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> VerifyMomentAsync(int frameClockTick, int preRollTicks = 192, int postRollTicks = 64,
            string? spectateName = null, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> HasLeftoverInstallModificationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task RestoreInstallAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Raise(LiveSyncState next)
        {
            LiveSyncState prev = State;
            State = next;
            StateChanged?.Invoke(this, new LiveSyncStateChangedEventArgs(prev, next));
        }
    }

    private sealed class FakeReelJob : IReelJobService
    {
        public ReelJobStatus Status { get; private set; } = ReelJobStatus.Idle;

        public event EventHandler<ReelJobStatus>? StatusChanged;

        public void Start(ReelRequest request)
        {
        }

        public Task CancelAsync() => Task.CompletedTask;

        public void RetryRemaining()
        {
        }

        public void Raise(ReelJobStatus next)
        {
            Status = next;
            StatusChanged?.Invoke(this, next);
        }
    }
}
